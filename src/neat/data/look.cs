namespace neat;

/// <summary>
/// The window colour. It keeps one hue plus how it is shaped (see
/// <see cref="Tint"/>) and turns that into the three colours of the window
/// gradient. Windows listen to <see cref="Changed"/> and repaint themselves, so
/// every window shows the same colours.
/// </summary>
public sealed class Look
{
    private readonly Store _cfg;

    public Look(Store cfg)
    {
        _cfg = cfg;
    }

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
    /// Top, middle and bottom colours of the window. The hue travels from the
    /// chosen one (top) round the colour wheel by up to 240 degrees (bottom),
    /// and the middle is almost grey, which keeps the transition soft.
    /// </summary>
    public Windows.UI.Color[] Stops()
    {
        var t = Cur;
        var span = 240 * t.Spread;

        var sat = Sat();
        var val = Val(t.Tone);
        var mid = t.Dark ? 0.65 : 0.97;

        return new[]
        {
            Hsv(t.Hue, sat, val),
            Hsv(t.Hue + span / 2, sat * 0.1, val * mid),
            Hsv(t.Hue + span, sat, val * 0.95),
        };
    }

    /// <summary>
    /// The window's top colour for a hue at the lightest tone. The colour square
    /// paints these across its width, then darkens it downwards by
    /// <see cref="Shade"/>, so every spot on it is exactly the top colour that
    /// spot would give.
    /// </summary>
    public Windows.UI.Color Lightest(double hue)
    {
        return Hsv(hue, Sat(), Val(1));
    }

    /// <summary>How much black covers the bottom (darkest) row of the colour square, 0 to 1.</summary>
    public double Shade => 1 - Val(0) / Val(1);

    private double Sat() => Cur.Dark ? 0.65 : 0.35;

    private double Val(double tone) => Cur.Dark ? 0.16 + 0.34 * tone : 0.78 + 0.19 * tone;

    /// <summary>A mid-strength colour for a ready-made hue, for the swatches.</summary>
    public static Windows.UI.Color Swatch(double hue)
    {
        return Hsv(hue, 0.65, 0.55);
    }

    /// <summary>Hue 0-360, saturation and value 0-1 to an opaque colour.</summary>
    private static Windows.UI.Color Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;

        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;

        var (r, g, b) = (h / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        static byte B(double d) => (byte)Math.Round(Math.Clamp(d, 0, 1) * 255);
        return Windows.UI.Color.FromArgb(255, B(r + m), B(g + m), B(b + m));
    }
}
