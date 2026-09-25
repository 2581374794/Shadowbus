using HarmonyLib;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Wizard;

namespace Shadowbus
{
    /// <summary>
    /// 打开卡组列表时热重载 CardMaster 与卡组数据。
    ///
    /// **只在 Mods 里的文件真的变过时才重建。** 重建（<c>ApplyCardMasterPatches</c>）第一件事
    /// 是 <c>RevokeCardMasterPatches</c>：它把 <c>m_cardParameters</c> 里每个 CardParameter
    /// 换成 Backup 的新 <c>Clone()</c>，并清空插件的卡图/语音注册表，然后重新应用 mod。
    /// 而 UI 里已经建好的那些卡还抓着旧对象、插件注册表也已经空了一轮 —— 于是那几张卡的
    /// 卡图会一直空到下一次重画（玩家看到的就是"改一下数量就好了，接着又轮到另一张"）。
    /// 这个重建本身也要几百 ms 到几秒（<c>BuildArtBundleRedirects</c> 要对全部卡各做两次
    /// <c>File.Exists</c>）。
    ///
    /// 所以这里改用「Mods 文件的路径 + 大小 + 修改时间」当签名：没变就直接跳过，
    /// 改了（玩家编辑了卡、卡组、格式）下一次打开卡组列表照样会重建。
    /// </summary>
    public static class DeckListHotReload
    {
        private static string lastCardSignature;
        private static string lastDeckSignature;

