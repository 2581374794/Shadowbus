import type { CardImageFiles, CardVoiceFiles, JsonRecord } from "../types";

/**
 * The three optional CardMaster patch fields that point at files outside the json:
 * `extraVoiceIds`, `imageFiles` and `voiceFiles`.
 *
 * Since 2.5.5 a mod card owns a folder (`Mods/CardMaster/<folder>/`) and every file
 * is looked up inside it, so an image or voice value is a path relative to that
 * folder; `图/card.png` is fine, an absolute path or one that walks out of the
 * folder is refused by `ModCardAssets.TryResolveRelativePath`. A card whose json
 * sits directly in `CardMaster/` has no folder of its own and cannot use local
 * files at all.
 *
 * All three are omitted rather than written empty: a patch that declares nothing
 * must not gain `"imageFiles": {}` in its json, which is also how the editor treats
 * `foilEffectCardId`.
 */

/** The six point-in-time voice slots of `CardVoiceFilePatch`, in its own order. */
export const voiceFileKeys = ["play", "evolve", "attack", "evolvedAttack", "destroy", "evolvedDestroy"] as const;

/** The two per-skill-slot voice lists; each entry is one skill slot, in field order. */
export const voiceFileListKeys = ["skills", "evolvedSkills"] as const;

/** The two image slots of `CardImageFilePatch`. */
export const imageFileKeys = ["normal", "evolved"] as const;

const object = (value: unknown): Record<string, unknown> =>
  value && typeof value === "object" && !Array.isArray(value) ? value as Record<string, unknown> : {};
const trimmed = (value: unknown) => typeof value === "string" ? value.trim() : "";

/**
 * Voice ids of one `extraVoiceIds` array. Blank entries are dropped, repeats collapse
 * to their first occurrence, and a number is read as a string (`125641030` reads as
 * `"125641030"`), because the game only ever uses the text form.
 */
export function voiceIdList(value: unknown): string[] {
  return Array.isArray(value) ? [...new Set(value.map((item) => String(item).trim()).filter(Boolean))] : [];
}

/** Splits a bulk paste into voice ids; whitespace and commas both separate entries. */
export function parseVoiceIds(text: string): string[] {
  return voiceIdList(text.split(/[\s,;，；]+/));
}

/** The value to store for `extraVoiceIds`: an empty list is left out of the json. */
export function extraVoiceIdsValue(value: unknown): string[] | undefined {
  const ids = voiceIdList(value);
  return ids.length ? ids : undefined;
}

/** Paths of a voice list: blanks dropped, order kept, because every entry is one skill slot. */
const pathList = (value: unknown): string[] =>
  Array.isArray(value) ? value.map((item) => String(item).trim()).filter(Boolean) : [];

/**
 * Keys this editor does not know are carried through verbatim. They belong to a
 * newer plugin, and dropping them on save is what the "未知字段原样保留" rule forbids.
 */
function copyUnknown(source: Record<string, unknown>, target: JsonRecord, known: readonly string[]) {
  for (const [key, value] of Object.entries(source)) if (!known.includes(key) && value != null) target[key] = value;
}

/** Reads `imageFiles`: known slots trimmed and dropped when empty, unknown keys kept. */
export function normalizeImageFiles(value: unknown): CardImageFiles | undefined {
  const source = object(value);
  const files: CardImageFiles = {};
  for (const key of imageFileKeys) {
    const text = trimmed(source[key]);
    if (text) files[key] = text;
  }
  copyUnknown(source, files, imageFileKeys);
  return Object.keys(files).length ? files : undefined;
}

/** Reads `voiceFiles`: known slots trimmed and dropped when empty, unknown keys kept. */
export function normalizeVoiceFiles(value: unknown): CardVoiceFiles | undefined {
  const source = object(value);
  const files: CardVoiceFiles = {};
  for (const key of voiceFileKeys) {
    const text = trimmed(source[key]);
    if (text) files[key] = text;
  }
  for (const key of voiceFileListKeys) {
    const items = pathList(source[key]);
    if (items.length) files[key] = items;
  }
  copyUnknown(source, files, [...voiceFileKeys, ...voiceFileListKeys]);
  return Object.keys(files).length ? files : undefined;
}

/**
 * Why the plugin would refuse this path, or null when it accepts it. Mirrors
 * `ModCardAssets.TryResolveRelativePath`: rooted paths and paths that leave the
 * card folder are both rejected there, so a config that uses one silently loads
 * no artwork or audio at all.
 */
export function relativePathProblem(path: string): string | null {
  if (/^[\\/]/.test(path) || /^[a-zA-Z]:/.test(path)) return "必须是相对卡文件夹的路径，不能使用绝对路径。";
  let depth = 0;
  for (const segment of path.split(/[\\/]+/)) {
    if (!segment || segment === ".") continue;
    if (segment !== "..") { depth++; continue; }
    if (--depth < 0) return "路径不能跳出卡文件夹（不能用 .. 回到上一层）。";
  }
  return null;
}
