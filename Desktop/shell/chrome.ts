// The window chrome's renderer side (preload): the title bar, the shell's own dialog and the leave guard. It draws into the document
// from the preload's isolated world, so the page's scripts need nothing; class names start with `ftb-` / `fsd-` and the bar's rules
// are scoped to #forge-titlebar, so a page's own CSS and the shell's leave each other alone.
import { ipcRenderer, type IpcRendererEvent } from 'electron';
import { TITLEBAR_READY, TITLEBAR_SLOT } from './titlebar-ids';

/** What the main process says about this page (window.ts `win:chrome`); null: a window without the shell's chrome (a hidden worker). */
export type ChromeInfo = {
  bar: null | {
    host: 'page' | 'view';
    title: string;
    icon: string;
    accent: string;
    background: string;
    height: number;
    actions: { label: string; icon: string }[];
    maximized: boolean;
    focused: boolean;
  };
};

// The page's language (the bar's own view has none): both programs are Chinese first, English when the page says so.
const english = () => /^en/i.test(document.documentElement?.lang ?? '');
const words = () =>
  !english()
    ? {
        minimize: '最小化',
        maximize: '最大化',
        restore: '还原',
        close: '关闭',
        stay: '留下',
        leave: '离开',
        closeTitle: '关闭窗口？',
        leaveTitle: '离开这一页？',
        leaveBody: '页面上还有没做完的事，现在离开会把它中断。',
      }
    : {
        minimize: 'Minimize',
        maximize: 'Maximize',
        restore: 'Restore',
        close: 'Close',
        stay: 'Stay',
        leave: 'Leave',
        closeTitle: 'Close the window?',
        leaveTitle: 'Leave this page?',
        leaveBody: 'Something on this page is still running; leaving now stops it.',
      };

// Windows caption glyphs, drawn on a 10 x 10 grid.
const glyph = {
  minimize: 'M0 5.5h10',
  maximize: 'M.5 .5h9v9h-9z',
  restore: 'M2.5 2.5h7v7h-7zM2.5 2.5V.5h7v7h-2',
  close: 'M.5 .5l9 9M9.5 .5l-9 9',
};

const svg = (d: string, box: number, size: number) => `<svg width="${size}" height="${size}" viewBox="0 0 ${box} ${box}" aria-hidden="true"><path d="${d}"/></svg>`;

function css(bar: NonNullable<ChromeInfo['bar']>): string {
  const page = bar.host === 'page';
  return `
#forge-titlebar{--ftb-accent:${bar.accent};position:${page ? 'fixed' : 'relative'};top:0;left:0;right:0;z-index:2147483600;height:${bar.height}px;display:flex;align-items:stretch;box-sizing:border-box;background:${bar.background};color:#e6ecee;border-bottom:1px solid rgb(255 255 255 / .07);font:600 12.5px/1 'Segoe UI Variable Text','Segoe UI','Microsoft YaHei UI',sans-serif;user-select:none;-webkit-app-region:drag}
#forge-titlebar::before{content:'';position:absolute;inset:0 auto auto 0;width:min(420px,45%);height:2px;background:linear-gradient(90deg,var(--ftb-accent),transparent);transition:opacity .15s}
#forge-titlebar .ftb-brand{display:flex;align-items:center;gap:8px;padding:0 12px;min-width:0;white-space:nowrap;transition:opacity .15s}
#forge-titlebar .ftb-brand img{width:18px;height:18px}
#forge-titlebar .ftb-brand span{overflow:hidden;text-overflow:ellipsis;letter-spacing:.02em}
#forge-titlebar .ftb-drag{flex:1;min-width:24px}
#forge-titlebar .ftb-slot{display:flex;align-items:center;gap:8px;padding-right:6px;-webkit-app-region:no-drag}
#forge-titlebar .ftb-slot:empty{display:none}
#forge-titlebar .ftb-buttons{display:flex;-webkit-app-region:no-drag}
#forge-titlebar button.ftb-btn{all:unset;box-sizing:border-box;width:46px;height:100%;display:grid;place-items:center;color:#a7b5bb;cursor:default;transition:background-color 80ms,color 80ms,opacity .15s}
#forge-titlebar button.ftb-btn svg{fill:none;stroke:currentColor;stroke-width:1;shape-rendering:crispEdges}
#forge-titlebar button.ftb-btn:hover{background:rgb(255 255 255 / .09);color:#fff}
#forge-titlebar button.ftb-btn:active{background:rgb(255 255 255 / .15)}
#forge-titlebar button.ftb-btn:focus-visible{outline:1px solid var(--ftb-accent);outline-offset:-3px}
#forge-titlebar button.ftb-action{width:40px}
#forge-titlebar button.ftb-action svg{stroke-width:1.7;shape-rendering:auto}
#forge-titlebar button.ftb-action:hover{color:var(--ftb-accent)}
#forge-titlebar button.ftb-close:hover{background:#c42b1c;color:#fff}
#forge-titlebar button.ftb-close:active{background:#a52517}
#forge-titlebar[data-blurred]::before{opacity:.25}
#forge-titlebar[data-blurred] .ftb-brand,#forge-titlebar[data-blurred] button.ftb-btn:not(:hover){opacity:.5}
${
  page
    ? `html{box-sizing:border-box;height:100%;padding-top:${bar.height}px!important;overflow:hidden!important}
body{box-sizing:border-box;height:100%;margin-top:0;overflow:auto}`
    : `html,body{margin:0;height:100%;overflow:hidden;background:${bar.background}}`
}`;
}

