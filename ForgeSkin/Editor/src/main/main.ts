import { app, BrowserWindow, ipcMain } from 'electron';
import { execFile } from 'node:child_process';
import { cpSync, existsSync, readdirSync, rmSync, watch, type FSWatcher } from 'node:fs';
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import { basename, dirname, isAbsolute, join, normalize, resolve, sep } from 'node:path';
import squirrelStartup from 'electron-squirrel-startup';
import { createShell, detectEdition } from '../../../../Desktop/shell/index';

// The shell (Desktop/shell) holds what any desktop app of ours shares: the backend process, the data folder, updates, dialogs,
// the debugging port, the log and the window's recovery. This file passes it ForgeSkin's settings and keeps what is the skin
// editor's own: the write whitelist, the file and profile watches, the card window, the sf:// file types and the plugin staging.

const edition = detectEdition();
// The installer runs the app once per install, update and uninstall to make or remove its shortcuts; it quits then. The
// portable copy has no installer.
const squirrelRun = edition === 'installed' && squirrelStartup;
if (squirrelRun) app.quit();

// Moves the pre-rename Electron profile (GTFO Skin Forge -> ForgeSkin: preferences, panel layout, last weapon) to the
// new app name before Chromium opens it; once moved there is nothing left to move. Electron has already created the new
// folder by now, so the old one is copied in and then removed.
function movePreRenameUserData(): void {
  const old = join(app.getPath('appData'), 'GTFO Skin Forge');
  const now = app.getPath('userData');
  if (!existsSync(old) || existsSync(join(now, 'Local Storage'))) return;
  cpSync(old, now, { recursive: true });
  rmSync(old, { recursive: true, force: true });
}
movePreRenameUserData();

// sf:// serves what the editor shows (textures, models, videos, sounds, fonts, and a character work's converted model: its
// descriptor and mesh, drawn when the work came from a pack) wherever the user keeps them, and no other kind of file.
const SERVED = /\.(png|jpe?g|tga|tiff?|bmp|webp|gif|exr|mp4|webm|mov|m4v|wav|mp3|ogg|flac|m4a|aac|opus|ttf|otf|woff2?|glb|gltf|vrm|bin|forgemesh)$|[\\/]character[\\/]model\.json$/i;

const shell = createShell({
  dataDir: { installedFolder: 'ForgeSkin', localFolder: 'GTFO_UserData', dataMarkers: ['settings.json', 'autosave', join('logs', 'backend.log')], legacyInstallFolder: 'GTFO_UserData' },
  backend: {
    args: ({ dataDir, formerDirs }) => ['--data-dir', dataDir, ...formerDirs.flatMap((d) => ['--former-data-dir', d])],
    packagedFile: () => join(process.resourcesPath, 'publish', 'ForgeSkin.Backend.exe'),
    // The backend installs the plugin from artifacts/forge-skin-release/publish/plugin (Install.PluginSource), which
    // build-editor-release.ps1 -PluginOnly -ForgeCore <release-set ForgeCore zip> stages; without it the backend says so (no-plugin).
    dev: () => {
      const project = join(app.getAppPath(), '..', 'Backend');
      return { project, file: join(project, 'bin', 'Debug', 'net10.0', 'win-x64', 'ForgeSkin.Backend.exe') };
    },
  },
  // `--ai-control[=<port>]` (default 9333); the renderer then offers window.forgeAI (renderer/aiControl.ts).
  aiControl: { envVar: 'FORGESKIN_AI_CONTROL', defaultPort: 9333 },
  // Updates come from the GTFO-Forge-Mods repository: its release `ForgeSkin-update` always holds the newest installer's
  // RELEASES and .nupkg (the repository has other packages' releases, so its "latest" is not ours).
  updateFeed: 'https://github.com/NAinfini/GTFO-Forge-Mods/releases/download/ForgeSkin-update',
  // Local files (preview GLB, textures, videos) are served to the renderer as sf://local/<abs path>.
  protocol: { scheme: 'sf', allow: SERVED },
  installerRun: squirrelRun,
  beforeDataMove: () => watchFiles([]), // a watched folder cannot be deleted
});
const { send } = shell;
const appLog = shell.log;

// Card pictures (the work list's and the gallery's) are drawn in a hidden second window of the same renderer (renderer/viewport/
// CardWindow.tsx, the page's `#card`), with the 3D view's own stage, so drawing them never holds up the editor window. It opens
// with the first picture asked for, draws one at a time and closes after a minute without work.
let cards: Promise<BrowserWindow> | null = null;
const cardJobs = new Map<number, (result: unknown) => void>();
let cardSeq = 0;
let cardIdle: NodeJS.Timeout | undefined;

