using System.IO.Enumeration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace neat;

/// <summary>
/// The Media section: a grid of thumbnails of the pictures, videos, audio files and PDFs in the
/// user's Downloads, Pictures and Documents folders, newest first, for browsing files on disk
/// without leaving the browser. Everything stays local: files are only read to make thumbnails,
/// and nothing is sent anywhere.
///
/// The folders are scanned on a background thread, down to <see cref="Depth"/> levels. Only the
/// names, times and attributes are read, so even a big tree is quick, and the result is kept in
/// memory: the section shows it at once when it is opened again and scans in the background to
/// refresh it. Search and filters work on that list in memory, so they need no new scan. The grid
/// is filled <see cref="Chunk"/> files at a time as it is scrolled.
/// </summary>
internal sealed class MediaPage : LibPage
{
    // How many levels of subfolders are looked into, below the three folders themselves.
    // 0 would be the files directly in them, 2 (the default) also their subfolders and the
    // subfolders of those. This is the one place to change it.
    private const int Depth = 2;

    // Files put into the grid at a time. The newest come first; the next chunk is added when
    // the grid is scrolled near its end.
    private const int Chunk = 300;

    // The 0x00040000 and 0x00400000 attributes Windows gives to an item that is not (fully) on
    // the disk: it is in the cloud and opening, reading or listing it makes Windows fetch it.
    // The .NET enum only has Offline (0x1000), the third of the three.
    private const FileAttributes RecallOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallData = (FileAttributes)0x00400000;
    private const FileAttributes Away = FileAttributes.Offline | RecallOpen | RecallData;

    // The types of the Filter menu. The ones after "All" follow the order of MediaKind.
    private static readonly string[] Types = { "All", "Images", "Videos", "Audio", "Documents" };

    // The folders of the Filter menu; the numbers after "All" are the ones MediaItem.Folder holds.
    private static readonly string[] Places = { "All", "Downloads", "Pictures", "Documents" };

    // Skips what the user cannot see in Explorer, and what they have no rights to look at.
    // Subfolders are walked by Walk, one at a time, so a symbolic link is never followed by accident.
    private static readonly EnumerationOptions Opts = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private readonly MediaList _list = new();

    // Every file found by the last scan, newest first. Kept when the section is left, so it opens at once.
    private List<MediaItem> _all = new();

    // The files that fit the search and the filters, and how many of them are in the grid.
    private List<MediaItem> _match = new();
    private int _shown;

    private string _q = string.Empty;
    private int _type;
    private int _where;

    // Counts the scans that were started; an answer from an older one is dropped.
    private int _ver;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public MediaPage()
    {
        _list.Picked += m => Try(() => InTab(m));
        _list.Asked += (m, at, pos) => Try(() => Ask(m, at, pos));
        _list.End += () => Try(More);
    }

    public override string Name
    {
        get { return "Media"; }
    }

    // Photo2 (E8B9): the glyph the rail has always had for this section.
    public override string Glyph
    {
        get { return ""; }
    }

    public override UIElement View
    {
        get { return _list; }
    }

    public override bool Searchable
    {
        get { return true; }
    }

    public override string Hint
    {
        get { return "Search media"; }
    }

    public override Task Load()
    {
        _q = string.Empty;
        _type = 0;
        _where = 0;

        // What the last scan found is shown at once, and the scan below replaces it if anything changed.
        Apply();
        _ = Scan();

        return Task.CompletedTask;
    }

    public override void Search(string text)
    {
        _q = text;
        Apply();
    }

    public override void Park()
    {
        // The Library closes or the section is left: stop the scan and the thumbnails still waiting.
        _ver++;
        _cts?.Cancel();
        _busy = false;
        Thumbs.Stop();

        // The Filter menu is built before Load runs (see LibView.Enter), so the filters go back to
        // their defaults here, or the menu would show the ones of the last visit.
        _q = string.Empty;
        _type = 0;
        _where = 0;
    }

    public override void Paint(Skin s)
    {
        _list.Paint(s);
    }

