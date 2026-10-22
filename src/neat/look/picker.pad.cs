using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;

namespace neat;

/// <summary>
/// The colour pad in the colour menu. The pad is a disc: the angle of a dot
/// round the centre is its colour, its distance from the centre is how light
/// the colour is (dark in the middle, light at the rim).
///
/// The big dot is the one you move: drag it, or click anywhere to send it
/// there. The small dots follow it, each placed by the harmony (see
/// <see cref="Harmonies"/>); they cannot be moved by themselves. The + and -
/// buttons add and remove dots, the middle button changes the harmony, and a
/// right click on a dot removes it. The maths is in <see cref="Wheel"/>.
/// </summary>
public sealed partial class Picker
{
    // A press this close (in pixels) to a dot picks that dot; further away it sends the first dot to the press.
    private const double Grab = 16;

    private const double BigDot = 18;
    private const double SmallDot = 14;

    /// <summary>
    /// A dot on the pad. It is moved by changing its place at once and then
    /// easing <see cref="Glide"/> back to zero, which makes it glide from where
    /// it was to where it is now.
    /// </summary>
    private sealed class PadDot
    {
        public PadDot(Ellipse shape)
        {
            Shape = shape;
            Shape.Fill = Fill;
            Shape.RenderTransform = Glide;
        }

        public Ellipse Shape { get; }
        public SolidColorBrush Fill { get; } = new();
        public TranslateTransform Glide { get; } = new();
        public Storyboard? Sb { get; set; }
    }

    // The first dot is the one drawn in the menu's XAML; the rest are made as needed.
    private readonly List<PadDot> _dots = new();

    private bool _drag;

    private void PadInit()
    {
        _dots.Add(new PadDot(dot));
    }

    /// <summary>The pad's width, which is also its height. Zero until the menu has been shown.</summary>
    private double PadSize() => Math.Min(square.ActualWidth, square.ActualHeight);

    /// <summary>
    /// Brings the pad up to date: the right number of dots, coloured and put
    /// where the colours are, and the buttons under the pad. Moves nothing
    /// slowly; <see cref="Animated"/> adds the gliding.
    /// </summary>
    private void Square()
    {
        var spots = App.Look.Spots();
        var colors = App.Look.PadColors();

        while (_dots.Count < spots.Length)
            AddDot();
        while (_dots.Count > spots.Length)
            RemoveDot();

        // Before the menu has been shown the pad has no size yet; the dots are
        // placed again when it gets one.
        var size = PadSize();

        for (var i = 0; i < spots.Length; i++)
        {
            _dots[i].Fill.Color = colors[i];

            if (size > 0)
            {
                var (x, y) = Wheel.ToPoint(spots[i].Angle, spots[i].Radius, size);
                Canvas.SetLeft(_dots[i].Shape, x - _dots[i].Shape.Width / 2);
                Canvas.SetTop(_dots[i].Shape, y - _dots[i].Shape.Height / 2);
            }
        }

        var count = spots.Length;
        plus.IsEnabled = count < Harmonies.Max;
        minus.IsEnabled = count > 1;
        harm.IsEnabled = Harmonies.Next(App.Look.Cur.Harmony) != App.Look.Cur.Harmony;
        harm.Content = Harmonies.Label(App.Look.Cur.Harmony);
    }

    private void AddDot()
    {
        var shape = new Ellipse
        {
            Width = SmallDot,
            Height = SmallDot,
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 2,
        };

        dots.Children.Insert(0, shape);   // behind the big dot, so that one stays on top
        _dots.Add(new PadDot(shape));
    }

    private void RemoveDot()
    {
        var last = _dots[^1];
        last.Sb?.Stop();
        dots.Children.Remove(last.Shape);
        _dots.RemoveAt(_dots.Count - 1);
    }

    // ---- the buttons ----

    private void plus_Click(object sender, RoutedEventArgs e)
    {
        Animated(() => App.Look.Add());
    }

    private void minus_Click(object sender, RoutedEventArgs e)
    {
        Animated(() => App.Look.Remove(App.Look.Count - 1));
    }

    private void harm_Click(object sender, RoutedEventArgs e)
    {
        Animated(() => App.Look.Cycle());
    }

    // ---- pressing and dragging on the pad ----

