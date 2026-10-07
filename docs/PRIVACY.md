# NotchX privacy policy

_Last updated: 6 October 2026_

NotchX is a desktop app for Windows that shows a notch-style panel at the top of your screen. It's built and
maintained by Navneet Kaur. This policy explains what NotchX does with your information. The short version:
**NotchX has no accounts, no ads, no analytics and no telemetry. Your data stays on your PC.**

## What NotchX stores, and where

Everything NotchX keeps is saved in a folder on your own PC (`%APPDATA%\NotchX`, or the app's private
folder when installed from the Microsoft Store). Nothing in it is uploaded anywhere. It holds:

- **Your settings**, including layout, theme and your list of cities.
- **Clipboard history**, if you leave it on: text and images you copy. Copies from password managers are
  never recorded. NotchX respects the markers Windows uses for sensitive clipboard content, and skips apps
  like KeePass, 1Password and Bitwarden. You can turn history off, clear it, or exclude more apps.
- **Notes, reminders, the file shelf list, launcher pins and snippets** you create.
- **Screen time**: how long each app was in front today, for the Screen time card. You can turn it off in
  Settings › General. It never leaves your PC.
- **A log file** (`notchx.log`) with start-up messages and errors, to help fix bugs. It stays on your PC.
- **A GitHub token**, only if you enter one to see Copilot usage. It's encrypted with Windows' own data
  protection (DPAPI), so only your Windows account can read it.

Uninstalling the Store version removes this folder. For the zip version, you can delete the folder yourself.

## What NotchX reads on your PC

To work, NotchX reads some things locally. None of it is stored beyond what's listed above, or sent anywhere:

- **Keyboard:** NotchX listens for the volume and Caps Lock keys to show on-screen indicators. It doesn't
  record what you type. The optional keystroke display for presentations is off by default, never shows
  keys typed into password fields, and doesn't save anything.
- **Media and audio:** what's playing (title, artist, album art) from Windows' media controls, and your
  audio devices and per-app volume, for the music and sound features.
- **Windows and desktops:** the titles and positions of open windows, and which virtual desktop they're on,
  for the Desktops tab.
- **Camera and microphone status:** NotchX shows a dot while another app uses your camera or microphone.
  The camera image itself is only used for the optional camera mirror and teleprompter, while they're open.
  Nothing is recorded.
- **Bluetooth devices and battery levels**, for the Bluetooth and battery cards.
- **AI coding tool logs:** if you use Claude Code or Codex, NotchX reads their usage logs on your PC to show
  token counts and limits.
- **Other apps' notifications** (Microsoft Store version only, and only after you allow it when Windows
  asks): NotchX reads the notifications in Windows' notification centre to show them in the notch. They're
  kept in memory while shown and never saved or sent anywhere. You can hide message text, mute apps, or turn
  the feature off in Settings, and withdraw access in Windows Settings › Privacy & security › Notifications.
- **Files you give it:** files you drop on the shelf or open in the Documents tab are converted or edited on
  your PC, with Microsoft Word or LibreOffice when they're installed.

## When NotchX connects to the internet

NotchX only goes online for features that need it. Each one sends the minimum it needs, directly to the
service named. NotchX has no server of its own, and nothing goes through one.

| Feature | Service | What's sent | On by default? |
|---|---|---|---|
| Weather and world clocks | Open-Meteo (open-meteo.com) | Your places' coordinates. If you haven't added a place and you allow Windows location, your approximate location | Yes, when you've added a place or allowed Windows location. To stop it, remove your places in Settings › Weather and turn off location for desktop apps in Windows |
| City search (Settings › Weather) | Open-Meteo | The letters you type in the search box | Only while you search |
| Song lyrics | LRCLIB (lrclib.net) | The title, artist, album and length of the song playing | Yes. Turn off in Settings › Now Playing |
| Calendar | The calendar links you add | A normal request for your calendar feed | Only if you add a link |
| Translation | Google Translate, or LibreTranslate if you choose it | The text you ask it to translate | Only when you translate |
| GitHub Copilot usage | GitHub (api.github.com) | Your GitHub token, to read your Copilot quota | Only if you enter a token |
| Web search | Your browser opens the search engine you pick | Your search, sent by your browser | Only when you search |
| Saving a video from a link | yt-dlp, if you installed it | The link you give it | Only when you use it |

These services have their own privacy policies. NotchX doesn't add any identifiers to these requests.

NotchX also has a **developer API** for scripts and coding tools. It's off by default and, when switched
on, it only listens on your own PC (`127.0.0.1`). Other devices on your network can't reach it.

## What NotchX doesn't do

- No account or sign-in.
- No analytics, tracking, advertising or telemetry.
- No selling or sharing of your data with anyone.
- No data collected from children. NotchX isn't aimed at children, and doesn't collect personal data from
  anyone.

## Changes

If this policy changes, the new version will be published here, with a new date at the top. The full
history is visible in the project's GitHub repository.

## Contact

Questions or concerns: open an issue at
[github.com/Navneet035/NotchX/issues](https://github.com/Navneet035/NotchX/issues).
NotchX is open source, so you can also check everything above in the code.
