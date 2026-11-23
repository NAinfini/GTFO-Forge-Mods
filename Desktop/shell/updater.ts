// Squirrel update check: reads the feed and says which version it holds; nothing is downloaded until the user asks for it.
import { app, autoUpdater, ipcMain, net } from 'electron';
import { statSync } from 'node:fs';
import { dirname, join } from 'node:path';
import type { Edition } from './dataDir';

/** What a check found: `available` (an installed copy can download `version`) and `portable` (a newer `version` exists,
 *  fetched by hand) carry the version. */
export type UpdateCheck = { state: 'dev' | 'latest' | 'error' } | { state: 'available' | 'portable'; version: string };

export interface UpdaterOptions {
  /** Folder URL holding the newest installer's `RELEASES` and `.nupkg`. */
  feed: string;
  edition: Edition;
  send: (channel: string, ...args: unknown[]) => void;
  log: (message: string) => void;
}

/** The newest package in the feed: its file name, version and size, from the last line of RELEASES. */
async function feedPackage(feed: string): Promise<{ name: string; version: string; size: number }> {
  const r = await net.fetch(`${feed}/RELEASES`);
  if (!r.ok) throw new Error(`RELEASES: HTTP ${r.status}`);
  const [, url, size] = (await r.text())
    .replace(/^\uFEFF/, '')
    .trim()
    .split(/\r?\n/)
    .pop()!
    .split(' ');
  const name = decodeURIComponent(url.split('/').pop()!);
  const version = /-(\d+(?:\.\d+)*)-full\.nupkg$/i.exec(name)?.[1];
  if (!version) throw new Error(`RELEASES: no version in ${name}`);
  return { name, version, size: Number(size) || 0 };
}

/** True when version `a` is above `b`, part by part. */
function newer(a: string, b: string): boolean {
  const [x, y] = [a, b].map((v) => v.split('.').map(Number));
  for (let i = 0; i < Math.max(x.length, y.length); i++) if ((x[i] ?? 0) !== (y[i] ?? 0)) return (x[i] ?? 0) > (y[i] ?? 0);
  return false;
}

/** Registers update:check / update:download / update:restart (before the app is ready) and wires Squirrel's events (once ready). */
export function registerUpdater({ feed, edition, send, log }: UpdaterOptions): { check: () => Promise<UpdateCheck>; wire: () => void } {
  const updateLog = (message: string) => log(message);
  const check = async (): Promise<UpdateCheck> => {
    if (edition === 'dev') return { state: 'dev' };
    try {
      const { version } = await feedPackage(feed);
      return newer(version, app.getVersion()) ? { state: edition === 'portable' ? 'portable' : 'available', version } : { state: 'latest' };
    } catch (e) {
      updateLog(`check failed: ${String(e)}`);
      return { state: 'error' };
    }
  };
  ipcMain.handle('update:check', check);
  // The user chose to update: Squirrel checks the same feed, downloads the package and stages it for the next start.
  ipcMain.handle('update:download', () => {
    if (edition !== 'installed') return;
    autoUpdater.setFeedURL({ url: feed });
    autoUpdater.checkForUpdates();
  });
  ipcMain.handle('update:restart', () => autoUpdater.quitAndInstall());

  // The download takes minutes and Squirrel reports no progress: the window shows a bar from the package's size in the
  // feed's RELEASES and the size of the file Update.exe is writing into <install>/packages/. Unknown sizes show a moving bar.
  let watching = false;
  async function watchDownload() {
    if (watching) return;
    watching = true;
    let file: string | null = null;
    let total = 0;
    try {
      const found = await feedPackage(feed);
      file = join(dirname(process.execPath), '..', 'packages', found.name);
      total = found.size;
    } catch (e) {
      updateLog(`progress: no package size: ${String(e)}`);
    }
    const tick = () => {
      let received = 0;
      try {
        if (file) received = statSync(file).size;
      } catch {
        // not started yet
      }
      send('update:progress', received, total);
    };
    tick();
    const timer = setInterval(tick, 1000);
    const stop = (done: boolean) => () => {
      clearInterval(timer);
      watching = false;
      send('update:progress', done ? total || 1 : -1, total || 1);
    };
    autoUpdater.once('update-downloaded', stop(true));
    autoUpdater.once('error', stop(false));
  }

  // Nothing updates on its own: the window asks which version the feed holds (update:check) and offers it; only the user's click
  // downloads it, and the window then offers the restart.
  const wire = () => {
    autoUpdater.on('update-available', () => void watchDownload());
    autoUpdater.on('update-downloaded', () => send('update:ready'));
    autoUpdater.on('update-not-available', () => send('update:none'));
    autoUpdater.on('error', (e) => {
      updateLog(`update failed: ${e.message}`);
      send('update:failed', e.message);
    });
  };
  return { check, wire };
}
