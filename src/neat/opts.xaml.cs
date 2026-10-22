using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace neat;

/// <summary>
/// The settings window. One at a time (Win keeps the reference, see win.opts.cs),
/// opened with Ctrl+, or the gear in the sidebar footer.
///
/// Its title, "Settings", shows in the task bar and Alt+Tab; nothing is drawn in
/// the window. It has no minimize or maximize and cannot be resized: a thin
/// empty strip along the top (drag it to move the window), the system close
/// button, and under that a row of tabs, icon above label, like Arc's. The
/// pages are built in code from the blocks in ui/block.cs, one file each:
/// opts.gen.cs (General), opts.look.cs (Appearance), opts.pass.cs and
/// opts.pass.task.cs (Passwords), opts.keys.cs (Shortcuts) and opts.about.cs (About).
///
/// Its colours come from the same window colour as the browser window
/// (App.Look), through a Skin of its own, and follow it live.
/// </summary>
public sealed partial class Opts : Window
{
    // Window size in DIPs. AppWindow wants physical pixels, so this is scaled.
    private const int W = 760;
    private const int H = 640;

    // The icon file copied next to neat.exe by neat.csproj (the same file as the exe's own icon).
    private const string Ico = "neat.ico";

    // Tab name and its glyph. All five are in both Segoe Fluent Icons and Segoe MDL2 Assets
    // (Windows 10 only has the second): Setting, Color, Lock, KeyboardClassic and Info.
    // The Passwords page is the third one: PassAt in opts.pass.cs says so.
    private static readonly (string Name, string Glyph)[] Pages =
    {
        ("General", "\uE713"),
        ("Appearance", "\uE790"),
        ("Passwords", "\uE72E"),
        ("Shortcuts", "\uE765"),
        ("About", "\uE946"),
    };

    private readonly Win _own;
    private readonly Skin _skin = new();
    private readonly SolidColorBrush _none = new(Colors.Transparent);
    private readonly List<Button> _btns = new();

    // The page on show, -1 before the first one.
    private int _at = -1;

    public Opts(Win own)
    {
        InitializeComponent();
        _own = own;

        // The name for the task bar and Alt+Tab. It is not drawn in the window: the strip stays empty.
        Title = "Settings";

        // The same icon as neat.exe. If the file is missing the window keeps the default icon.
        var ico = Path.Combine(AppContext.BaseDirectory, Ico);
        if (File.Exists(ico))
            AppWindow.SetIcon(ico);

        // The content reaches the top edge; the empty strip is the drag area.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(strip);

        // Only the system close button should be left.
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMinimizable = false;
            p.IsMaximizable = false;
            p.IsResizable = false;
        }

        Place(own);

        // The skin's brush is changed in place by Paint, so it is set once.
        root.Background = _skin.Bg;
        BuildTabs();

        // The page is centred and has a width of its own, so it follows the window when that changes.
        scroll.SizeChanged += (s, e) => Fit();

        var esc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (s, e) =>
        {
            // With the engine list open, Esc is for the list; it must not take the window with it.
            if (_box is { IsDropDownOpen: true })
                return;

            e.Handled = true;
            Close();
        };
        root.KeyboardAccelerators.Add(esc);

        // The browser window closing must take this one with it, or the
        // app would keep running with only the settings window open.
        own.Closed += OnOwnClosed;
        Closed += OnClosed;

        App.Look.Changed += Paint;
        Paint();

