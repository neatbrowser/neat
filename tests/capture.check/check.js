'use strict';
// Runs the page scripts of the save-password bubble (src/neat/pass/capture.cs) against made-up
// pages, in jsdom. Plain asserts, one line per check, exit code 1 on any failure.
//
// jsdom has no layout, so the size part of the visibility test is skipped, as it is for any page
// whose root has no size; display, visibility, opacity and the hidden attribute are checked.
// jsdom also marks every script-made event as not trusted, so a "real" click or key press is
// made by switching on a wrapper that reports isTrusted = true for the duration of one dispatch.

const fs = require('fs');
const path = require('path');
const { JSDOM, VirtualConsole } = require('jsdom');

const file = path.join(__dirname, '..', '..', 'src', 'neat', 'pass', 'capture.cs');
const cs = fs.readFileSync(file, 'utf8').split(/\r?\n/);

function extract(name) {
  const start = cs.findIndex(l => l.includes(name + ' = """'));
  if (start < 0) throw new Error('no ' + name + ' in capture.cs');
  const end = cs.findIndex((l, i) => i > start && l.trim() === '""";');
  const indent = cs[end].match(/^\s*/)[0].length;
  return cs.slice(start + 1, end).map(l => l.slice(indent)).join('\n');
}

const SOURCE = extract('Source');
const HAS_PASSWORD = extract('HasPassword');
const HAS_CARD = extract('HasCard');
const TOKEN = 'tok123';

// The script as a page gets it: the token and the site rules (an object, as in pass/rules.json) filled in.
const src = rules => SOURCE.replace('__TOKEN__', TOKEN).replace('__RULES__', JSON.stringify(rules || {}));

let ran = 0;
let failed = 0;

const later = [];

// A check may be async (it waits for a timer); those run one after the other, after the plain ones.
function check(name, fn) {
  ran++;
  const done = () => console.log('ok    ' + name);
  const bad = e => { failed++; console.log('FAIL  ' + name + '\n        ' + e.message); };
  try {
    const r = fn();
    if (r && typeof r.then === 'function') later.push(() => r.then(done, bad));
    else done();
  } catch (e) {
    bad(e);
  }
}

function expect(cond, what) {
  if (!cond) throw new Error(what);
}

// Makes the events of a window report isTrusted = true while the returned switch is on.
function trustSwitch(win) {
  const state = { real: false };
  const add = win.EventTarget.prototype.addEventListener;
  win.EventTarget.prototype.addEventListener = function (type, fn, o) {
    return add.call(this, type, function (e) {
      if (!state.real) return fn.call(this, e);
      const trusted = new Proxy(e, {
        get(t, p) {
          if (p === 'isTrusted') return true;
          const v = Reflect.get(t, p, t);
          return typeof v === 'function' ? v.bind(t) : v;
        },
      });
      return fn.call(this, trusted);
    }, o);
  };
  return state;
}

function load(html, opts = {}) {
  const dom = new JSDOM(html, {
    runScripts: 'outside-only',
    url: opts.url || 'https://example.com/login',
    virtualConsole: new VirtualConsole(),
  });
  const win = dom.window;
  const posts = [];
  const timers = [];
  win.chrome = { webview: { postMessage: m => posts.push(m) } };
  win.setInterval = fn => { timers.push({ fn, on: true }); return timers.length - 1; };
  win.clearInterval = id => { if (timers[id]) timers[id].on = false; };

  const state = trustSwitch(win);
  if (opts.run !== false) win.eval(src(opts.rules));

  const $ = s => win.document.querySelector(s);
  return {
    win,
    $,
    posts,
    // What was reported, as objects.
    got: () => posts.map(m => {
      const head = 'neatpw:' + TOKEN + ':';
      if (!m.startsWith(head)) throw new Error('message does not start with the token: ' + m);
      return JSON.parse(m.slice(head.length));
    }),
    set: (s, v) => { $(s).value = v; },
    // A genuine user action.
    click: el => { state.real = true; try { (typeof el === 'string' ? $(el) : el).click(); } finally { state.real = false; } },
    key: (el, key) => {
      state.real = true;
      try {
        (typeof el === 'string' ? $(el) : el).dispatchEvent(new win.KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, composed: true }));
      } finally { state.real = false; }
    },
    focus: el => { state.real = true; try { (typeof el === 'string' ? $(el) : el).focus(); } finally { state.real = false; } },
    blur: el => { state.real = true; try { (typeof el === 'string' ? $(el) : el).blur(); } finally { state.real = false; } },
    pointer: el => { state.real = true; try { (typeof el === 'string' ? $(el) : el).dispatchEvent(new win.MouseEvent('pointerdown', { bubbles: true, composed: true })); } finally { state.real = false; } },
    // The same action made by the page's own script.
    fakeClick: el => (typeof el === 'string' ? $(el) : el).click(),
    fakeKey: (el, key) => (typeof el === 'string' ? $(el) : el).dispatchEvent(new win.KeyboardEvent('keydown', { key, bubbles: true, cancelable: true })),
    tick: () => timers.filter(t => t.on).forEach(t => t.fn()),
    live: () => timers.filter(t => t.on).length,
  };
}

const LOGIN = `<form id="f" onsubmit="return false">
  <input id="u" type="text" name="user"><input id="p" type="password"><button id="go" type="submit">Sign in</button></form>`;

// ---- checks ----

check('a login is reported when the person clicks Sign in', () => {
  const p = load(LOGIN);
  p.set('#u', 'alice'); p.set('#p', 'hunter2 hunter2');
  p.click('#go');
  const m = p.got().filter(x => x.t === 'offer');
  expect(m.length >= 1, 'nothing reported');
  expect(m[0].u === 'alice' && m[0].p === 'hunter2 hunter2', 'wrong values: ' + JSON.stringify(m[0]));
  expect(p.posts[0].startsWith('neatpw:' + TOKEN + ':'), 'the message does not carry the token');
});

check('Enter in the field reports it, Enter in a text area does not', () => {
  const p = load(LOGIN.replace('</form>', '<textarea id="t"></textarea></form>'));
  p.set('#u', 'alice'); p.set('#p', 'secret');
  p.key('#t', 'Enter');
  expect(p.got().length === 0, 'Enter in a text area reported a login');
  p.key('#p', 'Enter');
  expect(p.got().some(x => x.t === 'offer' && x.p === 'secret'), 'Enter in the password field was not reported');
  const q = load(LOGIN);
  q.set('#p', 'secret'); q.key('#p', 'a');
  expect(q.got().length === 0, 'another key reported a login');
});

check('nothing the page does by itself is reported', () => {
  const p = load(LOGIN);
  p.set('#u', 'alice'); p.set('#p', 'secret');
  p.fakeClick('#go');
  p.fakeKey('#p', 'Enter');
  p.win.document.querySelector('#f').dispatchEvent(new p.win.Event('submit', { bubbles: true, cancelable: true }));
  expect(p.got().length === 0, 'a script-made click, key or submit was reported: ' + p.posts.length);
});

check('a submit the browser fires for a script-made click is not proof of a person', () => {
  // button.click() from a page script makes the browser fire a submit event that says isTrusted
  // (jsdom does the same, which is what this shows): so submit must not be a trigger.
  const p = load(LOGIN);
  p.set('#u', 'alice'); p.set('#p', 'secret');
  let trusted = null;
  p.$('#f').addEventListener('submit', e => { trusted = e.isTrusted; });
  p.fakeClick('#go');
  expect(trusted === true, '(sanity) the submit event of a script-made click is trusted here: ' + trusted);
  expect(p.got().length === 0, 'it was taken as a person submitting');
  p.$('#f').requestSubmit && p.$('#f').requestSubmit();
  expect(p.got().length === 0, 'requestSubmit was taken as a person submitting');
});

