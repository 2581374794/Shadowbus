import { describe, expect, it } from "vitest";
import { addDeckTag, normalizeDeckCsv, parseCsv, serializeCsv, styleHeaders } from "../src/models/csv";
import { normalizeBossRush, normalizeCardMaster, normalizeTwoPick } from "../src/models/normalize";
import { validateBossRush, validateCardMaster, validateCsv, validateTwoPick } from "../src/models/validation";
import { newBossRush, newTwoPick } from "../src/models/defaults";
import { createCardCatalog } from "../src/data/cards";

describe("JSON 模型保真", () => {
  it("保留 BossRush 各层未知字段并补齐已知默认值", () => {
    const value = normalizeBossRush({
      id: "custom",
      future_root: { enabled: true },
      abilities: [{ ability_id: 100, future_ability: 7 }],
      bosses: [{ name: "测试 Boss", future_boss: [1, 2, 3] }],
    });
    expect(value.future_root).toEqual({ enabled: true });
    expect(value.abilities[0].future_ability).toBe(7);
    expect(value.bosses[0].future_boss).toEqual([1, 2, 3]);
    expect(value.bosses[0].enemy_life).toBe(20);
  });

  it("保留 CardMaster 与 TwoPick 的未知字段", () => {
    const card = normalizeCardMaster([{ templateCardId: 1, extension: "keep" }])[0];
    const twoPick = normalizeTwoPick({ id: "test", extension: { a: 1 } });
    expect(card.extension).toBe("keep");
    expect(twoPick.extension).toEqual({ a: 1 });
    const attackCard = normalizeCardMaster([{
      templateCardId: 1,
      attackEffectFields: {
        effectPath: ["normal_attack", "evo_attack"],
        se: ["se_normal", "se_evo"],
        moveType: ["DIRECT", "ARC"],
        effectEnginType: ["SHURIKEN", "SOLID"],
        time: [0.5, 0.75],
      },
    }])[0];
    expect(attackCard.attackEffectFields.effectPath).toEqual(["normal_attack", "evo_attack"]);
    expect(attackCard.attackEffectFields.time).toEqual([0.5, 0.75]);
    const tribeCard = normalizeCardMaster([{
      templateCardId: 1,
      intArrayFields: { Tribe: [2, "7", "invalid"] },
    }])[0];
    expect(tribeCard.intArrayFields.Tribe).toEqual([2, 7]);
    const foilCard = normalizeCardMaster([{
      templateCardId: 1,
      foilEffectCardId: "100011010",
    }])[0];
    expect(foilCard.foilEffectCardId).toBe(100011010);
    expect(normalizeCardMaster([{ templateCardId: 1, foilEffectCardId: 0 }])[0].foilEffectCardId).toBeUndefined();
  });

  it("保留借用语音与本地卡图/语音声明，并丢掉空值", () => {
    const card = normalizeCardMaster([{
      templateCardId: 1,
      // 数字也要按字符串读，空白与重复项都去掉
      extraVoiceIds: ["125641030_4", 125641030, " 125641030_4 ", "", " 125641031_2"],
      imageFiles: { normal: " 图/card.png ", evolved: "  " },
      voiceFiles: { play: "play.wav", evolve: "", attack: null, destroy: "音效/破坏.wav", skills: ["技能/1.wav", ""], evolvedSkills: [] },
    }])[0];
    expect(card.extraVoiceIds).toEqual(["125641030_4", "125641030", "125641031_2"]);
    expect(card.imageFiles).toEqual({ normal: "图/card.png" });
    expect(card.voiceFiles).toEqual({ play: "play.wav", destroy: "音效/破坏.wav", skills: ["技能/1.wav"] });

    const plain = normalizeCardMaster([{ templateCardId: 1 }])[0];
    expect(plain.extraVoiceIds).toBeUndefined();
    expect(plain.imageFiles).toBeUndefined();
    expect(plain.voiceFiles).toBeUndefined();
    const json = JSON.stringify(plain);
    for (const key of ["extraVoiceIds", "imageFiles", "voiceFiles"]) expect(json).not.toContain(key);
  });
});

describe("CSV 往返", () => {
  it("保留引号、换行和未知列", () => {
    const source = 'ID,Category,Priority,Type,Arg,Cond,Extra\r\n1,All,100,unitBonus,"POW ( 2 , 3 )","NOW_TURN >= 2","line 1\nline 2"\r\n';
    const parsed = parseCsv(source);
    expect(parsed.headers.at(-1)).toBe("Extra");
    expect(parsed.rows[0].Extra).toBe("line 1\nline 2");
    const roundTrip = parseCsv(serializeCsv(parsed));
    expect(roundTrip.rows).toEqual(parsed.rows);
  });

  it("Deck 标准化保留未知列并将 End 放到末尾", () => {
    const parsed = parseCsv("CardID,Extra,Tag1.Type,Tag1.Arg,Tag1.Condition,End\n1,x,a,b,c,\n");
    const normalized = normalizeDeckCsv(parsed);
    expect(normalized.headers.at(-1)).toBe("End");
    expect(normalized.headers).toContain("Extra");
    const expanded = addDeckTag(normalized);
    expect(expanded.headers).toContain("Tag2.Type");
  });

  it("使用游戏实际 Style 六列表头", () => {
    expect(styleHeaders).toEqual(["ID", "Category", "Priority", "Type", "Arg", "Cond"]);
    expect(validateCsv({ headers: styleHeaders, rows: [], newline: "\n" }, "style")).toHaveLength(0);
  });
});

