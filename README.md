# NEAT Browser

A browser built with WinUI 3 and WebView2, with a vertical tab sidebar.

**Status:** phase W6.2, window colour. The window gradient is generated from one
hue. The palette button at the foot of the sidebar opens a menu with a colour
square (across is the hue, down is the brightness; drag the dot), a Dark/Light
switch and ready-made hues. The choice is saved.

## Requirements

- Windows 10 1809 or newer, x64
- .NET 8 SDK

The Windows App SDK runtime, the .NET runtime and the WebView2 runtime are all
bundled, so nothing else needs to be installed to run a published build.

## Build

```powershell
dotnet restore src/neat/neat.csproj -p:Platform=x64
dotnet build   src/neat/neat.csproj -c Release --no-restore -p:Platform=x64
```

Always pass `-p:Platform=x64`.

## Get a runnable build from GitHub

Every push to `main` runs the **Build** workflow. Open the run on the
**Actions** tab and download the `neat-win-x64` artifact. Unzip it and run
`neat.exe`. The first restore is slow because the bundled WebView2 runtime is
about 250 MB.

## Layout

Folder and file names are lowercase, in the style of large browser codebases.

```
src/neat/
  neat.csproj       project (unpackaged, self-contained, x64)
  app.xaml(.cs)     application entry, look constants, shared services
  win.xaml(.cs)     main window: tabs, navigation, bookmarks
  win.side.cs       sidebar states (docked, hidden, peeking) and tab row look
  win.bar.cs        title bar: click-through regions, address chip, copy link
  win.keys.cs       shortcut table and the script that reports keys pressed inside pages
  win.cmd.cs        command bar: the box and its list of suggestions, arrow keys
  win.sess.cs       session: open tabs kept for the next start, closed tabs (Ctrl+Shift+T)
  win.full.cs       full screen (F11)
  web/env.cs        app folders; points WebView2 at the bundled runtime
  web/tab.cs        one tab: its web view, title, address and favicon
  look/look.cs      window colour: turns the saved tint into the window gradient
  look/win.paint.cs colour menu: dark/light, gradient and opacity sliders, swatches
  look/win.pad.cs   colour pad: drag or click the big dot, add/remove dots, change harmony
  look/backdrop.cs  the blur behind the window: Windows Acrylic (or Mica), as in Zen
  look/win.wash.cs  the window gradient as layers that fade to clear (Zen's shape), over the blur
  look/win.grain.cs texture: the layer of fine noise over the window colour, and its dial
  look/wheel.cs     colour pad maths: angle and distance from the centre to a colour (as in Zen)
  look/harmony.cs   how up to three dots relate (complementary, triadic, ...), as in Zen
  look/contrast.cs  text contrast maths: keeps the window readable whatever colour is picked
  look/grain.cs     texture maths: the sixteen dial steps and the noise tile
  data/prefs.cs     what settings.json holds (home page, search engines, window size)
  data/store.cs     loads and saves settings.json
  data/search.cs    turns typed text into an address or a search
  data/session.cs   session.json: the tabs that were open, for "Continue where you left off"
  data/suggest.cs   the rows of the command bar: open tabs, bookmarks, history, typed text
  data/db.cs        connections to browser.db
  data/history.cs   history table
  data/marks.cs     bookmarks table (flat list, no folders yet)
  vault/vault.cs    password vault core, no screen yet: add, change, remove, list, reveal, lock
  vault/types.cs    its results, the sign-in proof (Access), the secret that wipes itself, file shape
  vault/seal.cs     AES-256-GCM sealing and the data that ties a value to its entry, site and field
  vault/keywrap.cs  seals the data key for the Windows user (DPAPI, called directly, no package)
  vault/origin.cs   the one way a site is written: scheme, host in ASCII, port
  vault/gate.cs     the gate: who may show, copy or delete a password (Hello or NEAT password), how long
  vault/capture.cs  a login a page just showed, held until it is known whether the login worked
  vault/never.cs    the sites the person said never to offer to save a password for (kept in settings.json)
  pass/capture.cs   the two page scripts of the save-password bubble (JavaScript in C# strings)
  win.pass.cs       the save-password bubble: takes the page's report, decides what to offer, shows the bubble
  win.fill.cs       the key button in the address chip and the list of logins to fill; runs the fill script
  pass/fill.cs      the script that fills a login into a page (JavaScript in a C# string)
  vault/lookalike.cs how an address is shown before filling, and the warning for fake-looking ones
  vault/csv.cs      reading and writing CSV, the format other browsers export passwords in
  vault/exchange.cs turns another browser's password export into logins, and logins into one
  opts.pass.exchange.cs Settings > Passwords, third part: never-save list, editing a login, import and export
  pass/hello.cs     Windows Hello, behind the gate's IHello
  pass/clip.cs      SecretClip: copy a password, not in clipboard history or cloud, cleared after 30 seconds
  opts.pass.cs      Settings > Passwords: protection, saved logins, show / copy / delete
  opts.pass.task.cs the cards that page opens: add a login, set or change the NEAT password, confirm a delete
  ads/ads.cs        the ad blocker (App.Ads): loads the engine in the background, judges requests, popups and pages
  ads/engine.cs     one loaded filter release behind neat_adblock.dll's C functions (thread-safe, frees itself)
  ads/native.cs     the C functions of neat_adblock.dll (P/Invoke)
  ads/filterdir.cs  checks a filter folder against its manifest.json hashes before the engine reads it
  ads/cosmetic.cs   what the engine says to hide or run on a page (read from its JSON)
  ads/pagejs.cs     fills in ads/page.js for one host
  ads/page.js       the script every page gets: scriptlets, hiding by style sheet, generic rules by class and id
  ads/blockpage.cs  the warning shown instead of a page that a page rule ($document / $all) stops; "Continue anyway" opens it
  win.blk.cs        NEAT Blocker's chip in the title bar ("Blocked: N", beside Reload) and its panel (this page, in total, a switch for this site, the lists in use)
  ads/filterupdate.cs  where releases live (FilterStore) and the list updater (FilterUpdater): download, check, switch
  ads/requestlog.cs  optional one-line-per-request log (ads-requests.tsv), for finding out why a host got through
  ads/adurl.cs, dataurl.cs, reqtype.cs, tabads.cs   small helpers: addresses, data: URLs, request types, per-tab state
  win.ads.cs        the window's side of the blocker: WebResourceRequested, popups, page scripts
  fp/farble.js      the fingerprint protection's page script: canvas, audio, navigator, screen, devices, WebGL
  fp/fp.cs, fpprefs.cs  which level a site gets, what the script is filled in with, the header values, the settings
  win.fp.cs         the window's side: registers the script in every tab (again when settings change), Sec-GPC and Accept-Language
tools/fetch-ads.ps1 downloads neat_adblock.dll and the filter lists into ads-dl\ (CI runs it before the build)
tests/ads.check/    the blocker's self-checks: C# pieces (dotnet run) and the page script (node page.check.mjs)
tests/fp.check/     the fingerprint protection's self-checks: C# pieces (dotnet run) and the page script in a real Chromium (node farble.check.mjs, needs Playwright)
tests/vault.check/  the vault's self-check (plain asserts, no packages), run by CI before the build
tests/capture.check/ the page scripts' self-check: made-up login forms in jsdom (needs Node; npm ci, npm test)
```

