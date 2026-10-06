# NEAT Browser

A browser built with WinUI 3 and WebView2, with a vertical tab sidebar.

**Status:** phase W5, keyboard shortcuts. The shortcuts below work both when
focus is in the window's own controls and when it is inside a web page.

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
  neat.csproj      project (unpackaged, self-contained, x64)
  app.xaml(.cs)    application entry, look constants, shared services
  win.xaml(.cs)    main window: tabs, navigation, bookmarks, command bar
  win.side.cs      sidebar states (docked, hidden, peeking) and tab row look
  win.bar.cs       title bar: click-through regions, address chip, copy link
  win.keys.cs      shortcut table and the script that reports keys pressed inside pages
  web/env.cs       app folders; points WebView2 at the bundled runtime
  web/tab.cs       one tab: its web view, title, address and favicon
  data/prefs.cs    what settings.json holds (home page, search engines, window size)
  data/store.cs    loads and saves settings.json
  data/search.cs   turns typed text into an address or a search
  data/db.cs       connections to browser.db
  data/history.cs  history table
  data/marks.cs    bookmarks table (flat list, no folders yet)
```

## Where data is saved

Everything is under `%LOCALAPPDATA%\NEAT\`: `settings.json`, `browser.db`
(history and bookmarks) and the `WebView2Profile` folder.

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
