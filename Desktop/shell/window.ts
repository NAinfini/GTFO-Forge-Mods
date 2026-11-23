import { app, BaseWindow, BrowserWindow, ipcMain, nativeImage, shell, webContents, WebContentsView, type BrowserWindowConstructorOptions, type WebContents } from 'electron';
import type { AppLog } from './appLog';
import type { ChromeInfo } from './chrome';

/** The shell's title bar on a window (chrome.ts draws it); the system's frame is gone. */
export interface TitleBarOptions {
  /** Shown beside the icon. */
  title: string;
  /** An image file for the icon (PNG or ICO); without one, the program's own exe icon. */
  icon?: string;
  /** The colour of the bar's top line and of a hovered action. */
  accent: string;
  background: string;
  /**
   * 'page' (the default): the window's own page draws the bar at its top through the shell's preload and leaves room for it; it may
   * put its own controls into the bar's slot. 'view': the bar is a view of its own above the page, which keeps the whole viewport
   * below it untouched: for pages the program does not own (the website's, whose layouts size themselves to 100dvh).
   */
  host?: 'page' | 'view';
  /** Buttons left of the window controls; `icon` is an SVG path on a 24 x 24 grid, `run` runs in the main process. */
  actions?: { label: string; icon: string; run: () => void }[];
}

export interface WindowOptions {
  /** The window's own settings; `webPreferences` is completed with contextIsolation, sandbox and the throttling rule. */
  browserWindow: BrowserWindowConstructorOptions & { webPreferences: { preload: string } };
  titleBar: TitleBarOptions;
  /** Loads the page (loadURL / loadFile) into `contents`; called again for a retry. */
  load: (contents: WebContents) => Promise<unknown> | undefined;
  onClosed?: () => void;
}

export type ShellWindow = { window: BaseWindow; contents: WebContents };

const BAR_HEIGHT = 36;
const secure = { contextIsolation: true, nodeIntegration: false, sandbox: true } as const;

// Every web contents that carries the shell's chrome: its window, and the bar it draws (null for a page under a bar view).
const chromed = new Map<number, { window: BaseWindow; bar: TitleBarOptions | null }>();
const iconUrls = new Map<string, string>();
let exeIcon = '';

/** Reads the program's own exe icon for bars that name none; awaited once before the first window. */
export async function loadExeIcon(): Promise<void> {
  exeIcon = (await app.getFileIcon(process.execPath, { size: 'normal' })).toDataURL();
}

function iconUrl(path: string | undefined): string {
  if (!path) return exeIcon;
  if (!iconUrls.has(path)) iconUrls.set(path, nativeImage.createFromPath(path).toDataURL());
  return iconUrls.get(path)!;
}

/** `contents` draws the chrome of `window`; the bar is told when the window is maximized, restored, focused or left. */
function addChrome(window: BaseWindow, contents: WebContents, bar: TitleBarOptions | null): void {
  const id = contents.id;
  chromed.set(id, { window, bar });
  contents.once('destroyed', () => chromed.delete(id));
  if (!bar) return;
  const push = () => contents.isDestroyed() || contents.send('win:state', { maximized: window.isMaximized(), focused: window.isFocused() });
  for (const event of ['maximize', 'unmaximize', 'focus', 'blur', 'restore'] as const) window.on(event as 'focus', push);
}

/**
 * A page that holds the window open (beforeunload) is asked through the shell's dialog (chrome.ts); without this Electron drops the
 * close, reload or link silently. `closing` says whether a close set it off.
 */
function askBeforeLeaving(window: BaseWindow, contents: WebContents): void {
  let closeAt = 0;
  window.on('close', () => (closeAt = Date.now()));
  contents.on('will-prevent-unload', () => contents.send('win:leave', Date.now() - closeAt < 1500));
}

/** A frameless window whose page draws the shell's title bar (host 'page'); also the settings and other windows a program opens. */
export function framedWindow(options: BrowserWindowConstructorOptions & { webPreferences: { preload: string } }, titleBar: TitleBarOptions): BrowserWindow {
  const win = new BrowserWindow({ ...options, titleBarStyle: 'hidden', webPreferences: { ...secure, ...options.webPreferences } });
  addChrome(win, win.webContents, { ...titleBar, host: 'page' });
  askBeforeLeaving(win, win.webContents);
  return win;
}