function cardWindow(): Promise<BrowserWindow> {
  cards ??= new Promise<BrowserWindow>((ready, failed) => {
    const w = new BrowserWindow({
      show: false,
      width: 600,
      height: 400,
      webPreferences: { preload: join(__dirname, 'preload.cjs'), contextIsolation: true, nodeIntegration: false, sandbox: true, backgroundThrottling: false },
    });
    // Ready once its page listens for jobs (card:ready); a job sent before that would be lost.
    const onReady = (e: Electron.IpcMainEvent) => e.sender === w.webContents && (ipcMain.off('card:ready', onReady), ready(w));
    ipcMain.on('card:ready', onReady);
    const gone = () => {
      ipcMain.off('card:ready', onReady);
      cards = null;
      failed(new Error('card window closed'));
      for (const done of cardJobs.values()) done(null);
      cardJobs.clear();
    };
    w.on('closed', gone);
    w.webContents.on('render-process-gone', (_e, d) => (appLog('card window gone', d), w.destroy()));
    if (MAIN_WINDOW_VITE_DEV_SERVER_URL) void w.loadURL(`${MAIN_WINDOW_VITE_DEV_SERVER_URL}#card`).catch(() => w.destroy());
    else void w.loadFile(join(__dirname, `../renderer/${MAIN_WINDOW_VITE_NAME}/index.html`), { hash: 'card' }).catch(() => w.destroy());
  });
  return cards;
}

async function drawCard(job: unknown): Promise<unknown> {
  clearTimeout(cardIdle);
  const w = await cardWindow();
  const id = ++cardSeq;
  const result = await new Promise((done) => {
    cardJobs.set(id, done);
    w.webContents.send('card:job', id, job);
  });
  if (!cardJobs.size) cardIdle = setTimeout(() => void cards?.then((x) => x.destroy()), 60_000);
  return result;
}

// The renderer may only write files inside the data dir (autosaved skins and their frames live there), and next to a skin
// project saved elsewhere (a folder holding a .forgeskinproj) its generated frames (`.frames/`) and converted sounds
// (`sounds/*.ogg`).
function writable(path: string): boolean {
  if (!isAbsolute(path)) return false;
  const p = normalize(path);
  if (p.startsWith(resolve(shell.dataDir()) + sep)) return true;
  const dirs = p.split(sep);
  const frames = dirs.indexOf('.frames');
  const project = frames > 0 && /\.(png|json)$/i.test(p) ? dirs.slice(0, frames) : dirs.at(-2) === 'sounds' && p.endsWith('.ogg') ? dirs.slice(0, -2) : null;
  try {
    return !!project && readdirSync(project.join(sep)).some((f) => f.endsWith('.forgeskinproj'));
  } catch {
    return false;
  }
}

// The development editor installs the plugin built from this checkout: before an install the user asked for, it runs
// build-editor-release.ps1 -PluginOnly, which builds ForgeSkin and ForgeCore from source and stages
// artifacts/forge-skin-release/publish/plugin for the backend. Nothing has to be packaged first. The script's whole output goes
// to app.log; the window gets its last lines, the failed step as PowerShell's error box says it, for the log line it writes.
async function stageDevPlugin(): Promise<{ ok: true } | { ok: false; reason: 'notDev' | 'failed'; detail?: string }> {
  if (edition !== 'dev') return { ok: false, reason: 'notDev' };
  const root = join(app.getAppPath(), '..', '..', '..');
  const script = join(root, 'Mods', 'ForgeSkin', 'tools', 'build-editor-release.ps1');
  const tail = (text: string, n: number) =>
    text
      .split('\n')
      .map((l) => l.replace(/^\s*\|\s?/, '').trim())
      .filter((l) => l && !/^[~\s]+$/.test(l) && !/^Line\s*\|?$/.test(l) && !/^\d+\s*\|/.test(l) && !/^Exception:/.test(l))
      .slice(-n);
  return new Promise((done) =>
    execFile('pwsh', ['-NoProfile', '-File', script, '-PluginOnly'], { cwd: root, windowsHide: true, maxBuffer: 64 << 20 }, (error, stdout, stderr) => {
      appLog('plugin stage', `${script} -PluginOnly: ${error ? 'failed' : 'ok'}\n${stdout}${stderr}`);
      done(error ? { ok: false, reason: 'failed', detail: [...tail(stdout, 2), ...tail(stderr, 1)].join('\n') } : { ok: true });
    }),
  );
}

