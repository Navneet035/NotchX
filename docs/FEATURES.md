# Feature status

How each feature from the macOS app maps to Windows, and where it lives in the code.

✅ implemented · 🟡 implemented with a Windows-specific difference · 🗺️ roadmap · ⛔ not possible on Windows (and what we do instead)

| Feature | Status | Notes | Code |
|---|---|---|---|
| **Now Playing** | ✅ | Spotify, Apple Music for Windows, Media Player, and any Chromium/Firefox browser via Windows' media session (GSMTC). Album art, scrubbing, shuffle, repeat, per-app volume, playback speed for browser video. | `Services/MediaService.cs`, `Modules/NowPlayingModule.cs` |
| ↳ one-tap Favorite (Apple Music) | ⛔ | Windows' media API doesn't expose "love". | — |
| **AI Usage Tracker** | 🟡 | Claude Code (local logs, 5-hour window, tokens, cost), Codex (local logs incl. server-reported rate-limit %), Copilot (GitHub token, DPAPI-encrypted). Cursor: 🗺️. Section order configurable, alerts before limits. | `Services/AiUsageService.cs` |
| **Sound Mixer** | 🟡 | Per-app volume + mute, output picker. The 3-band per-app EQ needs a system audio driver (APO) on Windows → 🗺️ | `Modules/SoundModule.cs`, `Services/AudioService.cs` |
| **Search & Translate** | ✅ | 9 engines + custom, live translation, calculator with history, save a video from a link (yt-dlp). | `Modules/SearchModule.cs` |
| **Command Palette** | ✅ | `Ctrl+Shift+Space` anywhere. Tabs, every module action, snippets, inline sums. `Ctrl+1–9` switches tabs in the notch. | `Shell/CommandPalette.xaml.cs` |
| **Menu Bar Detach** | 🟡 | Any tab pops out into a floating glass panel (always-on-top or pinned to desktop). Tray icon: left-click opens, middle-click toggles Keep Awake. | `Shell/DetachedWindow.cs`, `App.xaml.cs` |
| **Clipboard History** | ✅ | Pins survive the cap and Clear. Smart actions for links, colours, emails. Respects password-manager "don't record" flags. | `Services/ClipboardService.cs` |
| **Zoom Teleprompter (Cuely)** | ✅ | Script scrolls in the notch, under the webcam. Speed, size, mirror. | `Modules/CuelyModule.cs` |
| **System HUDs** | 🟡 | Volume, brightness, Caps Lock islands. "Replace the Windows volume flyout" intercepts the volume keys so the stock overlay never shows. Brightness keys are handled by firmware on most laptops, so the stock brightness OSD may still appear. | `Modules/BackgroundModules.cs` |
| **AirPods Manager** | 🟡 | Detects nearby AirPods via Apple's BLE advertisements; per-bud + case battery. Windows doesn't let apps initiate an audio connection, so "Connect" opens Bluetooth settings. | `Services/BluetoothService.cs` |
| **Pomodoro Timer** | ✅ | Session chaining, stopwatch, streaks, 7-day chart, mascots. | `Modules/TimerModule.cs` |
| **Calendar** | 🟡 | Google / Outlook / Exchange / iCloud via secret iCal links (no OAuth app needed). Join buttons for Zoom/Meet/Teams. | `Services/CalendarService.cs` |
| **Camera Mirror** | ✅ | Local preview, never recorded. | `Modules/CuelyModule.cs` |
| **Native & light** | 🟡 | C#/WPF on .NET 8 (the Windows counterpart to SwiftUI). Timers only run while something is visible. | — |
| **Liquid Glass UI, resizable panel** | ✅ | Real frosted glass: a click-through window under the pill, clipped to its shape, blurs what's behind (falls back to a solid fill when Windows transparency effects are off). Tint, sheen/rim, solid / gradient / picture backgrounds, text size, open and collapsed size, corner radius — all live-preview. | `Shell/GlassBackdrop.cs`, `Shell/NotchWindow.xaml.cs`, `Shell/SettingsWindow.cs` |
| **Documents** | 🟡 | Word / Excel / PowerPoint → PDF and PDF → Word through Microsoft Word (COM) when installed, else LibreOffice headless. Built-in PDF editor (PDFsharp): reorder, rotate, delete, merge, extract, split, text / watermark / page numbers, password. Editing the existing text inside a PDF isn't supported — convert to Word for that. | `Modules/DocumentsModule.cs`, `Services/DocumentService.cs`, `Services/PdfEditSession.cs` |
| **Screen capture** | ✅ | Built-in region capture on every monitor; copies to the clipboard, stages on the shelf, optional folder and shortcut. Snipping Tool mode available. | `Services/ScreenCaptureService.cs` |
| **Customisable layout** | ✅ | ✎ in the notch header (or right-click › Edit layout): drag tabs and Home widgets to reorder, eye to hide. Hidden tabs keep their feature running. Also in Settings › Features and › Appearance. | `Core/LayoutEditor.cs`, `Views/HomeView.xaml.cs` |
| **Default page** | ✅ | Open on a chosen tab, the last one used, or "smart" (music while playing, timer while running). Settings › General or right-click the notch. | `Shell/NotchWindow.xaml.cs` |
| **Home quick controls** | 🟡 | Volume + mute, brightness (built-in panel via WMI, external monitors via DDC/CI), sound output picker, lock screen, paired Bluetooth devices with battery. Windows apps can't connect/disconnect audio devices, so a device opens the quick-settings Bluetooth list. | `Views/HomeView.xaml`, `Services/BluetoothService.cs` |
| **Quick Notes** | ✅ | Newest open note stays on the pill until ticked off. | `Modules/NotesModule.cs` |
| **File Shelf** | ✅ | Drag files onto the notch; drag out anywhere; survives restarts. | `Services/ShelfService.cs` |
| **Keep Awake** | ✅ | Indefinitely / for a duration / until a time; auto-off on low battery. | `Services/SystemServices.cs` |
| **Live Weather** | ✅ | Open-Meteo (no key). City or Windows location. | `Services/SystemServices.cs` |
| **Focus Mode** | 🟡 | Detects Windows Do Not Disturb / Focus assist; low-priority islands stay quiet. Uses an undocumented shell state, so it may need updating on future Windows builds. | `Services/WatcherServices.cs` |
| **Battery HUD** | ✅ | Charging/unplugged islands, low-battery alert. | `Services/SystemServices.cs` |
| **Synced Lyrics** | ✅ | LRCLIB, in the tab or on the pill. | `Services/LyricsService.cs` |
| **Reminders** | ✅ | "Call Sam tomorrow 9am", snooze, done. | `Modules/CalendarModule.cs` |
| **Image Converter** | ✅ | PNG/JPEG/TIFF/BMP/GIF, PDF → images, HEIC (with the free HEIF extension). Offline. | `Services/ShelfService.cs` |
| **Audio Switcher** | ✅ | One tap from the Sound tab or palette. | `Services/AudioService.cs` |
| **Device Battery** | 🟡 | Every Bluetooth device that reports battery to Windows. | `Services/BluetoothService.cs` |
| **Download Alerts** | ✅ | Any browser, no extension. "Show in folder". | `Services/WatcherServices.cs` |
| **Zip & Unzip** | ✅ | On the shelf. | `Services/ShelfService.cs` |
| **Screenshot Shelf** | ✅ | Win+PrtScn / Snipping Tool saves are staged on the notch. | `Services/WatcherServices.cs` |
| **Drive Eject** | ✅ | USB inserted → island with Eject → "Safe to disconnect". | `Services/WatcherServices.cs` |
| **App Launcher** | ✅ | Start-menu picker, drag to reorder, drop shortcuts in. | `Modules/LauncherModule.cs` |
| **Window Snapping** | ✅ | Drag a window to the notch, drop on a zone. Off by default (overlaps Windows 11 snap layouts). | `Modules/WindowSnapModule.cs` |
| **Text Snippets** | ✅ | Paste from the palette; clipboard is restored afterwards. `{date}`, `{time}`, `{clipboard}`. | `Modules/SnippetsModule.cs` |
| **OCR & Search** | ✅ | On-device Windows OCR; clipboard search matches text inside images. | `Services/ClipboardService.cs` |
| **Screen Capture** | ✅ | Windows region selector → clipboard (OCR'd) + shelf. | `Modules/ToolsModule.cs` |
| **Color Eyedropper** | ✅ | Magnifier loupe, click to copy hex. | `Modules/ToolsModule.cs` |
| **Mic Mute** | ✅ | Mutes every capture device at once — Zoom, Meet, Teams all go silent. | `Services/AudioService.cs` |
| **Keystroke HUD** | ✅ | Password fields are never shown. | `Modules/BackgroundModules.cs` |
| **Keyboard Cleaning Lock** | ✅ | 60-second lock, `Ctrl+Esc` unlocks. (`Ctrl+Alt+Del` can't be blocked — by design.) | `Modules/ToolsModule.cs` |
| **Menu Bar Icon Hiding** | ⛔ | Windows already has the tray overflow (`^`). Drag icons there. | — |
| **System Stats** | ✅ | CPU, memory, network, disk with sparklines. | `Modules/StatsModule.cs` |
| **Notch Terminal** | 🗺️ / 🟡 | Today: Windows Terminal's drop-down "quake" window (or a normal window). The Tools tile shows / hides it, ✕ closes it, and a pill chip appears while it's open. Roadmap: an in-notch ConPTY terminal. | `Services/TerminalService.cs`, `Modules/ToolsModule.cs` |
| **Desktop Widgets** | 🟡 | Pop out any tab and choose "Pin to desktop". Native Windows Widgets board needs an MSIX package → 🗺️ | `Shell/DetachedWindow.cs` |
| **Agent Approvals** | ✅ | Claude Code `PermissionRequest` http hook → Allow/Deny island. Answering in the terminal clears it. | `Services/DeveloperApiService.cs` |
| **Privacy Indicator** | ✅ | Green (camera) / orange (mic) dot, from Windows' capability usage store. | `Services/WatcherServices.cs` |
| **Caps Lock HUD** | ✅ | | `Modules/BackgroundModules.cs` |
| **Full Charge Island** | ✅ | 80–100%, plus low-battery sound. | `Services/SystemServices.cs` |
| **Alt HUD Styles** | ✅ | Island, edge bar, or circular gauge, with size. | `Modules/BackgroundModules.cs` |
| **Live Audio Spectrum** | ✅ | Real FFT of system audio via WASAPI loopback; falls back to a synthetic wobble. | `Services/SpectrumService.cs` |
| **Custom Idle Animation (Lottie)** | 🗺️ | Needs a Lottie renderer for WPF (e.g. LottieSharp). | — |
| **Multiple Live Activities** | ✅ | Music, timer, builds, downloads… side by side, ranked by priority. | `Core/ActivityHub.cs` |
| **Lock Screen Widgets** | ⛔ | Windows doesn't allow third-party content on the lock screen. | — |
| **Message Island / Notification Mirror** | 🗺️ | Possible with `UserNotificationListener`, which requires an MSIX-packaged build. | — |
| **Agent Activity** | ✅ | Claude Code turns + per-turn cost; Codex turn-complete. | `Services/DeveloperApiService.cs`, `hooks/` |
| **Shell Activity** | ✅ | PowerShell hook; long commands become live islands; `Disable-NotchifyShell` removes it. | `hooks/notchify-shell.ps1` |
| **Developer API** | ✅ | `127.0.0.1` only, per-install token, off by default. | `docs/DEVELOPER_API.md` |
| **External Display Control** | ✅ | DDC/CI brightness for external monitors. Off by default. | `Services/BrightnessService.cs` |
| **Keyboard Backlight** | ⛔ | No standard Windows API; every laptop vendor does it differently. | — |
| **Clipboard Smart Actions** | ✅ | Open / compose / colour swatch, auto-paste into the front app. | `Services/ClipboardService.cs` |
| **Peek on Hover** | ✅ | Mini transport controls without opening the panel. | `Shell/NotchWindow.xaml.cs` |
| **Drift-free Stopwatch** | ✅ | Wall-clock based; costs nothing while hidden. | `Modules/TimerModule.cs` |
| **Gestures & Hover Open** | ✅ | Hover to open, auto-collapse, scroll/two-finger swipe to switch tabs, scroll on the pill for volume. Each switchable. | `Shell/NotchWindow.xaml.cs` |
| **Hidden from Screen Capture** | ✅ | One switch (uses `WDA_EXCLUDEFROMCAPTURE`). | `Shell/NotchWindow.xaml.cs` |
| **Pill on any display** | ✅ | Notch or floating pill, size, offset, target display, hide in fullscreen. | `Core/Settings.cs` |
| **Timer Mascots** | ✅ | 🐢 🐇 🐱 🦊 🐧 🚀 🍅 | `Modules/TimerModule.cs` |
