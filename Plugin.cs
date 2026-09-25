using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using HarmonyLib;
using Shadowbus.LLMAI;
using System.Linq;
using UnityEngine;

namespace Shadowbus;

[BepInPlugin("08c8e386-a794-442f-a98c-aec65a183898", "GeorgesZebit.Shadowbus", "2.5.8")]
public class Plugin : BaseUnityPlugin
{
    // 日志：包一层带锁的转发（见 LockedLogSource），避免多线程写日志时整行被插花。
    public static new LockedLogSource Logger;
    public static readonly string ModPath = System.IO.Path.Combine(Paths.GameRootPath, "Mods");
    public static readonly string UnlimitedDeckPath = System.IO.Path.Combine(ModPath, "UnlimitedDecks");
    public static readonly string CardMasterPath = System.IO.Path.Combine(ModPath, "CardMaster");
    public static Plugin Instance { get; private set; }

    /// <summary>
    /// 额外资源目录的覆盖值（[Resources] Root）。空字符串表示自动查找。
    /// </summary>
    public static string ResourceRootOverride { get; private set; } = string.Empty;

    /// <summary>
    /// 是否允许剧情临时语音真的去服务器下载（[Resources] AllowTemporaryVoiceDownload）。
    /// 默认关：离线构建不联网，本地没有的语音就静音。
    /// </summary>
    public static bool AllowTemporaryVoiceDownload { get; private set; }

    /// <summary>
    /// 离线战斗（剧情 / 练习 AI 战）录像，见 <c>[Replay] RecordOfflineBattles</c>。
    /// </summary>
    public static bool RecordOfflineReplays { get; private set; } = true;

    public BattleCardBase SelectedCard { get; set; }

    private ConfigEntry<string> socketIoBindAddress;
    private ConfigEntry<string> socketIoAdvertisedAddress;
    private ConfigEntry<int> socketIoPort;
    private ConfigEntry<float> aiStallTimeout;
    private ConfigEntry<bool> llmAIEnabled;
    private ConfigEntry<string> llmAIEndpoint;
    private ConfigEntry<string> llmAIResponsesEndpoint;
    private ConfigEntry<string> llmAIChatCompletionsEndpoint;
    private ConfigEntry<string> llmAIApiMode;
    private ConfigEntry<string> llmAIApiKey;
    private ConfigEntry<string> llmAIModel;
    private ConfigEntry<string> llmAIReasoningEffort;
    private ConfigEntry<float> llmAITimeout;
    private ConfigEntry<int> llmAIMaxCandidates;
    private ConfigEntry<int> llmAIMaxPlanSteps;
    private ConfigEntry<int> llmAIMaxApiCallsPerTurn;
    private ConfigEntry<int> llmAIMaxResponseTokens;
    private ConfigEntry<int> llmAIMaxOutputTokens;
    private ConfigEntry<int> llmAILethalSearchMaxPatterns;
    private ConfigEntry<int> llmAILethalSearchBudgetMs;
    private ConfigEntry<string> llmAIPromptFile;
    private ConfigEntry<bool> llmAIDebugLogPayloads;
    private ConfigEntry<float> aiUnknownCardPlayBonusMin;
    private ConfigEntry<float> aiUnknownCardPlayBonusMax;
    private ConfigEntry<bool> aiPriceUnpricedCards;
    private ConfigEntry<bool> aiRespectPlayLimitLocks;
    private ConfigEntry<int> aiLowLifeHealThreshold;
    private ConfigEntry<bool> bossRushAbilityPicker;
    private ConfigEntry<string> resourceRootOverride;
    private ConfigEntry<bool> allowTemporaryVoiceDownload;
    private ConfigEntry<bool> recordOfflineBattles;

