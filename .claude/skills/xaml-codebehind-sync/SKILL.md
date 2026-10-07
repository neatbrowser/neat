---
name: xaml-codebehind-sync
description: Keep XAML and C# code-behind in sync in the neat browser repo (WinUI 3, src/neat). Use this skill whenever you edit, rewrite, simplify, restyle, or delete anything in a .xaml file (win.xaml, app.xaml), change an icon, button, flyout, or layout, rename or remove an x:Name or event handler, or touch the partial-class files win.*.cs. Also use it before committing or pushing any UI change, and when a GitHub Actions "Build" run fails with "The name 'x' does not exist in the current context". Make sure to use it even for "small" UI tweaks, because small XAML edits are exactly how this repo broke before.
---

# Keeping XAML and C# in sync

## Why this skill exists

Build #3 of the `Build` workflow failed with 10 compiler errors after a commit titled "Update win.xaml". The intent was tiny: swap the sidebar-toggle icon for a `FontIcon`. But the edit was made by replacing the whole file, and it silently dropped about 90 lines of unrelated UI: the colour-picker flyout and its controls (`sw`, `fly`, `square`, `darksw`, `spread`, `spec`, `shade`, `dot`). `win.paint.cs` still used all of them, so the compiler could no longer resolve those names.

In WinUI 3, every `x:Name` in XAML becomes a field the generated code exposes to C#. Delete the element and the field disappears, while the C# that references it stays behind and fails to compile. XAML and C# are two halves of one class, and this repo splits that class across several files, so the other half is easy to forget.

## How this repo is laid out

All UI code lives in `src/neat/`. The main window is one partial class spread over several files:

- `win.xaml` is the markup. Every `x:Name` and every `Click="..."`-style handler here has a counterpart in C#.
- `win.xaml.cs`, `win.paint.cs`, `win.bar.cs`, `win.side.cs`, `win.keys.cs` are the code-behind for that window. `win.paint.cs`, for instance, owns the colour picker. Always search all of them, never only `win.xaml.cs`.
- `app.xaml` and `app.xaml.cs` are the application-level pair.
- `data/`, `look/` and `web/` hold logic that is not tied to specific controls. The check script below only scans `*.cs` and `*.xaml` directly in `src/neat/`, so code in these folders must never use the window's `x:Name` controls.

## Before editing XAML

1. Read the part of the file you are about to change, and name the elements it contains. Look for `x:Name="..."` inside the region, including inside nested flyouts, templates, and resources, since those are easy to overlook.
2. For each name in that region, grep the C# to see who uses it: `grep -nw "<name>" src/neat/*.cs`. This tells you what breaks if the element is removed or renamed.
3. Decide the smallest edit that achieves the goal. If the goal is "change this icon", the edit is a few lines around that one button.

## While editing

- Prefer targeted edits (a string replace on the exact lines) over regenerating or re-pasting the whole file. A full rewrite can drop sections you never meant to touch, and the loss is invisible in the diff unless you look for it.
- If you remove or rename an element that has an `x:Name`, update every C# reference in the same change. If you add a `Click="foo_Click"` (or any event handler attribute), add the matching method with the same name in the right code-behind file.
- If a feature is being removed on purpose, remove both sides together: the XAML block and the C# that drives it. Leaving either half behind is how builds break.
- Keep unrelated changes out of the commit. A UI polish change and a feature removal should be separate commits, so a failure points at one cause.

## Verify before committing

Run this from anywhere inside the repo. It compares the `x:Name`s in a base ref (default `HEAD`, meaning your uncommitted changes) against the working tree, reports any removed name still referenced from C#, and reports any event handler wired in XAML that has no method in C#.

