using LitJson;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wizard;

namespace Shadowbus
{
    internal static class CustomDeckStore
    {
        internal static void MigrateLegacyDeckFilenames()
        {
            Directory.CreateDirectory(PathHelper.UnlimitedDeckPath);
            string[] files = Directory.GetFiles(
                PathHelper.UnlimitedDeckPath,
                "*.json",
                SearchOption.TopDirectoryOnly);
            var usedDeckNos = new HashSet<int>();
            foreach (string file in files)
            {
                try
                {
                    JsonData deck = JsonMapper.ToObject(File.ReadAllText(file));
                    if (deck.IsObject && deck.Keys.Contains("deck_no"))
                    {
                        int deckNo = deck["deck_no"].ToInt();
                        if (deckNo > 0)
                        {
                            usedDeckNos.Add(deckNo);
                        }
                    }
                }
                catch
                {
                    // The normal loader reports malformed deck files with more context.
                }
            }

            int nextDeckNo = usedDeckNos.Count == 0 ? 1 : usedDeckNos.Max() + 1;
            foreach (string sourceFile in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    JsonData deck = JsonMapper.ToObject(File.ReadAllText(sourceFile));
                    if (!deck.IsObject || !deck.Keys.Contains("deck_no"))
                    {
                        continue;
                    }

                    int deckNo = deck["deck_no"].ToInt();
                    if (deckNo <= 0)
                    {
                        continue;
                    }

                    string targetFile = GetDeckPath(deckNo);
                    if (PathsEqual(sourceFile, targetFile))
                    {
                        continue;
                    }

                    if (!File.Exists(targetFile))
                    {
                        File.Move(sourceFile, targetFile);
                        Plugin.Logger.LogInfo(
                            $"[CustomFormats] Migrated deck file {Path.GetFileName(sourceFile)} " +
                            $"to {Path.GetFileName(targetFile)}.");
                        continue;
                    }

                    while (usedDeckNos.Contains(nextDeckNo) ||
                        File.Exists(GetDeckPath(nextDeckNo)))
                    {
                        nextDeckNo++;
                    }
                    int previousDeckNo = deckNo;
                    deckNo = nextDeckNo++;
                    usedDeckNos.Add(deckNo);
                    deck["deck_no"] = deckNo;
                    targetFile = GetDeckPath(deckNo);
                    File.WriteAllText(targetFile, deck.ToJson());
                    File.Delete(sourceFile);
                    Plugin.Logger.LogWarning(
                        $"[CustomFormats] Deck number {previousDeckNo} was duplicated; " +
                        $"preserved {Path.GetFileName(sourceFile)} as deck {deckNo}.");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError(
                        $"[CustomFormats] Failed to migrate deck file {sourceFile}: {ex.Message}");
                }
            }
        }

        internal static void MigrateLegacyModernDecks()
        {
            string sourceDirectory = Path.Combine(
                PathHelper.LegacyCustomFormatPath,
                CustomFormats.ModernId,
                "Decks");
            if (!Directory.Exists(sourceDirectory))
            {
                return;
            }

            Directory.CreateDirectory(PathHelper.UnlimitedDeckPath);
            var usedDeckNos = new HashSet<int>();
            var importedLegacyFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string currentFile in EnumerateDeckFiles())
            {
                try
                {
                    JsonData current = JsonMapper.ToObject(File.ReadAllText(currentFile));
                    if (current.IsObject && current.Keys.Contains("deck_no"))
                    {
                        usedDeckNos.Add(current["deck_no"].ToInt());
                    }
                    if (current.IsObject && current.Keys.Contains("legacy_source_file"))
                    {
                        importedLegacyFiles.Add(current["legacy_source_file"].ToString());
                    }
                    else if (Path.GetFileName(currentFile).StartsWith(
                        "legacy_modern_",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        string sourceName = Path.GetFileName(currentFile)
                            .Substring("legacy_modern_".Length);
                        current["legacy_source_file"] = sourceName;
                        File.WriteAllText(currentFile, current.ToJson());
                        importedLegacyFiles.Add(sourceName);
                    }
                }
                catch
                {
                    // The normal loader reports malformed active deck files.
                }
            }

            foreach (string sourceFile in Directory.GetFiles(
                sourceDirectory,
                "*.json",
                SearchOption.TopDirectoryOnly))
            {
                string sourceName = Path.GetFileName(sourceFile);
                if (importedLegacyFiles.Contains(sourceName))
                {
                    continue;
                }
                string targetFile = Path.Combine(
                    PathHelper.UnlimitedDeckPath,
                    "legacy_modern_" + sourceName);
                if (File.Exists(targetFile))
                {
                    continue;
                }

                try
                {
                    JsonData deck = JsonMapper.ToObject(File.ReadAllText(sourceFile));
                    if (!deck.IsObject || !deck.Keys.Contains("deck_no") ||
                        !deck.Keys.Contains("card_id_array") ||
                        !deck["card_id_array"].IsArray ||
                        deck["card_id_array"].Count == 0)
                    {
                        continue;
                    }

                    int deckNo = deck["deck_no"].ToInt();
                    while (usedDeckNos.Contains(deckNo))
                    {
                        deckNo++;
                    }
                    deck["deck_no"] = deckNo;
                    deck["format_id"] = CustomFormats.ModernId;
                    deck["legacy_source_file"] = sourceName;
                    File.WriteAllText(targetFile, deck.ToJson());
                    usedDeckNos.Add(deckNo);
                    importedLegacyFiles.Add(sourceName);
                    Plugin.Logger.LogInfo(
                        $"[CustomFormats] Imported legacy Modern deck {sourceFile} " +
                        $"as deck {deckNo} in the shared Unlimited deck folder.");
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError(
                        $"[CustomFormats] Failed to import legacy Modern deck " +
                        $"{sourceFile}: {ex.Message}");
                }
            }
        }

