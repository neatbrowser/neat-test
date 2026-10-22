using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;

namespace neat;

/// <summary>
/// Where a download is. WebView2 itself only knows in progress, interrupted and completed;
/// the reason it gives for an interruption tells a cancel and a pause from a failure.
/// Interrupted here means something else: the app was closed while the download ran.
/// </summary>
public enum DlState
{
    Downloading,
    Paused,
    Completed,
    Failed,
    Canceled,
    Interrupted,
}

/// <summary>
/// One download, as the Library shows it. While it runs the numbers change many times a
/// second, and every change reaches the row that shows it (INotifyPropertyChanged), so the
/// list is never rebuilt to show progress.
/// </summary>
public sealed class Download : INotifyPropertyChanged
{
    private DlState _state;
    private long _got;
    private long _total;
    private double _speed;
    private TimeSpan? _left;
    private string? _notice;

    public Download(string url, string name, string target, DateTime started)
    {
        Url = url;
        Name = name;
        Target = target;
        Started = started;
        Host = HostOf(url);
        Glyph = GlyphOf(name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The row id in the database; 0 until the row has been written.</summary>
    public int Id { get; set; }

    public string Url { get; }

    /// <summary>The file name as it ended up in the Downloads folder (after "(1)" and the like).</summary>
    public string Name { get; }

    /// <summary>The full path of the file.</summary>
    public string Target { get; }

    public DateTime Started { get; }

    public string Host { get; }

    /// <summary>The icon for the kind of file, chosen by its extension.</summary>
    public string Glyph { get; }

    /// <summary>The WebView2 operation behind a download that is still going; null for one read back from the database.</summary>
    internal CoreWebView2DownloadOperation? Op { get; set; }

    // Throttling for the service (milliseconds on Environment.TickCount64): when the numbers
    // were last read, when the database was last written, and the byte count at the last reading.
    internal long Seen { get; set; }

    internal long Saved { get; set; }

    internal long Prev { get; set; }

    public DlState State
    {
        get
        {
            return _state;
        }

        set
        {
            if (_state == value)
                return;

            _state = value;
            Raise(nameof(State));
            Show();
        }
    }

    /// <summary>Bytes received so far.</summary>
    public long Got
    {
        get { return _got; }
    }

    /// <summary>Size of the file; 0 when the server did not say.</summary>
    public long Total
    {
        get { return _total; }
    }

    public double Speed
    {
        get { return _speed; }
    }

    /// <summary>A short message that takes the place of the status line for a moment ("File was moved or deleted").</summary>
    public string? Notice
    {
        get
        {
            return _notice;
        }

        set
        {
            if (_notice == value)
                return;

            _notice = value;
            Raise(nameof(Line));
        }
    }

    /// <summary>0 to 100; 0 while the size is not known.</summary>
    public double Percent
    {
        get { return _total > 0 ? Math.Min(100, _got * 100.0 / _total) : 0; }
    }

    /// <summary>The bar has no end to fill when the server did not give a size.</summary>
    public bool Unknown
    {
        get { return _total <= 0 && _state == DlState.Downloading; }
    }

    public Visibility BarVis
    {
        get { return (_state is DlState.Downloading or DlState.Paused) ? Visibility.Visible : Visibility.Collapsed; }
    }

    /// <summary>Only web addresses can be fetched again; a blob: or data: address cannot.</summary>
    public bool CanRetry
    {
        get
        {
            return Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Whether WebView2 can carry on from where it stopped (a pause, or some network failures).</summary>
    public bool CanResume
    {
        get
        {
            try
            {
                return Op?.CanResume ?? false;
            }
            catch
            {
                // The operation is gone (its web view was closed): nothing to resume.
                return false;
            }
        }
    }

    /// <summary>The second line of the row: what the download is doing, in words.</summary>
    public string Line
    {
        get
        {
            if (_notice is not null)
                return _notice;

            switch (_state)
            {
                case DlState.Downloading:
                    return Doing("Downloading");

                case DlState.Paused:
                    return Doing("Paused");

                case DlState.Completed:
                    return "Completed, " + Size(_total > 0 ? _total : _got) + ", " + Host;

                case DlState.Failed:
                    return "Failed";

                case DlState.Canceled:
                    return "Canceled";

                default:
                    return "Interrupted";
            }
        }
    }

    /// <summary>Takes in new numbers from the service and tells the row once.</summary>
    internal void Move(long got, long total, double speed, TimeSpan? left)
    {
        _got = got;
        _total = total;
        _speed = speed;
        _left = left;
        Show();
    }

    private void Show()
    {
        Raise(nameof(Line));
        Raise(nameof(Percent));
        Raise(nameof(Unknown));
        Raise(nameof(BarVis));
    }

    private string Doing(string word)
    {
        var s = word;

        if (_total > 0)
            s += " " + Percent.ToString("0") + "%";
        else if (_got > 0)
            s += " " + Size(_got);

        if (_state == DlState.Downloading && _speed > 0)
            s += ", " + Size((long)_speed) + "/s";

        if (_state == DlState.Downloading && _left is { } t && t > TimeSpan.FromSeconds(3))
            s += ", " + Span(t) + " left";

        return s;
    }

    /// <summary>12.4 MB, 512 KB. Powers of 1024, like Explorer.</summary>
    private static string Size(long bytes)
    {
        string[] unit = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        var i = 0;

        while (v >= 1024 && i < unit.Length - 1)
        {
            v /= 1024;
            i++;
        }

        var fmt = i == 0 || v >= 100 ? "0" : "0.#";
        return v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture) + " " + unit[i];
    }

    private static string Span(TimeSpan t)
    {
        if (t.TotalSeconds < 60)
            return Math.Ceiling(t.TotalSeconds) + " s";

        if (t.TotalMinutes < 60)
            return Math.Ceiling(t.TotalMinutes) + " min";

        return Math.Floor(t.TotalHours) + " h " + t.Minutes + " min";
    }

    private static string HostOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.Host))
            return url;

        return u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
    }

    /// <summary>
    /// A glyph for the kind of file. All five are in both Segoe Fluent Icons and Segoe MDL2 Assets:
    /// Picture, Video, Audio, Package and Document (the default).
    /// </summary>
    private static string GlyphOf(string name)
    {
        var ext = System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();

        switch (ext)
        {
            case "jpg":
            case "jpeg":
            case "png":
            case "gif":
            case "webp":
            case "bmp":
            case "svg":
            case "ico":
            case "avif":
            case "heic":
                return "\uE8B9";

            case "mp4":
            case "mkv":
            case "webm":
            case "avi":
            case "mov":
            case "wmv":
            case "flv":
            case "m4v":
                return "\uE714";

            case "mp3":
            case "wav":
            case "flac":
            case "ogg":
            case "m4a":
            case "aac":
            case "opus":
                return "\uE8D6";

            case "zip":
            case "rar":
            case "7z":
            case "tar":
            case "gz":
            case "bz2":
            case "xz":
            case "exe":
            case "msi":
            case "msix":
            case "appx":
                return "\uE7B8";

            default:
                return "\uE8A5";
        }
    }

    private void Raise(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
