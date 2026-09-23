using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Cute;
using HarmonyLib;
using LitJson;
using Newtonsoft.Json;
using UnityEngine;
using Wizard;
using Wizard.Battle.Mulligan;
using Wizard.Battle.Replay;
using Wizard.BattleMgr;

namespace Shadowbus
{
    /// <summary>
    /// 本地回放（「其他 → 回放」）。
    ///
    /// 游戏自己就有整套新回放机制，只是离线战斗默认不录像：
    ///
    ///   · <c>NetworkBattleReplayOperationRecorder</c> 把一局录进
    ///     <c>&lt;persistentDataPath&gt;/NewReplay/&lt;battleId&gt;/</c>（AES 加密的 JSON：replay_info.json /
    ///     replay_turn_start.json / replay_battle_log.json …），最多保留 30 局；
    ///   · <c>ReplayDialogContent.GoReplay</c> 先找这个目录，找到就本地播放
    ///     （<c>ReplayController.StartPlayReplay(..., isNewReplay: true, battleId)</c>），
    ///     找不到才去服务器要 <c>ReplayDetailTask</c>。
    ///
    /// 缺的只有两件事，这里都补上：
    ///   1. 离线（剧情 / 练习等 AI 对战）用的是 <c>NullReplayRecordManager</c>，什么都不写 ——
    ///      改成真正的 <see cref="ReplayRecordManager"/>；顺带给录不到 battle_id 的离线局
    ///      生成一个 12 位数字 id（小于 14 位，不会被录像器自己的“清理时间戳目录”逻辑删掉）。
    ///   2. 回放列表来自服务器的 <c>ReplayInfoTask</c> —— 离线改成扫本地录像目录拼出来。
    ///      <c>ReplayDetailTask</c> 也一并接管（本地有录像时游戏根本不会走到它）。
    /// </summary>
    internal static class ReplayOfflineData
    {
        private const string InfoTaskName = "ReplayInfoTask";
        private const string DetailTaskName = "ReplayDetailTask";
        private const string ReplayInfoFile = "replay_info.json";

        /// <summary>NetworkDefine.ServerBattleType.Story —— 回放器只认 31/32/33 那三个房间类型，其余一律按房间战走。</summary>
        private const int OfflineBattleType = 2;

        private const int MaxItems = 999;      // 录像 / 列表的保留上限（原版是 30）

        /// <summary>
        /// 离线战斗录像。两个坑都得补，都已经定位：
        ///
        /// · <c>ReplayRecordManager.SetupRecorderEvents</c> 最后会调 <c>RecordBattleStartInfo()</c>，
        ///   其中 <c>NetworkUserInfoData.GetSelfDeck()</c> 在非观战局里会返回 null（AI 战没人填过它），
        ///   直接空引用 —— 所以开录之前先用战斗里双方的开局牌组把它填上；
        /// · 真正写文件的 <c>BattleFinishWriteJsonData</c> 只挂在
        ///   <c>NetworkStandardBattleMgr.OnBattleFinish</c> 上，AI 战不会触发 ——
        ///   这里挂到 <c>AINetworkBattleManager.InitiateGameEndSequence(hasWon)</c> 上。
        ///
        /// 总开关见配置 <c>[Replay] RecordOfflineBattles</c>；关掉时这条链路整个不跑，
        /// 排查「战斗加载卡死」时可以拿来 A/B。
        /// </summary>
        private static bool RecordingEnabled => Plugin.RecordOfflineReplays;

        private static readonly ConditionalWeakTable<NetworkBattleReplayOperationRecorder, string> GeneratedIds =
            new ConditionalWeakTable<NetworkBattleReplayOperationRecorder, string>();

        private static ReplayRecordManager _recorder;
        private static BattleManagerBase _recorderOwner;
        private static BattleManagerBase _pendingBattle;
        private static bool _loggedRecording;
        private static bool _loggedRecordingDisabled;
        private static bool _useGeneratedBattleId;

        internal static string ReplayRoot
        {
            get
            {
                string root = ResourceRootPatches.ResourceRoot;
                return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "NewReplay");
            }
        }

        // ---------------------------------------------------------------- 录像开关

        [HarmonyPatch(typeof(BattleManagerBase), "StartReplayRecording")]
        [HarmonyPostfix]
        internal static void StartReplayRecording_Postfix(BattleManagerBase __instance)
        {
            if (!ShouldRecord(__instance))
            {
                return;
            }

            // 这里太早了：BattlePlayer/BattleEnemy 还没拿到开局牌组（实测 self=0 / enemy=0），
            // 录像器的 SetupRecorderEvents 会直接空引用。改成记下来，等 StartBattle 再开录。
            _pendingBattle = __instance;
        }

        /// <summary>
        /// 单人 / 练习战（<c>SingleBattleMgr</c>）：它是练习赛实际用的管理器，
        /// <c>SetupInitialGameState</c> 时双方牌组已就位，从这里开录；结算在 <c>FinishBattle</c>。
        /// </summary>
        [HarmonyPatch(typeof(SingleBattleMgr), nameof(SingleBattleMgr.SetupInitialGameState))]
        [HarmonyPostfix]
        internal static void SingleBattleMgr_SetupInitialGameState_Postfix(SingleBattleMgr __instance)
        {
            StartRecording(__instance);
        }

        [HarmonyPatch(typeof(SingleBattleMgr), nameof(SingleBattleMgr.FinishBattle))]
        [HarmonyPostfix]
        internal static void SingleBattleMgr_FinishBattle_Postfix(SingleBattleMgr __instance)
        {
            bool hasWon = __instance.BattlePlayer == null || !__instance.BattlePlayer.Class.IsDead;
            FinishRecording(hasWon, "SingleBattleMgr.FinishBattle/" + __instance.GetType().Name);
        }

        /// <summary>
        /// 所有战斗管理器结算都会走这里，兜一层，避免某个管理器漏了 FinishBattle。
        ///
        /// 但 <c>JudgeBattleResult()</c> 返回的是一个 Vfx 序列：它每次**出牌/攻击**都会被新建一次
        /// （<c>BattleManagerBase.SetupActionProcessorEvent</c> 里的那几个回调），战斗刚开始摆完手牌
        /// 就会触发第一次 —— 那时候双方主战者都还活着，直接落盘等于把一局空录像写出去
        /// （日志里能看到 "recording enabled" 紧跟着一行 "recording finished"）。
        /// 所以只有真的分出胜负（一方主战者阵亡）才认这一次结算。
        /// </summary>
        [HarmonyPatch(typeof(BattleManagerBase), nameof(BattleManagerBase.JudgeBattleResult))]
        [HarmonyPostfix]
        internal static void JudgeBattleResult_Postfix(BattleManagerBase __instance)
        {
            if (!IsBattleOver(__instance))
            {
                return;
            }

            FinishRecording(!__instance.BattlePlayer.Class.IsDead, "JudgeBattleResult/" + __instance.GetType().Name);
        }

