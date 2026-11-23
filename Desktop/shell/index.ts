// The desktop shell's main-process side: what an Electron app with a background process, a data folder and an update feed shares.
// See README.md.
import { app, ipcMain, shell as electronShell, type BaseWindow, type WebContents } from 'electron';
import { stat } from 'node:fs/promises';
import { isAbsolute } from 'node:path';
import { enableAiControl, type AiControlOptions } from './aiControl';
import { appLogTail, createAppLog, limitCaches, logProcessFaults } from './appLog';
import { Backend, type BackendOptions } from './backend';
import { createDataDir, type DataDirOptions } from './dataDir';
import { registerDialogs } from './dialogs';
import { createLoadCheck } from './loadCheck';
import { registerLocalProtocol, type LocalProtocolOptions } from './protocol';
import { registerUpdater } from './updater';
import { createMainWindow, framedWindow, loadExeIcon, registerWindowControls, sendToWindows, type WindowOptions } from './window';

export { detectEdition, type DataDir, type DataDirOptions, type Edition, type MoveResult } from './dataDir';
export type { AppLog } from './appLog';
export type { BackendError, BackendOptions } from './backend';
export { forgeFinding, type GameLog, type GameLogLine, type LoadCheck, type LoadNote } from './loadCheck';
export type { UpdateCheck } from './updater';
export type { TitleBarOptions } from './window';

export interface ShellOptions {
  dataDir: DataDirOptions;
  /** The background process; its `args` are given the data folder and the folders it was moved out of. */
  backend: Omit<BackendOptions, 'args'> & { args: (data: { dataDir: string; formerDirs: string[] }) => string[] };
  aiControl: AiControlOptions;
  /** Squirrel feed folder URL (RELEASES and .nupkg). */
  updateFeed: string;
  protocol?: LocalProtocolOptions;
  /** The installer is running the app once to make or remove shortcuts: the data folder is left alone. */
  installerRun?: boolean;
  /** Called before the data folder moves, with the background process already stopped (stop watchers on it). */
  beforeDataMove?: () => void;
}

/**
 * Sets up everything that has to happen before the app is ready (the debugging port, caches, the protocol's privileges, fault
 * logging) and returns the handles. `run` does the rest once the app is ready.
 */
