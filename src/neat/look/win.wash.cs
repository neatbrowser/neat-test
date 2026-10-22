using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace neat;

/// <summary>
/// The window gradient as Zen Browser draws it over the system blur (see
/// getGradient in ZenGradientGenerator.mjs). A single three-colour brush paints
/// every spot of the window in some colour. Zen instead stacks layers that each
/// fade to clear, so between and behind them the blurred desktop shows through
/// untinted. That is most of why Zen looks glassy.
///
/// Two dots (colours a and b), as CSS lists them from the top layer down:
///   linear-gradient(150deg, a 30%, clear 120%)
///   linear-gradient(-30deg, b 30%, clear 120%)
/// Three dots (a, b and c):
///   linear-gradient(-5deg, c 10%, clear 80%)
///   radial-gradient(circle at 95% 0%, b 0%, clear 75%)
///   radial-gradient(circle at 0% 0%, a 10%, clear 70%)
/// One dot is one flat colour, painted by the window itself (see <see cref="Look.Stops"/>).
///
/// A brush cannot hold several layers, so the linear layers are rectangles in the
/// <c>wash</c> grid (win.xaml). The two radial layers are not XAML brushes: a
/// XAML RadialGradientBrush did not repaint when its colours were changed in place,
/// which left the top corners stuck. They are drawn with the composition layer
/// instead, whose colours change straight away, in sprite visuals hung on the
/// <c>glow</c> grid, which sits under <c>wash</c> (the linear layer of the third dot
/// has to be on top of them, as in Zen).
///
/// Only used when the blur is on (<see cref="Backdrop.Active"/>). Without it the
/// window keeps its solid gradient and both hosts stay hidden.
/// </summary>
public sealed partial class Win
{
    // One rectangle per linear layer, the bottom one first.
    private readonly Rectangle[] _layer = new Rectangle[2];

    // Brushes are made once and changed in place (a window must not share them).
    private readonly LinearGradientBrush[] _lin =
    {
        new() { MappingMode = BrushMappingMode.Absolute },
        new() { MappingMode = BrushMappingMode.Absolute },
    };

    // The two radial layers, bottom one first (top left, then top right). Made on first use.
    private SpriteVisual[]? _spot;
    private CompositionRadialGradientBrush[]? _spotBrush;

    /// <summary>True when the window gradient is drawn as layers: the blur is on and there are two or three dots.</summary>
    private bool Layered => Backdrop.Active && App.Look.Count > 1;

    private void WashInit()
    {
        for (var i = 0; i < _layer.Length; i++)
        {
            _layer[i] = new Rectangle { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            wash.Children.Add(_layer[i]);
        }

        // The gradients are laid out in pixels, so they follow the window's size.
        root.SizeChanged += (s, e) =>
        {
            if (Layered)
                WashPaint();
        };
    }

    /// <summary>Brings the layers up to date with the current colours and window size.</summary>
    private void WashPaint()
    {
        var on = Layered;
        wash.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on)
        {
            glow.Visibility = Visibility.Collapsed;
            return;
        }

        double w = root.ActualWidth, h = root.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        // This runs from layout and colour events: an exception leaving it would end the app.
        try
        {
            var c = App.Look.Layers();

            if (c.Length == 2)
            {
                glow.Visibility = Visibility.Collapsed;
                WashLine(0, _lin[0], c[1], w, h, -30, 1.2, 0.3);
                WashLine(1, _lin[1], c[0], w, h, 150, 1.2, 0.3);
            }
            else
            {
                _layer[0].Visibility = Visibility.Collapsed;
                WashLine(1, _lin[1], c[2], w, h, -5, 0.8, 0.1);

                WashSpotsInit();
                WashSpot(0, c[0], w, h, 0.00, 0.70, 0.10);
                WashSpot(1, c[1], w, h, 0.95, 0.75, 0.00);
                glow.Visibility = Visibility.Visible;
            }

            // The floating sidebar shows copies of these layers (look/win.sidewash.cs). Here, and
            // not in a handler of its own, so it always runs after they have been brought up to date.
            SideWash();
        }
        catch (Exception ex)
        {
            Log.Error("look", ex, "wash failed");
        }
    }

