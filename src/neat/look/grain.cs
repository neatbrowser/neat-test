namespace neat;

/// <summary>
/// The texture ("grain") of the window colour, as in Zen Browser: a layer of
/// fine noise over the window's colour. How strong it is comes from a dial with
/// sixteen positions, 0 (no grain) to 15. Pure maths, like <see cref="Wheel"/>,
/// so it can be tested without a window.
///
/// Positions on the dial are counted clockwise from the top: step 0 points
/// up, step 4 points right, step 8 points down.
/// </summary>
public static class Grain
{
    /// <summary>Positions on the dial.</summary>
    public const int Steps = 16;

    /// <summary>The strongest texture (the dial's last position).</summary>
    public const int Max = Steps - 1;

    /// <summary>Width and height in pixels of the noise tile that is repeated over the window.</summary>
    public const int TileSize = 256;

    /// <summary>A step kept within the dial.</summary>
    public static int Clamp(int step) => Math.Clamp(step, 0, Max);

    /// <summary>How strongly the noise shows for a step: 0 (not at all) up to 15/16. This is Zen's rule too.</summary>
    public static double Opacity(int step) => Clamp(step) / (double)Steps;

    /// <summary>The angle of a step, in degrees clockwise from the top.</summary>
    public static double Angle(int step) => Clamp(step) * 360.0 / Steps;

    /// <summary>
    /// The step the pointer points at, on a dial of the given size. The angle
    /// from the middle is rounded to the nearest of the sixteen positions, so
    /// pointing just before the top counts as the top, and so does the top
    /// itself. A pointer right at the middle has no direction, so it keeps the
    /// step the dial already has.
    /// </summary>
    public static int FromPoint(double x, double y, double size, int current)
    {
        var c = size / 2;
        double dx = x - c, dy = y - c;

        if (dx * dx + dy * dy < 9)
            return Clamp(current);

        var angle = Math.Atan2(dy, dx) * 180 / Math.PI + 90;   // 0 at the top, clockwise
        angle = ((angle % 360) + 360) % 360;

        return (int)Math.Round(angle / 360 * Steps, MidpointRounding.AwayFromZero) % Steps;
    }

    /// <summary>The point on a ring of the given radius (inside a dial of the given size) where a step sits.</summary>
    public static (double X, double Y) OnRing(int step, double size, double ringRadius)
    {
        var a = Angle(step) * Math.PI / 180;
        var c = size / 2;
        return (c + ringRadius * Math.Sin(a), c - ringRadius * Math.Cos(a));
    }

    /// <summary>
    /// One tile of noise, <see cref="TileSize"/> squared pixels as bytes in the
    /// order blue, green, red, alpha, with the colour already multiplied by the
    /// alpha (which is how bitmaps in a window want it). Every pixel is a grey
    /// anywhere from black to white, but only faintly there: the alpha averages
    /// about 12% and never goes over 27%, which is about what Zen's grain image
    /// has. The same tile comes out every time, so the texture looks the same
    /// from one run to the next.
    /// </summary>
    public static byte[] Tile()
    {
        var pixels = new byte[TileSize * TileSize * 4];

        uint state = 0x2545F491;   // any fixed non-zero seed
        uint Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        // A number from 0 up to (not including) 1.
        double Unit() => (Next() >> 8) / 16777216.0;

        for (var i = 0; i < TileSize * TileSize; i++)
        {
            // Alpha: a bell curve around 32 (of 255), kept between 0 and 69.
            var bell = Math.Sqrt(-2 * Math.Log(1 - Unit())) * Math.Cos(2 * Math.PI * Unit());
            var alpha = (int)Math.Clamp(Math.Round(31.6 + 17.1 * bell), 0, 69);

            // Grey level: any of 0..255 equally likely.
            var grey = (int)(Unit() * 256);

            var v = (byte)((grey * alpha + 127) / 255);
            var o = i * 4;
            pixels[o] = v;
            pixels[o + 1] = v;
            pixels[o + 2] = v;
            pixels[o + 3] = (byte)alpha;
        }

        return pixels;
    }
}
