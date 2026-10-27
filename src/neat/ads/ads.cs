using System.Text.Json;

namespace neat;

/// <summary>
/// The ad blocker, shared by every window (App.Ads). It owns the engine built from the filter
/// release that ships next to neat.exe, and answers the window's questions about requests, popups
/// and pages. Everything here is safe to call before the engine is ready (and when the dll or the
/// filters are missing): the answer is then "allow, nothing to hide".
/// </summary>
internal sealed class Adblock
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile AdsEngine? _engine;
    private int _failed;
    private int _blocked;

    /// <summary>Version of the loaded filter release, or empty.</summary>
    public string Version { get; private set; } = string.Empty;

    /// <summary>True once an engine is loaded.</summary>
    public bool Ready => _engine is not null;

    /// <summary>Requests stopped since the app started, all tabs together.</summary>
    public int Blocked => Volatile.Read(ref _blocked);

    /// <summary>The text of ads/page.js, read once.</summary>
    public static string Template { get; } = ReadTemplate();

    private static string ReadTemplate()
    {
        using var s = typeof(Adblock).Assembly.GetManifestResourceStream("neat.ads.page.js")
            ?? throw new InvalidOperationException("ads/page.js is not embedded");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>Settings > the blocker as a whole is on.</summary>
    public bool On => App.Cfg.Cur.Ads.On;

    /// <summary>
    /// The blocker is switched off for this host (the person's own list). A listed site covers its
    /// subdomains: "example.com" in the list also switches it off on "www.example.com".
    /// </summary>
    public bool IsOff(string? host)
    {
        if (!On)
            return true;

        if (host is null)
            return false;

        // The list is replaced as a whole when it changes, never edited in place, so this read is safe from any thread.
        foreach (var off in App.Cfg.Cur.Ads.Off)
        {
            if (AdUrl.Covers(off, host))
                return true;
        }

        return false;
    }


    /// <summary>Where releases are looked for: the one next to neat.exe and the ones downloaded later.</summary>
    private static readonly FilterStore Store = new(
        Path.Combine(AppContext.BaseDirectory, "filters"),
        Path.Combine(Env.Root, "filters-dl"));

    /// <summary>The saved engine of the release in use, so the next start can skip reading the lists.</summary>
    private static readonly string CachePath = Path.Combine(Env.Root, "ads-cache", "engine.bin");

    private static readonly HttpClient Http = MakeHttp();

    private static HttpClient MakeHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("NEAT-Browser-filter-update");
        return h;
    }

    /// <summary>Folder of the release in use.</summary>
    private string _dir = string.Empty;

    /// <summary>What the list updates did last, for the log and for a settings page later.</summary>
    public string UpdateStatus { get; private set; } = "not checked yet";

    /// <summary>
    /// Builds the engine on a background thread. Never throws: a missing dll, a damaged folder or
    /// a refusal of the engine is written to the log and the browser just runs without blocking.
    /// Then, unless settings turn it off, keeps looking for newer lists in the background.
    /// </summary>
    public Task Start() => Task.Run(async () =>
    {
        var dllOk = false;
        try
        {
            AdRequestLog.Start(App.Cfg.Cur.Ads.Log);

            if (!On)
            {
                Log.Write("ads", "blocker is switched off in settings");
                return;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var best = Store.Best(m => Log.Write("ads", m));
            if (best is null)
            {
                // Possibly the first run of a build without bundled lists: the updater below can fetch them.
                Log.Write("ads", "no usable filters next to neat.exe or downloaded yet");
                dllOk = true;
            }
            else
            {
                var (dir, version) = best.Value;
                var engine = Build(dir, out var fromCache);
                dllOk = true;
                Install(engine, dir, version);
                Log.Write("ads", $"filters {version} loaded in {clock.ElapsedMilliseconds} ms" + (fromCache ? " (from the saved engine)" : ""));
                if (!fromCache)
                    SaveEngineLater(engine, dir);
            }
        }
        catch (DllNotFoundException ex)
        {
            Log.Write("ads", "neat_adblock.dll not found, blocker off: " + ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("ads", ex, "could not start the blocker");
            dllOk = !(ex is DllNotFoundException);
        }
        finally
        {
            _done.TrySetResult();
        }

        if (dllOk && On)
            await UpdateLoop();
    });

    /// <summary>The engine for a release folder: from the saved engine when it belongs to it, else from the lists.</summary>
    private static AdsEngine Build(string dir, out bool fromCache)
    {
        FilterDir.Verify(dir);
        return AdsEngine.Load(dir, CachePath, FilterDir.ManifestKey(dir), out fromCache);
    }

    /// <summary>Makes an engine the one in use and lets go of the one before.</summary>
    private void Install(AdsEngine engine, string dir, string version)
    {
        _dir = dir;
        Version = version;
        var old = Interlocked.Exchange(ref _engine, engine);

        // A request may be inside a call on the old engine right now: it is let go a little later.
        if (old is not null)
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(_ => old.Dispose());
    }

    /// <summary>Saves the engine on another thread, a little later so it does not compete with the first pages.</summary>
    private static void SaveEngineLater(AdsEngine engine, string dir)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10));
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var ok = engine.SaveCache(CachePath, FilterDir.ManifestKey(dir));
                Log.Write("ads", ok ? $"engine saved in {clock.ElapsedMilliseconds} ms" : "the engine could not be saved (the next start reads the lists again)");
            }
            catch (Exception ex)
            {
                Log.Write("ads", "the engine could not be saved: " + ex.Message);
            }
        });
    }

    /// <summary>
    /// Looks for a newer release shortly after start and then every few hours, for as long as the
    /// app runs. A failure is only logged (and tried again sooner than the usual wait); the lists
    /// in use stay as they are until a complete, verified newer release is there.
    /// </summary>
    private async Task UpdateLoop()
    {
        var prefs = App.Cfg.Cur.Ads;
        if (!prefs.Update)
        {
            UpdateStatus = "switched off in settings";
            return;
        }

        var every = TimeSpan.FromHours(Math.Clamp(prefs.UpdateHours, 1, 168));
        var updater = new FilterUpdater(Http, prefs.UpdateRepo, Store, m => Log.Write("ads", m));

        // The first pages come first.
        await Task.Delay(TimeSpan.FromSeconds(30));
        while (true)
        {
            var wait = every;
            try
            {
                var dir = await updater.CheckAsync(Version, CancellationToken.None);
                if (dir is null)
                {
                    UpdateStatus = $"up to date ({Version}), checked {DateTime.Now:HH:mm}";
                }
                else
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var (engine, fromCache) = await Task.Run(() => (Build(dir, out var c), c));
                    var version = FilterDir.ReadVersion(dir) ?? string.Empty;
                    Install(engine, dir, version);
                    UpdateStatus = $"updated to {version} at {DateTime.Now:HH:mm}";
                    Log.Write("ads", $"now using filters {version} (built in {clock.ElapsedMilliseconds} ms{(fromCache ? ", from the saved engine" : "")})");
                    if (!fromCache)
                        SaveEngineLater(engine, dir);

                    Store.CleanUp(new[] { _dir }, m => Log.Write("ads", m));
                }
            }
            catch (Exception ex)
            {
                UpdateStatus = "could not check for new lists: " + (ex.Message.Length > 90 ? ex.Message[..90] + "…" : ex.Message);
                Log.Write("ads", "list update failed: " + ex.Message);
                wait = every < TimeSpan.FromMinutes(30) ? every : TimeSpan.FromMinutes(30);
            }

            await Task.Delay(wait);
        }
    }

    /// <summary>Completes when the engine is built (or failed), or after the time given.</summary>
    public Task WhenReady(TimeSpan max) => Task.WhenAny(_done.Task, Task.Delay(max));

    /// <summary>Judges one request. Anything that goes wrong means allow.</summary>
    public Verdict Check(string url, string source, string type, string? method)
    {
        var e = _engine;
        if (e is null)
            return Verdict.Allow;

        try
        {
            var v = e.Check(url, source, type, method);
            if (v.Action != AdAction.Allow)
                Interlocked.Increment(ref _blocked);

            return v;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return Verdict.Allow;
        }
    }

    /// <summary>True when a window opened to <paramref name="target"/> by <paramref name="opener"/> should be stopped.</summary>
    public bool BlockPopup(string target, string opener)
    {
        try
        {
            return _engine?.BlockPopup(target, opener) == true;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return false;
        }
    }

    private volatile bool _noDocument;

    /// <summary>
    /// True when opening this address as a page should be stopped. Only rules written for pages
    /// count (uBlock Origin's behaviour); an ad host's ordinary rule does not stop a visit.
    /// A dll or release without page rules answers false.
    /// </summary>
    public bool BlockDocument(string url)
    {
        var e = _engine;
        if (e is null || _noDocument)
            return false;

        try
        {
            if (!e.BlockDocument(url))
                return false;

            Interlocked.Increment(ref _blocked);
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            _noDocument = true;
            Log.Write("ads", "neat_adblock.dll is too old for page rules; pages are not stopped");
            return false;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return false;
        }
    }

    /// <summary>What the cosmetic and scriptlet rules say about a page.</summary>
    public Cosmetic PageRules(string url)
    {
        try
        {
            return Cosmetic.Parse(_engine?.CosmeticJson(url));
        }
        catch (Exception ex)
        {
            Fail(ex);
            return Cosmetic.Empty;
        }
    }

    /// <summary>
    /// The generic hide selectors for classes and ids a page uses, as a JSON array, or null when
    /// there are none. <paramref name="classes"/> and <paramref name="ids"/> are the names.
    /// </summary>
    public string? Generic(IReadOnlyCollection<string> classes, IReadOnlyCollection<string> ids, IReadOnlyCollection<string> exceptions)
    {
        try
        {
            var json = _engine?.GenericJson(
                JsonSerializer.Serialize(classes), JsonSerializer.Serialize(ids), JsonSerializer.Serialize(exceptions));
            return string.IsNullOrEmpty(json) || json == "[]" ? null : json;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return null;
        }
    }

    /// <summary>The first failure is logged; a flood of the same one is not.</summary>
    private void Fail(Exception ex)
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0)
            Log.Error("ads", ex, "the engine failed on a call; further failures are not logged");
    }
}
