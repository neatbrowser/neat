# NEAT Browser

A browser built with WinUI 3 and WebView2, with a vertical tab sidebar.

**Status:** phase W0, project skeleton. One window with a custom title bar and
a single WebView2 that loads a test page.

## Requirements

- Windows 10 1809 or newer, x64
- .NET 8 SDK

The Windows App SDK runtime, the .NET runtime and the WebView2 runtime are all
bundled, so nothing else needs to be installed to run a published build.

## Build

```powershell
dotnet restore src/NeatBrowser/NeatBrowser.csproj -p:Platform=x64
dotnet build   src/NeatBrowser/NeatBrowser.csproj -c Release --no-restore -p:Platform=x64
```

Always pass `-p:Platform=x64`.

## Get a runnable build from GitHub

Every push to `main` runs the **Build** workflow. Open the run on the
**Actions** tab and download the `NeatBrowser-win-x64` artifact. Unzip it and
run `NeatBrowser.exe`. The first restore is slow because the bundled
WebView2 runtime is about 250 MB.

## Layout

```
src/NeatBrowser/
  NeatBrowser.csproj     project (unpackaged, self-contained, x64)
  App.xaml(.cs)          application entry
  MainWindow.xaml(.cs)   main window
  Browser/WebViewRuntime.cs   points WebView2 at the bundled runtime
```