check('Enter and the click it brings are one login, not two', () => {
  const p = load(LOGIN);
  p.set('#u', 'alice'); p.set('#p', 'secret');
  p.key('#p', 'Enter');
  p.click('#go');
  expect(p.got().filter(x => x.t === 'offer').length === 1, 'reported twice');
  expect(p.live() === 1, 'watching twice: ' + p.live());
  // A different password a moment later is a different login.
  p.set('#p', 'other'); p.click('#go');
  expect(p.got().filter(x => x.t === 'offer').length === 2, 'a different login was dropped as a repeat');
});

check('a link is not a Sign in button', () => {
  const p = load(LOGIN.replace('</form>', '</form><a id="a" href="#x">Forgot password?</a>'));
  p.set('#u', 'alice'); p.set('#p', 'secret');
  p.click('#a');
  expect(p.got().length === 0, 'a click on a link was reported');
});

check('an empty password is not reported', () => {
  const p = load(LOGIN);
  p.set('#u', 'alice');
  p.click('#go'); p.key('#u', 'Enter');
  expect(p.got().length === 0, 'reported with no password');
});

check('the user name is the closest filled field before the password', () => {
  const p = load(`<form onsubmit="return false">
    <input id="s" type="text" value="search words">
    <input id="u" type="email" value="  alice@example.com ">
    <input id="p" type="password"><input id="after" type="text" value="after">
    <button id="go">Go</button></form>`);
  p.set('#p', 'secret');
  p.click('#go');
  expect(p.got()[0].u === 'alice@example.com', 'user: ' + JSON.stringify(p.got()[0].u));
});

check('a field marked username is preferred, and the user name can be empty', () => {
  const p = load(`<form onsubmit="return false"><input id="u" type="text" autocomplete="username" value="the-user">
    <input id="x" type="text" value="other"><input id="p" type="password"><button id="go">Go</button></form>`);
  p.set('#p', 'secret'); p.click('#go');
  expect(p.got()[0].u === 'the-user', 'user: ' + p.got()[0].u);

  const q = load('<form onsubmit="return false"><input id="p" type="password"><button id="go">Go</button></form>');
  q.set('#p', 'secret'); q.click('#go');
  expect(q.got()[0].u === '', 'a form with no user name gave: ' + q.got()[0].u);
});

check('hidden, search and checkbox fields are not user names', () => {
  const p = load(`<form onsubmit="return false"><input type="hidden" value="h"><input type="search" value="s">
    <input type="checkbox" value="c"><input style="display:none" type="text" value="gone">
    <input id="p" type="password"><button id="go">Go</button></form>`);
  p.set('#p', 'secret'); p.click('#go');
  expect(p.got()[0].u === '', 'user: ' + p.got()[0].u);
});

check('a hidden password field is ignored, a visible one next to it is used', () => {
  const p = load(`<form onsubmit="return false"><input id="u" type="text" value="alice">
    <input id="trap" type="password" style="display:none" value="trap">
    <div hidden><input id="trap2" type="password" value="trap2"></div>
    <input id="p" type="password"><button id="go">Go</button></form>`);
  p.set('#p', 'real'); p.click('#go');
  expect(p.got()[0].p === 'real', 'password: ' + p.got()[0].p);

  const q = load(`<form onsubmit="return false"><input id="trap" type="password" style="visibility:hidden" value="x">
    <input id="t2" type="password" style="opacity:0" value="y"><button id="go">Go</button></form>`);
  q.click('#go');
  expect(q.got().length === 0, 'only hidden fields, yet something was reported');
});

check('sign-up: two equal passwords are one new password', () => {
  const p = load(`<form onsubmit="return false"><input id="u" type="text" value="alice">
    <input id="p1" type="password"><input id="p2" type="password"><button id="go">Go</button></form>`);
  p.set('#p1', 'same-pass'); p.set('#p2', 'same-pass'); p.click('#go');
  expect(p.got()[0].p === 'same-pass', 'sign-up not reported');
});

check('two different passwords with nothing to tell them apart are left alone', () => {
  const two = load(`<form onsubmit="return false"><input id="p1" type="password"><input id="p2" type="password"><button id="go">Go</button></form>`);
  two.set('#p1', 'one'); two.set('#p2', 'two'); two.click('#go');
  expect(two.got().length === 0, 'two different passwords were reported');

  const odd = load(`<form onsubmit="return false"><input id="p0" type="password"><input id="p1" type="password">
    <input id="p2" type="password"><button id="go">Go</button></form>`);
  odd.set('#p0', 'a'); odd.set('#p1', 'b'); odd.set('#p2', 'c'); odd.click('#go');
  expect(odd.got().length === 0, 'three different passwords were reported');

  const same = load(`<form onsubmit="return false"><input id="p0" type="password"><input id="p1" type="password">
    <input id="p2" type="password"><button id="go">Go</button></form>`);
  same.set('#p0', 'x'); same.set('#p1', 'x'); same.set('#p2', 'x'); same.click('#go');
  expect(same.got().every(m => m.o === undefined), 'three equal passwords were taken as a change');
});

check('a change-password form is reported as the new password with the old one beside it', () => {
  const three = load(`<form onsubmit="return false"><input id="p0" type="password"><input id="p1" type="password">
    <input id="p2" type="password"><button id="go">Change</button></form>`);
  three.set('#p0', 'old-pass'); three.set('#p1', 'new-pass'); three.set('#p2', 'new-pass'); three.click('#go');
  const m = three.got().filter(x => x.t === 'offer');
  expect(m.length === 1 && m[0].p === 'new-pass' && m[0].o === 'old-pass', 'got ' + JSON.stringify(m));
  expect(three.live() === 1, 'the form is watched');
});

check('the same new password with another old one is a different report, not a repeat', () => {
  const p = load(`<form onsubmit="return false"><input id="p0" type="password"><input id="p1" type="password">
    <input id="p2" type="password"><button id="go">Change</button></form>`);
  p.set('#p0', 'old-one'); p.set('#p1', 'new'); p.set('#p2', 'new'); p.click('#go');
  p.set('#p0', 'old-two'); p.click('#go');
  const m = p.got().filter(x => x.t === 'offer');
  expect(m.length === 2 && m[0].o === 'old-one' && m[1].o === 'old-two', 'got ' + JSON.stringify(m));
});

check('fields marked current-password and new-password say which is which', () => {
  const one = load(`<form onsubmit="return false"><input id="c" type="password" autocomplete="current-password">
    <input id="n" type="password" autocomplete="new-password"><button id="go">Change</button></form>`);
  one.set('#c', 'old-pass'); one.set('#n', 'new-pass'); one.click('#go');
  let m = one.got().find(x => x.t === 'offer');
  expect(m && m.p === 'new-pass' && m.o === 'old-pass', 'marked, no confirmation field: ' + JSON.stringify(m));

  // The new password first in the page: the marks decide, not the order.
  const swapped = load(`<form onsubmit="return false"><input id="n" type="password" autocomplete="new-password">
    <input id="c" type="password" autocomplete="current-password"><button id="go">Change</button></form>`);
  swapped.set('#c', 'old-pass'); swapped.set('#n', 'new-pass'); swapped.click('#go');
  m = swapped.got().find(x => x.t === 'offer');
  expect(m && m.p === 'new-pass' && m.o === 'old-pass', 'marks, other order: ' + JSON.stringify(m));

  // Two new-password fields that agree (new + confirm) next to the current one.
  const confirm = load(`<form onsubmit="return false"><input id="c" type="password" autocomplete="current-password">
    <input id="n1" type="password" autocomplete="new-password"><input id="n2" type="password" autocomplete="new-password">
    <button id="go">Change</button></form>`);
  confirm.set('#c', 'old-pass'); confirm.set('#n1', 'new-pass'); confirm.set('#n2', 'new-pass'); confirm.click('#go');
  m = confirm.got().find(x => x.t === 'offer');
  expect(m && m.p === 'new-pass' && m.o === 'old-pass', 'with a confirmation field: ' + JSON.stringify(m));

  // New passwords that disagree: a typing mistake, nothing to report.
  const mistake = load(`<form onsubmit="return false"><input id="c" type="password" autocomplete="current-password">
    <input id="n1" type="password" autocomplete="new-password"><input id="n2" type="password" autocomplete="new-password">
    <button id="go">Change</button></form>`);
  mistake.set('#c', 'old'); mistake.set('#n1', 'one'); mistake.set('#n2', 'two'); mistake.click('#go');
  expect(mistake.got().length === 0, 'new passwords that disagree were reported');

  // The same password in both: not a change.
  const nochange = load(`<form onsubmit="return false"><input id="c" type="password" autocomplete="current-password">
    <input id="n" type="password" autocomplete="new-password"><button id="go">Change</button></form>`);
  nochange.set('#c', 'same'); nochange.set('#n', 'same'); nochange.click('#go');
  expect(nochange.got().every(m => m.o === undefined), 'an unchanged password was taken as a change');

  // An old password that is too long is not reported at all.
  const long = load(`<form onsubmit="return false"><input id="p0" type="password"><input id="p1" type="password">
    <input id="p2" type="password"><button id="go">Change</button></form>`);
  long.set('#p0', 'x'.repeat(513)); long.set('#p1', 'new'); long.set('#p2', 'new'); long.click('#go');
  expect(long.got().length === 0, 'an old password of 513 characters was reported');
});

