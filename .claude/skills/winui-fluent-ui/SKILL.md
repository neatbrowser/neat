---
name: winui-fluent-ui
description: Conventions for icons and Fluent-style UI in the neat browser repo (WinUI 3, src/neat). Use this skill whenever you add, change, or pick an icon or glyph, write a FontIcon or SymbolIcon, add or restyle a toolbar, sidebar, tab, settings, or title-bar button, mention Segoe Fluent Icons or Segoe MDL2 Assets, or make a decision that depends on which Windows version the app supports. Use it even for a one-glyph swap, because a glyph that is missing from the font on the user's machine renders as an empty box and nothing in the build will warn you. Pair it with the xaml-codebehind-sync skill whenever the XAML you touch has an x:Name.
---

# WinUI 3 and Fluent icons in neat

## Platform baseline

neat is a WinUI 3 browser: Windows App SDK 1.8, .NET 8, x64 only, unpackaged (`WindowsPackageType` is `None`), self-contained, with a Fixed Version WebView2 runtime bundled next to `neat.exe`. Because it is unpackaged, there is no package identity, so be wary of Windows APIs that require one.

**Supported Windows:** Windows 11 and Windows 10. `neat.csproj` declares `TargetPlatformMinVersion` as `10.0.17763` (Windows 10 1809). The exact oldest Windows 10 build the project wants to support is a decision for the user, so do not invent a minimum version or edit that property unprompted. If a task depends on it, ask.

**The icon font is the real constraint.** Microsoft's Segoe Fluent Icons page says Windows 10 does not ship that font by default (it is a separate download), and the app's symbol font resource falls back to Segoe MDL2 Assets there. So treat Windows 10 as an MDL2 platform. Do not assume any particular Windows 10 build has Segoe Fluent Icons. A glyph that exists only in Segoe Fluent Icons shows up as an empty box on Windows 10.

## Icon rules

**1. Use `FontIcon` with a `Glyph`, and leave `FontFamily` unset.** Without a `FontFamily`, the icon uses the theme's symbol font (`SymbolThemeFontFamily`), which is Segoe Fluent Icons where available and Segoe MDL2 Assets where not. That automatic fallback is exactly why you must not hard-code `FontFamily="Segoe Fluent Icons"`: on a machine without the font there would be nothing to fall back to. Do not use emoji, PNG, or SVG for UI chrome. (Tab favicons are real images and are a different case.)

**2. A glyph must exist in both fonts.** The code point has to be present in the Segoe Fluent Icons table (Windows 11) and in the Segoe MDL2 Assets table (Windows 10 fallback), and it should mean the same thing in both. Never trust a code point from memory; look it up. The two tables are Markdown files in Microsoft's public docs repo, so a lookup is one command (an empty line means the glyph is absent from that font):

```bash
glyph=E771   # the code point to check
base=https://raw.githubusercontent.com/MicrosoftDocs/windows-dev-docs/docs/hub/apps/design/iconography
for f in segoe-fluent-icons-font segoe-ui-symbol-font; do
  printf '%s: ' "$f"
  curl -s "$base/$f.md" | grep -iE "(^|[^0-9A-Fa-f])$glyph([^0-9A-Fa-f]|$)" | head -1 | sed -E 's/:::image[^:]*:::/ /'
  echo
done
```

`segoe-fluent-icons-font` is the Windows 11 table and `segoe-ui-symbol-font` is the MDL2 table. If the command cannot reach the network, read the same two pages on Microsoft Learn ("Segoe Fluent Icons font" and "Segoe MDL2 Assets"). The `Symbol` enum page lists names and codes too, but it only covers a subset.

**3. Prefer glyphs from the `Symbol` enum.** They are documented under the same fallback font, so they are the safest choice. Outside the enum, you still need both table lookups from rule 2. If you cannot run the lookup, say that the glyph was not checked on Windows 10.

