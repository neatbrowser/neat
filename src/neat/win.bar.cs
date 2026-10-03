using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;

namespace neat;

/// <summary>
/// The title bar: logo, sidebar toggle, back / forward / reload, and the
/// address chip (copy link, domain, bookmark star). The system still draws
/// minimize / maximize / close at the right end, so Windows 11 snap layouts
/// keep working.
/// </summary>
public sealed partial class Win
{
    // Last click-through regions handed to the system, so identical ones are not sent again.
    private RectInt32[] _holes = Array.Empty<RectInt32>();

    private void BarInit()
    {
        // The content reaches the top edge and the whole bar is the drag area.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(bar);

        // System caption buttons stay, made transparent so they sit on our background.
        var t = AppWindow.TitleBar;
        t.ButtonBackgroundColor = Colors.Transparent;
        t.ButtonInactiveBackgroundColor = Colors.Transparent;

        // Buttons inside a draggable bar only receive clicks where the system
        // has been told to pass them through (see Holes).
        bar.LayoutUpdated += (s, e) => Holes();

        Accel(VirtualKey.C, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => _ = CopyLink());
    }

    /// <summary>
    /// Tells the system which rectangles of the title bar are buttons rather
    /// than window-drag area. Positions come from the buttons themselves and are
    /// scaled to physical pixels. Runs after every layout, but only talks to the
    /// system when something actually moved.
    /// </summary>
    private void Holes()
    {
        if (root.XamlRoot is null)
            return;

        var k = root.XamlRoot.RasterizationScale;
        var all = new List<RectInt32>();

        foreach (var b in new FrameworkElement[] { tog, back, fwd, reload, copy, star })
        {
            if (b.ActualWidth <= 0 || b.ActualHeight <= 0)
                continue;

            // Position in window coordinates; null means "relative to the window content".
            var r = b.TransformToVisual(null).TransformBounds(
                new Windows.Foundation.Rect(0, 0, b.ActualWidth, b.ActualHeight));

            all.Add(new RectInt32(
                (int)Math.Round(r.X * k),
                (int)Math.Round(r.Y * k),
                (int)Math.Round(r.Width * k),
                (int)Math.Round(r.Height * k)));
        }

        var now = all.ToArray();
        if (now.SequenceEqual(_holes))
            return;

        _holes = now;
        Microsoft.UI.Input.InputNonClientPointerSource
            .GetForWindowId(AppWindow.Id)
            .SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, now);
    }

    /// <summary>Shows the selected tab's site in the address chip.</summary>
    private void ShowDom(Tab tab)
    {
        dom.Text = Dom(tab.Src);
        ToolTipService.SetToolTip(dom, string.IsNullOrEmpty(tab.Src) ? null : tab.Src);
    }

    /// <summary>"https://www.example.com/a/b" becomes "example.com"; anything else is shown as it is.</summary>
    private static string Dom(string? src)
    {
        if (string.IsNullOrEmpty(src))
            return "New Tab";

        var web = Uri.TryCreate(src, UriKind.Absolute, out var u)
            && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);
        if (!web)
            return src;

        var h = u!.Host;
        return h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h[4..] : h;
    }

    private async void copy_Click(object sender, RoutedEventArgs e)
    {
        await CopyLink();
    }

    /// <summary>Copies the selected tab's address, and shows a check mark for a moment.</summary>
    private async Task CopyLink()
    {
        var url = _cur?.Src;
        if (string.IsNullOrEmpty(url))
            return;

        try
        {
            var pk = new DataPackage();
            pk.SetText(url);
            Clipboard.SetContent(pk);
        }
        catch (Exception ex)
        {
            // The clipboard can be held by another program for an instant.
            System.Diagnostics.Debug.WriteLine("[bar] copy failed: " + ex.Message);
            return;
        }

        copyic.Glyph = "\uE73E";
        await Task.Delay(1200);
        copyic.Glyph = "\uE71B";
    }
}
