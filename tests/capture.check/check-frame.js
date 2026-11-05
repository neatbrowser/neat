'use strict';
// Runs the page script (src/neat/pass/capture.cs) and the fill scripts against made-up pages that sit in a frame
// of another page, in jsdom. Plain asserts, one line per check, exit code 1 on any failure.
//
// jsdom cannot make a frame with an address of its own, and its window.top and window.parent cannot be changed. So
// the frame is a window of its own, and the script is run in it with a stand-in for "window" that says what the
// top page and the parent are and what the page above is (location.ancestorOrigins). Nothing else of the script
// is changed. The top page is a plain window, with a real iframe element standing for the frame's box.

const fs = require('fs');
const path = require('path');
const { JSDOM, VirtualConsole } = require('jsdom');

const dir = path.join(__dirname, '..', '..', 'src', 'neat', 'pass');
function extract(file, name) {
  const cs = fs.readFileSync(path.join(dir, file), 'utf8').split(/\r?\n/);
  const start = cs.findIndex(l => l.includes(name + ' = """'));
  if (start < 0) throw new Error('no ' + name + ' in ' + file);
  const end = cs.findIndex((l, i) => i > start && l.trim() === '""";');
  const indent = cs[end].match(/^\s*/)[0].length;
  return cs.slice(start + 1, end).map(l => l.slice(indent)).join('\n');
}

const CAPTURE = extract('capture.cs', 'Source');
const TOKEN = 'tok123';
const TOP = 'https://shop.example';
const FRAME = 'https://bank.example';
const capture = () => CAPTURE.replace('__TOKEN__', TOKEN).replace('__RULES__', '{}');

let ran = 0;
let failed = 0;
function check(name, fn) {
  ran++;
  try { fn(); console.log('ok    ' + name); }
  catch (e) { failed++; console.log('FAIL  ' + name + '\n        ' + e.message); }
}
function expect(cond, what) { if (!cond) throw new Error(what); }

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

// A page that is the top page, with the script run in it as a real page gets it.
function top(html = '<iframe id="f"></iframe><input id="own" type="text" name="username"><input type="password" id="ownpw">') {
  const dom = new JSDOM(html, { runScripts: 'outside-only', url: TOP + '/checkout', virtualConsole: new VirtualConsole() });
  const win = dom.window;
  const posts = [];
  const timers = [];
  win.chrome = { webview: { postMessage: m => posts.push(m) } };
  win.setInterval = fn => { timers.push({ fn, on: true }); return timers.length - 1; };
  win.clearInterval = id => { if (timers[id]) timers[id].on = false; };
  const state = trustSwitch(win);
  win.eval(capture());
  const head = 'neatpw:' + TOKEN + ':';
  return {
    win,
    posts,
    got: () => posts.map(m => JSON.parse(m.slice(head.length))),
    frame: () => win.document.getElementById('f'),
    tick: () => timers.filter(t => t.on).forEach(t => t.fn()),
    live: () => timers.filter(t => t.on).length,
    // What the frame's page posted to its parent arrives here, from the frame's window.
    arrive: (data, source) => win.dispatchEvent(new win.MessageEvent('message', { data, source: source === undefined ? win.document.getElementById('f').contentWindow : source })),
    press: sel => {
      state.real = true;
      try { win.document.querySelector(sel).dispatchEvent(new win.Event('pointerdown', { bubbles: true, composed: true })); } finally { state.real = false; }
    },
    box: (el, r) => { el.getBoundingClientRect = () => ({ left: r[0], top: r[1], width: r[2], height: r[3], right: r[0] + r[2], bottom: r[1] + r[3] }); },
  };
}

