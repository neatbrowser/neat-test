using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace neat;

/// <summary>
/// The floating sidebar's background when the window is drawn over the system blur. The docked
/// sidebar has no panel: it shows the window's own layers (look/win.wash.cs: two round spots and a
/// straight fade, or two straight fades, all translucent over the blur). A floating sidebar must not
/// let the page show through, so it is an opaque panel, and the old way of colouring it (PeekBrush
/// in win.side.cs, one diagonal three-colour gradient) no longer looks like the window.
///
/// So the panel now carries the same layers itself, laid over an opaque base colour. They are not
/// worked out a second time: every number (where a gradient starts and ends, its colours, its
/// size) is copied from the brushes win.wash.cs has already set for the window, so the two cannot
/// drift apart when someone changes the look of the window. The copies are composition visuals in
/// window coordinates, moved to line up with where the panel sits, and cut to the panel's rounded
/// corners by one clip.
///
/// The desktop that shows through the blur cannot be copied, so the panel is close to the window's
/// look, not identical to it.
/// </summary>
public sealed partial class Win
{
    // The opaque colour under the layers: the base of the window colour (near-black or near-white).
    private readonly SolidColorBrush _under = new();

    // Made on first use. _swRoot is clipped to the panel's shape, _swWin holds the layers in window
    // coordinates. The four sprites are, bottom first: the two round spots, then the two straight layers.
    private ContainerVisual? _swRoot;
    private ContainerVisual? _swWin;
    private CompositionRoundedRectangleGeometry? _swCut;
    private readonly SpriteVisual[] _swVis = new SpriteVisual[4];
    private readonly CompositionRadialGradientBrush[] _swSpot = new CompositionRadialGradientBrush[2];
    private readonly CompositionLinearGradientBrush[] _swLine = new CompositionLinearGradientBrush[2];

    /// <summary>
    /// The floating panel's own background. Over the blur it is the opaque base (the layers go on
    /// top of it, see <see cref="SideWash"/>); without the blur the window is a plain gradient and
    /// PeekBrush matches it.
    /// </summary>
    private void SidePaint()
    {
        if (!Backdrop.Active)
        {
            side.Background = PeekBrush();
            return;
        }

        var under = Wheel.Base(App.Look.Cur.Dark);
        var c = Windows.UI.Color.FromArgb(255, under.R, under.G, under.B);

        // One dot: the window is one flat translucent colour, so the panel is that colour laid
        // over the base. With more dots the base stays as it is and the layers cover it.
        var layers = App.Look.Layers();
        if (layers.Length == 1)
            c = Over(under, layers[0]);

        _under.Color = c;
        side.Background = _under;
    }