function button(className: string, label: string, icon: string, onClick: () => void): HTMLButtonElement {
  const b = document.createElement('button');
  b.type = 'button';
  b.className = `ftb-btn ${className}`;
  b.title = label;
  b.setAttribute('aria-label', label);
  b.innerHTML = icon;
  b.addEventListener('click', onClick);
  return b;
}

function mountBar(bar: NonNullable<ChromeInfo['bar']>): void {
  const text = words();
  const style = document.createElement('style');
  style.textContent = css(bar);
  document.head.append(style);
  const root = document.createElement('div');
  root.id = 'forge-titlebar';
  const brand = document.createElement('div');
  brand.className = 'ftb-brand';
  const icon = document.createElement('img');
  icon.src = bar.icon;
  icon.alt = '';
  const title = document.createElement('span');
  title.textContent = bar.title;
  brand.append(icon, title);
  const drag = document.createElement('div');
  drag.className = 'ftb-drag';
  const slot = document.createElement('div');
  slot.className = 'ftb-slot';
  slot.id = TITLEBAR_SLOT;
  const buttons = document.createElement('div');
  buttons.className = 'ftb-buttons';
  bar.actions.forEach((a, i) => buttons.append(button('ftb-action', a.label, svg(a.icon, 24, 18), () => ipcRenderer.send('win:action', i))));
  const size = button('ftb-size', '', '', () => ipcRenderer.send('win:toggleMaximize'));
  buttons.append(
    button('ftb-min', text.minimize, svg(glyph.minimize, 10, 10), () => ipcRenderer.send('win:minimize')),
    size,
    button('ftb-close', text.close, svg(glyph.close, 10, 10), () => ipcRenderer.send('win:close')),
  );
  root.append(brand, drag, slot, buttons);
  const show = ({ maximized, focused }: { maximized: boolean; focused: boolean }) => {
    const label = maximized ? text.restore : text.maximize;
    size.title = label;
    size.setAttribute('aria-label', label);
    size.innerHTML = svg(maximized ? glyph.restore : glyph.maximize, 10, 10);
    root.toggleAttribute('data-maximized', maximized);
    root.toggleAttribute('data-blurred', !focused);
  };
  show(bar);
  ipcRenderer.on('win:state', (_e: IpcRendererEvent, state: { maximized: boolean; focused: boolean }) => show(state));
  document.body.prepend(root);
  window.dispatchEvent(new Event(TITLEBAR_READY));
}

type DialogButton = { label: string; kind?: 'primary' | 'danger' };

