// Locates the Shadowverse installation for the build scripts.
//
// Nothing here hardcodes a drive letter or a user name: the game root is looked
// up in this order, and the first folder that actually contains
// `Shadowverse_Data/Managed/Assembly-CSharp.dll` wins.
//
//   1. SHADOWVERSE_DIR (environment variable)
//   2. the usual Steam library locations next to the repository and under
//      %ProgramFiles(x86)%\Steam
//   3. a shallow scan of the folders around the repository
//      (<repo>/../*/Shadowverse, <repo>/../*/*/Shadowverse, and the same two
//      levels above that), which covers "the game sits next to the checkout"
//
// Callers that can be given explicit CSV paths do not need this at all; they
// should only resolve the game root when they actually fall back to a default.

import { existsSync, readdirSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
export const webEditorRoot = resolve(scriptDirectory, "..", "..");
export const repositoryRoot = resolve(webEditorRoot, "..");

const MARKER = ["Shadowverse_Data", "Managed", "Assembly-CSharp.dll"];

/** True when the folder looks like a Shadowverse install. */
export function isGameRoot(candidate) {
  return Boolean(candidate) && existsSync(resolve(candidate, ...MARKER));
}

function* nearbyCandidates() {
  for (const base of [resolve(repositoryRoot, ".."), resolve(repositoryRoot, "..", "..")]) {
    let entries;
    try {
      entries = readdirSync(base, { withFileTypes: true });
    } catch {
      continue;
    }

    for (const entry of entries) {
      if (!entry.isDirectory()) {
        continue;
      }

      const first = resolve(base, entry.name, "Shadowverse");
      yield first;

      let inner;
      try {
        inner = readdirSync(resolve(base, entry.name), { withFileTypes: true });
      } catch {
        continue;
      }

      for (const child of inner) {
        if (child.isDirectory()) {
          yield resolve(base, entry.name, child.name, "Shadowverse");
        }
      }
    }
  }
}

/**
 * Returns { root, source, tried }. `root` is null when nothing was found;
 * `tried` lists every candidate so the caller can print a useful error.
 */
export function resolveGameRoot() {
  const tried = [];
  const consider = (candidate, source) => {
    if (!candidate) {
      return null;
    }

    tried.push({ candidate, source });
    return isGameRoot(candidate) ? { root: candidate, source } : null;
  };

  const environment = consider(process.env.SHADOWVERSE_DIR, "SHADOWVERSE_DIR");
  if (environment) {
    return { ...environment, tried };
  }

  const steam = [
    resolve(repositoryRoot, "..", "..", "SteamLibrary", "steamapps", "common", "Shadowverse"),
    resolve(webEditorRoot, "..", "..", "..", "..", "SteamLibrary", "steamapps", "common", "Shadowverse"),
    process.env["ProgramFiles(x86)"]
      ? resolve(process.env["ProgramFiles(x86)"], "Steam", "steamapps", "common", "Shadowverse")
      : null,
  ];
  for (const candidate of steam) {
    const found = consider(candidate, "Steam library");
    if (found) {
      return { ...found, tried };
    }
  }

  for (const candidate of nearbyCandidates()) {
    const found = consider(candidate, "a folder around the repository");
    if (found) {
      return { ...found, tried };
    }
  }

  return { root: null, source: null, tried };
}

/** Message shown when the game folder could not be found. */
export function gameRootNotFoundMessage(tried) {
  const lines = [
    "找不到 Shadowverse 游戏目录。",
    "Set SHADOWVERSE_DIR to the game folder, or pass the CSV path as an argument.",
    "已尝试：",
    ...tried.map((item) => `  - [${item.source}] ${item.candidate}`),
  ];
  return lines.join("\n");
}
