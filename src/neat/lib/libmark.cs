using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace neat;

/// <summary>
/// The Bookmarks section. All bookmarks are read at once (there are not thousands of them)
/// and kept in memory; search and sort then just rebuild the list from that, with no
/// trip to the database.
/// </summary>
internal sealed class MarkPage : LibPage
{
    // Sort orders of the Filter menu. All() already returns newest first.
    private static readonly string[] Sorts = { "Newest", "Oldest", "Title A-Z" };

    private readonly LibList _list = new();

    // Every bookmark, newest first.
    private List<Mark> _all = new();

    private string _q = string.Empty;
    private int _sort;

    // Counts the loads that were started; an answer for an older one is dropped.
    private int _ver;

    public MarkPage()
    {
        _list.Picked += r => Opened?.Invoke(r.Url);
        _list.Dropped += r => _ = Drop(r);
    }

    public override string Name
    {
        get { return "Bookmarks"; }
    }

    // FavoriteStar: the same glyph as the star in the address bar.
    public override string Glyph
    {
        get { return "\uE734"; }
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
        get { return "Search bookmarks"; }
    }

    public override async Task Load()
    {
        _q = string.Empty;
        _sort = 0;

        var ver = ++_ver;

        // Off the UI thread: Microsoft.Data.Sqlite runs its "async" calls synchronously.
        var all = await Task.Run(() => App.Marks.All());

        if (ver != _ver)
            return;

        _all = all;
        Rebuild();
    }

    public override void Search(string text)
    {
        _q = text;
        Rebuild();
    }

    public override void Park()
    {
        _ver++;
    }

    public override void Paint(Skin s)
    {
        _list.Paint(s);
    }

    public override MenuFlyout? Filter()
    {
        var menu = new MenuFlyout();

        for (var i = 0; i < Sorts.Length; i++)
        {
            var at = i;
            var item = new RadioMenuFlyoutItem
            {
                Text = Sorts[i],
                GroupName = "sort",
                IsChecked = i == _sort,
            };
            item.Click += (s, e) =>
            {
                _sort = at;
                Rebuild();
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    /// <summary>Fills the list from what is in memory, for the current search and sort.</summary>
    private void Rebuild()
    {
        IEnumerable<Mark> q = _all;

        if (_q.Length > 0)
        {
            q = q.Where(m => m.Title.Contains(_q, StringComparison.CurrentCultureIgnoreCase)
                          || m.Url.Contains(_q, StringComparison.CurrentCultureIgnoreCase));
        }

        if (_sort == 1)
            q = q.Reverse();
        else if (_sort == 2)
            q = q.OrderBy(Key, StringComparer.CurrentCultureIgnoreCase);

        var rows = new List<object>();
        foreach (var m in q)
            rows.Add(new LibRow(m.Url, m.Title, m.Icon));

        _list.Set(rows);
        Note();
    }

    /// <summary>Removes one bookmark. The rest of the list stays as it is.</summary>
    private async Task Drop(LibRow r)
    {
        try
        {
            await Task.Run(() => App.Marks.Remove(r.Url));
        }
        catch (Exception ex)
        {
            Log.Write("library", "bookmark remove failed: " + ex.Message);
            return;
        }

        _all.RemoveAll(m => m.Url == r.Url);
        _list.Items.Remove(r);

        Note();

        // The star in the address bar and the counters in the footer depend on this.
        Edited?.Invoke();
    }

    private void Note()
    {
        if (_list.Items.Count > 0)
            _list.Note = null;
        else
            _list.Note = _all.Count == 0 ? "No bookmarks found" : "No results";
    }

    /// <summary>What a bookmark is sorted by: its title, or its address when it has none.</summary>
    private static string Key(Mark m)
    {
        return string.IsNullOrWhiteSpace(m.Title) ? m.Url : m.Title;
    }
}
