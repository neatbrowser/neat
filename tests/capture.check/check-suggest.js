'use strict';
// Runs the script that puts a suggested password into a page (SuggestScript in
// src/neat/pass/suggest.cs) against made-up pages, in jsdom. Plain asserts, one line per check,
// exit code 1 on any failure.

const fs = require('fs');
const path = require('path');
const { JSDOM, VirtualConsole } = require('jsdom');

const file = path.join(__dirname, '..', '..', 'src', 'neat', 'pass', 'suggest.cs');
const cs = fs.readFileSync(file, 'utf8').split(/\r?\n/);

function extract(name) {
  const start = cs.findIndex(l => l.includes(name + ' = """'));
  if (start < 0) throw new Error('no ' + name + ' in suggest.cs');
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

function load(html, url = ORIGIN + '/signup') {
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
    use: (origin, pass) => win.eval('(' + SOURCE + ')(' + JSON.stringify({ o: origin, p: pass }) + ');'),
  };
}

const SIGNUP = `<form onsubmit="return false"><input id="u" type="text" name="user">
  <input id="a" type="password" autocomplete="new-password"><input id="b" type="password" placeholder="Confirm password"></form>`;

check('the focused new-password field and its confirmation are filled, with input and change events', () => {
  const p = load(SIGNUP);
  p.$('#a').focus();
  expect(p.use(ORIGIN, 'Zx9!kq') === 'pass+confirm', 'result');
  expect(p.val('#a') === 'Zx9!kq' && p.val('#b') === 'Zx9!kq' && p.val('#u') === '', 'values');
  expect(p.events.join() === 'input:a,change:a,input:b,change:b', 'events: ' + p.events.join());
});

check('the password in use now, a filled field and the user name are left alone', () => {
  const p = load(`<form onsubmit="return false"><input id="o" type="password" autocomplete="current-password">
    <input id="a" type="password" autocomplete="new-password"><input id="c" type="password" placeholder="Old password">
    <input id="f" type="password"></form>`);
  p.$('#f').value = 'typed already';
  p.$('#a').focus();
  expect(p.use(ORIGIN, 'Zx9!kq') === 'pass', 'result');
  expect(p.val('#a') === 'Zx9!kq' && p.val('#o') === '' && p.val('#c') === '' && p.val('#f') === 'typed already', 'values');
});

check('nothing is filled when the focus is not on a password field, or the field cannot be typed in', () => {
  const p = load(SIGNUP);
  p.$('#u').focus();
  expect(p.use(ORIGIN, 'x') === 'none' && p.val('#a') === '', 'a user name field had the focus');
  const q = load(SIGNUP);
  q.$('#a').readOnly = true; q.$('#a').focus();
  expect(q.use(ORIGIN, 'x') === 'none' && q.val('#a') === '', 'a read-only field was filled');
  const r = load(SIGNUP);
  expect(r.use(ORIGIN, 'x') === 'none', 'no focus at all');
});

check('a field shown as text but marked as a password is filled', () => {
  const p = load(`<form><input id="a" type="text" autocomplete="new-password"></form>`);
  p.$('#a').focus();
  expect(p.use(ORIGIN, 'Zx9!kq') === 'pass' && p.val('#a') === 'Zx9!kq', 'not filled');
});

check('another origin and a frame get nothing', () => {
  const p = load(SIGNUP);
  p.$('#a').focus();
  expect(p.use('https://evil.example', 'x') === 'origin' && p.val('#a') === '', 'origin');
  const f = load('<iframe id="fr"></iframe>');
  const frame = f.$('#fr').contentWindow;
  frame.document.body.innerHTML = SIGNUP;
  frame.document.querySelector('#a').focus();
  const result = frame.eval('(' + SOURCE + ')(' + JSON.stringify({ o: 'about:blank', p: 'x' }) + ');');
  expect(result === 'frame' && frame.document.querySelector('#a').value === '', 'frame: ' + result);
});

check('a password with quotes, backslashes and script text arrives exactly', () => {
  const p = load(SIGNUP);
  p.$('#a').focus();
  const odd = '"\'\\</script>${x}`\n';
  p.use(ORIGIN, odd);
  expect(p.val('#a') === odd.replace(/\n/g, ''), 'got ' + JSON.stringify(p.val('#a')));
});

check('a focused field inside a shadow root is filled', () => {
  const p = load('<div id="h"></div>');
  const root = p.$('#h').attachShadow({ mode: 'open' });
  root.innerHTML = '<input id="s" type="password" autocomplete="new-password">';
  root.querySelector('#s').focus();
  expect(p.use(ORIGIN, 'Zx9!kq') === 'pass' && root.querySelector('#s').value === 'Zx9!kq', 'not filled');
});

console.log('\n' + (ran - failed) + ' of ' + ran + ' checks passed.');
process.exit(failed === 0 ? 0 : 1);
