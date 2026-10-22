namespace neat;

/// <summary>
/// The maths of the colour pad, kept free of any window code so it can be
/// tested on its own. The pad is a disc. A spot on it is an angle (which colour)
/// and a radius (how light), both measured from the centre:
///
///   angle   0-360 degrees, 0 pointing right, growing clockwise on screen
///   radius  0 at the centre, 1 on the rim
///
/// The colour is the one Zen Browser gives the same spot: the angle is the hue,
/// the radius is the lightness (the centre is black, the rim is white, the ring
/// halfway out is the purest colour), and the saturation stays near the top.
/// </summary>
public static class Wheel
{
    /// <summary>A spot on the pad as angle and radius. A point outside the disc lands on the rim.</summary>
    public static (double Angle, double Radius) FromPoint(double x, double y, double size)
    {
        var c = size / 2;
        double dx = x - c, dy = y - c;

        var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        if (angle < 0)
            angle += 360;

        var radius = c > 0 ? Math.Min(Math.Sqrt(dx * dx + dy * dy) / c, 1) : 0;
        return (angle, radius);
    }

    /// <summary>The point on a pad of this size (x across, y down) for an angle and radius.</summary>
    public static (double X, double Y) ToPoint(double angle, double radius, double size)
    {
        var c = size / 2;
        var a = angle * Math.PI / 180;
        return (c + c * radius * Math.Cos(a), c + c * radius * Math.Sin(a));
    }

    /// <summary>The colour at an angle and radius on the pad.</summary>
    public static (byte R, byte G, byte B) Rgb(double angle, double radius)
    {
        radius = Math.Clamp(radius, 0, 1);
        return Hsl(angle, 0.9 + 0.1 * radius, radius);
    }

    /// <summary>
    /// The colour the window is tinted over: near-black in a dark window,
    /// near-white in a light one. Pad colours are mixed into it (see
    /// <see cref="Blend"/>) so text stays readable whatever spot is picked.
    /// </summary>
    public static (byte R, byte G, byte B) Base(bool dark)
    {
        return dark ? ((byte)23, (byte)23, (byte)26) : ((byte)240, (byte)240, (byte)244);
    }

    /// <summary>
    /// Mixes two colours: <paramref name="amount"/> of <paramref name="top"/>
    /// over the rest of <paramref name="under"/>, 0 giving only the second.
    /// </summary>
    public static (byte R, byte G, byte B) Blend(
        (byte R, byte G, byte B) top, (byte R, byte G, byte B) under, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);

        static byte Mix(byte a, byte b, double p) =>
            (byte)Math.Clamp(Math.Round(a * p + b * (1 - p)), 0, 255);

        return (Mix(top.R, under.R, amount), Mix(top.G, under.G, amount), Mix(top.B, under.B, amount));
    }

    /// <summary>Hue in degrees, saturation and lightness 0-1, to a colour.</summary>
    public static (byte R, byte G, byte B) Hsl(double hue, double s, double l)
    {
        var h = (((hue % 360) + 360) % 360) / 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        double r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            var p = 2 * l - q;
            r = Channel(p, q, h + 1.0 / 3);
            g = Channel(p, q, h);
            b = Channel(p, q, h - 1.0 / 3);
        }

        static byte B(double d) => (byte)Math.Round(Math.Clamp(d, 0, 1) * 255);
        return (B(r), B(g), B(b));
    }

    private static double Channel(double p, double q, double t)
    {
        if (t < 0)
            t += 1;
        if (t > 1)
            t -= 1;

        if (t < 1.0 / 6)
            return p + (q - p) * 6 * t;
        if (t < 1.0 / 2)
            return q;
        if (t < 2.0 / 3)
            return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }
}
