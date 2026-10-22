using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace neat;

/// <summary>
/// The window colour menu: light or dark, the colour pad with its dots, how
/// much of the colour shows, the texture dial and ready-made hues. It is the
/// content of the sidebar's colour flyout (win.xaml) and of the Appearance
/// page in the settings window (opts.look.cs), so the two are always the same.
///
/// It follows the window colour (App.Look) on its own, but only while it is
/// on screen: it starts when it is loaded and stops when it is unloaded, so a
/// menu in a closed window is not left listening. The pad is in picker.pad.cs
/// and the dial in picker.dial.cs. How the colour is painted over a window is
/// not its business; that is win.paint.cs.
/// </summary>
public sealed partial class Picker : UserControl
{
    // Ready-made hues for the swatches.
    private static readonly double[] Hues = { 270, 320, 0, 25, 45, 65, 130, 175, 220 };

    // Edge of the swatches. Every menu has a brush of its own: windows must not share one.
    private readonly SolidColorBrush _edge = new(ColorHelper.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    // The menu's controls are being filled in, or the menu is off screen, so their events do
    // nothing. It starts true on purpose: while the XAML is built a slider can raise
    // ValueChanged by itself (Opacity has a minimum above its default value of 0, so
    // the slider moves its value up to the minimum), and that must not be saved as
    // if the user had set it. Fill() turns it off once the controls hold the real values.
    private bool _sync = true;

    public Picker()
    {
        InitializeComponent();

        foreach (var h in Hues)
        {
            var b = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(1),
                BorderBrush = _edge,
                Background = new SolidColorBrush(Look.Swatch(h)),
                Tag = h,
            };
            b.Click += sw_Click;
            sw.Children.Add(b);
        }

        square.SizeChanged += (s, e) => Square();

        // The pad takes two thirds of the menu's width: 200 in the sidebar flyout (300 wide),
        // more where the menu is given more room. Its dots follow through square.SizeChanged.
        SizeChanged += (s, e) =>
        {
            var d = Math.Round(e.NewSize.Width * 2 / 3);
            if (d >= 100 && d != square.Width)
            {
                square.Width = d;
                square.Height = d;
            }
        };

        PadInit();
        DialInit();

        Loaded += (s, e) => Sync();
        Unloaded += (s, e) => Detach();
    }

    /// <summary>
    /// Starts following the window colour and shows the current one. Safe to
    /// call again and again: the flyout calls it every time it opens.
    /// </summary>
    public void Sync()
    {
        // Taken off first, so calling this twice never leaves two subscriptions.
        App.Look.Changed -= Redraw;
        App.Look.Changed += Redraw;
        Redraw();
    }

    /// <summary>
    /// Stops following the colour. The window that holds the menu calls this when it
    /// closes, because a closing window does not reliably tell its content it is unloaded.
    /// </summary>
    public void Detach()
    {
        App.Look.Changed -= Redraw;

        // Nothing the controls raise from now on is the user's.
        _sync = true;
    }

    /// <summary>Brings the menu's controls up to date with the colour.</summary>
    private void Redraw()
    {
        Fill();
        Square();
        DialPaint();
    }

    /// <summary>Puts the saved values into the menu's controls without those controls reacting.</summary>
    private void Fill()
    {
        var t = App.Look.Cur;

        _sync = true;
        darksw.IsOn = t.Dark;
        opacity.Value = t.Opacity;
        _sync = false;
    }

    /// <summary>
    /// Whether a change in one of the menu's controls was made by the user: not
    /// while the controls are being filled in, and not while the menu is off screen.
    /// </summary>
    private bool Touched => !_sync;

    private void darksw_Toggled(object sender, RoutedEventArgs e)
    {
        if (Touched)
            App.Look.Set(t => t.Dark = darksw.IsOn);
    }

    private void opacity_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Touched && Math.Abs(opacity.Value - App.Look.Cur.Opacity) > 0.0005)
            App.Look.Set(t => t.Opacity = opacity.Value);
    }

    private void sw_Click(object sender, RoutedEventArgs e)
    {
        // A swatch sits on the ring where colours are purest, so it sends the first dot there.
        if (sender is Button { Tag: double h })
            Animated(() => App.Look.Set(t =>
            {
                t.Hue = h;
                t.Tone = Look.SwatchTone;
            }));
    }
}