// A page in a frame: its own window, with a stand-in for "window" (see above). Its parent is the top page, and
// what it posts to its parent is kept in toParent. above: what location.ancestorOrigins says. deep: the parent is
// a frame itself (a frame of a frame), so the frame is not right under the top page.
function inFrame(html, opts = {}) {
  const dom = new JSDOM(html, { runScripts: 'outside-only', url: opts.url || FRAME + '/login', virtualConsole: new VirtualConsole() });
  const b = dom.window;
  const posts = [];
  const timers = [];
  const toParent = [];
  b.chrome = { webview: { postMessage: m => posts.push(m) } };
  b.setInterval = fn => { timers.push({ fn, on: true }); return timers.length - 1; };
  b.clearInterval = id => { if (timers[id]) timers[id].on = false; };
  const state = trustSwitch(b);

  const above = opts.above === undefined ? [TOP] : opts.above;
  const topPage = { postMessage: (m, to) => toParent.push({ m, to }) };
  const middle = { postMessage() {} };
  const loc = new Proxy(b.location, {
    get(t, p) {
      if (p === 'ancestorOrigins') return above;
      const v = t[p];
      return typeof v === 'function' ? v.bind(t) : v;
    },
  });
  const P = new Proxy({}, {
    has: (t, k) => k === 'window' || k === 'location',
    get(t, k) {
      if (k === 'window') return P;
      if (k === 'location') return loc;
      if (k === 'top') return topPage;
      if (k === 'parent') return opts.deep ? middle : topPage;
      const v = b[k];
      return typeof v === 'function' ? v.bind(b) : v;
    },
  });
  const run = src => b.eval('(function(P){ with(P){ ' + src + ' } })')(P);
  return {
    b, posts, timers, toParent, run,
    $: s => b.document.querySelector(s),
    got: () => posts.map(m => JSON.parse(m.slice(('neatpw:' + TOKEN + ':').length))),
    tick: () => timers.filter(t => t.on).forEach(t => t.fn()),
    // A real focus: jsdom marks every made-up event as not real, so the switch makes it real for one dispatch.
    focus: el => {
      state.real = true;
      try { el.dispatchEvent(new b.FocusEvent('focusin', { bubbles: true, composed: true })); } finally { state.real = false; }
    },
    click: el => {
      state.real = true;
      try { el.click(); } finally { state.real = false; }
    },
    // The fill scripts: run as the window runs them, with the data as one object.
    fill: (src, data) => run('return (' + src + ')(' + JSON.stringify(data) + ');'),
    box: (el, r) => { el.getBoundingClientRect = () => ({ left: r[0], top: r[1], width: r[2], height: r[3], right: r[0] + r[2], bottom: r[1] + r[3] }); },
  };
}

const LOGIN = '<form><input id="u" type="text" name="username" autocomplete="username"><input id="p" type="password" name="pw"><button id="go" type="submit">Sign in</button></form>';

// ---- the script in the frame ----

check('a frame right under the top page reports its field kind to the window only, and the box to its parent', () => {
  const f = inFrame(LOGIN);
  f.run(capture());
  f.box(f.$('#p'), [20, 80, 240, 36]);
  f.focus(f.$('#p'));
  const got = f.got();
  expect(got.length === 1 && got[0].t === 'field' && got[0].k === 'login' && got[0].fr === 1, 'host message: ' + JSON.stringify(got));
  expect(typeof got[0].fid === 'string' && got[0].fid.length >= 8, 'no name for the frame');
  expect(!('r' in got[0]) && !('v' in got[0]), 'the window was given a box that is only the frame\'s own');
  expect(f.toParent.length === 1, 'parent got ' + f.toParent.length + ' messages');
  const m = f.toParent[0];
  expect(m.to === '*' && m.m.neat === TOKEN && m.m.fid === got[0].fid, 'parent message: ' + JSON.stringify(m));
  expect(JSON.stringify(m.m.r) === '[20,80,240,36]', 'box ' + JSON.stringify(m.m.r));
  expect(Object.keys(m.m).sort().join() === 'fid,neat,r,v', 'more than a box in it: ' + Object.keys(m.m));
});

check('a login typed in a frame goes to the window with fr and fid, and nothing of it to the parent', () => {
  const f = inFrame(LOGIN);
  f.run(capture());
  f.$('#u').value = 'an@example.com';
  f.$('#p').value = 'hunter2-secret';
  f.click(f.$('#go'));
  const got = f.got().filter(m => m.t === 'offer');
  expect(got.length === 1 && got[0].fr === 1 && got[0].u === 'an@example.com' && got[0].p === 'hunter2-secret', JSON.stringify(got));
  expect(JSON.stringify(f.toParent).indexOf('hunter2') < 0 && JSON.stringify(f.toParent).indexOf('example.com') < 0, 'a secret went to the parent');
});

check('a card typed in a frame is reported with fr, and not to the parent', () => {
  const f = inFrame('<form><input id="n" autocomplete="cc-number"><input id="e" autocomplete="cc-exp"><button id="go" type="submit">Pay</button></form>', { url: 'https://pay.example/card' });
  f.run(capture());
  f.$('#n').value = '4111 1111 1111 1111';
  f.$('#e').value = '08/27';
  f.click(f.$('#go'));
  const got = f.got().filter(m => m.t === 'card');
  expect(got.length === 1 && got[0].fr === 1 && got[0].n === '4111111111111111', JSON.stringify(got));
  expect(JSON.stringify(f.toParent).indexOf('4111') < 0, 'a card number went to the parent');
});

