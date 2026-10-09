using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace neat;

/// <summary>
/// The texture of the window colour, as in Zen Browser. A layer of fine noise
/// lies over the window's colour, under the title bar and the sidebar, and
/// the dial in the colour menu sets how strongly it shows. The page area is not
/// affected: it is covered by the web view anyway. The maths is in
/// <see cref="Grain"/>.
///
/// The noise is one small tile made when it is first needed and repeated over
/// the window as many times as it takes (a window has no way to repeat an image
/// by itself).
/// </summary>
public sealed partial class Win
{
    // The dial in the colour menu: a ring of sixteen dots with a bar that turns round them.
    private const double DialSize = 80;
    private const double DialRing = 34;
    private const double TickSize = 4;

    private readonly List<Ellipse> _ticks = new();
    private readonly SolidColorBrush _tickInk = new();
    private readonly SolidColorBrush _knobInk = new();
    private readonly RotateTransform _knobTurn = new() { CenterX = 3, CenterY = 6 };

    private byte[]? _tileBytes;        // the noise tile's pixels, made once
    private WriteableBitmap? _tile;     // the tile as a bitmap, for the window's layer
    private int _sideRows;              // how many tiles tall the floating sidebar's strip is (0: none yet)
    private bool _dialDrag;

    private void GrainInit()
    {
        for (var i = 0; i < Grain.Steps; i++)
        {
            var tick = new Ellipse { Width = TickSize, Height = TickSize, Fill = _tickInk };
            var (x, y) = Grain.OnRing(i, DialSize, DialRing);
            Canvas.SetLeft(tick, x - TickSize / 2);
            Canvas.SetTop(tick, y - TickSize / 2);

            ring.Children.Insert(0, tick);   // under the bar
            _ticks.Add(tick);
        }

        knob.Background = _knobInk;
        knob.RenderTransform = _knobTurn;

        // The tiles have to cover the window, so they follow its size.
        root.SizeChanged += (s, e) =>
        {
            if (App.Look.Cur.Texture > 0)
            {
                Tiles();
                SideGrain();
            }
        };
    }

    /// <summary>Brings the grain layer and the dial up to date with the current texture.</summary>
    private void GrainPaint()
    {
        var t = App.Look.Cur;
        var on = t.Texture > 0;

        grain.Opacity = Grain.Opacity(t.Texture);
        grain.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on)
            Tiles();
        SideGrain();

        // Dial: the dots up to the current step light up, the bar sits on the current step.
        _tickInk.Color = t.Dark
            ? Windows.UI.Color.FromArgb(128, 255, 255, 255)
            : Windows.UI.Color.FromArgb(128, 0, 0, 0);
        _knobInk.Color = t.Dark
            ? Windows.UI.Color.FromArgb(255, 0xD1, 0xD1, 0xD1)
            : Windows.UI.Color.FromArgb(255, 0x75, 0x75, 0x75);

        for (var i = 0; i < _ticks.Count; i++)
            _ticks[i].Opacity = i <= t.Texture ? 1 : 0.4;