    public override MenuFlyout? Filter()
    {
        var menu = new MenuFlyout();

        menu.Items.Add(new MenuFlyoutItem { Text = "Type", IsEnabled = false });
        for (var i = 0; i < Types.Length; i++)
        {
            var at = i;
            menu.Items.Add(Radio(Types[i], "mediatype", at == _type, () =>
            {
                _type = at;
                Apply();
            }));
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(new MenuFlyoutItem { Text = "Folder", IsEnabled = false });
        for (var i = 0; i < Places.Length; i++)
        {
            var at = i;
            menu.Items.Add(Radio(Places[i], "mediafolder", at == _where, () =>
            {
                _where = at;
                Apply();
            }));
        }

        return menu;
    }

    private static RadioMenuFlyoutItem Radio(string text, string group, bool on, Action pick)
    {
        var item = new RadioMenuFlyoutItem { Text = text, GroupName = group, IsChecked = on };
        item.Click += (s, e) => Try(pick);
        return item;
    }

    // ---- what is shown ----

    /// <summary>Fills the grid from the files in memory, for the current search and filters: the newest chunk first.</summary>
    private void Apply()
    {
        _match = _all.Where(Fits).ToList();

        var first = _match.Take(Chunk).ToList();
        _shown = first.Count;

        _list.Set(first);
        Sync();
    }

    /// <summary>The next chunk of files is wanted: the grid was scrolled near its end.</summary>
    private void More()
    {
        if (_shown >= _match.Count)
            return;

        var end = Math.Min(_shown + Chunk, _match.Count);
        for (var i = _shown; i < end; i++)
            _list.Items.Add(_match[i]);

        _shown = end;
    }

    private bool Fits(MediaItem m)
    {
        if (_type != 0 && (int)m.Kind != _type - 1)
            return false;

        if (_where != 0 && m.Folder != _where)
            return false;

        return _q.Length == 0 || m.Name.Contains(_q, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>Shows the ring while the first scan has nothing to show yet, and the "nothing here" line when it is over and the grid is empty.</summary>
    private void Sync()
    {
        var empty = _list.Items.Count == 0;

        _list.Busy = _busy && empty;

        if (!empty || _busy)
            _list.Note = null;
        else
            _list.Note = _q.Length > 0 || _type != 0 || _where != 0 ? "No results" : "No media found";
    }

    // ---- scanning ----

    /// <summary>Looks through the folders on a pool thread and, when that finds something different from what is shown, shows it.</summary>
    private async Task Scan()
    {
        _cts?.Cancel();

        var cts = new CancellationTokenSource();
        _cts = cts;
        var ver = ++_ver;

        _busy = true;
        Sync();

        List<MediaItem>? got = null;

        try
        {
            got = await Task.Run(() => Collect(cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Another scan started or the Library closed; whoever did that owns the state now.
            return;
        }
        catch (Exception ex)
        {
            Log.Write("library", "media scan failed: " + ex.Message);
        }

        if (ver != _ver)
            return;

        _busy = false;

        // The same files as before: the grid stays as it is, so it does not jump back to the top.
        if (got is not null && !Same(_all, got))
        {
            _all = got;
            Apply();
        }
        else
        {
            Sync();
        }
    }

    /// <summary>Everything the three folders hold, newest first. Runs on a pool thread and stops soon after the token is cancelled.</summary>
    private static List<MediaItem> Collect(CancellationToken ct)
    {
        var all = new List<MediaItem>();

        // One file is found once even if one of the folders lies inside another.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, dir) in Roots())
            Walk(dir, id, 0, all, seen, ct);

        all.Sort((a, b) =>
        {
            var c = b.At.CompareTo(a.At);
            return c != 0 ? c : string.Compare(a.Full, b.Full, StringComparison.OrdinalIgnoreCase);
        });

        return all;
    }

    /// <summary>
    /// The user's Downloads, Pictures and Documents, wherever they have been moved to (a folder
    /// kept by OneDrive too), as Windows reports them. One that does not exist is left out.
    /// </summary>
    private static List<(int Id, string Dir)> Roots()
    {
        var roots = new List<(int, string)>();

        void Add(int id, string? dir)
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                roots.Add((id, dir));
        }

        try
        {
            var u = Windows.Storage.UserDataPaths.GetDefault();
            Add(1, u.Downloads);
            Add(2, u.Pictures);
            Add(3, u.Documents);
        }
        catch (Exception ex)
        {
            Log.Write("library", "could not ask Windows for the media folders: " + ex.Message);
        }

        return roots;
    }

    /// <summary>Adds the media files of one folder to the list, then does the same in its subfolders, one level deeper.</summary>
    private static void Walk(string dir, int id, int depth, List<MediaItem> into, HashSet<string> seen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var subs = new List<string>();

        try
        {
            var rows = new FileSystemEnumerable<Row>(dir, Make, Opts)
            {
                ShouldIncludePredicate = Want,
            };

            foreach (var r in rows)
            {
                ct.ThrowIfCancellationRequested();

                if (r.Dir)
                {
                    if (depth < Depth && Enter(r))
                        subs.Add(r.Full);

                    continue;
                }

                // Want() only lets media files through, so a kind is always found.
                var kind = KindOf(System.IO.Path.GetExtension(r.Name));
                if (kind is null || !seen.Add(r.Full))
                    continue;

                into.Add(new MediaItem(r.Full, r.Name, kind.Value, id, r.At, (r.Attr & Away) != 0));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A folder that went away or cannot be read while it is being listed: the rest is still scanned.
            Log.Write("library", "media scan skipped " + dir + ": " + ex.Message);
        }

        foreach (var sub in subs)
            Walk(sub, id, depth + 1, into, seen, ct);
    }

    /// <summary>
    /// Whether a subfolder is looked into. Not one that is only in the cloud: listing it can make
    /// the cloud service fetch its contents. Not a symbolic link or junction either, which can lead
    /// back to where the scan came from. A cloud placeholder is also a reparse point, but not a link,
    /// so the test is whether the folder has a link target and not whether it is a reparse point.
    /// </summary>
    private static bool Enter(Row r)
    {
        if ((r.Attr & Away) != 0)
            return false;

        if ((r.Attr & FileAttributes.ReparsePoint) == 0)
            return true;

        try
        {
            return new DirectoryInfo(r.Full).LinkTarget is null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>One entry of a folder, as the enumeration reports it: nothing but what comes with the listing, so no file is opened.</summary>
    private readonly record struct Row(bool Dir, string Full, string Name, FileAttributes Attr, DateTime At);

    private static Row Make(ref FileSystemEntry e)
    {
        return new Row(e.IsDirectory, e.ToFullPath(), e.FileName.ToString(), e.Attributes, e.LastWriteTimeUtc.UtcDateTime);
    }

    /// <summary>Folders (to look into) and the file types this section shows; nothing else is even turned into a Row.</summary>
    private static bool Want(ref FileSystemEntry e)
    {
        return e.IsDirectory || KindOf(System.IO.Path.GetExtension(e.FileName)) is not null;
    }

    private static MediaKind? KindOf(ReadOnlySpan<char> ext)
    {
        // The extensions are ASCII, so lower-casing a short copy is enough.
        return ext.ToString().ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" => MediaKind.Image,
            ".mp4" or ".mkv" or ".webm" or ".mov" => MediaKind.Video,
            ".mp3" or ".wav" or ".flac" or ".m4a" => MediaKind.Audio,
            ".pdf" => MediaKind.Doc,
            _ => null,
        };
    }

    /// <summary>True when the two scans found the same files, with the same times and the same cloud state, in the same order.</summary>
    private static bool Same(List<MediaItem> a, List<MediaItem> b)
    {
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].At != b[i].At || a[i].Cloud != b[i].Cloud || a[i].Full != b[i].Full)
                return false;
        }

        return true;
    }

    // ---- what a click and the menu do ----

    /// <summary>
    /// Opens the file in a new tab. The address is a file:// one, made by Uri so that spaces and
    /// accented or Vietnamese characters in the path are encoded; Win.Open accepts it as it is.
    /// Pictures, videos, audio and PDFs are all things the web view shows itself, and the scan
    /// keeps no other kind, so there is no "other type" to hand to the default app here: that is
    /// what "Open with default app" in the menu is for.
    /// </summary>
    private void InTab(MediaItem m)
    {
        if (!Exists(m))
            return;

        Opened?.Invoke(new Uri(m.Full).AbsoluteUri);
    }

    private void Ask(MediaItem m, FrameworkElement at, Windows.Foundation.Point pos)
    {
        var menu = new MenuFlyout();

        void Add(string text, Action act)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (s, e) => Try(act);
            menu.Items.Add(item);
        }

        Add("Open in new tab", () => InTab(m));
        Add("Open with default app", () => _ = Launch(m));
        Add("Show in folder", () => Reveal(m));
        Add("Copy path", () => Copier.Text(m.Full));

        menu.ShowAt(at, pos);
    }

    private async Task Launch(MediaItem m)
    {
        try
        {
            if (!Exists(m))
                return;

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(m.Full);

            // Windows says no (false) for kinds of file it will not start from here; nothing is shown for that.
            if (!await Windows.System.Launcher.LaunchFileAsync(file))
                Log.Write("library", "Windows would not open " + m.Name);
        }
        catch (Exception ex)
        {
            Log.Error("library", ex, "could not open " + m.Name);
        }
    }

    private void Reveal(MediaItem m)
    {
        if (!Exists(m))
            return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
        {
            Arguments = "/select,\"" + m.Full + "\"",
        });
    }

    /// <summary>Whether the file is still there. When it is not (moved or deleted since the scan) its tile goes.</summary>
    private bool Exists(MediaItem m)
    {
        if (File.Exists(m.Full))
            return true;

        _all.Remove(m);
        _match.Remove(m);

        var i = _list.Items.IndexOf(m);
        if (i >= 0)
        {
            _list.Items.RemoveAt(i);
            _shown--;
        }

        Sync();
        return false;
    }

    /// <summary>
    /// Everything here runs from a click or an event of the interface, and an exception that gets out
    /// of one of those ends the whole app.
    /// </summary>
    private static void Try(Action act)
    {
        try
        {
            act();
        }
        catch (Exception ex)
        {
            Log.Error("library", ex, "media");
        }
    }
}
