using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace neat;

/// <summary>
/// Window colour: paints the window from the current <see cref="Tint"/>, and
/// the small menu at the foot of the sidebar that changes it.
/// </summary>
public sealed partial class Win
{
    // Ready-made hues for the swatches.
    private static readonly double[] Hues = { 270, 320, 0, 25, 45, 65, 130, 175, 220 };

    private readonly SolidColorBrush _frameDark = new(Windows.UI.Color.FromArgb(255, 0x0F, 0x0F, 0x0F));
    private readonly SolidColorBrush _frameLight = new(Windows.UI.Color.FromArgb(255, 0xFA, 0xFA, 0xFA));

    private bool _fly;    // the colour menu is open
    // The menu's controls are being filled in, so their events do nothing. It starts
    // true on purpose: while the window's XAML is built a slider can raise
    // ValueChanged by itself (Opacity has a minimum above its default value of 0, so
    // the slider moves its value up to the minimum), and that must not be saved as
    // if the user had set it. Fill() turns it off once the controls hold the real values.
    private bool _sync = true;

    private void PaintInit()
    {
        foreach (var h in Hues)
        {
            var b = new Button
            {
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(1),
                BorderBrush = _edge,
                Background = new SolidColorBrush(Look.Swatch(h)),
                Tag = h,
            };
            b.Click += sw_Click;
            sw.Children.Add(b);
        }

        // While the menu is open the sidebar must not float away from under it.
        fly.Opened += (s, e) =>
        {
            _fly = true;
            Fill();
            Square();
        };
        square.SizeChanged += (s, e) => Square();
        fly.Closed += (s, e) =>
        {
            _fly = false;
            Leave();
        };

        PadInit();
        GrainInit();

        App.Look.Changed += Paint;
        Paint();
    }

    /// <summary>Applies the current colours to the window and brings the menu's controls up to date.</summary>
    private void Paint()
    {
        var t = App.Look.Cur;

        root.RequestedTheme = t.Dark ? ElementTheme.Dark : ElementTheme.Light;
        frame.Background = t.Dark ? _frameDark : _frameLight;

        // The window brush is shared, so changing its stops repaints everything that uses it.
        if (root.Background is LinearGradientBrush g && g.GradientStops.Count == 3)
        {
            var c = App.Look.Stops();
            for (var i = 0; i < 3; i++)
                g.GradientStops[i].Color = c[i];
        }

        // The floating sidebar copies those colours, so it has to follow.
        if (!_dock)
            side.Background = PeekBrush();

        Fill();

        Square();
        GrainPaint();
    }

    /// <summary>Puts the saved values into the menu's controls without those controls reacting.</summary>
    private void Fill()
    {
        var t = App.Look.Cur;

        _sync = true;
        darksw.IsOn = t.Dark;
        spread.Value = t.Spread;
        opacity.Value = t.Opacity;
        _sync = false;
    }

    /// <summary>
    /// Whether a change in one of the menu's controls was made by the user: only
    /// while the menu is open, and not while the controls are being filled in.
    /// </summary>
    private bool Touched => _fly && !_sync;

    private void darksw_Toggled(object sender, RoutedEventArgs e)
    {
        if (Touched)
            App.Look.Set(t => t.Dark = darksw.IsOn);
    }

    private void spread_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Touched && Math.Abs(spread.Value - App.Look.Cur.Spread) > 0.0005)
            App.Look.Set(t => t.Spread = spread.Value);
    }

    private void opacity_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (Touched && Math.Abs(opacity.Value - App.Look.Cur.Opacity) > 0.0005)
            App.Look.Set(t => t.Opacity = opacity.Value);
    }

    private void sw_Click(object sender, RoutedEventArgs e)
    {
        // A swatch sits on the ring where colours are purest, so it sends the first dot there.
        if (sender is Button { Tag: double h })
            Animated(() => App.Look.Set(t =>
            {
                t.Hue = h;
                t.Tone = Look.SwatchTone;
            }));
    }
}
