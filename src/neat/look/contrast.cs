namespace neat;

/// <summary>
/// How well text shows against a colour, so a bright or dark spot on the colour
/// pad never makes the window unreadable. Pure maths, like <see cref="Wheel"/>.
/// The numbers follow the web accessibility guidelines (WCAG): a contrast
/// ratio runs from 1 (no difference) to 21 (black on white), and 4.5 is the
/// minimum for normal-size text.
/// </summary>
public static class Contrast
{
    /// <summary>How bright a colour looks, 0 (black) to 1 (white), counting green most and blue least.</summary>
    public static double Luminance((byte R, byte G, byte B) c)
    {
        static double Linear(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    /// <summary>The contrast ratio of two colours, 1 to 21. Either one can be the lighter.</summary>
    public static double Ratio((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        var x = Luminance(a) + 0.05;
        var y = Luminance(b) + 0.05;
        return x > y ? x / y : y / x;
    }

    /// <summary>
    /// Mixes <paramref name="top"/> over <paramref name="under"/> by
    /// <paramref name="amount"/> (see <see cref="Wheel.Blend"/>), but never so
    /// far that <paramref name="ink"/> (the text colour) falls below the
    /// contrast <paramref name="min"/> against the result. If the full amount
    /// would, it uses the most that still keeps the text readable.
    /// </summary>
    public static (byte R, byte G, byte B) Legible(
        (byte R, byte G, byte B) top, (byte R, byte G, byte B) under, double amount,
        (byte R, byte G, byte B) ink, double min)
    {
        var full = Wheel.Blend(top, under, amount);
        if (Ratio(full, ink) >= min)
            return full;

        // Mixing in less moves the colour towards the base, which is made to suit
        // the text. Find the most that still works: lo always works, hi never does.
        double lo = 0, hi = amount;
        for (var i = 0; i < 24; i++)
        {
            var mid = (lo + hi) / 2;
            if (Ratio(Wheel.Blend(top, under, mid), ink) >= min)
                lo = mid;
            else
                hi = mid;
        }

        return Wheel.Blend(top, under, lo);
    }
}
