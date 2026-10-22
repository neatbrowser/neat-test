using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace neat;

/// <summary>
/// Window colour: paints the window from the current <see cref="Tint"/>. The
/// small menu at the foot of the sidebar that changes it is the
/// <see cref="Picker"/> control (picker.xaml); this file only opens and closes
/// the flyout that holds it.
/// </summary>
public sealed partial class Win
{
    private readonly SolidColorBrush _frameDark = new(Windows.UI.Color.FromArgb(255, 0x0F, 0x0F, 0x0F));
    private readonly SolidColorBrush _frameLight = new(Windows.UI.Color.FromArgb(255, 0xFA, 0xFA, 0xFA));

    private bool _fly;    // the colour menu is open

    private void PaintInit()
    {
        // While the menu is open the sidebar must not float away from under it.
        fly.Opened += (s, e) =>
        {
            _fly = true;
            picker.Sync();
        };
        fly.Closed += (s, e) =>
        {
            _fly = false;
            Leave();
        };

        WashInit();
        GrainInit();

        App.Look.Changed += Paint;
        Paint();
    }

    /// <summary>Applies the current colours to the window.</summary>
    private void Paint()
    {
        var t = App.Look.Cur;

        root.RequestedTheme = t.Dark ? ElementTheme.Dark : ElementTheme.Light;
        frame.Background = t.Dark ? _frameDark : _frameLight;

        // The window brush is shared, so changing its stops repaints everything that uses it.
        if (root.Background is LinearGradientBrush g && g.GradientStops.Count == 3)
        {
            // Layered (two or three dots over the blur): the wash layers draw the gradient
            // instead (look/win.wash.cs), so this brush is left clear. It stays in place
            // because the floating sidebar reads its direction and offsets (win.side.cs).
            var c = App.Look.Stops();
            var none = Windows.UI.Color.FromArgb(0, 0, 0, 0);
            for (var i = 0; i < 3; i++)
                g.GradientStops[i].Color = Layered ? none : c[i];
        }

        WashPaint();

        // The floating sidebar copies those colours, so it has to follow. Its layers copy the
        // ones WashPaint has just set, so this comes after it.
        if (!_dock)
            SidePaint();

        SideWash();

        GrainPaint();
    }
}
