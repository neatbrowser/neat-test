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

    /// <summary>The blocker is switched off for this host (the person's own list).</summary>
    public bool IsOff(string? host) =>
        !On || (host is not null && App.Cfg.Cur.Ads.Off.Contains(host, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Builds the engine on a background thread. Never throws: a missing dll, a damaged folder or
    /// a refusal of the engine is written to the log and the browser just runs without blocking.
    /// </summary>
    public Task Start() => Task.Run(() =>
    {
        try
        {
            AdRequestLog.Start(App.Cfg.Cur.Ads.Log);

            if (!On)
            {
                Log.Write("ads", "blocker is switched off in settings");
                return;
            }

            var dir = Path.Combine(AppContext.BaseDirectory, "filters");
            if (!Directory.Exists(dir))
            {
                Log.Write("ads", "no filters folder next to neat.exe, blocker off");
                return;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var version = FilterDir.Verify(dir);
            var engine = AdsEngine.Create(dir);

            Version = version;
            var old = Interlocked.Exchange(ref _engine, engine);
            old?.Dispose();

            Log.Write("ads", $"filters {version} loaded in {clock.ElapsedMilliseconds} ms");
        }
        catch (DllNotFoundException ex)
        {
            Log.Write("ads", "neat_adblock.dll not found, blocker off: " + ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("ads", ex, "could not start the blocker");
        }
        finally
        {
            _done.TrySetResult();
        }
    });

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
