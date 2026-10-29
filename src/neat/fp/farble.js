// NEAT fingerprint protection. Runs in every page and frame before the page's own scripts
// (AddScriptToExecuteOnDocumentCreated), filled in by fp/fpjs.cs.
//
// It follows the idea Brave calls "farbling": instead of blocking the APIs that fingerprinters
// read, it changes what they read by a little, in a way that is
//   - the same every time on one site during one run of the browser (pages keep working), and
//   - different on another site, and different after the browser is restarted (so two sites
//     cannot match their visitor by the values).
// The numbers come from a secret made when the browser starts and the site's registrable
// domain (eTLD+1); a frame uses the domain of the top page, as in Brave.
//
// Levels (per site): 0 off, 1 standard, 2 strict. Standard changes values by a little or hides
// detail; strict goes further (noise instead of audio, random WebGL names, a fixed language list).
//
// What it cannot do from a page script (and Brave does inside the engine): it does not reach
// into Workers, it cannot hide that a function was replaced from a determined fingerprinter,
// and it leaves fonts, media queries and network fingerprints alone.
(function () {
  'use strict';

  const CFG = __NEAT_FP__; // { secret, def, sites: [[host, level], ...], gpc }

  // ---- 1. Which level does this page get -------------------------------------------------

  const isWeb = (p) => p === 'http:' || p === 'https:';

  // The top page's address, as far as this frame may know it.
  let topProto = location.protocol;
  let topHost = location.hostname;
  try {
    if (!isWeb(location.protocol)) {
      const o = location.origin;
      if (o && o !== 'null') {
        const u = new URL(o);
        topProto = u.protocol;
        topHost = u.hostname;
      }
    }
    if (window.top !== window) {
      const ao = location.ancestorOrigins;
      if (ao && ao.length) {
        const u = new URL(ao[ao.length - 1]);
        topProto = u.protocol;
        topHost = u.hostname;
      }
    }
  } catch (e) { /* keep what we have */ }

  if (!isWeb(topProto) || !topHost) return;

  let level = CFG.def | 0;
  {
    let best = -1;
    for (const [k, v] of CFG.sites) {
      if ((topHost === k || topHost.endsWith('.' + k)) && k.length > best) {
        best = k.length;
        level = v | 0;
      }
    }
  }
  if (level <= 0) return;
  const STRICT = level >= 2;

  // Run once per document, even if the script were added twice.
  const MARK = Symbol.for('neat.fp');
  try {
    if (window[MARK]) return;
    Object.defineProperty(window, MARK, { value: 1 });
  } catch (e) { return; }

  // ---- 2. Numbers that are stable per site and per run ---------------------------------

  // The registrable domain, without a public-suffix list: the last two labels, or three when
  // the second last is a common second-level word under a two-letter country code.
  const SLD = new Set(['co', 'com', 'org', 'net', 'gov', 'edu', 'ac', 'or', 'ne', 'go', 'gob', 'nom', 'mil']);
  function site(h) {
    if (/^[\d.]+$/.test(h) || h.indexOf(':') >= 0) return h;
    const p = h.split('.');
    const n = p.length;
    if (n <= 2) return h;
    if (p[n - 1].length === 2 && SLD.has(p[n - 2])) return p.slice(-3).join('.');
    return p.slice(-2).join('.');
  }
  const SITE = site(topHost);

  function hash128(str) {
    let h1 = 1779033703, h2 = 3144134277, h3 = 1013904242, h4 = 2773480762;
    for (let i = 0; i < str.length; i++) {
      const k = str.charCodeAt(i);
      h1 = h2 ^ Math.imul(h1 ^ k, 597399067);
      h2 = h3 ^ Math.imul(h2 ^ k, 2869860233);
      h3 = h4 ^ Math.imul(h3 ^ k, 951274213);
      h4 = h1 ^ Math.imul(h4 ^ k, 2716044179);
    }
    h1 = Math.imul(h3 ^ (h1 >>> 18), 597399067);
    h2 = Math.imul(h4 ^ (h2 >>> 22), 2869860233);
    h3 = Math.imul(h1 ^ (h3 >>> 17), 951274213);
    h4 = Math.imul(h2 ^ (h4 >>> 19), 2716044179);
    return [(h1 ^ h2 ^ h3 ^ h4) >>> 0, (h2 ^ h1) >>> 0, (h3 ^ h1) >>> 0, (h4 ^ h1) >>> 0];
  }

  // sfc32: a small fast generator; returns numbers in [0, 1).
  function sfc32(a, b, c, d) {
    return function () {
      a >>>= 0; b >>>= 0; c >>>= 0; d >>>= 0;
      let t = (a + b) | 0;
      a = b ^ (b >>> 9);
      b = (c + (c << 3)) | 0;
      c = (c << 21) | (c >>> 11);
      d = (d + 1) | 0;
      t = (t + d) | 0;
      c = (c + t) | 0;
      return (t >>> 0) / 4294967296;
    };
  }

  const BASE = CFG.secret + '|' + SITE + '|';

  // A generator for one purpose ("audio", "hwc", ...): the same on every call for one site and run.
  function rngFor(key, extra) {
    const s = hash128(BASE + key);
    if (extra !== undefined) { s[0] ^= extra >>> 0; s[3] = (s[3] + (extra >>> 0)) >>> 0; }
    const r = sfc32(s[0], s[1], s[2], s[3]);
    for (let i = 0; i < 12; i++) r();
    return r;
  }
  const below = (r, n) => Math.floor(r() * n);

  // ---- 3. Replacing functions without leaving obvious marks -----------------------------

  const natives = new WeakMap();
  const origToString = Function.prototype.toString;

  function fixup(fn, orig, prefix) {
    try {
      Object.defineProperty(fn, 'name', { value: prefix + orig.name, configurable: true });
      Object.defineProperty(fn, 'length', { value: orig.length, configurable: true });
    } catch (e) { /* not important */ }
    natives.set(fn, orig);
  }

  // A method replaced by handler(orig, thisArg, args).
  function hookMethod(obj, name, handler) {
    if (!obj) return false;
    const d = Object.getOwnPropertyDescriptor(obj, name);
    if (!d || typeof d.value !== 'function') return false;
    const orig = d.value;
    const fn = ({ w(...a) { return handler(orig, this, a); } }).w;
    fixup(fn, orig, '');
    d.value = fn;
    Object.defineProperty(obj, name, d);
    return true;
  }

  // A getter replaced by handler(realValue, thisArg); the real getter always runs first, so a
  // wrong `this` still fails the way the browser's own getter does.
  function hookGetter(obj, name, handler) {
    if (!obj) return false;
    const d = Object.getOwnPropertyDescriptor(obj, name);
    if (!d || typeof d.get !== 'function') return false;
    const orig = d.get;
    const fn = ({ w() { return handler(Reflect.apply(orig, this, []), this); } }).w;
    fixup(fn, orig, '');
    d.get = fn;
    Object.defineProperty(obj, name, d);
    return true;
  }

  // A getter that is new (the browser has none of that name).
  function addGetter(obj, name, value) {
    const fn = ({ ['get ' + name]() { return value; } })['get ' + name];
    natives.set(fn, 'function get ' + name + '() { [native code] }');
    Object.defineProperty(obj, name, { get: fn, set: undefined, enumerable: true, configurable: true });
  }

  const fakeToString = ({
    toString() {
      // A replaced function reports the text of the one it replaced; a new getter has its own text.
      const o = natives.get(this);
      if (typeof o === 'string') return o;
      return Reflect.apply(origToString, o || this, []);
    },
  }).toString;
  natives.set(fakeToString, origToString);
  try {
    const d = Object.getOwnPropertyDescriptor(Function.prototype, 'toString');
    d.value = fakeToString;
    Object.defineProperty(Function.prototype, 'toString', d);
  } catch (e) { /* leave it */ }

  const guard = (name, fn) => {
    try { fn(); } catch (e) { /* one feature failing must not break the others or the page */ }
  };

  // ---- 4. Canvas: a few pixels differ in the lowest bit ---------------------------------

  const PAIRS = 512; // as many changed bits as Brave uses
  const seedCanvas = hash128(BASE + 'canvas');

  function perturb(data) {
    const n = data.length;
    if (!n || n < 4) return;
    const px = n >>> 2;

    // The changes depend on the pixels too, so a blank canvas and a drawn one differ in different
    // places. Big images are hashed on a stride so the cost stays small.
    let h = 2166136261 ^ n;
    const step = n > 262144 ? ((n >>> 16) | 1) : 1;
    for (let i = 0; i < n; i += step) h = Math.imul(h ^ data[i], 16777619);

    const r = sfc32(seedCanvas[0] ^ h, seedCanvas[1], seedCanvas[2], (seedCanvas[3] + h) >>> 0);
    for (let i = 0; i < 12; i++) r();
    for (let i = 0; i < PAIRS; i++) {
      const p = below(r, px);
      const c = below(r, 3);
      if (r() < 0.5) data[4 * p + c] ^= 1;
    }
  }

  guard('canvas', () => {
    const C2D = typeof CanvasRenderingContext2D === 'function' ? CanvasRenderingContext2D.prototype : null;
    const O2D = typeof OffscreenCanvasRenderingContext2D === 'function' ? OffscreenCanvasRenderingContext2D.prototype : null;
    const HC = typeof HTMLCanvasElement === 'function' ? HTMLCanvasElement.prototype : null;
    const OC = typeof OffscreenCanvas === 'function' ? OffscreenCanvas.prototype : null;

    const oDraw = C2D && C2D.drawImage;
    const oGet = C2D && C2D.getImageData;
    const oPut = C2D && C2D.putImageData;
    const oGetContext = HC && HC.getContext;
    const oCreate = Document.prototype.createElement;
    const oToDataURL = HC && HC.toDataURL;
    const oToBlob = HC && HC.toBlob;
    const oConvert = OC && OC.convertToBlob;

    // The canvas itself is never changed: what is read comes from a copy.
    function copyOf(src) {
      const w = src.width, h = src.height;
      if (!w || !h) return null;
      let t, ctx;
      if (typeof HTMLCanvasElement === 'function' && src instanceof HTMLCanvasElement) {
        t = Reflect.apply(oCreate, document, ['canvas']);
        t.width = w; t.height = h;
        ctx = Reflect.apply(oGetContext, t, ['2d']);
      } else {
        t = new OffscreenCanvas(w, h);
        ctx = t.getContext('2d');
      }
      if (!ctx) return null;
      Reflect.apply(oDraw, ctx, [src, 0, 0]);
      const img = Reflect.apply(oGet, ctx, [0, 0, w, h]);
      perturb(img.data);
      Reflect.apply(oPut, ctx, [img, 0, 0]);
      return t;
    }

    if (C2D && oGet) {
      hookMethod(C2D, 'getImageData', (orig, self, a) => {
        const img = Reflect.apply(orig, self, a);
        try { perturb(img.data); } catch (e) { /* hand back what the browser gave */ }
        return img;
      });
    }
    if (O2D && O2D.getImageData) {
      hookMethod(O2D, 'getImageData', (orig, self, a) => {
        const img = Reflect.apply(orig, self, a);
        try { perturb(img.data); } catch (e) { /* as above */ }
        return img;
      });
    }
    if (HC && oToDataURL && oGet && oDraw && oPut && oGetContext) {
      hookMethod(HC, 'toDataURL', (orig, self, a) => {
        let t = null;
        try { t = copyOf(self); } catch (e) { /* tainted or unusable: the original answers (or throws) */ }
        return Reflect.apply(orig, t || self, a);
      });
      hookMethod(HC, 'toBlob', (orig, self, a) => {
        let t = null;
        try { t = copyOf(self); } catch (e) { /* as above */ }
        return Reflect.apply(orig, t || self, a);
      });
    }
    if (OC && oConvert && oGet && oDraw && oPut) {
      hookMethod(OC, 'convertToBlob', (orig, self, a) => {
        let t = null;
        try { t = copyOf(self); } catch (e) { /* as above */ }
        return Reflect.apply(orig, t || self, a);
      });
    }

    // WebGL pixels read back the same way a 2D canvas is read.
    for (const name of ['WebGLRenderingContext', 'WebGL2RenderingContext']) {
      const G = typeof window[name] === 'function' ? window[name].prototype : null;
      if (!G) continue;
      hookMethod(G, 'readPixels', (orig, self, a) => {
        const r = Reflect.apply(orig, self, a);
        try {
          // readPixels(x, y, width, height, format, type, pixels): RGBA bytes only.
          const dst = a[6];
          if (dst && ArrayBuffer.isView(dst) && a[5] === 0x1401 && a[4] === 0x1908) {
            const len = Math.min(dst.byteLength, a[2] * a[3] * 4);
            perturb(new Uint8Array(dst.buffer, dst.byteOffset, len));
          }
        } catch (e) { /* leave the pixels as read */ }
        return r;
      });
    }
  });

  // ---- 5. Audio: the numbers read back are a little off ---------------------------------

  guard('audio', () => {
    if (typeof AudioBuffer !== 'function') return;

    // 0.999 to 1: values stay inside -1..1 and a listener cannot tell.
    const fudge = 0.999 + rngFor('audio-fudge')() / 1000;

    // Strict replaces the data with a fixed noise of the same shape. The noise at a sample
    // depends only on its place, so reading a part of the data gives the same numbers as
    // reading all of it.
    const nseed = hash128(BASE + 'audio-noise');
    const noiseAt = (i) => (Math.imul((i ^ nseed[0]) >>> 0, 2654435761) ^ nseed[1]) >>> 0;
    const noise01 = (i) => {
      let h = noiseAt(i);
      h = Math.imul(h ^ (h >>> 15), 2246822507);
      h = Math.imul(h ^ (h >>> 13), 3266489909);
      return ((h ^ (h >>> 16)) >>> 0) / 4294967296;
    };
    const farble = (arr, len, from) => {
      if (STRICT) for (let i = 0; i < len; i++) arr[i] = noise01(from + i) / 10;
      else for (let i = 0; i < len; i++) arr[i] = arr[i] * fudge;
    };

    // The channels whose data was already changed in place by getChannelData; copyFromChannel
    // then reads them as they are, so nothing is changed twice.
    const changed = new WeakMap();
    hookMethod(AudioBuffer.prototype, 'getChannelData', (orig, self, a) => {
      const arr = Reflect.apply(orig, self, a);
      try {
        let set = changed.get(self);
        if (!set) { set = new Set(); changed.set(self, set); }
        const ch = Number(a[0]);
        if (!set.has(ch)) {
          set.add(ch);
          farble(arr, arr.length, 0);
        }
      } catch (e) { /* as read */ }
      return arr;
    });
    hookMethod(AudioBuffer.prototype, 'copyFromChannel', (orig, self, a) => {
      const r = Reflect.apply(orig, self, a);
      try {
        const dst = a[0];
        const set = changed.get(self);
        if (dst && dst.length && !(set && set.has(Number(a[1])))) farble(dst, dst.length, Number(a[2]) || 0);
      } catch (e) { /* as read */ }
      return r;
    });

    if (typeof AnalyserNode === 'function') {
      const P = AnalyserNode.prototype;
      const floaty = (orig, self, a) => {
        const r = Reflect.apply(orig, self, a);
        try {
          const dst = a[0];
          if (dst && dst.length) farble(dst, dst.length, 0);
        } catch (e) { /* as read */ }
        return r;
      };
      const bytey = (orig, self, a) => {
        const r = Reflect.apply(orig, self, a);
        try {
          const dst = a[0];
          if (dst && dst.length) {
            if (STRICT) {
              for (let i = 0; i < dst.length; i++) dst[i] = Math.max(0, Math.min(255, 128 * (1 + noise01(i) / 10)));
            } else {
              for (let i = 0; i < dst.length; i++) dst[i] = dst[i] * fudge;
            }
          }
        } catch (e) { /* as read */ }
        return r;
      };
      hookMethod(P, 'getFloatFrequencyData', floaty);
      hookMethod(P, 'getFloatTimeDomainData', floaty);
      hookMethod(P, 'getByteFrequencyData', bytey);
      hookMethod(P, 'getByteTimeDomainData', bytey);
    }
  });

  // ---- 6. navigator -----------------------------------------------------------------------

  guard('navigator', () => {
    const N = Navigator.prototype;

    // Cores: under 4 is common and stays; above it a value between 4 and the real count
    // (standard) or between 4 and 8 (strict).
    let cores = null;
    hookGetter(N, 'hardwareConcurrency', (real) => {
      if (cores === null) {
        if (typeof real !== 'number' || real < 4) cores = real;
        else {
          const top = STRICT ? 8 : real;
          cores = 4 + below(rngFor('hwc'), top + 1 - 4);
        }
      }
      return cores;
    });

    // Memory: standard picks between 4 and the real value, strict any of the five sizes.
    let mem = null;
    hookGetter(N, 'deviceMemory', (real) => {
      if (mem === null) {
        const valid = [2, 4, 8, 16, 32];
        if (typeof real !== 'number') mem = real;
        else if (!STRICT && real <= 2) mem = real;
        else {
          let lo = 1, hi = valid.length - 1;
          if (STRICT) lo = 0;
          else {
            const i = valid.indexOf(real);
            hi = i < 0 ? valid.length - 1 : i;
          }
          mem = hi <= lo ? valid[lo] : valid[lo + below(rngFor('mem'), hi + 1 - lo)];
        }
      }
      return mem;
    });

    // Languages: standard keeps the first one only, strict says English. The Accept-Language
    // header is changed to match (win.fp.cs).
    let langs = null;
    hookGetter(N, 'languages', (real) => {
      if (langs === null) {
        if (STRICT) langs = Object.freeze(['en-US', 'en']);
        else if (Array.isArray(real) && real.length > 1) langs = Object.freeze([real[0]]);
        else langs = real;
      }
      return langs;
    });
    if (STRICT) hookGetter(N, 'language', () => 'en-US');

    // "Do not sell or share": the browser has no such property; sites may read it.
    if (CFG.gpc && !('globalPrivacyControl' in N)) addGetter(N, 'globalPrivacyControl', true);
  });

  // ---- 7. Screen and window sizes ---------------------------------------------------------

  guard('screen', () => {
    const SIZES = [[1280, 800], [1366, 768], [1440, 900], [1680, 1050], [1920, 1080], [2560, 1440], [3840, 2160]];

    const own = (name) => Object.getOwnPropertyDescriptor(window, name);
    const outerW = own('outerWidth'), outerH = own('outerHeight');
    const real = {
      w: () => (outerW && outerW.get ? Reflect.apply(outerW.get, window, []) : window.innerWidth),
      h: () => (outerH && outerH.get ? Reflect.apply(outerH.get, window, []) : window.innerHeight),
    };

    // The screen is said to be the smallest common size the window fits in.
    const fakeScreen = () => {
      const w = real.w(), h = real.h();
      for (const s of SIZES) if (s[0] >= w && s[1] >= h) return s;
      return null;
    };

    const S = typeof Screen === 'function' ? Screen.prototype : null;
    if (S) {
      hookGetter(S, 'width', (v) => { const s = fakeScreen(); return s ? s[0] : v; });
      hookGetter(S, 'height', (v) => { const s = fakeScreen(); return s ? s[1] : v; });
      hookGetter(S, 'availWidth', (v) => { const s = fakeScreen(); return s ? s[0] : v; });
      hookGetter(S, 'availHeight', (v) => { const s = fakeScreen(); return s ? s[1] : v; });
      const near0 = (key) => () => below(rngFor(key), 9);
      hookGetter(S, 'availLeft', near0('screen-left'));
      hookGetter(S, 'availTop', near0('screen-top'));
    }

    // Where the window is, and how big: values near zero, and near the page's own size.
    const redefine = (name, get) => {
      const d = own(name);
      if (!d || typeof d.get !== 'function') return;
      const fn = ({ w() { return get(); } }).w;
      fixup(fn, d.get, '');
      d.get = fn;
      Object.defineProperty(window, name, d);
    };
    const topInner = (prop) => {
      try { return window.top[prop]; } catch (e) { return window[prop]; }
    };
    redefine('outerWidth', () => topInner('innerWidth') + below(rngFor('outer-w'), 9));
    redefine('outerHeight', () => topInner('innerHeight') + below(rngFor('outer-h'), 9));
    redefine('screenX', () => below(rngFor('screen-x'), 9));
    redefine('screenLeft', () => below(rngFor('screen-x'), 9));
    redefine('screenY', () => below(rngFor('screen-y'), 9));
    redefine('screenTop', () => below(rngFor('screen-y'), 9));

    // Mouse events carry the pointer's place on the screen; make it the place in the page plus
    // a fixed small offset.
    if (typeof MouseEvent === 'function') {
      const M = MouseEvent.prototype;
      hookGetter(M, 'screenX', (v, self) => (typeof self.clientX === 'number' ? self.clientX + below(rngFor('screen-x'), 9) : v));
      hookGetter(M, 'screenY', (v, self) => (typeof self.clientY === 'number' ? self.clientY + below(rngFor('screen-y'), 9) : v));
    }
  });

  // ---- 8. Camera, microphone and speaker lists --------------------------------------------

  guard('devices', () => {
    if (typeof MediaDevices !== 'function') return;
    hookMethod(MediaDevices.prototype, 'enumerateDevices', (orig, self, a) => {
      const p = Reflect.apply(orig, self, a);
      return p.then((list) => {
        try {
          // The first device (the default) stays first; the rest are shuffled.
          if (Array.isArray(list) && list.length > 2) {
            const r = rngFor('devices');
            for (let i = list.length - 1; i > 1; i--) {
              const j = 1 + below(r, i);
              const t = list[i]; list[i] = list[j]; list[j] = t;
            }
          }
        } catch (e) { /* as listed */ }
        return list;
      });
    });
  });

  // ---- 9. WebGL: the graphics card's name -------------------------------------------------

  guard('webgl', () => {
    const VENDOR = 0x9245, RENDERER = 0x9246; // UNMASKED_VENDOR_WEBGL, UNMASKED_RENDERER_WEBGL
    const ALNUM = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
    const fake = (p) => {
      if (!STRICT) return 'NEAT';
      const r = rngFor('webgl-' + p);
      let s = '';
      for (let i = 0; i < 8; i++) s += ALNUM[below(r, ALNUM.length)];
      return s;
    };
    for (const name of ['WebGLRenderingContext', 'WebGL2RenderingContext']) {
      const G = typeof window[name] === 'function' ? window[name].prototype : null;
      if (!G) continue;
      hookMethod(G, 'getParameter', (orig, self, a) => {
        const r = Reflect.apply(orig, self, a);
        const p = a[0];
        if ((p === VENDOR || p === RENDERER) && typeof r === 'string') return fake(p);
        return r;
      });
    }
  });
})();