check('the frame says when no field has the focus, to the window and to the parent', () => {
  const f = inFrame(LOGIN);
  f.run(capture());
  f.box(f.$('#p'), [20, 80, 240, 36]);
  f.focus(f.$('#p'));
  f.$('#p').style.display = 'none';
  f.tick();
  const got = f.got();
  expect(got[got.length - 1].t === 'field' && got[got.length - 1].r === null && got[got.length - 1].fr === 1, JSON.stringify(got));
  expect(f.toParent[f.toParent.length - 1].m.r === null, 'the parent was not told');
});

check('a frame of a frame, a frame with no web address, and a top page without the channel report nothing', () => {
  const deep = inFrame(LOGIN, { deep: true });
  deep.run(capture());
  deep.$('#p').value = 'x'; deep.click(deep.$('#go')); deep.focus(deep.$('#p'));
  expect(deep.posts.length === 0 && deep.toParent.length === 0, 'a deeper frame spoke');
  const blank = inFrame(LOGIN, { url: 'about:blank' });
  blank.run(capture());
  blank.$('#p').value = 'x'; blank.click(blank.$('#go')); blank.focus(blank.$('#p'));
  expect(blank.posts.length === 0 && blank.toParent.length === 0, 'about:blank spoke');
});

// ---- the top page, which knows where the frame sits ----

const FIELD = { neat: TOKEN, fid: 'abc123', r: [20, 80, 240, 36], v: 800 };

check('the top page turns the frame\'s box into its own coordinates, clipped to the frame', () => {
  const t = top();
  t.box(t.frame(), [50, 100, 300, 200]);
  t.arrive(FIELD);
  let got = t.got();
  expect(got.length === 1 && got[0].t === 'frect' && got[0].fid === 'abc123', JSON.stringify(got));
  expect(JSON.stringify(got[0].r) === '[70,180,240,36]' && typeof got[0].v === 'number', 'box ' + JSON.stringify(got[0]));
  t.arrive(Object.assign({}, FIELD, { r: [200, 150, 240, 36] }));
  got = t.got();
  expect(JSON.stringify(got[1].r) === '[250,250,100,36]', 'not clipped: ' + JSON.stringify(got[1]));
});

check('the top page follows the frame when the page scrolls, and says nothing twice', () => {
  const t = top();
  t.box(t.frame(), [50, 100, 300, 200]);
  t.arrive(FIELD);
  t.tick();
  expect(t.got().length === 1, 'said the same thing twice');
  t.box(t.frame(), [50, 40, 300, 200]);
  t.tick();
  const got = t.got();
  expect(got.length === 2 && JSON.stringify(got[1].r) === '[70,120,240,36]', JSON.stringify(got));
});

check('a frame that is hidden, or a message from something that is not a frame of the page, gets no box', () => {
  const t = top();
  t.box(t.frame(), [50, 100, 300, 200]);
  t.frame().style.display = 'none';
  t.arrive(FIELD);
  let got = t.got();
  expect(got.length === 1 && got[0].r === null, 'a hidden frame got a box: ' + JSON.stringify(got));
  const u = top();
  u.box(u.frame(), [50, 100, 300, 200]);
  u.arrive(FIELD, u.win);
  u.arrive(FIELD, null);
  u.arrive(FIELD, new JSDOM('<p>').window);
  expect(u.got().length === 0, 'from the page itself or nowhere: ' + JSON.stringify(u.got()));
});

check('wrong token, wrong shapes and numbers that are not numbers are ignored', () => {
  const t = top();
  t.box(t.frame(), [50, 100, 300, 200]);
  for (const bad of [
    Object.assign({}, FIELD, { neat: 'other' }), 'text', null, 5, [],
    Object.assign({}, FIELD, { fid: '' }), Object.assign({}, FIELD, { fid: 'x'.repeat(81) }), Object.assign({}, FIELD, { fid: 7 }),
    Object.assign({}, FIELD, { r: [1, 2, 3] }), Object.assign({}, FIELD, { r: [1, 2, 3, '4'] }),
    Object.assign({}, FIELD, { r: [1, 2, 3, NaN] }), Object.assign({}, FIELD, { r: [1, 2, 3, 1e9] }), Object.assign({}, FIELD, { r: 'x' }),
  ]) t.arrive(bad);
  expect(t.got().length === 0, 'took ' + JSON.stringify(t.got()));
});