        internal static JsonData LoadDeckList()
        {
            Directory.CreateDirectory(PathHelper.UnlimitedDeckPath);
            MigrateLegacyDeckFilenames();
            var loadedDecks = new List<JsonData>();
            var existingDeckNos = new HashSet<int>();
            bool hasEmptyDeck = false;

            foreach (string file in EnumerateDeckFiles())
            {
                try
                {
                    JsonData deck = JsonMapper.ToObject(File.ReadAllText(file));
                    if (!deck.IsObject || !deck.Keys.Contains("deck_no") ||
                        !deck.Keys.Contains("card_id_array") ||
                        !deck["card_id_array"].IsArray)
                    {
                        throw new InvalidDataException("Required deck fields are missing.");
                    }
                    EnsureFormatId(deck);
                    DropUnknownCards(file, deck);
                    loadedDecks.Add(deck);
                    existingDeckNos.Add(deck["deck_no"].ToInt());
                    hasEmptyDeck |= deck["card_id_array"].Count == 0;
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError(
                        $"[CustomFormats] Failed to load deck from {file}: {ex.Message}");
                }
            }

            if (!hasEmptyDeck)
            {
                int deckNo = 1;
                while (existingDeckNos.Contains(deckNo))
                {
                    deckNo++;
                }

                string json = CreateEmptyDeckJson(deckNo);
                string file = GetDeckPath(deckNo);
                File.WriteAllText(file, json);
                loadedDecks.Add(JsonMapper.ToObject(json));
            }

            return ToDeckList(loadedDecks);
        }

        /// <summary>已经报过「牌组里有不存在的卡号」的牌组（按卡号集合去重，免得每次读盘都刷屏）。</summary>
        private static readonly HashSet<string> ReportedUnknownCards = new HashSet<string>();

        /// <summary>已经报过「卡表还没就绪，暂时不校验卡号」的牌组（同样按卡号集合去重）。</summary>
        private static readonly HashSet<string> ReportedNotReadyDecks = new HashSet<string>();

