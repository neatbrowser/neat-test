using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace neat;

/// <summary>
/// The command bar: the box that opens on Ctrl+T (new tab) and Ctrl+L (edit this tab's
/// address). Under the box is a list of suggestions (open tabs, bookmarks, history and the
/// typed text, see data/suggest.cs). The up and down arrows move through the list while
/// the cursor stays in the box, so typing is never interrupted.
/// </summary>
public sealed partial class Win
{
    private readonly ObservableCollection<Hint> _hints = new();

    // Command bar mode: false = open in a new tab, true = edit this tab's address.
    private bool _edit;

    // Counts the lookups. A lookup that finds the count has moved on (more typing, or the bar
    // was closed) drops its result instead of showing it.
    private int _gen;

    // The bookmarks, read once each time the bar opens and searched in memory after that.
    private Task<List<Mark>> _marks = Task.FromResult(new List<Mark>());

    // The panel behind the list (win.xaml) is this dark colour and the text on it is white,
    // whatever the window colour is.
    private static readonly (byte R, byte G, byte B) PanelRgb = (0x14, 0x14, 0x14);
    private static readonly (byte R, byte G, byte B) InkRgb = (0xFF, 0xFF, 0xFF);

    /// <summary>
    /// Opens the command bar. Two modes: new tab (empty box, Enter opens the
    /// result in a new tab) or edit (the selected tab's address filled in and
    /// selected, Enter navigates that tab).
    /// </summary>
    private void ShowBar(bool edit)
    {
        _edit = edit;
        q.Text = edit ? (_cur?.Src ?? string.Empty) : string.Empty;
        scrim.Visibility = Visibility.Visible;

        BarPaint();
        hints.ItemsSource ??= _hints;
        _marks = Task.Run(() => App.Marks.All());
        _ = Suggestions();

        // The box only accepts focus once it is laid out.
        DispatcherQueue.TryEnqueue(() =>
        {
            q.Focus(FocusState.Programmatic);

            if (edit)
                q.SelectAll();
        });
    }

    /// <summary>
    /// Colours the picked row from the window's main colour (the first dot of the colour pad).
    /// Done each time the bar opens, which is enough: the scrim covers the window, so the colour
    /// cannot change while the bar is open. The brushes are the ones the list's own resources
    /// (win.xaml) give to its rows, so changing their colour in place restyles the rows.
    /// </summary>
    private void BarPaint()
    {
        var c = App.Look.Highlight(PanelRgb, InkRgb);

        // A little lighter while the pointer is over the picked row.
        var h = Wheel.Blend(InkRgb, (c.R, c.G, c.B), 0.12);

        BarBrush("ListViewItemBackgroundSelected", c);
        BarBrush("ListViewItemBackgroundSelectedPressed", c);
        BarBrush("ListViewItemBackgroundSelectedPointerOver", Windows.UI.Color.FromArgb(255, h.R, h.G, h.B));
    }

    private void BarBrush(string key, Windows.UI.Color c)
    {
        if (hints.Resources.TryGetValue(key, out var v) && v is SolidColorBrush b)
            b.Color = c;
    }

    private void HideBar()
    {
        _gen++;
        scrim.Visibility = Visibility.Collapsed;
        _hints.Clear();
        ShowList(false);
    }

    /// <summary>Fills the list for what the box holds now.</summary>
    private async Task Suggestions()
    {
        var gen = ++_gen;

        // In edit mode the box starts out holding the page's own address. That is not a
        // search yet, so the list stays as it is for an empty box until something is typed.
        var text = q.Text;
        var typed = _edit && text == _cur?.Src ? string.Empty : text.Trim();

        List<Mark> marks;
        List<Visit> visits;

        try
        {
            // A short pause, so fast typing does not ask the database after every key.
            if (typed.Length > 0)
                await Task.Delay(60);

            if (gen != _gen)
                return;

            marks = await _marks;
            visits = typed.Length == 0
                ? new List<Visit>()
                : await Task.Run(() => App.Hist.Find(Suggest.Term(typed), 30));
        }
        catch (Exception ex)
        {
            // Tabs and the typed text can still be listed without the database.
            Log.Error("cmd", ex, "suggestions failed");
            marks = new List<Mark>();
            visits = new List<Visit>();
        }

        if (gen != _gen)
            return;

        var rows = Suggest.Make(typed, _tabs, _edit ? _cur : null, marks, visits, App.Find);

        _hints.Clear();
        foreach (var r in rows)
            _hints.Add(r);

        ShowList(rows.Count > 0);
    }

    /// <summary>Shows or hides the list together with the thin line between it and the box.</summary>
    private void ShowList(bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        hints.Visibility = v;
        rule.Visibility = v;
    }

    /// <summary>
    /// Moves the pick up or down the list. Past either end it lands on "nothing picked",
    /// where Enter uses the typed text as it always did, and then goes round to the other end.
    /// </summary>
    private void Move(int step)
    {
        var n = _hints.Count;
        if (n == 0)
            return;

        var i = hints.SelectedIndex + step;
        if (i >= n)
            i = -1;
        else if (i < -1)
            i = n - 1;

        hints.SelectedIndex = i;

        if (i >= 0)
            hints.ScrollIntoView(hints.SelectedItem);
    }

    /// <summary>Does what a row says: switches to its tab, or opens its address.</summary>
    private void Go(Hint h)
    {
        if (h.Kind == HintKind.Tab)
        {
            HideBar();

            // The tab may have been closed since the list was made.
            if (h.Target is not null && _tabs.Contains(h.Target))
            {
                Pick(h.Target);
                h.Target.View.Focus(FocusState.Programmatic);
            }

            return;
        }

        Land(h.Kind == HintKind.Search ? App.Find.Resolve(h.Url) : h.Url);
    }

    /// <summary>Closes the bar and opens an address: in this tab (edit mode) or in a new one.</summary>
    private void Land(string? url)
    {
        var edit = _edit;
        var tab = _cur;
        HideBar();

        if (url is null)
            return;

        // Edit mode goes to the page of the selected tab, new-tab mode
        // opens a tab. A tab whose web view has not started yet cannot be
        // navigated, so it falls back to a new tab.
        var core = tab?.View.CoreWebView2;
        if (edit && tab is not null && core is not null)
        {
            core.Navigate(url);
            tab.View.Focus(FocusState.Programmatic);
        }
        else
        {
            _ = Open(url);
        }
    }

    private void newtab_Click(object sender, RoutedEventArgs e)
    {
        ShowBar(false);
    }

    private void scrim_Tapped(object sender, TappedRoutedEventArgs e)
    {
        HideBar();
    }

    private void panel_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // Keep taps on the panel from reaching the scrim and closing it.
        e.Handled = true;
    }

    private void q_TextChanged(object sender, TextChangedEventArgs e)
    {
        _ = Suggestions();
    }

    private void q_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HideBar();
        }
        else if (e.Key == VirtualKey.Down || e.Key == VirtualKey.Up)
        {
            Move(e.Key == VirtualKey.Down ? 1 : -1);

            // Without this the arrows would also move the cursor inside the box.
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            if (hints.SelectedItem is Hint h)
                Go(h);
            else
                Land(App.Find.Resolve(q.Text));
        }
    }

    private void hints_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Hint h)
            Go(h);
    }
}
