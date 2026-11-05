using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace neat;

/// <summary>
/// Forms inside frames (an embedded login box, a payment form of a card processor): the key, the list and
/// the fill work there as in the page itself, with these differences, all of them on the side of caution.
///
/// What is read:
///   - Only the frames right under the top page (nested ones are left alone), and only ordinary web pages
///     (the page script says so, and the window checks the address again).
///   - A frame reports through its own channel, <c>CoreWebView2Frame.WebMessageReceived</c>. The address a
///     frame's login is saved for, and listed for, is the one WebView2 says the message came from, not
///     anything the message says. A message that claims to be a frame's (fr) but came through the top
///     page's channel is dropped, and the other way round. Nothing secret is ever posted to the parent page.
///   - Where the frame's field is on the page is told by the top page (the frame cannot know it): the frame
///     tells the window the kind of field, and the top page the box, and the two are put together here by
///     the frame's name (fid). A page that forges the box can only move the key. What is listed, and where
///     it goes, comes from the frame's own address and the frame itself.
///
/// What is saved:
///   - A login is saved for the frame's own address. The question says it was typed in a frame of this page.
///   - A card is held for the top page's address (that is the shop the person is paying), like one typed in the page.
///
/// What is filled:
///   - Into the frame that holds the key, nowhere else. The list says which address it goes into and which
///     page the frame is in. The scripts check from inside the frame that it still has that address and that
///     its parent is the page the window saw (location.ancestorOrigins); a frame that cannot tell gets nothing.
///
/// Not covered: a payment form that puts each field in a frame of its own (one frame cannot see the others,
/// and a Pay button outside them reports nothing), and frames inside frames.
/// </summary>
public sealed partial class Win
{
    /// <summary>A frame that the key, the list or a fill belongs to, and the address it was said to have.</summary>
    private sealed record Via(CoreWebView2Frame Frame, string Origin);

    // The kind of field a frame said has the focus, until its box has been told too.
    private sealed class FrameKey
    {
        public FrameKey(Tab tab, CoreWebView2Frame frame, string origin, string fid, string kind, bool click)
        {
            Tab = tab;
            Frame = frame;
            Origin = origin;
            Fid = fid;
            Kind = kind;
            Click = click;
        }

        public Tab Tab { get; }
        public CoreWebView2Frame Frame { get; }
        public string Origin { get; }
        public string Fid { get; }
        public string Kind { get; }

        // The person pressed the pointer on the field: the list may open once the key is placed.
        public bool Click { get; set; }
    }

    // The box of a frame's field in the top page's pixels, as the top page told it (null: no field).
    private sealed record FrameRect(string Fid, double[]? Box, double Width);

    private FrameKey? _fk;
    private FrameRect? _fr;

    // The frame the key on show belongs to (null: the top page's own), and the address of its logins.
    private CoreWebView2Frame? _fieldFrame;
    private string? _fieldOrigin;

    // The frame that reported the login or card held for a tab (none: the top page did).
    private readonly Dictionary<Tab, CoreWebView2Frame> _loginFrame = new();
    private readonly Dictionary<Tab, CoreWebView2Frame> _cardFrame = new();

    // ---- wiring ----

    /// <summary>Called for every frame the page makes (the first level; deeper ones are not hooked).</summary>
    private void HookFrame(Tab tab, CoreWebView2Frame frame)
    {
        try
        {
            frame.WebMessageReceived += (s, e) => OnFrameMessage(tab, frame, e);

            frame.NavigationCompleted += (s, e) =>
            {
                if (e.IsSuccess)
                    AfterFrame(tab, frame);
            };

            frame.Destroyed += (s, e) =>
            {
                if (_fk?.Frame == frame)
                    _fk = null;

                if (_fieldFrame == frame)
                    HideField();
            };
        }
        catch (Exception ex)
        {
            Log.Write("pass", "a frame could not be hooked (" + ex.GetType().Name + ")");
        }
    }

