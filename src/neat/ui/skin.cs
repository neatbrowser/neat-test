using Microsoft.UI.Xaml.Media;

namespace neat;

/// <summary>
/// The colours of a window that is not the browser window (settings now, other
/// dialogs later), worked out from the window colour (see <see cref="Tint"/>).
///
/// A Skin belongs to ONE window. Windows must not share a brush, so each window
/// makes its own Skin. Apply() changes the colours of the brushes in place, so
/// everything already built from them follows the colour without being rebuilt.
/// </summary>
internal sealed class Skin
{
    /// <summary>Window background: the window hue, very dark (dark mode) or very light (light mode).</summary>
    public SolidColorBrush Bg { get; } = new();

    /// <summary>Text.</summary>
    public SolidColorBrush Fg { get; } = new();

    /// <summary>Hints and values: text, but quieter.</summary>
    public SolidColorBrush Dim { get; } = new();

    /// <summary>Fill of a card: a faint wash of the text colour over the background.</summary>
    public SolidColorBrush Wash { get; } = new();

    /// <summary>1px separators between the rows of a card.</summary>
    public SolidColorBrush Line { get; } = new();

    /// <summary>Soft pill behind the selected tab.</summary>
    public SolidColorBrush Pill { get; } = new();

    /// <summary>
    /// The pill's colour and the pressed colour as solid colours (the system
    /// title bar buttons cannot be given see-through ones).
    /// </summary>
    public Windows.UI.Color Hover { get; private set; }
    public Windows.UI.Color Press { get; private set; }

    public bool Dark { get; private set; }

    /// <summary>Works the colours out from the window colour and puts them into the brushes.</summary>
    public void Apply(Tint t)
    {
        Dark = t.Dark;

        // Dark: the hue pushed far down in brightness but still clearly coloured.
        // Light: nearly neutral and very bright, so a pale tint, not a coloured page.
        var bg = Hsv(t.Hue, Dark ? 0.60 : 0.08, Dark ? 0.10 : 0.97);
        Bg.Color = Windows.UI.Color.FromArgb(255, bg.R, bg.G, bg.B);

        // Everything that sits on the background is the text colour at some
        // strength: white on dark, black on light. That keeps cards, lines
        // and the pill in step with the text whatever the hue is.
        byte ink = Dark ? (byte)255 : (byte)0;

        // The text itself is a shade off pure white / black, which is easier on the eye.
        byte txt = Dark ? (byte)0xF2 : (byte)0x14;

        Fg.Color = Windows.UI.Color.FromArgb(255, txt, txt, txt);
        Dim.Color = Windows.UI.Color.FromArgb(0x99, ink, ink, ink);
        Wash.Color = Windows.UI.Color.FromArgb(Dark ? (byte)15 : (byte)13, ink, ink, ink);   // about 6% / 5%
        Line.Color = Windows.UI.Color.FromArgb(20, ink, ink, ink);                            // about 8%
        Pill.Color = Windows.UI.Color.FromArgb(26, ink, ink, ink);                            // about 10%

        Hover = Over(bg, ink, 0.10);
        Press = Over(bg, ink, 0.16);
    }

    /// <summary>The colour you get by laying the text colour over the background at this strength.</summary>
    private static Windows.UI.Color Over((byte R, byte G, byte B) bg, byte ink, double a)
    {
        static byte Mix(byte under, byte over, double p) =>
            (byte)Math.Clamp(Math.Round(over * p + under * (1 - p)), 0, 255);

        return Windows.UI.Color.FromArgb(255, Mix(bg.R, ink, a), Mix(bg.G, ink, a), Mix(bg.B, ink, a));
    }

    /// <summary>Hue in degrees, saturation and value 0-1, to a colour.</summary>
    private static (byte R, byte G, byte B) Hsv(double hue, double s, double v)
    {
        var h = (((hue % 360) + 360) % 360) / 60;
        var i = (int)Math.Floor(h);
        var f = h - i;

        var p = v * (1 - s);
        var q = v * (1 - s * f);
        var u = v * (1 - s * (1 - f));

        var (r, g, b) = (i % 6) switch
        {
            0 => (v, u, p),
            1 => (q, v, p),
            2 => (p, v, u),
            3 => (p, q, v),
            4 => (u, p, v),
            _ => (v, p, q),
        };

        static byte Part(double d) => (byte)Math.Round(Math.Clamp(d, 0, 1) * 255);
        return (Part(r), Part(g), Part(b));
    }
}