describe("阻止无效配置", () => {
  it("检查 BossRush ID 与初始关卡索引", () => {
    const value = newBossRush("bad id");
    value.initial_progress = 3;
    expect(validateBossRush(value).filter((item) => item.severity === "error").length).toBeGreaterThanOrEqual(2);
  });

  it("检查 TwoPick 固定布局和轮次冲突", () => {
    const value = newTwoPick("test");
    value.offersPerRound = 3;
    value.roundRules = [{ rounds: [1], costs: null, rarities: null, cards: null }, { rounds: [1], costs: null, rarities: null, cards: null }];
    expect(validateTwoPick(value).filter((item) => item.severity === "error").length).toBeGreaterThanOrEqual(2);
  });

  it("拒绝将自制卡作为闪卡效果来源", () => {
    const value = normalizeCardMaster([{
      templateCardId: 100011010,
      foilEffectCardId: 999991001,
    }]);
    expect(validateCardMaster(value).some((item) => item.path === "[0].foilEffectCardId" && item.severity === "error")).toBe(true);
  });
});

describe("CardMaster 本地资源与闪卡自动派生", () => {
  it("接受相对卡文件夹的卡图与语音路径，且不要求填写 FoilCardId", () => {
    const value = normalizeCardMaster([{
      newCard: true,
      cardId: 999991000,
      templateCardId: 100011010,
      extraVoiceIds: ["125641030_4", "125641031"],
      imageFiles: { normal: "图/card.png", evolved: "图/card_evo.png" },
      voiceFiles: { play: "音效/登场.wav", skills: ["音效/技能 1.wav"] },
    }]);
    expect(value[0].intFields.FoilCardId).toBeUndefined();
    expect(validateCardMaster(value, undefined, "CardMaster/我的卡/我的卡.json")).toHaveLength(0);
  });

  it("拒绝绝对路径和跳出卡文件夹的路径", () => {
    const value = normalizeCardMaster([{
      templateCardId: 1,
      imageFiles: { normal: "C:/图/card.png" },
      voiceFiles: { destroy: "../别的卡/破坏.wav" },
    }]);
    const issues = validateCardMaster(value);
    expect(issues.map((item) => item.path)).toEqual(["[0].imageFiles.normal", "[0].voiceFiles.destroy"]);
    expect(issues.every((item) => item.severity === "error")).toBe(true);
  });

  it("提示借用语音 ID 里解析不出语音库的条目", () => {
    const value = normalizeCardMaster([{ templateCardId: 1, extraVoiceIds: ["125641030_4", "vo_goblin"] }]);
    const issues = validateCardMaster(value);
    expect(issues).toHaveLength(1);
    expect(issues[0]).toMatchObject({ severity: "warning", path: "[0].extraVoiceIds" });
    expect(issues[0].message).toContain("vo_goblin");
  });

  it("json 直接在 CardMaster 根目录时提示本地文件不会生效", () => {
    const value = normalizeCardMaster([{ templateCardId: 1, imageFiles: { normal: "card.png" } }]);
    const issues = validateCardMaster(value, undefined, "CardMaster/散装.json");
    expect(issues.some((item) => item.severity === "warning" && item.message.includes("根目录"))).toBe(true);
    // 卡文件夹里的 json 没有这条提示
    expect(validateCardMaster(value, undefined, "CardMaster/我的卡/我的卡.json")).toHaveLength(0);
  });

  it("newCard 且 cardId + 1 已被占用时提示闪卡会自动跳过", () => {
    const value = normalizeCardMaster([
      { newCard: true, cardId: 999991000, templateCardId: 100011010 },
      { newCard: true, cardId: 999991001, templateCardId: 100011010, boolFields: { IsFoil: true } },
    ]);
    const issues = validateCardMaster(value);
    expect(issues.some((item) => item.path === "[0].cardId" && item.message.includes("999991001") && item.message.includes("闪卡版会自动跳过"))).toBe(true);
    // 第二条显式写了 IsFoil，是手工配对的闪卡记录，不再提示自动派生
    expect(issues.some((item) => item.path === "[1].cardId")).toBe(false);
  });

  it("显式写 IsFoil 时视为手工配对", () => {
    const value = normalizeCardMaster([{ newCard: true, cardId: 999991000, templateCardId: 100011010, boolFields: { IsFoil: true } }]);
    expect(validateCardMaster(value).filter((item) => item.severity === "warning")).toHaveLength(0);
  });

  it("cardId + 1 命中内置卡表时也提示闪卡跳过", () => {
    const value = normalizeCardMaster([{ newCard: true, cardId: 100011009, templateCardId: 100011010 }]);
    const issues = validateCardMaster(value, createCardCatalog());
    expect(issues.some((item) => item.message.includes("100011010") && item.message.includes("原版卡") && item.message.includes("闪卡版会自动跳过"))).toBe(true);
  });
});
