# NEAT Browser

A browser built with WinUI 3 and WebView2, with a vertical tab sidebar.

**Status:** phase W1, layout and risk tests. A full-height rounded sidebar, a
rounded web frame, a command bar overlay drawn on top of the page, and a thin
drag strip at the top right.

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
  app.xaml(.cs)    application entry and look constants (radius, padding, background)
  win.xaml(.cs)    main window
  web/env.cs       points WebView2 at the bundled runtime
  web/url.cs       turns typed text into a URL or a search
```
