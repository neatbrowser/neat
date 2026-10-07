namespace neat;

/// <summary>
/// The window colour. It keeps the spot picked on the colour pad plus how the
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

    /// <summary>Raised after every change, so open windows can repaint.</summary>
    public event Action? Changed;

    public Tint Cur => _cfg.Cur.Tint;

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

        if (save)
            _cfg.Save();

        Changed?.Invoke();
    }

    /// <summary>Saves the current colours (after a series of Set calls that did not).</summary>
    public void Commit()
    {
        _cfg.Save();
    }

    /// <summary>
    /// Top, middle and bottom colours of the window. The top one is the colour
    /// of the picked spot; the hue then travels round the colour wheel by up to
    /// 240 degrees towards the bottom. Every colour is mixed into the window's
    /// base colour by the opacity, which keeps it soft and the text readable.
    /// </summary>
    public Windows.UI.Color[] Stops()
    {
        var t = Cur;
        var span = 240 * t.Spread;

        return new[]
        {
            Window(t.Hue),
            Window(t.Hue + span / 2),
            Window(t.Hue + span),
        };
    }

    /// <summary>The colour of the picked spot, as the dot on the pad shows it (not yet mixed into the base).</summary>
    public Windows.UI.Color Dot()
    {
        return Make(Wheel.Rgb(Cur.Hue, Cur.Tone));
    }

    /// <summary>A mid-strength colour for a ready-made hue, for the swatches.</summary>
    public static Windows.UI.Color Swatch(double hue)
    {
        return Make(Wheel.Rgb(hue, SwatchTone));
    }

    /// <summary>How far out on the pad a swatch sits: the ring where colours are purest.</summary>
    public const double SwatchTone = 0.5;

    private Windows.UI.Color Window(double hue)
    {
        var pad = Wheel.Rgb(hue, Cur.Tone);
        return Make(Wheel.Blend(pad, Wheel.Base(Cur.Dark), Cur.Opacity));
    }

    private static Windows.UI.Color Make((byte R, byte G, byte B) c)
    {
        return Windows.UI.Color.FromArgb(255, c.R, c.G, c.B);
    }
}
