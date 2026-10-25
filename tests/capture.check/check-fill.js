'use strict';
// Runs the fill script (src/neat/pass/fill.cs) against made-up pages, in jsdom. Plain asserts,
// one line per check, exit code 1 on any failure.
//
// jsdom has no layout, so the checks that depend on size and position stub the rectangles. The
// script is applied to its data the way the C# code does it: "(" + Source + ")(" + json + ");".

const fs = require('fs');
const path = require('path');
const { JSDOM, VirtualConsole } = require('jsdom');

const file = path.join(__dirname, '..', '..', 'src', 'neat', 'pass', 'fill.cs');
const cs = fs.readFileSync(file, 'utf8').split(/\r?\n/);

function extract(name) {
  const start = cs.findIndex(l => l.includes(name + ' = """'));
  if (start < 0) throw new Error('no ' + name + ' in fill.cs');
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

function load(html, url = ORIGIN + '/login') {
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
    fill: (origin, user, pass, w = win) => w.eval('(' + SOURCE + ')(' + JSON.stringify({ o: origin, u: user, p: pass }) + ');'),
  };
}

// Gives elements a size and place, so the layout part of the visibility test runs.
function layout(win, rects) {
  const zero = { left: 0, top: 0, width: 0, height: 0, right: 0, bottom: 0 };
  const rect = (l, t, w, h) => ({ left: l, top: t, width: w, height: h, right: l + w, bottom: t + h });
  const map = new Map();
  for (const [sel, r] of Object.entries(rects)) map.set(win.document.querySelector(sel), rect(...r));
  const real = win.Element.prototype.getBoundingClientRect;
  win.Element.prototype.getBoundingClientRect = function () {
    if (this === win.document.documentElement) return rect(0, 0, 1000, 800);
    return map.get(this) || zero;
  };
  return () => { win.Element.prototype.getBoundingClientRect = real; };
}

const FORM = `<form id="f" onsubmit="return false">
  <input id="u" type="text" name="user"><input id="p" type="password"><button id="go">Sign in</button></form>`;

check('a login form is filled, and the page is told with input and change events', () => {
  const p = load(FORM);
  expect(p.fill(ORIGIN, 'alice', 'hunter2') === 'user+pass', 'result');
  expect(p.val('#u') === 'alice' && p.val('#p') === 'hunter2', 'values');
  expect(p.events.join() === 'input:u,change:u,input:p,change:p', 'events: ' + p.events.join());
});

check('the value is set through the real setter, so a page that wraps value still sees it (React)', () => {
  const p = load(FORM);
  const el = p.$('#p');
  let seenByPage = null;
  // What React does: a value property on the element itself, tracking what it thinks the value is.
  let tracked = '';
  Object.defineProperty(el, 'value', { configurable: true, get() { return tracked; }, set(v) { tracked = v; } });
  el.addEventListener('input', () => { seenByPage = Object.getOwnPropertyDescriptor(p.win.HTMLInputElement.prototype, 'value').get.call(el); });
  p.fill(ORIGIN, '', 'secret');
  expect(seenByPage === 'secret', 'the page saw: ' + seenByPage);
  expect(tracked === '', 'the page\'s own tracker was bypassed, as the browser does for typing: ' + tracked);
});

check('a page with another origin is not filled, whatever it is asked', () => {
  for (const asked of ['https://evil.example', 'http://example.com', 'https://example.com:8443', 'https://www.example.com', 'https://example.com.']) {
    const p = load(FORM);
    expect(p.fill(asked, 'alice', 'secret') === 'origin', 'asked for ' + asked);
    expect(p.val('#u') === '' && p.val('#p') === '' && p.events.length === 0, 'something was filled for ' + asked);
  }
  const http = load(FORM, 'http://example.com/login');
  expect(http.fill('https://example.com', 'a', 'b') === 'origin', 'an https login on an http page');
  expect(http.val('#p') === '', 'filled an http page with an https login');
  expect(http.fill('http://example.com', 'a', 'b') === 'user+pass', '(sanity) an http login on its own http page');
});

