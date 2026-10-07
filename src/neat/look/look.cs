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
        Cur.Spread = Math.Clamp(Cur.Spread, 0, 1);
        Cur.Opacity = Math.Clamp(Cur.Opacity, MinOpacity, MaxOpacity);
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
    /// Top, middle and bottom colours of the window. Every colour is mixed into
    /// the window's base colour by the opacity, which keeps it soft and the text
    /// readable.
    ///
    /// One dot: its colour is at the top and the hue travels round the colour
    /// wheel by up to 240 degrees towards the bottom (the Gradient slider).
    /// Two dots: one at the top, the other at the bottom. Three dots: one each
    /// for top, middle and bottom.
    /// </summary>
    public Windows.UI.Color[] Stops()
    {
        var t = Cur;
        var spots = Spots();

        if (spots.Length == 1)
        {
            var span = 240 * t.Spread;
            return new[]
            {
                Window(spots[0].Angle, spots[0].Radius),
                Window(spots[0].Angle + span / 2, spots[0].Radius),
                Window(spots[0].Angle + span, spots[0].Radius),
            }.Select(Make).ToArray();
        }

        var a = Window(spots[0].Angle, spots[0].Radius);
        var b = Window(spots[1].Angle, spots[1].Radius);

        if (spots.Length == 2)
            return new[] { a, Wheel.Blend(a, b, 0.5), b }.Select(Make).ToArray();

        var c = Window(spots[2].Angle, spots[2].Radius);
        return new[] { a, b, c }.Select(Make).ToArray();
    }

    /// <summary>The colour of every dot as the pad shows it (not yet mixed into the base), first dot first.</summary>
    public Windows.UI.Color[] PadColors()
    {
        return Spots().Select(s => Make(Wheel.Rgb(s.Angle, s.Radius))).ToArray();
    }

    /// <summary>A mid-strength colour for a ready-made hue, for the swatches.</summary>
    public static Windows.UI.Color Swatch(double hue)
    {
        return Make(Wheel.Rgb(hue, SwatchTone));
    }

    private (byte R, byte G, byte B) Window(double angle, double radius)
    {
        return Wheel.Blend(Wheel.Rgb(angle, radius), Wheel.Base(Cur.Dark), Cur.Opacity);
    }

    private static Windows.UI.Color Make((byte R, byte G, byte B) c)
    {
        return Windows.UI.Color.FromArgb(255, c.R, c.G, c.B);
    }
}