check('a sign-up form with one marked new-password field is still a sign-up', () => {
  const p = load(`<form onsubmit="return false"><input id="u" type="text" value="alice">
    <input id="n1" type="password" autocomplete="new-password"><input id="n2" type="password" autocomplete="new-password">
    <button id="go">Sign up</button></form>`);
  p.set('#n1', 'chosen'); p.set('#n2', 'chosen'); p.click('#go');
  const m = p.got().find(x => x.t === 'offer');
  expect(m && m.p === 'chosen' && m.o === undefined && m.u === 'alice', 'got ' + JSON.stringify(m));
});

check('a page with no form at all works too (a script-driven login)', () => {
  const p = load(`<div><input id="u" type="text" value="alice"><input id="p" type="password">
    <div id="go" role="button">Log in</div></div>`);
  p.set('#p', 'secret'); p.click('#go');
  expect(p.got()[0].u === 'alice' && p.got()[0].p === 'secret', 'not reported');
});

check('only the form that was submitted is read', () => {
  const p = load(`<form onsubmit="return false"><input id="u1" type="text" value="alice"><input id="p1" type="password"><button id="go1">Go</button></form>
    <form onsubmit="return false"><input id="u2" type="text" value="bob"><input id="p2" type="password"><button id="go2">Go</button></form>`);
  p.set('#p1', 'first'); p.set('#p2', 'second');
  p.click('#go2');
  const m = p.got().filter(x => x.t === 'offer');
  expect(m.length >= 1 && m.every(x => x.u === 'bob' && x.p === 'second'), 'read the wrong form: ' + JSON.stringify(m));
});

check('values longer than the limit are not reported', () => {
  const p = load(LOGIN);
  p.set('#u', 'alice'); p.set('#p', 'x'.repeat(513)); p.click('#go');
  expect(p.got().length === 0, 'a 513-character password was reported');
  const q = load(LOGIN);
  q.set('#u', 'alice'); q.set('#p', 'x'.repeat(512)); q.click('#go');
  expect(q.got().length >= 1, 'a 512-character password was not reported');
});

check('two-step login: the user name page reports only the user name', () => {
  const p = load(`<form onsubmit="return false"><input id="e" type="email" autocomplete="username" name="identifier" value="alice@gmail.com">
    <button id="next" type="button">Next</button></form>`);
  p.click('#next');
  const m = p.got();
  expect(m.length === 1 && m[0].t === 'user' && m[0].u === 'alice@gmail.com', 'got ' + JSON.stringify(m));
  expect(p.live() === 0, 'nothing should be watched on a page with no password');
  p.click('#next');
  expect(p.got().length === 1, 'the same user name twice in a moment was reported twice');
});

check('a user name field is recognised by type, autocomplete or name, and a search box is not', () => {
  const report = field => {
    const p = load(`<form onsubmit="return false">${field}<button id="go">Go</button></form>`);
    p.click('#go');
    return p.got();
  };
  expect(report('<input type="email" value="a@b.c">').length === 1, 'type email');
  expect(report('<input type="text" autocomplete="username" value="alice">').length === 1, 'autocomplete username');
  expect(report('<input type="text" name="identifier" value="alice">').length === 1, 'name identifier');
  expect(report('<input type="text" id="loginName" value="alice">').length === 1, 'id loginName');
  expect(report('<input type="tel" name="phone" value="0123456789">').length === 1, 'a phone number');
  expect(report('<input type="search" name="q" value="cats">').length === 0, 'a search box');
  expect(report('<input type="text" name="q" value="cats">').length === 0, 'a plain text box');
  expect(report('<input type="text" name="comment" value="nice page">').length === 0, 'a comment box');
  expect(report('<input type="email" value="">').length === 0, 'an empty field');
  expect(report('<input type="email" style="display:none" value="a@b.c">').length === 0, 'a hidden field on the first step');
  expect(report('<input type="email" value="' + 'x'.repeat(513) + '">').length === 0, 'too long');
});

check('a page that has a password field is not a first step', () => {
  const p = load(`<form onsubmit="return false"><input id="u" type="email" value="alice@example.com">
    <input id="p" type="password"><button id="go">Go</button></form>`);
  p.click('#go');
  expect(p.got().length === 0, 'a combined form with an empty password was taken as a first step: ' + JSON.stringify(p.got()));
});

check('a password page takes the user name from a hidden field marked username', () => {
  const p = load(`<form onsubmit="return false"><input type="email" name="identifier" autocomplete="username" style="display:none" value="alice@gmail.com">
    <input id="p" type="password"><button id="go">Next</button></form>`);
  p.set('#p', 'the-password'); p.click('#go');
  const o = p.got().find(x => x.t === 'offer');
  expect(o && o.u === 'alice@gmail.com' && o.p === 'the-password', 'got ' + JSON.stringify(p.got()));

  const h = load(`<form onsubmit="return false"><input type="hidden" autocomplete="username" value="bob@gmail.com">
    <input id="p" type="password"><button id="go">Next</button></form>`);
  h.set('#p', 'pw'); h.click('#go');
  expect(h.got().find(x => x.t === 'offer').u === 'bob@gmail.com', 'a type=hidden field marked username');
});

check('a hidden field is only a user name when it is marked, and a visible one wins', () => {
  const unmarked = load(`<form onsubmit="return false"><input type="hidden" name="user_id" value="12345">
    <input type="text" style="display:none" name="user" value="trap"><input id="p" type="password"><button id="go">Go</button></form>`);
  unmarked.set('#p', 'pw'); unmarked.click('#go');
  expect(unmarked.got().find(x => x.t === 'offer').u === '', 'an unmarked hidden field was used');

  const both = load(`<form onsubmit="return false"><input type="email" autocomplete="username" style="display:none" value="hidden@x.com">
    <input type="text" value="visible-user"><input id="p" type="password"><button id="go">Go</button></form>`);
  both.set('#p', 'pw'); both.click('#go');
  expect(both.got().find(x => x.t === 'offer').u === 'visible-user', 'the hidden field beat the visible one');
});

