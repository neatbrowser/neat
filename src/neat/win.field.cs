using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace neat;

/// <summary>
/// The key over a login field of the page, and the list of saved logins that opens from it.
///
/// The page script (pass/capture.cs) only says where the field with the focus is and what kind of
/// field it is. The key is NEAT's own control (fieldkey, in win.xaml) drawn over the page, so a
/// page can neither read it nor press it. Pressing it opens the same list as the key button in the
/// address chip (win.fill.cs), under the field instead of under the address.
///
/// The rules, on top of those of win.fill.cs:
///   - The key for a saved login shows only on a page that has one, and only for a field the script calls
///     a login field (a user name or a password in use). A field where a password is being chosen
///     gets a key that suggests one (win.suggest.cs), on any page.
///   - The list opens by itself only when the person pressed the pointer on the field (the script says
///     so, for a real press only) and the vault is already open. A shut vault is never asked about
///     because a field was clicked; the key still shows and asks when it is pressed.
///   - Nothing is filled until a login is picked in the list.
///   - What the page says is only a place to draw: numbers are checked, kept inside the page, and a
///     field that is not on show or too small for the key gets none.
/// </summary>
public sealed partial class Win
{
    // Opens the list when a field is clicked. false: only the key shows, and the person presses it.
    private const bool FieldAutoOpen = true;

    private const double KeySize = 24;

    // The field the key is at, in window coordinates; null when no key is shown.
    private Rect? _field;

    // What the page called the field at that key: "login" (fill a saved login) or "new" (suggest a password).
    private string _fieldKind = string.Empty;

    private void fieldkey_Click(object sender, RoutedEventArgs e)
    {
        OpenAtField();
    }

    // What the key does, by kind of field.
    private void OpenAtField()
    {
        var tab = _cur;
        var at = _field;
        if (tab is null || at is null)
            return;

        if (_fieldKind == "new")
            PasswordList(tab, at, true);
        else
            _ = Guard(() => OpenFill(at), "opening the fill list at a field");
    }

    /// <summary>The page says which field has the focus (or that none has). Only for the tab on show.</summary>
    private void OnField(Tab tab, string origin, JsonElement msg)
    {
        if (_closed || tab != _cur)
            return;

        var shown = _bubbleAnchored;
        var kind = JsonText(msg, "k");

        // A login field needs a saved login on this page; a field for a new password does not.
        if ((kind != "login" && kind != "new") || !Box(tab, msg, out var field) || (kind == "login" && !App.Vault.Has(origin)))
        {
            HideField();
            return;
        }

        var click = msg.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.Number;

        // A list that hangs under a field that has since moved, or been left by the keyboard, is taken down.
        if (shown && !click)
            HideBubble();

        _field = field;
        _fieldKind = kind;
        ToolTipService.SetToolTip(fieldkey, kind == "new" ? "Suggest a password" : "Fill a saved login");
        fieldkey.Margin = new Thickness(field.X + field.Width - KeySize - 6, field.Y + (field.Height - KeySize) / 2, 0, 0);
        fieldkey.Visibility = Visibility.Visible;

        // A shut vault is never asked about because a field was clicked (see above); a new password needs no vault.
        if (FieldAutoOpen && click && (kind == "new" || App.Vault.IsOpen))
            OpenAtField();
    }

    /// <summary>Takes the key down, and the list with it if it hangs under a field.</summary>
    private void HideField()
    {
        if (_closed)
            return;

        _field = null;
        fieldkey.Visibility = Visibility.Collapsed;

        if (_bubbleAnchored)
            HideBubble();
    }

    /// <summary>
    /// The field's box in window coordinates, from the page's own [x, y, width, height] in page pixels.
    /// False when the numbers are not numbers, the box is too small for the key, or its middle is not in the page.
    /// </summary>
    private static bool Box(Tab tab, JsonElement msg, out Rect box)
    {
        box = default;

        if (!msg.TryGetProperty("r", out var r) || r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != 4)
            return false;

        var v = new double[4];
        var i = 0;
        foreach (var n in r.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Number || !n.TryGetDouble(out v[i]) || !double.IsFinite(v[i]) || Math.Abs(v[i]) > 100000)
                return false;

            i++;
        }

        // The page's zoom: the view is as wide as the page's own width times the zoom.
        var zoom = 1d;
        if (msg.TryGetProperty("v", out var pageWidth) && pageWidth.ValueKind == JsonValueKind.Number
            && pageWidth.TryGetDouble(out var pw) && pw >= 1 && pw < 100000 && tab.View.ActualWidth > 0)
            zoom = tab.View.ActualWidth / pw;

        if (!(zoom >= 0.25 && zoom <= 5))
            zoom = 1;

        var w = v[2] * zoom;
        var h = v[3] * zoom;
        var x = v[0] * zoom;
        var y = v[1] * zoom;

        // Too small to carry the key, or its middle is outside the page.
        if (w < KeySize * 2 || h < 18 || x + w / 2 < 0 || y + h / 2 < 0
            || x + w / 2 > tab.View.ActualWidth || y + h / 2 > tab.View.ActualHeight)
            return false;

        var origin = tab.View.TransformToVisual(null).TransformPoint(new Point(0, 0));
        box = new Rect(origin.X + x, origin.Y + y, w, h);
        return true;
    }
}
