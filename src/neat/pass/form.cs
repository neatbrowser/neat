using System.Text.Json;

namespace neat;

/// <summary>
/// The script that puts a saved card or identity into the form of the page the person is looking at, when
/// they pick it in the list at one of its fields (win.field.cs). It keeps the rules of the fill script
/// (pass/fill.cs): the values go in as data, the page's origin is the one the window saw (checked from inside
/// the page, so a navigation in between gets nothing), the top page only, only fields the person can see and
/// type in, values set the way the browser does with input and change events fired.
///
/// Which field is which is read from the browser's own mark (autocomplete: cc-number, cc-exp, given-name, ...)
/// or, where there is none, from what the field says about itself (label, placeholder, name). Only fields of
/// the form that holds the focus (the page, when the field has no form) are looked at, and a field that already
/// has something typed in is left alone, except the one with the focus. Selects are set by their option's value
/// or text (a month, a year, a country). It returns the number of fields filled as a word: none, or form:N,
/// or origin or frame. It is JavaScript in a C# string and is run by tests/capture.check against made-up pages,
/// so keep it between its own """ marks.
/// </summary>
internal static class FormScript
{
    public static string For(string origin, string kind, IReadOnlyDictionary<string, string> fields)
    {
        var data = JsonSerializer.Serialize(new { o = origin, k = kind, d = fields });
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

            const TOKENS = {
              'cc-number': 'number', 'cc-name': 'holder', 'cc-exp': 'expiry', 'cc-exp-month': 'month', 'cc-exp-year': 'year', 'cc-csc': 'cvc',
              'given-name': 'first', 'family-name': 'last', 'name': 'name', 'email': 'email', 'tel': 'phone', 'tel-national': 'phone',
              'organization': 'org', 'street-address': 'street', 'address-line1': 'street', 'address-line2': 'street2',
              'address-level2': 'city', 'address-level1': 'state', 'postal-code': 'zip', 'country': 'country', 'country-name': 'country',
            };
            const WORDS = [
              [/\b(card ?number|credit card|cc ?num(ber)?)\b/, 'number'],
              [/\b(name on card|card ?holder)\b/, 'holder'],
              [/\b(cvc|cvv|csc|card code|card security code)\b/, 'cvc'],
              [/\b(exp\w* ?(month|mm)|card ?month)\b/, 'month'],
              [/\b(exp\w* ?(year|yy)|card ?year)\b/, 'year'],
              [/\b(expiry|expiration|exp date|valid thru|mm ?\/ ?yy)\b/, 'expiry'],
              [/\b(first|given) ?name\b/, 'first'], [/\b(last|family|sur) ?name\b/, 'last'], [/\be-?mail\b/, 'email'],
              [/\b(phone|mobile|telephone)\b/, 'phone'], [/\b(zip|postal|post ?code)\b/, 'zip'], [/\b(city|town)\b/, 'city'],
              [/\b(street|address)\b/, 'street'], [/\b(state|province|region)\b/, 'state'], [/\bcountry\b/, 'country'],
              [/\b(company|organi[sz]ation)\b/, 'org'], [/\bsecurity code\b/, 'cvc'],
            ];
            const squash = t => (t || '').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_.\[\]]+/g, ' ').replace(/\s+/g, ' ').trim().toLowerCase();
            const saysOf = el => {
              const out = [el.getAttribute('aria-label'), el.getAttribute('placeholder'), el.name, el.id];
              try { for (const l of Array.from(el.labels || [])) out.push(l.textContent); } catch (e) {}
              return out.map(squash).join(' ');
            };
            const roleOf = el => {
              const token = (el.getAttribute('autocomplete') || '').toLowerCase().trim().split(/\s+/).pop();
              if (TOKENS[token]) return TOKENS[token];
              const text = saysOf(el);
              for (const [re, role] of WORDS) if (re.test(text)) return role;
              return '';
            };

            const d = a.d || {};
            const two = n => String(n).padStart(2, '0');

            // What a field of a role gets, given the field itself (a year may be 2 or 4 digits long).
            const valueFor = (role, el) => {
              const exp = /^(\d{2})\/(\d{2})$/.exec(d.expiry || '');
              const long = el.maxLength >= 7 || /yyyy/i.test(el.getAttribute('placeholder') || '');
              switch (role) {
                case 'expiry': return exp ? exp[1] + '/' + (long ? '20' + exp[2] : exp[2]) : '';
                case 'month': return exp ? exp[1] : '';
                case 'year': return exp ? (el.maxLength === 2 || (el.tagName === 'INPUT' && /yy$/i.test(el.getAttribute('placeholder') || '')) ? exp[2] : '20' + exp[2]) : '';
                case 'name': return a.k === 'card' ? (d.holder || '') : [d.first, d.last].filter(Boolean).join(' ');
                case 'holder': return d.holder || [d.first, d.last].filter(Boolean).join(' ');
                default: return d[role] || '';
              }
            };

            const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
            const fire = el => {
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
            };
            const choose = (el, v, role) => {
              const want = String(v).toLowerCase();
              const num = /^\d+$/.test(want) ? String(parseInt(want, 10)) : null;
              const opts = Array.from(el.options);
              const hit = opts.find(o => o.value.toLowerCase() === want || o.text.trim().toLowerCase() === want) ||
                (num !== null && opts.find(o => /^\d+$/.test(o.value) && String(parseInt(o.value, 10)) === num)) ||
                (role === 'country' && opts.find(o => o.text.trim().toLowerCase().startsWith(want)));
              if (!hit) return false;
              el.value = hit.value;
              fire(el);
              return true;
            };

            let at = document.activeElement;
            while (at && at.shadowRoot && at.shadowRoot.activeElement) at = at.shadowRoot.activeElement;
            const scope = (at && at.form) || document;

            const fields = Array.from(scope.querySelectorAll('input, select')).filter(i =>
              i.tagName === 'SELECT' ? usable(i) : (['text', 'tel', 'email', 'number', 'search', ''].includes(i.type) && usable(i)));

            let n = 0;
            for (const el of fields) {
              const role = roleOf(el);
              if (!role) continue;
              const v = valueFor(role, el);
              if (!v) continue;
              if (el.tagName === 'SELECT') {
                if (choose(el, v, role)) n++;
              } else if (el === at || el.value === '') {
                setter.call(el, v);
                fire(el);
                n++;
              }
            }

            return n === 0 ? 'none' : 'form:' + n;
          } catch (e) {
            return 'error';
          }
        }
        """;
}