check('a frame is never filled', () => {
  const p = load('<iframe id="fr"></iframe>');
  const frame = p.$('#fr').contentWindow;
  frame.document.body.innerHTML = FORM;
  const result = frame.eval('(' + SOURCE + ')(' + JSON.stringify({ o: 'about:blank', u: 'alice', p: 'secret' }) + ');');
  expect(result === 'frame', 'result: ' + result);
  expect(frame.document.querySelector('#p').value === '', 'a frame was filled');
});

check('hidden fields are not touched: display, visibility, opacity, the hidden attribute', () => {
  const p = load(`<form onsubmit="return false">
    <input id="hu" type="text" name="user" style="display:none">
    <input id="hp" type="password" style="visibility:hidden">
    <div hidden><input id="hp2" type="password"></div>
    <div style="opacity:0"><input id="hp3" type="password"></div>
    <input id="u" type="text"><input id="p" type="password"></form>`);
  expect(p.fill(ORIGIN, 'alice', 'secret') === 'user+pass', 'result');
  expect(p.val('#p') === 'secret' && p.val('#u') === 'alice', 'the visible fields were not used');
  for (const id of ['#hu', '#hp', '#hp2', '#hp3']) expect(p.val(id) === '', id + ' was filled');

  const only = load('<form onsubmit="return false"><input type="password" style="display:none"></form>');
  expect(only.fill(ORIGIN, 'a', 'b') === 'none', 'a page with only hidden fields');
});

check('fields with no size, or pushed off the page, are not filled (a honeypot)', () => {
  // The traps come first in the page: if the script took any of them for a real field it would
  // fill that one, because it fills the first password field it accepts.
  const p = load(`<form onsubmit="return false"><input id="zero" type="password"><input id="left" type="password">
    <input id="top" type="password"><input id="right" type="password"><input id="u" type="text"><input id="p" type="password"></form>`);
  const undo = layout(p.win, {
    '#zero': [10, 90, 0, 0], '#left': [-9999, 120, 200, 24], '#top': [10, -9999, 200, 24], '#right': [99999, 120, 200, 24],
    '#u': [10, 10, 200, 24], '#p': [10, 50, 200, 24],
  });
  expect(p.fill(ORIGIN, 'alice', 'secret') === 'user+pass', 'result');
  expect(p.val('#p') === 'secret' && p.val('#u') === 'alice', 'the real fields were not the ones filled');
  for (const id of ['#zero', '#left', '#top', '#right']) expect(p.val(id) === '', id + ' was filled');
  undo();

  const traps = load('<form onsubmit="return false"><input id="left" type="password"></form>');
  layout(traps.win, { '#left': [-500, 10, 200, 24] });
  expect(traps.fill(ORIGIN, 'a', 'b') === 'none' && traps.val('#left') === '', 'a page with only an off-page field');

  const right = load('<form onsubmit="return false"><input id="far" type="password"></form>');
  layout(right.win, { '#far': [5000, 10, 200, 24] });
  expect(right.fill(ORIGIN, 'a', 'b') === 'none' && right.val('#far') === '', 'a page with only a field far off to the right');

  const small = load('<form onsubmit="return false"><input id="dot" type="password"></form>');
  layout(small.win, { '#dot': [10, 10, 0.5, 0.5] });
  expect(small.fill(ORIGIN, 'a', 'b') === 'none' && small.val('#dot') === '', 'a field smaller than a pixel');
});

check('read-only and disabled fields are left alone', () => {
  const p = load('<form onsubmit="return false"><input id="u" type="text" readonly><input id="p" type="password" disabled></form>');
  expect(p.fill(ORIGIN, 'alice', 'secret') === 'none', 'result');
  expect(p.val('#u') === '' && p.val('#p') === '', 'filled');
});

check('a password page (second step) takes the password and leaves the hidden user name alone', () => {
  const p = load(`<form onsubmit="return false"><input id="hu" type="email" autocomplete="username" style="display:none" value="kept">
    <input id="p" type="password"></form>`);
  expect(p.fill(ORIGIN, 'alice@gmail.com', 'the-password') === 'pass', 'result');
  expect(p.val('#p') === 'the-password' && p.val('#hu') === 'kept', 'values');
});