        [HarmonyPatch(typeof(DeckListUI), "onOpen")]
        [HarmonyPrefix]
        public static void DeckListUI_onOpen_Prefix()
        {
            // 卡表那组（重建贵、掉图）和卡组那组（重建便宜）分开算签名：玩家只是改了卡组
            // 就不该顺手把整张卡表重建一遍。
            string cardSignature = ComputeCardSignature();
            string deckSignature = ComputeDeckSignature();
            bool cardsChanged = cardSignature == null || cardSignature != lastCardSignature;
            bool decksChanged = deckSignature == null || deckSignature != lastDeckSignature;

            if (!cardsChanged && !decksChanged)
            {
                Plugin.Logger.LogInfo(
                    "[DeckListHotReload] Mods unchanged since the last reload; " +
                    "keeping the current CardMaster and deck data.");
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            Plugin.Logger.LogInfo(
                $"[DeckListHotReload] Deck list opened; refreshing " +
                $"{(cardsChanged ? "CardMaster" : "nothing")}/{(decksChanged ? "deck data" : "no deck data")}.");

            try
            {
                using (PerfTrace.Enter("DeckListHotReload"))
                {
                    if (cardsChanged)
                    {
                        CustomFormats.ReloadForUi("deck list");
                        RefreshCardMaster();
                    }

                    // 卡表变了的话牌组数据也要重算（新卡可能要进卡组校验），否则只在卡组文件
                    // 真的动过时才刷。
                    if (cardsChanged || decksChanged)
                    {
                        RefreshDeckListData();
                    }
                }
            }
            finally
            {
                // 重建过程中自己也会写文件（导出、卡组规范化），所以基准取重建之后的状态，
                // 下一次打开才能正确判断"没变"。
                lastCardSignature = ComputeCardSignature();
                lastDeckSignature = ComputeDeckSignature();
            }

            stopwatch.Stop();
            Plugin.Logger.LogInfo($"[DeckListHotReload] Refresh finished in {stopwatch.ElapsedMilliseconds} ms.");

            // 牌组 / 卡表都重建过了，自定义练习那边缓存的卡组列表与 AI 牌组也就过期了：
            // 让下一次进练习页重新预热，避免列表里还是旧卡组。
            AIManager.InvalidatePracticeWarmup();
        }

        /// <summary>强制下一次打开卡组列表时重建（正常靠文件时间戳就够，留给别的写文件的路径用）。</summary>
        public static void Invalidate()
        {
            lastCardSignature = null;
            lastDeckSignature = null;
        }

        private static string ComputeCardSignature()
        {
            // 卡图与语音自 2.5.5 起一律放各自卡的文件夹（Mods/CardMaster/<卡>/），
            // 旧的 Mods/CardImages、Mods/CardVoices 已彻底废弃：不创建、不读取、
            // 也不参与这里的重建判定（早先留着的两个空目录不再影响热重载）。
            return ComputeSignature(
                Plugin.CardMasterPath,
                Path.Combine(Plugin.ModPath, "Format"));
        }

        private static string ComputeDeckSignature()
        {
            return ComputeSignature(
                PathHelper.UnlimitedDeckPath,
                Path.Combine(Plugin.ModPath, "RotationDecks"));
        }

        private static string ComputeSignature(params string[] folders)
        {
            try
            {
                var builder = new StringBuilder();
                foreach (string folder in folders)
                {
                    // Reference/ 里全是插件自己导出的 CSV（card_skills.csv 有十几 MB），
                    // 每次导出都会刷新时间戳，不能让它触发重建。
                    AppendFolder(
                        builder,
                        folder,
                        skipReferenceFolder: string.Equals(
                            folder, Plugin.CardMasterPath, StringComparison.OrdinalIgnoreCase));
                }

                return builder.ToString();
            }
            catch (Exception)
            {
                // 读不出来就当作"变了"：宁可多重建一次，也不要漏掉玩家的改动。
                return null;
            }
        }

        private static void AppendFolder(StringBuilder builder, string folder, bool skipReferenceFolder)
        {
            builder.Append(folder).Append('\n');
            if (!Directory.Exists(folder))
            {
                builder.Append("(missing)\n");
                return;
            }

            foreach (string file in Directory
                .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                if (skipReferenceFolder &&
                    file.StartsWith(PathHelper.CardMasterReferencePath, StringComparison.OrdinalIgnoreCase))
                {
                    // Reference/ 里全是插件自己导出的 CSV（card_skills.csv 有十几 MB），
                    // 每次导出都会刷新时间戳，不能让它触发重建。
                    continue;
                }

                FileInfo info = new FileInfo(file);
                builder.Append(info.FullName).Append('=')
                    .Append(info.Length).Append('@')
                    .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
        }

        private static void RefreshCardMaster()
        {
            try
            {
                CardMaster master = CardMaster.GetInstanceForBattle();
                if (master == null)
                {
                    Plugin.Logger.LogWarning("[DeckListHotReload] CardMaster is not available; skipped CardMaster reload.");
                    return;
                }

                int patchFileCount = Directory.Exists(Plugin.CardMasterPath)
                    ? Directory.GetFiles(Plugin.CardMasterPath, "*.json", SearchOption.AllDirectories).Length
                    : 0;
                int cardCountBefore = master.GetAllCardIds().Count;

                CardMasterPatcher.ApplyCardMasterPatches(master);

                int cardCountAfter = master.GetAllCardIds().Count;
                Plugin.Logger.LogInfo(
                    $"[DeckListHotReload] CardMaster reloaded: files={patchFileCount}, " +
                    $"cards={cardCountBefore}->{cardCountAfter}.");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogError($"[DeckListHotReload] CardMaster reload failed; opening deck list with current data.\n{exception}");
            }
        }

        private static void RefreshDeckListData()
        {
            try
            {
                if (Data.Load == null || Data.Load.data == null)
                {
                    Plugin.Logger.LogWarning("[DeckListHotReload] LoadDetail is not available; skipped deck list refresh.");
                    return;
                }

                Directory.CreateDirectory(PathHelper.UnlimitedDeckPath);
                int fileCountBefore = Directory.GetFiles(
                    PathHelper.UnlimitedDeckPath,
                    "*.json",
                    SearchOption.TopDirectoryOnly).Length;

                Offlinizer.LoadLocalDecks(Data.Load.data);

                var deckGroups = DeckListUtility.DeckGroupDataBaseClone();
                var unlimitedGroup = deckGroups.FirstOrDefault(group =>
                    group.DeckFormat == Format.Unlimited &&
                    group.AttributeType == DeckAttributeType.CustomDeck);
                int deckCount = unlimitedGroup?.DeckDataList.Count ?? 0;
                int emptyDeckCount = unlimitedGroup?.DeckDataList.Count(deck => deck.IsNoCard()) ?? 0;

                GameMgr gameMgr = GameMgr.GetIns();
                if (gameMgr != null && gameMgr.GetDataMgr() != null)
                {
                    gameMgr.GetDataMgr().CurrentDeckListParamData = new DeckGroupListData(deckGroups);
                }

                int fileCountAfter = Directory.GetFiles(
                    PathHelper.UnlimitedDeckPath,
                    "*.json",
                    SearchOption.TopDirectoryOnly).Length;
                Plugin.Logger.LogInfo(
                    "[DeckListHotReload] Shared Unlimited deck data refreshed: " +
                    $"files={fileCountBefore}->{fileCountAfter}, " +
                    $"decks={deckCount}, emptyDecks={emptyDeckCount}.");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogError($"[DeckListHotReload] Deck data refresh failed; opening deck list with current data.\n{exception}");
            }
        }
    }
}
