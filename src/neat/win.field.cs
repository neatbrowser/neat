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
///     gets a key that suggests one (win.suggest.cs), on any page. A field for a one-time code gets a
///     key when a saved login of the page has a code key (vault/totp.cs); the list shows only those logins.
///     Card and contact fields get a key when a card or an identity is saved (win.items.cs).
///   - The list opens by itself only when the person pressed the pointer on the field (the script says
///     so, for a real press only) and the vault is already open. A shut vault is never asked about
///     because a field was clicked; the key still shows and asks when it is pressed.
///   - Nothing is filled until a login is picked in the list.
///   - A field inside a frame gets the key too (win.frames.cs): the frame says the kind of field, the top page says where the
///     frame sits, and the list and the fill go to that frame, with the frame's own address.
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

        // The key of a field in a frame fills into that frame (win.frames.cs).
        var via = ViaNow();

        if (_fieldKind == "new")
            PasswordList(tab, at, true, via);
        else
            _ = Guard(() => OpenFill(at, _fieldKind, via), "opening the fill list at a field");
    }

    /// <summary>The page says which field has the focus (or that none has). Only for the tab on show.</summary>
    private void OnField(Tab tab, string origin, JsonElement msg)
    {
        if (_closed || tab != _cur)
            return;

        var kind = JsonText(msg, "k");

        // "No field" from the page itself takes down only a key that is the page's own: the focus moving
        // into a frame says it too, a moment before the frame says where it went.
        if (kind is null)
        {
            if (_fieldFrame is null)
                HideField(false);

            return;
        }

        if (!KnownKind(kind, origin) || !Box(tab, msg, kind == "otp" ? 20 : KeySize * 2, out var field))
        {
            HideField();
            return;
        }

        var click = msg.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.Number;
        ShowKey(field, kind, origin, null, click);
    }

    // A login field needs a saved login on this page, a code field a saved key for one; a field for a
    // new password needs nothing.
    private static bool KnownKind(string? kind, string origin)
    {
        return kind == "new"
            || (kind == "login" && App.Vault.Has(origin))
            || (kind == "otp" && App.Vault.HasOtp(origin))
            || (kind is "card" or "identity" && App.Vault.HasItems(kind));
    }

    /// <summary>
    /// Draws the key at a field. <paramref name="via"/> is the frame the field is in (null: the page's own), and
    /// <paramref name="origin"/> the address its logins are saved for.
    /// </summary>
    private void ShowKey(Rect field, string kind, string origin, Via? via, bool click)
    {
        // A list that hangs under a field that has since moved, or been left by the keyboard, is taken down.
        if (_bubbleAnchored && !click)
            HideBubble();

        _field = field;
        _fieldKind = kind;
        _fieldFrame = via?.Frame;
        _fieldOrigin = origin;

        // A field of the page itself replaces any field of a frame that was waiting for its box.
        if (via is null)
            _fk = null;

        ToolTipService.SetToolTip(fieldkey, kind switch
        {
            "new" => "Suggest a password",
            "otp" => "Fill a one-time code",
            "card" => "Fill a saved card",
            "identity" => "Fill a saved identity",
            _ => "Fill a saved login",
        });
        fieldkey.Margin = new Thickness(field.X + field.Width - KeySize - 6, field.Y + (field.Height - KeySize) / 2, 0, 0);

        // A box of a code split in digits is too small to carry the key: it only opens the list when clicked.
        fieldkey.Visibility = field.Width >= KeySize * 2 ? Visibility.Visible : Visibility.Collapsed;

        // A shut vault is never asked about because a field was clicked (see above); a new password needs no vault.
        if (FieldAutoOpen && click && (kind == "new" || App.Vault.IsOpen))
            OpenAtField();
    }

    /// <summary>
    /// Takes the key down, and the list with it if it hangs under a field. <paramref name="forget"/>: also forget
    /// the field of a frame that was waiting for its box (the page changed or the tab did).
    /// </summary>
    private void HideField(bool forget = true)
    {
        if (_closed)
            return;

        _field = null;
        _fieldFrame = null;
        _fieldOrigin = null;
        fieldkey.Visibility = Visibility.Collapsed;

        if (forget)
        {
            _fk = null;
            _fr = null;
        }

        if (_bubbleAnchored)
            HideBubble();
    }

    /// <summary>
    /// The field's box in window coordinates, from the page's own [x, y, width, height] in page pixels.
    /// False when the numbers are not numbers, the box is narrower than <paramref name="least"/>, or its middle is not in the page.
    /// </summary>
    private static bool Box(Tab tab, JsonElement msg, double least, out Rect box)
    {
        box = default;

        if (!msg.TryGetProperty("r", out var r) || !TryRect(r, out var v))
            return false;

        var width = msg.TryGetProperty("v", out var pageWidth) && pageWidth.ValueKind == JsonValueKind.Number
            && pageWidth.TryGetDouble(out var pw) ? pw : 0;

        return BoxAt(tab, v, width, least, out box);
    }

    /// <summary>
    /// The same from numbers already read. <paramref name="pageWidth"/> is the page's width in its own pixels
    /// (0 when not told): the zoom is the view's width over it.
    /// </summary>
    private static bool BoxAt(Tab tab, double[] v, double pageWidth, double least, out Rect box)
    {
        box = default;

        // The page's zoom: the view is as wide as the page's own width times the zoom.
        var zoom = 1d;
        if (pageWidth >= 1 && pageWidth < 100000 && tab.View.ActualWidth > 0)
            zoom = tab.View.ActualWidth / pageWidth;

        if (!(zoom >= 0.25 && zoom <= 5))
            zoom = 1;

        var w = v[2] * zoom;
        var h = v[3] * zoom;
        var x = v[0] * zoom;
        var y = v[1] * zoom;

        // Too small to carry the key, or its middle is outside the page.
        if (w < least || h < 18 || x + w / 2 < 0 || y + h / 2 < 0
            || x + w / 2 > tab.View.ActualWidth || y + h / 2 > tab.View.ActualHeight)
            return false;

        var origin = tab.View.TransformToVisual(null).TransformPoint(new Point(0, 0));
        box = new Rect(origin.X + x, origin.Y + y, w, h);
        return true;
    }
}