```bash
#!/usr/bin/env bash
# Usage: ./check.sh [BASE_REF]   (default: HEAD)
BASE="${1:-HEAD}"
cd "$(git rev-parse --show-toplevel)/src/neat" || exit 2

names() { grep -oE 'x:Name="[^"]+"' | sed -E 's/x:Name="(.*)"/\1/' | sort -u; }

# 1) x:Name values that existed at BASE but are gone now, yet still used in C#
removed=$(comm -23 \
  <(for f in $(git ls-tree --name-only -r "$BASE" . | grep '\.xaml$'); do git show "$BASE:./$(basename "$f")" 2>/dev/null; done | names) \
  <(cat *.xaml | names))
status=0
for n in $removed; do
  hits=$(grep -nw -- "$n" *.cs)
  if [ -n "$hits" ]; then echo "REMOVED x:Name '$n' is still used in C#:"; echo "$hits" | sed 's/^/    /'; status=1; fi
done

# 2) Event handlers wired in XAML that have no matching method in C#
handlers=$(grep -ohE '\b(Click|Tapped|DoubleTapped|RightTapped|Loaded|Unloaded|SizeChanged|TextChanged|SelectionChanged|KeyDown|KeyUp|PointerPressed|PointerMoved|PointerReleased|Toggled|ValueChanged|Checked|Unchecked|ItemClick|GotFocus|LostFocus|Closed|Opened|QuerySubmitted|SuggestionChosen|Drop|DragOver)="[A-Za-z_][A-Za-z0-9_]*"' *.xaml | sed -E 's/.*="(.*)"/\1/' | sort -u)
for h in $handlers; do
  grep -qE "\b$h\s*\(" *.cs || { echo "MISSING handler '$h' (wired in XAML, no method in C#)"; status=1; }
done

[ $status -eq 0 ] && echo "OK: XAML and C# are in sync."
exit $status
```

How to read the result:

- `OK` means no dangling names or handlers were found. It does not prove the project compiles, only that this specific class of error is absent.
- `REMOVED x:Name ... still used in C#` means the build will fail. Either restore the element or remove the C# that uses it. Matches inside comments are false alarms, so check each hit rather than assuming.
- `MISSING handler` means XAML points at a method that does not exist. Add the method or fix the attribute.

To check a commit you already made, pass the commit before it as the base, for example `./check.sh HEAD~1`.

Also skim `git diff --stat` before committing. If a "small" change shows dozens of deleted lines in a XAML file, stop and find out why before going further.

## Building locally

The real project targets `net8.0` on Windows with the Windows App SDK, so a full build usually only works on a Windows machine with the .NET 8 SDK:

```
dotnet build src/neat/neat.csproj --configuration Release -p:Platform=x64
```

If you are in an environment that cannot build it (Linux, no Windows SDK), say so plainly in your report. State that you ran the sync check above and did not compile, rather than implying the build was verified.

## After pushing

The `Build` workflow runs on every push to `main` (it builds on `windows-latest` and uploads the `neat-win-x64` artifact). A push is not finished until that run is green.

- Open `https://github.com/neatbrowser/neat/actions` and look at the newest `Build` run. With the `gh` CLI available, `gh run list --workflow build.yml --limit 3` and `gh run view <id>` give the same information.
- Full step logs need a GitHub login, but the run page shows compiler errors as annotations without one. Read those: the `file#Lnn` link and message usually name the broken identifier directly.
- Unauthenticated calls to the GitHub REST API are rate-limited quickly, so fall back to the web pages when the API refuses.
- The Node.js 20 deprecation warning on `actions/checkout@v4`, `actions/setup-dotnet@v4`, and `actions/upload-artifact@v4` appears on every run. It is not a failure and not related to a broken build.
- If the run fails, fix forward in a new commit, or restore the last good state, and watch the next run. Do not leave `main` red.

## Quick checklist

1. Listed the `x:Name`s and handlers in the region being edited.
2. Grepped all `src/neat/*.cs` for each one.
3. Made a targeted edit, not a full-file rewrite.
4. Updated XAML and C# together.
5. Ran the sync check and read `git diff --stat`.
6. Committed one concern per commit.
7. Confirmed the `Build` run on Actions is green.
