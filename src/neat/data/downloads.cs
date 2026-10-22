using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Web.WebView2.Core;

namespace neat;

/// <summary>
/// The download engine. Every web view hands its downloads to <see cref="Start"/>; from there
/// the app owns them: the file goes into the user's Downloads folder under a name that is safe
/// and never replaces an existing file, WebView2 shows no download bubble of its own, progress
/// is tracked and written to the database, and the Library's Downloads section shows the list.
///
/// Everything here runs on the UI thread (WebView2 raises its events there). The database is
/// written on a pool thread, one write after another, and a failed write is only logged: it must
/// never get in the way of a download. The list the UI shows (Items) is the live one.
/// </summary>
public sealed class Downloads
{
    // Downloads read back from the database at start.
    private const int Keep = 1000;

    // The longest a file name may be, however much room the folder leaves.
    private const int NameMax = 120;

    // Names Windows treats as devices. A file called CON or NUL.txt cannot be made.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly Db _db;

    // Paths promised to downloads that are still running. A file does not exist yet while it is
    // being fetched, so two downloads of the same name started together would otherwise get the same path.
    private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);

    // The last database write queued; the next one starts when it is done, so writes keep their order.
    private Task _tail = Task.CompletedTask;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tick;
    private int _active;

    public Downloads(Db db)
    {
        _db = db;
    }

    /// <summary>All downloads, newest first. Changes while downloads run; only touch it on the UI thread.</summary>
    public ObservableCollection<Download> Items { get; } = new();

    /// <summary>How many downloads are going or paused.</summary>
    public int Active
    {
        get { return _active; }
    }

    /// <summary>The number of active downloads changed.</summary>
    public event Action? ActiveChanged;

    /// <summary>
    /// Creates the table, marks downloads that were still running when the app last closed as
    /// interrupted, and reads the list back. Call it once at start, on the UI thread.
    /// </summary>
    public async Task Init()
    {
        // Running downloads are asked for their numbers twice a second. Events do the work
        // normally, but they can go missing (a tab closed on its download), and then the row
        // would stand still for ever.
        var q = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (q is not null)
        {
            _tick = q.CreateTimer();
            _tick.Interval = TimeSpan.FromMilliseconds(500);
            _tick.IsRepeating = true;
            _tick.Tick += (s, e) => Guard(Poll);
            _tick.Start();
        }

        // The database work is done on a pool thread: Microsoft.Data.Sqlite runs its "async" calls
        // synchronously, so on the UI thread it would stall the window.
        await Task.Run(async () =>
        {
            using var c = await _db.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS downloads (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    url TEXT NOT NULL,
                    path TEXT NOT NULL,
                    name TEXT NOT NULL,
                    bytes_total INTEGER NOT NULL DEFAULT 0,
                    bytes_received INTEGER NOT NULL DEFAULT 0,
                    state TEXT NOT NULL,
                    started_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_downloads_started_at ON downloads(started_at);

                -- Nothing can still be running at start: whatever was, the app closed on it.
                UPDATE downloads SET state = 'interrupted' WHERE state IN ('downloading', 'paused');
                """;
            await cmd.ExecuteNonQueryAsync();
        });

        var rows = await Task.Run(() => ReadAll());
        foreach (var d in rows)
            Items.Add(d);
    }

    /// <summary>
    /// A web view is starting a download. Takes it over completely (WebView2 shows nothing) and
    /// begins to follow it. Returns the new Download, or null when the download was refused.
    /// </summary>
    public Download? Start(CoreWebView2DownloadStartingEventArgs e)
    {
        // The app handles the download itself, so WebView2 must not show its own bubble.
        e.Handled = true;

        var op = e.DownloadOperation;
        string target;

        try
        {
            var dir = Dir();
            Directory.CreateDirectory(dir);

            var name = SafeName(Path.GetFileName(e.ResultFilePath), dir);
            target = Unique(dir, name);

            // Whatever the name was, the file has to end up inside the Downloads folder.
            if (!Inside(dir, target))
                throw new InvalidOperationException("the path leaves the Downloads folder: " + target);
        }
        catch (Exception ex)
        {
            Log.Error("dl", ex, "refused a download");
            e.Cancel = true;
            return null;
        }

        e.ResultFilePath = target;
        _held.Add(target);

        var d = new Download(op.Uri, Path.GetFileName(target), target, DateTime.Now) { Op = op };
        d.Move(0, Expected(op), 0, null);

        Items.Insert(0, d);
        Recount();

        op.BytesReceivedChanged += (s, a) => Guard(() => Sync(d));
        op.EstimatedEndTimeChanged += (s, a) => Guard(() => Sync(d));
        op.StateChanged += (s, a) => Guard(() => Sync(d));

        Insert(d);
        return d;
    }

    public void Cancel(Download d)
    {
        Guard(() => d.Op?.Cancel());
    }

    public void Pause(Download d)
    {
        Guard(() =>
        {
            if (d.State == DlState.Downloading)
                d.Op?.Pause();
        });
    }

    /// <summary>Carries on a paused download, or one that failed in a way WebView2 can pick up again.</summary>
    public void Resume(Download d)
    {
        Guard(() =>
        {
            if (d.Op is { CanResume: true } op)
                op.Resume();
        });
    }

    /// <summary>Takes a download off the list. The file is not touched. One that is still going is cancelled first.</summary>
    public void Remove(Download d)
    {
        if (d.State is DlState.Downloading or DlState.Paused)
            Cancel(d);

        Items.Remove(d);
        _held.Remove(d.Target);
        Recount();

        // d.Id is filled in by the write that made the row, which is ahead of this one in the queue.
        Enqueue(async () =>
        {
            if (d.Id == 0)
                return;

            using var c = await _db.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM downloads WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", d.Id);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    /// <summary>Takes every completed download off the list. The files stay.</summary>
    public void ClearCompleted()
    {
        foreach (var d in Items.Where(x => x.State == DlState.Completed).ToList())
        {
            Items.Remove(d);
            _held.Remove(d.Target);
        }

        Enqueue(async () =>
        {
            using var c = await _db.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM downloads WHERE state = 'completed';";
            await cmd.ExecuteNonQueryAsync();
        });
    }

    /// <summary>Opens the file with the program Windows has for it. Null when it worked, else a short message.</summary>
    public async Task<string?> Open(Download d)
    {
        try
        {
            if (!File.Exists(d.Target))
                return "File was moved or deleted";

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(d.Target);

            // Windows refuses to start programs from here (exe, bat and the like) and says so by returning false.
            var ok = await Windows.System.Launcher.LaunchFileAsync(file);
            return ok ? null : "Windows will not open this kind of file from here. Use Show in folder.";
        }
        catch (Exception ex)
        {
            Log.Error("dl", ex, "could not open " + d.Name);
            return "Could not open the file";
        }
    }

    /// <summary>Shows the file selected in Explorer. If it is gone, the Downloads folder is shown instead. Null when it worked.</summary>
    public string? Reveal(Download d)
    {
        try
        {
            if (File.Exists(d.Target))
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + d.Target + "\"" });
                return null;
            }

            var dir = Dir();
            if (!Directory.Exists(dir))
                return "File was moved or deleted";

            Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "\"" + dir + "\"" });
            return "File was moved or deleted";
        }
        catch (Exception ex)
        {
            Log.Error("dl", ex, "could not show " + d.Name + " in Explorer");
            return "Could not open Explorer";
        }
    }

    /// <summary>Shows a message in place of a download's status line for a few seconds.</summary>
    public async Task Flash(Download d, string message)
    {
        d.Notice = message;

        await Task.Delay(4000);

        // Only if nothing else has been said since.
        if (d.Notice == message)
            d.Notice = null;
    }

    // ---- following a download ----

    /// <summary>
    /// Brings a download up to date with its WebView2 operation: its state, and while it runs its
    /// numbers. Events, and the timer that backs them up, both end up here.
    /// </summary>
    private void Sync(Download d)
    {
        var op = d.Op;
        if (op is null)
            return;

        var now = op.State switch
        {
            CoreWebView2DownloadState.InProgress => DlState.Downloading,
            CoreWebView2DownloadState.Completed => DlState.Completed,
            _ => op.InterruptReason switch
            {
                CoreWebView2DownloadInterruptReason.UserCanceled => DlState.Canceled,
                CoreWebView2DownloadInterruptReason.UserPaused => DlState.Paused,
                _ => DlState.Failed,
            },
        };

        if (now == d.State)
        {
            Read(d);
            return;
        }

        var got = op.BytesReceived;
        var total = Expected(op);

        switch (now)
        {
            case DlState.Completed:
            {
                var size = SizeOf(d.Target, got);
                d.Move(size, size, 0, null);
                d.Op = null;
                break;
            }

            case DlState.Downloading:
            {
                // Resumed: the speed is measured afresh from here.
                d.Seen = 0;
                d.Prev = got;
                d.Move(got, total, 0, null);
                break;
            }

            case DlState.Canceled:
            {
                d.Move(got, total, 0, null);
                d.Op = null;
                break;
            }

            default:
            {
                // Paused, or failed (a failed one keeps its operation: some can be resumed).
                d.Move(got, total, 0, null);
                break;
            }
        }

        d.State = now;
        Settled(d);
    }

    /// <summary>Reads the numbers of a running download. At most four times a second, however often WebView2 reports.</summary>
    private void Read(Download d)
    {
        var op = d.Op;
        if (op is null || d.State != DlState.Downloading)
            return;

        var now = Environment.TickCount64;
        if (now - d.Seen < 250)
            return;

        var got = op.BytesReceived;
        var total = Expected(op);

        // Bytes per second since the last reading, smoothed so the number does not jump about.
        var speed = d.Speed;
        if (d.Seen > 0 && now > d.Seen)
        {
            var fresh = (got - d.Prev) * 1000.0 / (now - d.Seen);
            speed = speed <= 0 ? fresh : speed * 0.6 + fresh * 0.4;
        }

        d.Seen = now;
        d.Prev = got;
        d.Move(got, total, Math.Max(0, speed), Left(op));

        // Written now and then, not on every reading: the database is not for live numbers.
        if (now - d.Saved > 3000)
        {
            d.Saved = now;
            Save(d);
        }
    }

    /// <summary>Asks every running download where it is. See Init for why.</summary>
    private void Poll()
    {
        if (_active == 0)
            return;

        foreach (var d in Items.ToList())
        {
            if (d.Op is null || d.State is not (DlState.Downloading or DlState.Paused))
                continue;

            try
            {
                Sync(d);
            }
            catch (Exception ex)
            {
                // The operation cannot be asked any more (its web view is gone): this one is lost.
                Log.Error("dl", ex, "lost track of " + d.Name);
                d.Move(d.Got, d.Total, 0, null);
                d.Op = null;
                d.State = DlState.Interrupted;
                Settled(d);
            }
        }
    }

    /// <summary>A download changed state: let go of its file name if it is over, recount, write it down.</summary>
    private void Settled(Download d)
    {
        if (d.State is not (DlState.Downloading or DlState.Paused))
            _held.Remove(d.Target);

        Recount();
        Save(d);
    }

    private void Recount()
    {
        var n = Items.Count(x => x.State is DlState.Downloading or DlState.Paused);
        if (n == _active)
            return;

        _active = n;
        ActiveChanged?.Invoke();
    }

    /// <summary>
    /// WebView2 gives the end time as an ISO 8601 string, empty until it can tell. Returns how long
    /// is left, or null when it is not known or not believable.
    /// </summary>
    private static TimeSpan? Left(CoreWebView2DownloadOperation op)
    {
        if (!DateTimeOffset.TryParse(op.EstimatedEndTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            return null;

        var t = end - DateTimeOffset.Now;
        return t > TimeSpan.Zero && t < TimeSpan.FromDays(1) ? t : null;
    }

    /// <summary>
    /// The size the server announced, or 0 when it did not. TotalBytesToReceive is a plain long,
    /// not a nullable one, and says "not known" with a negative number (-1), so it cannot be
    /// combined with ??. The rest of the code (Download.Percent, Download.Unknown) reads 0 as unknown.
    /// </summary>
    private static long Expected(CoreWebView2DownloadOperation op)
    {
        return Math.Max(0, op.TotalBytesToReceive);
    }

    private static long SizeOf(string path, long fallback)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Runs a piece of code that touches WebView2. An exception that gets out of a WebView2 event
    /// handler ends the app, and the web view can be gone by the time an event arrives, so
    /// nothing is allowed to get out.
    /// </summary>
    private static void Guard(Action act)
    {
        try
        {
            act();
        }
        catch (Exception ex)
        {
            Log.Error("dl", ex, "download event");
        }
    }

    // ---- where the file goes ----

    /// <summary>The user's Downloads folder, wherever they moved it to.</summary>
    private static string Dir()
    {
        try
        {
            var p = Windows.Storage.UserDataPaths.GetDefault().Downloads;
            if (!string.IsNullOrEmpty(p))
                return p;
        }
        catch (Exception ex)
        {
            Log.Error("dl", ex, "could not ask Windows for the Downloads folder");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>
    /// Makes a file name out of what the server suggested: only the last part (a name with slashes
    /// must not reach into another folder), no characters Windows does not allow, no trailing dots
    /// or spaces, no device names, and short enough for the whole path to fit.
    /// </summary>
    private static string SafeName(string? raw, string dir)
    {
        var s = raw ?? string.Empty;
        s = s[(Math.Max(s.LastIndexOf('/'), s.LastIndexOf('\\')) + 1)..];

        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');

        // Windows drops trailing dots and spaces from a name, so the file would not have the name we think.
        s = s.Trim().TrimEnd('.', ' ');
        if (s.Length == 0)
            s = "download";

        // CON, NUL, COM1 and the rest are devices, with any extension: CON.txt and CON.tar.gz too.
        if (Reserved.Contains(s.Split('.')[0]))
            s = "_" + s;

        // Room for the folder and " (9999)": a whole path may be 259 characters at most.
        var max = Math.Clamp(250 - dir.Length - 7, 20, NameMax);
        if (s.Length > max)
        {
            var ext = Path.GetExtension(s);
            if (ext.Length > 16)
                ext = ext[..16];

            s = s[..Math.Max(1, max - ext.Length)] + ext;
        }

        return s;
    }

    /// <summary>The path for a new file: the name itself, or "name (1).ext", "name (2).ext"... never one that is taken.</summary>
    private string Unique(string dir, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var path = Path.Combine(dir, name);

        for (var n = 1; File.Exists(path) || _held.Contains(path); n++)
        {
            if (n > 9999)
                throw new IOException("no free name for " + name);

            path = Path.Combine(dir, stem + " (" + n + ")" + ext);
        }

        return path;
    }

    /// <summary>Whether a path is inside a folder, whatever tricks the name plays (.., slashes, a different case).</summary>
    private static bool Inside(string dir, string path)
    {
        var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(path);
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the database ----

    /// <summary>Queues a database write behind the ones before it. A failed write is logged and nothing more.</summary>
    private void Enqueue(Func<Task> work)
    {
        _tail = _tail.ContinueWith(
            async _ =>
            {
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    Log.Error("dl", ex, "database write failed");
                }
            },
            TaskScheduler.Default).Unwrap();
    }

    private void Insert(Download d)
    {
        var at = d.Started.ToUniversalTime().ToString("O");
        var total = d.Total;

        Enqueue(async () =>
        {
            using var c = await _db.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT INTO downloads (url, path, name, bytes_total, bytes_received, state, started_at)
                VALUES ($url, $path, $name, $total, 0, 'downloading', $at)
                RETURNING id;
                """;
            cmd.Parameters.AddWithValue("$url", d.Url);
            cmd.Parameters.AddWithValue("$path", d.Target);
            cmd.Parameters.AddWithValue("$name", d.Name);
            cmd.Parameters.AddWithValue("$total", total);
            cmd.Parameters.AddWithValue("$at", at);
            d.Id = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        });
    }

    private void Save(Download d)
    {
        // The numbers are copied now, on the UI thread; the write happens later on another.
        var got = d.Got;
        var total = d.Total;
        var state = d.State.ToString().ToLowerInvariant();

        Enqueue(async () =>
        {
            // No id: the row was never written (the insert failed), so there is nothing to update.
            if (d.Id == 0)
                return;

            using var c = await _db.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE downloads SET bytes_total = $total, bytes_received = $got, state = $state WHERE id = $id;";
            cmd.Parameters.AddWithValue("$total", total);
            cmd.Parameters.AddWithValue("$got", got);
            cmd.Parameters.AddWithValue("$state", state);
            cmd.Parameters.AddWithValue("$id", d.Id);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    private async Task<List<Download>> ReadAll()
    {
        var list = new List<Download>();

        using var c = await _db.Open();
        var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, url, path, name, bytes_total, bytes_received, state, started_at
            FROM downloads
            ORDER BY started_at DESC, id DESC
            LIMIT $n;
            """;
        cmd.Parameters.AddWithValue("$n", Keep);

        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var d = new Download(r.GetString(1), r.GetString(3), r.GetString(2), DateTime.Parse(r.GetString(7)))
            {
                Id = r.GetInt32(0),
            };

            // The numbers go in first, then the state, so the row is complete when it says what it is.
            d.Move(r.GetInt64(5), r.GetInt64(4), 0, null);
            d.State = Enum.TryParse<DlState>(r.GetString(6), true, out var st) ? st : DlState.Interrupted;

            list.Add(d);
        }

        return list;
    }
}
