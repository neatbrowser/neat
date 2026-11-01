'use strict';
// Runs the script that fills a saved card or identity (FormScript in src/neat/pass/form.cs) against
// made-up pages, in jsdom. Plain asserts, one line per check, exit code 1 on any failure.

const fs = require('fs');
const path = require('path');
const { JSDOM, VirtualConsole } = require('jsdom');

const file = path.join(__dirname, '..', '..', 'src', 'neat', 'pass', 'form.cs');
const cs = fs.readFileSync(file, 'utf8').split(/\r?\n/);

function extract(name) {
  const start = cs.findIndex(l => l.includes(name + ' = """'));
  if (start < 0) throw new Error('no ' + name + ' in form.cs');
  const end = cs.findIndex((l, i) => i > start && l.trim() === '""";');
  const indent = cs[end].match(/^\s*/)[0].length;
  return cs.slice(start + 1, end).map(l => l.slice(indent)).join('\n');
}

const SOURCE = extract('Source');
const ORIGIN = 'https://shop.example';
let ran = 0;
let failed = 0;

function check(name, fn) {
  ran++;
  try { fn(); console.log('ok    ' + name); }
  catch (e) { failed++; console.log('FAIL  ' + name + '\n        ' + e.message); }
}
function expect(cond, what) { if (!cond) throw new Error(what); }

function load(html, url = ORIGIN + '/checkout') {
  const dom = new JSDOM(html, { runScripts: 'outside-only', url, virtualConsole: new VirtualConsole() });
  const win = dom.window;
  const events = [];
  win.document.addEventListener('input', e => events.push('input:' + (e.target.id || '?')), true);
  win.document.addEventListener('change', e => events.push('change:' + (e.target.id || '?')), true);
  return {
    win,
    events,
    $: s => win.document.querySelector(s),
    val: s => win.document.querySelector(s).value,
    fill: (origin, kind, d, w = win) => w.eval('(' + SOURCE + ')(' + JSON.stringify({ o: origin, k: kind, d }) + ');'),
  };
}

const CARD = { number: '4111111111111111', holder: 'A Nguyen', expiry: '08/27', cvc: '123' };
const ID = { first: 'An', last: 'Nguyen', email: 'an@example.com', phone: '0901234567', street: '1 Le Loi', city: 'Hue', zip: '49000', country: 'Vietnam' };

check('a card form is filled by the browser marks, with input and change events', () => {
  const p = load(`<form><input id="n" autocomplete="cc-number"><input id="h" autocomplete="cc-name"><input id="e" autocomplete="cc-exp"><input id="c" autocomplete="cc-csc"></form>`);
  p.$('#n').focus();
  expect(p.fill(ORIGIN, 'card', CARD) === 'form:4', 'result');
  expect(p.val('#n') === CARD.number && p.val('#h') === 'A Nguyen' && p.val('#e') === '08/27' && p.val('#c') === '123', 'values');
  expect(p.events.slice(0, 2).join() === 'input:n,change:n', 'events: ' + p.events.join());
});

check('a card form with no marks is read from what the fields say; a month and a year may be selects', () => {
  const months = Array.from({ length: 12 }, (_, i) => `<option value="${i + 1}">${String(i + 1).padStart(2, '0')}</option>`).join('');
  const p = load(`<form><label for="a">Card number</label><input id="a" name="x1"><input id="b" placeholder="Name on card">
    <select id="m" name="exp_month"><option value="">Month</option>${months}</select>
    <input id="y" name="exp_year" maxlength="2" placeholder="YY"><input id="s" name="cvv"></form>`);
  p.$('#a').focus();
  expect(p.fill(ORIGIN, 'card', CARD) === 'form:5', 'result');
  expect(p.val('#a') === CARD.number && p.val('#b') === 'A Nguyen' && p.val('#m') === '8' && p.val('#y') === '27' && p.val('#s') === '123', 'values');
});

check('a four-digit year and an expiry that wants four digits', () => {
  const p = load(`<form><input id="y" autocomplete="cc-exp-year"><input id="e" autocomplete="cc-exp" placeholder="MM/YYYY"></form>`);
  p.$('#y').focus();
  p.fill(ORIGIN, 'card', CARD);
  expect(p.val('#y') === '2027' && p.val('#e') === '08/2027', 'got ' + p.val('#y') + ' and ' + p.val('#e'));
});