/** A frameless window with the bar as its own view above the page's (host 'view'). */
function windowWithBarView(options: WindowOptions['browserWindow'], titleBar: TitleBarOptions, keepLive: boolean): ShellWindow {
  const { webPreferences, backgroundColor, ...rest } = options;
  const win = new BaseWindow({ ...rest, backgroundColor, titleBarStyle: 'hidden', show: false });
  const page = new WebContentsView({ webPreferences: { ...secure, backgroundThrottling: !keepLive, ...webPreferences } });
  const bar = new WebContentsView({ webPreferences: { ...secure, preload: webPreferences.preload } });
  if (backgroundColor) page.setBackgroundColor(backgroundColor);
  bar.setBackgroundColor(titleBar.background);
  win.contentView.addChildView(page);
  win.contentView.addChildView(bar);
  const layout = () => {
    const [width, height] = win.getContentSize();
    bar.setBounds({ x: 0, y: 0, width, height: BAR_HEIGHT });
    page.setBounds({ x: 0, y: BAR_HEIGHT, width, height: Math.max(0, height - BAR_HEIGHT) });
  };
  layout();
  win.on('resize', layout);
  addChrome(win, bar.webContents, titleBar);
  addChrome(win, page.webContents, null);
  askBeforeLeaving(win, page.webContents);
  // Keys go to the page: the bar hands its focus on, and a window coming to the front gives it to the page.
  bar.webContents.on('focus', () => page.webContents.focus());
  win.on('focus', () => page.webContents.focus());
  // A window has no beforeunload of its own: closing first closes the page, which may ask to stay (askBeforeLeaving).
  win.on('close', (e) => {
    if (page.webContents.isDestroyed()) return;
    e.preventDefault();
    page.webContents.close({ waitForBeforeUnload: true });
  });
  page.webContents.once('destroyed', () => win.isDestroyed() || win.close());
  win.once('closed', () => bar.webContents.isDestroyed() || bar.webContents.close());
  bar.webContents.once('did-finish-load', () => win.show());
  void bar.webContents.loadURL('data:text/html;charset=utf-8,<!doctype html><title>titlebar</title>');
  return { window: win, contents: page.webContents };
}

/**
 * The main window: frameless with the shell's title bar, shown when ready, a dead renderer is reloaded, a first load that fails (a
 * cold dev server, a crashed network service) is retried, https links open in the browser. A covered or minimized window keeps its
 * frames and timers while an agent drives it (`keepLive`).
 */
export function createMainWindow({ browserWindow, titleBar, load, onClosed }: WindowOptions, keepLive: boolean, quitting: () => boolean, log: AppLog): ShellWindow {
  let shown: ShellWindow;
  if (titleBar.host === 'view') shown = windowWithBarView(browserWindow, titleBar, keepLive);
  else {
    const win = framedWindow({ ...browserWindow, show: false, webPreferences: { backgroundThrottling: !keepLive, ...browserWindow.webPreferences } }, titleBar);
    win.once('ready-to-show', () => win.show());
    shown = { window: win, contents: win.webContents };
  }
  const { window: win, contents } = shown;
  win.on('closed', () => onClosed?.());
  // A dead renderer is reloaded.
  contents.on('render-process-gone', (_e, d) => {
    log('render-process-gone', d);
    if (d.reason !== 'clean-exit' && !quitting()) setTimeout(() => contents.reload(), 500);
  });
  contents.on('unresponsive', () => log('renderer unresponsive', ''));
  let loadTries = 0;
  let loaded = false;
  const run = () => load(contents)?.catch(() => {});
  const retry = (why: unknown) => {
    if (quitting()) return;
    log('did-fail-load', { why, try: loadTries + 1 });
    if (++loadTries > 5) return log('did-fail-load', 'gave up after 5 retries');
    setTimeout(run, 1000);
  };
  contents.on('did-finish-load', () => (loaded = true));
  contents.on('did-fail-load', (_e, code, _desc, url, isMainFrame) => {
    // -3 (aborted) is skipped only while a newer navigation goes on; one aborted by a crashed network service leaves nothing loading.
    if (isMainFrame && (code !== -3 || !contents.isLoading())) retry({ code, url });
  });
  // A network service that crashes before the first page is up can end that load without any did-fail-load.
  app.on('child-process-gone', (_e, d) => {
    if (!loaded && d.serviceName === 'network.mojom.NetworkService') retry('network service crashed');
  });
  run();
  contents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://')) shell.openExternal(url);
    return { action: 'deny' };
  });
  return shown;
}

/** Sends to the page of every window of the program (the main window and the ones `framedWindow` opened), for events that more than
 *  one window shows (an update downloading). */
export function sendToWindows(channel: string, ...args: unknown[]): void {
  for (const id of chromed.keys()) {
    const contents = webContents.fromId(id);
    if (contents && !contents.isDestroyed()) contents.send(channel, ...args);
  }
}

/** The chrome's channels: what a page draws (win:chrome) and the bar's buttons, each acting on the window of the page that sent it. */
export function registerWindowControls(): void {
  const of = (e: { sender: WebContents }) => chromed.get(e.sender.id)?.window;
  ipcMain.on('win:chrome', (e) => {
    const entry = chromed.get(e.sender.id);
    const bar = entry?.bar ?? null;
    e.returnValue = (
      entry
        ? {
            bar: bar && {
              host: bar.host ?? 'page',
              title: bar.title,
              icon: iconUrl(bar.icon),
              accent: bar.accent,
              background: bar.background,
              height: BAR_HEIGHT,
              actions: (bar.actions ?? []).map(({ label, icon }) => ({ label, icon })),
              maximized: entry.window.isMaximized(),
              focused: entry.window.isFocused(),
            },
          }
        : null
    ) satisfies ChromeInfo | null;
  });
  ipcMain.on('win:minimize', (e) => of(e)?.minimize());
  ipcMain.on('win:toggleMaximize', (e) => {
    const win = of(e);
    if (win) win[win.isMaximized() ? 'unmaximize' : 'maximize']();
  });
  ipcMain.on('win:close', (e) => of(e)?.close());
  ipcMain.on('win:action', (e, i: number) => chromed.get(e.sender.id)?.bar?.actions?.[i]?.run());
}
