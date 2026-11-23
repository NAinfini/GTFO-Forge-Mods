import { app } from 'electron';
import { appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync, renameSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

export type AppLog = (kind: string, detail: unknown) => void;

/** Crash and error evidence: one line per event in <dataDir>/logs/app.log, written synchronously so it survives the crash. */
export function createAppLog(dataDir: () => string): AppLog {
  return (kind, detail) => {
    const text = detail instanceof Error ? `${detail.message}\n${detail.stack}` : typeof detail === 'string' ? detail : JSON.stringify(detail);
    try {
      const dir = join(dataDir(), 'logs');
      mkdirSync(dir, { recursive: true });
      // Past 2 MB the log moves to app.1.log (the older ones to .2 and .3, the oldest dropped).
      const file = join(dir, 'app.log');
      if ((statSync(file, { throwIfNoEntry: false })?.size ?? 0) > 2 << 20)
        for (let i = 3; i >= 1; i--) {
          const from = i === 1 ? file : join(dir, `app.${i - 1}.log`);
          if (existsSync(from)) renameSync(from, join(dir, `app.${i}.log`));
        }
      appendFileSync(file, `${new Date().toISOString()} [${kind}] ${text}\n`);
    } catch {
      // nowhere left to report it
    }
  };
}

/** The last `count` lines of <dataDir>/logs/app.log (a problem report carries them), read only; a log that was never written is empty. */
export function appLogTail(dataDir: string, count: number): { file: string; lines: string[] } {
  const file = join(dataDir, 'logs', 'app.log');
  if (!existsSync(file)) return { file, lines: [] };
  return { file, lines: readFileSync(file, 'utf8').split('\n').filter(Boolean).slice(-count) };
}

/** Main-process faults go to the log. */
export function logProcessFaults(log: AppLog): void {
  process.on('uncaughtException', (e) => log('main uncaughtException', e));
  process.on('unhandledRejection', (e) => log('main unhandledRejection', e));
  app.on('child-process-gone', (_e, d) => log('child-process-gone', d));
}

/**
 * Chromium's HTTP disk cache only ever holds what comes over http (a development run's modules); the app's own files are
 * never cached. Kept small. V8's code cache keeps the compiled scripts of every app version (and development build): it is
 * deleted when the version changes or it passes `codeCacheMB`, before Chromium opens it (the session starts when the app is ready).
 */
export function limitCaches(log: AppLog, diskCacheMB = 32, codeCacheMB = 128): void {
  app.commandLine.appendSwitch('disk-cache-size', String(diskCacheMB << 20));
  try {
    const mark = join(app.getPath('userData'), 'code-cache-version');
    const dir = join(app.getPath('userData'), 'Code Cache');
    const files = existsSync(dir) ? readdirSync(dir, { recursive: true, withFileTypes: true }).filter((e) => e.isFile()) : [];
    const size = files.reduce((sum, e) => sum + statSync(join(e.parentPath, e.name)).size, 0);
    if (existsSync(mark) && readFileSync(mark, 'utf8') === app.getVersion() && size <= codeCacheMB << 20) return;
    rmSync(dir, { recursive: true, force: true });
    writeFileSync(mark, app.getVersion());
  } catch (e) {
    log('code cache', e);
  }
}
