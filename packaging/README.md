# Microsoft Store package (MSIX)

The Store build of NotchX is the same app as the zip, packaged as MSIX. The Store signs it when you upload,
so no code-signing certificate is needed.

## 1. Put your Partner Center identity in `identity.json`

In Partner Center, open your app (after reserving the name **NotchX**) › **Product management** ›
**Product identity**, and copy these three values into [`identity.json`](identity.json):

| Partner Center shows | `identity.json` field |
|---|---|
| Package/Identity/Name | `IdentityName` |
| Package/Identity/Publisher | `Publisher` (starts with `CN=`) |
| Package/Properties/PublisherDisplayName | `PublisherDisplayName` |

## 2. Build

```powershell
.\packaging\build-msix.ps1
```

This writes `msix-out\NotchX_<version>.msixbundle` (x64 + ARM64), plus one `.msix` per architecture.
The version comes from `src/Notchify/Notchify.csproj` (`0.3.1` becomes `0.3.1.0`; the Store needs the last
number to be 0). Every push to GitHub also builds it: see the **NotchX-Store** artifact on the Actions run.

The script needs `makeappx` and `makepri` from the Windows SDK. If the SDK isn't installed it fetches
Microsoft's `Microsoft.Windows.SDK.BuildTools` NuGet package instead (no admin rights needed).

## 3. Upload

Partner Center › your app › **Start your submission** › **Packages** › drop in the `.msixbundle`.

## What's different in the Store build

- **Start with Windows** uses the package's StartupTask (`NotchXStartup`) instead of the registry Run key.
  If someone turns NotchX off in Windows Settings › Apps › Startup, Windows doesn't let the app turn it back on,
  so NotchX switches its own setting off and says where to change it.
- **Settings and data** stay in `%APPDATA%\NotchX`. Windows keeps a packaged app's copy separate, and removes
  it when the app is uninstalled.

## Certification notes

### Why NotchX needs `runFullTrust`

Paste this into the submission's **Restricted capabilities** justification:

> NotchX is a desktop utility that shows a notch-style panel at the top of the screen. It needs full trust to:
> listen for the volume and Caps Lock keys to show on-screen indicators (a low-level keyboard hook; keystrokes
> are never stored or sent anywhere, and password fields are skipped); read other windows' titles and
> virtual-desktop placement for its window switcher; read media sessions and audio devices for music controls
> and the sound mixer; show a tray icon; and convert documents locally with Word or LibreOffice. Everything runs
> on the user's PC. Network requests are made only by features that need them: weather and city search
> (Open-Meteo), song lyrics (LRCLIB), calendar feeds the user adds, translation, and GitHub Copilot usage if the
> user enters a token.

### Things reviewers look for

- A **privacy policy URL** is required, because the app reads the clipboard and keyboard.
- The description and screenshots should match what the app does. `--readme-shots` renders clean screenshots
  (see the main README).
- Don't name other products' trademarks in the listing ("inspired by notch apps on the Mac" is fine).
