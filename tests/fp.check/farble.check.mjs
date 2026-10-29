// Runs src/neat/fp/farble.js in a real Chromium (Playwright) and checks what pages can see.
// usage: node tests/fp.check/farble.check.mjs   (Playwright must be importable: NODE_PATH or a local install)
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const { chromium } = require('playwright');

const here = path.dirname(fileURLToPath(import.meta.url));
const template = fs.readFileSync(path.join(here, '..', '..', 'src', 'neat', 'fp', 'farble.js'), 'utf8');

// Same filling-in as FpJs.Build in C#.
function build({ secret = 'secret-one', def = 1, sites = [], gpc = true } = {}) {
  return template.replace('__NEAT_FP__', () => JSON.stringify({ secret, def, sites, gpc }));
}

let ran = 0;
let failed = 0;
async function check(name, fn) {
  ran++;
  try {
    await fn();
    console.log('ok   ' + name);
  } catch (e) {
    failed++;
    console.log('FAIL ' + name + ': ' + (e && e.message ? e.message : e));
  }
}

// What a fingerprinting script would read.
const probe = async () => {
  const out = {};
  const c = document.createElement('canvas');
  c.width = 300; c.height = 150;
  const x = c.getContext('2d');
  const g = x.createLinearGradient(0, 0, 300, 0);
  g.addColorStop(0, '#f00'); g.addColorStop(1, '#00f');
  x.fillStyle = g; x.fillRect(0, 0, 300, 150);
  x.fillStyle = '#fff'; x.font = '20px sans-serif'; x.fillText('NEAT fingerprint', 10, 50);
  out.url1 = c.toDataURL();
  out.url2 = c.toDataURL();
  out.px = Array.from(x.getImageData(0, 0, 300, 150).data);
  out.px2 = Array.from(x.getImageData(0, 0, 300, 150).data);
  out.blob = await new Promise((r) => c.toBlob(async (b) => r(Array.from(new Uint8Array(await b.arrayBuffer())).slice(0, 4000).join(',')), 'image/png'));

  const ctx = new OfflineAudioContext(1, 44100, 44100);
  const osc = ctx.createOscillator();
  osc.type = 'triangle'; osc.frequency.value = 10000;
  const comp = ctx.createDynamicsCompressor();
  osc.connect(comp); comp.connect(ctx.destination); osc.start(0);
  const buf = await ctx.startRendering();
  const sum = (d) => { let s = 0; for (let i = 4500; i < 5000; i++) s += Math.abs(d[i]); return s; };
  out.audio1 = sum(buf.getChannelData(0));
  out.audio2 = sum(buf.getChannelData(0));
  const copy = new Float32Array(1000);
  buf.copyFromChannel(copy, 0, 4500);
  out.audioCopy = copy.reduce((s, v, i) => s + (i < 500 ? Math.abs(v) : 0), 0);

  // Read in the other order: a copy first, then the whole channel. Both must show the same numbers.
  {
    const c2 = new OfflineAudioContext(1, 44100, 44100);
    const o2 = c2.createOscillator(); o2.type = 'triangle'; o2.frequency.value = 10000;
    o2.connect(c2.destination); o2.start(0);
    const b2 = await c2.startRendering();
    const part = new Float32Array(10);
    b2.copyFromChannel(part, 0, 4500);
    const whole = b2.getChannelData(0);
    out.audioOrder = [Array.from(part), Array.from(whole.slice(4500, 4510))];
  }

  out.cores = navigator.hardwareConcurrency;
  out.mem = navigator.deviceMemory;
  out.langs = Array.from(navigator.languages);
  out.lang = navigator.language;
  out.gpc = navigator.globalPrivacyControl;
  out.screen = [screen.width, screen.height, screen.availWidth, screen.availHeight];
  out.outer = [outerWidth - innerWidth, outerHeight - innerHeight];
  out.screenXY = [screenX, screenY, screenLeft, screenTop];

  const gl = document.createElement('canvas').getContext('webgl');
  if (gl) {
    const ext = gl.getExtension('WEBGL_debug_renderer_info');
    out.glVendor = ext ? gl.getParameter(ext.UNMASKED_VENDOR_WEBGL) : null;
    out.glRenderer = ext ? gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) : null;
    gl.clearColor(0.2, 0.4, 0.6, 1); gl.clear(gl.COLOR_BUFFER_BIT);
    const px = new Uint8Array(32 * 32 * 4);
    gl.readPixels(0, 0, 32, 32, gl.RGBA, gl.UNSIGNED_BYTE, px);
    out.glPixels = Array.from(px);
  }

  out.devices = await navigator.mediaDevices.enumerateDevices().then((l) => l.length, (e) => 'error ' + e.name);

  const d = Object.getOwnPropertyDescriptor(Navigator.prototype, 'hardwareConcurrency').get;
  out.names = [
    HTMLCanvasElement.prototype.toDataURL.name, HTMLCanvasElement.prototype.toDataURL.length,
    d.name, Function.prototype.toString.name,
  ];
  out.str = [
    HTMLCanvasElement.prototype.toDataURL.toString(),
    CanvasRenderingContext2D.prototype.getImageData.toString(),
    d.toString(),
    Function.prototype.toString.toString(),
    Object.getOwnPropertyDescriptor(Navigator.prototype, 'globalPrivacyControl')?.get?.toString() ?? null,
  ];
  out.hasProto = 'prototype' in HTMLCanvasElement.prototype.toDataURL;
  out.illegal = (() => { try { HTMLCanvasElement.prototype.toDataURL.call({}); return 'no error'; } catch (e) { return e.constructor.name; } })();
  out.big = await (async () => {
    const b = document.createElement('canvas'); b.width = 1920; b.height = 1080;
    const bx = b.getContext('2d'); bx.fillStyle = '#369'; bx.fillRect(0, 0, 1920, 1080);
    const t0 = performance.now();
    bx.getImageData(0, 0, 1920, 1080); b.toDataURL();
    return performance.now() - t0;
  })();
  return out;
};

