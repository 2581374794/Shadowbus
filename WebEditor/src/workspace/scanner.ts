import type { WorkspaceAdapter } from "./workspace";

export type ModuleId = "bossrush" | "aidata" | "cardmaster" | "format" | "twopick" | "reference";

export interface ModuleFiles {
  bossrush: string[];
  aidata: string[];
  cardmaster: string[];
  format: string[];
  twopick: string[];
  reference: string[];
}

const emptyFiles = (): ModuleFiles => ({ bossrush: [], aidata: [], cardmaster: [], format: [], twopick: [], reference: [] });

export async function scanWorkspace(workspace: WorkspaceAdapter): Promise<ModuleFiles> {
  const result = emptyFiles();
  for (const rawPath of await workspace.listFiles()) {
    const path = rawPath.replaceAll("\\", "/");
    const lower = path.toLowerCase();
    if (/^bossrush\/[^/]+\/bossrush\.json$/.test(lower) && !lower.startsWith("bossrush/reference/")) result.bossrush.push(path);
    else if (/^aidata\/(deck|style|emote)\/[^/]+\.csv$/.test(lower) || /^bossrush\/[^/]+\/ai\/(deck|style|emote)\/[^/]+\.csv$/.test(lower)) result.aidata.push(path);
    else if (/^aidata\/ai_(basic|common|ally_common|deck|style|emote)\.json$/.test(lower)) result.reference.push(path);
    // Since 2.5.5 a mod card lives in its own folder: Mods/CardMaster/<folder>/<name>.json,
    // so a card folder may hold several json files (a normal and a hand-paired foil
    // record, for example). Loose json directly in CardMaster/ is still supported — it
    // is how the older single-file cards were written. CardMaster/Reference/ is the
    // exported lookup table, not a card folder, so it is skipped.
    else if (/^cardmaster\/(?:[^/]+\/)?[^/]+\.json$/.test(lower) && !lower.startsWith("cardmaster/reference/")) result.cardmaster.push(path);
    else if (/^format\/[^/]+\.json$/.test(lower)) result.format.push(path);
    else if (/^twopick\/[^/]+\.json$/.test(lower)) result.twopick.push(path);
    else if (/^bossrush\/reference\/.*\.(json|csv|txt)$/.test(lower)) result.reference.push(path);
  }
  return result;
}
