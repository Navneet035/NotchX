# Microsoft Store listing: NotchX - Dynamic Notch

Copy each section into the matching field in Partner Center (submission › **Store listings** ›
**English (United States)**, plus **Properties** and **Age ratings**). Store ID: `9N2LWTDSX986`.

---

## Store listing

### Product name
NotchX - Dynamic Notch

### Description

```
NotchX puts a small, Dynamic Island-style notch at the top of your Windows screen. Hover over it and it opens into a panel with everything you reach for all day: your music, files, clipboard, notes, timers and more. Move the mouse away and it tucks itself back in.

It works on any Windows 10 or 11 PC, notch or no notch.

MUSIC
See what's playing in Spotify, YouTube Music, Apple Music, your browser or any app that shows in Windows' media controls. Album art, play, pause, skip and scrub, plus synced lyrics. Click the album art to jump straight to the player.

A HOME PAGE YOU DESIGN
Pick from 22 cards, including clock, weather, world clocks, volume and output, brightness, Bluetooth devices, notes, reminders, calendar and screen time, then move and resize them on a simple grid.

YOUR DESKTOPS AT A GLANCE
See every virtual desktop and the windows open on it. Click a window to jump to it, or drag windows onto another desktop to move them.

GET THINGS DONE
- Clipboard history with search, including text inside screenshots
- A file shelf: drop files on the notch, drag them out later
- Convert Word to PDF and PDF to Word, and edit PDFs
- Screen capture straight to the clipboard
- Pomodoro timer, reminders in plain English and quick notes
- Command palette, app launcher and text snippets

MAKE IT YOURS
Seven themes, frosted glass, your own colours or picture, adjustable size and shape, named tabs on one or two rows, and a clock that shows time the way you like.

PRIVATE BY DESIGN
No account, no ads, no tracking. Your clipboard, notes and settings stay on your PC. NotchX only goes online for features that need it, like weather and lyrics, and never through a server of its own.

NotchX is free and open source.
```

### What's new in this version

```
- Themes: seven one-click looks, and a highlight colour for the selected tab
- Desktops tab: see each virtual desktop's windows and drag them between desktops
- World clocks: search for cities as you type and see their time and weather
- Named tabs, with extra tabs on a second row or in a More menu
- Clock options: 12 or 24 hour, seconds and date style
- Custom icons for Launcher and Shelf items
- Smoother, more polished look throughout
```

### Product features (one per line; Partner Center allows up to 20)

```
Music controls with album art and synced lyrics
A Home page of 22 cards you can arrange and resize
See and move windows between virtual desktops
Clipboard history with search inside screenshots
File shelf: drop files on the notch, drag them out later
Word to PDF, PDF to Word and a PDF editor
World clocks with live weather
Screen capture straight to the clipboard
Volume, brightness and Caps Lock on-screen indicators
Pomodoro timer, reminders and quick notes
Seven themes, frosted glass and full customisation
No account, no ads, no tracking
```

### Short description (optional)

```
A Dynamic Island-style notch for Windows: music, files, clipboard, desktops and more, one hover away.
```

### Search terms (up to 7)

```
notch
dynamic island
productivity
music controls
clipboard
virtual desktops
widgets
```

### Store logos

From `docs/store/logos/`:

- **1:1 logo, 300×300:** `store-logo-300.png` (the app icon)
- **1:1 box art, if asked for a larger square:** `box-art-1080.png` or `box-art-2160.png` (the icon on the dark notch background)

### Screenshots (Desktop)

Upload in this order, from `docs/store/screenshots/` (1920×1080):

1. `01-home.png`
2. `02-music.png`
3. `03-desktops.png`
4. `05-themes.png`
5. `04-documents.png`

`02-music.png` was shot while playing an original demo track ("Midnight Drive" by NotchX, with our own cover
art), so no third-party album art or lyrics appear in the listing. Its source render is
`docs/store/source/now-playing-demo.png`.

Regenerate all of them with `packaging\store-screenshots.ps1`.

### Copyright and trademark info

```
© 2026 Navneet Kaur
```

### Additional license terms

```
NotchX is open source under the MIT licence: https://github.com/Navneet035/NotchX/blob/main/LICENSE
```

### Developed by

```
Navneet Kaur
```

---

## Properties

| Field | Value |
|---|---|
| Category | Productivity |
| Privacy policy URL | https://github.com/Navneet035/NotchX/blob/main/docs/PRIVACY.md |
| Website | https://github.com/Navneet035/NotchX |
| Support contact info | https://github.com/Navneet035/NotchX/issues |
| System requirements | Windows 10 version 2004 or later, or Windows 11 |
| Product declarations | Leave "customers can install to alternate drives" and "removable storage" on. Everything else off |

## Pricing and availability

- **Pricing:** Free
- **Markets:** all (or the ones you choose)
- **Visibility:** Public

## Age ratings (IARC questionnaire)

Choose the **Productivity** (or Utility) app category, then answer:

| Question (summarised) | Answer |
|---|---|
| Violence, fear, sexual content, crude humour, drugs, gambling | No |
| Can users communicate or share content with each other? | No |
| Does it share the user's location with other users? | No |
| Digital purchases | No |
| Unrestricted internet access, like a web browser? | No. Web searches open in the user's own browser |

This should give a 3+ / Everyone rating.

## Packages

Upload `msix-out\NotchX_<version>.msixbundle`, built with `packaging\build-msix.ps1`.

## Restricted capabilities (`runFullTrust`)

Paste the justification from [packaging/README.md](../../packaging/README.md#why-notchx-needs-runfulltrust).