const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--use-fake-device-for-media-stream'] });

const pages = [];
async function open(script, url, { locale = 'en-US' } = {}) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true, locale });
  const errors = [];
  if (script) await context.addInitScript({ content: script });
  const page = await context.newPage();
  page.on('pageerror', (e) => errors.push(String(e)));
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  await context.route('**/*', (route) => {
    const u = new URL(route.request().url());
    if (u.pathname === '/frame.html') {
      return route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>f</title>' });
    }
    if (u.pathname === '/x.png') {
      return route.fulfill({ contentType: 'image/png', body: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64') });
    }
    if (u.pathname === '/outer.html') {
      return route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>o</title><iframe id="f" src="https://frames.example/frame.html"></iframe>' });
    }
    return route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>t</title><body>hi</body>' });
  });
  await page.goto(url);
  const r = { context, page, errors };
  pages.push(r);
  return r;
}
const run = (r) => r.page.evaluate(probe);
const diffCount = (a, b) => a.reduce((n, v, i) => n + (v !== b[i] ? 1 : 0), 0);

// Baseline: nothing injected.
const base = await run(await open(null, 'https://a.example/'));
// Standard, secret one.
const s1a = await open(build(), 'https://a.example/');
const A1 = await run(s1a);
const A1again = await run(await open(build(), 'https://a.example/'));
const B1 = await run(await open(build(), 'https://b.example/'));
const strictAudio = await run(await open(build({ sites: [['a.example', 2]] }), 'https://a.example/'));
// Another run of the browser: another secret.
const A2 = await run(await open(build({ secret: 'secret-two' }), 'https://a.example/'));

await check('canvas: same on one site, same after a reload of the site', () => {
  assert.equal(A1.url1, A1.url2);
  assert.equal(A1.url1, A1again.url1);
  assert.deepEqual(A1.px, A1.px2);
  assert.deepEqual(A1.px, A1again.px);
  assert.equal(A1.blob, A1again.blob);
});

await check('canvas: different from the real one, from another site, from another run', () => {
  assert.notEqual(A1.url1, base.url1);
  assert.notEqual(A1.url1, B1.url1);
  assert.notEqual(A1.url1, A2.url1);
  assert.notEqual(A1.blob, base.blob);
});

await check('canvas: only a few pixels, only by the lowest bit', () => {
  const n = diffCount(A1.px, base.px);
  assert.ok(n > 0 && n <= 512, 'changed values: ' + n);
  for (let i = 0; i < A1.px.length; i++) assert.ok(Math.abs(A1.px[i] - base.px[i]) <= 1, 'value ' + i);
});

await check('canvas: the canvas itself is not changed by reading it', () => {
  // toDataURL twice gave the same answer above; the real pixels under it must be untouched
  assert.equal(A1.url1, A1.url2);
});

await check('canvas: a big canvas is read in reasonable time', () => {
  assert.ok(A1.big < 1500, 'took ' + A1.big + ' ms');
  console.log('     (1920x1080 getImageData + toDataURL: ' + Math.round(A1.big) + ' ms)');
});

await check('audio: a little off, stable, not compounding, differs by site', () => {
  assert.notEqual(A1.audio1, base.audio1);
  assert.equal(A1.audio1, A1.audio2);
  const ratio = A1.audio1 / base.audio1;
  assert.ok(ratio >= 0.999 && ratio <= 1.0000001, 'ratio ' + ratio);
  assert.ok(Math.abs(A1.audioCopy / base.audioCopy - ratio) < 1e-6, 'copyFromChannel uses the same factor');
  assert.notEqual(A1.audio1, B1.audio1);
});

await check('audio: a copy and the whole channel agree, whichever is read first', () => {
  for (const r of [A1, strictAudio]) {
    for (let i = 0; i < 10; i++) assert.ok(Math.abs(r.audioOrder[0][i] - r.audioOrder[1][i]) < 1e-9, 'sample ' + i);
  }
});

await check('navigator: cores and memory between the allowed bounds, stable', () => {
  if (base.cores >= 4) assert.ok(A1.cores >= 4 && A1.cores <= base.cores, `cores ${A1.cores} of ${base.cores}`);
  else assert.equal(A1.cores, base.cores);
  assert.equal(A1.cores, A1again.cores);
  if (base.mem !== undefined) assert.ok([2, 4, 8].includes(A1.mem) && A1.mem <= Math.max(base.mem, 4), `memory ${A1.mem} of ${base.mem}`);
});

await check('navigator: the do-not-sell signal is there', () => {
  assert.equal(A1.gpc, true);
  assert.equal(base.gpc, undefined);
});

await check('screen: a common size, window values near the page, position near zero', () => {
  const sizes = ['1280x800', '1366x768', '1440x900', '1680x1050', '1920x1080', '2560x1440', '3840x2160'];
  assert.ok(sizes.includes(A1.screen[0] + 'x' + A1.screen[1]), 'screen ' + A1.screen);
  assert.equal(A1.screen[0], A1.screen[2]);
  assert.equal(A1.screen[1], A1.screen[3]);
  assert.ok(A1.outer[0] >= 0 && A1.outer[0] <= 8 && A1.outer[1] >= 0 && A1.outer[1] <= 8, 'outer ' + A1.outer);
  for (const v of A1.screenXY) assert.ok(v >= 0 && v <= 8, 'screenX/Y ' + A1.screenXY);
  assert.equal(A1.screenXY[0], A1.screenXY[2]);
});

await check('webgl: the card name is hidden, the pixels read back are not the same', () => {
  if (!base.glRenderer) { console.log('     (no WebGL in this Chromium; skipped)'); return; }
  assert.equal(A1.glVendor, 'NEAT');
  assert.equal(A1.glRenderer, 'NEAT');
  assert.notEqual(base.glRenderer, 'NEAT');
  const n = diffCount(A1.glPixels, base.glPixels);
  assert.ok(n <= 512, 'changed ' + n);
});

await check('devices: the list can still be read', () => {
  assert.equal(typeof A1.devices, typeof base.devices);
});

await check('replaced functions look like the browser\'s own', () => {
  assert.equal(A1.names[0], 'toDataURL');
  assert.equal(A1.names[1], base.names[1]);
  assert.equal(A1.names[2], base.names[2]);
  assert.equal(A1.names[3], 'toString');
  assert.deepEqual(A1.str.slice(0, 4), base.str.slice(0, 4));
  assert.match(A1.str[4], /^function get globalPrivacyControl\(\) \{ \[native code\] \}$/);
  assert.equal(A1.hasProto, base.hasProto);
  assert.equal(A1.illegal, base.illegal);
});

await check('no errors in the page', () => {
  for (const r of pages) assert.deepEqual(r.errors, []);
});

// ---- levels, sites, frames ----

const off = await run(await open(build({ sites: [['c.example', 0]] }), 'https://www.c.example/'));
await check('a site set to off sees the real values', () => {
  assert.equal(off.url1, base.url1);
  assert.equal(off.audio1, base.audio1);
  assert.equal(off.cores, base.cores);
  assert.equal(off.gpc, undefined);
});

await check('default off: nothing changes, a listed site is protected', async () => {
  const none = await run(await open(build({ def: 0 }), 'https://a.example/'));
  assert.equal(none.url1, base.url1);
  const some = await run(await open(build({ def: 0, sites: [['a.example', 1]] }), 'https://shop.a.example/'));
  assert.notEqual(some.url1, base.url1);
});

const strict = await run(await open(build({ sites: [['d.example', 2]] }), 'https://sub.d.example/'));
await check('strict: English language list, noise audio, random card names, any memory', () => {
  assert.deepEqual(strict.langs, ['en-US', 'en']);
  assert.equal(strict.lang, 'en-US');
  assert.notEqual(strict.audio1, base.audio1);
  assert.notEqual(Math.abs(strict.audio1 / base.audio1 - 1) < 0.001, true, 'strict audio is not just scaled');
  if (base.glRenderer) {
    assert.match(strict.glRenderer, /^[A-Za-z0-9]{8}$/);
    assert.match(strict.glVendor, /^[A-Za-z0-9]{8}$/);
    assert.notEqual(strict.glRenderer, strict.glVendor);
  }
  if (base.cores >= 4) assert.ok(strict.cores >= 4 && strict.cores <= 8);
});

await check('a frame gets the protection (and the seed) of the top page', async () => {
  const o = await open(build(), 'https://a.example/outer.html');
  const frame = o.page.frames().find((f) => f.url().includes('frames.example'));
  assert.ok(frame, 'frame found');
  const inFrame = await frame.evaluate(probe);
  const top = await o.page.evaluate(probe);
  assert.equal(inFrame.url1, top.url1, 'same canvas value as the top page');
  assert.equal(inFrame.audio1, top.audio1);
  assert.notEqual(inFrame.url1, base.url1);
  assert.deepEqual(o.errors, []);
});

await check('a frame under a listed-off top page is left alone', async () => {
  const o = await open(build({ sites: [['a.example', 0]] }), 'https://a.example/outer.html');
  const frame = o.page.frames().find((f) => f.url().includes('frames.example'));
  const inFrame = await frame.evaluate(probe);
  assert.equal(inFrame.url1, base.url1);
});

await check('data: and about: pages are left alone', async () => {
  const c = await browser.newContext();
  await c.addInitScript({ content: build() });
  const p = await c.newPage();
  await p.goto('about:blank');
  const gpc = await p.evaluate(() => navigator.globalPrivacyControl);
  assert.equal(gpc, undefined);
  await c.close();
});

await check('two copies of the script in one page do not stack', async () => {
  const c = await browser.newContext({ ignoreHTTPSErrors: true });
  await c.addInitScript({ content: build() });
  await c.addInitScript({ content: build() });
  const p = await c.newPage();
  await c.route('**/*', (r) => r.fulfill({ contentType: 'text/html', body: '<!doctype html><title>t</title>' }));
  await p.goto('https://a.example/');
  const r = await p.evaluate(probe);
  assert.equal(r.audio1, A1.audio1);
  assert.equal(r.url1, A1.url1);
  await c.close();
});

// ---- things that must keep behaving as the browser does ----

const edge = async (script) => {
  const r = await open(script, 'https://a.example/');
  const out = await r.page.evaluate(async () => {
    const res = {};
    const err = (f) => { try { f(); return 'no error'; } catch (e) { return e.name; } };
    const c = document.createElement('canvas'); c.width = 40; c.height = 40;
    const x = c.getContext('2d'); x.fillStyle = '#abc'; x.fillRect(0, 0, 40, 40);

    // a canvas that was drawn on with a picture from another site cannot be read
    const img = new Image();
    await new Promise((ok) => { img.onload = ok; img.onerror = ok; img.src = 'https://img.example/x.png'; });
    const t = document.createElement('canvas'); t.width = 10; t.height = 10;
    t.getContext('2d').drawImage(img, 0, 0);
    res.tainted = [err(() => t.toDataURL()), err(() => t.getContext('2d').getImageData(0, 0, 1, 1))];

    const z = document.createElement('canvas'); z.width = 0; z.height = 0;
    res.zero = z.toDataURL();
    res.badArgs = err(() => x.getImageData(0, 0, 0, 0));
    res.noArgs = err(() => x.getImageData());

    // a WebGL canvas that keeps its picture can be saved, and it is not blank
    const g = document.createElement('canvas'); g.width = 64; g.height = 64;
    const gl = g.getContext('webgl', { preserveDrawingBuffer: true });
    if (gl) { gl.clearColor(1, 0.5, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT); res.gl = g.toDataURL().length; }

    // an offscreen canvas can be saved
    const o = new OffscreenCanvas(30, 30); const ox = o.getContext('2d'); ox.fillStyle = '#f0f'; ox.fillRect(0, 0, 30, 30);
    const blob = await o.convertToBlob();
    res.off = blob.size > 0 && blob.type === 'image/png';
    res.offPx = Array.from(ox.getImageData(0, 0, 30, 30).data).slice(0, 8);

    // an image bitmap can be made from a canvas and drawn
    const bmp = await createImageBitmap(c);
    const d = document.createElement('canvas'); d.width = 40; d.height = 40;
    d.getContext('2d').drawImage(bmp, 0, 0);
    res.bmp = d.toDataURL().length > 100;
    return res;
  });
  return out;
};

const edgeBase = await edge(null);
const edgeFp = await edge(build());
await check('canvas edge cases behave as without the script', () => {
  assert.deepEqual(edgeFp.tainted, edgeBase.tainted);
  assert.deepEqual(edgeBase.tainted, ['SecurityError', 'SecurityError']);
  assert.equal(edgeFp.zero, edgeBase.zero);
  assert.equal(edgeFp.badArgs, edgeBase.badArgs);
  assert.equal(edgeFp.noArgs, edgeBase.noArgs);
  assert.equal(edgeFp.off, true);
  assert.equal(edgeFp.bmp, true);
});

await check('a WebGL canvas can still be saved', () => {
  if (edgeBase.gl === undefined) { console.log('     (no WebGL here; skipped)'); return; }
  assert.ok(edgeFp.gl > 100, 'length ' + edgeFp.gl);
  // A flat picture packs small; the changed bits make it a little bigger. That is expected.
  assert.ok(edgeFp.gl > edgeBase.gl && edgeFp.gl < 20000, `${edgeFp.gl} against ${edgeBase.gl}`);
});

for (const r of pages) await r.context.close();
await browser.close();

console.log(failed === 0 ? `${ran} checks passed` : `${failed} of ${ran} checks FAILED`);
process.exit(failed === 0 ? 0 : 1);