        /// <summary>
        /// 牌组里引用了**当前卡表里已经不存在的卡号**时，游戏那条链是直接解引用的：
        ///
        ///   DeckData.ParseCardIdList → UIBase_CardManager.SortIDList
        ///   → orderby new ComparableCard(card.CardId, …)   // card == null → NullReferenceException
        ///
        /// 典型场景：自制卡的 json 被删掉/改名（比如把某个卡文件夹删了），旧牌组还存着它的卡号。
        /// 结果是**整份本地牌组都注入不进去**（DeckInfoTask 直接报错、牌组列表打不开）。
        /// 这里在交给游戏之前把这些卡号剔掉，并打一行日志说清是哪份牌组、少了哪些卡号 ——
        /// 牌组还能打开编辑，只是少了几张已经没了的卡。
        ///
        /// **但是**：卡表本身没就绪时（`CardMaster` 还没加载 / 还没应用 `CardMaster` mod），
        /// 任何卡号都会查不到。早期版本在这种情况下会把**整副牌所有卡**都当成"不存在"剔掉
        /// （实测日志里每份牌组都报 "uses 40/100 card id(s) that are not in the card master"，
        /// 而那些卡在 `Reference/card_skills.csv` 里明明都在）。所以现在先确认卡表可用：
        /// 只有当至少一套卡表**确实装着卡**时才做剔除，否则原样放行。
        /// </summary>
        private static void DropUnknownCards(string file, JsonData deck)
        {
            try
            {
                JsonData cardIds = deck["card_id_array"];
                if (cardIds == null || !cardIds.IsArray || cardIds.Count == 0)
                {
                    return;
                }

                if (!TryGetUsableCardMasters(out List<CardMaster> masters))
                {
                    string key = Path.GetFileName(file);
                    if (ReportedNotReadyDecks.Add(key))
                    {
                        Plugin.Logger.LogInfo(
                            $"[CustomFormats] {key}: card master is not loaded yet, so the card ids " +
                            "were left untouched (no card was removed).");
                    }

                    return;
                }

                List<int> unknown = null;
                var kept = new List<int>(cardIds.Count);
                for (int i = 0; i < cardIds.Count; i++)
                {
                    int cardId = cardIds[i].ToInt();
                    if (IsKnownCard(masters, cardId))
                    {
                        kept.Add(cardId);
                    }
                    else
                    {
                        (unknown ??= []).Add(cardId);
                    }
                }

                if (unknown == null)
                {
                    return;
                }

                deck["card_id_array"] = ToJsonArray(kept);
                unknown.Sort();
                string unknownKey = string.Join(",", unknown.Distinct());
                if (!ReportedUnknownCards.Add(unknownKey))
                {
                    return;
                }

                string name = deck.Keys.Contains("deck_name") ? deck["deck_name"].ToString() : null;
                int deckNo = deck.Keys.Contains("deck_no") ? deck["deck_no"].ToInt() : 0;
                Plugin.Logger.LogWarning(
                    $"[CustomFormats] deck {deckNo}" +
                    (string.IsNullOrEmpty(name) ? "" : $" '{name}'") +
                    $" ({Path.GetFileName(file)}) uses {unknown.Count} card id(s) that are not in the card master: " +
                    $"{string.Join(", ", unknown.Distinct())} — they were removed so the deck list can load. " +
                    "Put the card files back (Mods/CardMaster/<卡文件夹>/) or fix/delete this deck.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[CustomFormats] could not check {file} for unknown cards: {ex.Message}");
            }
        }

        /// <summary>
        /// 取当前可用的卡表（`Default` 与对战用的那套）。**只有真的装着卡的卡表才算可用**：
        /// 卡表对象存在但 `GetAllCardIds()` 为空，说明还没加载，这时任何卡号都查不到，
        /// 拿它做存在性判断只会把整副牌误剔。
        /// </summary>
        private static bool TryGetUsableCardMasters(out List<CardMaster> masters)
        {
            // 卡表一旦装好就不会再变空，所以只缓存"可用"这个正结果；
            // 未就绪时每次都重新问，等它加载好了立刻就能正常校验。
            if (_usableCardMasters != null)
            {
                masters = _usableCardMasters;
                return true;
            }

            masters = new List<CardMaster>(2);
            AddIfUsable(masters, CardMaster.CardMasterId.Default);
            try
            {
                AddIfUsable(masters, CardMaster.BatttleCardMasterId);
            }
            catch (Exception)
            {
            }

            if (masters.Count == 0)
            {
                return false;
            }

            _usableCardMasters = masters;
            return true;
        }

        private static List<CardMaster> _usableCardMasters;