        /// <summary>主战者已经倒下（或者压根没建起来）才算这局结束了。</summary>
        private static bool IsBattleOver(BattleManagerBase battleMgr)
        {
            try
            {
                if (battleMgr == null || battleMgr.BattlePlayer == null)
                {
                    return false;
                }

                return battleMgr.BattlePlayer.Class.IsDead ||
                       (battleMgr.BattleEnemy != null && battleMgr.BattleEnemy.Class.IsDead);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 最后的保险：练习战的结算任务是我们自己离线应答的，它一被解析就说明这局打完了，
        /// 这时候如果还在录像就把它落盘（<see cref="FinishRecording"/> 自带「只写一次」）。
        /// </summary>
        [HarmonyPatch(typeof(PracticeFinishTask), "Parse")]
        [HarmonyPostfix]
        internal static void PracticeFinishTask_Parse_Postfix()
        {
            CommitIfRecording("PracticeFinishTask.Parse");
        }

        internal static void CommitIfRecording(string source)
        {
            if (_recorder == null)
            {
                return;
            }

            bool hasWon = true;
            try
            {
                BattleManagerBase battleMgr = BattleManagerBase.GetIns();
                if (battleMgr != null && battleMgr.BattlePlayer != null)
                {
                    hasWon = !battleMgr.BattlePlayer.Class.IsDead;
                }
            }
            catch (Exception)
            {
            }

            FinishRecording(hasWon, source);
        }

        /// <summary>
        /// 原版 <c>RecordBattleFinish</c> 第一行就是
        /// <c>BattleManagerBase.GetIns() as NetworkBattleManagerBase</c> —— 单人 / 练习战是
        /// <c>SingleBattleMgr</c>，转不过去拿到 null，下一行直接空引用（实测栈就是这里）。
        /// 离线局由我们照原样写这条结算记录，联机战仍走原版。
        /// </summary>
        [HarmonyPatch(typeof(NetworkBattleReplayOperationRecorder), "RecordBattleFinish")]
        [HarmonyPrefix]
        internal static bool RecordBattleFinish_Prefix(
            NetworkBattleReplayOperationRecorder __instance,
            bool isWin)
        {
            BattleManagerBase battleMgr = BattleManagerBase.GetIns();
            if (battleMgr == null || battleMgr is NetworkBattleManagerBase)
            {
                return true;
            }

            try
            {
                if (__instance.RecordStatusPanel(battleMgr.BattlePlayer, battleMgr.BattleEnemy))
                {
                    __instance.RecordBattleInfo(battleMgr.BattlePlayer, battleMgr.BattleEnemy);
                    __instance.RecordClassInformationUi(
                        battleMgr.BattlePlayer, battleMgr.BattleEnemy, __instance._isAfterTurnEndFinish);
                }

                JsonData result = new JsonData();
                result["0"] = 89;
                result["81"] = isWin ? 1 : 0;
                result["82"] = battleMgr.BattlePlayer.Class.IsDead ? 1 : 0;
                result["83"] = battleMgr.BattleEnemy.Class.IsDead ? 1 : 0;
                result["84"] = 0;      // GetFinishTypeByStatus 只在网络管理器上，离线沿用 0
                result["85"] = 0;      // JudgeResultReceiveCode 同上
                __instance._currentOperationJsonData.Add(result);
                __instance.RecordCurrentOperationJsonData(false);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Offline battle-finish record failed: {exception}");
            }

            return false;
        }

        private static void StartRecording(BattleManagerBase battleMgr)
        {
            if (!RecordingEnabled || battleMgr == null)
            {
                return;
            }

            if (!ReferenceEquals(_pendingBattle, battleMgr))
            {
                return;      // 这一局不是我们接管的那一局
            }

            _pendingBattle = null;

            PerfTrace.Scope scope = PerfTrace.Enter("ReplayStartRecording");
            try
            {
                ReplayRecordManager recorder = RecorderFor(battleMgr);
                EnsureNetworkUserInfo(battleMgr);
                EnsureDecks(battleMgr);

                // 离线局没有 battle_id：让 GetBattleId 走我们自己生成的那个。
                _useGeneratedBattleId = true;
                recorder.SetupRecording(battleMgr);

                if (!_loggedRecording)
                {
                    _loggedRecording = true;
                    Plugin.Logger.LogInfo(
                        $"[Replay] Replay recording enabled for offline battles; files go to {ReplayRoot}.");
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Could not enable replay recording: {exception}");
            }
            finally
            {
                scope.Dispose();
            }
        }

        private static void FinishRecording(bool hasWon, string source)
        {
            ReplayRecordManager recorder = _recorder;
            if (!RecordingEnabled || recorder == null)
            {
                return;
            }

            // 只写一次：不管成功失败都摘掉，免得结算被多次触发时反复重试刷日志。
            // 注意 _useGeneratedBattleId 必须等写完再清 —— 写文件过程中 RenameDirectoryToBattleId
            // 会调 GetBattleId，那时候还得靠我们的 Prefix 顶着，否则原版又会空引用。
            using (PerfTrace.Enter("ReplayFinishRecording(" + source + ")"))
            {
                try
                {
                    recorder._recorder?.BattleFinishWriteJsonData(hasWon);
                    Plugin.Logger.LogInfo(
                        $"[Replay] Replay recording finished (win={hasWon}, from {source}).");
                }
                catch (Exception exception)
                {
                    Plugin.Logger.LogWarning($"[Replay] Could not finish the replay recording: {exception}");
                }
                finally
                {
                    _recorder = null;
                    _recorderOwner = null;
                    _useGeneratedBattleId = false;
                }
            }
        }

        /// <summary>
        /// AI 战真正开打（开局演出 / 抽牌之前）的时机：双方牌组已经就位，这时候开录最合适。
        /// </summary>
        [HarmonyPatch(typeof(AINetworkBattleManager), nameof(AINetworkBattleManager.StartBattle))]
        [HarmonyPostfix]
        internal static void StartBattle_Postfix(AINetworkBattleManager __instance)
        {
            StartRecording(__instance);
        }

        [HarmonyPatch(typeof(BattleManagerBase), "SetupReplayBattleInfoFilter")]
        [HarmonyPostfix]
        internal static void SetupReplayBattleInfoFilter_Postfix(BattleManagerBase __instance)
        {
            if (!ShouldRecord(__instance))
            {
                return;
            }

            try
            {
                RecorderFor(__instance).SetupBattleInfoFilter();
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Could not set up the battle info filter: {exception.Message}");
            }
        }

        [HarmonyPatch(typeof(NetworkBattleManagerBase), "SetupReplayRecordingEvent")]
        [HarmonyPostfix]
        internal static void SetupReplayRecordingEvent_Postfix(NetworkBattleManagerBase __instance)
        {
            if (!ShouldRecord(__instance))
            {
                return;
            }

            try
            {
                RecorderFor(__instance).SetupOperateMgrEvents(__instance);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Could not set up the replay events: {exception.Message}");
            }
        }

        /// <summary>
        /// 离线战斗没有 battle_id，原版 <c>GetBattleId()</c> 在离线路径上会空引用（实测栈就是这里），
        /// 所以接管录像时直接**跳过原版**，返回一个 12 位数字 id 当目录名。
        /// 同一个录像器只生成一次。
        /// </summary>
        [HarmonyPatch(typeof(NetworkBattleReplayOperationRecorder), "GetBattleId")]
        [HarmonyPrefix]
        internal static bool GetBattleId_Prefix(
            NetworkBattleReplayOperationRecorder __instance,
            ref string __result)
        {
            if (!_useGeneratedBattleId || __instance == null || ReplayRoot == null)
            {
                return true;      // 联机战 / 别人的录像器：走原版
            }

            __result = GeneratedIds.GetValue(__instance, _ => NextBattleId());
            return false;
        }

        /// <summary>
        /// 录像器构造函数里写死了 <c>while (目录数 &gt;= 30) 删最旧</c>。这里把那个常量换成
        /// <see cref="MaxItems"/>（999），让它能留 999 局；列表读取上限也用同一个数。
        /// </summary>
        [HarmonyPatch(typeof(NetworkBattleReplayOperationRecorder), MethodType.Constructor)]
        [HarmonyTranspiler]
        internal static IEnumerable<CodeInstruction> RecorderConstructor_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Ldc_I4_S &&
                    instruction.operand is sbyte small && small == 30)
                {
                    instruction.opcode = OpCodes.Ldc_I4;
                    instruction.operand = MaxItems;
                }
                else if (instruction.opcode == OpCodes.Ldc_I4 &&
                         instruction.operand is int value && value == 30)
                {
                    instruction.operand = MaxItems;
                }

                yield return instruction;
            }
        }