## Ad blocker

Blocking is done by Brave's adblock-rust, built as `neat_adblock.dll` (repository `neat-adblock`),
fed by the uBlock Origin lists that `neat-filters` rebuilds every 6 hours. At build time
`tools/fetch-ads.ps1` puts the dll next to `neat.exe` and the lists in a `filters` folder there.
Without them the app still runs, with no blocker (the log says so: `ads` lines in the log).

What it does today: stops network requests, replaces some with harmless stand-ins (blank script,
pixel), stops popups, hides elements (cosmetic rules, including generic ones by class and id) and
runs uBO scriptlets before the page's own scripts, handles procedural cosmetic rules (`:has-text`
and the like), watches requests from workers, and stops a page itself when a page rule (`$document`,
`$all`) says so, with a warning that has "Go back" and "Continue anyway" (as uBO, an ordinary
`||host^` rule does not stop a visit). What it does not do yet: `$removeparam`, `$csp`, HTML
filtering.

Lists update themselves: 30 seconds after start and then every 6 hours the blocker reads the
latest `neat-filters` release (its ordinary web addresses on github.com, not the API, which allows only 60 requests an hour for each address), and when its version is newer than
the one in use it downloads the files that changed (the others are copied from what is here),
checks every file against the manifest, and only then switches to the new release without a
restart. Releases are kept in `%LOCALAPPDATA%\NEAT\filters-dl\<version>` (the newest two); the
release next to neat.exe is the fallback. The engine built from the release in use is saved to
`%LOCALAPPDATA%\NEAT\ads-cache\engine.bin` (about 40 MB), so the next start does not read the
lists again. For this the `neat-filters` releases must be public.

NEAT Blocker panel: the chip "Blocked: N" in the title bar, beside Reload, shows the total stopped
since NEAT started. Clicking it opens a panel at the page's top-left corner (it moves with the page
when the sidebar is docked or put away) with the count for this page and in total, a switch for the
blocker on this site (it adds the site to `Off` and reloads the page) and the filter version.
A site in `Off` also covers its subdomains: `example.com` switches the blocker off on `www.example.com`.

