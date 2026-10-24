using System.Collections.Concurrent;
using System.Text;

namespace neat;

/// <summary>
/// One line per request the blocker saw: what it was, and what was done with it, including the
/// requests it did not judge and why. Meant for answering "why did this host get through?".
/// Off unless settings.json has <c>"Ads": { "Log": true }</c>; it is read once at start.
///
/// The file is %LOCALAPPDATA%\NEAT\ads-requests.tsv (tab separated). Lines are queued and written
/// by one background thread, so the page never waits for the disk. At 20 MB the file is renamed to
/// ads-requests.tsv.old (replacing the previous one).
/// </summary>
internal static class AdRequestLog
{
    private const long MaxBytes = 20_000_000;
    private const int MaxUrl = 300;
    private const int MaxQueue = 20_000;

    private static readonly BlockingCollection<string> Queue = new(MaxQueue);
    private static readonly string Where = Path.Combine(Env.Root, "ads-requests.tsv");
    private static int _started;

    /// <summary>True when requests are being written. Set once by <see cref="Start"/>.</summary>
    public static volatile bool Enabled;

    /// <summary>Starts the writer when the setting asks for it. Never throws.</summary>
    public static void Start(bool wanted)
    {
        if (!wanted || Interlocked.Exchange(ref _started, 1) == 1)
            return;

        try
        {
            Directory.CreateDirectory(Env.Root);
            Enabled = true;
            Queue.Add($"# {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}\tnew run\ttime\tverdict\ttype\tmethod\tpage\turl");
            new Thread(Run) { IsBackground = true, Name = "ads-request-log" }.Start();
            Log.Write("ads", "request log is on: " + Where);
        }
        catch (Exception ex)
        {
            Enabled = false;
            Log.Error("ads", ex, "could not start the request log");
        }
    }

    /// <param name="verdict">block, redirect, allow, or skip:reason.</param>
    /// <param name="type">The WebView2 resource context (Fetch, XmlHttpRequest, Ping, ...).</param>
    public static void Add(string verdict, string type, string? method, string? page, string url)
    {
        if (!Enabled)
            return;

        var shown = url.Length > MaxUrl ? url[..MaxUrl] + "…" : url;
        var line = $"{DateTime.Now:HH:mm:ss.fff}\t{verdict}\t{type}\t{method}\t{AdUrl.Host(page ?? string.Empty)}\t{shown}";

        // A full queue means the disk cannot keep up; dropping a line is better than slowing a page.
        Queue.TryAdd(line);
    }

    private static void Run()
    {
        try
        {
            var batch = new StringBuilder();
            while (true)
            {
                batch.Clear();
                batch.AppendLine(Queue.Take());
                while (batch.Length < 64_000 && Queue.TryTake(out var more))
                    batch.AppendLine(more);

                var info = new FileInfo(Where);
                if (info.Exists && info.Length > MaxBytes)
                    File.Move(Where, Where + ".old", overwrite: true);

                File.AppendAllText(Where, batch.ToString(), Encoding.UTF8);
                Thread.Sleep(200);
            }
        }
        catch (Exception ex)
        {
            Enabled = false;
            Log.Error("ads", ex, "the request log stopped");
        }
    }
}