function registerIpc() {
  ipcMain.handle('plugin:stage', () => stageDevPlugin());
  ipcMain.handle('files:write', async (_e, path: string, data: Uint8Array) => {
    if (!writable(path)) throw new Error(`refused write outside frames/data dir: ${path}`);
    await mkdir(dirname(path), { recursive: true });
    // The same bytes again (a bake whose inputs did not change) leave the file and its write time alone, so the pack
    // build reuses what it made from it.
    if ((await readFile(path).catch(() => null))?.equals(data)) return;
    await writeFile(path, data);
  });
  // A text file the renderer may write (a bake's record), null when it is missing.
  ipcMain.handle('files:readText', async (_e, path: string) => {
    if (!writable(path)) throw new Error(`refused read outside frames/data dir: ${path}`);
    return readFile(path, 'utf8').catch(() => null);
  });
  // [size, write time in ms] of a file, null when it is not one: part of a bake's input key (renderer frames.ts bakeKey).
  ipcMain.handle('files:stat', async (_e, path: string) => {
    const s = await stat(path).catch(() => null);
    return s?.isFile() ? [s.size, s.mtimeMs] : null;
  });
  ipcMain.handle('files:exists', (_e, path: string) => existsSync(path));
  ipcMain.handle('files:watch', (_e, paths: string[]) => watchFiles(paths));
  ipcMain.handle('profile:watch', (_e, profile: string | null) => watchProfile(profile));
  ipcMain.handle('card:draw', (_e, job: unknown) => drawCard(job).catch(() => null));
  ipcMain.on('card:done', (_e, id: number, result: unknown) => {
    cardJobs.get(id)?.(result);
    cardJobs.delete(id);
  });
}

// The files the open skin uses, watched through their folders (an editor saving a file often replaces it, which a watch on
// the file itself loses). A changed file is reported once its writes have been quiet for 400 ms.
const watched = new Map<string, { watcher: FSWatcher; names: Set<string> }>();
const changed = new Set<string>();
let changedTimer: NodeJS.Timeout | undefined;
function watchFiles(paths: string[]) {
  const want = new Map<string, Set<string>>();
  for (const p of paths) {
    const dir = dirname(p);
    if (!want.has(dir)) want.set(dir, new Set());
    want.get(dir)!.add(basename(p).toLowerCase());
  }
  for (const [dir, w] of watched)
    if (!want.has(dir)) {
      w.watcher.close();
      watched.delete(dir);
    }
  for (const [dir, names] of want) {
    const w = watched.get(dir);
    if (w) {
      w.names = names;
      continue;
    }
    try {
      const entry = { names, watcher: watch(dir) };
      entry.watcher.on('change', (_type, name) => {
        if (!name || !entry.names.has(String(name).toLowerCase())) return;
        changed.add(join(dir, String(name)));
        clearTimeout(changedTimer);
        changedTimer = setTimeout(() => {
          send('files:changed', [...changed]);
          changed.clear();
        }, 400);
      });
      entry.watcher.on('error', () => (entry.watcher.close(), watched.delete(dir)));
      watched.set(dir, entry);
    } catch (e) {
      appLog('watch', e); // a folder that is gone: nothing to watch
    }
  }
}

// The chosen game profile's skin state: its skins folder (packs, active.json, picks) and the plugin's choices in config/, watched
// so the window's lists follow a pack put in or taken out by hand, the game's picker, or another program. Reported once the
// writes have been quiet for 400 ms.
let profileWatchers: FSWatcher[] = [];
let profileTimer: NodeJS.Timeout | undefined;
function watchProfile(profile: string | null) {
  for (const w of profileWatchers) w.close();
  profileWatchers = [];
  if (!profile) return;
  const folders: [string, (name: string) => boolean][] = [
    [join(profile, 'BepInEx', 'ForgeSkin', 'skins'), () => true],
    [join(profile, 'BepInEx', 'config'), (name) => /^ForgeSkin\.choices\.json$/i.test(name)],
  ];
  for (const [dir, counts] of folders) {
    if (!existsSync(dir)) continue; // made by the first install, which watches again
    try {
      const w = watch(dir, (_type, name) => {
        if (name && !counts(String(name))) return;
        clearTimeout(profileTimer);
        profileTimer = setTimeout(() => send('profile:changed'), 400);
      });
      w.on('error', () => w.close());
      profileWatchers.push(w);
    } catch (e) {
      appLog('watch', e); // a profile without BepInEx: nothing to follow
    }
  }
}

shell.run({
  registerIpc,
  window: {
    browserWindow: {
      width: 1600,
      height: 960,
      minWidth: 1180,
      minHeight: 720,
      backgroundColor: '#15171a',
      title: 'ForgeSkin',
      // The packaged exe carries the icon (packagerConfig.icon); from source the window takes it from assets/.
      icon: app.isPackaged ? undefined : join(app.getAppPath(), 'assets', 'icon.ico'),
      webPreferences: { preload: join(__dirname, 'preload.cjs') },
    },
    // The shell's title bar; the editor's own controls go into its slot (renderer/ui/TopBar.tsx).
    titleBar: { title: 'ForgeSkin', icon: app.isPackaged ? undefined : join(app.getAppPath(), 'assets', 'icon.png'), accent: '#ff7a2f', background: '#141619' },
    load: (page) => (MAIN_WINDOW_VITE_DEV_SERVER_URL ? page.loadURL(MAIN_WINDOW_VITE_DEV_SERVER_URL) : page.loadFile(join(__dirname, `../renderer/${MAIN_WINDOW_VITE_NAME}/index.html`))),
    // The hidden card window goes with the editor's (it would keep the app running).
    onClosed: () =>
      void cards?.then(
        (w) => w.destroy(),
        () => undefined,
      ),
  },
});
