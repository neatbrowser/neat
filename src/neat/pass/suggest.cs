using System.Security.Cryptography;
using System.Text.Json;

namespace neat;

/// <summary>
/// Makes a strong password for a field where the person is choosing one (sign-up, change password).
/// It uses the system's random numbers only, picks every character with no bias, and puts at least
/// one of each kind in (lower case, upper case, digit, and a symbol unless they are switched off).
/// Letters and digits that are easy to mistake for each other (l, I, O, 0, 1) are left out, so a
/// password that has to be read or typed by hand is not misread.
/// </summary>
internal static class PasswordGen
{
    public const int Default = 20;

    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Digits = "23456789";
    private const string Symbols = "!@#$%^&*-_=+?";

    /// <summary>A new password of <paramref name="length"/> characters (8 to 128).</summary>
    public static string Make(int length = Default, bool symbols = true)
    {
        length = Math.Clamp(length, 8, 128);

        var sets = symbols ? new[] { Lower, Upper, Digits, Symbols } : new[] { Lower, Upper, Digits };
        var all = string.Concat(sets);
        var chars = new char[length];

        // One of each kind first, then anything; the shuffle below hides where the first ones are.
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Pick(i < sets.Length ? sets[i] : all);

        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    private static char Pick(string from)
    {
        return from[RandomNumberGenerator.GetInt32(from.Length)];
    }
}

/// <summary>
/// The script that puts a chosen password into the page, when the person picks "Use" in the
/// suggestion list. It fills the field that has the focus (the one the key was pressed at) and any
/// empty password field of the same form beside it that is not the password in use now: the
/// confirmation. It keeps the rules of the fill script (pass/fill.cs): the password goes in as data,
/// the page's origin is checked from inside the page, the top page only, only fields the person can
/// see and type in, values set the way the browser does, input and change events fired. It returns
/// pass, pass+confirm, none, origin or frame. It is JavaScript in a C# string and is run by
/// tests/capture.check against made-up pages, so keep it between its own """ marks.
/// </summary>
internal static class SuggestScript
{
    public static string For(string origin, string password)
    {
        var data = JsonSerializer.Serialize(new { o = origin, p = password });
        return "(" + Source + ")(" + data + ");";
    }

    public const string Source = """
        (a) => {
          try {
            if (window.top !== window) return 'frame';
            if (location.origin !== a.o) return 'origin';

            const up = n => n.parentElement || (n.getRootNode && n.getRootNode().host) || null;
            const visible = el => {
              if (!el || !el.isConnected) return false;
              for (let n = el; n && n.nodeType === 1; n = up(n)) {
                if (n.hidden) return false;
                const s = getComputedStyle(n);
                if (s.display === 'none' || s.visibility === 'hidden' || s.opacity === '0') return false;
              }
              if (document.documentElement.getBoundingClientRect().width > 0) {
                const r = el.getBoundingClientRect();
                if (r.width < 1 || r.height < 1) return false;
              }
              return true;
            };
            const usable = el => visible(el) && !el.readOnly && !el.disabled;
            const hint = el => (el.getAttribute('autocomplete') || '').toLowerCase();
            const said = el => ((el.name || '') + ' ' + (el.id || '') + ' ' + (el.getAttribute('aria-label') || '') + ' ' +
              (el.getAttribute('placeholder') || '')).toLowerCase();
            const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
            const put = (el, v) => {
              setter.call(el, v);
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
            };

            // The field with the focus, looking into open shadow roots.
            let el = document.activeElement;
            while (el && el.shadowRoot && el.shadowRoot.activeElement) el = el.shadowRoot.activeElement;
            const pwish = el && el.tagName === 'INPUT' &&
              (el.type === 'password' || (el.type === 'text' && (hint(el) === 'new-password' || /pass(word|wd)/.test(said(el)))));
            if (!pwish || !usable(el)) return 'none';

            const scope = el.form || el.getRootNode();
            const others = Array.from(scope.querySelectorAll('input[type="password"]')).filter(i =>
              i !== el && usable(i) && i.value === '' && hint(i) !== 'current-password' &&
              !/\b(current|old|existing)\b|hiện tại|mật khẩu cũ/.test(said(i)));

            put(el, a.p);
            others.forEach(i => put(i, a.p));
            return others.length ? 'pass+confirm' : 'pass';
          } catch (e) {
            return 'error';
          }
        }
        """;
}
