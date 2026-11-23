import { contextBridge, ipcRenderer } from 'electron';
import { mountWindowChrome, shellBridge, subscribe } from '../../../../Desktop/shell/preload';

// The shell's methods (backend calls, data folder, dialogs, updates, window controls, log) plus the skin editor's own. The editor
// does not load website pages, so its bridge keeps the name window.forge; a shell app that does uses window.forgeHost.
const api = {
  ...shellBridge(),
  /** Development editor only: builds and stages the plugin from the checkout before an install (main.ts stageDevPlugin). */
  stagePlugin: (): Promise<{ ok: true } | { ok: false; reason: 'notDev' | 'failed'; detail?: string }> => ipcRenderer.invoke('plugin:stage'),
  writeFile: (path: string, data: Uint8Array): Promise<void> => ipcRenderer.invoke('files:write', path, data),
  exists: (path: string): Promise<boolean> => ipcRenderer.invoke('files:exists', path),
  /** A text file where writeFile may write, null when it is missing. */
  readText: (path: string): Promise<string | null> => ipcRenderer.invoke('files:readText', path),
  /** [size, write time in ms] of a file, null when it is not one. */
  stat: (path: string): Promise<[number, number] | null> => ipcRenderer.invoke('files:stat', path),
  /** Watches these files (replacing the last list); onFilesChanged reports the ones written since. */
  watchFiles: (paths: string[]): Promise<void> => ipcRenderer.invoke('files:watch', paths),
  onFilesChanged: (cb: (paths: string[]) => void) => subscribe('files:changed', cb),
  /** Follows a game profile's skins folder and the plugin's choices (null: none); onProfileChanged says when they changed. */
  watchProfile: (profile: string | null): Promise<void> => ipcRenderer.invoke('profile:watch', profile),
  onProfileChanged: (cb: () => void) => subscribe('profile:changed', cb),
  /** Draws a card picture in the hidden card window (renderer/viewport/CardWindow.tsx); null when it could not be drawn. */
  drawCard: (job: unknown): Promise<unknown> => ipcRenderer.invoke('card:draw', job),
  /** The card window: takes the jobs (after saying it is ready) and hands back each picture under its job's id. */
  onCardJob: (cb: (id: number, job: unknown) => void) => {
    const off = subscribe('card:job', cb);
    ipcRenderer.send('card:ready');
    return off;
  },
  cardDone: (id: number, result: unknown): void => ipcRenderer.send('card:done', id, result),
};

export type ForgeApi = typeof api;
contextBridge.exposeInMainWorld('forge', api);
mountWindowChrome();
