// The desktop shell's renderer side (preload): the methods behind the channels `createShell` registers. The app spreads
// `shellBridge()` into its own bridge object and exposes it itself, or hands it to `exposeHostBridge`, and calls
// `mountWindowChrome()` once for the title bar (chrome.ts).
import { contextBridge, ipcRenderer, webUtils, type IpcRendererEvent } from 'electron';
import type { LoadCheck } from './loadCheck';

export { mountWindowChrome } from './chrome';

export type FileFilter = { name: string; extensions: string[] };

export function subscribe<A extends unknown[]>(channel: string, cb: (...args: A) => void): () => void {
  const fn = (_e: IpcRendererEvent, ...args: unknown[]) => cb(...(args as A));
  ipcRenderer.on(channel, fn);
  return () => ipcRenderer.off(channel, fn);
}

export const shellBridge = () => ({
  call: async (method: string, params?: unknown): Promise<unknown> => {
    const r = (await ipcRenderer.invoke('backend:call', method, params)) as { result?: unknown; error?: unknown };
    if ('error' in r) throw r.error;
    return r.result;
  },
  onEvent: (cb: (event: string, data: unknown) => void) => subscribe('backend:event', cb),
  onLog: (cb: (line: string) => void) => subscribe('backend:log', cb),
  onStatus: (cb: (status: 'ready' | 'restarting' | 'down') => void) => subscribe('backend:status', cb),
  dataDir: (): Promise<string> => ipcRenderer.invoke('app:dataDir'),
  /** dev: run from a checkout; installed: the Setup.exe install; portable: the unzipped copy. */
  edition: (): Promise<'dev' | 'installed' | 'portable'> => ipcRenderer.invoke('app:edition'),
  /** Whether the app was started with --ai-control. */
  aiControl: (): Promise<boolean> => ipcRenderer.invoke('app:aiControl'),
  /** Moves the data folder; the window reloads after a move that worked. */
  moveDataDir: (to: string): Promise<{ ok: true; adopted?: boolean; leftover?: string } | { ok: false; reason: 'dev' | 'same' | 'nested' | 'notEmpty' | 'failed'; detail?: string }> =>
    ipcRenderer.invoke('data:move', to),
  /** The system's dialog: 'file' one file, 'files' one or more. Opens where the last dialog of the same `purpose` (else kind and file types) was confirmed. */
  openDialog: (kind: 'folder' | 'files' | 'file', filters?: FileFilter[], purpose?: string): Promise<string[]> => ipcRenderer.invoke('dialog:open', kind, filters, purpose),
  saveDialog: (defaultPath: string, filters: FileFilter[]): Promise<string | null> => ipcRenderer.invoke('dialog:save', defaultPath, filters),
  /** With --ai-control: the answer of the next file dialog, which then does not open. */
  presetPaths: (paths: string[]): Promise<void> => ipcRenderer.invoke('dialog:preset', paths),
  openPath: (path: string): Promise<string> => ipcRenderer.invoke('shell:open', path),
  showInFolder: (path: string): Promise<void> => ipcRenderer.invoke('shell:show', path),
  /** Appends an error line to <dataDir>/logs/app.log. */
  appLog: (kind: string, detail: string): void => ipcRenderer.send('app:log', kind, detail),
  /** Absolute path of a dropped File (Electron removed File.path). */
  pathOf: (file: File): string => webUtils.getPathForFile(file),
  /** Which version the update feed holds; downloads nothing. `available`: an installed copy can download `version`
   *  (downloadUpdate); `portable`: a newer `version` exists, fetched by hand. */
  checkUpdate: (): Promise<{ state: 'dev' | 'latest' | 'error' } | { state: 'available' | 'portable'; version: string }> => ipcRenderer.invoke('update:check'),
  /** Downloads the update the check found (onUpdateProgress, then onUpdateReady). */
  downloadUpdate: (): Promise<void> => ipcRenderer.invoke('update:download'),
  version: (): Promise<string> => ipcRenderer.invoke('app:version'),
  /** What BepInEx said about the program's packages in `profile`'s game log, and with `lines` the Forge lines that failed, refused or
   *  skipped something; each only once per run of the program (loadCheck.ts). */
  loadCheck: (profile: string, options?: { lines?: boolean }): Promise<LoadCheck> => ipcRenderer.invoke('game:loadCheck', profile, options),
  /** A found update downloading: bytes so far and in all (0 when unknown); received -1 means it failed, = total done. */
  onUpdateProgress: (cb: (received: number, total: number) => void) => subscribe('update:progress', cb),
  onUpdateReady: (cb: () => void) => subscribe('update:ready', cb),
  restartToUpdate: (): Promise<void> => ipcRenderer.invoke('update:restart'),
});

/**
 * Exposes a bridge as `window.<name>`. The name is the caller's: a workshop that loads the website's pages uses `forgeHost`;
 * `forge` belongs to the page commands and is refused.
 */
export function exposeHostBridge(name: string, api: object): void {
  if (name === 'forge') throw new Error('window.forge belongs to the website page commands; expose the host bridge as window.forgeHost');
  contextBridge.exposeInMainWorld(name, api);
}