/** The shell's own dialog, drawn over the page: resolves with the index of the button pressed; Escape and a click outside take `cancel`. */
function showDialog(title: string, message: string, buttons: DialogButton[], cancel: number): Promise<number> {
  const style = document.createElement('style');
  style.textContent = `
.fsd-backdrop{position:fixed;inset:0;z-index:2147483646;display:grid;place-items:center;background:rgb(2 4 5 / .62);font:14px/1.5 'Segoe UI Variable Text','Segoe UI','Microsoft YaHei UI',sans-serif}
.fsd{box-sizing:border-box;width:min(420px,calc(100vw - 32px));padding:20px 22px 18px;border-radius:10px;background:#151b1f;border:1px solid #2a353b;border-top:2px solid #f2a93b;color:#a7b5bb;box-shadow:0 18px 48px rgb(0 0 0 / .55)}
.fsd h2{margin:0 0 8px;font-size:16px;font-weight:600;color:#e6ecee}
.fsd p{margin:0 0 18px;white-space:pre-line}
.fsd-buttons{display:flex;justify-content:flex-end;gap:8px}
.fsd-buttons button{all:unset;box-sizing:border-box;padding:7px 16px;border-radius:6px;background:#222b31;border:1px solid #33414a;color:#e6ecee;cursor:pointer;font-weight:600}
.fsd-buttons button:hover{background:#2a353b}
.fsd-buttons button:focus-visible{outline:2px solid #f2a93b;outline-offset:2px}
.fsd-buttons button.primary{background:#f2a93b;border-color:#f2a93b;color:#0c1013}
.fsd-buttons button.primary:hover{background:#ffba52}
.fsd-buttons button.danger{background:#c9473d;border-color:#c9473d;color:#fff}
.fsd-buttons button.danger:hover{background:#dd574c}`;
  const backdrop = document.createElement('div');
  backdrop.className = 'fsd-backdrop';
  const box = document.createElement('div');
  box.className = 'fsd';
  box.setAttribute('role', 'alertdialog');
  box.setAttribute('aria-modal', 'true');
  const h = document.createElement('h2');
  h.textContent = title;
  const p = document.createElement('p');
  p.textContent = message;
  const row = document.createElement('div');
  row.className = 'fsd-buttons';
  box.append(h, p, row);
  backdrop.append(style, box);
  return new Promise((done) => {
    const finish = (i: number) => {
      backdrop.remove();
      removeEventListener('keydown', onKey, true);
      done(i);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape') return;
      e.stopPropagation();
      finish(cancel);
    };
    const elements = buttons.map((b, i) => {
      const el = document.createElement('button');
      el.type = 'button';
      el.textContent = b.label;
      if (b.kind) el.className = b.kind;
      el.addEventListener('click', () => finish(i));
      return el;
    });
    row.append(...elements);
    backdrop.addEventListener('mousedown', (e) => e.target === backdrop && finish(cancel));
    addEventListener('keydown', onKey, true);
    document.body.append(backdrop);
    elements[cancel]?.focus();
  });
}

/**
 * A page that asks to stay (beforeunload) gets the shell's dialog instead of nothing: Electron shows none and the close, reload or
 * link is just dropped. Leaving lets the next unload through and repeats it: the close, else the last link followed, else a reload.
 */
function guardLeaving(): void {
  let leaving = false;
  let link: { href: string; at: number } | null = null;
  // Listeners on the target run capture first, so this one stops the page's own before it can ask again.
  addEventListener('beforeunload', (e) => leaving && e.stopImmediatePropagation(), true);
  addEventListener(
    'click',
    (e) => {
      const a = (e.target as Element | null)?.closest?.('a[href]') as HTMLAnchorElement | null;
      if (a) link = { href: a.href, at: Date.now() };
    },
    true,
  );
  ipcRenderer.on('win:leave', async (_e: IpcRendererEvent, closing: boolean) => {
    const text = words();
    const choice = await showDialog(closing ? text.closeTitle : text.leaveTitle, text.leaveBody, [{ label: text.stay }, { label: text.leave, kind: 'danger' }], 0);
    if (choice !== 1) return;
    leaving = true;
    if (closing) ipcRenderer.send('win:close');
    else if (link && Date.now() - link.at < 2000) location.href = link.href;
    else location.reload();
  });
}

/**
 * The shell's chrome for this page, as the main process set it up: the title bar (drawn here for host 'page', or as the whole page of
 * the bar's own view for host 'view') and the leave guard. Call once from the app's preload.
 */
export function mountWindowChrome(): void {
  const info = ipcRenderer.sendSync('win:chrome') as ChromeInfo | null;
  if (!info) return;
  if (info.bar?.host !== 'view') guardLeaving();
  const bar = info.bar;
  if (!bar) return;
  if (document.body) mountBar(bar);
  else addEventListener('DOMContentLoaded', () => mountBar(bar), { once: true });
}
