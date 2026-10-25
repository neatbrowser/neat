using System.Text.Json;

namespace neat;

/// <summary>
/// The script that fills a saved login into the page the person is looking at. The window runs it
/// when, and only when, the person picks a login from the key button; nothing fills by itself when
/// a page loads. It is JavaScript in a C# string, and the self-check in tests/capture.check
/// reads it from this file and runs it against made-up pages, so keep it between its own """ marks.
///
/// The rules it keeps:
///   - The values are never glued into the script text. They go in as one JSON object, which is
///     a data literal and cannot end a string early or start code, whatever the password holds.
///   - It checks for itself that the page it runs in has exactly the origin the login was saved
///     for. The window has checked that too, but a page can navigate between the check and the
///     run, and this is the check that cannot be raced.
///   - It fills only the top page, never a frame.
///   - It fills only fields the person can see and type in: shown, with a size, not pushed off
///     the page, not transparent, not read-only or disabled. A hidden field is how a fake form
///     catches a password, so it is never touched.
///   - A field marked as taking a NEW password (a sign-up form) is left alone.
///   - It sets the value the way the browser does, through the field's own setter, and then fires
///     input and change events, so pages built with React or Vue notice the new value.
/// It returns a short word saying what it did: user+pass, pass, user, none, origin or frame.
/// </summary>
internal static class FillScript
{
    /// <summary>The script to run, with the login as data.</summary>
    public static string For(string origin, string user, string password)
    {
        var data = JsonSerializer.Serialize(new { o = origin, u = user, p = password });
        return "(" + Source + ")(" + data + ");";
    }

    public const string Source = """
        (a) => {
          try {
            if (window.top !== window) return 'frame';
            if (location.origin !== a.o) return 'origin';

            const visible = el => {
              if (!el || !el.isConnected) return false;
              for (let n = el; n && n.nodeType === 1; n = n.parentElement) {
                if (n.hidden) return false;
                const s = getComputedStyle(n);
                if (s.display === 'none' || s.visibility === 'hidden' || s.opacity === '0') return false;
              }
              if (document.documentElement.getBoundingClientRect().width > 0) {
                const r = el.getBoundingClientRect();
                if (r.width < 1 || r.height < 1) return false;
                const x = r.left + window.scrollX;
                const y = r.top + window.scrollY;
                if (x + r.width <= 0 || y + r.height <= 0) return false;
                if (x >= Math.max(document.documentElement.scrollWidth, window.innerWidth)) return false;
              }
              return true;
            };

            const TEXTY = new Set(['text', 'email', 'tel']);
            const IDENT = /user|e-?mail|login|ident|account|phone|tel/i;
            const usable = el => visible(el) && !el.readOnly && !el.disabled;
            const hint = el => el.getAttribute('autocomplete') || '';
            const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;

            const put = (el, v) => {
              setter.call(el, v);
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
            };

            const fields = Array.from(document.querySelectorAll('input'));
            const pws = fields.filter(i => i.type === 'password' && usable(i) && !/new-password/i.test(hint(i)));
            let did = '';

            if (pws.length > 0) {
              const pw = pws[0];
              if (a.u) {
                const root = pw.form || document;
                const before = Array.from(root.querySelectorAll('input')).filter(i =>
                  TEXTY.has(i.type) && usable(i) && (pw.compareDocumentPosition(i) & Node.DOCUMENT_POSITION_PRECEDING));
                const marked = before.filter(i => /(^|\s)(username|email)(\s|$)/i.test(hint(i)));
                const user = marked.length ? marked[marked.length - 1] : before[before.length - 1];
                if (user) { put(user, a.u); did += 'user+'; }
              }
              put(pw, a.p);
              did += 'pass';
            } else if (a.u) {
              const first = fields.filter(i => TEXTY.has(i.type) && usable(i) &&
                (i.type === 'email' || /(^|\s)(username|email)(\s|$)/i.test(hint(i)) || IDENT.test((i.name || '') + ' ' + (i.id || ''))));
              if (first.length) { put(first[0], a.u); did = 'user'; }
            }

            return did === '' ? 'none' : did;
          } catch (e) {
            return 'error';
          }
        }
        """;
}