    private void Awake()
    {
        Instance = this;
        // Plugin startup logic
        Logger = new LockedLogSource(base.Logger);
        Logger.LogInfo($"Plugin Shadowbus is loaded!");
        // 构建时间横幅：判断日志是不是最新 DLL 产生的，一眼就能看出来
        // （之前出现过"代码改了但日志里没有新日志"，就是因为加载的还是旧 dll）。
        try
        {
            Logger.LogInfo(
                $"[Shadowbus] build={System.IO.File.GetLastWriteTimeUtc(
                    System.Reflection.Assembly.GetExecutingAssembly().Location)
                    .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)}Z, " +
                $"version={System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");
        }
        catch (System.Exception exception)
        {
            Logger.LogWarning($"[Shadowbus] Could not report the build stamp: {exception.Message}");
        }

        // 资源目录：留空就自动找（游戏目录同级的 Resources 等），
        // 也可以指到任意位置、任意名字的文件夹。相对路径按游戏目录解析。
        resourceRootOverride = Config.Bind(
            "Resources",
            "Root",
            string.Empty,
            "Extra resource folder. Empty auto-detects: <game>/../Resources, <game>/Resources, " +
            "<game>/Shadowbus/Resources, then the SHADOWBUS_RESOURCES environment variable. " +
            "A relative path is resolved against the game folder.");
        ResourceRootOverride = resourceRootOverride.Value ?? string.Empty;

        // 剧情临时语音（v/t/ 里的那些）默认不去服务器下载：本地没有的就静音。
        // 想自己把语音下下来（下完可以随整合包分发），把它改成 true。
        allowTemporaryVoiceDownload = Config.Bind(
            "Resources",
            "AllowTemporaryVoiceDownload",
            false,
            "Let the game download the story's temporary voice banks (v/t/*.acb) from the official " +
            "server again. Off by default: an offline build cannot fetch them, so those lines stay " +
            "silent. Turn it on while online and play the chapter; the ACBs land in the resource " +
            "folder's v/t/ and can then be shipped with the pack.");
        AllowTemporaryVoiceDownload = allowTemporaryVoiceDownload.Value;

        // 离线战斗录像（游戏自己的「其他 → 回放」）。关掉之后插件不再接管录像器：
        // 战斗起手/结束时那条录像链路整条不跑，排查卡顿时可以用来做 A/B。
        recordOfflineBattles = Config.Bind(
            "Replay",
            "RecordOfflineBattles",
            true,
            "Record offline (story / practice AI) battles into <resource root>/NewReplay so they can be " +
            "played back from the game's own replay menu. Turn it off to check whether replay recording " +
            "is involved in a battle-start stutter or freeze.");
        RecordOfflineReplays = recordOfflineBattles.Value;

        socketIoBindAddress = Config.Bind(
            "SocketIO",
            "BindAddress",
            "0.0.0.0",
            "Address used by the Socket.IO listener.");
        socketIoAdvertisedAddress = Config.Bind(
            "SocketIO",
            "AdvertisedAddress",
            string.Empty,
            "Address embedded in the room code. Empty auto-detects a reachable local adapter.");
        socketIoPort = Config.Bind(
            "SocketIO",
            "Port",
            29600,
            "Socket.IO port used by room hosting.");
        // 联机服务器配置（新系统）
        Server.OnlineRuntime.Initialize(new Server.Core.ServerConfig
        {
            BindAddress = socketIoBindAddress.Value,
            AdvertisedAddress = socketIoAdvertisedAddress.Value,
            Port = socketIoPort.Value
        });
        aiStallTimeout = Config.Bind(
            "AI",
            "StallTimeoutSeconds",
            30f,
            "Seconds the enemy AI may make no progress before its turn is force ended. Use 0 to disable.");
        AITurnGuard.Configure(aiStallTimeout.Value);
        llmAIEnabled = Config.Bind("LLMAI", "Enabled", false,
            "Default state of the in-game LLM AI switch for new custom practice battles.");
        llmAIEndpoint = Config.Bind("LLMAI", "Endpoint", "https://api.openai.com/v1/chat/completions",
            "Backward-compatible API endpoint or /v1 base. Auto derives Responses and Chat Completions URLs.");
        llmAIResponsesEndpoint = Config.Bind("LLMAI", "ResponsesEndpoint", string.Empty,
            "Optional Responses API endpoint override.");
        llmAIChatCompletionsEndpoint = Config.Bind("LLMAI", "ChatCompletionsEndpoint", string.Empty,
            "Optional Chat Completions endpoint override.");
        llmAIApiMode = Config.Bind("LLMAI", "ApiMode", "Auto",
            "API mode: Auto, Responses, or ChatCompletions. Auto prefers Responses.");
        llmAIApiKey = Config.Bind("LLMAI", "ApiKey", string.Empty,
            "Bearer API key. This value is never written to the log or model payload.");
        llmAIModel = Config.Bind("LLMAI", "Model", string.Empty,
            "Model name.");
        llmAIReasoningEffort = Config.Bind("LLMAI", "ReasoningEffort", "high",
            "Reasoning effort: none, minimal, low, medium, high, or xhigh. Empty omits the field.");
        llmAITimeout = Config.Bind("LLMAI", "TimeoutSeconds", 12f,
            "Timeout for one model request.");
        llmAIMaxCandidates = Config.Bind("LLMAI", "MaxCandidates", 512,
            "Maximum legal actions at one simulated node before falling back to the original AI.");
        llmAIMaxPlanSteps = Config.Bind("LLMAI", "MaxPlanSteps", 12,
            "Maximum number of actions accepted in one TurnPlan.");
        llmAIMaxApiCallsPerTurn = Config.Bind("LLMAI", "MaxApiCallsPerTurn", 12,
            "Maximum model calls during one turn, including replans.");
        llmAIMaxResponseTokens = Config.Bind("LLMAI", "MaxResponseTokens", 768,
            "Legacy Chat Completions maximum response tokens.");
        llmAIMaxOutputTokens = Config.Bind("LLMAI", "MaxOutputTokens", 4096,
            "Responses API output budget shared by reasoning and final JSON.");
        llmAILethalSearchMaxPatterns = Config.Bind("LLMAI", "LethalSearchMaxPatterns", 32,
            "Maximum original-AI play patterns checked by local lethal search per decision state.");
        llmAILethalSearchBudgetMs = Config.Bind("LLMAI", "LethalSearchBudgetMs", 1000,
            "Total local lethal-search budget in milliseconds per decision state.");
        llmAIPromptFile = Config.Bind("LLMAI", "PromptFile", "Mods/AIData/llm_prompt.txt",
            "Optional prompt path relative to the game root. A built-in prompt is used when absent.");
        llmAIDebugLogPayloads = Config.Bind("LLMAI", "DebugLogPayloads", false,
            "Log public model payloads and responses. Authorization is never logged.");
        if (!LLMEndpointResolver.TryParseMode(llmAIApiMode.Value, out LLMApiMode apiMode))
        {
            Logger.LogWarning($"[LLMAI] Invalid ApiMode '{llmAIApiMode.Value}'; using Auto.");
            apiMode = LLMApiMode.Auto;
        }
        LLMAITurnController.Configure(new LLMAISettings
        {
            Enabled = llmAIEnabled.Value,
            Endpoint = llmAIEndpoint.Value,
            ResponsesEndpoint = llmAIResponsesEndpoint.Value,
            ChatCompletionsEndpoint = llmAIChatCompletionsEndpoint.Value,
            ApiMode = apiMode,
            ApiKey = llmAIApiKey.Value,
            Model = llmAIModel.Value,
            ReasoningEffort = llmAIReasoningEffort.Value,
            TimeoutSeconds = System.Math.Max(1f, llmAITimeout.Value),
            MaxCandidates = System.Math.Max(1, llmAIMaxCandidates.Value),
            MaxPlanSteps = System.Math.Max(1, llmAIMaxPlanSteps.Value),
            MaxApiCallsPerTurn = System.Math.Max(1, llmAIMaxApiCallsPerTurn.Value),
            MaxResponseTokens = System.Math.Max(64, llmAIMaxResponseTokens.Value),
            MaxOutputTokens = System.Math.Max(64, llmAIMaxOutputTokens.Value),
            LethalSearchMaxPatterns = System.Math.Max(0, llmAILethalSearchMaxPatterns.Value),
            LethalSearchBudgetMs = System.Math.Max(0, llmAILethalSearchBudgetMs.Value),
            PromptFile = llmAIPromptFile.Value,
            DebugLogPayloads = llmAIDebugLogPayloads.Value
        });
        aiUnknownCardPlayBonusMin = Config.Bind(
            "AI",
            "UnknownCardPlayBonusMin",
            0.5f,
            "Lowest play bonus given to a card that has no AI data. Set both bounds to 0 to keep the crash fix but stop the AI from playing such cards.");
        aiUnknownCardPlayBonusMax = Config.Bind(
            "AI",
            "UnknownCardPlayBonusMax",
            1.5f,
            "Highest play bonus given to a card that has no AI data. The original data keeps most numeric play bonuses between 0 and 2.");
        aiPriceUnpricedCards = Config.Bind(
            "AI",
            "PriceUnpricedCards",
            true,
            "Score spells and amulets whose AI tags describe an effect but never give it a value. Without this the AI leaves them in hand for the whole game.");
        aiRespectPlayLimitLocks = Config.Bind(
            "AI",
            "RespectPlayLimitLocks",
            false,
            "Leave cards the original data locked with a playLimit tag unpriced. Only 4 cards are both locked and unpriced, and all sit far below their threshold, so this changes nothing today; it guards against a future card whose threshold a synthesized bonus could cross.");
        aiLowLifeHealThreshold = Config.Bind(
            "AI",
            "LowLifeHealThreshold",
            10,
            "Leader healing only scores when the AI is at this much life or less. Use 0 to score it like any other unpriced effect.");
        AICardDataFallback.Configure(
            aiUnknownCardPlayBonusMin.Value,
            aiUnknownCardPlayBonusMax.Value,
            aiPriceUnpricedCards.Value,
            aiRespectPlayLimitLocks.Value,
            aiLowLifeHealThreshold.Value);
        bossRushAbilityPicker = Config.Bind(
            "BossRush",
            "AbilityPicker",
            true,
            "Shows a 随便选 button on the BossRush ability select screen that offers every configured buff instead of the three random candidates. Set to false to hide the button and keep the original random selection.");
        BossRushAbilityPicker.Configure(bossRushAbilityPicker.Value);
        CustomFormats.Initialize();
        BossRushOfflineData.Initialize();
        BossRushReferenceExporter.Export();

        try
        {
            var harmony = new Harmony("GeorgesZebit.Shadowbus");
            try
            {
                // 最先做：把游戏里所有 Application.persistentDataPath 换到额外资源目录，
                // 越早越好（游戏读资源之前）。资源目录是后来才放进来的时候，
                // Plugin.Update 里的 Tick 会补上这一步。
                PersistentDataPathRedirect.SetHarmony(harmony);
                PersistentDataPathRedirect.Apply(harmony);

                // 紧接着：把 PlayerPrefs（注册表 HKCU\Software\Cygames\Shadowverse）
                // 搬到额外资源目录里的 PlayerPrefs.txt，游戏从一开始就读到本地值。
                PlayerPrefsRedirect.SetHarmony(harmony);
                PlayerPrefsRedirect.Apply(harmony);
            }
            catch (System.Exception exception)
            {
                Logger.LogError($"[Resources] FAILED to redirect persistentDataPath: {exception}");
            }
            Harmony.CreateAndPatchAll(typeof(DebugPatcher));
            Harmony.CreateAndPatchAll(typeof(DeckEdit));
            Harmony.CreateAndPatchAll(typeof(CardMasterPatcher));
            try
            {
                // Isolated: a reference dump must never block the card master.
                Harmony.CreateAndPatchAll(typeof(CardSkillExporter));
            }
            catch (System.Exception exception)
            {
                Logger.LogError($"[CardSkill] FAILED to apply the card skill export patch: {exception}");
            }
            Harmony.CreateAndPatchAll(typeof(Offlinizer));
            var deckListHotReloadHarmony = Harmony.CreateAndPatchAll(typeof(DeckListHotReload));
            Logger.LogInfo(
                $"[DeckListHotReload] Harmony registration complete: " +
                $"{deckListHotReloadHarmony.GetPatchedMethods().Count()} game method(s) patched.");
            Harmony.CreateAndPatchAll(typeof(FakeConnect));
            Harmony.CreateAndPatchAll(typeof(Server.OnlineRoomInputPatch));
            Harmony.CreateAndPatchAll(typeof(Server.SocketIoProfilePatch));
            Harmony.CreateAndPatchAll(typeof(Server.SocketIoCreateRoomIdentityPatch));
            Harmony.CreateAndPatchAll(typeof(Server.SocketIoEnterRoomIdentityPatch));
            Harmony.CreateAndPatchAll(typeof(Server.SocketIoCertificationViewerIdPatch));
            Harmony.CreateAndPatchAll(typeof(Server.SocketIoBattleDeckPatch));
            try
            {
                // Read-only diagnostics for hidden-card resolution. This is isolated so
                // a game-version signature change cannot affect other patches.
                var hiddenCardDiagnosticsHarmony =
                    Harmony.CreateAndPatchAll(typeof(BattleHiddenCardDiagnostics));
                Logger.LogInfo(
                    $"[HiddenDiag] Harmony registration complete: " +
                    $"{hiddenCardDiagnosticsHarmony.GetPatchedMethods().Count()} game method(s) patched.");
            }
            catch (System.Exception exception)
            {
                Logger.LogError($"[HiddenDiag] FAILED to apply diagnostics: {exception}");
            }
            Harmony.CreateAndPatchAll(typeof(BossRushPatches));
            try
            {
                // Isolated: an optional testing aid must not take down the rest of
                // the BossRush patches if the ability select screen changes.
                Harmony.CreateAndPatchAll(typeof(BossRushAbilityPicker));
            }
            catch (System.Exception exception)
            {
                Logger.LogError($"[BossRush] FAILED to apply the ability picker patch: {exception}");
            }
            Harmony.CreateAndPatchAll(typeof(AIManager));
            Harmony.CreateAndPatchAll(typeof(LLMAIPatches));
            try
            {
                Harmony.CreateAndPatchAll(typeof(PracticeDualAI));
            }
            catch (System.Exception exception)
            {
                Logger.LogError($"[AIManager] FAILED to apply the dual-practice AI patches: {exception}");
            }
            Harmony.CreateAndPatchAll(typeof(BossRushReferenceExporter));
            try
            {
                // Isolated: these patches bind to private and virtual game methods, and a
                // binding failure must not take down the patches that follow.
                Harmony.CreateAndPatchAll(typeof(AITurnGuard));
            }
            catch (System.Exception exception)
            {
                Logger.LogError($"[AITurnGuard] FAILED to apply the AI stall patches: {exception}");
            }
            try
            {
                Harmony.CreateAndPatchAll(typeof(AICardDataFallback));
            }
            catch (System.Exception exception)
            {
                Logger.LogError($"[AICardData] FAILED to apply the AI card data fallback patch: {exception}");
            }
            try
            {
                // Guards cards that carry no AI data at all. Without it the original
                // EvaluatePlayValue dereferences a null AIData record.
                Harmony.CreateAndPatchAll(typeof(AIEvaluateTagCompatibility));
            }
            catch (System.Exception exception)
            {
                Logger.LogError($"[AICardData] FAILED to apply the AI evaluate compatibility patch: {exception}");
            }
            Harmony.CreateAndPatchAll(typeof(ActiveSkill));
            Harmony.CreateAndPatchAll(typeof(GeminizeSkillPatcher));
            Harmony.CreateAndPatchAll(typeof(AcquireSkillsSkillPatcher));
            Harmony.CreateAndPatchAll(typeof(MirrorSkillPatcher));
            Harmony.CreateAndPatchAll(typeof(MirrorResidentEffectPatcher));
            Harmony.CreateAndPatchAll(typeof(StoryOfflinePatches));
            try
            {
                // Story script battles take their enemy AI deck/style/emote from local
                // files; the count is logged so a silent registration failure is visible.
                var storyBattleAiHarmony =
                    Harmony.CreateAndPatchAll(typeof(StoryBattleAiPatches));
                Logger.LogInfo(
                    $"[StoryAI] Harmony registration complete: " +
                    $"{storyBattleAiHarmony.GetPatchedMethods().Count()} game method(s) patched.");
            }
            catch (System.Exception exception)
            {
                Logger.LogError($"[StoryAI] FAILED to apply the story AI patches: {exception}");
            }
            Harmony.CreateAndPatchAll(typeof(LanguageVoicePatches));
            Harmony.CreateAndPatchAll(typeof(LanguageSelectionPatches));
            Harmony.CreateAndPatchAll(typeof(StoryTextLanguagePatches));
            Harmony.CreateAndPatchAll(typeof(LocalCardVoicePatches));
            Harmony.CreateAndPatchAll(typeof(BattleVfxGuards));
            Harmony.CreateAndPatchAll(typeof(BattleLogPatch));
            Harmony.CreateAndPatchAll(typeof(HomeMenuPatches));
            Harmony.CreateAndPatchAll(typeof(MyPageOtherPatches));
            Harmony.CreateAndPatchAll(typeof(ReplayOfflineData));
            // 临时诊断（定位完就删）：回放播放停在换牌界面时打印 op / 换牌 / 开战 / 暂停日志。
            Harmony.CreateAndPatchAll(typeof(ReplayPlaybackTrace));
            // 回放兜底：融合这类"记录与状态对不上"的操作不再把整场回放钉死。
            Harmony.CreateAndPatchAll(typeof(ReplayPlaybackGuards));
            // 失焦诊断：窗口一失焦游戏就会暂停对局并屏蔽战斗输入（网络与回合计时器照跑），
            // 这条日志让玩家反馈的"联机卡死"一眼就能定性。
            Harmony.CreateAndPatchAll(typeof(FocusDiagnostics));
            Harmony.CreateAndPatchAll(typeof(ProfileOfflineData));
            Harmony.CreateAndPatchAll(typeof(ResourceRootPatches));
            Harmony.CreateAndPatchAll(typeof(DeckFormatUI));
            try
            {
                // 结算界面是所有模式共用的 prefab：把「任务」按钮整个删掉。
                var battleResultUiHarmony =
                    Harmony.CreateAndPatchAll(typeof(BattleResultUiPatches));
                Logger.LogInfo(
                    $"[BattleUI] Harmony registration complete: " +
                    $"{battleResultUiHarmony.GetPatchedMethods().Count()} game method(s) patched.");
            }
            catch (System.Exception exception)
            {
                Logger.LogError($"[BattleUI] FAILED to apply the battle result UI patches: {exception}");
            }
            Harmony.CreateAndPatchAll(typeof(RoomRuleSelectDialogCreatePatch));
            Harmony.CreateAndPatchAll(typeof(RoomRuleSelectDialogInitializePatch));
            Harmony.CreateAndPatchAll(typeof(RoomRuleSelectDialogDefaultSettingPatch));
            Harmony.CreateAndPatchAll(typeof(RoomRuleSelectDialogRefreshPatch));
            Harmony.CreateAndPatchAll(typeof(RoomRuleSelectDialogFormatButtonPatch));
            Harmony.CreateAndPatchAll(typeof(MyPageItemBattleRoomFormatResetPatch));
            Harmony.CreateAndPatchAll(typeof(PlayerControllerForOwnSelectDeckFormatPatch));
            Harmony.CreateAndPatchAll(typeof(RoomRuleSettingTopBarFormatPatch));
            Harmony.CreateAndPatchAll(typeof(GuestRoomUnlimitedBasePatch));
            Harmony.CreateAndPatchAll(typeof(LocalDeckCodePatches));
            var deckFormatRulesHarmony =
                Harmony.CreateAndPatchAll(typeof(CustomFormatDeckEditRules));
            Logger.LogInfo(
                $"[CustomFormats] Deck edit rule registration complete: " +
                $"{deckFormatRulesHarmony.GetPatchedMethods().Count()} game method(s) patched.");

        }
        catch (System.Exception exception)
        {
            Logger.LogError($"Harmony - FAILED to Apply Patch(s): {exception}");
        }
    }

    private void Update()
    {
        // 资源文件夹可能是开游戏之后才放进来的：发现之后在这里补打重定向补丁。
        PersistentDataPathRedirect.Tick();
        PlayerPrefsRedirect.Tick();
        Server.OnlineRuntime.Update();
        TaskWatchdog.Tick();
        PracticeDualAI.Update();
        AITurnGuard.Update();
        AICardDataFallback.Update();
    }

    private void OnDestroy()
    {
        LocalCardVoicePatches.Clear();
        LLMAITurnController.CancelAll("plugin_destroyed");
        Server.OnlineRuntime.Shutdown();
    }
}