    /// <summary>
    /// Shows the window's layers on the floating panel, or hides them. Called whenever something
    /// they depend on may have changed: the sidebar's state (Layout), the window colour (Paint), the
    /// window's size (WashPaint, after it has updated the window's own layers) and the panel's size.
    /// </summary>
    private void SideWash()
    {
        // Only a floating sidebar, over the blur, with the window drawn as layers.
        var on = !_dock && Layered;

        // This runs from layout and colour events: an exception leaving it would end the app.
        try
        {
            sidewash.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on)
                return;

            SideWashInit();

            // Pulled out over the panel's padding so it covers the whole panel (as sidegrain does).
            var pad = side.Padding;
            sidewash.Margin = new Thickness(-pad.Left, -pad.Top, -pad.Right, -pad.Bottom);

            // The rounded shape the layers are cut to: the panel's inner edge, one border in.
            var r = (float)Math.Max(0, _rs - side.BorderThickness.Left);
            _swCut!.Size = sidewash.ActualSize;
            _swCut.CornerRadius = new Vector2(r, r);

            // Where the panel's inner top-left corner is in the window (the same spot PeekBrush
            // uses, plus the one-pixel border). The layers are drawn in window coordinates, so they
            // are moved up and left by that much.
            var ox = side.Margin.Left + side.BorderThickness.Left;
            var oy = bar.ActualHeight + side.Margin.Top + side.BorderThickness.Top;
            _swWin!.Offset = new Vector3((float)-ox, (float)-oy, 0);

            SideWashCopy();
        }
        catch (Exception ex)
        {
            Log.Error("look", ex, "side wash failed");
        }
    }

    /// <summary>Copies the window's layers (set by WashPaint) onto the panel's sprites.</summary>
    private void SideWashCopy()
    {
        var size = new Vector2((float)root.ActualWidth, (float)root.ActualHeight);

        // The two round spots exist only with three dots, and sit under the straight layers.
        var spots = _spot is not null && _spotBrush is not null && glow.Visibility == Visibility.Visible;
        for (var i = 0; i < 2; i++)
        {
            _swVis[i].IsVisible = spots;
            if (!spots)
                continue;

            var src = _spotBrush![i];
            var dst = _swSpot[i];
            dst.EllipseCenter = src.EllipseCenter;
            dst.EllipseRadius = src.EllipseRadius;

            for (var k = 0; k < 3; k++)
            {
                dst.ColorStops[k].Color = src.ColorStops[k].Color;
                dst.ColorStops[k].Offset = src.ColorStops[k].Offset;
            }

            _swVis[i].Size = _spot![i].Size;
        }

        // The straight layers: two with two dots, only the second with three.
        for (var i = 0; i < 2; i++)
        {
            var src = _lin[i];

            // The window's layers are made in WashInit, which runs after the first Layout of a
            // sidebar that starts floating, so they may not exist yet.
            var show = _layer[i] is not null
                && _layer[i].Visibility == Visibility.Visible
                && src.GradientStops.Count == 3;

            _swVis[2 + i].IsVisible = show;
            if (!show)
                continue;

            var dst = _swLine[i];
            dst.StartPoint = new Vector2((float)src.StartPoint.X, (float)src.StartPoint.Y);
            dst.EndPoint = new Vector2((float)src.EndPoint.X, (float)src.EndPoint.Y);

            for (var k = 0; k < 3; k++)
            {
                dst.ColorStops[k].Color = src.GradientStops[k].Color;
                dst.ColorStops[k].Offset = (float)src.GradientStops[k].Offset;
            }

            _swVis[2 + i].Size = size;
        }
    }

    /// <summary>Builds the panel's layers the first time they are needed and hangs them on <c>sidewash</c>.</summary>
    private void SideWashInit()
    {
        if (_swRoot is not null)
            return;

        var compositor = ElementCompositionPreview.GetElementVisual(sidewash).Compositor;

        _swCut = compositor.CreateRoundedRectangleGeometry();
        _swRoot = compositor.CreateContainerVisual();
        _swRoot.Clip = compositor.CreateGeometricClip(_swCut);

        _swWin = compositor.CreateContainerVisual();
        _swRoot.Children.InsertAtTop(_swWin);

        for (var i = 0; i < 4; i++)
        {
            CompositionGradientBrush brush;

            if (i < 2)
            {
                var radial = compositor.CreateRadialGradientBrush();
                radial.MappingMode = CompositionMappingMode.Absolute;
                _swSpot[i] = radial;
                brush = radial;
            }
            else
            {
                var line = compositor.CreateLinearGradientBrush();
                line.MappingMode = CompositionMappingMode.Absolute;
                _swLine[i - 2] = line;
                brush = line;
            }

            for (var k = 0; k < 3; k++)
                brush.ColorStops.Add(compositor.CreateColorGradientStop());

            var visual = compositor.CreateSpriteVisual();
            visual.Brush = brush;
            visual.IsVisible = false;

            // Each one goes on top of the one before, the same order as in the window.
            _swWin.Children.InsertAtTop(visual);
            _swVis[i] = visual;
        }

        ElementCompositionPreview.SetElementChildVisual(sidewash, _swRoot);

        // The panel's size is only known after layout, and the clip needs it.
        sidewash.SizeChanged += (s, e) => SideWash();
    }

    /// <summary>A translucent colour laid over an opaque one, as a solid colour.</summary>
    private static Windows.UI.Color Over((byte R, byte G, byte B) under, Windows.UI.Color top)
    {
        var c = Wheel.Blend((top.R, top.G, top.B), under, top.A / 255.0);
        return Windows.UI.Color.FromArgb(255, c.R, c.G, c.B);
    }
}
