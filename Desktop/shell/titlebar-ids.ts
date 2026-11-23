// The title bar's names a page may use (no Electron import: a page's own bundle can take this file).

/** The bar's slot: a page that draws the bar itself (host 'page') may put its own controls in it. */
export const TITLEBAR_SLOT = 'forge-titlebar-slot';
/** Sent on window once the bar is in the document (the slot exists from then on). */
export const TITLEBAR_READY = 'forge-titlebar-ready';
