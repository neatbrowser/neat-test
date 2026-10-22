using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace neat;

/// <summary>
/// The History section: visits grouped by day (Today, Yesterday, then a date), newest
/// first, with repeats of the same page on the same day shown as one row. The list
/// is read from the database a page at a time as it is scrolled, so a long history does
/// not have to be loaded to be shown. Search (App.Hist.Find) and the time range filter
/// work on the same list.
/// </summary>
internal sealed class HistPage : LibPage
{
    // Visits read from the database at a time.
    private const int Step = 200;

    // A page of results can be all repeats, or partly outside the time range. Keep reading
    // until at least this many rows have been added or there is nothing more to read, so the
    // list never stalls with room left on screen.
    private const int Want = 20;

    // Name and age in days. Today (0) means since midnight; All (-1) is no limit.
    private static readonly (string Name, int Days)[] Ranges =
    {
        ("All", -1),
        ("Today", 0),
        ("Last 7 days", 7),
        ("Last 30 days", 30),
    };

    private readonly LibList _list = new();

    // Rows of the day being filled, by address. A visit of an address already in here
    // joins its row instead of making a new one.
    private readonly Dictionary<string, LibRow> _seen = new();

    private string _q = string.Empty;
    private int _range;

    // Counts the lists that were started. An answer from the database that belongs to an
    // older count (another search, another range, the section left) is dropped.
    private int _ver;

    // The count that is being read right now, so one list is not read twice at once.
    private int _loading = -1;

    // Visits of this list read so far, and whether the database has more.
    private int _skip;
    private bool _more;

    // The day the last heading was for.
    private DateTime? _day;

    public HistPage()
    {
        _list.Picked += r => Opened?.Invoke(r.Url);
        _list.Dropped += r => _ = Drop(r);
        _list.End += () => _ = Next();
    }

    public override string Name
    {
        get { return "History"; }
    }

    public override string Glyph
    {
        get { return "\uE81C"; }
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
        get { return "Search history"; }
    }

    public override Task Load()
    {
        _q = string.Empty;
        _range = 0;
        return Fresh();
    }