    private void square_Down(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(square);
        if (PadSize() <= 0)
            return;

        var hit = DotAt(p.Position);

        // A right click on a dot removes it.
        if (p.Properties.IsRightButtonPressed)
        {
            if (hit >= 0)
                Animated(() => App.Look.Remove(hit));
            return;
        }

        if (!p.Properties.IsLeftButtonPressed)
            return;

        if (hit == 0)
        {
            StopGlides();
            _drag = square.CapturePointer(e.Pointer);
            Pick(p.Position);
        }
        else if (hit < 0)
        {
            Jump(p.Position);
        }

        // hit > 0: the small dots follow the big one and are not moved by hand, so a press on one does nothing.
    }

    private void square_Move(object sender, PointerRoutedEventArgs e)
    {
        if (_drag)
            Pick(e.GetCurrentPoint(square).Position);
    }

    private void square_Up(object sender, PointerRoutedEventArgs e)
    {
        End(e);
    }

    private void square_Lost(object sender, PointerRoutedEventArgs e)
    {
        End(e);
    }

    /// <summary>
    /// The dot under a point (0 is the big one), or -1 if there is none. When
    /// dots overlap, as they do near the centre, the first one wins.
    /// </summary>
    private int DotAt(Windows.Foundation.Point p)
    {
        var best = -1;
        var bestDistance = double.MaxValue;

        for (var i = 0; i < _dots.Count; i++)
        {
            var (x, y) = Where(_dots[i]);
            var d = Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));

            if (d <= Grab && d < bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }

        return best;
    }

    /// <summary>
    /// Sets the colour to the spot under the pointer. A point outside the disc
    /// counts as the nearest point on its rim. Not saved on every movement;
    /// End() saves once when the drag is over.
    /// </summary>
    private void Pick(Windows.Foundation.Point p)
    {
        var (angle, radius) = Wheel.FromPoint(p.X, p.Y, PadSize());

        App.Look.Set(t =>
        {
            t.Hue = angle;
            t.Tone = radius;
        }, save: false);
    }

    /// <summary>A click away from the dots: the colour changes at once, and the dots glide to their new places.</summary>
    private void Jump(Windows.Foundation.Point p)
    {
        Animated(() => Pick(p));
        App.Look.Commit();
    }

    private void End(PointerRoutedEventArgs e)
    {
        if (!_drag)
            return;

        _drag = false;
        square.ReleasePointerCapture(e.Pointer);
        App.Look.Commit();
    }

    // ---- the glide ----

    /// <summary>The middle of a dot on the pad, where it is now (ignoring any glide in progress).</summary>
    private static (double X, double Y) Where(PadDot d)
    {
        return (Canvas.GetLeft(d.Shape) + d.Shape.Width / 2, Canvas.GetTop(d.Shape) + d.Shape.Height / 2);
    }

    /// <summary>
    /// Runs a change that moves, adds or removes dots, and makes the dots glide
    /// from where they were to where they end up, with a small overshoot. A dot
    /// that is new starts from the centre of the pad.
    /// </summary>
    private void Animated(Action change)
    {
        StopGlides();
        var before = _dots.Select(Where).ToList();

        change();   // Square() runs through the Changed event and puts the dots in place

        var centre = PadSize() / 2;
        for (var i = 0; i < _dots.Count; i++)
        {
            var (fromX, fromY) = i < before.Count ? before[i] : (centre, centre);
            var (x, y) = Where(_dots[i]);
            Glide(_dots[i], fromX - x, fromY - y);
        }
    }

    private void Glide(PadDot d, double dx, double dy)
    {
        if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5)
            return;

        d.Glide.X = dx;
        d.Glide.Y = dy;

        var sb = new Storyboard();
        Ease(sb, d.Glide, "X", dx);
        Ease(sb, d.Glide, "Y", dy);

        d.Sb = sb;
        sb.Begin();
    }

    private static void Ease(Storyboard sb, TranslateTransform target, string property, double from)
    {
        var a = new DoubleAnimation
        {
            From = from,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(400)),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 },
        };

        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, property);
        sb.Children.Add(a);
    }

    private void StopGlides()
    {
        foreach (var d in _dots)
        {
            d.Sb?.Stop();
            d.Sb = null;
            d.Glide.X = 0;
            d.Glide.Y = 0;
        }
    }
}
