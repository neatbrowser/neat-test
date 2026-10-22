namespace neat;

/// <summary>
/// How the dots on the colour pad relate to each other, as in Zen Browser.
/// The first dot is the one you move; the others are worked out from it. How
/// many dots there are follows from the harmony (see <see cref="Harmonies.Count"/>),
/// so "one dot" is a harmony too.
///
/// These are saved in settings.json as numbers: add new ones at the end and
/// never reorder them.
/// </summary>
public enum Harmony
{
    /// <summary>Just the one dot.</summary>
    Floating = 0,

    /// <summary>Two dots, opposite each other.</summary>
    Complementary = 1,

    /// <summary>Two dots, one a little way round from the other.</summary>
    SingleAnalogous = 2,

    /// <summary>Three dots: the dot and the two beside the one opposite it.</summary>
    SplitComplementary = 3,

    /// <summary>Three dots, a little way round on both sides of the first.</summary>
    Analogous = 4,

    /// <summary>Three dots, evenly spread round the pad.</summary>
    Triadic = 5,
}

/// <summary>What each <see cref="Harmony"/> means, in angles round the pad.</summary>
public static class Harmonies
{
    /// <summary>Most dots the pad can hold.</summary>
    public const int Max = 3;

    // Angle of each other dot from the first, in degrees, in the order of the enum.
    private static readonly double[][] Offsets =
    {
        new double[] { },
        new[] { 180.0 },
        new[] { 310.0 },
        new[] { 150.0, 210.0 },
        new[] { 50.0, 310.0 },
        new[] { 120.0, 240.0 },
    };

    private static readonly string[] Names =
    {
        "Single",
        "Complementary",
        "Single analogous",
        "Split complementary",
        "Analogous",
        "Triadic",
    };

    /// <summary>How many dots a harmony has.</summary>
    public static int Count(Harmony h) => Offsets[(int)h].Length + 1;

    /// <summary>The name to show for a harmony.</summary>
    public static string Label(Harmony h) => Names[(int)h];

    /// <summary>Whether a value read from settings.json is one of the harmonies.</summary>
    public static bool Valid(Harmony h) => Enum.IsDefined(h);

    /// <summary>The harmony a pad gets when it has this many dots: the first one listed for that number.</summary>
    public static Harmony Default(int count)
    {
        count = Math.Clamp(count, 1, Max);

        for (var i = 0; i < Offsets.Length; i++)
            if (Offsets[i].Length + 1 == count)
                return (Harmony)i;

        return Harmony.Floating;
    }

    /// <summary>The next harmony with the same number of dots, going round (the same one if it is the only one).</summary>
    public static Harmony Next(Harmony h)
    {
        var count = Count(h);

        for (var step = 1; step <= Offsets.Length; step++)
        {
            var i = ((int)h + step) % Offsets.Length;
            if (Offsets[i].Length + 1 == count)
                return (Harmony)i;
        }

        return h;
    }

    /// <summary>
    /// Where every dot sits: the first at the given angle and radius, the
    /// others the same distance from the centre and turned by the harmony's
    /// angles.
    /// </summary>
    public static (double Angle, double Radius)[] Spots(double angle, double radius, Harmony h)
    {
        var offsets = Offsets[(int)h];
        var spots = new (double Angle, double Radius)[offsets.Length + 1];

        spots[0] = (angle, radius);
        for (var i = 0; i < offsets.Length; i++)
            spots[i + 1] = (((angle + offsets[i]) % 360 + 360) % 360, radius);

        return spots;
    }
}