**4. Reuse the glyph for the same role.** The icons already in the app are listed below. If a new feature needs an icon for one of these roles, use the same glyph so the UI stays consistent. Every glyph here has been checked against both tables.

| Glyph | Official name | Role in the app | Where |
|---|---|---|---|
| `E90C` | DockLeft | Toggle sidebar | `win.xaml`, sidebar toggle button |
| `E72B` | Back | Back | `win.xaml`, `back` |
| `E72A` | Forward | Forward | `win.xaml`, `fwd` |
| `E72C` | Refresh | Reload | `win.xaml`, `reload` |
| `E71B` | Link | Copy link (default state) | `copyic`, restored in `win.bar.cs` |
| `E73E` | CheckMark | Copy link (confirmation state) | set on `copyic` in `win.bar.cs` |
| `E734` | FavoriteStar | Bookmark, not bookmarked | `staric` |
| `E735` | FavoriteStarFill | Bookmark, bookmarked | set on `staric` in `win.xaml.cs` |
| `E710` | Add | New tab | `win.xaml` |
| `E774` | Globe | Tab with no favicon | tab list template in `win.xaml` |
| `E711` | Cancel | Close tab | `win.xaml`, `shut_Click` |
| `E790` | Color | Window colour picker | `win.xaml`, opens the flyout handled in `win.paint.cs` |

**5. Sizes and button shape.** Toolbar and sidebar icons are `FontSize="14"`, icons inside the address bar are `12`, and the tab close icon is `10`. These are deliberately compact. Microsoft recommends 16, 20, 24, 32, 40, 48, or 64 for the sharpest rendering, so when a new surface calls for larger icons (a settings page, say), pick one of those sizes rather than an in-between value. Icon buttons are `Height="28"` with `Padding="8,0"`, `Background="Transparent"`, and `BorderThickness="0"`. Icon buttons that have a keyboard shortcut carry a tooltip that names it, for example `Toggle sidebar (Ctrl+S)`. Follow that pattern for new buttons.

**6. Let icons inherit their colour.** Do not set a `Foreground` on a `FontIcon` unless the design needs it. The window colour is user-adjustable through the picker in `win.paint.cs`, so hard-coded colours can look wrong against the user's choice. Read `win.paint.cs` before changing any background or colour.

## Icons that change at runtime

Two icons are swapped from C#: `copyic` (in `win.bar.cs`) and `staric` (in `win.xaml.cs`). The glyph appears twice in each case, once in XAML and once as a `"\uXXXX"` string in C#.

- If you change the default glyph in XAML, change the matching restore value in C# too. `win.bar.cs` resets `copyic` to its original glyph after a short delay, so a XAML-only change would flip back to the old icon after the first use.
- In XAML a glyph is written `&#xE71B;`, in C# it is `"\uE71B"`. These are the same code point, so check that both sides agree, and that the new glyph passes rule 2.
- Do not remove or rename `copyic` or `staric` without updating the C#. That is exactly the kind of break the `xaml-codebehind-sync` skill exists to catch, so follow its steps and run its check whenever you edit XAML that has an `x:Name`.

## What you can and cannot verify

This repo only builds and runs on Windows. If you are working somewhere that cannot run the app, you cannot see how an icon renders. Say so plainly: report that you checked the code point against both official tables and kept the XAML and C# in sync, but did not look at the result on screen.

## Checklist

1. Glyph is a `FontIcon` with no `FontFamily`.
2. Code point found in both the Segoe Fluent Icons table and the Segoe MDL2 Assets table, with the same meaning, not recalled from memory.
3. Same glyph reused for a role that already exists.
4. Size and button shape match the existing pattern (or a Microsoft-recommended size for a new, larger surface).
5. If the icon has an `x:Name` or changes at runtime, XAML and C# were updated together and `xaml-codebehind-sync` was followed.
6. The report states what was and was not verified, including anything not checked on Windows 10.
