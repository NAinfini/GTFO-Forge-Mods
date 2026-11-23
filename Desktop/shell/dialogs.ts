// Every file the app asks for comes through pickPaths: the system's dialog, with the file types and, for 'files', several at
// once. An empty answer is a cancel. With --ai-control an agent may answer the next dialog beforehand (dialog:preset), since a
// protocol client cannot drive the system's dialog.
import { dialog, ipcMain, type BaseWindow, type OpenDialogOptions } from 'electron';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, dirname, isAbsolute, join, normalize } from 'node:path';

type PathKind = 'folder' | 'files' | 'file' | 'save';
type FileFilters = OpenDialogOptions['filters'];

export interface DialogsOptions {
  window: () => BaseWindow | null;
  dataDir: () => string;
  aiControlOn: boolean;
  log: (kind: string, detail: unknown) => void;
}

/** Registers dialog:open / dialog:save / dialog:preset. Returns the save dialog for the main process (the one behind dialog:save) and the
 *  reset that forgets the remembered folders (after the data folder moved). */
export function registerDialogs({ window, dataDir, aiControlOn, log }: DialogsOptions): { save: (defaultPath: string, filters: FileFilters) => Promise<string | null>; forget: () => void } {
  const presetPaths: string[][] = [];

  async function pickPaths(kind: PathKind, filters: FileFilters, start: string | undefined): Promise<string[]> {
    const preset = presetPaths.shift();
    if (preset) return preset;
    if (kind === 'save') {
      const r = await dialog.showSaveDialog(window()!, { defaultPath: start, filters });
      return r.canceled || !r.filePath ? [] : [r.filePath];
    }
    const properties: OpenDialogOptions['properties'] = kind === 'folder' ? ['openDirectory'] : kind === 'files' ? ['openFile', 'multiSelections'] : ['openFile'];
    const r = await dialog.showOpenDialog(window()!, { properties, filters, defaultPath: start });
    return r.canceled ? [] : r.filePaths;
  }

  // Every file dialog opens where the last one of its kind was confirmed (a purpose, else the kind and file types), kept
  // across runs in <dataDir>/dialogs.json.
  const dialogsFile = () => join(dataDir(), 'dialogs.json');
  let dialogDirs: Record<string, string> | null = null;
  const dialogKey = (purpose: string, filters?: FileFilters) => [purpose, ...(filters ?? []).flatMap((f) => f.extensions)].join(':');
  function lastDir(key: string): string | undefined {
    if (!dialogDirs) {
      try {
        dialogDirs = JSON.parse(readFileSync(dialogsFile(), 'utf8')) as Record<string, string>;
      } catch {
        dialogDirs = {};
      }
    }
    const dir = dialogDirs[key];
    return dir && existsSync(dir) ? dir : undefined;
  }
  function remember(key: string, dir: string) {
    lastDir(key);
    dialogDirs![key] = dir;
    try {
      writeFileSync(dialogsFile(), JSON.stringify(dialogDirs, null, 2));
    } catch (e) {
      log('dialogs', e);
    }
  }

  ipcMain.handle('dialog:open', async (_e, kind: 'folder' | 'files' | 'file', filters?: FileFilters, purpose?: string) => {
    const key = dialogKey(purpose ?? kind, filters);
    const paths = await pickPaths(kind, filters, lastDir(key));
    if (!paths.length) return [];
    remember(key, kind === 'folder' ? paths[0] : dirname(paths[0]));
    return paths;
  });
  async function save(defaultPath: string, filters: FileFilters): Promise<string | null> {
    const key = dialogKey('save', filters);
    // A bare file name opens in the folder this kind of file was last saved to.
    const last = basename(defaultPath) === defaultPath ? lastDir(key) : undefined;
    const [path] = await pickPaths('save', filters, last ? join(last, defaultPath) : defaultPath);
    if (!path) return null;
    remember(key, dirname(path));
    return path;
  }
  ipcMain.handle('dialog:save', (_e, defaultPath: string, filters: FileFilters) => save(defaultPath, filters));
  ipcMain.handle('dialog:preset', (_e, paths: string[]) => {
    if (!aiControlOn) throw new Error('dialog:preset needs --ai-control');
    if (!paths.length || paths.some((p) => !isAbsolute(p))) throw new Error('dialog:preset takes absolute paths');
    presetPaths.push(paths.map((p) => normalize(p)));
  });
  return {
    save,
    forget: () => {
      dialogDirs = null;
    },
  };
}