        // ------------------------------------------------------------ 补录开局换牌（回放卡在换牌界面的根因）
        //
        // 单机局（练习 / 剧情 / AI 战）的录像开头缺两条 op，回放播到换牌界面就再也动不了：
        //
        //   · **op 0（发牌）**：原版只在 `NetworkPlayerMulliganCtrl` / `NetworkOpponentMulliganCtrl`
        //     的 `StartMulliganVfx` 末尾调 `CallRecordingMulliganStart`；单机用的是它们的基类
        //     `PlayerMulliganCtrl` / `OpponentMulliganCtrl`，一次都不调 → 录像里没有发牌记录。
        //   · **op 1（敌方换牌结果）**：`BattleEnemy.OnMulliganEndForReplay` 只在
        //     `NetworkMulliganMgr.EnemyChangeCardVfx` 里发；单机的
        //     `SingleMulliganMgr.EnemyChangeCardVfx` 只发 `CallRecordingMulligan`（那条
        //     `OnMulliganEnd` 是断线恢复用的），于是敌人的换牌结果永远没被录进去。
        //
        // 播放器按 op 推进：op 0 → `DealOperation` 发牌，**第一个** op 1 → `SwapOperation`
        // 播我方换牌，**第二个** op 1 → `SecondMulliganOperation`（收尾并继续对局）。少了第二个
        // op 1，`NetworkBattleData.isOppoMulliganEnd` 永远是 false，`OnEndMulligan` 不会来，
        // 画面就停在换牌界面（实测：27 份录像里每一份都只有一个 `isSelf=1` 的 op 1）。
        //
        // 补法与联机路径逐字一致：事件本身早就被原版 `SetupRecorderEvents` 订阅好了
        //（`OnMulliganStart` → `RecordMulliganStart`，`OnMulliganEndForReplay` →
        // `RecordEnemyMulliganReplaceCards`），这里只把单机路径没发的两个事件补发出去，
        // 而且只在「这一局正被我们录像」时才补。

        [HarmonyPatch(typeof(PlayerMulliganCtrl), nameof(PlayerMulliganCtrl.StartMulliganVfx))]
        [HarmonyPostfix]
        internal static void PlayerMulliganCtrl_StartMulliganVfx_Postfix(PlayerMulliganCtrl __instance)
        {
            RecordMulliganStart(__instance);
        }

        [HarmonyPatch(typeof(OpponentMulliganCtrl), nameof(OpponentMulliganCtrl.StartMulliganVfx))]
        [HarmonyPostfix]
        internal static void OpponentMulliganCtrl_StartMulliganVfx_Postfix(OpponentMulliganCtrl __instance)
        {
            RecordMulliganStart(__instance);
        }