    public override void Search(string text)
    {
        _q = text;
        _ = Fresh();
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

        for (var i = 0; i < Ranges.Length; i++)
        {
            var at = i;
            var item = new RadioMenuFlyoutItem
            {
                Text = Ranges[i].Name,
                GroupName = "range",
                IsChecked = i == _range,
            };
            item.Click += (s, e) =>
            {
                _range = at;
                _ = Fresh();
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var clear = new MenuFlyoutItem { Text = "Clear all history..." };
        clear.Click += (s, e) => _ = Clear();
        menu.Items.Add(clear);

        return menu;
    }

    /// <summary>Starts the list over from the newest visit, for the current search and range.</summary>
    private Task Fresh()
    {
        _ver++;
        _skip = 0;
        _more = true;
        _day = null;
        _seen.Clear();

        _list.Set(Array.Empty<object>());
        _list.Note = null;

        return Next();
    }

    /// <summary>Reads the next page of visits and adds them to the list.</summary>
    private async Task Next()
    {
        if (!_more || _loading == _ver)
            return;

        var ver = _ver;
        _loading = ver;

        try
        {
            var cut = Cutoff();
            var added = 0;

            while (_more && added < Want)
            {
                var q = _q;
                var skip = _skip;

                // The database is read on a pool thread: Microsoft.Data.Sqlite runs its "async"
                // calls synchronously, which on the UI thread would stall the window.
                var got = q.Length == 0
                    ? await Task.Run(() => App.Hist.Recent(Step, skip))
                    : await Task.Run(() => App.Hist.Find(q, Step, skip));

                if (ver != _ver)
                    return;

                _skip += got.Count;
                if (got.Count < Step)
                    _more = false;

                added += Add(got, cut);
            }

            Note();
        }
        catch (Exception ex)
        {
            Log.Write("library", "history load failed: " + ex.Message);

            // Do not retry on every scroll: the list stays as it is.
            if (ver == _ver)
                _more = false;
        }
        finally
        {
            if (_loading == ver)
                _loading = -1;
        }
    }

    /// <summary>
    /// Adds visits to the list under day headings. Visits come newest first, so once one
    /// is older than the time range everything after it is too, and reading stops.
    /// Returns how many rows were added (repeats join an existing row, so it can be fewer than visits).
    /// </summary>
    private int Add(List<Visit> visits, DateTime? cut)
    {
        var rows = 0;

        foreach (var v in visits)
        {
            var at = Local(v.At);

            if (cut is not null && at < cut)
            {
                _more = false;
                break;
            }

            var day = at.Date;
            if (_day != day)
            {
                _day = day;
                _seen.Clear();
                _list.Items.Add(new LibHead(Heading(day)));
            }

            // The same page again on the same day: one row for all of them, the newest visit's.
            if (_seen.TryGetValue(v.Url, out var row))
            {
                row.Ids.Add(v.Id);
                continue;
            }

            row = new LibRow(v.Url, v.Title, v.Icon);
            row.Ids.Add(v.Id);
            _seen[v.Url] = row;
            _list.Items.Add(row);
            rows++;
        }

        return rows;
    }

    /// <summary>Removes one row, and with it every visit it stands for. The rest of the list stays as it is.</summary>
    private async Task Drop(LibRow r)
    {
        try
        {
            foreach (var id in r.Ids)
            {
                var one = id;
                await Task.Run(() => App.Hist.Delete(one));
            }
        }
        catch (Exception ex)
        {
            Log.Write("library", "history delete failed: " + ex.Message);
            return;
        }

        var i = _list.Items.IndexOf(r);
        if (i < 0)
            return;   // the list was reloaded while the database worked

        _list.Items.RemoveAt(i);

        // Those visits were read already; the next page starts that much earlier.
        _skip = Math.Max(0, _skip - r.Ids.Count);

        // The row may be the one a later visit of the same day would join.
        if (_seen.TryGetValue(r.Url, out var cur) && cur == r)
            _seen.Remove(r.Url);

        // A heading with nothing under it goes too.
        if (i > 0 && _list.Items[i - 1] is LibHead && (i == _list.Items.Count || _list.Items[i] is LibHead))
            _list.Items.RemoveAt(i - 1);

        Note();
        Edited?.Invoke();
    }

    /// <summary>Asks, then wipes the whole history.</summary>
    private async Task Clear()
    {
        try
        {
            var box = new ContentDialog
            {
                XamlRoot = _list.XamlRoot,
                Title = "Clear all history?",
                Content = "Every page in your history will be removed. Bookmarks are not affected.",
                PrimaryButtonText = "Clear",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                RequestedTheme = App.Look.Cur.Dark ? ElementTheme.Dark : ElementTheme.Light,
            };

            if (await box.ShowAsync() != ContentDialogResult.Primary)
                return;

            await Task.Run(() => App.Hist.Clear());
            await Fresh();
            Edited?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write("library", "history clear failed: " + ex.Message);
        }
    }

    /// <summary>Shows the "nothing here" line when the list is empty, and hides it otherwise.</summary>
    private void Note()
    {
        if (_list.Items.Count > 0)
            _list.Note = null;
        else
            _list.Note = _q.Length == 0 ? "No history found" : "No results";
    }

    /// <summary>The oldest time the chosen range reaches back to; null for no limit.</summary>
    private DateTime? Cutoff()
    {
        var days = Ranges[_range].Days;

        if (days < 0)
            return null;

        return days == 0 ? DateTime.Today : DateTime.Now.AddDays(-days);
    }

    /// <summary>
    /// The visit time as local time. The database gives back a time already converted to local;
    /// converting again would shift it, so only a time that says it is UTC is converted.
    /// </summary>
    private static DateTime Local(DateTime t)
    {
        return t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t;
    }

    private static string Heading(DateTime day)
    {
        var today = DateTime.Today;

        if (day == today)
            return "Today";

        if (day == today.AddDays(-1))
            return "Yesterday";

        // The invariant culture writes English names, which is what the rest of the interface speaks.
        var en = System.Globalization.CultureInfo.InvariantCulture;
        return day.ToString(day.Year == today.Year ? "dddd, MMMM d" : "dddd, MMMM d, yyyy", en);
    }
}