        private static void AddIfUsable(List<CardMaster> masters, CardMaster.CardMasterId id)
        {
            try
            {
                CardMaster master = CardMaster.GetInstance(id);
                if (master == null || masters.Contains(master))
                {
                    return;
                }

                List<int> ids = master.GetAllCardIds();
                if (ids == null || ids.Count == 0)
                {
                    return;
                }

                masters.Add(master);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>这张卡号在已就绪的卡表里能不能查到（查不到才会被剔掉，保守处理）。</summary>
        private static bool IsKnownCard(List<CardMaster> masters, int cardId)
        {
            if (cardId <= 0 || masters == null)
            {
                return false;
            }

            foreach (CardMaster master in masters)
            {
                try
                {
                    if (master.GetCardParameterFromId(cardId) != null)
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                }
            }

            return false;
        }

        private static JsonData ToJsonArray(IEnumerable<int> values)
        {
            JsonData array = new JsonData();
            array.SetJsonType(JsonType.Array);
            foreach (int value in values)
            {
                array.Add(value);
            }

            return array;
        }

        internal static IEnumerable<string> EnumerateDeckFiles()
        {
            Directory.CreateDirectory(PathHelper.UnlimitedDeckPath);
            return Directory.GetFiles(
                PathHelper.UnlimitedDeckPath,
                "*.json",
                SearchOption.TopDirectoryOnly);
        }

        internal static string GetDeckPath(int deckNo)
        {
            if (deckNo <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(deckNo));
            }
            return Path.Combine(PathHelper.UnlimitedDeckPath, $"deck_{deckNo}.json");
        }

        internal static void SaveDeck(JsonData deck, string previousPath = null)
        {
            if (deck == null || !deck.IsObject || !deck.Keys.Contains("deck_no"))
            {
                throw new InvalidDataException("Cannot save a deck without deck_no.");
            }

            string targetPath = GetDeckPath(deck["deck_no"].ToInt());
            File.WriteAllText(targetPath, deck.ToJson());
            if (!string.IsNullOrEmpty(previousPath) &&
                !PathsEqual(previousPath, targetPath) &&
                File.Exists(previousPath))
            {
                File.Delete(previousPath);
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }

        internal static string GetDeckFormatId(int deckNo)
        {
            foreach (string file in EnumerateDeckFiles())
            {
                try
                {
                    JsonData deck = JsonMapper.ToObject(File.ReadAllText(file));
                    if (deck.IsObject && deck.Keys.Contains("deck_no") &&
                        deck["deck_no"].ToInt() == deckNo)
                    {
                        return ReadFormatId(deck);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogWarning(
                        $"[CustomFormats] Could not inspect deck metadata in {file}: {ex.Message}");
                }
            }
            return CustomFormats.UnlimitedId;
        }

        internal static string ReadFormatId(JsonData deck)
        {
            if (deck != null && deck.IsObject && deck.Keys.Contains("format_id"))
            {
                return CustomFormats.Get(deck["format_id"].ToString()).Id;
            }
            return CustomFormats.UnlimitedId;
        }

        internal static void EnsureFormatId(JsonData deck)
        {
            deck["format_id"] = ReadFormatId(deck);
        }

        private static JsonData ToDeckList(IEnumerable<JsonData> decks)
        {
            JsonData deckList = new JsonData();
            foreach (JsonData deck in decks
                .OrderBy(item => item["card_id_array"].Count == 0 ? 1 : 0)
                .ThenBy(GetOrderNumber)
                .ThenBy(item => item["deck_no"].ToInt()))
            {
                deckList.Add(deck);
            }
            return deckList;
        }

        private static int GetOrderNumber(JsonData deck)
        {
            if (deck.Keys.Contains("order_num"))
            {
                int order = deck["order_num"].ToInt();
                if (order > 0)
                {
                    return order;
                }
            }
            return int.MaxValue;
        }

        private static string CreateEmptyDeckJson(int deckNo)
        {
            return "{\r\n" +
                $"  \"deck_no\": {deckNo},\r\n" +
                "  \"class_id\": 1,\r\n" +
                "  \"sleeve_id\": 3000011,\r\n" +
                "  \"leader_skin_id\": 0,\r\n" +
                "  \"deck_name\": \"\",\r\n" +
                "  \"format_id\": \"unlimited\",\r\n" +
                "  \"card_id_array\": [],\r\n" +
                "  \"is_complete_deck\": 0,\r\n" +
                "  \"restricted_card_exists\": false,\r\n" +
                "  \"is_available_deck\": 1,\r\n" +
                "  \"maintenance_card_ids\": [],\r\n" +
                "  \"is_include_un_possession_card\": false,\r\n" +
                "  \"is_random_leader_skin\": 0,\r\n" +
                "  \"leader_skin_id_list\": [0],\r\n" +
                "  \"order_num\": 0,\r\n" +
                "  \"create_deck_time\": null\r\n" +
                "}";
        }
    }
}
