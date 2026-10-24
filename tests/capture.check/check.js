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
const TOKEN = 'tok123';

let ran = 0;
let failed = 0;

function check(name, fn) {
  ran++;
  try {
    fn();
    console.log('ok    ' + name);
  } catch (e) {
    failed++;
    console.log('FAIL  ' + name + '\n        ' + e.message);
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
  if (opts.run !== false) win.eval(SOURCE.replace('__TOKEN__', TOKEN));

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
        (typeof el === 'string' ? $(el) : el).dispatchEvent(new win.KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
      } finally { state.real = false; }
    },
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

check('two different passwords, or three, are left alone (changing a password)', () => {
  const two = load(`<form onsubmit="return false"><input id="p1" type="password"><input id="p2" type="password"><button id="go">Go</button></form>`);
  two.set('#p1', 'one'); two.set('#p2', 'two'); two.click('#go');
  expect(two.got().length === 0, 'two different passwords were reported');

  const three = load(`<form onsubmit="return false"><input id="p0" type="password"><input id="p1" type="password">
    <input id="p2" type="password"><button id="go">Go</button></form>`);
  three.set('#p0', 'old'); three.set('#p1', 'new'); three.set('#p2', 'new'); three.click('#go');
  expect(three.got().length === 0, 'a change-password form was reported');
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
  frame.eval(SOURCE.replace('__TOKEN__', TOKEN));
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
  dom.window.eval(SOURCE.replace('__TOKEN__', TOKEN));
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

console.log('\n' + (ran - failed) + ' of ' + ran + ' checks passed.');
process.exit(failed === 0 ? 0 : 1);
