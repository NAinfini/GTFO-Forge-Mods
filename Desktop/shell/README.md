# Desktop shell

The Electron main-process and preload code that desktop apps of ours share: the ForgeSkin editor today, the local map workshop
next. It holds no app content (no skin, no map): the app passes its names, paths and limits in. TypeScript, no dependency of its
own: `electron` and the Node types come from the app that bundles it. An app imports it by relative path (Vite and tsc compile it
with the app; its tsconfig maps `electron` to its own `node_modules`), and `export-mirror.ps1` exports it with ForgeSkin.

## Main process: `index.ts`

`createShell(options)` runs before the app is ready and returns `{ edition, dataDir, log, aiControlOn, aiControlPort, checkUpdate, send, call, loadCheck, appLogTail, window, openWindow, run }` (`call`: a backend call from the main process; `checkUpdate`: the page's update check from the main process; `loadCheck(profile, {lines?})`: what the game said about the app's packages the last time it started from that profile, from the backend's `plugin.loadCheck` and, with `lines`, the Forge lines of `plugin.gameLog` that fail, refuse or skip, each handed back once per run, a log older than the profile's plugins skipped with one log line (`loadCheck.ts`, also on the page as `loadCheck` over `game:loadCheck`); `appLogTail(count)`: the last lines of the app's own log);
`run({ registerIpc, window })` does the rest once it is ready (moves a legacy data folder, installs the protocol, registers the
shell's IPC, calls the app's own `registerIpc`, starts the background process, opens the main window).

| Option | Meaning |
| --- | --- |
| `dataDir` | `installedFolder` (under Documents), `localFolder` (beside the app in dev, beside the exe when portable), `dataMarkers` (paths that mark a folder as this app's data when one is chosen), optional `legacyInstallFolder`. Three editions: dev, installed (Squirrel), portable. |
| `backend` | `args({dataDir, formerDirs})`, `packagedFile()`, `dev()` returning `{project, file}`: a checkout runs `dotnet build` on `project` before each start. Calls and events travel as one JSON line each way; a crash restarts after 1 s, three crashes within 10 s each report `down`. |
| `aiControl` | `envVar`, `defaultPort`, optional `setting(dataDir)`: `--ai-control[=<port>]` (or the environment variable, or the app's setting when it gives one, read at start) opens the DevTools port on 127.0.0.1 and keeps a covered window live. |
| `updateFeed` | Squirrel feed folder URL. The check only reads `RELEASES`; the download starts when the user asks. |
| `protocol` | optional `{ scheme, allow, root? }`: `<scheme>://local/<path>` serves files whose full path matches `allow`; with `root()` the path is relative to it and cannot leave it. |
| `installerRun` | the installer is running the app once: the data folder is not touched. |
| `beforeDataMove` | called after the background process stopped and before the data folder moves. |

The window (`run`'s `window`): `browserWindow` (its constructor options, `webPreferences.preload` required; the shell adds context
isolation, sandbox and `backgroundThrottling` off under `--ai-control`), `titleBar`, `load(contents)`, `onClosed`. The shell shows
it when ready, reloads a dead renderer, retries a failed first load five times and opens `https://` links in the browser.
`shell.window()` is the window (a `BaseWindow`), `shell.send` reaches its page.

## Title bar and window chrome

Every window of a shell app is frameless and carries the shell's title bar: the program's icon and name, a drag area, the app's
action buttons, minimize, maximize/restore (its icon follows the state) and close; the bar fades while the window is not focused.
Double-click, Windows snapping and edge resizing are the system's (the bar is a drag region of a window that keeps its thick frame).

| `titleBar` | Meaning |
| --- | --- |
| `title`, `accent`, `background` | name beside the icon, the colour of the bar's top line and hovered actions, the bar's colour |
| `icon` | an image file; without one the exe's own icon |
| `host` | `'page'` (default): the page draws the bar at its top (it gets `padding-top` on `html`) and may put its own controls into the bar's slot (`TITLEBAR_SLOT`, ready on `TITLEBAR_READY`, both in `titlebar-ids.ts`, which a page bundle can import). `'view'`: the bar is its own view above the page, whose viewport then starts below it untouched (pages the app does not own, such as the website's 100dvh layouts). |
| `actions` | `{ label, icon (SVG path, 24 grid), run }` buttons left of the window buttons; `run` runs in the main process |

The app's preload calls `mountWindowChrome()` once (it asks the main process what this page draws; a window without chrome, such
as a hidden worker, gets nothing). `shell.openWindow(options, titleBar)` opens another window of the app with the same bar. A page
that holds itself open (`beforeunload`) gets the shell's own dialog (stay / leave) instead of Electron's silent refusal; leaving
repeats the close, the link or the reload. The system's file pickers stay the system's.

Other exports: `detectEdition()` (needed before `createShell` for the installer check), types.

## Renderer side: `preload.ts`

`shellBridge()` returns the methods behind the shell's channels (`call`, `onEvent`, `onLog`, `onStatus`, `dataDir`, `edition`,
`moveDataDir`, dialogs, `presetPaths`, `openPath`, `appLog`, `pathOf`, update calls, `version`, `loadCheck`); the app spreads it
into its own bridge and adds its methods with `subscribe` and `ipcRenderer`. `exposeHostBridge(name, api)` exposes the bridge as
`window.<name>`; it refuses `forge`, which the website's page commands use, so a shell app that loads website pages exposes
`window.forgeHost`. The shell writes no name of its own.

## Not in the shell

File write whitelists, file and profile watchers, a hidden drawing window and file-type rules beyond `protocol.allow` belong to the
app, which registers them in `registerIpc`.