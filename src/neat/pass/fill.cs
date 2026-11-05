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
///   - It fills the top page, or a frame right under it when the window names the page above (a.t) and the frame
///     itself finds that to be its parent (location.ancestorOrigins); no other frame, and a frame without that check, gets 'frame'.
///   - It fills the form that holds the field with the focus (the one the key was drawn over), and only that
///     one, so a page with a sign-in and a sign-up side by side gets the login in the right one. With no
///     field in focus, or when the focused field's form has nothing to fill, it looks at the whole page.
///   - It reads inside open shadow roots (web components), where many sign-in boxes live.
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
    public static string For(string origin, string user, string password, string? top = null)
    {
        var data = JsonSerializer.Serialize(new { o = origin, u = user, p = password, t = top });
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

            const visible = el => {
              if (!el || !el.isConnected) return false;
              // Up through the tree, and out of a shadow root into the element that hosts it.
              for (let n = el; n && n.nodeType === 1; n = n.parentElement || (n.getRootNode() && n.getRootNode().host)) {
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

            // Every input in page order, open shadow roots included (a closed one cannot be read).
            const every = [];
            const walk = node => {
              for (let c = node.firstElementChild; c; c = c.nextElementSibling) {
                if (c.tagName === 'INPUT') every.push(c);
                if (c.shadowRoot) walk(c.shadowRoot);
                walk(c);
              }
            };
            walk(document);

            // The form (or the web component) of an input: what makes two inputs belong together.
            const groupOf = el => el.form || (el.getRootNode() instanceof ShadowRoot ? el.getRootNode() : null);

            // The field with the focus, also when it is inside a shadow root.
            let focus = document.activeElement;
            while (focus && focus.shadowRoot && focus.shadowRoot.activeElement) focus = focus.shadowRoot.activeElement;

            const isNew = i => /new-password/i.test(hint(i));
            const isUser = i => TEXTY.has(i.type) && usable(i) &&
              (i.type === 'email' || /(^|\s)(username|email)(\s|$)/i.test(hint(i)) || IDENT.test((i.name || '') + ' ' + (i.id || '')));

            // The focused field's form, if it has a login to fill; otherwise the whole page.
            let fields = every;
            if (focus && focus.tagName === 'INPUT' && groupOf(focus)) {
              const mine = every.filter(i => groupOf(i) === groupOf(focus));
              if (mine.some(i => (i.type === 'password' && usable(i) && !isNew(i)) || (a.u && isUser(i))))
                fields = mine;
            }

            const pws = fields.filter(i => i.type === 'password' && usable(i) && !isNew(i));
            let did = '';

            if (pws.length > 0) {
              const pw = pws.includes(focus) ? focus : pws[0];
              if (a.u) {
                // The fields before the password, in page order (it works across shadow roots too).
                const before = fields.slice(0, fields.indexOf(pw)).filter(i => TEXTY.has(i.type) && usable(i) && (!groupOf(pw) || groupOf(i) === groupOf(pw)));
                const marked = before.filter(i => /(^|\s)(username|email)(\s|$)/i.test(hint(i)));
                const user = marked.length ? marked[marked.length - 1] : before[before.length - 1];
                if (user) { put(user, a.u); did += 'user+'; }
              }
              put(pw, a.p);
              did += 'pass';
            } else if (a.u) {
              const first = fields.filter(isUser);
              if (first.length) { put(first.includes(focus) ? focus : first[0], a.u); did = 'user'; }
            }

            return did === '' ? 'none' : did;
          } catch (e) {
            return 'error';
          }
        }
        """;
}