        [HarmonyPatch(typeof(SingleMulliganMgr), nameof(SingleMulliganMgr.EnemyChangeCardVfx))]
        [HarmonyPostfix]
        internal static void SingleMulliganMgr_EnemyChangeCardVfx_Postfix(BattleManagerBase btlMgrIns)
        {
            if (btlMgrIns?.BattleEnemy == null || !IsRecordingThisBattle(btlMgrIns))
            {
                return;
            }

            try
            {
                // 与同一处的 CallRecordingMulligan 用同一个表达式：单机路径自己也是拿敌方
                // HandCardList 的 Index 当「换牌后的手牌」记的。
                btlMgrIns.BattleEnemy.CallRecordingMulliganEnd(
                    btlMgrIns.BattleEnemy.HandCardList.Select(card => card.Index).ToList());
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    $"[Replay] Could not record the enemy mulligan: {exception.Message}");
            }
        }

        private static void RecordMulliganStart(MulliganCtrl ctrl)
        {
            if (ctrl == null)
            {
                return;
            }

            try
            {
                BattlePlayerBase player = ctrl.GetBattlePlayer();
                if (player == null || !IsRecordingThisBattle(player.BattleMgr))
                {
                    return;
                }

                List<BattleCardBase> firstDraw = AccessTools
                    .Field(typeof(MulliganCtrl), "_firstDrawList")?
                    .GetValue(ctrl) as List<BattleCardBase>;
                if (firstDraw == null || firstDraw.Count == 0)
                {
                    return;
                }

                // 双方各发一次；原版 RecordMulliganStart 会把它们合并成同一条 op 0。
                player.CallRecordingMulliganStart(firstDraw);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    $"[Replay] Could not record the mulligan start: {exception.Message}");
            }
        }

        private static bool IsRecordingThisBattle(BattleManagerBase battleMgr)
        {
            return RecordingEnabled &&
                battleMgr != null &&
                _recorder != null &&
                ReferenceEquals(_recorderOwner, battleMgr);
        }

        private static bool ShouldRecord(BattleManagerBase battleMgr)
        {
            if (battleMgr == null || ReplayRoot == null)
            {
                return false;
            }


            string typeName = battleMgr.GetType().Name;
            if (typeName.Contains("Puzzle") || typeName.Contains("Replay"))
            {
                return false;
            }

            IReplayRecordManager current = battleMgr._contentsCreator?.ReplayRecordManager;
            return current is NullReplayRecordManager;
        }

        private static ReplayRecordManager RecorderFor(BattleManagerBase battleMgr)
        {
            if (_recorder == null || !ReferenceEquals(_recorderOwner, battleMgr))
            {
                _recorder = new ReplayRecordManager();
                _recorderOwner = battleMgr;
            }

            return _recorder;
        }

        /// <summary>
        /// 给 <c>NetworkUserInfoData</c> 补上双方的开局牌组：<c>RecordBattleStartInfo()</c>
        /// 就是从这里取牌组写进 replay_info.json 的，AI 战原本是空的。
        /// </summary>
        private static void EnsureDecks(BattleManagerBase battleMgr)
        {
            try
            {
                GameMgr gameMgr = GameMgr.GetIns();
                NetworkUserInfoData info = gameMgr != null ? gameMgr.GetNetworkUserInfoData() : null;
                if (info == null)
                {
                    return;
                }

                if (info._selfDeck == null)
                {
                    info._selfDeck = BuildDeckList(battleMgr.BattlePlayer);
                }

                if (info._oppoDeck == null)
                {
                    info._oppoDeck = BuildDeckList(battleMgr.BattleEnemy);
                }

                Plugin.Logger.LogInfo(
                    $"[Replay] Decks for recording: self={info._selfDeck?.Count ?? 0} card(s), " +
                    $"enemy={info._oppoDeck?.Count ?? 0} card(s).");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Could not prepare decks for recording: {exception.Message}");
            }
        }

        /// <summary>
        /// 录像器写 replay_info.json 时会调 <c>RecordBattleInfo()</c>，它从 <c>NetworkUserInfoData</c> 的
        /// <c>_selfInfo</c> / <c>_oppoInfo</c> 两个字典里取名字 / 卡背 / 徽章 / 段位 / 地区 / 官方标记
        /// （<c>GetSelfName()</c> 直接 <c>_selfInfo["userName"]</c>，离线局没有这份数据 → KeyNotFoundException）。
        /// 这里按它要的键补齐（只补缺失的，联机战原样不动）。
        /// </summary>
        private static void EnsureNetworkUserInfo(BattleManagerBase battleMgr)
        {
            try
            {
                GameMgr gameMgr = GameMgr.GetIns();
                NetworkUserInfoData info = gameMgr != null ? gameMgr.GetNetworkUserInfoData() : null;
                if (info == null)
                {
                    return;
                }

                // 职业 / 主战者：录制时取自 SelfBattleStartInfo / OppoBattleStartInfo，
                // 离线局这两个对象是 null，取出来全是 0 —— 回放里就显示「中立」，
                // 播放初始化还会 DataMgr.GetClassPrm(0) 抛异常。这里先用战斗里的
                // 双方主战者卡（BattlePlayerBase.Class 是 BattleCardBase）把信息建起来。
                Dictionary<string, object> self = BuildBattleUserInfo(battleMgr.BattlePlayer,
                    PlayerStaticData.UserName ?? "Player");
                Dictionary<string, object> opponent = BuildBattleUserInfo(battleMgr.BattleEnemy, "CPU");
                info.SetSelfInfo(self, false);
                info.SetOpponentInfo(opponent, false);

                // 上面两个字典只喂给了「录像器取名字/卡背」那一半；职业与主战者号它读的是
                // SelfBattleStartInfo / OppoBattleStartInfo，这两个对象得单独建（见方法注释）。
                ApplyBattleStartInfo(info, self, opponent);

                // SetSelfInfo/SetOpponentInfo 会重建 _selfInfo/_oppoInfo（并且会清掉 _oppoDeck），
                // 所以补字典要放在它后面，补牌组放在更后面（见调用处）。
                if (info._selfInfo == null)
                {
                    info._selfInfo = new Dictionary<string, object>();
                }

                if (info._oppoInfo == null)
                {
                    info._oppoInfo = new Dictionary<string, object>();
                }

                Fill(info._selfInfo, "userName", PlayerStaticData.UserName ?? "Player");
                Fill(info._selfInfo, "viewerId", Certification.ViewerId);
                Fill(info._selfInfo, "oppoId", 0);
                Fill(info._selfInfo, "sleeveId", 0L);
                Fill(info._selfInfo, "emblemId", PlayerStaticData.UserEmblemID);
                Fill(info._selfInfo, "degreeId", PlayerStaticData.UserDegreeID);
                Fill(info._selfInfo, "country_code", PlayerStaticData.UserCountryCode ?? string.Empty);
                Fill(info._selfInfo, "isOfficial", PlayerStaticData.IsOfficialUserDisplay);
                Fill(info._selfInfo, "battlePoint", 0);
                Fill(info._selfInfo, "masterPoint", 0);
                Fill(info._selfInfo, "rank", 0);
                Fill(info._selfInfo, "deckCount", 40);
                Fill(info._selfInfo, "subclassId", 10);
                Fill(info._selfInfo, "rotationId", string.Empty);

                Fill(info._oppoInfo, "userName", "CPU");
                Fill(info._oppoInfo, "viewerId", 0);
                Fill(info._oppoInfo, "sleeveId", 0L);
                Fill(info._oppoInfo, "emblemId", 0L);
                Fill(info._oppoInfo, "degreeId", 0);
                Fill(info._oppoInfo, "country_code", string.Empty);
                Fill(info._oppoInfo, "isOfficial", false);
                Fill(info._oppoInfo, "battlePoint", 0);
                Fill(info._oppoInfo, "masterPoint", 0);
                Fill(info._oppoInfo, "rank", 0);
                Fill(info._oppoInfo, "deckCount", 40);
                Fill(info._oppoInfo, "subclassId", 10);
                Fill(info._oppoInfo, "rotationId", string.Empty);

                Plugin.Logger.LogInfo("[Replay] Filled the network user info the replay record needs.");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Could not fill the network user info: {exception.Message}");
            }
        }

        /// <summary>
        /// 录像器写 classId1/charaId1/classId2/charaId2（<c>RecordBattleInfo</c>）时读的**不是**
        /// <c>_selfInfo</c>/<c>_oppoInfo</c> 两个字典，而是这两个对象：
        ///
        ///   GetSelfClassId()      → SelfBattleStartInfo.ClassId   （null 时直接返回 0）
        ///   GetSelfCharaId()      → SelfBattleStartInfo.CharaId   （null 时直接返回 0）
        ///   GetOpponentCharaId()  → OppoBattleStartInfo.CharaId   （null 时直接返回 0）
        ///
        /// 而它们只有「看录像恢复」那条路会建（<c>SetSelfInfo(info, isWatchReplayRecovery: true)</c> →
        /// <c>SetNetworkSelfInfo</c>）。离线自己打的对局是 false，所以一直是 null —— 录出来的
        /// charaId/classId 全是 0，回放里敌方就退回「该职业的默认主战者」（实测症状）。
        ///
        /// 这里照原版 <c>NetworkUserInfo.SetParameter</c> 需要的键把两个对象直接建出来。
        /// 只写这两个属性，**不碰 DataMgr**：原版 <c>SetNetworkSelfInfo</c> 会顺带
        /// <c>SetPlayerCharaId / SetPlayerSubClassID / SetPlayerAvatarBattleInfo</c>，
        /// 离线局没必要，也怕覆盖掉本来就对的值。
        /// </summary>
        private static void ApplyBattleStartInfo(NetworkUserInfoData info,
            Dictionary<string, object> self, Dictionary<string, object> opponent)
        {
            bool selfOk = TrySetBattleStartInfo(info, "SelfBattleStartInfo", self);
            bool oppoOk = TrySetBattleStartInfo(info, "OppoBattleStartInfo", opponent);
            if (selfOk && oppoOk)
            {
                Plugin.Logger.LogInfo(
                    $"[Replay] Battle start info for recording: self(class={self["classId"]}, chara={self["charaId"]}), " +
                    $"enemy(class={opponent["classId"]}, chara={opponent["charaId"]}).");
            }
            else
            {
                Plugin.Logger.LogWarning(
                    $"[Replay] Could not set the battle start info (self={selfOk}, enemy={oppoOk}); " +
                    "the recording may still write class/chara 0.");
            }
        }

        private static bool TrySetBattleStartInfo(NetworkUserInfoData info, string propertyName,
            Dictionary<string, object> values)
        {
            try
            {
                PropertyInfo property = typeof(NetworkUserInfoData).GetProperty(propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                Type userInfoType = property?.PropertyType;
                MethodInfo setter = property?.GetSetMethod(nonPublic: true);
                if (property == null || setter == null || userInfoType == null)
                {
                    return false;
                }

                object userInfo = Activator.CreateInstance(userInfoType);
                MethodInfo setParameter = userInfoType.GetMethod("SetParameter",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (setParameter == null)
                {
                    return false;
                }

                setParameter.Invoke(userInfo, new object[] { values });
                setter.Invoke(info, new[] { userInfo });
                return true;
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Could not build '{propertyName}': {exception.Message}");
                return false;
            }
        }

        /// <summary>从战斗里的主战者（BattleCardBase）取出职业/主战者，拼成 NetworkUserInfo 要的字典。</summary>
        private static Dictionary<string, object> BuildBattleUserInfo(BattlePlayerBase player, string name)
        {
            int classId = 0;
            int charaId = 0;

            try
            {
                // 优先用 DataMgr 的对战职业：主战者卡上的 Clan 不可靠（实测录出来的是错的/0）。
                DataMgr dataMgr = GameMgr.GetIns() != null ? GameMgr.GetIns().GetDataMgr() : null;
                bool isEnemy = player != null && ReferenceEquals(player, BattleManagerBase.GetIns()?.BattleEnemy);

                if (dataMgr != null)
                {
                    classId = isEnemy ? 0 : dataMgr.GetPlayerClassId();
                    charaId = isEnemy ? dataMgr.GetEnemyCharaId() : dataMgr.GetPlayerCharaId();
                }

                // 敌方职业：DataMgr 上没有统一访问器，自定义练习那套起局参数里是确定的。
                if (isEnemy && classId <= 0)
                {
                    classId = AIManager.CurrentEnemyClassId;
                }

                if (classId <= 0)
                {
                    BattleCardBase leader = player?.Class;
                    if (leader != null)
                    {
                        classId = (int)leader.Clan;
                        charaId = leader.CardId;
                    }
                }

                // 最后一道：练习/自定义对局里 DataMgr 与主战者卡都给 0，只能从牌组里数量最多的
                // 职业推（否则录出来的 replay_info.json classId=0，回放会在 GetClassPrm(0) 直接崩）。
                if (classId <= 0)
                {
                    List<int> deckCardIds = player?.BattleStartDeckCardList?
                        .Select(card => card?.CardId ?? 0)
                        .ToList();
                    classId = InferClassFromCardIds(deckCardIds);
                }

                // 主战者号：DataMgr 给不出来时用**主战者卡自己的卡号**（剧情局是 500xxx 那位主角，
                // 练习局是所选的皮肤），最后才退回「该职业的默认主战者」——否则剧情回放里
                // 主战者会变成职业默认的那位。
                if (charaId <= 0)
                {
                    charaId = player?.Class?.CardId ?? 0;
                }

                if (charaId <= 0)
                {
                    charaId = StoryOfflineData.GetDefaultCharacterId(classId);
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Could not read the battle class: {exception.Message}");
            }

            return new Dictionary<string, object>
            {
                ["rank"] = 0,
                ["isMasterRank"] = 0,
                ["battlePoint"] = 0,
                ["masterPoint"] = 0,
                ["classId"] = ClampClass(classId),
                ["charaId"] = charaId,
                ["subclassId"] = 10,
                ["rotationId"] = string.Empty,
                ["userName"] = name
            };
        }

        private static void Fill(Dictionary<string, object> dictionary, string key, object value)
        {
            if (dictionary != null && !dictionary.ContainsKey(key))
            {
                dictionary[key] = value;
            }
        }

        private static List<CardDataModel> BuildDeckList(BattlePlayerBase player)
        {
            List<BattleCardBase> cards = player?.BattleStartDeckCardList;
            if (cards != null && cards.Count > 0)
            {
                List<CardDataModel> deck = new List<CardDataModel>(cards.Count);
                for (int i = 0; i < cards.Count; i++)
                {
                    deck.Add(new CardDataModel { Index = i + 1, CardId = cards[i].CardId });
                }

                return deck;
            }

            // 实在拿不到真牌组也不让录像开不起来：塞一副占位牌组（观战那条路径也是这么兜的），
            // 回放里第一视角的牌组会是占位卡，但操作序列照样录得到。
            List<CardDataModel> fallback = new List<CardDataModel>(40);
            for (int i = 0; i < 40; i++)
            {
                fallback.Add(new CardDataModel
                {
                    Index = i + 1,
                    CardId = NetworkUserInfoData.DUMMY_CARD_ID
                });
            }

            Plugin.Logger.LogWarning(
                "[Replay] Battle decks were empty; recording with placeholder decks (first-person deck in " +
                "the replay will be placeholder cards).");
            return fallback;
        }

        /// <summary>
        /// AI 战的结算：<c>NetworkStandardBattleMgr</c> 会把 <c>OnBattleFinish</c> 接到录像器的
        /// <c>BattleFinishWriteJsonData</c> 上，AI 战没有这条线，这里自己接。
        /// </summary>
        [HarmonyPatch(typeof(AINetworkBattleManager), nameof(AINetworkBattleManager.InitiateGameEndSequence))]
        [HarmonyPostfix]
        internal static void InitiateGameEndSequence_Postfix(bool hasWon)
        {
            FinishRecording(hasWon, "AINetworkBattleManager.InitiateGameEndSequence");
        }

        private static string NextBattleId()
        {
            string root = ReplayRoot;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                string id = DateTime.Now.AddSeconds(attempt).ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
                if (root == null || !Directory.Exists(Path.Combine(root, id)))
                {
                    return id;
                }
            }

            return DateTime.Now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- 离线应答

        internal static bool CanHandle(string taskName)
        {
            return taskName == InfoTaskName || taskName == DetailTaskName;
        }

        internal static bool TryCreateResponse(NetworkTask task, out JsonData response)
        {
            response = null;
            if (task == null)
            {
                return false;
            }

            string taskName = task.GetType().Name;
            if (!CanHandle(taskName))
            {
                return false;
            }

            try
            {
                object data = taskName == InfoTaskName
                    ? new Dictionary<string, object> { ["replay_list"] = BuildReplayList() }
                    : LoadBattleDetail(ReadRequestedBattleId(task));

                response = JsonMapper.ToObject(JsonConvert.SerializeObject(CreateResponseEnvelope(data)));
                return true;
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Could not build local data for {taskName}: {exception.Message}");
                return false;
            }
        }

        private static List<object> BuildReplayList()
        {
            List<object> items = new List<object>();
            string root = ReplayRoot;
            if (root == null || !Directory.Exists(root))
            {
                return items;
            }

            try
            {
                IEnumerable<string> directories = Directory
                    .GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .Take(MaxItems);

                foreach (string directory in directories)
                {
                    long battleId;
                    if (!long.TryParse(Path.GetFileName(directory), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out battleId) || battleId <= 0)
                    {
                        continue;      // 没录完的时间戳目录
                    }

                    JsonData replay = ReadReplayInfo(directory);
                    if (replay == null)
                    {
                        continue;
                    }

                    // 早期录的那几局 classId 是 0（当时还没补职业），列表里会直接抛
                    // ClassCharaPrm.SetClassLabelSetting 的 KeyNotFound —— 这里统一夹到合法职业。
                    JsonData sanitized = SanitizeReplayJson(replay);

                    items.Add(new Dictionary<string, object>
                    {
                        ["battle_id"] = battleId,
                        ["opponent_name"] = ReadString(sanitized, "name2", "CPU"),
                        ["class_id"] = ClampClass(ReadInt(sanitized, "classId1", 0)),
                        ["sub_class_id"] = ReadInt(sanitized, "subclassId1", 10),
                        ["opponent_class_id"] = ClampClass(ReadInt(sanitized, "classId2", 0)),
                        ["opponent_sub_class_id"] = ReadInt(sanitized, "subclassId2", 10),
                        ["chara_id"] = ReadInt(sanitized, "charaId1", 0),
                        ["opponent_chara_id"] = ReadInt(sanitized, "charaId2", 0),
                        ["opponent_country_code"] = ReadString(sanitized, "countryCode2", string.Empty),
                        ["opponent_emblem_id"] = ReadString(sanitized, "emblemId2", "0"),
                        ["opponent_degree_id"] = ReadString(sanitized, "degreeId2", "0"),
                        ["is_win"] = ReadInt(sanitized, "is_win", 1),
                        ["battle_start_time"] = File.GetLastWriteTime(directory).ToString("yyyy-MM-dd HH:mm:ss"),
                        ["deck_format"] = ReadInt(sanitized, "deck_format", 1),
                        // BattleParameter 要这两把钥匙，缺了会返回 null，之后 IsTwoPick 直接空引用。
                        ["battle_type"] = OfflineBattleType,
                        ["two_pick_type"] = 0,
                        ["battle_rule"] = 0
                    });
                }

                Plugin.Logger.LogInfo($"[Replay] Local replay list: {items.Count} entrie(s) from {root}.");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Could not scan '{root}': {exception.Message}");
            }

            return items;
        }

        /// <summary>
        /// 早期录的回放里职业是 0（当时还没补职业数据），直接拿去显示 / 播放会抛
        /// <c>ClassCharaPrm.SetClassLabelSetting</c> 与 <c>DataMgr.GetClassPrm(0)</c> 的异常。
        /// 这里把双方职业夹到合法范围，并把「玩家 ID / 昵称」换成离线自定义的那份。
        /// </summary>
        private static JsonData SanitizeReplayJson(JsonData replay)
        {
            try
            {
                foreach (string key in new[] { "classId1", "classId2" })
                {
                    if (replay.Keys.Contains(key))
                    {
                        replay[key] = ClampClass(replay[key].ToInt());
                    }
                }

                string name = PlayerStaticData.UserName;
                if (!string.IsNullOrEmpty(name))
                {
                    replay["name1"] = name;
                }

                int viewerId = ProfileOfflineData.GetViewerId();
                if (viewerId > 0)
                {
                    replay["vid1"] = viewerId;
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Could not sanitize a replay: {exception.Message}");
            }

            return replay;
        }

        private static int ClampClass(int classId)
        {
            return classId >= 1 && classId <= 8 ? classId : 1;
        }

        /// <summary>本地没有这一局时返回空对象（正常流程走不到：列表里的 id 都来自本地目录）。</summary>
        private static object LoadBattleDetail(long battleId)
        {
            string directory = FindReplayDirectory(battleId);
            JsonData replay = directory == null ? null : ReadReplayInfo(directory);
            if (replay == null)
            {
                Plugin.Logger.LogWarning($"[Replay] No local replay for battle_id={battleId}.");
                return new Dictionary<string, object>();
            }

            SanitizeReplayJson(replay);
            return JsonConvert.DeserializeObject<Dictionary<string, object>>(replay.ToJson())
                   ?? new Dictionary<string, object>();
        }

        private static long ReadRequestedBattleId(NetworkTask task)
        {
            ReplayDetailTask detail = task as ReplayDetailTask;
            ReplayDetailTask.ReplayDetailTaskParam parameters = detail?.Params as ReplayDetailTask.ReplayDetailTaskParam;
            return parameters != null ? parameters.battle_id : 0L;
        }

        private static string FindReplayDirectory(long battleId)
        {
            string root = ReplayRoot;
            if (root == null)
            {
                return null;
            }

            string directory = Path.Combine(root, battleId.ToString(CultureInfo.InvariantCulture));
            return Directory.Exists(directory) ? directory : null;
        }

        // ------------------------------------------------------------ 修 replay_info.json 里的职业
        //
        // 离线局录出来的 replay_info.json 里 classId1/classId2 是 0：BuildBattleUserInfo 取
        // DataMgr.GetPlayerClassId() 与主战者卡的 Clan 都是 0。列表/预览那一路已经由
        // SanitizeReplayJson 夹过，但**播放读的是磁盘上的文件**：
        //
        //   ReplayDialogContent.GoReplay（本地分支）→ NewReplayBattleMgr.ReadJson("replay_info.json")
        //   → Data.ReplayBattleInfo → ReplayController.ParseReplayData → SettingSelfInfo
        //   → OnReplayReady() 取 DataMgr.GetPlayerClassId()（=0）
        //   → Matching.FirstSetting → StartBattleLoad → DataMgr.GetClassPrm(0)
        //   → **KeyNotFoundException**（实测栈就是这里），对局根本起不来。
        //
        // 所以读取时补正：职业非法就从牌组里数量最多的职业推；主战者号非法时，**剧情局先按
        // fieldId 去章节表（Mods/StorySpecialBattles）反查官方配的敌我主战者**（否则回放里
        // 会显示「该职业的默认主战者」，实测敌方就是这样），查不到才退回默认主战者。
        // 录的时候也顺带补一次（见 BuildBattleUserInfo / ApplyBattleStartInfo），新录像就不会再带着 0。
        [HarmonyPatch(typeof(NewReplayBattleMgr), nameof(NewReplayBattleMgr.ReadJson))]
        [HarmonyPostfix]
        internal static void NewReplayBattleMgr_ReadJson_Postfix(string __0, ref JsonData __result)
        {
            RepairReplayClassIds(__result, __0);
        }

        /// <summary>
        /// 剧情对局的回放会崩在发牌这一步（实测）：
        ///
        ///   DealOperation → SetSkillDescriptionValueList(ReplayBattlePlayer.AllCards, receivedData.CardInfoList)
        ///   → GetBattleCardIdx 找不到那张卡时返回 null
        ///   → UpdateSkillDescriptionValueList(null, cardInfo) 里给 card.ReplaySkillDescriptionValueList 赋值
        ///   → NullReferenceException，发牌流程整条断掉，播放器于是永远停在换牌界面。
        ///
        /// 原版这条链没有任何空判断（CardInfo 本身也可能是 null）。这里只补「拿到 null 就跳过」，
        /// 参数都正常时行为一个字不变。
        /// </summary>
        [HarmonyPatch(typeof(NewReplayBattleMgr), "UpdateSkillDescriptionValueList")]
        [HarmonyPrefix]
        internal static bool UpdateSkillDescriptionValueList_Prefix(
            BattleCardBase card,
            NetworkBattleReceiver.CardInfo cardInfo)
        {
            if (card == null || cardInfo == null)
            {
                Plugin.Logger.LogWarning(
                    "[Replay] Skipped a skill-description entry whose card was not found in the replay " +
                    "(stock code would throw here and abort the deal).");
                return false;
            }

            return true;
        }

        private static void RepairReplayClassIds(JsonData replay, string fileName)
        {
            try
            {
                if (replay == null || !replay.IsObject ||
                    (replay["deck1"] == null && replay["deck2"] == null))
                {
                    // replay_network.json / replay_turn_start.json 这些没有 deck1/deck2，不用管。
                    return;
                }

                int[] classIds = new int[3];
                int[] charaIds = new int[3];

                // 第一遍：职业。离线局录的时候拿不到，只能从牌组里数量最多的职业推。
                for (int side = 1; side <= 2; side++)
                {
                    classIds[side] = replay["classId" + side]?.ToInt() ?? 0;
                    charaIds[side] = replay["charaId" + side]?.ToInt() ?? 0;
                    if (!IsPlayableClass(classIds[side]))
                    {
                        classIds[side] = InferClassFromDeck(replay["deck" + side]);
                        replay["classId" + side] = classIds[side];
                    }
                }

                // 第二遍：主战者。**剧情局优先查章节表**（Mods/StorySpecialBattles/<story>.json 的
                // _chapter）：回放里的 fieldId 就是那张表的 battle3dfield_id，官方在那里配了
                // player_chara_id / enemy_chara_id。这样老录像里 charaId=0 时也能还原出剧情里
                // 真正的那位主战者，而不是「该职业的默认主战者」（实测症状：敌方变成职业默认皮肤）。
                //
                // 只对剧情局（deck_format = DataMgr.BattleType.Story）查：练习局也能把战场选成
                // 剧情用过的背景（实测 field=71/6/61/7 的练习回放），光按 fieldId 反查会把剧情
                // 主战者安到练习回放上。
                //
                // 注意**不能只看 charaId<=0**：这一版之后录的剧情局敌方写的是「该职业的默认主战者」
                // （实测 `enemy(class=8, chara=8)` —— 录制那一刻 `DataMgr` 还拿不到剧情敌方的皮肤号），
                // 非 0 但一样是错的。所以「等于该职业默认主战者」也算待补，被章节表覆盖掉。
                StoryBattleLeaders leaders = null;
                int deckFormat = replay["deck_format"]?.ToInt() ?? 0;
                bool storyBattle = deckFormat == (int)DataMgr.BattleType.Story;
                if (storyBattle)
                {
                    leaders = FindStoryBattleLeaders(
                        replay["fieldId"]?.ToInt() ?? 0, classIds[1], classIds[2]);
                }

                List<string> repaired = new List<string>();
                for (int side = 1; side <= 2; side++)
                {
                    int recorded = replay["charaId" + side]?.ToInt() ?? 0;
                    int defaultCharaId = StoryOfflineData.GetDefaultCharacterId(classIds[side]);
                    int fromStory = leaders == null
                        ? 0
                        : side == 1 ? leaders.PlayerCharaId : leaders.EnemyCharaId;

                    if (fromStory > 0 && (recorded <= 0 || recorded == defaultCharaId))
                    {
                        charaIds[side] = fromStory;
                    }
                    else if (charaIds[side] <= 0)
                    {
                        charaIds[side] = defaultCharaId;
                    }

                    if (charaIds[side] > 0 && recorded != charaIds[side])
                    {
                        replay["charaId" + side] = charaIds[side];
                        repaired.Add($"side{side}: class={classIds[side]}, chara={charaIds[side]}");
                    }
                }

                if (repaired.Count > 0)
                {
                    Plugin.Logger.LogInfo(
                        $"[Replay] Repaired the recorded class of '{Path.GetFileName(fileName)}': " +
                        string.Join("; ", repaired));
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Could not repair recorded class ids: {exception.Message}");
            }
        }

        /// <summary>章节表里的一条对局配置（只取回放修复要的几个字段）。</summary>
        private sealed class StoryBattleLeaders
        {
            public int PlayerCharaId;

            public int EnemyCharaId;

            public int PlayerClassId;

            public int EnemyClassId;
        }

        /// <summary>battle3dfield_id → 该战场上出现过的所有章节配置。</summary>
        private static readonly Dictionary<int, List<StoryBattleLeaders>> StoryBattlesByField =
            new Dictionary<int, List<StoryBattleLeaders>>();

        private static bool _storyBattleTableLoaded;

        /// <summary>
        /// 按「回放里的 fieldId（= battle3dfield_id）+ 双方职业」在剧情章节表里反查这一局的
        /// 敌我主战者。同一个战场上打过的章节可能不止一场（比如同一张最终战场连着好几章），
        /// 所以还要用两个职业卡一下；卡完仍有多条就认为分不清，返回 null 让调用方走默认主战者，
        /// 免得把 A 章的主战者安到 B 章的回放上。
        /// </summary>
        private static StoryBattleLeaders FindStoryBattleLeaders(int fieldId, int playerClassId, int enemyClassId)
        {
            if (fieldId <= 0)
            {
                return null;
            }

            LoadStoryBattleTable();
            if (!StoryBattlesByField.TryGetValue(fieldId, out List<StoryBattleLeaders> candidates) ||
                candidates == null ||
                candidates.Count == 0)
            {
                return null;
            }

            // 职业现算：章节表是缓存的，而主战者→职业查的是 master 表（读表那一刻可能还没加载完，
            // 缓存成 0 会一直错下去），所以每次匹配时补一次。
            foreach (StoryBattleLeaders entry in candidates)
            {
                if (entry.PlayerClassId <= 0 && entry.PlayerCharaId > 0)
                {
                    entry.PlayerClassId = StoryOfflineData.ResolveCharacterClassId(entry.PlayerCharaId);
                }

                if (entry.EnemyClassId <= 0 && entry.EnemyCharaId > 0)
                {
                    entry.EnemyClassId = StoryOfflineData.ResolveCharacterClassId(entry.EnemyCharaId);
                }
            }

            List<StoryBattleLeaders> matched = candidates
                .Where(entry =>
                    (!IsPlayableClass(enemyClassId) || entry.EnemyClassId == enemyClassId) &&
                    (!IsPlayableClass(playerClassId) || entry.PlayerClassId == playerClassId))
                .ToList();

            // 职业对不上（比如牌组推出来的职业有偏差）就退回只看敌方职业。
            if (matched.Count == 0)
            {
                matched = candidates
                    .Where(entry => !IsPlayableClass(enemyClassId) || entry.EnemyClassId == enemyClassId)
                    .ToList();
            }

            // 同一张战场上同一个职业线会连着好几章（同一批主战者），按「双方主战者」去重：
            // 去重后只剩一组才算分得清。
            List<StoryBattleLeaders> distinct = matched
                .GroupBy(entry => new { entry.PlayerCharaId, entry.EnemyCharaId })
                .Select(group => group.First())
                .ToList();

            if (distinct.Count != 1)
            {
                if (distinct.Count > 1)
                {
                    Plugin.Logger.LogInfo(
                        $"[Replay] Field {fieldId} matches {distinct.Count} story battles; " +
                        "keeping the class default leader for the replay.");
                }

                return null;
            }

            StoryBattleLeaders leaders = distinct[0];
            Plugin.Logger.LogInfo(
                $"[Replay] Field {fieldId} matches a story battle: " +
                $"player={leaders.PlayerCharaId} (class={leaders.PlayerClassId}), " +
                $"enemy={leaders.EnemyCharaId} (class={leaders.EnemyClassId}).");
            return leaders;
        }

        /// <summary>
        /// 把 <c>Mods/StorySpecialBattles/*.json</c> 的 <c>_chapter</c> 段读进内存（整个进程只读一次：
        /// 424 个文件里有挺大的技能字符串，只在真的要修一条剧情回放时才读）。
        /// </summary>
        private static void LoadStoryBattleTable()
        {
            if (_storyBattleTableLoaded)
            {
                return;
            }

            _storyBattleTableLoaded = true;
            try
            {
                string directory = Path.Combine(Plugin.ModPath, "StorySpecialBattles");
                if (!Directory.Exists(directory))
                {
                    return;
                }

                int entries = 0;
                foreach (string file in Directory.GetFiles(directory, "*.json"))
                {
                    try
                    {
                        JsonData data = JsonMapper.ToObject(File.ReadAllText(file));
                        JsonData chapter = data != null && data.IsObject && data.Keys.Contains("_chapter")
                            ? data["_chapter"]
                            : null;
                        if (chapter == null || !chapter.IsObject)
                        {
                            continue;
                        }

                        int fieldId = chapter["battle3dfield_id"]?.ToInt() ?? 0;
                        int enemyCharaId = chapter["enemy_chara_id"]?.ToInt() ?? 0;
                        if (fieldId <= 0 || enemyCharaId <= 0)
                        {
                            continue;
                        }

                        // 职业先留 0（不查 master），匹配时再补 —— 表是缓存的，读表那一刻
                        // class_chara_master 可能还没加载完。
                        StoryBattleLeaders leaders = new StoryBattleLeaders
                        {
                            PlayerCharaId = chapter["player_chara_id"]?.ToInt() ?? 0,
                            EnemyCharaId = enemyCharaId,
                            EnemyClassId = chapter["enemy_class"]?.ToInt() ?? 0
                        };

                        if (!StoryBattlesByField.TryGetValue(fieldId, out List<StoryBattleLeaders> list))
                        {
                            list = new List<StoryBattleLeaders>();
                            StoryBattlesByField[fieldId] = list;
                        }

                        list.Add(leaders);
                        entries++;
                    }
                    catch (Exception)
                    {
                        // 单个文件坏了不影响其它章节。
                    }
                }

                Plugin.Logger.LogInfo(
                    $"[Replay] Story battle leader table: {entries} chapter(s) over " +
                    $"{StoryBattlesByField.Count} field(s).");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Could not read the story battle leader table: {exception.Message}");
            }
        }

        internal static bool IsPlayableClass(int classId)
        {
            return classId >= 1 && classId <= 8;
        }

        /// <summary>职业只能从卡推：离线局 DataMgr 与主战者卡都给 0，牌组里数量最多的职业就是它。</summary>
        internal static int InferClassFromCardIds(IEnumerable<int> cardIds)
        {
            Dictionary<int, int> counts = new Dictionary<int, int>();
            CardMaster master = null;
            try
            {
                master = CardMaster.GetInstance(CardMaster.CardMasterId.Default);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] CardMaster unavailable for class inference: {exception.Message}");
            }

            if (master == null || cardIds == null)
            {
                return 1;
            }

            foreach (int cardId in cardIds)
            {
                if (cardId <= 0)
                {
                    continue;
                }

                try
                {
                    CardParameter parameter = master.GetCardParameterFromId(cardId);
                    if (parameter == null)
                    {
                        continue;
                    }

                    int clan = (int)parameter.Clan;
                    if (!IsPlayableClass(clan))
                    {
                        continue;
                    }

                    counts[clan] = counts.TryGetValue(clan, out int seen) ? seen + 1 : 1;
                }
                catch (Exception)
                {
                    // 单张卡查不到就算了。
                }
            }

            if (counts.Count == 0)
            {
                return 1;
            }

            return counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
        }

        private static int InferClassFromDeck(JsonData deck)
        {
            if (deck == null || deck.Count <= 0)
            {
                return 1;
            }

            List<int> cardIds = new List<int>(deck.Count);
            for (int i = 0; i < deck.Count; i++)
            {
                cardIds.Add(deck[i]?["cardId"]?.ToInt() ?? 0);
            }

            return InferClassFromCardIds(cardIds);
        }

        private static JsonData ReadReplayInfo(string directory)
        {
            try
            {
                string file = Directory
                    .GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(path => path.Contains(ReplayInfoFile));

                return file == null ? null : NewReplayBattleMgr.ReadJson(file);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    $"[Replay] Could not read '{Path.GetFileName(directory)}': {exception.Message}");
                return null;
            }
        }

        private static int ReadInt(JsonData data, string key, int fallback)
        {
            try
            {
                if (data != null && data.Keys.Contains(key))
                {
                    return data[key].ToInt();
                }
            }
            catch (Exception)
            {
            }

            return fallback;
        }

        private static string ReadString(JsonData data, string key, string fallback)
        {
            try
            {
                if (data != null && data.Keys.Contains(key))
                {
                    string value = data[key].ToString();
                    return string.IsNullOrEmpty(value) ? fallback : value;
                }
            }
            catch (Exception)
            {
            }

            return fallback;
        }

        private static Dictionary<string, object> CreateResponseEnvelope(object data)
        {
            return new Dictionary<string, object>
            {
                ["data_headers"] = new Dictionary<string, object>
                {
                    ["short_udid"] = 0,
                    ["viewer_id"] = 0,
                    ["sid"] = string.Empty,
                    ["servertime"] = 0L,
                    ["result_code"] = 1
                },
                ["data"] = data
            };
        }

        // ------------------------------------------------------------ 回放列表弹窗：「打开回放文件夹」
        //
        // 回放列表就是 <c>ReplayDialog.Create()</c> 建的那个弹窗：它用
        // <c>DialogBase.SetButtonLayout(ButtonLayout.CloseBtn)</c> 只放了一个「关闭」按钮。
        // 这里在它右边再加一个同尺寸同材质的按钮（走引擎自己的 button2 槽位与贴图，
        // 所以大小/材质/字体和「关闭」完全一致），点了直接在资源管理器里打开录像目录。
        //
        // 路径**不能写死**：<see cref="ReplayRoot"/> 是从运行时解析出来的资源根（也就是被重定向的
        // <c>Application.persistentDataPath</c>）拼出来的，换一台电脑、换个盘符/文件夹名都跟着走。
        [HarmonyPatch(typeof(ReplayDialog), nameof(ReplayDialog.SetupContents))]
        [HarmonyPostfix]
        internal static void ReplayDialog_SetupContents_Postfix(ReplayDialog __instance)
        {
            AddOpenReplayFolderButton(__instance);
        }

        private static void AddOpenReplayFolderButton(ReplayDialog replayDialog)
        {
            try
            {
                if (replayDialog == null)
                {
                    return;
                }

                // 弹窗是 ReplayDialog 的父物体（ReplayDialog.Create 里 dialog.SetObj(gameObject)）。
                DialogBase dialog = replayDialog.GetComponentInParent<DialogBase>();
                if (dialog == null)
                {
                    return;
                }

                // 引擎只给「关闭」一个按钮时 button2 是关着的；已经有人用过这个槽位就别再插一次
                // （否则会走到三按钮布局，位置全乱）。
                if (dialog.Btn2GameObject == null || dialog.Btn2GameObject.activeSelf)
                {
                    return;
                }

                // 按钮名字跟着游戏的文字语言走：简中 / 繁中 / 其它语言（英文）。
                // 「关闭」本身是游戏自己本地化的，我们这一个是插件加的，所以自己选。
                string buttonText = StoryTextLanguagePatches.ResolveUiText(
                    "打开回放文件夹",
                    "開啟回放資料夾",
                    "Open Replay Folder");

                // ButtonType.Gray 和「关闭」同一个贴图（btn_common_01_m_off），大小/材质一致。
                dialog.AddButton(DialogBase.ButtonType.Gray, isReflect: true, text: buttonText);

                // 引擎的两按钮布局里 button1 在右边、button2 在左边；这里对调一下，
                // 让「关闭」留在左、「打开回放文件夹」落在它的**右侧**。
                UISprite closeSprite = dialog.Btn1GameObject != null
                    ? dialog.Btn1GameObject.GetComponent<UISprite>()
                    : null;
                UISprite folderSprite = dialog.Btn2GameObject.GetComponent<UISprite>();
                if (closeSprite != null)
                {
                    closeSprite.leftAnchor.absolute = -264;
                    closeSprite.rightAnchor.absolute = -8;
                }

                if (folderSprite != null)
                {
                    folderSprite.leftAnchor.absolute = 8;
                    folderSprite.rightAnchor.absolute = 264;
                }

                // 名字比「关闭」长，缩字而不是溢出按钮。
                UILabel label = dialog.Btn2GameObject.GetComponentInChildren<UILabel>(true);
                if (label != null)
                {
                    label.overflowMethod = UILabel.Overflow.ShrinkContent;
                }

                // button2 的点击是引擎在 Awake 里接好的：先放音效、调 onPushButton2，
                // 再按 isNotCloseWindowButton2 决定要不要关掉弹窗。打开文件夹不应该关窗。
                dialog.isNotCloseWindowButton2 = true;
                dialog.onPushButton2 = OpenReplayFolder;

                Plugin.Logger.LogInfo(
                    $"[Replay] Added the '{buttonText}' button next to Close in the replay dialog " +
                    $"(folder: {ReplayRoot}).");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Could not add the replay-folder button: {exception}");
            }
        }

        /// <summary>在资源管理器里打开录像目录（路径由资源根推出来，不写死）。</summary>
        internal static void OpenReplayFolder()
        {
            string root = ReplayRoot;
            if (string.IsNullOrEmpty(root))
            {
                Plugin.Logger.LogWarning("[Replay] Cannot open the replay folder: the resource root is unknown.");
                return;
            }

            try
            {
                Directory.CreateDirectory(root);
                string full = Path.GetFullPath(root).Replace('/', '\\');
                Plugin.Logger.LogInfo($"[Replay] Opening the replay folder: {full}");

                try
                {
                    // 走 ShellExecute：和双击文件夹一样，交给系统的默认文件管理器。
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "\"" + full + "\"",
                        UseShellExecute = true
                    });
                }
                catch (Exception shellFailure)
                {
                    // 有的 Mono/系统组合不支持 UseShellExecute，退回直接起进程。
                    Plugin.Logger.LogWarning(
                        $"[Replay] ShellExecute could not open the replay folder ({shellFailure.Message}); " +
                        "starting explorer.exe directly instead.");
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + full + "\"");
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Replay] Could not open the replay folder '{root}': {exception}");
            }
        }
    }
}
