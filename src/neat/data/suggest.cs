using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace neat;

public enum HintKind
{
    Tab,
    Mark,
    Visit,
    Search,
}

/// <summary>
/// One row in the command bar's list: an open tab, a bookmark, a visited page or the typed
/// text itself. The list binds to these fields. Only the icon can change after the row is
/// made (a favicon that fails to load falls back to the glyph), so only it notifies.
/// </summary>
public sealed class Hint : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public event PropertyChangedEventHandler? PropertyChanged;

    public HintKind Kind { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>Quieter text after the title: the site the page is on.</summary>
    public string Sub { get; init; } = string.Empty;

    /// <summary>Small label at the right end of the row ("Switch to Tab").</summary>
    public string Tag { get; init; } = string.Empty;

    /// <summary>The address to open. For the search row it is the typed text, not yet resolved.</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>For a tab row, the tab it switches to.</summary>
    public Tab? Target { get; init; }

    /// <summary>Segoe icon shown until there is a favicon.</summary>
    public string Glyph { get; init; } = "\uE774";

    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            _icon = value;
            Raise();
            Raise(nameof(IconVis));
            Raise(nameof(GlyphVis));
        }
    }

    public Visibility IconVis => _icon is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility GlyphVis => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Uses an icon that is already loaded (a tab's favicon).</summary>
    public void UseIcon(ImageSource? img)
    {
        Icon = img;
    }

    /// <summary>Loads a favicon from the address kept in history or bookmarks. Call it on the UI thread.</summary>
    public void LoadIcon(string? uri)
    {
        if (string.IsNullOrEmpty(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var u))
            return;

        var img = new BitmapImage(u);

        // Formats the image control cannot decode (e.g. SVG) fall back to the glyph.
        img.ImageFailed += (s, e) => Icon = null;

        Icon = img;
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Works out the rows of the command bar from what the box holds. It reads nothing itself:
/// the tabs, bookmarks and visits are handed in, so it runs on the UI thread without
/// waiting for anything. Order: open tabs, bookmarks, history, then the typed text.
/// </summary>
public static class Suggest
{
    /// <summary>Most rows that come from tabs, bookmarks and history together (the typed-text row is extra).</summary>
    public const int Max = 8;

    private const int MaxTabs = 4;
    private const int MaxMarks = 3;

    /// <summary>
    /// The word to ask the history table for. It matches one piece of text, so with several
    /// words the longest one is asked for and <see cref="Make"/> checks the rest.
    /// </summary>
    public static string Term(string query)
    {
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .OrderByDescending(w => w.Length)
            .FirstOrDefault() ?? string.Empty;
    }

    /// <summary>
    /// Builds the list. An empty query lists the open tabs and a few bookmarks. <paramref name="skip"/>
    /// is left out of the tabs (the tab whose address is being edited).
    /// </summary>
    public static List<Hint> Make(
        string query, IEnumerable<Tab> tabs, Tab? skip,
        IEnumerable<Mark> marks, IEnumerable<Visit> visits, Search find)
    {
        var q = query.Trim();
        var rows = new List<Hint>();

        // A page that is open, bookmarked and in history shows once, as the first of those.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var n = 0;
        foreach (var t in tabs)
        {
            if (n >= MaxTabs)
                break;

            if (t == skip || string.IsNullOrEmpty(t.Src) || !Hit(q, t.Title, t.Src) || !seen.Add(Key(t.Src)))
                continue;

            var h = Row(HintKind.Tab, t.Title, t.Src, "Switch to Tab \u2192", "\uE774", t);
            h.UseIcon(t.Icon);
            rows.Add(h);
            n++;
        }

        n = 0;
        foreach (var m in marks)
        {
            if (n >= MaxMarks)
                break;

            if (!Hit(q, m.Title, m.Url) || !seen.Add(Key(m.Url)))
                continue;

            var h = Row(HintKind.Mark, m.Title, m.Url, "Bookmark", "\uE734", null);
            h.LoadIcon(m.Icon);
            rows.Add(h);
            n++;
        }

        foreach (var v in visits)
        {
            if (rows.Count >= Max)
                break;

            if (!Hit(q, v.Title, v.Url) || !seen.Add(Key(v.Url)))
                continue;

            var h = Row(HintKind.Visit, v.Title, v.Url, string.Empty, "\uE81C", null);
            h.LoadIcon(v.Icon);
            rows.Add(h);
        }

        if (q.Length > 0)
        {
            var addr = find.IsAddress(q);
            rows.Add(new Hint
            {
                Kind = HintKind.Search,
                Title = q,
                Sub = addr ? string.Empty : " \u2014 " + find.Use.Name,
                Tag = addr ? "Open" : "Search",
                Url = q,
                Glyph = addr ? "\uE774" : "\uE721",
            });
        }

        return rows;
    }

    private static Hint Row(HintKind kind, string title, string url, string tag, string glyph, Tab? target)
    {
        var host = Host(url);
        var named = !string.IsNullOrWhiteSpace(title);

        return new Hint
        {
            Kind = kind,
            Title = named ? title : host,
            Sub = named ? " \u2014 " + host : string.Empty,
            Tag = tag,
            Url = url,
            Target = target,
            Glyph = glyph,
        };
    }

    /// <summary>Every word typed has to be in the title or the address.</summary>
    private static bool Hit(string q, string title, string url)
    {
        foreach (var w in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!title.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                && !url.Contains(w, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>The address with a closing slash ignored, so "site.com" and "site.com/" are one page.</summary>
    private static string Key(string url)
    {
        return url.TrimEnd('/');
    }

    private static string Host(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host))
            return u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;

        return url;
    }
}