check('the field going away is reported, a field that stays is not', () => {
  const gone = load(LOGIN);
  gone.set('#u', 'alice'); gone.set('#p', 'secret'); gone.click('#go');
  expect(gone.live() >= 1, 'nothing is watching the form');
  gone.tick();
  expect(!gone.got().some(x => x.t === 'gone'), 'reported gone while the field is still there');
  gone.$('#f').remove();
  gone.tick();
  const g = gone.got().filter(x => x.t === 'gone');
  expect(g.length === 1, 'the field is gone and that was not reported once: ' + g.length);
  gone.tick();
  expect(gone.got().filter(x => x.t === 'gone').length === 1, 'reported more than once');

  const hidden = load(LOGIN);
  hidden.set('#p', 'secret'); hidden.click('#go');
  hidden.$('#f').style.display = 'none';
  hidden.tick();
  expect(hidden.got().some(x => x.t === 'gone'), 'a form that was hidden was not reported');

  const stays = load(LOGIN);
  stays.set('#p', 'secret'); stays.click('#go');
  for (let i = 0; i < 25; i++) stays.tick();
  expect(!stays.got().some(x => x.t === 'gone'), 'a field that stayed was reported gone');
  expect(stays.live() === 0, 'it is still watching after the time is up');
});

check('the offer and the gone message belong together by id', () => {
  const p = load(LOGIN);
  p.set('#p', 'secret'); p.click('#go');
  p.$('#f').remove(); p.tick();
  const all = p.got();
  const offer = all.find(x => x.t === 'offer');
  const gone = all.find(x => x.t === 'gone');
  expect(offer && gone && offer.id === gone.id, 'ids differ');
});

check('a frame inside a page reports nothing, even for a real click', () => {
  const p = load('<iframe id="fr"></iframe>', { run: false });
  const frame = p.$('#fr').contentWindow;
  frame.document.body.innerHTML = LOGIN;
  frame.chrome = { webview: { postMessage: m => p.posts.push(m) } };
  const state = trustSwitch(frame);
  frame.eval(src());
  expect(frame.top !== frame, '(sanity) the frame knows it is not the top page');
  frame.document.querySelector('#u').value = 'alice';
  frame.document.querySelector('#p').value = 'secret';
  state.real = true;
  try { frame.document.querySelector('#go').click(); } finally { state.real = false; }
  expect(p.posts.length === 0, 'a frame reported a login');

  // The same page as a top page does report, so the check above is not an empty one.
  const top = load(LOGIN);
  top.set('#u', 'alice'); top.set('#p', 'secret'); top.click('#go');
  expect(top.posts.length > 0, '(sanity) the same form in a top page is reported');
});

check('a page without the WebView2 bridge does nothing and does not fail', () => {
  const dom = new JSDOM(LOGIN, { runScripts: 'outside-only', url: 'https://example.com/', virtualConsole: new VirtualConsole() });
  dom.window.eval(src());
});

check('the script keeps no globals the page can find', () => {
  const p = load(LOGIN);
  const before = new Set(Object.getOwnPropertyNames(load(LOGIN, { run: false }).win));
  const added = Object.getOwnPropertyNames(p.win).filter(n => !before.has(n));
  expect(added.length === 0, 'added globals: ' + added.join(', '));
});

check('the token is only in the message, and messages are one string', () => {
  const p = load(LOGIN);
  p.set('#p', 'secret'); p.click('#go');
  expect(p.posts.every(m => typeof m === 'string'), 'a message is not a string');
  expect(!SOURCE.includes('neatpw:neatpw'), 'sanity');
  expect(SOURCE.includes('__TOKEN__') && !SOURCE.includes(TOKEN), 'the token is not a placeholder in the source');
});

check('HasPassword: true with a visible password field, false without or with hidden ones', () => {
  const run = html => {
    const dom = new JSDOM(html, { runScripts: 'outside-only', virtualConsole: new VirtualConsole() });
    return dom.window.eval(HAS_PASSWORD);
  };
  expect(run('<input type="password">') === true, 'visible field');
  expect(run('<p>Welcome back</p>') === false, 'no field');
  expect(run('<input type="password" style="display:none">') === false, 'display none');
  expect(run('<div hidden><input type="password"></div>') === false, 'hidden parent');
  expect(run('<input type="text"><input type="PASSWORD">') === true, 'type written in capitals');
});

// ---- shadow roots (web components) ----

// A page whose only content is a host with an open shadow root holding `inner`.
function shadowPage(inner, opts) {
  const p = load('<div id="host"></div>', opts);
  const root = p.$('#host').attachShadow({ mode: 'open' });
  root.innerHTML = inner;
  p.s = sel => root.querySelector(sel);
  return p;
}

const SHADOW_LOGIN = `<form id="f" onsubmit="return false"><input id="u" type="text" name="user">
  <input id="p" type="password"><button id="go" type="submit">Sign in</button></form>`;

check('a login inside a shadow root is reported, and so is the form going away', () => {
  const p = shadowPage(SHADOW_LOGIN);
  p.s('#u').value = 'alice'; p.s('#p').value = 'hunter2';
  p.click(p.s('#go'));
  const o = p.got().find(x => x.t === 'offer');
  expect(o && o.u === 'alice' && o.p === 'hunter2', 'got ' + JSON.stringify(p.got()));
  expect(p.live() === 1, 'not watching the field');
  p.s('#f').remove(); p.tick();
  expect(p.got().some(x => x.t === 'gone'), 'a form removed from a shadow root was not reported');
});

check('Enter in a field inside a shadow root is reported (the key event crosses the boundary)', () => {
  const p = shadowPage(SHADOW_LOGIN);
  p.s('#u').value = 'alice'; p.s('#p').value = 'secret';
  p.key(p.s('#p'), 'Enter');
  expect(p.got().some(x => x.t === 'offer' && x.p === 'secret'), 'not reported: ' + JSON.stringify(p.got()));
});

check('a password field in a component inside an ordinary form is found, with the user name before it', () => {
  const p = load(`<form onsubmit="return false"><input id="u" type="text" name="user"><div id="h"></div><button id="go">Go</button></form>`);
  p.$('#h').attachShadow({ mode: 'open' }).innerHTML = '<input id="p" type="password">';
  p.$('#u').value = 'alice'; p.$('#h').shadowRoot.querySelector('#p').value = 'pw1234';
  p.click('#go');
  const o = p.got().find(x => x.t === 'offer');
  expect(o && o.u === 'alice' && o.p === 'pw1234', 'got ' + JSON.stringify(p.got()));
});

check('a shadow host that is hidden hides what is inside it', () => {
  const p = shadowPage(SHADOW_LOGIN);
  p.s('#u').value = 'alice'; p.s('#p').value = 'secret';
  p.$('#host').style.display = 'none';
  p.click(p.s('#go'));
  expect(p.got().length === 0, 'a field inside a hidden host was reported');
});

// ---- what counts as a password field ----

check('a password the page shows as text is still the password, and is never the user name', () => {
  const p = load(`<form onsubmit="return false"><input id="u" type="text" name="user">
    <input id="p" type="password" name="password"><button id="eye" type="button">Show</button><button id="go">Sign in</button></form>`);
  p.set('#u', 'alice'); p.set('#p', 'hunter2');
  p.$('#p').type = 'text';
  p.click('#go');
  const o = p.got().find(x => x.t === 'offer');
  expect(o && o.u === 'alice' && o.p === 'hunter2', 'got ' + JSON.stringify(p.got()));

  const q = load(`<form onsubmit="return false"><input id="p" type="text" autocomplete="current-password" value="abc"><button id="go">Go</button></form>`);
  q.click('#go');
  expect(q.got().some(x => x.t === 'offer' && x.p === 'abc' && x.u === ''), 'a text field marked current-password: ' + JSON.stringify(q.got()));

  const plain = load(`<form onsubmit="return false"><input type="text" name="nickname" value="abc"><button id="go">Go</button></form>`);
  plain.click('#go');
  expect(plain.got().length === 0, 'an ordinary text field was taken for a password');
});

