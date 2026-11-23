import { app } from 'electron';
import type { AppLog } from './appLog';

export interface AiControlOptions {
  /** Environment variable that does the same as the flag, for launches that cannot pass arguments (a port, or 1 for the default). */
  envVar: string;
  defaultPort: number;
  /** An app setting that opens the port at the default port as well (read once at start, so a change takes effect at the next start);
   *  given the data folder. The flag and the environment variable still win and may name another port. */
  setting?: (dataDir: string) => boolean;
}

/**
 * `--ai-control[=<port>]` opens the Chromium DevTools protocol on 127.0.0.1 so an AI agent can drive the window; without it no
 * debugging port is open. It keeps a covered or minimized window live (Chromium would stop its animation frames). Has to run
 * before the app is ready. Returns the port that was opened, null when none.
 */
export function enableAiControl({ envVar, defaultPort, setting }: AiControlOptions, log: AppLog, dataDir: string): number | null {
  const fromEnv = process.env[envVar];
  const flag =
    process.argv.find((a) => a === '--ai-control' || a.startsWith('--ai-control=')) ??
    (fromEnv ? `--ai-control=${fromEnv === '1' ? defaultPort : fromEnv}` : setting?.(dataDir) ? '--ai-control' : undefined);
  if (!flag) return null;
  const port = flag === '--ai-control' ? defaultPort : Number(flag.slice('--ai-control='.length));
  if (!Number.isInteger(port) || port < 1024 || port > 65535) {
    log('ai-control', `ignored ${flag}: the port must be an integer from 1024 to 65535`);
    return null;
  }
  app.commandLine.appendSwitch('remote-debugging-address', '127.0.0.1');
  app.commandLine.appendSwitch('remote-debugging-port', String(port));
  app.commandLine.appendSwitch('disable-features', 'CalculateNativeWinOcclusion');
  app.commandLine.appendSwitch('disable-renderer-backgrounding');
  return port;
}