check('a user name page (first step) takes only the user name', () => {
  const p = load(`<form onsubmit="return false"><input id="s" type="search" name="q"><input id="e" type="email" name="identifier"><button>Next</button></form>`);
  expect(p.fill(ORIGIN, 'alice@gmail.com', 'the-password') === 'user', 'result');
  expect(p.val('#e') === 'alice@gmail.com' && p.val('#s') === '', 'values');

  const plain = load('<form onsubmit="return false"><input id="q" type="text" name="q"><input id="c" type="text" name="comment"></form>');
  expect(plain.fill(ORIGIN, 'alice', 'secret') === 'none', 'a page with a search box and a comment box');
  expect(plain.val('#q') === '' && plain.val('#c') === '', 'a box that is not a user name was filled');
});

check('a field for a NEW password (a sign-up form) is not filled', () => {
  const p = load('<form onsubmit="return false"><input id="u" type="text"><input id="p" type="password" autocomplete="new-password"></form>');
  expect(p.fill(ORIGIN, 'alice', 'secret') === 'none', 'result');
  expect(p.val('#p') === '', 'a new-password field was filled');
});

check('with several password fields only the first is filled', () => {
  const p = load('<form onsubmit="return false"><input id="u" type="text"><input id="p1" type="password"><input id="p2" type="password"></form>');
  expect(p.fill(ORIGIN, 'alice', 'secret') === 'user+pass', 'result');
  expect(p.val('#p1') === 'secret' && p.val('#p2') === '', 'the second field was filled');
});

check('a login with no user name fills the password and does not blank the user field', () => {
  const p = load(FORM);
  p.$('#u').value = 'typed-already';
  expect(p.fill(ORIGIN, '', 'secret') === 'pass', 'result');
  expect(p.val('#u') === 'typed-already' && p.val('#p') === 'secret', 'values');
});

check('the user name field is the closest one before the password, a marked one preferred', () => {
  const p = load(`<form onsubmit="return false"><input id="s" type="text"><input id="u" type="email" autocomplete="username">
    <input id="x" type="text"><input id="p" type="password"><input id="after" type="text"></form>`);
  p.fill(ORIGIN, 'alice', 'secret');
  expect(p.val('#u') === 'alice' && p.val('#s') === '' && p.val('#x') === '' && p.val('#after') === '', 'values');

  const q = load(`<form onsubmit="return false"><input id="a" type="text"><input id="b" type="text"><input id="p" type="password"></form>`);
  q.fill(ORIGIN, 'alice', 'secret');
  expect(q.val('#b') === 'alice' && q.val('#a') === '', 'the closest field');
});

check('a page with no form at all is filled too', () => {
  const p = load('<div><input id="u" type="text"><input id="p" type="password"></div>');
  expect(p.fill(ORIGIN, 'alice', 'secret') === 'user+pass', 'result');
});

check('awkward characters in the password arrive exactly, because it is data and not text', () => {
  const nasty = 'p\');alert(1)//\\"</script>\n\u2028\u2029\u00e9 \ud83d\ude00 `${x}` \\u0041';
  const p = load(FORM);
  p.win.alerted = false;
  p.win.alert = () => { p.win.alerted = true; };
  expect(p.fill(ORIGIN, 'us"er\'s', nasty) === 'user+pass', 'result');
  // A text or password field drops line breaks from what it is given (the HTML rules), so that is the
  // one change that is expected; everything else must arrive exactly.
  const expected = nasty.replace(/[\r\n]/g, '');
  expect(p.val('#p') === expected, 'the password changed on the way: ' + JSON.stringify(p.val('#p')));
  expect(p.val('#u') === 'us"er\'s', 'the user name changed on the way');
  expect(p.win.alerted === false, 'code in the password ran');
});

check('the script keeps nothing behind and a page without the pieces it needs gives an answer, not an error', () => {
  const p = load('<p>nothing to fill here</p>');
  const before = new Set(Object.getOwnPropertyNames(p.win));
  expect(p.fill(ORIGIN, 'alice', 'secret') === 'none', 'an empty page');
  expect(Object.getOwnPropertyNames(p.win).filter(n => !before.has(n)).length === 0, 'it left globals behind');
  expect(p.fill(ORIGIN, '', '') === 'none', 'no values');
});

console.log('\n' + (ran - failed) + ' of ' + ran + ' checks passed.');
process.exit(failed === 0 ? 0 : 1);