export function createShell(options: ShellOptions) {
  const data = createDataDir(options.dataDir);
  const { edition } = data;
  const log = createAppLog(data.dataDir);
  const aiControlPort = enableAiControl(options.aiControl, log, data.dataDir());
  const aiControlOn = aiControlPort !== null;
  limitCaches(log);
  const handleProtocol = options.protocol ? registerLocalProtocol(options.protocol) : undefined;
  logProcessFaults(log);

  let win: BaseWindow | null = null;
  let contents: WebContents | null = null;
  let saveDialog: ReturnType<typeof registerDialogs>['save'] | null = null;
  let quitting = false;
  // The backend is stopped on purpose while the data folder moves.
  let moving = false;
  let startedAt = 0;
  let crashes = 0;
  // The window can be gone while the app shuts down (the backend exits after it).
  const send = (channel: string, ...args: unknown[]) => {
    if (contents && !contents.isDestroyed()) contents.send(channel, ...args);
  };

  const backend = new Backend(
    { ...options.backend, args: () => options.backend.args({ dataDir: data.dataDir(), formerDirs: data.formerDirs() }) },
    (event, payload) => send('backend:event', event, payload),
    (line) => send('backend:log', line),
    (code) => {
      send('backend:log', `backend exited (${code})`);
      if (quitting || moving) return;
      // Restart, but give up after repeated crashes right after start (shown to the user as "down").
      crashes = Date.now() - startedAt < 10_000 ? crashes + 1 : 0;
      if (crashes >= 3) return send('backend:status', 'down');
      send('backend:status', 'restarting');
      setTimeout(startBackend, 1000);
    },
  );
  const loadCheck = createLoadCheck((method, params) => backend.call(method, params), log);
  function startBackend() {
    startedAt = Date.now();
    // Ready only once the process exists; an exe that is missing never reports ready.
    backend.start(() => send('backend:status', 'ready'));
  }

  function registerIpc() {
    // IPC turns a rejected plain object into "[object Object]", so backend errors travel as a value and preload rethrows them.
    ipcMain.handle('backend:call', (_e, method: string, params: unknown) =>
      backend.call(method, params).then(
        (result) => ({ result }),
        (error: unknown) => ({ error }),
      ),
    );
    ipcMain.handle('app:dataDir', () => data.dataDir());
    ipcMain.handle('app:edition', () => edition);
    ipcMain.handle('app:aiControl', () => aiControlOn);
    ipcMain.handle('app:version', () => app.getVersion());
    ipcMain.handle('game:loadCheck', (_e, profile: string, options?: { lines?: boolean }) => loadCheck(profile, options));
    ipcMain.on('app:log', (_e, kind: string, detail: string) => log(kind, detail));
    const dialogs = registerDialogs({ window: () => win, dataDir: data.dataDir, aiControlOn, log });
    saveDialog = dialogs.save;
    const forgetDialogDirs = dialogs.forget;
    // The window saves first and reloads after a move (its open document and paths name the old folder).
    ipcMain.handle('data:move', async (_e, to: string) => {
      if (edition === 'dev') return { ok: false, reason: 'dev' };
      moving = true;
      try {
        await backend.stop();
        options.beforeDataMove?.();
        const r = await data.moveData(to, true).catch((e: Error) => ({ ok: false as const, reason: 'failed' as const, detail: e.message }));
        log('data folder', { to, ...r });
        forgetDialogDirs();
        return r;
      } finally {
        moving = false;
        startBackend();
      }
    });
    // A folder opens in Explorer; a file is only shown in its folder, never launched.
    ipcMain.handle('shell:open', async (_e, path: string) => {
      if (isAbsolute(path) && (await stat(path).catch(() => null))?.isDirectory()) return electronShell.openPath(path);
      electronShell.showItemInFolder(path);
      return '';
    });
    ipcMain.handle('shell:show', (_e, path: string) => electronShell.showItemInFolder(path));
    registerWindowControls();
  }
  const { check: checkUpdate, wire: wireUpdater } = registerUpdater({ feed: options.updateFeed, edition, send: sendToWindows, log: (m) => log('update', m) });

  app.on('before-quit', () => {
    quitting = true;
    backend.stop();
  });
  app.on('window-all-closed', () => app.quit());

  return {
    edition,
    dataDir: data.dataDir,
    log,
    aiControlOn,
    /** The debugging port that is open (--ai-control, its environment variable or the app's setting), null when none. */
    aiControlPort,
    /** The update check of the page's `checkUpdate`, from the main process (the app's start-up check). */
    checkUpdate,
    send,
    /** A call to the background process from the main process (the renderer uses `call` of the preload bridge). */
    call: (method: string, params?: unknown) => backend.call(method, params),
    /** What BepInEx and the Forge packages said in a profile's game log since the program's last check (loadCheck.ts), the same as the page's `loadCheck`. */
    loadCheck,
    /** The last `count` lines of the program's own log. */
    appLogTail: (count: number) => appLogTail(data.dataDir(), count),
    /** The system's save dialog from the main process, the same one as the renderer's `saveDialog` (remembered folder, --ai-control
     *  preset); null on cancel. Only after `run` registered the IPC. */
    saveDialog: (defaultPath: string, filters: { name: string; extensions: string[] }[]): Promise<string | null> => {
      if (!saveDialog) throw new Error('saveDialog before run');
      return saveDialog(defaultPath, filters);
    },
    /** The main window, null before `run` opened it. */
    window: () => win,
    /** Another window of the program (settings and the like): frameless, its page draws the shell's title bar (its preload calls mountWindowChrome). */
    openWindow: framedWindow,
    /**
     * Once the app is ready: moves a legacy data folder, installs the protocol, registers the shell's IPC, then `registerIpc` (the
     * app's own), starts the background process and opens the main window.
     */
    run(setup: { registerIpc: () => void; window: WindowOptions }) {
      void app.whenReady().then(async () => {
        // Before anything writes into the data folder.
        if (!options.installerRun) await data.moveInstallData((line) => log('data folder', line));
        handleProtocol?.();
        registerIpc();
        setup.registerIpc();
        startBackend();
        await loadExeIcon();
        ({ window: win, contents } = createMainWindow(setup.window, aiControlOn, () => quitting, log));
        wireUpdater();
      });
    },
  };
}

export type Shell = ReturnType<typeof createShell>;
