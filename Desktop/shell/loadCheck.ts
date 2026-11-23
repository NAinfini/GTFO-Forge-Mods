// What the game said about our packages the last time it started from a profile, read back from BepInEx's own log (only read, never
// written): BepInEx's verdicts on the program's packages (backend plugin.loadCheck) and, when asked, the Forge lines of the log that
// refuse, skip or fail (backend plugin.gameLog). Each finding is handed back once per run of the program; the ForgeSkin editor shows it
// after a start or an install, the map workshop after an export into a profile or a hot reload.
import type { AppLog } from './appLog';

/** A BepInEx verdict on one of the program's packages (Backend InstallNote): `code` and `args` make the short message, `detail` is the
 *  full account for the log. */
export interface LoadNote {
  code: string;
  args: Record<string, string>;
  detail: string;
}
/** One kept line of BepInEx/LogOutput.log: its level, its source (the plugin's name) and its text (with the lines that continue it). */
export interface GameLogLine {
  level: string;
  source: string;
  text: string;
}
/** plugin.gameLog: every Fatal, Error and Warning line and every line of a Forge source; `stale` when the log predates the plugins. */
export interface GameLog {
  log: string;
  exists: boolean;
  written: string | null;
  changed: string | null;
  stale: boolean;
  lines: GameLogLine[];
}
export interface LoadCheck {
  /** BepInEx refused or skipped one of the program's packages. */
  notes: LoadNote[];
  /** Forge lines that failed (Error, Fatal) or refused or skipped something (Warning). */
  lines: GameLogLine[];
}

type Call = (method: string, params?: unknown) => Promise<unknown>;

/** A Forge line the author is told about: an error, or a warning that says something was refused or skipped. */
export const forgeFinding = (line: GameLogLine) =>
  /forge/i.test(line.source) && (line.level === 'Error' || line.level === 'Fatal' || (line.level === 'Warning' && /\b(refus|reject|skip)/i.test(line.text)));

export function createLoadCheck(call: Call, log: AppLog) {
  // What was handed back already, so a check repeated on focus, a profile change, a game start or an export says each once per run.
  const told = new Set<string>();
  const once = (kind: string, line: string) => {
    if (told.has(kind + line)) return;
    told.add(kind + line);
    log(kind, line);
  };
  return async (profile: string, { lines = false }: { lines?: boolean } = {}): Promise<LoadCheck> => {
    const out: LoadCheck = { notes: [], lines: [] };
    const verdicts = (await call('plugin.loadCheck', { profile }).catch((e: unknown) => (log('plugin.loadCheck', e), null))) as { notes?: LoadNote[] } | null;
    const fresh = (verdicts?.notes ?? []).filter((n) => !told.has(n.detail));
    // A package skipped only because another one did not load is left to that one's message, which names the cause.
    const causes = fresh.filter((n) => n.code !== 'load-dependency-failed');
    for (const note of fresh) {
      told.add(note.detail);
      if (causes.length && !causes.includes(note)) log(`install ${note.code}`, note.detail);
      else out.notes.push(note);
    }
    if (!lines) return out;
    const game = (await call('plugin.gameLog', { profile }).catch((e: unknown) => (log('plugin.gameLog', e), null))) as GameLog | null;
    if (!game) return out;
    if (!game.exists) {
      once('game log', `${game.log} does not exist (the game has not started from ${profile}); its Forge lines are read after the next start`);
      return out;
    }
    if (game.stale) {
      once(
        'game log',
        `${game.log} (written ${game.written}) is older than the last change under BepInEx/plugins (${game.changed}): it says nothing about the plugins there now, so its Forge lines were skipped; read again after the next game start`,
      );
      return out;
    }
    for (const line of game.lines.filter(forgeFinding)) {
      const key = `${line.level}|${line.source}|${line.text}`;
      if (told.has(key)) continue;
      told.add(key);
      out.lines.push(line);
    }
    return out;
  };
}