Settings (`settings.json`, browser closed):
`"Ads": { "On": true, "Off": ["example.com"], "Update": true, "UpdateHours": 6, "UpdateRepo": "https://github.com/neatbrowser/neat-filters" }`.
`On: false` switches the blocker off; `Off` lists hosts where it does nothing; `Update: false` keeps
the lists that are there; `UpdateHours` is how often to look (1 to 168).

## Fingerprint protection

Sites can recognise a device without cookies, by reading small details: how a canvas is drawn, how
the audio engine rounds, the number of cores, the screen, the graphics card's name. NEAT does what
Brave calls "farbling": it changes what the page reads by a little, so that

- on one site the values stay the same (pages keep working),
- on another site they are different, and they are different again after NEAT is restarted
  (two sites cannot match their visitor by them).

The numbers come from a secret made at each start and the site's registrable domain; a frame uses
the top page's domain. The script (`fp/farble.js`) is registered in every tab and runs before the
page's own scripts, in the page and in each frame.

| | Standard (default) | Strict |
|---|---|---|
| Canvas (`getImageData`, `toDataURL`, `toBlob`, `convertToBlob`, WebGL `readPixels`) | up to 512 lowest bits flipped, from a copy | same |
| Audio (`getChannelData`, `copyFromChannel`, analyser reads) | values times 0.999 to 1 | seeded noise |
| `hardwareConcurrency`, `deviceMemory` | between 4 and the real value | between 4 and 8 / any size |
| `navigator.languages` (and the Accept-Language header) | the first language only | `en-US, en` |
| Screen and window sizes, window position, pointer position on screen | the smallest common screen size the window fits in; position near 0 | same |
| Camera, microphone and speaker list | shuffled after the first | same |
| WebGL graphics card name | `NEAT` | random text |
| `Sec-GPC: 1` and `navigator.globalPrivacyControl` | on protected pages | same |

Chosen per site from the NEAT Blocker panel (the page reloads); the default is in Settings > General >
Privacy. A site in the list also covers its subdomains. WebRTC is told to use only the route normal
traffic takes (a VPN is not bypassed; takes effect after a restart).

What a page script cannot do, and Brave does inside its engine: it does not reach into Web Workers (a
fingerprinter that runs in a worker sees real values), a determined fingerprinter can find out that
functions were replaced, and fonts, CSS media queries and network fingerprints (TLS, HTTP/2) are not
touched. Plugins are left alone on purpose: modern Chromium reports the same five PDF viewers to
everyone, so changing them would make a visitor more recognisable, not less.

Settings (`settings.json`, browser closed):
`"Fp": { "On": true, "Default": 1, "Sites": { "example.com": 0 }, "Gpc": true, "WebRtc": true }`,
levels 0 off, 1 standard, 2 strict.

## Where data is saved

Everything is under `%LOCALAPPDATA%\NEAT\`: `settings.json`, `browser.db`
(history and bookmarks), `session.json` (the open tabs) and the `WebView2Profile` folder.
The password vault is `vault.json`, made the first time the Passwords page is opened.

If `settings.json` cannot be read (for example after a typo in a hand edit),
NEAT starts with the defaults and keeps the damaged file as `settings.json.bad`.

To change the search engine used for plain text typed in the address box, set
`"Use"` in `settings.json` to one of the engine names listed there (for example
`"Bing"`), with the browser closed.

## Keyboard shortcuts

Modelled on Zen's defaults, for the features NEAT already has.

| Keys | Action |
|---|---|
| Ctrl+T | New tab (command bar, result opens in a new tab) |
| Ctrl+L, Alt+D, Ctrl+K, Ctrl+E | Edit this tab's address (command bar) |
| Ctrl+W | Close tab |
| Ctrl+Shift+W | Close window |
| Ctrl+Tab, Ctrl+Shift+Tab | Next / previous tab |
| Ctrl+1 to Ctrl+8 | Go to that tab; Ctrl+9 the last tab |
| Alt+Left, Alt+Right | Back / forward |
| Alt+Home | Home page |
| Ctrl+R, F5 | Reload |
| Ctrl+Shift+R, Ctrl+F5 | Reload ignoring the cache |
| Ctrl+D | Bookmark this page / remove the bookmark |
| Ctrl+Shift+C | Copy link |
| Ctrl+S | Show / hide the sidebar |
| Ctrl+Alt+S | Float / put away the hidden sidebar |

Left to WebView2, which handles them inside the page: Ctrl+F (find), Ctrl+P
(print), F12 and Ctrl+Shift+I (DevTools), and the zoom keys.

Not assigned yet because the feature does not exist: history, downloads,
bookmarks library, settings, private window, restore closed tab, mute tab,
full screen.