check('an identity form is filled, a country select by its text, a name field gets first and last', () => {
  const p = load(`<form><input id="f" autocomplete="given-name"><input id="l" autocomplete="family-name"><input id="e" type="email" autocomplete="email">
    <input id="t" type="tel" autocomplete="tel"><input id="s" autocomplete="street-address"><input id="c" autocomplete="address-level2">
    <input id="z" autocomplete="postal-code"><select id="k" autocomplete="country"><option value="">-</option><option value="VN">Vietnam</option><option value="US">United States</option></select>
    <input id="full" autocomplete="name"></form>`);
  p.$('#f').focus();
  expect(p.fill(ORIGIN, 'identity', ID) === 'form:9', 'result');
  expect(p.val('#f') === 'An' && p.val('#l') === 'Nguyen' && p.val('#e') === 'an@example.com' && p.val('#t') === '0901234567', 'names, email, phone');
  expect(p.val('#s') === '1 Le Loi' && p.val('#c') === 'Hue' && p.val('#z') === '49000' && p.val('#k') === 'VN' && p.val('#full') === 'An Nguyen', 'address');
});

check('a field that already has something is left alone, except the one with the focus', () => {
  const p = load(`<form><input id="f" autocomplete="given-name" value="Typed"><input id="l" autocomplete="family-name" value="Also"><input id="e" autocomplete="email"></form>`);
  p.$('#l').focus();
  p.fill(ORIGIN, 'identity', ID);
  expect(p.val('#f') === 'Typed' && p.val('#l') === 'Nguyen' && p.val('#e') === 'an@example.com', 'got ' + p.val('#f') + ',' + p.val('#l'));
});

check('only the form with the focus is filled, and a password field is never touched', () => {
  const p = load(`<form id="a"><input id="n" autocomplete="cc-number"></form><form id="b"><input id="n2" autocomplete="cc-number"><input id="pw" type="password" name="card_number"></form>`);
  p.$('#n').focus();
  expect(p.fill(ORIGIN, 'card', CARD) === 'form:1' && p.val('#n') === CARD.number && p.val('#n2') === '' && p.val('#pw') === '', 'wrong fields');
});

check('read-only, hidden and disabled fields are not filled, and nothing is said of a page with no form', () => {
  const p = load(`<form><input id="a" autocomplete="cc-number" readonly><input id="b" autocomplete="cc-name" style="display:none"><input id="c" autocomplete="cc-csc" disabled></form>`);
  p.$('#a').focus();
  expect(p.fill(ORIGIN, 'card', CARD) === 'none' && p.val('#a') === '' && p.val('#b') === '' && p.val('#c') === '', 'something was filled');
});

check('another origin and a frame get nothing', () => {
  const p = load(`<form><input id="n" autocomplete="cc-number"></form>`);
  p.$('#n').focus();
  expect(p.fill('https://evil.example', 'card', CARD) === 'origin' && p.val('#n') === '', 'origin');
  const f = load('<iframe id="fr"></iframe>');
  const frame = f.$('#fr').contentWindow;
  frame.document.body.innerHTML = '<form><input id="n" autocomplete="cc-number"></form>';
  const result = f.fill('about:blank', 'card', CARD, frame);
  expect(result === 'frame' && frame.document.querySelector('#n').value === '', 'frame: ' + result);
});

check('awkward values arrive exactly, because they are data and not text', () => {
  const p = load(`<form><input id="h" autocomplete="cc-name"></form>`);
  p.$('#h').focus();
  const odd = '"\'\\</script>${x}`';
  p.fill(ORIGIN, 'card', { holder: odd });
  expect(p.val('#h') === odd, 'got ' + p.val('#h'));
});

check('an email word is not an address: "Email address" is the email, not the street', () => {
  const p = load(`<form><input id="e" placeholder="Email address"><input id="s" placeholder="Street address"></form>`);
  p.$('#e').focus();
  p.fill(ORIGIN, 'identity', ID);
  expect(p.val('#e') === 'an@example.com' && p.val('#s') === '1 Le Loi', 'got ' + p.val('#e') + ' / ' + p.val('#s'));
});

console.log('\n' + (ran - failed) + ' of ' + ran + ' checks passed.');
process.exit(failed === 0 ? 0 : 1);
