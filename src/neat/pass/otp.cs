using System.Text.Json;

namespace neat;

/// <summary>
/// The script that puts a one-time code into the page, when the person picks a login in the list at a
/// code field. It follows the rules of the fill script (pass/fill.cs): the code goes in as data, the
/// page's origin is checked from inside the page, the top page or a frame right under it that knows its parent (see fill.cs), only a field the person can see and
/// type in, values set the way the browser does with input and change events fired.
///
/// It fills the field that has the focus (the one the key was pressed at). When that field takes one
/// digit and the form has exactly as many such fields as the code has digits (a code split in boxes), the
/// digits go one to a box. It returns otp, otp+boxes, none, origin or frame. It is JavaScript in a C#
/// string and is run by tests/capture.check against made-up pages, so keep it between its own """ marks.
/// </summary>
internal static class OtpScript
{
    public static string For(string origin, string code, string? top = null)
    {
        var data = JsonSerializer.Serialize(new { o = origin, p = code, t = top });
        return "(" + Source + ")(" + data + ");";
    }

    public const string Source = """
        (a) => {
          try {
            // Inside a frame only when the window said which page is above it, and that page is its direct parent.
            if (window.top !== window) {
              const above = location.ancestorOrigins;
              if (!a.t || !above || above.length !== 1 || above[0] !== a.t) return 'frame';
            }
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
            const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
            const put = (el, v) => {
              setter.call(el, v);
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
            };

            let el = document.activeElement;
            while (el && el.shadowRoot && el.shadowRoot.activeElement) el = el.shadowRoot.activeElement;
            if (!el || el.tagName !== 'INPUT' || el.type === 'password' || !usable(el)) return 'none';

            const code = String(a.p);
            if (el.maxLength === 1) {
              const scope = el.form || el.getRootNode();
              const boxes = Array.from(scope.querySelectorAll('input')).filter(i =>
                i.maxLength === 1 && i.type !== 'password' && i.type !== 'hidden' && usable(i));
              if (boxes.length === code.length && boxes.includes(el)) {
                boxes.forEach((b, i) => put(b, code[i]));
                return 'otp+boxes';
              }
            }

            put(el, code);
            return 'otp';
          } catch (e) {
            return 'error';
          }
        }
        """;
}