check('the frame saying it has no field, a press in the top page, or the top page\'s own field take the frame\'s key away', () => {
  const t = top();
  t.box(t.frame(), [50, 100, 300, 200]);
  t.arrive(FIELD);
  t.arrive({ neat: TOKEN, fid: 'abc123', r: null });
  let got = t.got();
  expect(got.length === 2 && got[1].t === 'frect' && got[1].r === null, 'frame said none: ' + JSON.stringify(got));
  expect(t.live() === 0, 'still following');

  const u = top();
  u.box(u.frame(), [50, 100, 300, 200]);
  u.arrive(FIELD);
  u.press('#own');
  got = u.got();
  expect(got.length === 2 && got[1].r === null, 'a press in the page: ' + JSON.stringify(got));

  // A press that the page made up does nothing.
  const w = top();
  w.box(w.frame(), [50, 100, 300, 200]);
  w.arrive(FIELD);
  w.win.document.querySelector('#own').dispatchEvent(new w.win.Event('pointerdown', { bubbles: true }));
  expect(w.got().length === 1, 'a made-up press took it away');
});

check('the top page does not take a message of a second frame for the first one\'s', () => {
  const t = top('<iframe id="f"></iframe><iframe id="g"></iframe>');
  t.box(t.frame(), [50, 100, 300, 200]);
  t.box(t.win.document.getElementById('g'), [400, 100, 300, 200]);
  t.arrive(FIELD);
  t.arrive(Object.assign({}, FIELD, { fid: 'second' }), t.win.document.getElementById('g').contentWindow);
  const got = t.got();
  expect(got.length === 3 && got[1].t === 'frect' && got[1].fid === 'abc123' && got[1].r === null && got[2].fid === 'second' && got[2].r[0] === 420, JSON.stringify(got));
});

// ---- the fill scripts, run in a frame ----

const SCRIPTS = [
  ['fill.cs', 'Source', { o: FRAME, u: 'an', p: 'secret' }, LOGIN, 'user+pass'],
  ['otp.cs', 'Source', { o: FRAME, p: '123456' }, '<input id="c" type="text" autocomplete="one-time-code">', 'otp'],
  ['suggest.cs', 'Source', { o: FRAME, p: 'Zx9-new-pass' }, '<form><input id="n" type="password" autocomplete="new-password"></form>', 'pass'],
  ['form.cs', 'Source', { o: FRAME, k: 'card', d: { number: '4111111111111111' } }, '<form><input id="n" autocomplete="cc-number"></form>', 'form:1'],
];

for (const [file, name, data, html, ok] of SCRIPTS) {
  const SRC = extract(file, name);
  const field = html.includes('id="n"') ? '#n' : html.includes('id="c"') ? '#c' : '#p';

  check(file + ': fills inside the frame when the window names the page above and the frame finds it to be its parent', () => {
    const f = inFrame(html);
    f.$(field).focus();
    const r = f.fill(SRC, Object.assign({ t: TOP }, data));
    expect(r === ok, 'answered ' + r);
  });

  check(file + ': a frame is refused when the window named no page, another page, or the frame is not right under the top page', () => {
    for (const [what, above, t] of [['no page named', [TOP], undefined], ['another page', [TOP], 'https://evil.example'],
      ['a frame of a frame', [TOP, 'https://mid.example'], TOP], ['no ancestorOrigins', undefined, TOP], ['it says nothing', [], TOP]]) {
      const f = inFrame(html, { above: above === undefined && what === 'no ancestorOrigins' ? null : above });
      f.$(field).focus();
      const r = f.fill(SRC, Object.assign({ t }, data));
      expect(r === 'frame', what + ': answered ' + r);
      expect(f.$(field).value === '', what + ': the field was filled');
    }
  });

  check(file + ': a frame whose address is not the one the window saw is refused', () => {
    const f = inFrame(html, { url: 'https://other.example/x' });
    f.$(field).focus();
    const r = f.fill(SRC, Object.assign({ t: TOP }, data));
    expect(r === 'origin' && f.$(field).value === '', 'answered ' + r);
  });
}

console.log('\n' + (ran - failed) + ' of ' + ran + ' checks passed.');
process.exit(failed === 0 ? 0 : 1);