check('a text field drawn masked (-webkit-text-security) is a password field', () => {
  // jsdom does not know that property, so it is made up for the elements marked data-mask.
  const q = load(`<form onsubmit="return false"><input id="u" type="text" name="user">
    <input id="p" type="text" name="secretbox" data-mask="1"><button id="go">Go</button></form>`, { run: false });
  const real = q.win.getComputedStyle.bind(q.win);
  q.win.getComputedStyle = el => new Proxy(real(el), {
    get(t, k) {
      if (k === 'webkitTextSecurity') return el.dataset && el.dataset.mask ? 'disc' : 'none';
      const v = Reflect.get(t, k, t);
      return typeof v === 'function' ? v.bind(t) : v;
    },
  });
  q.win.eval(src());
  q.set('#u', 'alice'); q.set('#p', 'hunter2'); q.click('#go');
  const o = q.got().find(x => x.t === 'offer');
  expect(o && o.u === 'alice' && o.p === 'hunter2', 'got ' + JSON.stringify(q.got()));
});

// ---- buttons that are not a sign-in ----

check('Cancel, Close, Back and the eye that shows the password are not a sign-in', () => {
  const p = load(`<form onsubmit="return false"><input id="u" type="text" name="user"><input id="p" type="password">
    <button id="c1" type="button">Cancel</button><button id="c2" type="button" aria-label="Close dialog"><svg></svg></button>
    <button id="c3" type="button">Back</button><button id="c4" type="button">Hủy</button>
    <button id="c5" type="button" aria-label="Show password"></button><button id="c6" type="button" title="Hide password"></button>
    <input id="c7" type="button" value="Cancel"><button id="go">Sign in</button></form>`);
  p.set('#u', 'alice'); p.set('#p', 'secret');
  for (const id of ['#c1', '#c2', '#c3', '#c4', '#c5', '#c6', '#c7']) p.click(id);
  expect(p.got().length === 0, 'reported for a button that is not a sign-in: ' + JSON.stringify(p.got()));
  expect(p.live() === 0, 'it started watching the form');
  p.click('#go');
  expect(p.got().some(x => x.t === 'offer'), 'Sign in was no longer reported');
});

check('a long label is not mistaken for a Cancel button, and Sign in with a close-looking word still counts', () => {
  const p = load(`<form onsubmit="return false"><input id="p" type="password">
    <button id="go">Sign in to see your account and close the loop on your open orders</button></form>`);
  p.set('#p', 'secret'); p.click('#go');
  expect(p.got().some(x => x.t === 'offer'), 'a long label was taken for Cancel');
});

// ---- controls that are not buttons ----

check('a styled div that is clicked like a button counts when a filled password is on show', () => {
  const p = load(`<form id="f"><input id="u" type="text" name="user"><input id="p" type="password">
    <div id="d" style="cursor:pointer"><span id="sp">Sign in</span></div></form>`);
  p.set('#u', 'alice'); p.set('#p', 'secret');
  p.click('#sp');
  const o = p.got().find(x => x.t === 'offer');
  expect(o && o.u === 'alice' && o.p === 'secret', 'got ' + JSON.stringify(p.got()));

  const plain = load(`<form id="f"><input id="u" type="text" name="user"><input id="p" type="password">
    <div id="d"><span id="sp">Sign in</span></div></form>`);
  plain.set('#p', 'secret'); plain.click('#sp');
  expect(plain.got().length === 0, 'a div that does not look clickable was taken for a button');
});

check('a clickable div never starts the guess about a first step, a link is never a button', () => {
  const p = load(`<form><input type="email" value="a@b.c"><div id="d" style="cursor:pointer">Join</div></form>`);
  p.click('#d');
  expect(p.got().length === 0, 'a div on a page with no password reported a user name');

  const a = load(`<form><input id="p" type="password"><a id="l" href="#x" style="cursor:pointer"><span id="s">Forgot</span></a></form>`);
  a.set('#p', 'secret'); a.click('#s');
  expect(a.got().length === 0, 'a link with a pointer cursor was taken for a button');

  const c = load(`<form><input id="p" type="password"><div id="d" style="cursor:pointer">Close</div></form>`);
  c.set('#p', 'secret'); c.click('#d');
  expect(c.got().length === 0, 'a Close div was taken for a sign-in');
});

// ---- what a first-step field says about itself ----

check('a first-step user name is also recognised by what the field says (aria-label, placeholder)', () => {
  const report = field => {
    const p = load(`<form onsubmit="return false">${field}<button id="go">Next</button></form>`);
    p.click('#go');
    return p.got();
  };
  expect(report('<input type="text" placeholder="Email or phone number" value="alice">').length === 1, 'placeholder');
  expect(report('<input type="text" aria-label="Tên đăng nhập" value="alice">').length === 1, 'aria-label in Vietnamese');
  expect(report('<input type="text" placeholder="Search accounts" value="cats">').length === 0, 'a search placeholder');
  expect(report('<input type="text" aria-label="Tìm kiếm tài khoản" value="cats">').length === 0, 'a search label in Vietnamese');
  expect(report('<input type="text" placeholder="Your comment" value="nice">').length === 0, 'a comment placeholder');
});

// ---- rules for single sites ----

const PLAIN_PAGE = `<div id="widget"><input type="text" name="user" value="alice"><input type="password" value="secret"></div><button id="go">Go</button>`;

check('a rule can leave a part of the page out', () => {
  const free = load(PLAIN_PAGE);
  free.click('#go');
  expect(free.got().some(x => x.t === 'offer'), '(sanity) without a rule it is reported');
  const ruled = load(PLAIN_PAGE, { rules: { 'example.com': { exclude: ['#widget'] } } });
  ruled.click('#go');
  expect(ruled.got().length === 0, 'an excluded part was reported: ' + JSON.stringify(ruled.got()));
});

check('a rule can name the fields the guess cannot find, a hidden user name included', () => {
  const html = `<form onsubmit="return false"><input type="hidden" name="login" value="bob@x.com">
    <input id="pw" type="text" name="zz" value="s3cret"><button id="go">Next</button></form>`;
  const plain = load(html);
  plain.click('#go');
  expect(plain.got().length === 0, '(sanity) the guess finds nothing here');
  const rules = { 'example.com': { login: [{ user: 'input[name="login"]', pass: '#pw' }] } };
  const p = load(html, { rules });
  p.click('#go');
  const o = p.got().find(x => x.t === 'offer');
  expect(o && o.u === 'bob@x.com' && o.p === 's3cret', 'got ' + JSON.stringify(p.got()));
  expect(p.live() === 1, 'not watching the field');
});

check('a rule path goes down through shadow roots, one selector at a time', () => {
  const rules = { 'example.com': { login: [{ user: ['#h', 'input[name="u"]'], pass: ['#h', 'input[name="zz"]'] }] } };
  const p = load('<div id="h"></div><button id="go">Go</button>', { rules });
  p.$('#h').attachShadow({ mode: 'open' }).innerHTML = '<input name="u" value="carol"><input name="zz" value="pw9">';
  p.click('#go');
  const o = p.got().find(x => x.t === 'offer');
  expect(o && o.u === 'carol' && o.p === 'pw9', 'got ' + JSON.stringify(p.got()));
});

check('a rule applies to its host and subdomains, to its path, and to nothing else', () => {
  const html = `<input type="text" name="u" value="dan"><input id="pw" type="text" name="zz" value="pw9"><button id="go">Go</button>`;
  const rule = key => ({ [key]: { login: [{ user: 'input[name="u"]', pass: '#pw' }] } });
  const hit = (url, key) => { const p = load(html, { url, rules: rule(key) }); p.click('#go'); return p.got().some(x => x.t === 'offer'); };
  expect(hit('https://example.com/', 'example.com'), 'the host itself');
  expect(hit('https://www.example.com/', 'example.com'), 'a subdomain');
  expect(!hit('https://notexample.com/', 'example.com'), 'a host that only ends the same');
  expect(!hit('https://example.org/', 'example.com'), 'another host');
  expect(hit('https://example.com/login/step', 'example.com/login'), 'a path prefix');
  expect(!hit('https://example.com/other', 'example.com/login'), 'another path');
});

