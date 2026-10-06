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

    /// <summary>Changes the colour settings, saves them and tells the windows.</summary>
    public void Set(Action<Tint> edit)
    {
        edit(Cur);

        Cur.Hue = ((Cur.Hue % 360) + 360) % 360;
        Cur.Tone = Math.Clamp(Cur.Tone, 0, 1);
        Cur.Spread = Math.Clamp(Cur.Spread, 0, 1);

        _cfg.Save();
        Changed?.Invoke();
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

        var sat = t.Dark ? 0.65 : 0.35;
        var val = t.Dark ? 0.16 + 0.34 * t.Tone : 0.78 + 0.19 * t.Tone;
        var mid = t.Dark ? 0.65 : 0.97;

        return new[]
        {
            Hsv(t.Hue, sat, val),
            Hsv(t.Hue + span / 2, sat * 0.1, val * mid),
            Hsv(t.Hue + span, sat, val * 0.95),
        };
    }

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
