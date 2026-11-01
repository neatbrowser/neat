'use strict';
// Runs the script that puts a one-time code into a page (OtpScript in src/neat/pass/otp.cs) against
// made-up pages, in jsdom. Plain asserts, one line per check, exit code 1 on any failure.

const fs = require('fs');
const path = require('path');
const { JSDOM, VirtualConsole } = require('jsdom');

const file = path.join(__dirname, '..', '..', 'src', 'neat', 'pass', 'otp.cs');
const cs = fs.readFileSync(file, 'utf8').split(/\r?\n/);

function extract(name) {
  const start = cs.findIndex(l => l.includes(name + ' = """'));
  if (start < 0) throw new Error('no ' + name + ' in otp.cs');
  const end = cs.findIndex((l, i) => i > start && l.trim() === '""";');
  const indent = cs[end].match(/^\s*/)[0].length;
  return cs.slice(start + 1, end).map(l => l.slice(indent)).join('\n');
}

const SOURCE = extract('Source');
const ORIGIN = 'https://example.com';
let ran = 0;
let failed = 0;

function check(name, fn) {
  ran++;
  try { fn(); console.log('ok    ' + name); }
  catch (e) { failed++; console.log('FAIL  ' + name + '\n        ' + e.message); }
}
function expect(cond, what) { if (!cond) throw new Error(what); }

function load(html, url = ORIGIN + '/2fa') {
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
    put: (origin, code, w = win) => w.eval('(' + SOURCE + ')(' + JSON.stringify({ o: origin, p: code }) + ');'),
  };
}

check('the focused code field is filled, with input and change events', () => {
  const p = load('<form><input id="c" type="text" autocomplete="one-time-code"><input id="x" type="text"></form>');
  p.$('#c').focus();
  expect(p.put(ORIGIN, '287082') === 'otp', 'result');
  expect(p.val('#c') === '287082' && p.val('#x') === '', 'values');
  expect(p.events.join() === 'input:c,change:c', 'events: ' + p.events.join());
});

check('a code split in boxes goes one digit to a box, when the boxes are as many as the digits', () => {
  const boxes = n => '<form>' + Array.from({ length: n }, (_, i) => `<input id="b${i}" type="text" maxlength="1">`).join('') + '</form>';
  const p = load(boxes(6));
  p.$('#b2').focus();
  expect(p.put(ORIGIN, '287082') === 'otp+boxes', 'result');
  expect(Array.from({ length: 6 }, (_, i) => p.val('#b' + i)).join('') === '287082', 'digits');

  // Eight boxes for a six digit code: not clear what goes where, so only the focused box gets the whole code.
  const q = load(boxes(8));
  q.$('#b0').focus();
  expect(q.put(ORIGIN, '287082') === 'otp' && q.val('#b0') === '287082' && q.val('#b1') === '', 'boxes that do not match');
});

check('nothing is filled in a password field, a read-only or hidden field, or with no focus', () => {
  const p = load('<input id="p" type="password"><input id="r" type="text" readonly><input id="h" type="text" style="display:none">');
  p.$('#p').focus();
  expect(p.put(ORIGIN, '1') === 'none' && p.val('#p') === '', 'password field');
  p.$('#r').focus();
  expect(p.put(ORIGIN, '1') === 'none' && p.val('#r') === '', 'read-only field');
  expect(p.put(ORIGIN, '1') === 'none', 'no focus on a usable field');
});

check('another origin and a frame get nothing', () => {
  const p = load('<input id="c" type="text">');
  p.$('#c').focus();
  expect(p.put('https://evil.example', '123456') === 'origin' && p.val('#c') === '', 'origin');
  const f = load('<iframe id="fr"></iframe>');
  const frame = f.$('#fr').contentWindow;
  frame.document.body.innerHTML = '<input id="c" type="text">';
  frame.document.querySelector('#c').focus();
  const result = f.put('about:blank', '123456', frame);
  expect(result === 'frame' && frame.document.querySelector('#c').value === '', 'frame: ' + result);
});

check('a field inside a shadow root is filled', () => {
  const p = load('<div id="h"></div>');
  const root = p.$('#h').attachShadow({ mode: 'open' });
  root.innerHTML = '<input id="s" type="text" autocomplete="one-time-code">';
  root.querySelector('#s').focus();
  expect(p.put(ORIGIN, '287082') === 'otp' && root.querySelector('#s').value === '287082', 'not filled');
});

console.log('\n' + (ran - failed) + ' of ' + ran + ' checks passed.');
process.exit(failed === 0 ? 0 : 1);
