using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace neat;

/// <summary>
/// The Downloads section. The list is App.Dl.Items, the live collection of the download engine,
/// narrowed by the search and the filter. Progress needs no work here: the rows are bound to
/// the Download objects, which change by themselves. Only a download that starts, goes, or
/// changes which filter it belongs to touches the list.
/// </summary>
internal sealed class DlPage : LibPage
{
    // The Failed filter holds everything that ended without a file: failed, canceled and interrupted.
    private static readonly string[] Filters = { "All", "Downloading", "Completed", "Failed" };

    private readonly DlList _list = new();

    // What the list shows: the engine's list with the search and the filter applied.
    private readonly ObservableCollection<Download> _view = new();

    private string _q = string.Empty;
    private int _filter;

    public DlPage()
    {
        _list.Show(_view);
        _list.Picked += d => Try(() => Pick(d));
        _list.Asked += (d, at, pos) => Try(() => Ask(d, at, pos));

        foreach (var d in App.Dl.Items)
            d.PropertyChanged += OnItem;

        // The page follows the engine for as long as the app runs, whether it is on show or not.
        App.Dl.Items.CollectionChanged += OnItems;

        Rebuild();
    }

    public override string Name
    {
        get { return "Downloads"; }
    }

    public override string Glyph
    {
        get { return "\uE896"; }
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
        get { return "Search downloads"; }
    }

    public override Task Load()
    {
        _q = string.Empty;
        _filter = 0;
        Rebuild();
        return Task.CompletedTask;
    }

    public override void Search(string text)
    {
        _q = text;
        Rebuild();
    }

    public override void Paint(Skin s)
    {
        _list.Paint(s);
    }

    public override MenuFlyout? Filter()
    {
        var menu = new MenuFlyout();

        for (var i = 0; i < Filters.Length; i++)
        {
            var at = i;
            var item = new RadioMenuFlyoutItem
            {
                Text = Filters[i],
                GroupName = "dlfilter",
                IsChecked = i == _filter,
            };
            item.Click += (s, e) =>
            {
                _filter = at;
                Rebuild();
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var clear = new MenuFlyoutItem { Text = "Clear completed" };
        clear.Click += (s, e) => Try(() => App.Dl.ClearCompleted());
        menu.Items.Add(clear);

        return menu;
    }

    /// <summary>A click on a row opens the file if there is one.</summary>
    private void Pick(Download d)
    {
        if (d.State == DlState.Completed)
            _ = OpenIt(d);
    }

    /// <summary>The menu of a row. What it offers depends on where the download is.</summary>
    private void Ask(Download d, FrameworkElement at, Windows.Foundation.Point? pos)
    {
        var menu = new MenuFlyout();

        void Add(string text, Action act)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (s, e) => Try(act);
            menu.Items.Add(item);
        }

        var live = d.State is DlState.Downloading or DlState.Paused;

        if (d.State == DlState.Completed)
        {
            Add("Open", () => _ = OpenIt(d));
            Add("Show in folder", () => Reveal(d));
        }

        if (d.State == DlState.Downloading)
            Add("Pause", () => App.Dl.Pause(d));

        // A failed one can be picked up again only when WebView2 says it still can.
        if (d.State == DlState.Paused || (d.State == DlState.Failed && d.CanResume))
            Add("Resume", () => App.Dl.Resume(d));

        if (live)
            Add("Cancel", () => App.Dl.Cancel(d));

        // Retry is a new download from the same address: the tab that opens for it closes by
        // itself when the address turns out to be a download (see win.dl.cs).
        if (!live && d.State != DlState.Completed && d.CanRetry)
            Add("Retry", () => Opened?.Invoke(d.Url));

        if (!live)
            Add("Remove from list", () => App.Dl.Remove(d));

        if (pos is { } p)
            menu.ShowAt(at, p);
        else
            menu.ShowAt(at);
    }

    private async Task OpenIt(Download d)
    {
        var msg = await App.Dl.Open(d);

        if (msg is not null)
            await App.Dl.Flash(d, msg);
    }

    private void Reveal(Download d)
    {
        var msg = App.Dl.Reveal(d);

        if (msg is not null)
            _ = App.Dl.Flash(d, msg);
    }

    // ---- keeping the list in step with the engine ----

    private void OnItems(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Try(() =>
        {
            switch (e.Action)
            {
                // A download starts: it goes on top, and into the list if it fits the search and filter.
                case NotifyCollectionChangedAction.Add when e.NewStartingIndex == 0 && e.NewItems is not null:
                    foreach (Download d in e.NewItems)
                    {
                        d.PropertyChanged += OnItem;

                        if (Match(d))
                            _view.Insert(0, d);
                    }

                    break;

                case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                    foreach (Download d in e.OldItems)
                    {
                        d.PropertyChanged -= OnItem;
                        _view.Remove(d);
                    }

                    break;

                default:
                    // Anything else (a reset, a move): start over from the engine's list.
                    foreach (var d in App.Dl.Items)
                    {
                        d.PropertyChanged -= OnItem;
                        d.PropertyChanged += OnItem;
                    }

                    Rebuild();
                    return;
            }

            Note();
        });
    }

    /// <summary>A download changed state: with a filter on, it may now belong to another one.</summary>
    private void OnItem(object? sender, PropertyChangedEventArgs e)
    {
        if (_filter != 0 && e.PropertyName == nameof(Download.State))
            Try(Rebuild);
    }

    private void Rebuild()
    {
        _view.Clear();

        foreach (var d in App.Dl.Items)
        {
            if (Match(d))
                _view.Add(d);
        }

        Note();
    }

    private bool Match(Download d)
    {
        var ok = _filter switch
        {
            1 => d.State is DlState.Downloading or DlState.Paused,
            2 => d.State == DlState.Completed,
            3 => d.State is DlState.Failed or DlState.Canceled or DlState.Interrupted,
            _ => true,
        };

        return ok && (_q.Length == 0 || d.Name.Contains(_q, StringComparison.CurrentCultureIgnoreCase));
    }

    private void Note()
    {
        if (_view.Count > 0)
            _list.Note = null;
        else
            _list.Note = _q.Length > 0 ? "No results" : "No downloads found";
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
            Log.Error("library", ex, "downloads");
        }
    }
}