check('a rule whose field is empty or missing falls back to the guess, and broken rules do no harm', () => {
  const p = load(LOGIN, { rules: { 'example.com': { login: [{ user: '#u', pass: '#nothere' }, null, 'x', { pass: 5 }, { pass: ['[['] }], exclude: ['[[', 5] } } });
  p.set('#u', 'alice'); p.set('#p', 'secret'); p.click('#go');
  const o = p.got().find(x => x.t === 'offer');
  expect(o && o.u === 'alice' && o.p === 'secret', 'got ' + JSON.stringify(p.got()));

  const empty = load(LOGIN, { rules: { 'example.com': { login: [{ user: '#u', pass: '#p' }] } } });
  empty.set('#u', 'alice'); empty.click('#go');
  expect(empty.got().length === 0, 'an empty password named by a rule was reported');

  const junk = load(LOGIN, { rules: { 'example.com': 7, 'bad': null } });
  junk.set('#p', 'secret'); junk.click('#go');
  expect(junk.got().some(x => x.t === 'offer'), 'junk rules stopped the guess');
});

check('a rule cannot be changed by the page: the script keeps no globals even with rules', () => {
  const rules = { 'example.com': { exclude: ['#x'] } };
  const before = new Set(Object.getOwnPropertyNames(load(LOGIN, { run: false }).win));
  const added = Object.getOwnPropertyNames(load(LOGIN, { rules }).win).filter(n => !before.has(n));
  expect(added.length === 0, 'added globals: ' + added.join(', '));
  expect(SOURCE.includes('__RULES__'), 'the rules are not a placeholder in the source');
});

// ---- which password is which, and what kind of form it is ----

const CHANGE_FORM = (a, b, extra) => `<form onsubmit="return false">${extra || ''}${a}${b}<button id="go">Save</button></form>`;

check('the old and the new password are told apart by what the fields say (label, placeholder, name)', () => {
  const report = html => {
    const p = load(html);
    p.set('#a', 'old-secret'); p.set('#b', 'new-secret');
    p.click('#go');
    return p.got().find(x => x.t === 'offer');
  };
  const labelled = report(CHANGE_FORM('<label for="a">Current password</label><input id="a" type="password">', '<label for="b">New password</label><input id="b" type="password">'));
  expect(labelled && labelled.p === 'new-secret' && labelled.o === 'old-secret', 'labels: ' + JSON.stringify(labelled));
  const holder = report(CHANGE_FORM('<input id="a" type="password" placeholder="Old password">', '<input id="b" type="password" placeholder="New password">'));
  expect(holder && holder.p === 'new-secret' && holder.o === 'old-secret', 'placeholders: ' + JSON.stringify(holder));
  const named = report(CHANGE_FORM('<input id="a" type="password" name="existing_password">', '<input id="b" type="password" name="new_password">'));
  expect(named && named.p === 'new-secret' && named.o === 'old-secret', 'names: ' + JSON.stringify(named));
  const vn = report(CHANGE_FORM('<input id="a" type="password" aria-label="Mật khẩu hiện tại">', '<input id="b" type="password" aria-label="Mật khẩu mới">'));
  expect(vn && vn.p === 'new-secret' && vn.o === 'old-secret', 'Vietnamese: ' + JSON.stringify(vn));
});

check('old, new and its confirmation are found whatever the order, and "confirm new" is a confirmation', () => {
  const p = load(`<form onsubmit="return false">
    <input id="n" type="password" placeholder="New password"><input id="c" type="password" placeholder="Confirm new password">
    <input id="o" type="password" placeholder="Current password"><button id="go">Save</button></form>`);
  p.set('#n', 'brand-new'); p.set('#c', 'brand-new'); p.set('#o', 'before');
  p.click('#go');
  const o = p.got().find(x => x.t === 'offer');
  expect(o && o.p === 'brand-new' && o.o === 'before', 'got ' + JSON.stringify(p.got()));
});

check('two unmarked passwords on a form that says it changes the password: the old, then the new', () => {
  const report = (page, a, b) => {
    const p = load(page);
    p.set('#a', a); p.set('#b', b);
    p.click('#go');
    return p.got().find(x => x.t === 'offer');
  };
  const two = '<input id="a" type="password"><input id="b" type="password">';
  // By the heading, outside the form.
  const byHeading = report(`<h1>Change password</h1><form onsubmit="return false">${two}<button id="go">Save</button></form>`, 'old1', 'new1');
  expect(byHeading && byHeading.p === 'new1' && byHeading.o === 'old1', 'heading: ' + JSON.stringify(byHeading));
  // By the button.
  const byButton = report(`<form onsubmit="return false">${two}<button id="go">Update password</button></form>`, 'old2', 'new2');
  expect(byButton && byButton.p === 'new2' && byButton.o === 'old2', 'button: ' + JSON.stringify(byButton));
  // In Vietnamese.
  const vn = report(`<form onsubmit="return false">${two}<button id="go">Đổi mật khẩu</button></form>`, 'old3', 'new3');
  expect(vn && vn.p === 'new3' && vn.o === 'old3', 'Vietnamese: ' + JSON.stringify(vn));
});

check('two different passwords on a sign-in, a sign-up or an unclear form are still left alone', () => {
  const none = (page) => {
    const p = load(page);
    p.set('#a', 'one'); p.set('#b', 'two');
    p.click('#go');
    return p.got().length === 0;
  };
  const two = '<input id="a" type="password"><input id="b" type="password">';
  expect(none(`<h1>Sign in</h1><form onsubmit="return false">${two}<button id="go">Sign in</button></form>`), 'sign in');
  expect(none(`<h1>Create account</h1><form onsubmit="return false">${two}<button id="go">Sign up</button></form>`), 'sign up (a typo in the confirmation)');
  expect(none(`<form onsubmit="return false">${two}<button id="go">Continue</button></form>`), 'nothing says what it is');
  // Fields that say they are for a NEW password are not the old one, whatever the heading says.
  expect(none(`<h1>Create a password</h1><form onsubmit="return false"><input id="a" type="password" placeholder="New password">
    <input id="b" type="password" placeholder="Confirm password"><button id="go">Continue</button></form>`), 'a new password and a mistyped confirmation');
  // A form that says both is not clear, so it is not trusted either.
  expect(none(`<h1>Change password</h1><form onsubmit="return false">${two}<button id="go">Sign in</button></form>`), 'mixed words');
});

check('words that only look like a change form do not turn a sign-in into one', () => {
  const p = load(`<h1>Sign in</h1><form onsubmit="return false"><input id="u" type="text" name="user">
    <input id="p" type="password"><a href="#r">Forgot your password? Reset password</a>
    <label><input type="checkbox"> Remember me</label><button id="go">Sign in</button></form>`);
  p.set('#u', 'alice'); p.set('#p', 'secret'); p.click('#go');
  const o = p.got().find(x => x.t === 'offer');
  expect(o && o.u === 'alice' && o.p === 'secret' && !('o' in o), 'got ' + JSON.stringify(p.got()));
});

// ---- HasPassword, as the page script now sees fields ----

check('HasPassword: a password field in a shadow root, or shown as text, still counts', () => {
  const run = (html, after) => {
    const dom = new JSDOM(html, { runScripts: 'outside-only', virtualConsole: new VirtualConsole() });
    if (after) after(dom.window.document);
    return dom.window.eval(HAS_PASSWORD);
  };
  expect(run('<div id="h"></div>', d => { d.querySelector('#h').attachShadow({ mode: 'open' }).innerHTML = '<input type="password">'; }) === true, 'inside a shadow root');
  expect(run('<div id="h" style="display:none"></div>', d => { d.querySelector('#h').attachShadow({ mode: 'open' }).innerHTML = '<input type="password">'; }) === false, 'inside a hidden host');
  expect(run('<input type="text" autocomplete="current-password">') === true, 'shown as text, marked current-password');
  expect(run('<input type="text" name="password">') === true, 'shown as text, named password');
  expect(run('<input type="text" name="nickname">') === false, 'an ordinary text field');
});

