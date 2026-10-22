using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace neat;

/// <summary>
/// A plain text diagnostic log: %LOCALAPPDATA%\NEAT\crash.log. It exists to
/// answer "what was the browser doing right before it died?", because a WinUI
/// crash leaves almost nothing in Event Viewer. Every line is written to disk
/// at once, so it survives the process being killed a moment later.
///
/// Logging must never be the thing that breaks the app: nothing in here throws.
/// When the file grows past 1 MB it is renamed to crash.log.old (replacing the
/// previous .old), so it cannot fill the disk.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1_000_000;

    // An operation that takes at least this long is written to the log.
    private const int SlowMs = 100;

    private static readonly object Gate = new();
    private static readonly string Where = System.IO.Path.Combine(Env.Root, "crash.log");

    /// <summary>
    /// What the UI thread is doing right now, set by <see cref="Timed"/>. When the
    /// heartbeat finds the UI thread stuck, this is the best guess at why. It is
    /// only a hint: two overlapping operations share one slot.
    /// </summary>
    public static volatile string Op = string.Empty;

    /// <summary>Number of open tabs, kept up to date by the window so any thread can read it.</summary>
    public static volatile int Tabs;

    public static void Write(string tag, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Env.Root);
                Rotate();

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] (thread {Environment.CurrentManagedThreadId}) {msg}{Environment.NewLine}";
                File.AppendAllText(Where, line, Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Nowhere left to report this.
        }
    }

    /// <summary>Writes an exception with its type, HRESULT, message and full stack trace.</summary>
    public static void Error(string tag, Exception? ex, string? note = null)
    {
        var what = ex is null
            ? "(no exception object)"
            : $"{ex.GetType().FullName} HRESULT 0x{ex.HResult:X8}: {ex}";

        Write(tag, note is null ? what : note + " | " + what);
    }

    /// <summary>The first line of every run: version, Windows, memory and which WebView2 runtime is used.</summary>
    public static void Start()
    {
        var ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576;

        Write("app", $"start, v{typeof(Log).Assembly.GetName().Version}, {Environment.OSVersion.VersionString}, " +
            $"{RuntimeInformation.FrameworkDescription}, {Environment.ProcessorCount} cores, {ram} MB RAM, " +
            $"WebView2 runtime {(Env.UsesFixed ? "bundled" : "system")}, folder {AppContext.BaseDirectory}");
    }

    /// <summary>Runs the work and writes a "slow" line if it took long. Also records what the UI thread is busy with.</summary>
    public static async Task Timed(string what, Func<Task> work)
    {
        var sw = Stopwatch.StartNew();
        Op = what;

        try
        {
            await work();
        }
        finally
        {
            Done(what, sw);
        }
    }

    /// <summary>Same as <see cref="Timed"/>, for work that returns a value.</summary>
    public static async Task<T> TimedValue<T>(string what, Func<Task<T>> work)
    {
        var sw = Stopwatch.StartNew();
        Op = what;

        try
        {
            return await work();
        }
        finally
        {
            Done(what, sw);
        }
    }

    private static void Done(string what, Stopwatch sw)
    {
        Op = string.Empty;

        if (sw.ElapsedMilliseconds >= SlowMs)
            Write("slow", $"{what} took {sw.ElapsedMilliseconds} ms");
    }

    private static void Rotate()
    {
        var f = new FileInfo(Where);
        if (f.Exists && f.Length > MaxBytes)
            File.Move(Where, Where + ".old", overwrite: true);
    }
}

/// <summary>
/// Notices when the UI thread stops answering. A background timer queues a tiny
/// job on the UI thread every half second; if the job has not run after two
/// seconds, the UI thread is stuck (the spinning cursor) and a "stall" line is
/// written with what it was doing, how many web view processes exist and how
/// much memory they use. The line repeats every few seconds while the stall
/// lasts, and a final line says how long it was. If the app dies during a
/// stall, the last "stall" line in crash.log is the evidence.
///
/// It cannot say which line of code is stuck (that needs a debugger or a crash
/// dump), but together with the "slow" lines it tells whether the cause is the
/// database, memory pressure, or something else.
/// </summary>
internal static class Heartbeat
{
    private const int Tick = 500;     // ms between pokes
    private const int Limit = 2000;   // ms without an answer before it counts as a stall
    private const int Again = 5000;   // ms between repeated reports while a stall goes on

    private static System.Threading.Timer? _timer;
    private static int _pending;      // 1 while a poke waits in the UI queue unanswered
    private static long _sent;        // Environment.TickCount64 when that poke was queued
    private static long _next = Limit; // how long a poke must wait before the next report
    private static volatile bool _stalled;

    /// <summary>Starts watching. <paramref name="post"/> queues an action on the UI thread and returns false if it cannot.</summary>
    public static void Start(Func<Action, bool> post)
    {
        if (_timer is not null)
            return;

        _timer = new System.Threading.Timer(_ => Poke(post), null, Tick, Tick);
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    // Runs on the timer's own thread.
    private static void Poke(Func<Action, bool> post)
    {
        try
        {
            var now = Environment.TickCount64;

            // Only one poke is ever queued, so a long stall does not pile up work.
            if (Interlocked.CompareExchange(ref _pending, 1, 0) == 0)
            {
                Volatile.Write(ref _sent, now);

                // False means the UI thread is gone (the window is closing).
                if (!post(Answer))
                    Interlocked.Exchange(ref _pending, 0);

                return;
            }

            var waited = now - Volatile.Read(ref _sent);
            if (waited < Volatile.Read(ref _next))
                return;

            _stalled = true;
            Volatile.Write(ref _next, waited + Again);

            Log.Write("stall", $"UI thread has not answered for {waited} ms; last operation '{Log.Op}'; tabs {Log.Tabs}; {Load()}");
        }
        catch (Exception)
        {
            // The watchdog must never become a problem of its own.
        }
    }

    // Runs on the UI thread, once it is free again.
    private static void Answer()
    {
        var late = Environment.TickCount64 - Volatile.Read(ref _sent);

        if (_stalled)
        {
            _stalled = false;
            Log.Write("stall", $"UI thread answered again after {late} ms");
        }

        Volatile.Write(ref _next, Limit);
        Interlocked.Exchange(ref _pending, 0);
    }

    /// <summary>A one-line picture of the machine's memory: web view processes and overall load.</summary>
    private static string Load()
    {
        try
        {
            var n = 0;
            long used = 0;

            foreach (var p in Process.GetProcessesByName("msedgewebview2"))
            {
                using (p)
                {
                    n++;

                    try
                    {
                        used += p.WorkingSet64;
                    }
                    catch (Exception)
                    {
                        // The process ended while we were looking at it.
                    }
                }
            }

            var gc = GC.GetGCMemoryInfo();

            return $"{n} msedgewebview2 processes using {used / 1048576} MB, " +
                $"neat.exe {Environment.WorkingSet / 1048576} MB, " +
                $"system memory {gc.MemoryLoadBytes / 1048576} of {gc.TotalAvailableMemoryBytes / 1048576} MB in use (as of the last GC)";
        }
        catch (Exception ex)
        {
            return "memory unknown: " + ex.Message;
        }
    }
}
