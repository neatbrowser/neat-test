# NEAT Browser

A browser built with WinUI 3 and WebView2, with a vertical tab sidebar.

**Status:** phase W6.2, window colour. The window gradient is generated from one
hue. The palette button at the foot of the sidebar opens a menu with a colour
square (across is the hue, down is the brightness; drag the dot), a Dark/Light
switch and ready-made hues. The choice is saved.

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
  neat.csproj       project (unpackaged, self-contained, x64)
  app.xaml(.cs)     application entry, look constants, shared services
  win.xaml(.cs)     main window: tabs, navigation, bookmarks
  win.side.cs       sidebar states (docked, hidden, peeking) and tab row look
  win.bar.cs        title bar: click-through regions, address chip, copy link
  win.keys.cs       shortcut table and the script that reports keys pressed inside pages
  win.cmd.cs        command bar: the box and its list of suggestions, arrow keys
  win.sess.cs       session: open tabs kept for the next start, closed tabs (Ctrl+Shift+T)
  win.full.cs       full screen (F11)
  web/env.cs        app folders; points WebView2 at the bundled runtime
  web/tab.cs        one tab: its web view, title, address and favicon
  look/look.cs      window colour: turns the saved tint into the window gradient
  look/win.paint.cs colour menu: dark/light, gradient and opacity sliders, swatches
  look/win.pad.cs   colour pad: drag or click the big dot, add/remove dots, change harmony
  look/backdrop.cs  the blur behind the window: Windows Acrylic (or Mica), as in Zen
  look/win.wash.cs  the window gradient as layers that fade to clear (Zen's shape), over the blur
  look/win.grain.cs texture: the layer of fine noise over the window colour, and its dial
  look/wheel.cs     colour pad maths: angle and distance from the centre to a colour (as in Zen)
  look/harmony.cs   how up to three dots relate (complementary, triadic, ...), as in Zen
  look/contrast.cs  text contrast maths: keeps the window readable whatever colour is picked
  look/grain.cs     texture maths: the sixteen dial steps and the noise tile
  data/prefs.cs     what settings.json holds (home page, search engines, window size)
  data/store.cs     loads and saves settings.json
  data/search.cs    turns typed text into an address or a search
  data/session.cs   session.json: the tabs that were open, for "Continue where you left off"
  data/suggest.cs   the rows of the command bar: open tabs, bookmarks, history, typed text
  data/db.cs        connections to browser.db
  data/history.cs   history table
  data/marks.cs     bookmarks table (flat list, no folders yet)
  vault/vault.cs    password vault core, no screen yet: add, change, remove, list, reveal, lock
  vault/types.cs    its results, the sign-in proof (Access), the secret that wipes itself, file shape
  vault/seal.cs     AES-256-GCM sealing and the data that ties a value to its entry, site and field
  vault/keywrap.cs  seals the data key for the Windows user (DPAPI, called directly, no package)
  vault/origin.cs   the one way a site is written: scheme, host in ASCII, port
  vault/gate.cs     the gate: who may show, copy or delete a password (Hello or NEAT password), how long
  pass/hello.cs     Windows Hello, behind the gate's IHello
  pass/clip.cs      SecretClip: copy a password, not in clipboard history or cloud, cleared after 30 seconds
  opts.pass.cs      Settings > Passwords: protection, saved logins, show / copy / delete
  opts.pass.task.cs the cards that page opens: add a login, set or change the NEAT password, confirm a delete
tests/vault.check/  the vault's self-check (plain asserts, no packages), run by CI before the build
```

## Where data is saved

Everything is under `%LOCALAPPDATA%\NEAT\`: `settings.json`, `browser.db`
(history and bookmarks), `session.json` (the open tabs) and the `WebView2Profile` folder.
The password vault is `vault.json`, made the first time the Passwords page is opened.

If `settings.json` cannot be read (for example after a typo in a hand edit),
NEAT starts with the defaults and keeps the damaged file as `settings.json.bad`.

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
