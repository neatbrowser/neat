namespace neat;

/// <summary>
/// The two scripts of the save-password bubble. Both are JavaScript kept in C# strings; the
/// self-check in tests/capture.check reads them from this file and runs them against made-up
/// login forms, so keep each one between its own """ marks and do not use """ inside.
///
/// What the page script does and does not do:
///   - It runs in the top page only. A login form inside an embedded frame is not captured.
///   - It reacts only to real clicks and the Enter key (isTrusted): a page that makes up events
///     cannot make it report anything. It does NOT listen for "submit": the browser itself fires
///     that, with isTrusted true, when a page calls button.click() or form.requestSubmit(), so
///     it proves nothing about the person. A page can still type into its own form, but then
///     it only gets NEAT to ask the person whether to save what the page itself already has.
///   - It reports a login, it does not decide. Whether it worked is found out afterwards,
///     by the window (see vault/capture.cs).
///   - A change-password form (the old password, then the new one twice; or fields marked
///     autocomplete current-password and new-password) is reported as the new password with the
///     old one beside it, so the window can find the saved entry it replaces. Two different
///     passwords with nothing to say which is which are left alone: that may just be a typing mistake.
///   - Two-step logins (user name on one page, password on the next) are reported in two parts: the
///     first step sends only the user name, and the window puts the two together. A password page
///     may also carry the user name in a hidden field marked autocomplete="username" (that is what
///     password managers look for), which is used when no visible field has it.
///   - It runs in the page's own world, so the page can see that it exists. What it sends
///     starts with the session token (the same kind as the shortcut script's), but the token
///     is not a secret from the page: what really checks a message is the window, which
///     accepts it only from the page that is open in the tab, by origin.
/// </summary>
internal static class CaptureScript
{
    /// <summary>The script added to every page, with the session token filled in.</summary>
    public static string For(string token)
    {
        return Source.Replace("__TOKEN__", token);
    }

    public const string Source = """
        (() => {
          if (window.top !== window) return;
          const w = window.chrome && window.chrome.webview;
          if (!w) return;
          const send = w.postMessage.bind(w);
          const TOKEN = '__TOKEN__';
          const MAX = 512;
          const post = o => { try { send('neatpw:' + TOKEN + ':' + JSON.stringify(o)); } catch (e) {} };

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
            }
            return true;
          };

          const TEXTY = new Set(['text', 'email', 'tel']);

          const userFor = (pw, root) => {
            const before = Array.from(root.querySelectorAll('input')).filter(i =>
              i !== pw && TEXTY.has(i.type) && visible(i) && i.value.trim().length > 0 &&
              i.value.length <= MAX && (pw.compareDocumentPosition(i) & Node.DOCUMENT_POSITION_PRECEDING));
            if (!before.length) return '';
            const marked = before.filter(i => /(^|\s)(username|email)(\s|$)/i.test(i.getAttribute('autocomplete') || ''));
            return (marked.length ? marked[marked.length - 1] : before[before.length - 1]).value.trim();
          };

          // A user name left in a field the page hides, marked as the user name the way password
          // managers expect: a password page often keeps the user name of the step before there.
          const hiddenUser = root => {
            const found = Array.from(root.querySelectorAll('input')).filter(i =>
              !visible(i) && i.type !== 'password' && i.value.trim().length > 0 && i.value.length <= MAX &&
              /(^|\s)username(\s|$)/i.test(i.getAttribute('autocomplete') || ''));
            return found.length ? found[found.length - 1].value.trim() : '';
          };

          // The first step of a two-step login: a page with no password field and a filled field that
          // looks like a user name. A search box or a plain text box does not look like one.
          const IDENT = /user|e-?mail|login|ident|account|phone|tel/i;
          let lastStep = null;
          const step = root => {
            const found = Array.from(root.querySelectorAll('input')).filter(i =>
              TEXTY.has(i.type) && visible(i) && i.value.trim().length > 0 && i.value.length <= MAX &&
              (i.type === 'email' || /(^|\s)(username|email)(\s|$)/i.test(i.getAttribute('autocomplete') || '') ||
               IDENT.test((i.name || '') + ' ' + (i.id || ''))));
            if (!found.length) return;
            const u = found[found.length - 1].value.trim();
            const now = Date.now();
            if (lastStep && lastStep.u === u && now - lastStep.at < 1500) return;
            lastStep = { u: u, at: now };
            post({ t: 'user', u: u });
          };

          const watch = (el, id) => {
            let n = 0;
            const t = setInterval(() => {
              n++;
              if (!el.isConnected || !visible(el)) { clearInterval(t); post({ t: 'gone', id: id }); }
              else if (n >= 20) clearInterval(t);
            }, 400);
          };

          let seq = 0;
          let last = null;
          const offer = container => {
            const root = container && container.querySelectorAll ? container : document;
            const shown = Array.from(root.querySelectorAll('input[type="password"]')).filter(i => visible(i));
            if (shown.length === 0) { step(root); return; }
            const pws = shown.filter(i => i.value.length > 0);
            const mark = i => (i.getAttribute('autocomplete') || '').toLowerCase();
            const current = pws.filter(i => mark(i) === 'current-password');
            const fresh = pws.filter(i => mark(i) === 'new-password');
            let pick = null;
            let old = null;
            if (current.length === 1 && fresh.length >= 1 && fresh.every(i => i.value === fresh[0].value) &&
                fresh[0].value !== current[0].value) {
              // A change-password form that says which field is which: the old password and the new one.
              old = current[0].value;
              pick = fresh[0];
            } else if (pws.length === 1) pick = pws[0];
            else if (pws.length === 2 && pws[0].value === pws[1].value) pick = pws[0];
            else if (pws.length === 3 && pws[1].value === pws[2].value && pws[0].value !== pws[1].value) {
              // Old password, then the new one twice.
              old = pws[0].value;
              pick = pws[1];
            }
            if (!pick || pick.value.length > MAX || (old !== null && old.length > MAX)) return;
            const u = userFor(pick, root) || hiddenUser(root);
            // One login often makes two reports at once (Enter, then the click the browser
            // makes on the button): the second is the same login and is left out.
            const now = Date.now();
            if (last && last.u === u && last.p === pick.value && last.o === old && now - last.at < 1500) return;
            last = { u: u, p: pick.value, o: old, at: now };
            const id = ++seq;
            const report = { t: 'offer', id: id, u: u, p: pick.value };
            if (old !== null) report.o = old;
            post(report);
            watch(pick, id);
          };

          document.addEventListener('click', e => {
            if (!e.isTrusted || !e.target || !e.target.closest) return;
            const el = e.target.closest('button, input[type="submit"], input[type="image"], [role="button"]');
            if (!el || (el.tagName === 'BUTTON' && el.type === 'reset')) return;
            offer(el.closest('form') || document);
          }, true);

          document.addEventListener('keydown', e => {
            if (!e.isTrusted || e.isComposing || e.key !== 'Enter') return;
            const el = e.target;
            if (!el || el.tagName !== 'INPUT') return;
            offer(el.closest('form') || document);
          }, true);
        })();
        """;

    /// <summary>
    /// Run by the window, after a page has loaded, to find out whether a password field is on
    /// show: a login that worked leads to a page without one. The answer is true or false.
    /// (The visibility test is the same as in <see cref="Source"/>.)
    /// </summary>
    public const string HasPassword = """
        (() => {
          try {
            const visible = el => {
              for (let n = el; n && n.nodeType === 1; n = n.parentElement) {
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
            return Array.from(document.querySelectorAll('input[type="password"]')).some(visible);
          } catch (e) {
            return true;
          }
        })();
        """;
}
