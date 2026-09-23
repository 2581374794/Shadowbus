// @vitest-environment node
import { describe, expect, it } from "vitest";
import { unzipSync } from "fflate";
import { ImportedWorkspaceAdapter } from "../src/workspace/workspace";
import { scanWorkspace } from "../src/workspace/scanner";

const decode = (value: Uint8Array) => new TextDecoder().decode(value);
const empty = new Uint8Array();

describe("导入工作区", () => {
  it("编辑文本时保持无关二进制文件逐字节不变", async () => {
    const binary = new Uint8Array([0, 255, 1, 128, 42]);
    const workspace = new ImportedWorkspaceAdapter("Mods", [
      { path: "BossRush/demo/bossrush.json", data: new TextEncoder().encode('{"id":"demo"}'), modified: false },
      { path: "Native/plugin.dll", data: binary, modified: false },
    ]);
    await workspace.writeText("BossRush/demo/bossrush.json", '{"id":"updated"}');
    const zip = unzipSync(new Uint8Array(await (await workspace.exportZip()).arrayBuffer()));
    expect([...zip["Native/plugin.dll"]]).toEqual([...binary]);
    expect(decode(zip["BossRush/demo/bossrush.json"])).toBe('{"id":"updated"}');
  });

  it("按目录重命名和删除完整 BossRush 包", async () => {
    const workspace = new ImportedWorkspaceAdapter("Mods", [
      { path: "BossRush/old/bossrush.json", data: new Uint8Array([1]), modified: false },
      { path: "BossRush/old/ai/deck/a.csv", data: new Uint8Array([2]), modified: false },
      { path: "BossRush/other/bossrush.json", data: new Uint8Array([3]), modified: false },
    ]);
    await workspace.renameTree("BossRush/old", "BossRush/new");
    expect(await workspace.listFiles()).toContain("BossRush/new/ai/deck/a.csv");
    expect(await workspace.listFiles()).not.toContain("BossRush/old/bossrush.json");
    await workspace.deleteTree("BossRush/new");
    expect(await workspace.listFiles()).toEqual(["BossRush/other/bossrush.json"]);
  });

  it("复制完整 BossRush 包且不改动来源", async () => {
    const workspace = new ImportedWorkspaceAdapter("Mods", [
      { path: "BossRush/old/bossrush.json", data: new Uint8Array([1]), modified: false },
      { path: "BossRush/old/ai/style/a.csv", data: new Uint8Array([2]), modified: false },
    ]);
    await workspace.copyTree("BossRush/old", "BossRush/copy");
    expect(await workspace.listFiles()).toEqual([
      "BossRush/copy/ai/style/a.csv",
      "BossRush/copy/bossrush.json",
      "BossRush/old/ai/style/a.csv",
      "BossRush/old/bossrush.json",
    ]);
  });
});

describe("扫描 CardMaster 文件", () => {
  const scan = (paths: string[]) => scanWorkspace(new ImportedWorkspaceAdapter("Mods", paths.map((path) => ({ path, data: empty, modified: false }))));

  it("同时收录卡文件夹里的 json 和根目录的散装 json", async () => {
    const files = await scan([
      // 2.5.5 起一张 mod 卡有自己的一层文件夹，夹里可以有多个 json
      "CardMaster/我的卡/我的卡.json",
      "CardMaster/我的卡/闪卡.json",
      // 旧写法：json 直接放在 CardMaster 根目录
      "CardMaster/散装.json",
      "CardMaster/Reference/card_names.csv",
      "CardMaster/Reference/extra.json",
      // 卡文件夹只允许一层，再深就不是扫描范围
      "CardMaster/我的卡/子目录/更深.json",
      "Format/standard.json",
    ]);
    expect([...files.cardmaster].sort()).toEqual([
      "CardMaster/散装.json",
      "CardMaster/我的卡/我的卡.json",
      "CardMaster/我的卡/闪卡.json",
    ].sort());
    expect(files.format).toEqual(["Format/standard.json"]);
  });
});
