using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace neat;

/// <summary>
/// The texture dial in the colour menu: a ring of sixteen dots with a bar that
/// turns round them. It sets how strongly the grain shows; the grain itself is
/// laid over the browser window in win.grain.cs. The maths is in
/// <see cref="Grain"/>.
/// </summary>
public sealed partial class Picker
{
    private const double DialSize = 80;
    private const double DialRing = 34;
    private const double TickSize = 4;

    private readonly List<Ellipse> _ticks = new();
    private readonly SolidColorBrush _tickInk = new();
    private readonly SolidColorBrush _knobInk = new();
    private readonly RotateTransform _knobTurn = new() { CenterX = 3, CenterY = 6 };
    private bool _dialDrag;

    private void DialInit()
    {
        for (var i = 0; i < Grain.Steps; i++)
        {
            var tick = new Ellipse { Width = TickSize, Height = TickSize, Fill = _tickInk };
            var (x, y) = Grain.OnRing(i, DialSize, DialRing);
            Canvas.SetLeft(tick, x - TickSize / 2);
            Canvas.SetTop(tick, y - TickSize / 2);

            ring.Children.Insert(0, tick);   // under the bar
            _ticks.Add(tick);
        }

        knob.Background = _knobInk;
        knob.RenderTransform = _knobTurn;
    }

    /// <summary>Brings the dial up to date: the dots up to the current step light up, the bar sits on the current step.</summary>
    private void DialPaint()
    {
        var t = App.Look.Cur;

        _tickInk.Color = t.Dark
            ? Windows.UI.Color.FromArgb(128, 255, 255, 255)
            : Windows.UI.Color.FromArgb(128, 0, 0, 0);
        _knobInk.Color = t.Dark
            ? Windows.UI.Color.FromArgb(255, 0xD1, 0xD1, 0xD1)
            : Windows.UI.Color.FromArgb(255, 0x75, 0x75, 0x75);

        for (var i = 0; i < _ticks.Count; i++)
            _ticks[i].Opacity = i <= t.Texture ? 1 : 0.4;

        var (x, y) = Grain.OnRing(t.Texture, DialSize, DialRing);
        Canvas.SetLeft(knob, x - knob.Width / 2);
        Canvas.SetTop(knob, y - knob.Height / 2);
        _knobTurn.Angle = Grain.Angle(t.Texture);
    }

    // ---- turning the dial ----

    private void dial_Down(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(dial);
        if (!p.Properties.IsLeftButtonPressed)
            return;

        _dialDrag = dial.CapturePointer(e.Pointer);
        Turn(p.Position);
    }

    private void dial_Move(object sender, PointerRoutedEventArgs e)
    {
        if (_dialDrag)
            Turn(e.GetCurrentPoint(dial).Position);
    }

    private void dial_Up(object sender, PointerRoutedEventArgs e)
    {
        EndTurn(e);
    }

    private void dial_Lost(object sender, PointerRoutedEventArgs e)
    {
        EndTurn(e);
    }

    /// <summary>Sets the texture to the step the pointer points at. Not saved on every step; EndTurn saves once.</summary>
    private void Turn(Windows.Foundation.Point p)
    {
        var step = Grain.FromPoint(p.X, p.Y, DialSize, App.Look.Cur.Texture);

        if (step != App.Look.Cur.Texture)
            App.Look.Set(t => t.Texture = step, save: false);
    }

    private void EndTurn(PointerRoutedEventArgs e)
    {
        if (!_dialDrag)
            return;

        _dialDrag = false;
        dial.ReleasePointerCapture(e.Pointer);
        App.Look.Commit();
    }
}