        var (x, y) = Grain.OnRing(t.Texture, DialSize, DialRing);
        Canvas.SetLeft(knob, x - knob.Width / 2);
        Canvas.SetTop(knob, y - knob.Height / 2);
        _knobTurn.Angle = Grain.Angle(t.Texture);
    }

    // ---- the noise tiles ----

    /// <summary>Makes the noise tile the first time it is needed.</summary>
    private WriteableBitmap Tile()
    {
        _tile ??= Bitmap(Grain.TileSize, TileBytes());
        return _tile;
    }

    private byte[] TileBytes()
    {
        _tileBytes ??= Grain.Tile();
        return _tileBytes;
    }

    /// <summary>A bitmap of the given height holding the given pixels (blue, green, red, alpha; colour already multiplied by alpha).</summary>
    private static WriteableBitmap Bitmap(int height, byte[] pixels)
    {
        var bitmap = new WriteableBitmap(Grain.TileSize, height);

        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(pixels, 0, pixels.Length);

        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    /// Lays out enough copies of the tile to cover the window, adding or
    /// removing copies as the window grows or shrinks. Cheap to call often.
    /// </summary>
    private void Tiles()
    {
        var size = Grain.TileSize;
        var columns = (int)Math.Ceiling(root.ActualWidth / size);
        var rows = (int)Math.Ceiling(root.ActualHeight / size);
        var need = columns * rows;

        for (var i = 0; i < need; i++)
        {
            Image image;

            if (i < grain.Children.Count)
            {
                image = (Image)grain.Children[i];
            }
            else
            {
                image = new Image
                {
                    Source = Tile(),
                    Width = size,
                    Height = size,
                    Stretch = Stretch.Fill,
                    IsHitTestVisible = false,
                };
                grain.Children.Add(image);
            }

            Canvas.SetLeft(image, i % columns * size);
            Canvas.SetTop(image, i / columns * size);
        }

        while (grain.Children.Count > need)
            grain.Children.RemoveAt(grain.Children.Count - 1);
    }

    // ---- the floating sidebar ----

    /// <summary>
    /// The sidebar, when it floats over the page (hidden, or peeking), is a solid
    /// panel, and it hides the window's grain behind it. So the panel carries a
    /// layer of its own, under its content: a brush made of a strip of the noise
    /// tile repeated downwards (a brush cannot repeat by itself), which a rounded
    /// border then clips to the panel's corners. The layer is pulled out over the
    /// panel's padding so that it covers the whole panel, and it is hidden while
    /// the sidebar is docked, because then the window's own grain shows through.
    /// Called whenever the texture, the window's size or the sidebar's state change.
    /// </summary>
    private void SideGrain()
    {
        var t = App.Look.Cur;
        var show = t.Texture > 0 && !_dock;

        sidegrain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
            return;

        sidegrain.Opacity = Grain.Opacity(t.Texture);

        var pad = side.Padding;
        sidegrain.Margin = new Thickness(-pad.Left, -pad.Top, -pad.Right, -pad.Bottom);

        // The strip has to be as tall as the panel, which is at most as tall as the window.
        // It only ever grows: a strip that is too tall is cut off by the panel.
        var rows = Math.Max(1, (int)Math.Ceiling(root.ActualHeight / Grain.TileSize));
        if (rows > _sideRows)
        {
            var tile = TileBytes();
            var strip = new byte[tile.Length * rows];
            for (var i = 0; i < rows; i++)
                Buffer.BlockCopy(tile, 0, strip, i * tile.Length, tile.Length);

            sidegrain.Background = new ImageBrush
            {
                ImageSource = Bitmap(Grain.TileSize * rows, strip),
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            _sideRows = rows;
        }
    }

    // ---- turning the dial ----

    private void dial_Down(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(dial);
        if (!p.Properties.IsLeftButtonPressed)
            return;

        _dialDrag = dial.CapturePointer(e.Pointer);
        Turn(p.Position);
    }

    private void dial_Move(object sender, PointerRoutedEventArgs e)
    {
        if (_dialDrag)
            Turn(e.GetCurrentPoint(dial).Position);
    }

    private void dial_Up(object sender, PointerRoutedEventArgs e)
    {
        EndTurn(e);
    }

    private void dial_Lost(object sender, PointerRoutedEventArgs e)
    {
        EndTurn(e);
    }

    /// <summary>Sets the texture to the step the pointer points at. Not saved on every step; EndTurn saves once.</summary>
    private void Turn(Windows.Foundation.Point p)
    {
        var step = Grain.FromPoint(p.X, p.Y, DialSize, App.Look.Cur.Texture);

        if (step != App.Look.Cur.Texture)
            App.Look.Set(t => t.Texture = step, save: false);
    }

    private void EndTurn(PointerRoutedEventArgs e)
    {
        if (!_dialDrag)
            return;

        _dialDrag = false;
        dial.ReleasePointerCapture(e.Pointer);
        App.Look.Commit();
    }
}
