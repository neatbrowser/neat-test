using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;

namespace neat;

/// <summary>
/// Rounds the corners of the page. The frame around the web view (win.xaml) has rounded corners
/// and a solid fill, but a Border does not cut its content to them: the web view is a plain
/// rectangle drawn on top, so its square corners stick out of the rounded fill. They were hard
/// to see while the window colour behind them was nearly as dark as the page, and showed as soon
/// as the colour got brighter (a higher opacity, a light window).
///
/// The cut is made in the composition layer, where a rounded clip can be put on any element's
/// visual, and it applies to everything drawn inside the frame, the web view included. The radius
/// is the frame's own CornerRadius (r_web in app.xaml), so there is one place to change it.
/// </summary>
public sealed partial class Win
{
    // The rounded shape the page is cut to. Made when the frame is first laid out.
    private CompositionRoundedRectangleGeometry? _cut;

    private void RoundInit()
    {
        // The shape follows the frame as the window or the sidebar changes its size.
        frame.SizeChanged += (s, e) => Round();
    }

    private void Round()
    {
        // This runs from layout: an exception leaving it would end the app.
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(frame);

            if (_cut is null)
            {
                _cut = visual.Compositor.CreateRoundedRectangleGeometry();
                visual.Clip = visual.Compositor.CreateGeometricClip(_cut);
            }

            var r = (float)frame.CornerRadius.TopLeft;
            _cut.Size = frame.ActualSize;
            _cut.CornerRadius = new Vector2(r, r);
        }
        catch (Exception ex)
        {
            Log.Error("look", ex, "page clip failed");
        }
    }
}