    /// <summary>
    /// Every message a frame posts comes through here. As in <see cref="OnMessage"/>, it can be anything and
    /// nothing may leave this method as an exception.
    /// </summary>
    private void OnFrameMessage(Tab tab, CoreWebView2Frame frame, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string? text;
            try
            {
                text = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                return;
            }

            var head = "neatpw:" + _tok + ":";
            if (text is null || !text.StartsWith(head, StringComparison.Ordinal) || text.Length > 4096)
                return;

            // The frame's own address, from WebView2. A frame with no web address of its own gets nothing,
            // and neither does a tab whose page is not a web page.
            var core = tab.View.CoreWebView2;
            var origin = Origin.Of(e.Source);
            if (core is null || origin is null || Origin.Of(core.Source) is null)
                return;

            Dispatch(tab, origin, frame, text, head);
        }
        catch (Exception ex)
        {
            Log.Write("pass", "a message from a frame could not be handled (" + ex.GetType().Name + ")");
        }
    }

    private void AfterFrame(Tab tab, CoreWebView2Frame frame)
    {
        var core = tab.View.CoreWebView2;
        if (core is null)
            return;

        if (_capture.Holds(tab) && SameFrame(_loginFrame, tab, frame))
            _ = Guard(() => AfterLogin(tab, core), "after a login");

        if (_cardCapture.Holds(tab) && SameFrame(_cardFrame, tab, frame))
            _ = Guard(() => AfterCard(tab, core), "after a card");
    }

    // ---- who reported what ----

    private static void SetFrame(Dictionary<Tab, CoreWebView2Frame> map, Tab tab, CoreWebView2Frame? frame)
    {
        if (frame is null)
            map.Remove(tab);
        else
            map[tab] = frame;
    }

    /// <summary>Whether the login (or card) held for the tab was reported by this frame (null: by the top page).</summary>
    private static bool SameFrame(Dictionary<Tab, CoreWebView2Frame> map, Tab tab, CoreWebView2Frame? frame)
    {
        return map.TryGetValue(tab, out var held) ? held == frame : frame is null;
    }

    private static bool Dead(CoreWebView2Frame frame)
    {
        try
        {
            return frame.IsDestroyed();
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// Runs a script in the frame that reported the held login (or card), or in the top page when the top page
    /// did, or when that frame is gone (the page moved on: then the page above is what shows how it went).
    /// </summary>
    private static Task<string> RunHeld(Dictionary<Tab, CoreWebView2Frame> map, Tab tab, CoreWebView2 core, string script)
    {
        return map.TryGetValue(tab, out var frame) && !Dead(frame)
            ? frame.ExecuteScriptAsync(script)
            : core.ExecuteScriptAsync(script);
    }

    // ---- the key at a field of a frame ----

    private static bool TryRect(JsonElement r, out double[] v)
    {
        v = new double[4];

        if (r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != 4)
            return false;

        var i = 0;
        foreach (var n in r.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Number || !n.TryGetDouble(out v[i]) || !double.IsFinite(v[i]) || Math.Abs(v[i]) > 100000)
                return false;

            i++;
        }

        return true;
    }

    private static bool Named(string? fid)
    {
        return fid is { Length: > 0 and <= 80 };
    }

    /// <summary>A frame says which kind of field has the focus in it, or that none has.</summary>
    private void OnFrameField(Tab tab, CoreWebView2Frame frame, string origin, JsonElement msg)
    {
        if (_closed || tab != _cur)
            return;

        var fid = JsonText(msg, "fid");
        if (!Named(fid))
            return;

        var kind = JsonText(msg, "k");
        if (kind is null)
        {
            // No field of the frame has the focus any more. Only a key that is this frame's own comes down.
            if (_fk?.Frame == frame)
                _fk = null;

            if (_fieldFrame == frame)
                HideField(false);

            return;
        }

        if (kind is not ("login" or "new" or "otp" or "card" or "identity"))
            return;

        var click = msg.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.Number;
        _fk = new FrameKey(tab, frame, origin, fid!, kind, click);
        ShowFrameKey();
    }

    /// <summary>The top page tells where the frame's field is, or that the frame is no longer on show.</summary>
    private void OnFrameRect(JsonElement msg)
    {
        if (_closed)
            return;

        var fid = JsonText(msg, "fid");
        if (!Named(fid) || !msg.TryGetProperty("r", out var r))
            return;

        if (r.ValueKind == JsonValueKind.Null)
        {
            if (_fr?.Fid == fid)
                _fr = null;

            if (_fk is { } k && k.Fid == fid && _fieldFrame == k.Frame)
                HideField(false);

            return;
        }

        if (!TryRect(r, out var box))
            return;

        var width = msg.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;
        _fr = new FrameRect(fid!, box, width);
        ShowFrameKey();
    }

    /// <summary>Puts the key at the frame's field once both halves are known: the kind (frame) and the box (top page).</summary>
    private void ShowFrameKey()
    {
        if (_closed || _fk is not { } k || _fr is not { Box: not null } f || f.Fid != k.Fid || k.Tab != _cur)
            return;

        if (!KnownKind(k.Kind, k.Origin) || !BoxAt(k.Tab, f.Box!, f.Width, k.Kind == "otp" ? 20 : KeySize * 2, out var field))
        {
            if (_fieldFrame == k.Frame)
                HideField(false);

            return;
        }

        var click = k.Click;
        k.Click = false;
        ShowKey(field, k.Kind, k.Origin, new Via(k.Frame, k.Origin), click);
    }

    // ---- showing where a list or a fill goes ----

    /// <summary>The frame the key on show belongs to, with its address; null when it is the top page's.</summary>
    private Via? ViaNow()
    {
        return _fieldFrame is { } frame && _fieldOrigin is { } origin && !Dead(frame) ? new Via(frame, origin) : null;
    }

    /// <summary>The line of a list that says where a pick goes: the address, and the page above it for a frame.</summary>
    private static string IntoLine(Via? via, string top)
    {
        return via is null
            ? "Into  " + Lookalike.Shown(top)
            : "Into  " + Lookalike.Shown(via.Origin) + "\n(a frame inside  " + Lookalike.Shown(top) + ")";
    }

    private static Task<string> RunIn(CoreWebView2 core, Via? via, string script)
    {
        return via is null ? core.ExecuteScriptAsync(script) : via.Frame.ExecuteScriptAsync(script);
    }

    /// <summary>The address the script of a fill must find above the frame it runs in (null for the top page itself).</summary>
    private static string? Above(CoreWebView2 core, Via? via)
    {
        return via is null ? null : Origin.Of(core.Source);
    }
}
