namespace neat;

/// <summary>
/// The window colour. It keeps the spots picked on the colour pad plus how the
/// colour is shaped (see <see cref="Tint"/>) and turns that into the three
/// colours of the window gradient. Windows listen to <see cref="Changed"/> and
/// repaint themselves, so every window shows the same colours.
/// </summary>
public sealed class Look
{
    private readonly Store _cfg;

    public Look(Store cfg)
    {
        _cfg = cfg;
    }

    /// <summary>Lowest and highest opacity the slider allows (the same as Zen's).</summary>
    public const double MinOpacity = 0.25;
    public const double MaxOpacity = 0.8;

    /// <summary>How far out on the pad a swatch sits: the ring where colours are purest.</summary>
    public const double SwatchTone = 0.5;

    /// <summary>
    /// The least contrast between the window's colours and its text (see
    /// <see cref="Contrast"/>), 4.5 being the usual minimum for normal text. A
    /// colour that would go below it is mixed in less, so the text stays readable.
    /// </summary>
    public const double MinContrast = 4.5;

    /// <summary>Raised after every change, so open windows can repaint.</summary>
    public event Action? Changed;

    public Tint Cur => _cfg.Cur.Tint;

    /// <summary>How many dots the pad has now.</summary>
    public int Count => Harmonies.Count(Cur.Harmony);

    /// <summary>
    /// Changes the colour settings and tells the windows. It also saves them,
    /// unless <paramref name="save"/> is false: while a dot is being dragged the
    /// colour changes dozens of times a second, so the save waits for
    /// <see cref="Commit"/> when the drag ends.
    /// </summary>
    public void Set(Action<Tint> edit, bool save = true)
    {
        edit(Cur);

        Cur.Hue = ((Cur.Hue % 360) + 360) % 360;
        Cur.Tone = Math.Clamp(Cur.Tone, 0, 1);
        Cur.Opacity = Math.Clamp(Cur.Opacity, MinOpacity, MaxOpacity);
        Cur.Texture = Grain.Clamp(Cur.Texture);
        if (!Harmonies.Valid(Cur.Harmony))
            Cur.Harmony = Harmony.Floating;

        if (save)
            _cfg.Save();

        Changed?.Invoke();
    }

    /// <summary>Saves the current colours (after a series of Set calls that did not).</summary>
    public void Commit()
    {
        _cfg.Save();
    }

    // ---- the dots ----

    /// <summary>Where every dot sits on the pad, first dot first.</summary>
    public (double Angle, double Radius)[] Spots()
    {
        return Harmonies.Spots(Cur.Hue, Cur.Tone, Cur.Harmony);
    }

    /// <summary>Adds a dot, if there is room: the pad gets the first harmony with one more dot.</summary>
    public void Add()
    {
        var count = Count;
        if (count >= Harmonies.Max)
            return;

        var next = Harmonies.Default(count + 1);
        Set(t => t.Harmony = next);
    }

    /// <summary>
    /// Removes a dot (0 is the first), if there is more than one. The pad gets
    /// the first harmony with one dot fewer. Removing the first dot makes the
    /// second one the new first, so the colour you can see does not jump to
    /// somewhere else on its own.
    /// </summary>
    public void Remove(int index)
    {
        var spots = Spots();
        if (spots.Length <= 1 || index < 0 || index >= spots.Length)
            return;

        var next = Harmonies.Default(spots.Length - 1);
        var first = index == 0 ? spots[1] : spots[0];

        Set(t =>
        {
            t.Hue = first.Angle;
            t.Tone = first.Radius;
            t.Harmony = next;
        });
    }

    /// <summary>Switches to the next harmony with the same number of dots.</summary>
    public void Cycle()
    {
        var next = Harmonies.Next(Cur.Harmony);
        Set(t => t.Harmony = next);
    }

    // ---- colours ----

    /// <summary>
    /// Top, middle and bottom colours of the window gradient.
    ///
    /// One dot: one flat colour, as in Zen. Two dots: one at the top, the other
    /// at the bottom. Three dots: one each for top, middle and bottom.
    ///
    /// With the system blur on (see <see cref="Backdrop"/>) the colours are
    /// translucent, so the blurred desktop shows through them, as in Zen. Without
    /// it they are solid, which is what <see cref="OpaqueStops"/> always gives.
    /// </summary>
    public Windows.UI.Color[] Stops()
    {
        return Backdrop.Active ? Shape(Glass) : OpaqueStops();
    }

    /// <summary>
    /// The same three colours as <see cref="Stops"/>, but always solid: every
    /// colour is mixed into the window's base colour by the opacity, which keeps
    /// it soft, and by no more than still leaves the text readable (see
    /// <see cref="MinContrast"/>). The floating sidebar uses these, because a
    /// panel over the page must not let the page show through.
    /// </summary>
    public Windows.UI.Color[] OpaqueStops()
    {
        return Shape(pad => Make(Window(pad)));
    }