        Pick(0);
    }

    /// <summary>
    /// Sizes the window and centres it over the browser window, kept on the
    /// screen the browser window is on.
    /// </summary>
    private void Place(Win own)
    {
        // Resize takes physical pixels. The scale of the browser window is used
        // because this window opens on the same screen.
        var k = own.Content?.XamlRoot?.RasterizationScale ?? 1.0;
        var w = (int)Math.Round(W * k);
        var h = (int)Math.Round(H * k);

        var o = own.AppWindow;
        var area = DisplayArea.GetFromWindowId(o.Id, DisplayAreaFallback.Nearest).WorkArea;

        var x = o.Position.X + (o.Size.Width - w) / 2;
        var y = o.Position.Y + (o.Size.Height - h) / 2;

        x = Math.Clamp(x, area.X, Math.Max(area.X, area.X + area.Width - w));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Y + area.Height - h));

        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    // ---- tabs ----

    private void BuildTabs()
    {
        for (var i = 0; i < Pages.Length; i++)
        {
            var at = i;

            var col = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };

            // No FontFamily: the default picks Segoe Fluent Icons, or Segoe MDL2 Assets where it is missing.
            col.Children.Add(new FontIcon { Glyph = Pages[i].Glyph, FontSize = 20, Foreground = _skin.Fg });
            col.Children.Add(new TextBlock
            {
                Text = Pages[i].Name,
                FontSize = 12,
                Foreground = _skin.Fg,
                HorizontalAlignment = HorizontalAlignment.Center,
            });

            var b = new Button
            {
                Width = 88,
                Height = 64,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(0),
                Background = _none,
                Content = col,
            };
            b.Click += (s, e) => Pick(at);

            _btns.Add(b);
            tabs.Children.Add(b);
        }
    }

    /// <summary>Shows a page and marks its tab.</summary>
    private void Pick(int i)
    {
        if (i == _at)
            return;

        // Whatever is typed in the page being left is kept before it goes.
        Flush();
        _home = null;
        _box = null;

        // Leaving the Passwords page ends the sign-in and shuts a password vault.
        if (_at == PassAt)
            LeavePass();

        // The colour menu of the page being left stops following the colour.
        _pk?.Detach();
        _pk = null;

        _at = i;
        for (var n = 0; n < _btns.Count; n++)
            _btns[n].Background = n == i ? _skin.Pill : _none;

        // Rebuilt each time, so a page always shows the current values (the
        // WebView2 version, for one, is not known until the first tab has loaded).
        scroll.Content = i switch
        {
            0 => BuildGen(),
            1 => BuildLook(),
            2 => BuildPass(),
            3 => BuildKeys(),
            4 => BuildAbout(),
            _ => Block.Page(),
        };
        Fit();
        scroll.ChangeView(0, 0, null, true);
    }

    /// <summary>Gives the page on show its width for the room the window leaves it.</summary>
    private void Fit()
    {
        // Before the first layout there is no width to go by; SizeChanged calls this again.
        if (scroll.Content is FrameworkElement page && scroll.ActualWidth > 0)
            Block.Fit(page, scroll.ActualWidth);
    }

    // ---- colour ----

    /// <summary>Applies the window colour: the skin, the theme and the system buttons.</summary>
    private void Paint()
    {
        var t = App.Look.Cur;
        _skin.Apply(t);

        // The controls on the pages (text boxes, drop-downs) follow this.
        root.RequestedTheme = t.Dark ? ElementTheme.Dark : ElementTheme.Light;

        if (!AppWindowTitleBar.IsCustomizationSupported())
            return;

        // The close button sits straight on the background and is told what to
        // look like, because on its own it follows the app's theme, not this window's.
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = _skin.Fg.Color;
        tb.ButtonInactiveForegroundColor = _skin.Dim.Color;
        tb.ButtonHoverBackgroundColor = _skin.Hover;
        tb.ButtonHoverForegroundColor = _skin.Fg.Color;
        tb.ButtonPressedBackgroundColor = _skin.Press;
        tb.ButtonPressedForegroundColor = _skin.Fg.Color;
    }

    // ---- closing ----

    private void OnOwnClosed(object sender, WindowEventArgs e)
    {
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs e)
    {
        // A closed window must stop repainting itself when the colour changes.
        App.Look.Changed -= Paint;
        _own.Closed -= OnOwnClosed;

        // Esc and the close button do not move focus first, so a half-typed
        // home page would be lost without this.
        Flush();

        // The sign-in ends and a password vault is shut, and a password copied a moment ago
        // comes off the clipboard: once this window is gone nothing else would do it.
        LeavePass();
        _clip?.ClearNow();

        // A closing window does not reliably tell its content it is unloaded, so the
        // colour menu is told to stop following the colour here.
        _pk?.Detach();
    }
}
