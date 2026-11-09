# Overlay Browser

An always-on-top browser window for gaming downtime. Run it, then play your game in
**Windowed (Fullscreen)** / borderless mode — exclusive fullscreen hides all overlays.

| Key / control | What it does |
|---|---|
| `Ctrl+B` | Show / hide (showing does not take keyboard focus from the game) |
| `Ctrl+Shift+B` | Click-through on/off — mouse clicks go to the game, frame turns purple |
| Drag the top bar | Move |
| Drag any edge or corner | Resize |
| ◐ slider | Opacity (15–100%) |
| ⌂ right-click | Make the current page your home page |
| Video fullscreen button | Fills the screen, still at your opacity |
| Tray icon | Left-click show/hide; right-click for reset position and **Exit** |

✕ and Alt+F4 only hide it — quit from the tray icon.
Position, size, opacity and last page are remembered in `%LOCALAPPDATA%\OverlayBrowser`
(logins/cookies too).

## Build

```
powershell -ExecutionPolicy Bypass -File build.ps1
```

Uses the C# compiler that ships with Windows; the first build downloads the WebView2 SDK
from nuget.org. Output: `bin\OverlayBrowser.exe` (keep the three DLLs next to it).
