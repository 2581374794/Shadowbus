using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Wizard;

namespace Shadowbus;

/// <summary>
/// Global log formatting shared by the base mod and the P2P subsystem.
/// BepInEx dispatches one LogEventArgs instance to all listeners, so the
/// InternalLogEvent prefix formats the message before the stock console/disk
/// listeners receive it. The extra listener also keeps a readable, enriched
/// copy for post-mortem debugging.
/// </summary>
internal static class EnhancedLogSystem
{
    private static readonly object FileLock = new object();
    private static readonly object ConsoleLock = new object();
    private static readonly Regex CardIdPattern = new Regex(
        @"(?<![0-9])([0-9]{6,12})(?![0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex AnsiPattern = new Regex(
        @"\x1B\[[0-9;]*m",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly FieldInfo LogDataField =
        typeof(LogEventArgs).GetField(
            "<Data>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly Dictionary<int, CardLogInfo> CardCache =
        new Dictionary<int, CardLogInfo>();
    private static readonly HashSet<int> MissingCardCache =
        new HashSet<int>();
    private static CardMaster cachedCardMaster;

    private static ConfigEntry<bool> enabled;
    private static ConfigEntry<bool> showCardDetails;
    private static ConfigEntry<bool> showCardEffects;
    private static ConfigEntry<bool> detailedP2P;
    private static ConfigEntry<bool> ansiColors;
    private static ConfigEntry<int> wrapWidth;
    private static ConfigEntry<string> outputPath;
    private static EnhancedLogListener listener;
    private static StreamWriter writer;
    private static string writerPath;
    private static int mainThreadId;

    internal static bool IsEnabled => enabled == null || enabled.Value;
    internal static bool DetailedP2PEnabled =>
        IsEnabled && detailedP2P != null && detailedP2P.Value;

    internal static void Initialize(ConfigFile config)
    {
        if (config == null || enabled != null)
        {
            return;
        }

        mainThreadId = Thread.CurrentThread.ManagedThreadId;

        enabled = config.Bind(
            "Logging",
            "EnhancedEnabled",
            true,
            "Enable global log formatting and the enriched Shadowbus log file.");
        showCardDetails = config.Bind(
            "Logging",
            "ShowCardDetails",
            true,
            "Annotate card IDs with card name and cost in all log messages.");
        showCardEffects = config.Bind(
            "Logging",
            "ShowCardEffects",
            false,
            "Append card skill/evolution text to log messages that contain card IDs.");
        detailedP2P = config.Bind(
            "Logging",
            "DetailedP2P",
            false,
            "Log every P2P action with native envelope details and card effects.");
        ansiColors = config.Bind(
            "Logging",
            "AnsiColors",
            true,
            "Use real BepInEx console colors when an external console is active. " +
            "Unity/Player.log and enhanced files always remain plain text.");
        wrapWidth = config.Bind(
            "Logging",
            "WrapWidth",
            140,
            "Maximum characters per formatted log line. Use 0 to disable wrapping.");
        outputPath = config.Bind(
            "Logging",
            "EnhancedFile",
            "BepInEx/LogOutput-enhanced.log",
            "Enhanced log path, relative to the game root unless absolute.");

        try
        {
            listener = new EnhancedLogListener();
            BepInEx.Logging.Logger.Listeners.Add(listener);
        }
        catch (Exception exception)
        {
            Plugin.Logger?.LogWarning(
                "[Logging] Could not register enhanced log listener: " +
                exception.Message);
        }
    }

    internal static void Shutdown()
    {
        lock (FileLock)
        {
            try
            {
                writer?.Flush();
                writer?.Dispose();
            }
            catch (Exception)
            {
            }
            writer = null;
            writerPath = null;
        }
        CardCache.Clear();
        MissingCardCache.Clear();
        cachedCardMaster = null;

        if (listener != null)
        {
            try
            {
                BepInEx.Logging.Logger.Listeners.Remove(listener);
            }
            catch (Exception)
            {
            }
            listener = null;
        }
    }

    internal static void Process(LogEventArgs eventArgs)
    {
        if (!IsEnabled || eventArgs == null || LogDataField == null)
        {
            return;
        }

        try
        {
            string raw = eventArgs.Data?.ToString() ?? string.Empty;
            if (raw.Length == 0)
            {
                return;
            }

            string formatted = FormatMessage(
                raw,
                eventArgs.Level,
                eventArgs.Source?.SourceName,
                DetailedP2PEnabled && raw.IndexOf(
                    "[P2P-DETAIL]", StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.Equals(raw, formatted, StringComparison.Ordinal))
            {
                LogDataField.SetValue(eventArgs, formatted);
            }
        }
        catch (Exception)
        {
            // Logging must never break the game or the original BepInEx
            // listeners. The enriched listener remains available even if a
            // particular event contains an object with a broken ToString().
            // Do not log from this catch block: doing so would recursively
            // enter the same formatter if the failure is deterministic.
        }
    }

    internal static void LogDetailedP2P(
        string phase,
        string uri,
        Dictionary<string, object> data)
    {
        if (!DetailedP2PEnabled || Plugin.Logger == null)
        {
            return;
        }

        StringBuilder message = new StringBuilder();
        // Keep a machine-recognisable marker for the formatter, but leave all
        // level/source prefixes to BepInEx.  Adding another [INFO]/source here
        // makes the normal log start with duplicate prefixes.
        message.Append("[P2P-DETAIL] ACTION");
        AppendTableRow(message, "phase", DescribePhase(phase));
        AppendTableRow(message, "uri", DescribeUri(uri));
        if (data != null)
        {
            try
            {
                AppendActionTable(message, data);
            }
            catch (Exception exception)
            {
                AppendTableRow(message, "describeError", exception.Message);
            }
        }
        Plugin.Logger.LogInfo(message.ToString());
    }

    private static void AppendActionTable(
        StringBuilder output,
        Dictionary<string, object> data)
    {
        if (output == null || data == null)
        {
            return;
        }

        AppendValueRow(output, data, "playIdx", value =>
            value + " (动作索引 / action index)");
        AppendValueRow(output, data, "type", DescribePlayActionType);
        AppendValueRow(output, data, "turnState", DescribeTurnState);
        AppendValueRow(output, data, "playSeq", value =>
            value + " (原版网络序列 / native play sequence)");
        AppendValueRow(output, data, "actionSeq", value =>
            value + " (动作序列 / action sequence)");
        AppendValueRow(output, data, "p2pServerActionId", value =>
            value + " (Host权威动作 / Host authority action)");
        AppendValueRow(output, data, "p2pClientSequence", value =>
            value + " (客户端请求序列 / client request sequence)");

        foreach (string key in new[]
        {
            "knownList", "uList", "targetList", "oppoTargetList"
        })
        {
            if (data.TryGetValue(key, out object value))
            {
                AppendTableRow(output, key, CountValue(value) + " 项 / entries");
            }
        }

        if (data.TryGetValue("orderList", out object rawOrders))
        {
            List<object> orders = Enumerate(rawOrders).ToList();
            AppendTableRow(output, "orderList", orders.Count + " 项 / native moves");
            for (int i = 0; i < orders.Count; i++)
            {
                if (!(orders[i] is IDictionary<string, object> order) ||
                    !(order.TryGetValue("move", out object rawMove) &&
                        rawMove is IDictionary<string, object> move))
                {
                    continue;
                }

                string from = ReadValue(move, "from", "?");
                string to = ReadValue(move, "to", "?");
                string indices = ReadIndices(move);
                string self = ReadValue(move, "isSelf", "?");
                output.AppendLine()
                    .Append("    move[").Append(i).Append("] | ")
                    .Append(DescribeZone(from)).Append(" -> ")
                    .Append(DescribeZone(to)).Append(" | idx=")
                    .Append(indices).Append(" | ")
                    .Append(self == "1" ? "自己方" : self == "0" ? "对手方" : "归属=?");
            }
        }

        if (data.TryGetValue("keyAction", out object rawKeyActions))
        {
            List<object> keyActions = Enumerate(rawKeyActions).ToList();
            AppendTableRow(output, "keyAction", keyActions.Count + " 项 / special choices");
            for (int i = 0; i < keyActions.Count; i++)
            {
                if (!(keyActions[i] is IDictionary<string, object> action))
                {
                    continue;
                }
                string type = action.TryGetValue("type", out object rawType)
                    ? DescribeKeyActionType(rawType)
                    : "未知";
                string card = action.TryGetValue("cardId", out object rawCard)
                    ? rawCard?.ToString() ?? "?"
                    : "-";
                output.AppendLine()
                    .Append("    choice[").Append(i).Append("] | type=")
                    .Append(type).Append(" | card=").Append(card);
            }
        }

        if (data.TryGetValue(P2PBattleProtocol.ActionManifestKey, out object rawManifest) &&
            rawManifest is IDictionary<string, object> manifest)
        {
            AppendTableRow(output, "manifest", DescribeManifest(manifest));
        }

        try
        {
            // Only collect card identities from native action containers and
            // explicit transform/fusion containers. Do not regex-scan the full
            // JSON envelope: manifests/history/private baselines can contain
            // many unrelated card IDs and make one action look duplicated.
            List<CardLogInfo> cards = FindCardsFromActionData(data);
            if (cards.Count > 0)
            {
                AppendTableRow(output, "cards", string.Join(", ", cards.Select(FormatCardSummary)));
                if (showCardEffects != null && showCardEffects.Value)
                {
                    foreach (CardLogInfo card in cards)
                    {
                        AppendWrapped(output, "    效果：", card.Effect);
                        AppendWrapped(output, "    进化效果：", card.EvolutionEffect);
                    }
                }
            }
        }
        catch (Exception)
        {
        }
    }

    private static void AppendTableRow(
        StringBuilder output,
        string key,
        string value)
    {
        output.AppendLine()
            .Append("  ")
            .Append((key ?? "?").PadRight(18))
            .Append(" | ")
            .Append(value ?? "null");
    }

    private static void AppendValueRow(
        StringBuilder output,
        IDictionary<string, object> data,
        string key,
        Func<string, string> formatter)
    {
        if (data == null || !data.TryGetValue(key, out object rawValue))
        {
            return;
        }

        string value = rawValue?.ToString() ?? "null";
        AppendTableRow(output, key, formatter == null ? value : formatter(value));
    }

    private static int CountValue(object value)
    {
        if (value == null || value is string || !(value is IEnumerable enumerable))
        {
            return 0;
        }

        int count = 0;
        foreach (object ignored in enumerable)
        {
            count++;
        }
        return count;
    }

    private static IEnumerable<object> Enumerate(object value)
    {
        if (value == null || value is string || !(value is IEnumerable enumerable))
        {
            yield break;
        }

        foreach (object item in enumerable)
        {
            yield return item;
        }
    }

    private static string ReadValue(
        IDictionary<string, object> data,
        string key,
        string fallback)
    {
        return data != null && data.TryGetValue(key, out object value)
            ? value?.ToString() ?? fallback
            : fallback;
    }

    private static string ReadIndices(IDictionary<string, object> move)
    {
        if (move == null)
        {
            return "?";
        }
        if (move.TryGetValue("idx", out object idx))
        {
            return FormatIndicesValue(idx);
        }
        if (move.TryGetValue("idxList", out object idxList))
        {
            return FormatIndicesValue(idxList);
        }
        return "?";
    }

    private static string FormatIndicesValue(object value)
    {
        if (value == null)
        {
            return "?";
        }
        if (value is string || !(value is IEnumerable))
        {
            return value.ToString();
        }
        return "[" + string.Join(",", Enumerate(value).Select(item =>
            item?.ToString() ?? "null")) + "]";
    }

    private static string DescribePhase(string phase)
    {
        string value = phase ?? "?";
        string meaning;
        switch (value)
        {
            case "emit":
                meaning = "发出 / emit";
                break;
            case "host-request":
                meaning = "Host接收请求 / Host request";
                break;
            case "host-response":
                meaning = "Host生成响应 / Host response";
                break;
            case "receive":
                meaning = "接收执行 / receive";
                break;
            default:
                meaning = "未知阶段 / unknown";
                break;
        }
        return value + " (" + meaning + ")";
    }

    private static string DescribeUri(string uri)
    {
        string value = uri ?? "?";
        string meaning;
        switch (value)
        {
            case "PlayActions":
                meaning = "出牌/攻击/进化动作";
                break;
            case "TurnStart":
                meaning = "回合开始";
                break;
            case "TurnEndActions":
                meaning = "回合结束时触发的动作";
                break;
            case "TurnEnd":
                meaning = "回合结束";
                break;
            case "TurnEndFinal":
                meaning = "终局回合结束";
                break;
            case "Judge":
                meaning = "胜负/回合判定";
                break;
            case "Echo":
                meaning = "原版动作确认回显";
                break;
            case "ChatStamp":
                meaning = "战斗表情";
                break;
            case "Deal":
                meaning = "发牌";
                break;
            case "Swap":
                meaning = "换牌";
                break;
            case "Ready":
                meaning = "准备完成";
                break;
            case "authority_request":
                meaning = "Guest请求Host执行";
                break;
            case "authority_result":
                meaning = "Host权威执行结果";
                break;
            case "authority_reject":
                meaning = "Host拒绝请求";
                break;
            default:
                meaning = "原版网络URI";
                break;
        }
        return value + " (" + meaning + ")";
    }

    private static string DescribePlayActionType(string raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int value))
        {
            if (Enum.TryParse(raw, true, out NetworkBattleDefine.PlayActionType named))
            {
                return raw + " (" + PlayActionMeaning(named) + ")";
            }
            return raw + " (未知动作类型 / unknown)";
        }

        NetworkBattleDefine.PlayActionType type =
            (NetworkBattleDefine.PlayActionType)value;
        return value.ToString(CultureInfo.InvariantCulture) +
            " (" + (Enum.IsDefined(typeof(NetworkBattleDefine.PlayActionType), value)
                ? PlayActionMeaning(type)
                : "未知动作类型 / unknown") + ")";
    }

    private static string PlayActionMeaning(NetworkBattleDefine.PlayActionType type)
    {
        switch (type)
        {
            case NetworkBattleDefine.PlayActionType.ATTACK:
                return "攻击 / ATTACK";
            case NetworkBattleDefine.PlayActionType.EVOLUTION:
                return "进化 / EVOLUTION";
            case NetworkBattleDefine.PlayActionType.EVOLUTION_SELECT:
                return "进化并选择 / EVOLUTION_SELECT";
            case NetworkBattleDefine.PlayActionType.PLAY_HAND:
                return "使用手牌 / PLAY_HAND";
            case NetworkBattleDefine.PlayActionType.PLAY_HAND_SELECT:
                return "使用手牌并选择 / PLAY_HAND_SELECT";
            case NetworkBattleDefine.PlayActionType.FUSION:
                return "融合 / FUSION";
            default:
                return type.ToString();
        }
    }

    private static string DescribeTurnState(string raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int value))
        {
            return raw + " (未知回合状态 / unknown)";
        }
        string meaning = value == 0
            ? "自己回合 / self turn"
            : value == 1
                ? "对手回合 / opponent turn"
                : "未知回合状态 / unknown";
        return value.ToString(CultureInfo.InvariantCulture) + " (" + meaning + ")";
    }

    private static string DescribeZone(string raw)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int value))
        {
            return raw + " (未知区域 / unknown)";
        }

        string meaning;
        switch (value)
        {
            case 0:
                meaning = "牌组 / Deck";
                break;
            case 10:
                meaning = "手牌 / Hand";
                break;
            case 20:
                meaning = "战场 / Field";
                break;
            case 30:
                meaning = "墓地 / Cemetery";
                break;
            case 40:
                meaning = "消灭区 / Banish";
                break;
            case 50:
                meaning = "无 / None";
                break;
            case 60:
                meaning = "融合素材区 / FusionIngredient";
                break;
            case 70:
                meaning = "骑乘区 / Riding";
                break;
            case 80:
                meaning = "预留区 / Reservation";
                break;
            case 90:
                meaning = "协作区 / Unite";
                break;
            case 999:
                meaning = "黑洞区 / BlackHole";
                break;
            default:
                meaning = "未知区域 / unknown";
                break;
        }
        return value.ToString(CultureInfo.InvariantCulture) + " (" + meaning + ")";
    }

    private static string DescribeKeyActionType(object raw)
    {
        string value = raw?.ToString() ?? "?";
        switch (value)
        {
            case "Choice":
                return value + " (抉择)";
            case "Accelerated":
                return value + " (激奏)";
            case "Crystallize":
                return value + " (结晶)";
            case "Fusion":
                return value + " (融合)";
            case "HaveBeforeSkillChoice":
                return value + " (先行抉择)";
            case "BurialRate":
                return value + " (葬送)";
            case "ChoiceEvolution":
                return value + " (抉择进化)";
            case "ChoiceBrave":
                return value + " (抉择勇士)";
            default:
                return value;
        }
    }

    private static string DescribeManifest(IDictionary<string, object> manifest)
    {
        string sequence = ReadValue(manifest, "seq", "?");
        int evaluations = manifest.TryGetValue(
            "p2pAuthoritativeSkillEvaluations", out object rawEvaluations)
            ? CountValue(rawEvaluations)
            : 0;
        int targets = manifest.TryGetValue(
            "p2pAuthoritativeSkillTargets", out object rawTargets)
            ? CountValue(rawTargets)
            : 0;
        int conditions = 0;
        foreach (object item in Enumerate(rawEvaluations))
        {
            if (item is IDictionary<string, object> evaluation &&
                evaluation.TryGetValue("conditions", out object rawConditions))
            {
                conditions += CountValue(rawConditions);
            }
        }
        return "seq=" + sequence + ", evaluations=" + evaluations +
            ", targets=" + targets + ", conditions=" + conditions;
    }

    private static string FormatCardSummary(CardLogInfo card)
    {
        return card.Id.ToString(CultureInfo.InvariantCulture) + " " + card.Name +
            " (费用=" + card.Cost.ToString(CultureInfo.InvariantCulture) + ")";
    }

    private static List<CardLogInfo> FindCardsFromActionData(
        Dictionary<string, object> data)
    {
        if (data == null)
        {
            return new List<CardLogInfo>();
        }

        List<int> ids = new List<int>();
        HashSet<int> seen = new HashSet<int>();
        Action<object> add = null;
        add = raw =>
        {
            if (raw == null || raw is string && !TryParseCardId(raw, out _))
            {
                return;
            }
            if (raw is IEnumerable enumerable && !(raw is string))
            {
                foreach (object item in enumerable)
                {
                    add(item);
                }
                return;
            }
            if (TryParseCardId(raw, out int id) && seen.Add(id))
            {
                ids.Add(id);
            }
        };

        if (data.TryGetValue("cardId", out object rootCardId))
        {
            add(rootCardId);
        }

        foreach (string key in new[]
        {
            "knownList", "uList", "orderList", "keyAction",
            "p2pFusionActions", "p2pMetamorphoses",
            P2PBattleProtocol.FusionMetamorphoseOriginalsKey
        })
        {
            if (data.TryGetValue(key, out object value))
            {
                CollectActionCardIds(value, add, true);
            }
        }

        return FindCards(string.Join(",", ids));
    }

    private static void CollectActionCardIds(
        object value,
        Action<object> add,
        bool insideCardContainer)
    {
        if (value == null || value is string)
        {
            return;
        }
        if (value is IDictionary<string, object> dictionary)
        {
            foreach (KeyValuePair<string, object> pair in dictionary)
            {
                bool cardIdentity = IsCardIdentityKey(pair.Key);
                if (cardIdentity && insideCardContainer)
                {
                    add(pair.Value);
                }

                bool nestedContainer = insideCardContainer ||
                    string.Equals(pair.Key, "card", StringComparison.Ordinal) ||
                    string.Equals(pair.Key, "after", StringComparison.Ordinal) ||
                    string.Equals(pair.Key, "before", StringComparison.Ordinal) ||
                    string.Equals(pair.Key, "selectCard", StringComparison.Ordinal) ||
                    string.Equals(pair.Key, "move", StringComparison.Ordinal);
                CollectActionCardIds(pair.Value, add, nestedContainer);
            }
            return;
        }
        if (value is IEnumerable enumerable)
        {
            foreach (object item in enumerable)
            {
                CollectActionCardIds(item, add, insideCardContainer);
            }
        }
    }

    private static bool IsCardIdentityKey(string key)
    {
        return string.Equals(key, "cardId", StringComparison.Ordinal) ||
            string.Equals(key, "originalCardId", StringComparison.Ordinal) ||
            string.Equals(key, "mutationCardId", StringComparison.Ordinal) ||
            string.Equals(key, "preparedCardId", StringComparison.Ordinal) ||
            string.Equals(key, "transformId", StringComparison.Ordinal);
    }

    private static bool TryParseCardId(object raw, out int id)
    {
        id = 0;
        if (raw == null)
        {
            return false;
        }

        string text = raw.ToString();
        if (!int.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out id) || id <= 0)
        {
            id = 0;
            return false;
        }

        int digits = id.ToString(CultureInfo.InvariantCulture).Length;
        return digits >= 6 && digits <= 12;
    }

    private static string FormatMessage(
        string raw,
        LogLevel level,
        string sourceName,
        bool forceCardDetails)
    {
        string normalized = (raw ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');
        bool includeDetails = forceCardDetails ||
            showCardDetails == null || showCardDetails.Value;
        // ShowCardEffects is an explicit privacy/verbosity switch and must
        // always win.  DetailedP2P may force card identity annotations, but it
        // must not silently enable long skill/evolution descriptions when the
        // user has disabled them.
        bool includeEffects = showCardEffects != null && showCardEffects.Value;
        bool detailedAction = forceCardDetails &&
            normalized.IndexOf("[P2P-DETAIL] ACTION", StringComparison.OrdinalIgnoreCase) >= 0;

        // Detailed P2P actions already contain a dedicated card summary table
        // produced from native action fields. Do not run the generic ID regex
        // over that table or append a second "卡牌 ..." block.
        List<CardLogInfo> cards = detailedAction
            ? new List<CardLogInfo>()
            : FindCards(normalized);
        string enriched = includeDetails && !detailedAction
            ? ReplaceCardIds(normalized, cards)
            : normalized;

        StringBuilder result = new StringBuilder();
        // BepInEx already emits the complete [Level:Source] prefix from
        // LogEventArgs.ToStringLine().  Data must contain only the message;
        // adding another level/source prefix here produced lines such as
        // "[Warning:Shadowbus] [WARN] ...".
        result.Append(enriched);

        // Card IDs are already annotated inline as "id<name,费用=...>".
        // Emit an additional multiline block only when the user explicitly
        // requested effect text; otherwise it would repeat the same card name
        // and cost on every ordinary log line.
        if (includeDetails && includeEffects && cards.Count > 0 && !detailedAction)
        {
            foreach (CardLogInfo card in cards)
            {
                result.AppendLine()
                    .Append("  效果卡牌 ").Append(card.Id)
                    .Append(" ｜ ").Append(card.Name);
                AppendWrapped(result, "    效果：", card.Effect);
                AppendWrapped(result, "    进化效果：", card.EvolutionEffect);
            }
        }

        return WrapLines(result.ToString(), level, sourceName);
    }

    private static List<CardLogInfo> FindCards(string text)
    {
        List<CardLogInfo> result = new List<CardLogInfo>();
        if (string.IsNullOrEmpty(text) ||
            (mainThreadId != 0 &&
             Thread.CurrentThread.ManagedThreadId != mainThreadId))
        {
            return result;
        }

        HashSet<int> seen = new HashSet<int>();
        CardMaster master;
        try
        {
            master = CardMaster.GetInstanceForBattle();
            if (!ReferenceEquals(master, cachedCardMaster))
            {
                CardCache.Clear();
                MissingCardCache.Clear();
                cachedCardMaster = master;
            }
        }
        catch (Exception)
        {
            return result;
        }

        foreach (Match match in CardIdPattern.Matches(text))
        {
            if (!int.TryParse(match.Groups[1].Value, out int id) ||
                !seen.Add(id))
            {
                continue;
            }

            if (CardCache.TryGetValue(id, out CardLogInfo cached))
            {
                result.Add(cached);
                continue;
            }
            if (MissingCardCache.Contains(id))
            {
                continue;
            }

            try
            {
                CardParameter parameter = master?.GetCardParameterFromId(id);
                if (parameter == null)
                {
                    if (master != null)
                    {
                        MissingCardCache.Add(id);
                    }
                    continue;
                }

                CardLogInfo card = new CardLogInfo(
                    id,
                    string.IsNullOrWhiteSpace(parameter.CardName)
                        ? "<未命名卡牌>"
                        : parameter.CardName,
                    parameter.Cost,
                    SafeText(() => parameter.SkillDescription),
                    SafeText(() => parameter.EvoSkillDescription));
                CardCache[id] = card;
                result.Add(card);
            }
            catch (Exception)
            {
                // CardMaster may not be initialized during very early startup.
            }
        }
        return result;
    }

    private static string ReplaceCardIds(string text, List<CardLogInfo> cards)
    {
        if (string.IsNullOrEmpty(text) || cards == null || cards.Count == 0)
        {
            return text;
        }

        Dictionary<int, CardLogInfo> lookup = cards.ToDictionary(
            card => card.Id, card => card);
        return CardIdPattern.Replace(text, match =>
        {
            if (!int.TryParse(match.Groups[1].Value, out int id) ||
                !lookup.TryGetValue(id, out CardLogInfo card))
            {
                return match.Value;
            }
            return match.Value + "<" + card.Name + ",費用=" +
                card.Cost + ">";
        });
    }

    private static void AppendWrapped(
        StringBuilder output,
        string label,
        string text)
    {
        if (output == null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        string value = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = value.Split('\n');
        output.AppendLine().Append(label);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                output.AppendLine().Append("      ");
            }
            output.Append(lines[i]);
        }
    }

    private static string WrapLines(
        string text,
        LogLevel level,
        string sourceName)
    {
        int width = wrapWidth?.Value ?? 0;
        if (width <= 0 || string.IsNullOrEmpty(text))
        {
            return text;
        }

        // The configured width applies to the complete visible log line, not
        // just LogEventArgs.Data. Reserve room for BepInEx's prefix so long
        // P2P tables do not run past the console/file viewport after
        // "[Warning:GeorgesZebit.Shadowbus] ".
        width = Math.Max(40, width - EstimateBepInExPrefixLength(level, sourceName));

        StringBuilder result = new StringBuilder();
        foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length <= width)
            {
                if (result.Length > 0)
                {
                    result.AppendLine();
                }
                result.Append(line);
                continue;
            }

            string remaining = line;
            while (remaining.Length > width)
            {
                int split = remaining.LastIndexOf(' ', width - 1);
                if (split < Math.Max(1, width / 2))
                {
                    split = width;
                }
                if (result.Length > 0)
                {
                    result.AppendLine();
                }
                result.Append(remaining.Substring(0, split).TrimEnd());
                remaining = "  " + remaining.Substring(split).TrimStart();
            }
            if (result.Length > 0)
            {
                result.AppendLine();
            }
            result.Append(remaining);
        }
        return result.ToString();
    }

    private static int EstimateBepInExPrefixLength(
        LogLevel level,
        string sourceName)
    {
        string levelName;
        if ((level & LogLevel.Fatal) != 0)
        {
            levelName = "Fatal";
        }
        else if ((level & LogLevel.Error) != 0)
        {
            levelName = "Error";
        }
        else if ((level & LogLevel.Warning) != 0)
        {
            levelName = "Warning";
        }
        else if ((level & LogLevel.Debug) != 0)
        {
            levelName = "Debug";
        }
        else if ((level & LogLevel.Message) != 0)
        {
            levelName = "Message";
        }
        else
        {
            levelName = "Info";
        }

        // Add a small safety margin for BepInEx's alignment spaces and the
        // separator between the prefix and message.
        return ("[" + levelName + ":" + (sourceName ?? "?") + "] ").Length + 2;
    }

    private static string LevelTag(LogLevel level)
    {
        if ((level & LogLevel.Fatal) != 0)
        {
            return "[FATAL]";
        }
        if ((level & LogLevel.Error) != 0)
        {
            return "[ERROR]";
        }
        if ((level & LogLevel.Warning) != 0)
        {
            return "[WARN]";
        }
        if ((level & LogLevel.Debug) != 0)
        {
            return "[DEBUG]";
        }
        if ((level & LogLevel.Message) != 0)
        {
            return "[MESSAGE]";
        }
        return "[INFO]";
    }

    /// <summary>
    /// Writes a formatted event using the BepInEx console driver.  This is
    /// deliberately kept separate from <see cref="LogEventArgs.Data"/>: the
    /// latter is consumed by Unity, disk and third-party listeners that expect
    /// plain text.  Returning true tells the stock ConsoleLogListener that the
    /// event was already written and prevents a duplicate line.
    /// </summary>
    internal static bool TryWriteColoredConsole(LogEventArgs eventArgs)
    {
        if (!IsEnabled || eventArgs == null ||
            ansiColors == null || !ansiColors.Value)
        {
            return false;
        }

        try
        {
            if (!ConsoleManager.ConsoleActive)
            {
                return false;
            }

            TextWriter console = ConsoleManager.ConsoleStream;
            if (console == null)
            {
                return false;
            }

            // ToStringLine includes BepInEx's normal timestamp/source prefix
            // and the enriched, plain-text Data produced by FormatMessage.
            // Strip escapes that may have been emitted by another plugin so
            // they can never leak into the visible console as literal [..m].
            string line = AnsiPattern.Replace(
                eventArgs.ToStringLine() ?? string.Empty,
                string.Empty);

            lock (ConsoleLock)
            {
                ConsoleManager.SetConsoleColor(
                    eventArgs.Level.GetConsoleColor());
                console.Write(line);
                console.Flush();
                // Match BepInEx's normal neutral colour after each event so a
                // non-BepInEx line written by the game is not accidentally
                // inherited from the previous log entry.
                ConsoleManager.SetConsoleColor(ConsoleColor.Gray);
            }
            return true;
        }
        catch (Exception)
        {
            // Logging must never affect gameplay.  Falling back to the stock
            // listener is safer than swallowing the event entirely.
            return false;
        }
    }

    private static string SafeText(Func<string> getter)
    {
        try
        {
            return getter?.Invoke() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void WriteEnhanced(LogEventArgs eventArgs)
    {
        if (!IsEnabled || eventArgs == null)
        {
            return;
        }

        string message = eventArgs.Data?.ToString() ?? string.Empty;
        if (message.Length == 0)
        {
            return;
        }

        string path = outputPath?.Value ?? "BepInEx/LogOutput-enhanced.log";
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(Paths.GameRootPath, path);
        }

        lock (FileLock)
        {
            try
            {
                if (writer == null || !string.Equals(
                        writerPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    writer?.Dispose();
                    string directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }
                    writer = new StreamWriter(
                        new FileStream(path, FileMode.Append, FileAccess.Write,
                            FileShare.ReadWrite), new UTF8Encoding(false))
                    {
                        AutoFlush = true
                    };
                    writerPath = path;
                }

                string timestamp = DateTime.Now.ToString(
                    "yyyy-MM-dd HH:mm:ss.fff");
                writer.WriteLine(
                    timestamp + " " + LevelTag(eventArgs.Level) + " [" +
                    (eventArgs.Source?.SourceName ?? "?") + "] " +
                    AnsiPattern.Replace(message, string.Empty));
            }
            catch (Exception)
            {
                // File logging is best-effort and must never affect gameplay.
            }
        }
    }

    private sealed class CardLogInfo
    {
        internal CardLogInfo(int id, string name, int cost,
            string effect, string evolutionEffect)
        {
            Id = id;
            Name = name;
            Cost = cost;
            Effect = effect ?? string.Empty;
            EvolutionEffect = evolutionEffect ?? string.Empty;
        }

        internal int Id { get; }
        internal string Name { get; }
        internal int Cost { get; }
        internal string Effect { get; }
        internal string EvolutionEffect { get; }
    }

    private sealed class EnhancedLogListener : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            WriteEnhanced(eventArgs);
        }

        public void Dispose()
        {
        }
    }
}

internal static class EnhancedLogPatches
{
    [HarmonyPatch(typeof(BepInEx.Logging.Logger), "InternalLogEvent")]
    [HarmonyPrefix]
    private static void Logger_InternalLogEvent_Prefix(LogEventArgs eventArgs)
    {
        EnhancedLogSystem.Process(eventArgs);
    }

    [HarmonyPatch(typeof(BepInEx.Logging.ConsoleLogListener), "LogEvent")]
    [HarmonyPrefix]
    private static bool ConsoleLogListener_LogEvent_Prefix(
        object sender,
        LogEventArgs eventArgs)
    {
        // When enabled, replace only the console listener's output with a
        // colour-aware write through BepInEx's console driver.  All other
        // listeners (Unity/Player.log, disk and third-party listeners) still
        // receive the same plain-text LogEventArgs instance.
        return !EnhancedLogSystem.TryWriteColoredConsole(eventArgs);
    }
}