    /// <summary>
    /// Lays out the three stops (see <see cref="Stops"/> for how the dots map to
    /// them) and gives each colour from the pad to <paramref name="paint"/>, which
    /// decides how it is turned into the colour the window shows.
    /// </summary>
    private Windows.UI.Color[] Shape(Func<(byte R, byte G, byte B), Windows.UI.Color> paint)
    {
        var spots = Spots();

        if (spots.Length == 1)
        {
            var one = paint(Wheel.Rgb(spots[0].Angle, spots[0].Radius));
            return new[] { one, one, one };
        }

        var a = Wheel.Rgb(spots[0].Angle, spots[0].Radius);
        var b = Wheel.Rgb(spots[1].Angle, spots[1].Radius);

        if (spots.Length == 2)
            return new[] { paint(a), paint(Wheel.Blend(a, b, 0.5)), paint(b) };

        var c = Wheel.Rgb(spots[2].Angle, spots[2].Radius);
        return new[] { paint(a), paint(b), paint(c) };
    }

    /// <summary>
    /// The colour of every dot as it shows over the system blur (translucent, see
    /// <see cref="Glass"/>), first dot first. Zen's layered gradient is drawn from
    /// these, one layer per dot (see look/win.wash.cs).
    /// </summary>
    public Windows.UI.Color[] Layers()
    {
        return Spots().Select(s => Glass(Wheel.Rgb(s.Angle, s.Radius))).ToArray();
    }

    /// <summary>The colour of every dot as the pad shows it (not yet mixed into the base), first dot first.</summary>
    public Windows.UI.Color[] PadColors()
    {
        return Spots().Select(s => Make(Wheel.Rgb(s.Angle, s.Radius))).ToArray();
    }

    /// <summary>How much of the first dot's colour the command bar's pick keeps; the rest is the panel it sits on.</summary>
    private const double HighlightShare = 0.6;

    /// <summary>
    /// The colour of the pick in the command bar's list: the first dot of the pad (the main
    /// colour), mixed into <paramref name="panel"/>, the colour the list sits on, but not so far
    /// that the text (<paramref name="ink"/>) falls below <see cref="MinContrast"/>.
    /// </summary>
    public Windows.UI.Color Highlight((byte R, byte G, byte B) panel, (byte R, byte G, byte B) ink)
    {
        var spot = Spots()[0];
        var pad = Wheel.Rgb(spot.Angle, spot.Radius);

        return Make(Contrast.Legible(pad, panel, HighlightShare, ink, MinContrast));
    }

    /// <summary>A mid-strength colour for a ready-made hue, for the swatches.</summary>
    public static Windows.UI.Color Swatch(double hue)
    {
        return Make(Wheel.Rgb(hue, SwatchTone));
    }

    /// <summary>
    /// A colour from the pad as it shows in the window: mixed into the base
    /// colour by the opacity, but not so far that the text (white on a dark
    /// window, black on a light one) gets hard to read.
    /// </summary>
    private (byte R, byte G, byte B) Window((byte R, byte G, byte B) pad)
    {
        var dark = Cur.Dark;
        var ink = dark ? ((byte)255, (byte)255, (byte)255) : ((byte)0, (byte)0, (byte)0);

        return Contrast.Legible(pad, Wheel.Base(dark), Cur.Opacity, ink, MinContrast);
    }

    /// <summary>How much of the base layer (black or white) is added under a colour over the blur.</summary>
    private const double GlassOverlay = 0.3;

    /// <summary>
    /// A colour from the pad as it shows over the system blur. This is Zen's rule
    /// for Windows (blendWithWhiteOverlay in ZenGradientGenerator.mjs): the colour
    /// is first mixed with black (dark window) or white (light window), then given
    /// the opacity as its alpha. The mixed-in layer is what keeps the text readable
    /// over a bright or busy desktop; there is no contrast clamp here, because
    /// what is behind a translucent colour is not known.
    /// </summary>
    private Windows.UI.Color Glass((byte R, byte G, byte B) pad)
    {
        var o = Cur.Opacity;
        var layer = Cur.Dark ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255);

        // The share of the pad colour that is kept. It never drops below the opacity.
        var keep = Math.Min(1, o + MinOpacity + GlassOverlay * (1 - (o + MinOpacity)));
        var c = Wheel.Blend(pad, layer, keep);

        return Windows.UI.Color.FromArgb((byte)Math.Round(o * 255), c.R, c.G, c.B);
    }

    private static Windows.UI.Color Make((byte R, byte G, byte B) c)
    {
        return Windows.UI.Color.FromArgb(255, c.R, c.G, c.B);
    }
}