// ---- the key at a login field ----

// What the window was told about fields, in order. jsdom has no layout, so the field's box is made up.
const fieldsOf = p => p.got().filter(x => x.t === 'field');
const boxed = (p, sel, r) => {
  const el = p.$(sel);
  el.getBoundingClientRect = () => ({ left: r[0], top: r[1], width: r[2], height: r[3] });
  return el;
};
const focusReal = (p, el, pointer) => {
  // A real focus change; with pointer, the press that brings it comes just before.
  if (pointer) p.pointer(el);
  p.focus(el);
};

check('focusing a password field tells the window where it is and what it is', () => {
  const p = load(LOGIN);
  const pw = boxed(p, '#p', [100, 200, 240, 36]);
  p.focus(pw);
  const f = fieldsOf(p);
  expect(f.length === 1, 'got ' + JSON.stringify(f));
  expect(f[0].k === 'login' && JSON.stringify(f[0].r) === '[100,200,240,36]' && !('c' in f[0]), JSON.stringify(f[0]));
  expect(p.live() === 1, 'it should follow the field');
});

check('a press on the field before the focus asks the window to open the list', () => {
  const p = load(LOGIN);
  const u = boxed(p, '#u', [10, 10, 200, 30]);
  focusReal(p, u, true);
  const f = fieldsOf(p);
  expect(f.length === 1 && f[0].k === 'login' && f[0].c === 1, JSON.stringify(f));
});

check('a user name field right before a password field gets the key, a search box does not', () => {
  const p = load(`<input id="s" type="text" name="q" placeholder="Search"><form onsubmit="return false"><input id="u" type="text" name="foo"><input id="p" type="password"></form>`);
  boxed(p, '#u', [0, 0, 200, 30]); boxed(p, '#s', [0, 0, 200, 30]);
  p.focus('#s');
  expect(fieldsOf(p).length === 0, 'a search box got the key');
  p.focus('#u');
  expect(fieldsOf(p).length === 1 && fieldsOf(p)[0].k === 'login', JSON.stringify(fieldsOf(p)));
});

check('a user name field with no password after it needs to say it is a user name', () => {
  const p = load(`<input id="a" type="text" name="foo"><input id="b" type="email" name="x">`);
  boxed(p, '#a', [0, 0, 200, 30]); boxed(p, '#b', [0, 0, 200, 30]);
  p.focus('#a');
  expect(fieldsOf(p).length === 0, 'a plain text box got the key');
  p.focus('#b');
  expect(fieldsOf(p).length === 1 && fieldsOf(p)[0].k === 'login', JSON.stringify(fieldsOf(p)));
});

check('a sign-up form gets a new-password key and no key at the user name', () => {
  const p = load(`<h1>Create your account</h1><form onsubmit="return false"><input id="u" type="text" name="user">
    <input id="p" type="password" autocomplete="new-password"><button>Sign up</button></form>`);
  boxed(p, '#u', [0, 0, 200, 30]); boxed(p, '#p', [0, 40, 200, 30]);
  p.focus('#u');
  expect(fieldsOf(p).length === 0, 'the user name of a sign-up form got a key');
  p.focus('#p');
  expect(fieldsOf(p).length === 1 && fieldsOf(p)[0].k === 'new', JSON.stringify(fieldsOf(p)));
});

check('no key for a tiny, read-only, disabled or rule-excluded field, or an event the page made up', () => {
  const p = load(LOGIN, { rules: { 'example.com': { exclude: ['.skip'] } } });
  const pw = boxed(p, '#p', [0, 0, 20, 10]);
  p.win.document.documentElement.getBoundingClientRect = () => ({ left: 0, top: 0, width: 800, height: 600 });
  p.focus(pw);
  expect(fieldsOf(p).length === 0, 'a field too small for a key got one');
  boxed(p, '#p', [0, 0, 240, 36]);
  p.$('#p').readOnly = true; p.focus('#p');
  expect(fieldsOf(p).length === 0, 'a read-only field got a key');
  p.$('#p').readOnly = false; p.$('#p').disabled = true; p.focus('#p');
  expect(fieldsOf(p).length === 0, 'a disabled field got a key');
  const q = load(`<div class="skip"><input id="p" type="password"></div>`, { rules: { 'example.com': { exclude: ['.skip'] } } });
  boxed(q, '#p', [0, 0, 240, 36]); q.focus('#p');
  expect(fieldsOf(q).length === 0, 'a field the rules leave out got a key');
  const r = load(LOGIN);
  boxed(r, '#p', [0, 0, 240, 36]);
  // An event the page made up (a real focus() call is a real focus event, and only moves the key).
  r.$('#p').dispatchEvent(new r.win.FocusEvent('focusin', { bubbles: true, composed: true }));
  expect(fieldsOf(r).length === 0, 'an event that is not real counted');
});

check('the window is told when the field moves, goes away, or Escape is pressed', () => {
  const p = load(LOGIN);
  const pw = boxed(p, '#p', [100, 200, 240, 36]);
  p.focus(pw);
  boxed(p, '#p', [100, 150, 240, 36]);
  p.tick();
  let f = fieldsOf(p);
  expect(f.length === 2 && JSON.stringify(f[1].r) === '[100,150,240,36]', 'moved: ' + JSON.stringify(f));
  p.tick();
  expect(fieldsOf(p).length === 2, 'said the same thing twice');
  p.$('#p').style.display = 'none';
  p.tick();
  f = fieldsOf(p);
  expect(f.length === 3 && f[2].r === null, 'gone: ' + JSON.stringify(f));
  expect(p.live() === 0, 'still following a field that is gone');
  const q = load(LOGIN);
  boxed(q, '#p', [0, 0, 240, 36]); q.focus('#p'); q.key('#p', 'Escape');
  f = fieldsOf(q);
  expect(f.length === 2 && f[1].r === null, 'Escape: ' + JSON.stringify(f));
});

check('clicking elsewhere in the page takes the key away, a page that lost the focus does not', async () => {
  const p = load(LOGIN + '<p id="x">text</p>');
  boxed(p, '#p', [0, 0, 240, 36]);
  p.focus('#p');
  p.win.document.hasFocus = () => false;
  p.blur('#p');
  await new Promise(r => setTimeout(r, 10));
  expect(fieldsOf(p).length === 1, 'told that the field went when the whole page lost the focus');
  p.win.document.hasFocus = () => true;
  p.focus('#p');
  p.blur('#p');
  await new Promise(r => setTimeout(r, 10));
  const f = fieldsOf(p);
  expect(f.length === 2 && f[1].r === null, 'not told after a click elsewhere: ' + JSON.stringify(f));
});

check('a field for a one-time code gets an otp key, small boxes of a split code too', () => {
  const p = load(`<form onsubmit="return false"><input id="a" type="text" autocomplete="one-time-code">
    <input id="b" type="text" name="otp_code"><input id="c" type="number" placeholder="6-digit code">
    <input id="d" type="text" aria-label="Verification code"><input id="e" type="text" name="promo_code"></form>`);
  for (const id of ['a', 'b', 'c', 'd']) {
    boxed(p, '#' + id, [0, 0, 200, 30]);
    p.focus('#' + id);
    const f = fieldsOf(p);
    expect(f.length && f[f.length - 1].k === 'otp', id + ': ' + JSON.stringify(f));
    p.blur('#' + id);
  }
  const before = fieldsOf(p).length;
  boxed(p, '#e', [0, 0, 200, 30]);
  p.focus('#e');
  expect(fieldsOf(p).slice(before).every(x => x.r === null), 'a promo code field got a key');

  const q = load('<input id="d1" type="text" name="digit1" autocomplete="one-time-code" maxlength="1">');
  boxed(q, '#d1', [0, 0, 36, 40]);
  q.focus('#d1');
  expect(fieldsOf(q).length === 1 && fieldsOf(q)[0].k === 'otp', 'a small box of a split code: ' + JSON.stringify(fieldsOf(q)));
  const tiny = load('<input id="d1" type="text" name="digit1" autocomplete="one-time-code" maxlength="1">');
  boxed(tiny, '#d1', [0, 0, 10, 40]);
  tiny.win.document.documentElement.getBoundingClientRect = () => ({ left: 0, top: 0, width: 800, height: 600 });
  tiny.focus('#d1');
  expect(fieldsOf(tiny).length === 0, 'a box too small even for that got a key');
});

