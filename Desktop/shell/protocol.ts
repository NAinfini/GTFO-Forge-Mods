import { net, protocol } from 'electron';
import { isAbsolute, relative, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export interface LocalProtocolOptions {
  /** The scheme name: `<scheme>://local/<path>`. */
  scheme: string;
  /** The only paths served (tested against the full path). */
  allow: RegExp;
  /** With a root, the URL path is relative to it and must stay inside; without, it is an absolute path. */
  root?: () => string;
}

/** Registers the scheme as privileged; has to run before the app is ready. Returns the handler to install once it is. */
export function registerLocalProtocol({ scheme, allow, root }: LocalProtocolOptions): () => void {
  protocol.registerSchemesAsPrivileged([{ scheme, privileges: { standard: true, secure: true, supportFetchAPI: true, stream: true, corsEnabled: true } }]);
  // Local files are served to the renderer, never kept in the HTTP cache.
  return () =>
    protocol.handle(scheme, async (req) => {
      let path = decodeURIComponent(new URL(req.url).pathname.slice(1));
      if (root) {
        const base = resolve(root());
        path = resolve(base, path);
        if (relative(base, path).startsWith('..') || isAbsolute(relative(base, path))) return new Response(null, { status: 403 });
      }
      if (!isAbsolute(path) || !allow.test(path)) return new Response(null, { status: 403 });
      const r = await net.fetch(pathToFileURL(path).toString(), { headers: req.headers });
      const headers = new Headers(r.headers);
      headers.set('Cache-Control', 'no-store');
      return new Response(r.body, { status: r.status, statusText: r.statusText, headers });
    });
}
