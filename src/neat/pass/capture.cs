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
///   - It reads open shadow roots too (web components): fields, buttons and keys inside them are
///     found, and a field is "on show" only if every host above it is. Closed shadow roots cannot be read.
///   - A password field is one of type password, one that is drawn masked (-webkit-text-security),
///     or one the page switched to type text to show it ("show password") that still says
///     password in its autocomplete, name or id. Such a field is never taken for the user name.
///   - Cancel, Close, Back and "show password" buttons are not a sign-in: a click on one is left alone,
///     so closing a login dialog after typing does not look like a login that worked. A control that is
///     not a button but looks clickable (cursor: pointer, a styled div) counts only when a filled
///     password field is on show, so that it never starts the two-step guess on its own.
///   - Which password field is which (the one now in use, the new one, its confirmation) is read from
///     autocomplete and from what the field says about itself (label, placeholder, name). Where that
///     is not enough, the form's own words decide what kind of form it is (classify below: heading,
///     buttons, links, checkboxes): a form that is plainly a change-password form, with two different
///     passwords and no marks, is reported as the new password with the old one beside it. A sign-in
///     or sign-up form with two different passwords is still taken for a typing mistake and left alone.
///   - Rules for single sites (pass/rules.json, filled in as __RULES__) can leave a part of a page
///     out, or name the exact user name and password fields of a form the guess gets wrong.
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
        // The rules go in last: they are the one piece that may contain any text.
        return Source.Replace("__TOKEN__", token).Replace("__RULES__", SiteRules.Json);
    }

    public const string Source = """
        (() => {
          if (window.top !== window) return;
          const w = window.chrome && window.chrome.webview;
          if (!w) return;
          const send = w.postMessage.bind(w);
          const TOKEN = '__TOKEN__';
          const RULES = __RULES__;
          const MAX = 512;
          const post = o => { try { send('neatpw:' + TOKEN + ':' + JSON.stringify(o)); } catch (e) {} };

          // ---- walking the page, open shadow roots included ----

          // The element a node sits in, or the host of the shadow root it is the top of.
          const up = n => n.parentElement || (n.getRootNode && n.getRootNode().host) || null;

          const closestDeep = (el, sel) => {
            for (let n = el; n; n = up(n)) {
              try { if (n.matches && n.matches(sel)) return n; } catch (e) { return null; }
            }
            return null;
          };

          // Every input under a node, in page order, looking into open shadow roots. No recursion,
          // so a page nested very deep cannot overflow the stack.
          const inputsIn = root => {
            const out = [];
            const stack = [root.documentElement || root];
            while (stack.length) {
              const n = stack.pop();
              if (n.tagName === 'INPUT') out.push(n);
              const kids = [];
              if (n.shadowRoot) for (let c = n.shadowRoot.firstElementChild; c; c = c.nextElementSibling) kids.push(c);
              for (let c = n.firstElementChild; c; c = c.nextElementSibling) kids.push(c);
              for (let i = kids.length - 1; i >= 0; i--) stack.push(kids[i]);
            }
            return out;
          };

          const target = e => {
            const path = e.composedPath ? e.composedPath() : null;
            return (path && path[0]) || e.target;
          };

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

          const TEXTY = new Set(['text', 'email', 'tel']);

          // ---- rules for single sites (pass/rules.json) ----

          const rulesNow = () => {
            const out = { exclude: [], login: [] };
            try {
              const host = location.hostname.toLowerCase();
              const path = location.pathname;
              for (const key of Object.keys(RULES)) {
                const slash = key.indexOf('/');
                const h = (slash < 0 ? key : key.slice(0, slash)).toLowerCase();
                const p = slash < 0 ? '' : key.slice(slash);
                if (!h || (host !== h && !host.endsWith('.' + h))) continue;
                if (p && !path.startsWith(p)) continue;
                const r = RULES[key];
                if (r && Array.isArray(r.exclude)) out.exclude.push(...r.exclude.filter(x => typeof x === 'string'));
                if (r && Array.isArray(r.login)) out.login.push(...r.login);
              }
            } catch (e) {}
            return out;
          };

          // A path of selectors, each one looked for inside the one before it: in its own tree first,
          // then in its shadow root. A single selector may be given as a plain string.
          const find = path => {
            let node = document;
            for (const sel of (typeof path === 'string' ? [path] : (Array.isArray(path) ? path : []))) {
              if (typeof sel !== 'string') return null;
              let hit = null;
              try { hit = node.querySelector(sel) || (node.shadowRoot && node.shadowRoot.querySelector(sel)); } catch (e) {}
              if (!hit) return null;
              node = hit;
            }
            return node === document ? null : node;
          };

          // Inputs of a page or form, minus whatever the site's rules leave out.
          const inputs = root => {
            const all = inputsIn(root);
            const ex = rulesNow().exclude;
            return ex.length ? all.filter(i => !ex.some(s => closestDeep(i, s))) : all;
          };

          // ---- what a field is ----

          const PWNAME = /pass(word|wd)|mật khẩu/i;
          const attrs = i => (i.name || '') + ' ' + (i.id || '') + ' ' + (i.getAttribute('aria-label') || '') + ' ' + (i.getAttribute('placeholder') || '');

          // A password field: type password, drawn masked, or switched to text to be shown but still
          // marked as a password. (The same test is in HasPassword below.)
          const isPw = i => {
            if (i.type === 'password') return true;
            if (i.type !== 'text') return false;
            const mark = (i.getAttribute('autocomplete') || '').toLowerCase();
            if (mark === 'current-password' || mark === 'new-password') return true;
            try {
              const t = getComputedStyle(i).webkitTextSecurity;
              if (t && t !== 'none') return true;
            } catch (e) {}
            return PWNAME.test(attrs(i));
          };

          const userFor = (pw, list) => {
            const before = list.slice(0, Math.max(0, list.indexOf(pw))).filter(i =>
              TEXTY.has(i.type) && !isPw(i) && visible(i) && i.value.trim().length > 0 && i.value.length <= MAX);
            if (!before.length) return '';
            const marked = before.filter(i => /(^|\s)(username|email)(\s|$)/i.test(i.getAttribute('autocomplete') || ''));
            return (marked.length ? marked[marked.length - 1] : before[before.length - 1]).value.trim();
          };

          // A user name left in a field the page hides, marked as the user name the way password
          // managers expect: a password page often keeps the user name of the step before there.
          const hiddenUser = list => {
            const found = list.filter(i =>
              !visible(i) && !isPw(i) && i.value.trim().length > 0 && i.value.length <= MAX &&
              /(^|\s)username(\s|$)/i.test(i.getAttribute('autocomplete') || ''));
            return found.length ? found[found.length - 1].value.trim() : '';
          };

          // The first step of a two-step login: a page with no password field and a filled field that
          // looks like a user name. A search box or a plain text box does not look like one. Besides
          // type, autocomplete, name and id, what the field says about itself counts (aria-label,
          // placeholder), unless it talks about searching.
          const IDENT = /user|e-?mail|login|ident|account|phone|tel/i;
          const SAYS = /user ?name|e-?mail|log.?in|phone|identifier|đăng nhập|tài khoản|điện thoại/i;
          const SEARCH = /search|tìm kiếm/i;
          const looksUser = i => {
            if (i.type === 'email' || /(^|\s)(username|email)(\s|$)/i.test(i.getAttribute('autocomplete') || '')) return true;
            if (IDENT.test((i.name || '') + ' ' + (i.id || ''))) return true;
            const says = (i.getAttribute('aria-label') || '') + ' ' + (i.getAttribute('placeholder') || '');
            return SAYS.test(says) && !SEARCH.test(says);
          };

          let lastStep = null;
          const step = list => {
            const found = list.filter(i =>
              TEXTY.has(i.type) && !isPw(i) && visible(i) && i.value.trim().length > 0 && i.value.length <= MAX && looksUser(i));
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

          // The login a site's own rule names, if its password field is on show and filled. The user
          // name field of a rule may be hidden: that is what a rule is often for.
          const ruled = () => {
            for (const r of rulesNow().login) {
              if (!r || !r.pass) continue;
              const pw = find(r.pass);
              if (!pw || pw.tagName !== 'INPUT' || !visible(pw) || pw.value.length === 0 || pw.value.length > MAX) continue;
              const uf = r.user ? find(r.user) : null;
              const u = uf && uf.tagName === 'INPUT' && uf.value.trim().length <= MAX ? uf.value.trim() : '';
              return { pw: pw, u: u };
            }
            return null;
          };

          // ---- which password is which, and what kind of form it is ----

          const squash = t => (t || '').replace(/\s+/g, ' ').trim().toLowerCase().slice(0, 120);

          // What a field says about itself: its label, placeholder, name and id.
          const labelsOf = i => {
            const out = [i.getAttribute('aria-label'), i.getAttribute('placeholder'), i.name, i.id];
            try { for (const l of Array.from(i.labels || [])) out.push(l.textContent); } catch (e) {}
            try {
              const root = i.getRootNode();
              for (const id of (i.getAttribute('aria-labelledby') || '').split(/\s+/)) {
                const el = id && root.getElementById ? root.getElementById(id) : null;
                if (el) out.push(el.textContent);
              }
            } catch (e) {}
            // Names are written new_password or newPassword: made into words, so \b can see them.
            return out.map(t => squash((t || '').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_.\[\]]+/g, ' '))).join(' ');
          };

          const RE_CONFIRM = /\b(confirm|repeat|retype|re-?enter|again|verify)\b|nhập lại|xác nhận|lặp lại/;
          const RE_CURRENT = /\b(current|existing|old|previous)\b|hiện tại|mật khẩu cũ/;
          const RE_NEW = /\b(new|create|choose)\b|mật khẩu mới/;

          // 'cur' (the password in use now), 'new', 'confirm', or '' when nothing says.
          const roleOf = i => {
            const mark = (i.getAttribute('autocomplete') || '').toLowerCase();
            const text = labelsOf(i);
            if (RE_CONFIRM.test(text)) return 'confirm';
            if (mark === 'new-password' || RE_NEW.test(text)) return 'new';
            if (mark === 'current-password' || RE_CURRENT.test(text)) return 'cur';
            return '';
          };

          const RE_LOGIN = /\b(log ?in|sign ?in|login|welcome back)\b|đăng nhập/;
          const RE_REGISTER = /\b(sign ?up|register|create (an |your )?account|join|get started|new account)\b|đăng ký|tạo tài khoản/;
          const RE_CHANGE = /\b(change|update|reset|set|choose|create) (a |your |the |new )*password\b|\bpassword (change|reset|update)\b|đổi mật khẩu|đặt lại mật khẩu|cập nhật mật khẩu/;
          const RE_FORGOT = /forgot|lost (your )?password|quên mật khẩu/;
          const RE_REMEMBER = /remember|keep me|stay (signed|logged)|ghi nhớ|duy trì/;
          const RE_TERMS = /\bterms\b|privacy|i agree|điều khoản|chính sách|tôi đồng ý/;

          // The kind of form, from its own words: 'login', 'register', 'change', or '' when it is not
          // clear (the two best kinds are less than 2 points apart). It only reads text the person
          // can see and counts a button's label most. This is the one place that judges a form, so a
          // better judge can be put here without touching the rest.
          const classify = container => {
            const root = container && container.querySelectorAll ? container : document;
            const score = { login: 0, register: 0, change: 0 };
            const vote = (text, points) => {
              if (RE_LOGIN.test(text)) score.login += points;
              if (RE_REGISTER.test(text)) score.register += points;
              if (RE_CHANGE.test(text)) score.change += points;
            };

            // Headings in the form, or in the nearest parent that has any (up to three levels up).
            const HEAD = 'h1, h2, h3, h4, legend, [role="heading"]';
            let heads = [];
            for (let n = root, k = 0; n && k < 4; n = n.parentElement || n.host || null, k++) {
              heads = Array.from(n.querySelectorAll(HEAD)).filter(visible).slice(0, 3);
              if (heads.length || n === document) break;
            }
            heads.forEach(h => vote(squash(h.textContent), 2));
            vote(squash(document.title), 1);

            const label = el => squash((el.getAttribute('aria-label') || '') + ' ' + (el.tagName === 'INPUT' ? el.value || '' : '') + ' ' + (el.textContent || ''));
            Array.from(root.querySelectorAll('button, input[type="submit"], [role="button"]')).filter(visible).slice(0, 6)
              .forEach(b => vote(label(b), 3));

            const links = Array.from(root.querySelectorAll('a[href], [role="link"]')).filter(visible).slice(0, 12).map(a => squash(a.textContent));
            if (links.some(t => RE_FORGOT.test(t))) score.login += 2;

            const boxes = Array.from(root.querySelectorAll('input[type="checkbox"]')).filter(visible).slice(0, 6)
              .map(c => squash(labelsOf(c) + ' ' + (c.parentElement ? c.parentElement.textContent : '')));
            if (boxes.some(t => RE_REMEMBER.test(t))) score.login += 2;
            if (boxes.some(t => RE_TERMS.test(t)) || (root !== document && RE_TERMS.test(squash(root.textContent)))) score.register += 2;

            const roles = shownPw(root).map(roleOf);
            if (roles.includes('cur') && roles.includes('new')) score.change += 3;
            if (roles.includes('confirm')) { score.register += 1; score.change += 1; }

            const ranked = Object.keys(score).sort((a, b) => score[b] - score[a]);
            return score[ranked[0]] > 0 && score[ranked[0]] - score[ranked[1]] >= 2 ? ranked[0] : '';
          };

          const shownPw = root => inputs(root).filter(i => isPw(i) && visible(i));

          let seq = 0;
          let last = null;
          // strict: the click came from something that is not a button, so it only counts when a
          // filled password field is on show (it never starts the guess about a first step).
          const offer = (container, strict) => {
            const root = container && container.querySelectorAll ? container : document;
            const rule = ruled();
            const list = inputs(rule ? document : root);
            let pick = null;
            let old = null;
            let u = '';
            if (rule) {
              pick = rule.pw;
              u = rule.u;
            } else {
              const shown = list.filter(i => isPw(i) && visible(i));
              if (shown.length === 0) { if (!strict) step(list); return; }
              const pws = shown.filter(i => i.value.length > 0);
              const current = pws.filter(i => roleOf(i) === 'cur');
              const fresh = pws.filter(i => roleOf(i) === 'new' || roleOf(i) === 'confirm');
              if (current.length === 1 && fresh.length >= 1 && fresh.every(i => i.value === fresh[0].value) &&
                  fresh[0].value !== current[0].value) {
                // A change-password form that says which field is which: the old password and the new one.
                old = current[0].value;
                pick = fresh[0];
              } else if (pws.length === 1) pick = pws[0];
              else if (pws.length === 2 && pws[0].value === pws[1].value) pick = pws[0];
              else if (pws.length === 2 && pws.every(i => roleOf(i) === '') && classify(root) === 'change') {
                // Nothing marks the two fields, but the form says it changes the password: the old one, then the new.
                old = pws[0].value;
                pick = pws[1];
              }
              else if (pws.length === 3 && pws[1].value === pws[2].value && pws[0].value !== pws[1].value) {
                // Old password, then the new one twice.
                old = pws[0].value;
                pick = pws[1];
              }
            }
            if (!pick || pick.value.length > MAX || (old !== null && old.length > MAX)) return;
            u = u || userFor(pick, list) || hiddenUser(list);
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

          // Buttons that are not a sign-in: closing a dialog after typing, going back, or the eye
          // that shows the password. Only a short label counts (a long sentence is not a button name).
          const NOT_SIGNIN = /\b(cancel|close|dismiss|back)\b|\b(show|hide|reveal)\b.*\bpassword\b|\bpassword\b.*\b(show|hide|visible)\b|hủy|huỷ|đóng|quay lại|hiện mật khẩu|ẩn mật khẩu|取消|キャンセル|annuler|abbrechen|cancelar|fermer|schlie[sß]en|cerrar/i;
          const notSignin = el => {
            const label = ((el.getAttribute('aria-label') || '') + ' ' + (el.getAttribute('title') || '') + ' ' +
              (el.tagName === 'INPUT' ? el.value || '' : '') + ' ' + (el.textContent || '')).replace(/\s+/g, ' ').trim();
            return label.length <= 40 && NOT_SIGNIN.test(label);
          };

          // Something that is not a button but is made to be clicked like one (a styled div or span).
          const clickable = t => {
            if (closestDeep(t, 'a[href], label, input, select, textarea, option')) return null;
            let n = t;
            for (let i = 0; n && i < 4; i++, n = up(n)) {
              if (n.tagName === 'FORM' || n.tagName === 'BODY' || n.tagName === 'HTML') return null;
              try { if (getComputedStyle(n).cursor === 'pointer') return n; } catch (e) {}
            }
            return null;
          };

          document.addEventListener('click', e => {
            if (!e.isTrusted) return;
            const t = target(e);
            if (!t || !t.matches) return;
            let el = closestDeep(t, 'button, input[type="submit"], input[type="image"], [role="button"]');
            let strict = false;
            if (!el) { el = clickable(t); strict = true; }
            if (!el || (el.tagName === 'BUTTON' && el.type === 'reset')) return;
            if (notSignin(el)) return;
            offer(closestDeep(el, 'form') || document, strict);
          }, true);

          document.addEventListener('keydown', e => {
            if (!e.isTrusted || e.isComposing || e.key !== 'Enter') return;
            const el = target(e);
            if (!el || el.tagName !== 'INPUT') return;
            offer(closestDeep(el, 'form') || document);
          }, true);
        })();
        """;

    /// <summary>
    /// Run by the window, after a page has loaded, to find out whether a password field is on
    /// show: a login that worked leads to a page without one. The answer is true or false.
    /// (The visibility and password tests are the same as in <see cref="Source"/>.)
    /// </summary>
    public const string HasPassword = """
        (() => {
          try {
            const up = n => n.parentElement || (n.getRootNode && n.getRootNode().host) || null;
            const visible = el => {
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
            const isPw = i => {
              if (i.type === 'password') return true;
              if (i.type !== 'text') return false;
              const mark = (i.getAttribute('autocomplete') || '').toLowerCase();
              if (mark === 'current-password' || mark === 'new-password') return true;
              try {
                const t = getComputedStyle(i).webkitTextSecurity;
                if (t && t !== 'none') return true;
              } catch (e) {}
              return /pass(word|wd)|mật khẩu/i.test((i.name || '') + ' ' + (i.id || '') + ' ' + (i.getAttribute('aria-label') || '') + ' ' + (i.getAttribute('placeholder') || ''));
            };
            const stack = [document.documentElement];
            while (stack.length) {
              const n = stack.pop();
              if (n.tagName === 'INPUT' && isPw(n) && visible(n)) return true;
              if (n.shadowRoot) for (let c = n.shadowRoot.firstElementChild; c; c = c.nextElementSibling) stack.push(c);
              for (let c = n.firstElementChild; c; c = c.nextElementSibling) stack.push(c);
            }
            return false;
          } catch (e) {
            return true;
          }
        })();
        """;
}