    /// <summary>
    /// Layer <paramref name="i"/> as CSS linear-gradient(<paramref name="deg"/>deg, c <paramref name="solid"/>,
    /// clear <paramref name="reach"/>) over a w by h box. The CSS gradient line runs through the middle of the
    /// box and is |w sin| + |h cos| long, so the corner farthest along it is exactly at 100%. WinUI offsets stop
    /// at 1, so the line is stretched to <paramref name="reach"/> and the stops are scaled to match.
    /// </summary>
    private void WashLine(int i, LinearGradientBrush b, Windows.UI.Color c, double w, double h,
        double deg, double reach, double solid)
    {
        var a = deg * Math.PI / 180;
        double dx = Math.Sin(a), dy = -Math.Cos(a);
        var len = Math.Abs(w * dx) + Math.Abs(h * dy);

        var sx = w / 2 - dx * len / 2;
        var sy = h / 2 - dy * len / 2;
        b.StartPoint = new Point(sx, sy);
        b.EndPoint = new Point(sx + dx * len * reach, sy + dy * len * reach);

        WashFade(b.GradientStops, c, solid / reach);
        _layer[i].Fill = b;
        _layer[i].Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The three stops of one linear layer: the colour up to <paramref name="solid"/>, then the same colour
    /// with no alpha at the end. Fading to the same colour (not to black) keeps the fade from going grey,
    /// the way a CSS gradient fades to "transparent".
    /// </summary>
    private static void WashFade(GradientStopCollection s, Windows.UI.Color c, double solid)
    {
        if (s.Count != 3)
        {
            s.Clear();
            for (var k = 0; k < 3; k++)
                s.Add(new GradientStop());
        }

        s[0].Color = c;
        s[0].Offset = 0;
        s[1].Color = c;
        s[1].Offset = solid;
        s[2].Color = Windows.UI.Color.FromArgb(0, c.R, c.G, c.B);
        s[2].Offset = 1;
    }

    /// <summary>Builds the two radial layers the first time they are needed and hangs them on <c>glow</c>.</summary>
    private void WashSpotsInit()
    {
        if (_spot is not null)
            return;

        var compositor = ElementCompositionPreview.GetElementVisual(glow).Compositor;
        var host = compositor.CreateContainerVisual();
        var visuals = new SpriteVisual[2];
        var brushes = new CompositionRadialGradientBrush[2];

        for (var i = 0; i < 2; i++)
        {
            var brush = compositor.CreateRadialGradientBrush();
            brush.MappingMode = CompositionMappingMode.Absolute;
            for (var k = 0; k < 3; k++)
                brush.ColorStops.Add(compositor.CreateColorGradientStop());

            var visual = compositor.CreateSpriteVisual();
            visual.Brush = brush;
            host.Children.InsertAtTop(visual);   // the second one ends up above the first

            visuals[i] = visual;
            brushes[i] = brush;
        }

        ElementCompositionPreview.SetElementChildVisual(glow, host);
        _spotBrush = brushes;
        _spot = visuals;
    }

    /// <summary>
    /// Radial layer <paramref name="i"/> as CSS radial-gradient(circle at x 0%, c <paramref name="solid"/>, clear
    /// <paramref name="reach"/>), where x is the share of the width and the circle is on the top edge. A CSS
    /// circle is as big as the distance to the farthest corner, and the percentages count from that.
    /// </summary>
    private void WashSpot(int i, Windows.UI.Color c, double w, double h, double x, double reach, double solid)
    {
        var cx = w * x;
        var far = 0.0;
        foreach (var kx in new[] { 0.0, w })
            foreach (var ky in new[] { 0.0, h })
                far = Math.Max(far, Math.Sqrt((cx - kx) * (cx - kx) + ky * ky));

        var r = (float)(far * reach);
        var b = _spotBrush![i];
        b.EllipseCenter = new Vector2((float)cx, 0);
        b.EllipseRadius = new Vector2(r, r);

        // Fade to the same colour with no alpha (not to black), as WashFade does.
        var s = b.ColorStops;
        s[0].Color = c;
        s[0].Offset = 0;
        s[1].Color = c;
        s[1].Offset = (float)(solid / reach);
        s[2].Color = Windows.UI.Color.FromArgb(0, c.R, c.G, c.B);
        s[2].Offset = 1;

        _spot![i].Size = new Vector2((float)w, (float)h);
    }
}
