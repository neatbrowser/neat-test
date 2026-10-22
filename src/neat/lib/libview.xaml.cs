using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace neat;

/// <summary>
/// The Library shell, shown inside the sidebar in place of the tab list: the rail of
/// sections, the search row with its Filter button, the back arrow, and the page of the
/// section on show. It knows nothing about the window around it; it tells it what
/// happened through three events, and win.lib.cs does the rest.
/// </summary>
public sealed partial class LibView : UserControl
{
    // How long typing must pause before the list is searched.
    private const int Hold = 250;

    private readonly Skin _skin = new();
    private readonly SolidColorBrush _none = new(Colors.Transparent);
    private readonly List<Button> _btns = new();
    private readonly LibPage[] _pages;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _wait;

    // The section on show, -1 when none is.
    private int _at = -1;

    // True while the search box is cleared by the code, so that is not taken for typing.
    private bool _mute;

    public LibView()
    {
        InitializeComponent();

        _pages = Pages();
        foreach (var p in _pages)
        {
            p.Opened = url => Opened?.Invoke(url);
            p.Edited = () => Edited?.Invoke();
        }

        BuildRail();

        _wait = DispatcherQueue.CreateTimer();
        _wait.Interval = TimeSpan.FromMilliseconds(Hold);
        _wait.IsRepeating = false;
        _wait.Tick += (s, e) =>
        {
            if (_at >= 0)
                _pages[_at].Search(search.Text.Trim());
        };

        // The Library lives as long as the window, so this is never taken off.
        App.Look.Changed += Paint;
        Paint();
    }

    /// <summary>The back arrow or Esc: the Library should close.</summary>
    public event Action? Back;

    /// <summary>A page was chosen: open this address in a new tab.</summary>
    public event Action<string>? Opened;

    /// <summary>A bookmark or history entry was removed: the star and the footer counters may need a refresh.</summary>
    public event Action? Edited;

    /// <summary>
    /// The sections, in the order of <see cref="Sec"/>, which is the order of the rail.
    /// To add a section, write its LibPage and put it here.
    /// </summary>
    private static LibPage[] Pages()
    {
        return new LibPage[]
        {
            new MediaPage(),
            new DlPage(),
            new MarkPage(),
            new HistPage(),
        };
    }

    /// <summary>Shows a section, loading it afresh. Showing the one already on show reloads it.</summary>
    public void Show(Sec sec)
    {
        _ = Enter((int)sec);
    }

    /// <summary>The Library closes: forget searches and loads that are still on their way.</summary>
    public void Park()
    {
        _wait.Stop();

        foreach (var p in _pages)
            p.Park();

        _at = -1;
    }

    private void BuildRail()
    {
        for (var i = 0; i < _pages.Length; i++)
        {
            var at = i;

            // Icon over label. No FontFamily: the default picks Segoe Fluent Icons, or
            // Segoe MDL2 Assets where that is missing. 20 is one of the sizes the font draws sharply.
            var col = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(new FontIcon { Glyph = _pages[i].Glyph, FontSize = 20 });
            col.Children.Add(new TextBlock
            {
                Text = _pages[i].Name,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var b = new Button
            {
                Width = 64,
                Height = 56,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(0),
                Background = _none,
                Content = col,
            };
            b.Click += (s, e) => Show((Sec)at);

            _btns.Add(b);
            rail.Children.Add(b);
        }
    }

    private async Task Enter(int i)
    {
        var page = _pages[i];

        // Whatever the section on show was still doing is of no interest any more.
        _wait.Stop();
        if (_at >= 0)
            _pages[_at].Park();
        _at = i;

        // The selected section sits in a soft rounded pill.
        for (var n = 0; n < _btns.Count; n++)
            _btns[n].Background = n == i ? _skin.Pill : _none;

        host.Child = page.View;

        // A new section starts with an empty search box.
        _mute = true;
        search.Text = string.Empty;
        _mute = false;

        top.Visibility = page.Searchable ? Visibility.Visible : Visibility.Collapsed;
        search.PlaceholderText = page.Hint;

        var menu = page.Filter();
        filt.Flyout = menu;
        filt.Visibility = menu is null ? Visibility.Collapsed : Visibility.Visible;

        if (page.Searchable)
            search.Focus(FocusState.Programmatic);

        try
        {
            await page.Load();
        }
        catch (Exception ex)
        {
            // A section that fails to load shows an empty list; it must not take the app down.
            Log.Write("library", page.Name + " failed to load: " + ex.Message);
        }
    }

    /// <summary>Applies the window colour to the pill and to every page.</summary>
    private void Paint()
    {
        _skin.Apply(App.Look.Cur);

        foreach (var p in _pages)
            p.Paint(_skin);
    }

    private void search_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_mute || _at < 0)
            return;

        // Restarting the timer on every key means the search runs once typing pauses.
        _wait.Stop();
        _wait.Start();
    }

    private void back_Click(object sender, RoutedEventArgs e)
    {
        Back?.Invoke();
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape)
            return;

        e.Handled = true;
        Back?.Invoke();
    }
}