check('a number field that is not a code gets nothing, and a code field is not taken for a user name', () => {
  const p = load('<form onsubmit="return false"><input id="n" type="number" name="age"><input id="u" type="text" name="verification_code"><input id="p" type="password"></form>');
  boxed(p, '#n', [0, 0, 200, 30]); boxed(p, '#u', [0, 0, 200, 30]);
  p.focus('#n');
  expect(fieldsOf(p).length === 0, 'a number field got a key');
  p.focus('#u');
  expect(fieldsOf(p).length === 1 && fieldsOf(p)[0].k === 'otp', JSON.stringify(fieldsOf(p)));
});

check('card fields get a card key, by the browser marks or by what they say', () => {
  const p = load(`<form onsubmit="return false"><input id="n" autocomplete="cc-number"><input id="h" placeholder="Name on card">
    <input id="m" name="exp_month"><input id="s" name="security_code"><input id="e" name="cc_exp" placeholder="MM / YY"></form>`);
  for (const id of ['n', 'h', 'm', 's', 'e']) {
    boxed(p, '#' + id, [0, 0, 200, 30]);
    p.focus('#' + id);
    const f = fieldsOf(p);
    expect(f.length && f[f.length - 1].k === 'card', id + ': ' + JSON.stringify(f));
    p.blur('#' + id);
  }
});

check('"security code" alone is a one-time code, with a card number beside it it is a card code; a birth month is neither', () => {
  const a = load('<form onsubmit="return false"><input id="s" name="security_code"></form>');
  boxed(a, '#s', [0, 0, 200, 30]); a.focus('#s');
  expect(fieldsOf(a)[0].k === 'otp', 'alone: ' + JSON.stringify(fieldsOf(a)));
  const b = load('<form onsubmit="return false"><input name="card_number"><input id="s" name="security_code"></form>');
  boxed(b, '#s', [0, 0, 200, 30]); b.focus('#s');
  expect(fieldsOf(b)[0].k === 'card', 'with a card: ' + JSON.stringify(fieldsOf(b)));
  const c = load('<form onsubmit="return false"><input id="m" name="birth_month"></form>');
  boxed(c, '#m', [0, 0, 200, 30]); c.focus('#m');
  expect(fieldsOf(c).length === 0, 'a birth month got a key: ' + JSON.stringify(fieldsOf(c)));
});

check('contact fields get an identity key from the marks, or from the words when the form has three of them', () => {
  const a = load('<form onsubmit="return false"><input id="f" autocomplete="given-name"></form>');
  boxed(a, '#f', [0, 0, 200, 30]); a.focus('#f');
  expect(fieldsOf(a)[0].k === 'identity', 'by the mark: ' + JSON.stringify(fieldsOf(a)));
  const b = load('<form onsubmit="return false"><input id="x" placeholder="Street address"></form>');
  boxed(b, '#x', [0, 0, 200, 30]); b.focus('#x');
  expect(fieldsOf(b).length === 0, 'one word is not enough: ' + JSON.stringify(fieldsOf(b)));
  const c = load('<form onsubmit="return false"><input id="x" placeholder="Street address"><input placeholder="City"><input placeholder="ZIP code"></form>');
  boxed(c, '#x', [0, 0, 200, 30]); c.focus('#x');
  expect(fieldsOf(c)[0].k === 'identity', 'by the words: ' + JSON.stringify(fieldsOf(c)));
});

check('a sign-in form is still a sign-in: the email field and the password get the login key, not an identity', () => {
  const p = load(`<form onsubmit="return false"><input id="u" type="email" placeholder="Email address" autocomplete="email"><input id="p" type="password"><button>Sign in</button></form>`);
  boxed(p, '#u', [0, 0, 200, 30]); p.focus('#u');
  expect(fieldsOf(p)[0].k === 'login', JSON.stringify(fieldsOf(p)));
});

// ---- a card typed into a payment form ----

const PAY = `<form id="f" onsubmit="return false"><input id="n" autocomplete="cc-number"><input id="h" autocomplete="cc-name">
  <select id="m" name="exp_month"><option value="">MM</option><option value="08">08</option></select>
  <select id="y" name="exp_year"><option value="">YY</option><option value="27">27</option></select>
  <input id="c" autocomplete="cc-csc"><button id="go" type="submit">Pay now</button></form>`;
const cards = p => p.got().filter(x => x.t === 'card');

check('a card is reported when the person presses Pay, with the expiry from two selects', () => {
  const p = load(PAY);
  p.set('#n', '4111 1111 1111 1111'); p.set('#h', ' A Nguyen '); p.set('#m', '08'); p.set('#y', '27'); p.set('#c', '123');
  p.click('#go');
  const c = cards(p);
  expect(c.length === 1, 'got ' + JSON.stringify(p.got()));
  expect(c[0].n === '4111111111111111' && c[0].h === 'A Nguyen' && c[0].e === '08/27' && c[0].c === '123', JSON.stringify(c[0]));
  expect(p.live() === 1, 'it should watch for the form going away');
  p.$('#f').style.display = 'none';
  p.tick();
  expect(p.got().some(x => x.t === 'cardgone'), 'the form going away was not reported');
});

check('Enter in a card field reports it too, and Enter and the click it brings are one card', () => {
  const p = load(PAY);
  p.set('#n', '5555555555554444');
  p.key('#n', 'Enter');
  p.click('#go');
  expect(cards(p).length === 1 && cards(p)[0].n === '5555555555554444', JSON.stringify(p.got()));
});

check('a number that fails the check digit, a page that makes up a click, and Cancel report no card', () => {
  const p = load(PAY);
  p.set('#n', '4111111111111112');
  p.click('#go');
  expect(cards(p).length === 0, 'a bad number was reported');
  const q = load(PAY.replace('Pay now', 'Cancel'));
  q.set('#n', '4111111111111111'); q.click('#go');
  expect(cards(q).length === 0, 'Cancel reported a card');
  const r = load(PAY);
  r.set('#n', '4111111111111111'); r.fakeClick('#go');
  expect(cards(r).length === 0, 'a made-up click reported a card');
  const s = load(PAY);
  s.set('#n', '4111111111111111'); s.$('#n').style.display = 'none'; s.click('#go');
  expect(cards(s).length === 0, 'a hidden field reported a card');
});

check('HasCard: true with a card number field on show, false without or when it is hidden', () => {
  const run = html => {
    const dom = new JSDOM(html, { runScripts: 'outside-only', virtualConsole: new VirtualConsole() });
    return dom.window.eval(HAS_CARD);
  };
  expect(run('<input autocomplete="cc-number">') === true, 'by the mark');
  expect(run('<input name="card_number">') === true, 'by the name');
  expect(run('<input autocomplete="cc-number" style="display:none">') === false, 'hidden');
  expect(run('<input name="email">') === false, 'something else');
});

(async () => {
  for (const run of later) await run();
  console.log('\n' + (ran - failed) + ' of ' + ran + ' checks passed.');
  process.exit(failed === 0 ? 0 : 1);
})();
