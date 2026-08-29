using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Globalization;
using Newtonsoft.Json;
using Wizard;
using Wizard.Battle.View.Vfx;
using Wizard.RoomMatch;

namespace Shadowbus
{
    internal static class P2PRuntime
    {
        private const int BattleStateCheckTimeoutSeconds = 15;
        private const int AuthorityExecutionTimeoutSeconds = 45;
        private const int MaxDeferredAgentDeliveries = 128;
        private const int MaxPendingReceivedBattleMessages = 128;

        // The official battle client starts its own operation immediately and
        // sends the native operation envelope while that operation's VFX is
        // being played.  The server never makes client-side VFX a prerequisite
        // for routing the action.  P2P must keep that timing: the Host routes
        // and receives native messages, but it must not turn the Guest into a
        // thin client which waits for the Host's VFX and then replays itself.
        //
        // This is deliberately not a user setting.  The old request/result
        // replay gate remains only as unused compatibility code for decoding
        // an in-flight packet from an older build; all new local actions use
        // the stock client sender path.
        private static bool UseNativeClientActionTiming => true;

        private static readonly ConcurrentQueue<Action> MainThreadActions =
            new ConcurrentQueue<Action>();
        private static readonly P2PRoomRoundState RoomRoundState =
            new P2PRoomRoundState();
        private static readonly P2PDealState DealState = new P2PDealState();
        private static readonly P2PDeliverySequence GuestDeliverySequence =
            new P2PDeliverySequence();
        private static readonly Queue<Dictionary<string, object>> DeferredGuestDeliveries =
            new Queue<Dictionary<string, object>>();
        private static readonly Queue<Dictionary<string, object>> DeferredAgentDeliveries =
            new Queue<Dictionary<string, object>>();
        private static readonly Queue<Dictionary<string, object>>
            PendingReceivedBattleMessages =
            new Queue<Dictionary<string, object>>();
        // Authority results bypass RealTimeNetworkBattleAgent's normal packet
        // receiver. Keep them in a dedicated FIFO so that a later result cannot
        // enter NetworkBattleReceiver while the previous result's native VFX
        // and post-action snapshots are still being finalized.
        private static readonly Queue<PendingAuthorityReplayAction>
            PendingAuthorityReplayActions =
            new Queue<PendingAuthorityReplayAction>();
        private static readonly P2PBattleSelectionTracker BattleSelectionTracker =
            new P2PBattleSelectionTracker();
        private static readonly Queue<PendingBattleStateCheck> PendingBattleStateChecks =
            new Queue<PendingBattleStateCheck>();

        private static P2PTransport transport;
        private static string bindAddress = "0.0.0.0";
        private static string advertisedAddress = string.Empty;
        private static int configuredPort = 29600;
        private static RealTimeNetworkAgent currentAgent;
        private static int hostPlaySequence;
        private static int guestPlaySequence;
        private static bool hostInitBattle;
        private static bool guestInitBattle;
        private static bool matchedSent;
        private static bool hostLoaded;
        private static bool guestLoaded;
        private static bool battleStartSent;
        private static bool battleStartReceived;
        private static bool mulliganReadyReceived;
        private static bool finishResultSent;
        private static bool? retiringHost;
        private static bool localRetired;
        private static List<int> shuffledHostDeck;
        private static List<int> shuffledGuestDeck;
        private static readonly P2PBattleCardTracker BattleCardTracker =
            new P2PBattleCardTracker();
        // The native server only sends private card state when a card is involved
        // in a particular register action.  In P2P both clients execute the same
        // effects locally, so keep a compact per-index snapshot and publish every
        // hidden-zone state change.  The receiver feeds these entries through the
        // original knownList/ReplaceReceivedCard path.
        private static readonly Dictionary<int, string> LocalHiddenCardStateSignatures =
            new Dictionary<int, string>();
        // Keep the latest pre-action state separately from the last state sent to
        // the peer. A played/discarded card is no longer in a private zone when
        // EmitMsg runs, so without this cache its hand/deck-only state is lost at
        // exactly the point where the receiver replaces the hidden card.
        private static readonly Dictionary<int, Dictionary<string, object>>
            LocalHiddenCardStates =
            new Dictionary<int, Dictionary<string, object>>();
        // Hidden card snapshots use a P2P-only side channel because the native
        // CardDataModel has no fields for generic skill values, fusion turns, or
        // Super Skybound Art. Keep the latest complete state per owner/index so
        // a later native card replacement can apply it deterministically.
        private static readonly Dictionary<string, Dictionary<string, object>>
            ReceivedHiddenCardStates =
            new Dictionary<string, Dictionary<string, object>>();
        // Keep complete local/remote states behind the wire-level delta. The
        // native receiver still needs a complete state when applying
        // P2P-only fields, while the transport should carry only fields that
        // changed since the last boundary.
        private const string HiddenCardStateDeltaKey = "p2pStateDelta";
        private const string HiddenCardStateRemovedFieldsKey =
            "p2pRemovedFields";
        // A hidden-card snapshot attached to an action describes the state after
        // that action. Keep it out of ReceivedHiddenCardStates until the native
        // operation has finished, otherwise a card condition can observe its own
        // post-action buff while the action is still being evaluated.
        private static readonly Dictionary<string, Dictionary<string, object>>
            PendingReceivedHiddenCardStates =
            new Dictionary<string, Dictionary<string, object>>();
        // Identity/zone changes belonging to an ordered action are required to
        // arrive through that action's native knownList/orderList/uList data.
        // Keep this provenance so the deferred private-state adapter can never
        // replace a real hand card after the original VFX has already bound its
        // view/touch object.
        private static readonly HashSet<string> PostActionHiddenCardStateKeys =
            new HashSet<string>(StringComparer.Ordinal);
        // Only the one-time private_state baseline may use the compatibility
        // object replacement path. Native action snapshots must never replace
        // a card after the original receiver boundary has passed.
        private static readonly HashSet<string> NativeBaselineHiddenCardStateKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> ReportedMissingNativePrivateIdentities =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string>
            ReceivedHiddenCardStateSignatures =
            new Dictionary<string, string>();
        private static readonly Dictionary<string, AppliedHiddenCardState>
            AppliedReceivedHiddenCardStates =
            new Dictionary<string, AppliedHiddenCardState>();
        // A Host-authoritative packet carries a post-action private snapshot,
        // but the native client needs the card identity *before* it builds the
        // receive operation.  Keep the signatures that were promoted into that
        // packet's knownList so the post-action adapter can apply only the
        // P2P-only fields after VFX, without replacing a hand object a second
        // time.
        private static readonly Dictionary<string, string>
            NativePromotedReceivedHiddenCardStateSignatures =
            new Dictionary<string, string>();
        // A promoted entry can still have no dummy to replace (for example a
        // token that is created by the operation itself). Record the actual
        // ReplaceReceivedCard callback separately; only that callback proves
        // the original pre-operation replacement path materialized the card.
        private static readonly Dictionary<string, string>
            NativeReplacedReceivedHiddenCardStateSignatures =
            new Dictionary<string, string>();
        private static readonly HashSet<string> PrivateConditionWarnings =
            new HashSet<string>(StringComparer.Ordinal);
        // Both clients are trusted in a friends-only room. Exchange the complete
        // private-zone baseline once so ordinary actions do not carry the same
        // hand/deck state over and over again.
        private static bool localPrivateStateSent;
        private static bool localPrivateStateAcknowledged;
        private static bool remotePrivateStateReceived;
        private static readonly Queue<Dictionary<string, object>> PendingFusionActions =
            new Queue<Dictionary<string, object>>();
        private static readonly HashSet<string> PendingFusionActionSignatures =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly List<Dictionary<string, object>>
            StagedPreNativeFusionActions =
            new List<Dictionary<string, object>>();
        // FusionMaterialized mutates the target while the action VFX is being
        // built. Keep the latest cumulative list by index because metamorphose
        // can replace the object before the outbound PlayActions message is
        // finally prepared.
        private static readonly Dictionary<string, List<P2PFusionIngredientState>>
            LocalFusionIngredientSnapshots =
            new Dictionary<string, List<P2PFusionIngredientState>>(
                StringComparer.Ordinal);
        private const string AuthoritativeSkillTargetsKey =
            "p2pAuthoritativeSkillTargets";
        private const string AuthoritativeSkillEvaluationsKey =
            "p2pAuthoritativeSkillEvaluations";
        // The official server resolves random skill targets and returns the
        // selected card indices. In P2P the acting client is the authority, so
        // record the actual post-filter targets and replay those exact cards on
        // the peer. This covers public history lists as well as private zones.
        private static readonly List<Dictionary<string, object>>
            LocalAuthoritativeSkillTargets =
            new List<Dictionary<string, object>>();
        private static readonly Queue<AuthoritativeSkillTargetBatch>
            PendingAuthoritativeSkillTargetBatches =
            new Queue<AuthoritativeSkillTargetBatch>();
        private static AuthoritativeSkillTargetBatch activeAuthoritativeSkillTargetBatch;
        // The batch belonging to the action currently being injected.  The
        // active batch is not necessarily the one whose native ReceivedMessage
        // callback is running if a previous operation is still being finalized.
        private static AuthoritativeSkillTargetBatch currentInjectedAuthoritativeSkillTargetBatch;
        private static int localAuthoritativeSkillTargetSequence;
        private static bool receivedAuthoritativeActionActive;
        // Private hand/deck expressions are resolved by the official server into
        // concrete skill parameters and preprocess decisions. In P2P the acting
        // client records the values it actually used so the peer does not have to
        // reevaluate a hidden-zone expression at a different action boundary.
        private static readonly List<Dictionary<string, object>>
            LocalAuthoritativeSkillEvaluations =
            new List<Dictionary<string, object>>();
        private static readonly Queue<AuthoritativeSkillEvaluationBatch>
            PendingAuthoritativeSkillEvaluationBatches =
            new Queue<AuthoritativeSkillEvaluationBatch>();
        private static AuthoritativeSkillEvaluationBatch
            activeAuthoritativeSkillEvaluationBatch;
        private static AuthoritativeSkillEvaluationBatch
            currentInjectedAuthoritativeSkillEvaluationBatch;
        private static readonly Stack<AuthoritativeSkillEvaluationScope>
            AuthoritativeSkillEvaluationScopes =
            new Stack<AuthoritativeSkillEvaluationScope>();
        // CheckCondition can run while execution info is being constructed,
        // before SkillBase.CallStart creates its scope. Keep those source-side
        // results keyed by the concrete skill and attach them when the scope
        // appears (or flush them at the action emit boundary).
        private static readonly Dictionary<SkillBase,
            List<AuthoritativeSkillConditionResult>> PendingLocalConditionResults =
            new Dictionary<SkillBase, List<AuthoritativeSkillConditionResult>>();
        private static int localAuthoritativeSkillEvaluationSequence;
        private static bool receivedAuthoritativeSkillEvaluationActive;
        private static int localActionManifestSequence;
        private static bool localActionCaptureActive;
        private static bool processingReceivedBattleAction;
        // A normal ordered packet can be stocked by the native agent before it
        // reaches NetworkBattleReceiver. Reserve its boundary at injection time
        // so another P2P packet cannot overtake it during that gap.
        private static bool receivedBattleActionInjectionPending;
        private static bool receivedBattleActionPendingUntilVfx;
        // ReceivedMessage only constructs the native operation. The operation
        // itself starts later from OperateReceive, so VfxMgr.IsEnd alone is not
        // a valid action-completion signal.
        private static bool receivedBattleActionOperationStarted;
        private static DateTime receivedBattleActionStartedUtc;
        private static bool receivedBattleActionStallReported;
        private static bool nativeReceivedMetadataActive;
        private static Dictionary<string, object> currentAuthorityReplayData;
        private const string PlayerHistoryStateKey = "p2pPlayerHistory";
        private const string PlayerHistoryStateBeforeKey =
            "p2pPlayerHistoryBefore";
        private static readonly Dictionary<string, PendingPlayerHistoryState>
            ReceivedPlayerHistoryStates =
            new Dictionary<string, PendingPlayerHistoryState>();
        private static readonly Dictionary<int, Dictionary<string, object>>
            PendingPreActionPlayerHistoryStates =
            new Dictionary<int, Dictionary<string, object>>();
        private static readonly Dictionary<int, int> AppliedPlayerHistoryRevisions =
            new Dictionary<int, int>();
        private static string localPlayerHistoryStateSignature = string.Empty;
        private static int localPlayerHistoryRevision;
        private static string localPlayerHistoryBaselineSignature = string.Empty;
        private static Dictionary<string, object> localPlayerHistoryBaselineState;
        private static int localPlayerHistoryBaselineRevision;
        private static Dictionary<string, object> localActionPreHistoryState;
        private static int localActionPreHistoryRevision;
        private static List<int> hostMulliganHand;
        private static List<int> guestMulliganHand;
        private static bool hostSwapped;
        private static bool guestSwapped;
        private static bool mulliganReadySent;
        private static int battleSeed;
        private static bool hostFirst;
        private static int localEmitSequence = 1;
        private static bool peerDisconnected;
        private static bool roomReleaseInjected;
        private static bool pendingOpponentSync;
        private static bool applyingReceivedHiddenCardStates;
        private static bool applyingReceivedPlayerHistoryStates;
        private static Dictionary<string, object> hostDeckEntry;
        private static Dictionary<string, object> guestDeckEntry;
        private static int sessionGeneration;

        // Host-authoritative action gate. Guest input is converted into a
        // request before the native OperateMgr mutates the battle. The host
        // executes the original operation once; the resulting PlayActions /
        // TurnEnd packets then travel through the existing native replay path.
        private static int authorityRequestSequence;
        private static int authorityTransitionSequence;
        private static long authorityResultActionSequence;
        private static bool authorityReplayDispatchActive;
        private static bool guestAuthorityBusy;
        private static string guestAuthorityRequestId;
        private static string guestAuthorityRequestAction;
        private static DateTime guestAuthorityRequestSentUtc;
        private static string receivedAuthorityRequestId;
        // URI of the ordered authority result currently being replayed on the
        // Guest.  A turn-end request spans TurnEndActions, TurnEnd, and the
        // following TurnStart; the input gate must stay closed until the whole
        // transition has completed.
        private static string activeReceivedBattleActionUri;
        private static string activeAuthorityExecutionRequestId;
        // A Host operation may run asynchronously while its native VFX graph
        // resolves card effects. Track the start separately so a genuinely
        // stuck authority operation can fail closed instead of accepting a
        // later Guest input against an unknown state.
        private static DateTime activeAuthorityExecutionStartedUtc;
        private static readonly HashSet<string> processedAuthorityRequests =
            new HashSet<string>(StringComparer.Ordinal);
        // Authority result packets are reliable, but a delayed packet can still
        // arrive after a request was rejected/timed out or after the same
        // boundary was already replayed.  Keep a small per-battle history so a
        // stale result cannot re-enter the native receiver and mutate the Guest
        // mirror a second time.
        private static readonly HashSet<string> completedAuthorityRequestIds =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> appliedAuthorityResultBoundaries =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, DateTime> authorityRequestTimes =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static readonly List<int> localAuthorityChoiceCardIndexes =
            new List<int>();
        // The native OperateMgr resolves RandomAttackCount immediately before
        // constructing the attack VFX.  Keep the candidate list for that one
        // StableRandom call so the Host can publish the actual target selected
        // by its RNG instead of the card initially clicked by the Guest.
        private static List<BattleCardBase> authorityRandomAttackCandidates;
        private static BattleCardBase authorityResolvedAttackTarget;
        // During Guest-side authority replay OperateMgr.Attack still executes
        // the native RandomAttackCount branch. Keep the Host-selected target
        // and replace only that StableRandom result in a postfix, so the
        // native RNG counters continue advancing exactly once.
        private static int authorityReplayRandomAttackCandidateCount;
        private static int authorityReplayRandomAttackTargetIndex = -1;
        private static readonly HashSet<int> authorityGuestKnownIndices =
            new HashSet<int>();
        // ActionProcessor instances created while the Host executes a Guest
        // request need the same network-registration callbacks as a normal
        // local-player processor.  NetworkBattleManagerBase intentionally
        // skips several callbacks when card.IsPlayer is false (that is correct
        // for a client receiving an opponent action, but incorrect for the
        // authority that must publish the complete native replay).  Keep a
        // transient set so the Harmony postfix below cannot attach duplicate
        // callbacks if a derived manager initializes a processor more than once.
        private static readonly HashSet<Wizard.Battle.ActionProcessor>
            AuthorityActionProcessors =
            new HashSet<Wizard.Battle.ActionProcessor>();
        // Per-owner baselines used by Host-authoritative results.  The legacy
        // local/remote snapshot tables only track the sender's own zones, while
        // an authoritative action can also discard, draw, transform, or attach
        // skills to the other player's hand/deck.
        private static readonly Dictionary<int, HashSet<int>>
            authorityKnownPrivateIndicesByOwner =
            new Dictionary<int, HashSet<int>>
            {
                [0] = new HashSet<int>(),
                [1] = new HashSet<int>()
            };
        private static readonly Dictionary<string, string>
            authorityPrivateStateSignatures =
            new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Dictionary<string, object>>
            authorityPrivateStates =
            new Dictionary<string, Dictionary<string, object>>(
                StringComparer.Ordinal);
        // A fusion metamorphose is evaluated against the card that existed at
        // the beginning of the action.  The native orderList only contains the
        // post-transform identity, so keep a transient absolute-owner snapshot
        // for every private card while an action is being assembled.  This is
        // used by both local Host emits and Host-authoritative Guest requests.
        private static readonly Dictionary<string, Dictionary<string, object>>
            actionPreHiddenCardStates =
            new Dictionary<string, Dictionary<string, object>>(
                StringComparer.Ordinal);
        private static readonly Dictionary<int, string>
            authorityPlayerHistorySignatures =
            new Dictionary<int, string>();
        private static readonly Dictionary<int, int>
            authorityPlayerHistoryRevisions =
            new Dictionary<int, int>();
        private static bool authorityLocalReplayActive;
        // NetworkBattleData creates ReplaceReceivedCard from a CardDataModel
        // that already contains the exact owner (isOpponent).  Keep that
        // owner beside the short-lived receiver object so authority replay can
        // disambiguate two cards that legitimately share the same index/cardId
        // on opposite sides.  ConditionalWeakTable avoids retaining receivers
        // after the native replacement pass completes.
        private sealed class AuthorityReceivedCardOwnerHint
        {
            internal bool GuestOwnsCard;
        }

        private static readonly ConditionalWeakTable<ReplaceReceivedCard,
            AuthorityReceivedCardOwnerHint> AuthorityReceivedCardOwnerHints =
            new ConditionalWeakTable<ReplaceReceivedCard,
                AuthorityReceivedCardOwnerHint>();

        internal static bool IsHostAuthorityMode => IsActive &&
            (Role == P2PRole.Host || Role == P2PRole.Guest);
        internal static bool UsesNativeClientActionTiming =>
            UseNativeClientActionTiming;
        internal static bool IsProcessingNativeReceivedBattleAction =>
            processingReceivedBattleAction;
        internal static bool IsAuthorityLocalReplayActive => authorityLocalReplayActive;

        internal static void ApplyAuthorityReplayReceiverOwnership(
            Dictionary<string, object> data,
            ref bool isPlayer)
        {
            if (!IsHostAuthorityMode || Role != P2PRole.Guest ||
                !ReadAuthorityBool(data, "p2pAuthorityLocalReplay"))
            {
                return;
            }

            authorityLocalReplayActive = true;
            isPlayer = true;
        }

        internal static void MarkReceivedNativeBattleOperationStarted()
        {
            if (!IsActive || !processingReceivedBattleAction ||
                !receivedBattleActionPendingUntilVfx)
            {
                return;
            }

            receivedBattleActionOperationStarted = true;
        }

        internal static bool ShouldSuppressNativeBattleEmit =>
            IsActive &&
            (peerDisconnected ||
                authorityLocalReplayActive ||
                (Role == P2PRole.Host &&
                    !string.IsNullOrEmpty(activeAuthorityExecutionRequestId)));

        internal static bool ShouldQueueSuppressedNativeBattleAcknowledgement =>
            IsActive && !peerDisconnected &&
            (authorityLocalReplayActive ||
                (Role == P2PRole.Host &&
                    !string.IsNullOrEmpty(activeAuthorityExecutionRequestId)));

        internal static bool SuppressNativeBattleEmit(
            string uri,
            Action onFinishedSend)
        {
            if (!ShouldSuppressNativeBattleEmit)
            {
                return false;
            }

            try
            {
                // The native caller treats the callback as local send
                // completion.  Complete it immediately because no remote
                // acknowledgement exists during an authority execution or a
                // Guest-side replay.
                onFinishedSend?.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Suppressed native emit completion callback failed: " +
                    ex.Message);
            }
            Plugin.Logger.LogDebug(
                "[P2P] Suppressed native battle emit during authority " +
                "execution/replay: " + (uri ?? "?") + ".");
            return true;
        }

        internal static bool SuppressNativeBattleInput()
        {
            return ShouldSuppressNativeBattleEmit;
        }

        internal static bool IsAuthorityGuestExecution =>
            IsActive && Role == P2PRole.Host &&
            !string.IsNullOrEmpty(activeAuthorityExecutionRequestId);

        internal static void AttachAuthorityActionProcessorEvents(
            NetworkBattleManagerBase manager,
            Wizard.Battle.ActionProcessor processor)
        {
            if (!IsAuthorityGuestExecution || manager == null || processor == null ||
                !AuthorityActionProcessors.Add(processor))
            {
                return;
            }

            // These are the same registrations made by
            // NetworkBattleManagerBase.SetupNetworkActionProcessorEvent, but
            // without its local-player gate.  The native SendCardDataMaker then
            // receives the exact metamorphose/choice/fusion/condition records
            // that it would have received for a normal player action.
            processor.OnTransform += (card, id, isChoice) =>
                CaptureAuthorityTransform(manager, card, id, isChoice);
            processor.OnSpecialAccelerate += skill =>
                CaptureAuthoritySpecialAccelerate(manager, skill);
            processor.OnBeforeChosenPlayCard +=
                (originalCard, playCard, chosenIndexes) =>
                    CaptureAuthorityChosenPlay(
                        manager, originalCard, playCard, chosenIndexes);
            processor.OnBeforeChosenEvolution +=
                (originalCard, evolCard, chosenIndexes) =>
                    CaptureAuthorityChosenEvolution(
                        manager, originalCard, evolCard, chosenIndexes);
            processor.OnBeforeFusion += (originalCard, selectedCards) =>
                CaptureAuthorityFusion(manager, originalCard, selectedCards);
        }

        private static bool IsAuthorityGuestCard(BattleCardBase card)
        {
            return IsAuthorityGuestExecution && card != null && !card.IsPlayer;
        }

        private static void CaptureAuthorityTransform(
            NetworkBattleManagerBase manager,
            BattleCardBase originalCard,
            int transformCardId,
            bool isChoice)
        {
            if (!IsAuthorityGuestCard(originalCard) || transformCardId <= 0 ||
                manager.RegisterActionManager == null)
            {
                return;
            }

            bool alreadyRegistered = manager.RegisterActionManager.RegisterDataList
                .OfType<RegisterMetamorphoseData>()
                .Any(item => item.Index == originalCard.Index &&
                    item.AfterId == transformCardId &&
                    item.IsSelf == originalCard.IsPlayer &&
                    item.IsChoice == isChoice);
            if (!alreadyRegistered)
            {
                manager.RegisterActionManager.Add(
                    new RegisterMetamorphoseData(
                        transformCardId,
                        originalCard.Index,
                        originalCard.IsPlayer,
                        null,
                        isChoice,
                        false,
                        false));
            }

            // The native sender normally records this mutation from the local
            // player's OnTransform callback.  A Host executes Guest cards as
            // BattleEnemy, so that callback is skipped by the stock manager.
            // Keep the transformed identity in the same tracker used by local
            // emits; the authority result can then publish the transformed
            // knownList entry while retaining the original pre-action cost.
            if (!isChoice)
            {
                try
                {
                    int keyActionType = NetworkBattleGenericTool.IsAcceleratedCard(
                            originalCard)
                        ? (int)SendKeyActionDataManager.KeyActionType.Accelerated
                        : NetworkBattleGenericTool.IsCrystallizeCard(originalCard)
                            ? (int)SendKeyActionDataManager.KeyActionType.Crystallize
                            : 0;
                    if (keyActionType != 0)
                    {
                        BattleCardBase transformed = originalCard.MetamorphoseCard;
                        int transformedCost = transformed?.Cost ?? originalCard.Cost;
                        RememberCardMutationForOwner(
                            false,
                            originalCard.Index,
                            originalCard.CardId,
                            originalCard.Cost,
                            transformCardId,
                            transformedCost,
                            keyActionType);
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogDebug(
                        "[P2P] Could not cache the Guest card mutation: " +
                        ex.Message);
                }
            }
        }

        private static void CaptureAuthoritySpecialAccelerate(
            NetworkBattleManagerBase manager,
            SkillBase skill)
        {
            if (!IsAuthorityGuestCard(skill?.SkillPrm?.ownerCard) ||
                !RegisterSkillConditionCheck.IsSkillConditionCheck(
                    skill, false, false) ||
                manager._networkBattleSetupCardEventBase == null)
            {
                return;
            }

            manager._networkBattleSetupCardEventBase.Event_SkillConditionCheck(
                skill, new List<BattleCardBase>(), null);
        }

        private static SendKeyActionDataManager
            GetAuthoritySendKeyActionDataManager(
                NetworkBattleManagerBase manager)
        {
            if (manager == null || !TryFindInstanceField(
                    manager.GetType(), "sendKeyActionDataManager",
                    out FieldInfo field))
            {
                return null;
            }

            try
            {
                return field.GetValue(manager) as SendKeyActionDataManager;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not read authority key-action manager: " +
                    ex.Message);
                return null;
            }
        }

        private static void CaptureAuthorityChosenPlay(
            NetworkBattleManagerBase manager,
            BattleCardBase originalCard,
            BattleCardBase playCard,
            List<int> chosenIndexes)
        {
            if (!IsAuthorityGuestCard(originalCard) || playCard == null ||
                NetworkBattleGenericTool.IsAcceleratedCard(originalCard) ||
                NetworkBattleGenericTool.IsCrystallizeCard(originalCard) ||
                playCard.IsChoiceBraveSkillCard)
            {
                // Accelerated/Crystallize and ChoiceBrave are already covered by
                // the original manager callback, whose condition explicitly
                // allows those two cases even for an opponent card.
                return;
            }

            GetAuthoritySendKeyActionDataManager(manager)?.SettingKeyActionData(
                originalCard, playCard, chosenIndexes, false);
        }

        private static void CaptureAuthorityChosenEvolution(
            NetworkBattleManagerBase manager,
            BattleCardBase originalCard,
            BattleCardBase evolCard,
            List<int> chosenIndexes)
        {
            if (!IsAuthorityGuestCard(originalCard) || evolCard == null)
            {
                return;
            }

            GetAuthoritySendKeyActionDataManager(manager)?.SettingKeyActionData(
                originalCard, evolCard, chosenIndexes, true);
        }

        private static void CaptureAuthorityFusion(
            NetworkBattleManagerBase manager,
            BattleCardBase originalCard,
            IEnumerable<BattleCardBase> selectedCards)
        {
            if (!IsAuthorityGuestCard(originalCard) || selectedCards == null)
            {
                return;
            }

            GetAuthoritySendKeyActionDataManager(manager)?.SettingFusionKeyActionData(
                originalCard, selectedCards);
        }

        internal static P2PRole Role { get; private set; }
        internal static bool IsActive { get; private set; }
        internal static string ConnectionCode { get; private set; }
        internal static string RoomId { get; private set; }
        internal static string BattleId { get; private set; }
        internal static P2PProfile LocalProfile { get; private set; }
        internal static P2PProfile RemoteProfile { get; private set; }
        internal static P2PDeckSnapshot LocalDeck { get; private set; }
        internal static P2PDeckSnapshot RemoteDeck { get; private set; }
        internal static P2PRoomRules Rules { get; private set; }
        internal static int InitialMaxLife =>
            Rules?.InitialMaxLife ?? P2PRoomRules.DefaultInitialMaxLife;
        internal static bool IsTwoPickRoom => IsActive &&
            Rules?.TwoPickType == (int)TwoPickFormat.Normal &&
            Rules?.BattleType == (int)NetworkDefine.ServerBattleType.RoomTwoPick;
        internal static bool CanEditRoomRules =>
            IsActive &&
            Role == P2PRole.Host &&
            !RoomRoundState.HostReady &&
            !RoomRoundState.GuestReady &&
            !RoomRoundState.ReadySent;
        internal static bool JoinFinished { get; private set; }
        internal static bool JoinSucceeded { get; private set; }
        internal static string LastError { get; private set; }

        internal static void Configure(string bind, string advertised, int port)
        {
            bindAddress = string.IsNullOrWhiteSpace(bind) ? "0.0.0.0" : bind.Trim();
            advertisedAddress = advertised?.Trim() ?? string.Empty;
            configuredPort = port < 0 || port > ushort.MaxValue ? 29600 : port;
        }

        internal static void Update()
        {
            CacheLocalBattleCardIdentities();
            int count = 0;
            while (count++ < 128 && MainThreadActions.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError("[P2P] Main-thread action failed: " + ex);
                    if (Role == P2PRole.Guest && !JoinFinished)
                    {
                        FailJoin("Failed while processing the room join response: " + ex.Message);
                    }
                }
            }
            TrySynchronizeOpponentRoomState();
            TrySendInitialPrivateStateSnapshot();
            TryApplyPendingHiddenCardStates();
            TryApplyPendingPlayerHistoryStates();
            TryApplyPendingFusionActions();
            TryCompleteReceivedBattleAction();
            if (!UseNativeClientActionTiming)
            {
                TryClearConsumedAuthoritativeSkillTargets();
                TryClearConsumedAuthoritativeSkillEvaluations();
                TryCheckAuthorityExecutionTimeout();
                TryCheckAuthorityRequestTimeout();
            }
            TryCheckPendingBattleStates();
            if (!UseNativeClientActionTiming)
            {
                // Check the completed boundary before dispatching the next one.
                // Authority replay is deliberately single-flight: a newer native
                // URI must not observe or mutate the previous action's VFX/state.
                TryInjectPendingAuthorityReplayAction();
            }
            TryInjectPendingReceivedPlayAction();
            if (!UseNativeClientActionTiming)
            {
                // The legacy replay model needed a continuously refreshed
                // private-card cache because it replaced hand/deck objects
                // after another client had executed the action.  The native
                // client path does not do that replacement, so walking every
                // private card every frame is pure CPU/GC overhead.
                ObserveLocalHiddenCardStates();
            }
            if (!peerDisconnected)
            {
                return;
            }

            bool hasBattleManager =
                BattleManagerBase.GetIns() is NetworkBattleManagerBase;
            RoomBase room = RoomBase.GetInstance();
            P2PDisconnectAction disconnectAction = P2PDisconnectPolicy.Evaluate(
                peerDisconnected,
                finishResultSent,
                roomReleaseInjected,
                hasBattleManager,
                IsBattleScene(),
                room != null && room.IsInitializeDone,
                room != null && room.IsRoomReadyComplete,
                currentAgent != null && IsRoomAgentReady());
            if (disconnectAction == P2PDisconnectAction.BattleResult)
            {
                InjectPeerDisconnectResult();
            }
            else if (disconnectAction == P2PDisconnectAction.RoomRelease)
            {
                InjectRoomRelease();
            }
            else if (disconnectAction == P2PDisconnectAction.ForceRoomExit)
            {
                ForceExitDisconnectedRoom(room);
            }
        }

        internal static void StartHosting(P2PRoomRules rules)
        {
            ResetSession();
            Role = P2PRole.Host;
            IsActive = true;
            Rules = rules ?? new P2PRoomRules();
            if (Rules.TwoPickType == (int)TwoPickFormat.Normal)
            {
                Rules.TwoPickRule = P2PTwoPickRules.Normalize(
                    Rules.TwoPickRule ?? P2PTwoPickRules.LoadSelected());
                P2PTwoPickRules.ResetDraft(Rules.TwoPickRule);
            }
            CustomFormatDefinition roomFormat = CustomFormats.Get(Rules.CustomFormatId);
            Rules.CustomFormatId = roomFormat.Id;
            Rules.FormatDefinition = roomFormat.Clone();
            CustomFormatContext.RoomFormatId = Rules.CustomFormatId;
            LocalProfile = CreateLocalProfile();
            RoomId = CreateNumericId();
            BattleId = CreateNumericId();

            byte[] token = new byte[16];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(token);
            }

            IPAddress bind = ParseAddress(bindAddress, IPAddress.Any);
            CreateTransport();
            transport.StartHost(bind, configuredPort, token);
            IPAddress advertised = string.IsNullOrWhiteSpace(advertisedAddress)
                ? (IPAddress.Any.Equals(bind) || IPAddress.IPv6Any.Equals(bind)
                    ? FindAdvertisedAddress(bind.AddressFamily)
                    : bind)
                : ParseAddress(advertisedAddress, null);
            if (!IsUsableAdvertisedAddress(advertised) ||
                advertised.AddressFamily != bind.AddressFamily)
            {
                throw new InvalidOperationException(
                    "P2P advertised address is not usable by the configured listener.");
            }
            ConnectionCode = P2PConnectionCode.Create(advertised, transport.BoundPort, token);
            Plugin.Logger.LogInfo(
                $"[P2P] Hosting room on {bind}:{transport.BoundPort}; advertised as {advertised}; " +
                $"format={Rules.CustomFormatId}; openDeck={Rules.IsDeckOpen}; " +
                $"twoPick={Rules.TwoPickType}; " +
                $"twoPickRule={Rules.TwoPickRule?.Id ?? string.Empty}; " +
                $"draftSize={Rules.TwoPickRule?.FinalDeckSize ?? 0}; " +
                $"initialMaxLife={Rules.InitialMaxLife}.");
        }

        internal static bool TrySetInitialMaxLife(int value)
        {
            if (!CanEditRoomRules || Rules == null)
            {
                return false;
            }

            int clamped = P2PRoomRules.ClampInitialMaxLife(value);
            if (Rules.InitialMaxLife == clamped)
            {
                return true;
            }

            Rules.InitialMaxLife = clamped;
            if (RemoteProfile != null)
            {
                SendWire(new P2PWireMessage
                {
                    Type = "rules_update",
                    Rules = Rules
                });
            }
            Plugin.Logger.LogInfo(
                $"[P2P] Host set both players' initial maximum life to {clamped}.");
            return true;
        }

        internal static void BeginJoin(P2PConnectionInfo info)
        {
            if (info == null)
            {
                throw new ArgumentNullException(nameof(info));
            }
            ResetSession();
            Role = P2PRole.Guest;
            IsActive = true;
            LocalProfile = CreateLocalProfile();
            JoinFinished = false;
            JoinSucceeded = false;
            CreateTransport();
            transport.Connect(info.Address, info.Port, info.Token);
            Plugin.Logger.LogInfo($"[P2P] Connecting to room host at {info.Address}:{info.Port}.");
        }

        internal static bool IsCurrentAgent(RealTimeNetworkAgent agent)
        {
            return agent != null && ReferenceEquals(currentAgent, agent);
        }

        internal static void SetCurrentAgent(RealTimeNetworkAgent agent, string source)
        {
            if (agent == null)
            {
                return;
            }
            if (ReferenceEquals(currentAgent, agent))
            {
                return;
            }
            currentAgent = agent;
            localEmitSequence = 1;
            if (Role == P2PRole.Host)
            {
                hostPlaySequence = 0;
            }
            else if (Role == P2PRole.Guest)
            {
                guestPlaySequence = 0;
            }
            Plugin.Logger.LogInfo(
                $"[P2P] Bound realtime agent from {source ?? "unknown"}: {agent.GetType().Name}.");
            FlushDeferredAgentDeliveries();
        }

        internal static void SetLocalDeck(DeckData deck)
        {
            if (deck == null || deck.GetCardIdList() == null)
            {
                throw new InvalidOperationException("No room deck is selected.");
            }
            if (IsTwoPickRoom)
            {
                int expectedCount = Rules?.TwoPickRule?.FinalDeckSize ?? 30;
                if (deck.GetCardIdList().Count != expectedCount)
                {
                    throw new InvalidOperationException(
                        $"The completed Two Pick deck contains " +
                        $"{deck.GetCardIdList().Count} cards; expected {expectedCount}.");
                }
            }
            else if (!IsDeckAllowed(
                         deck,
                         out CustomFormatDefinition definition,
                         out CustomFormatViolation violation))
            {
                throw new InvalidOperationException(
                    $"Deck {deck.GetDeckID()} is not valid for room format " +
                    $"{definition.Id}: {violation.ToLogMessage()}.");
            }
            LocalDeck = new P2PDeckSnapshot
            {
                Cards = new List<int>(deck.GetCardIdList()),
                ClassId = deck.GetDeckClassID(),
                SubclassId = deck.GetDeckSubClassID(),
                CharaId = deck.GetSkinId(false),
                SleeveId = deck.GetDeckSleeveID()
            };
            if (Role == P2PRole.Guest || Role == P2PRole.Host)
            {
                SendWire(new P2PWireMessage { Type = "deck", Deck = LocalDeck });
            }
            else if (Role == P2PRole.Host)
            {
                TrySendMatched();
            }
        }

        internal static bool IsDeckAllowed(
            DeckData deck,
            out CustomFormatDefinition definition,
            out CustomFormatViolation violation)
        {
            definition = Rules?.FormatDefinition ??
                CustomFormats.Get(Rules?.CustomFormatId);
            if (deck == null || deck.GetCardIdList() == null)
            {
                violation = new CustomFormatViolation(
                    CustomFormatRule.CardDataUnavailable,
                    0,
                    0,
                    0);
                return false;
            }

            if (IsTwoPickRoom)
            {
                violation = null;
                return deck.GetCardIdList().Count > 0;
            }

            return CustomFormats.IsDeckCompliant(
                deck.GetCardIdList(),
                definition,
                CardMaster.GetInstanceForBattle(),
                out violation);
        }

        internal static void HandleEmit(string uri, Dictionary<string, object> data)
        {
            if (!IsActive || string.IsNullOrEmpty(uri))
            {
                return;
            }

            Dictionary<string, object> preActionHistoryState = null;
            int preActionHistoryRevision = 0;
            if (IsOrderedLocalBattleMessage(uri))
            {
                preActionHistoryState = localActionPreHistoryState == null
                    ? null
                    : P2PJson.CloneDictionary(localActionPreHistoryState);
                preActionHistoryRevision = localActionPreHistoryRevision;
                localActionPreHistoryState = null;
                localActionPreHistoryRevision = 0;
            }

            // ActionProcessor and BattlePlayer finish their model mutations while
            // constructing the action VFX. Sending here lets both peers play that
            // VFX concurrently; waiting for the local queue to drain serializes
            // every animation and makes the whole match feel delayed.
            try
            {
                HandleEmitNow(uri, data,
                    preActionHistoryState, preActionHistoryRevision);
            }
            finally
            {
                if (IsOrderedLocalBattleMessage(uri))
                {
                    localActionCaptureActive = false;
                    PendingLocalConditionResults.Clear();
                    actionPreHiddenCardStates.Clear();
                }
            }
        }

        private static bool IsOrderedLocalBattleMessage(string uri)
        {
            return string.Equals(uri,
                       NetworkBattleDefine.NetworkBattleURI.PlayActions.ToString(),
                       StringComparison.Ordinal) ||
                string.Equals(uri,
                    NetworkBattleDefine.NetworkBattleURI.TurnEndActions.ToString(),
                    StringComparison.Ordinal) ||
                string.Equals(uri,
                    NetworkBattleDefine.NetworkBattleURI.TurnStart.ToString(),
                    StringComparison.Ordinal) ||
                string.Equals(uri,
                    NetworkBattleDefine.NetworkBattleURI.TurnEnd.ToString(),
                    StringComparison.Ordinal) ||
                string.Equals(uri,
                    NetworkBattleDefine.NetworkBattleURI.TurnEndFinal.ToString(),
                    StringComparison.Ordinal);
        }

        private static void HandleEmitNow(
            string uri,
            Dictionary<string, object> data,
            Dictionary<string, object> preActionHistoryState = null,
            int preActionHistoryRevision = 0)
        {
            CacheLocalBattleCardIdentities();
            Dictionary<string, object> messageData = P2PJson.CloneDictionary(data);
            messageData["uri"] = uri;
            messageData["viewerId"] = LocalProfile?.ViewerId ?? P2PIdentity.ViewerId;
            messageData["bid"] = BattleId ?? string.Empty;
            if (UseNativeClientActionTiming)
            {
                // Native v3 has one authoritative source: the original
                // orderList/uList/knownList response. Do not let a stale
                // compatibility manifest or snapshot hitch a ride on a new
                // native packet.
                messageData.Remove(P2PBattleProtocol.ActionManifestKey);
                messageData.Remove("p2pAuthoritativeSkillTargets");
                messageData.Remove("p2pAuthoritativeSkillEvaluations");
                messageData.Remove(P2PBattleStateDiagnostics.StateKey);
            }
            if (Role == P2PRole.Host && !string.IsNullOrEmpty(activeAuthorityExecutionRequestId))
            {
                messageData[P2PBattleProtocol.AuthorityResultRequestIdKey] =
                    activeAuthorityExecutionRequestId;
            }
            if (!UseNativeClientActionTiming)
            {
                // Legacy request/result replay required post-action private
                // snapshots because the Guest had not executed the operation.
                // With native client timing both peers execute the same native
                // operation and the stock orderList/knownList/uList envelope
                // is the source of truth.  Capturing complete hand/deck and
                // history objects here is both non-native and expensive.
                AppendLocalHiddenCardState(uri, messageData);
                if (Role == P2PRole.Host && IsOrderedLocalBattleMessage(uri))
                {
                    AttachActionPreHiddenMetamorphoseOriginals(messageData);
                }
                AppendLocalPlayerHistoryState(uri, messageData,
                    preActionHistoryState, preActionHistoryRevision);
                if (Role == P2PRole.Host &&
                    IsHostAuthorityMode &&
                    IsOrderedLocalBattleMessage(uri) &&
                    BattleManagerBase.GetIns() is NetworkBattleManagerBase authorityManager)
                {
                    EnsureNativePrivateMoveIdentities(
                        authorityManager, messageData);
                    AppendAuthorityResultMetadata(authorityManager, messageData);
                }
            }
            else if (IsHostAuthorityMode && IsOrderedLocalBattleMessage(uri))
            {
                // Native client timing keeps the original local action/VFX
                // order, but Host authority still needs the changed private
                // card state before the next condition is evaluated. Reuse the
                // existing incremental hidden-state adapter here: it scans the
                // native card objects, emits only changed signatures, and the
                // receiver applies the data at its normal
                // BeforeSettingReceiveData/native post-action boundaries.
                //
                // This is deliberately not a complete hand/deck snapshot and
                // does not replace knownList/uList/orderList. It only carries
                // state that the closed official server would have returned
                // for a private-zone mutation that the native wire format
                // cannot expose to the opponent.
                AppendLocalHiddenCardState(uri, messageData);
                // History-dependent conditions use the same native
                // BattlePlayerBase lists/scalars as the local client. Keep the
                // state incremental and let the receiver apply it only after
                // the ordered native action boundary, so this does not alter
                // operation or VFX ordering.
                AppendLocalPlayerHistoryState(uri, messageData);
                AttachActionPreHiddenMetamorphoseOriginals(messageData);
            }
            else if (IsOrderedLocalBattleMessage(uri))
            {
                // The native payload does not expose the pre-transform hand
                // identity needed by its own fusion-metamorphose receiver
                // path. Preserve that one card identity only; do not rebuild
                // complete private-zone snapshots.
                AttachActionPreHiddenMetamorphoseOriginals(messageData);
            }
            if (ShouldPublishAuthoritativeActionManifest(uri, messageData))
            {
                DrainPendingLocalConditionResults();
                AppendLocalActionManifest(uri, messageData);
                AppendLocalAuthoritativeSkillTargets(uri, messageData);
                AppendLocalAuthoritativeSkillEvaluations(uri, messageData);
            }
            else if (IsOrderedLocalBattleMessage(uri))
            {
                // Ordinary Host packets are already committed authority output.
                // Do not attach a second private-condition program for the
                // Guest to replay; it is both redundant and the source of the
                // old "unconsumed evaluation" batches.
                LocalAuthoritativeSkillTargets.Clear();
                LocalAuthoritativeSkillEvaluations.Clear();
            }
            RemovePrivateTwoPickDraftData(messageData, uri);
            if (string.Equals(
                    uri,
                    PlayerController.ROOM_URI.RoomEntry.ToString(),
                    StringComparison.Ordinal))
            {
                Plugin.Logger.LogInfo(
                    $"[P2P] Emitting RoomEntry as {Role}; agentReady={currentAgent != null}.");
            }

            if (!IsHostAuthorityMode &&
                P2PBattleProtocol.CarriesBattleStateCheckpoint(uri))
            {
                Dictionary<string, object> state = CaptureBattleState();
                if (state != null)
                {
                    messageData[P2PBattleStateDiagnostics.StateKey] = state;
                }
            }

            if (string.Equals(
                    uri,
                    NetworkBattleDefine.NetworkBattleURI.Retire.ToString(),
                    StringComparison.Ordinal))
            {
                localRetired = true;
            }
            else if (string.Equals(
                    uri,
                    NetworkBattleDefine.NetworkBattleURI.JudgeResult.ToString(),
                    StringComparison.Ordinal))
            {
                int localResult = GetLocalFinishResult();
                messageData["p2pLocalResult"] = localResult;
                Plugin.Logger.LogInfo(
                    $"[P2P] Reporting local battle result {localResult} with JudgeResult.");
            }

            bool attachedBurialSelection =
                BattleSelectionTracker.PrepareOutgoingAction(
                    messageData,
                    ResolveLocalBurialRiteSkillIndexes,
                    out string selectionSummary);
            if (attachedBurialSelection)
            {
                Plugin.Logger.LogInfo(
                    "[P2P] Attached skill selection to outgoing action: " +
                    selectionSummary + ".");
            }
            else if (!string.IsNullOrEmpty(selectionSummary))
            {
                Plugin.Logger.LogWarning(
                    "[P2P] Did not attach skill selection: " +
                    selectionSummary + ".");
            }

            if (!string.Equals(uri, P2PBattleProtocol.EchoUri,
                    StringComparison.Ordinal))
            {
                BattleCardTracker.PrepareOutgoingAction(
                    Role == P2PRole.Host, messageData, out _, out _,
                    ResolveLocalCardId, ResolveLocalCardCost,
                    warning => Plugin.Logger.LogWarning(
                        "[P2P] Hidden-card synchronization: " + warning + "."),
                    ResolveLocalFusionIngredients);
            }

            if (Role == P2PRole.Host)
            {
                HandleServerEmit(true, messageData);
            }
            else
            {
                SendWire(new P2PWireMessage
                {
                    Type = "emit",
                    ViewerId = LocalProfile.ViewerId,
                    BattleId = BattleId,
                    Data = messageData
                });
            }
        }

        internal static void HandleHandData(
            RealTimeNetworkAgent agent,
            List<object> parameters,
            NetworkBattleSender.HAND_URI_TYPE uri)
        {
            if (!IsActive)
            {
                return;
            }

            CacheLocalBattleCardIdentities();
            if (BattleSelectionTracker.RecordHandData(
                    (int)uri, parameters, out string selectionSummary))
            {
                Plugin.Logger.LogInfo(
                    "[P2P] Recorded skill selection: " +
                    selectionSummary + ".");
            }

            CaptureLocalChoiceSelection(parameters, uri);

            if (uri != NetworkBattleSender.HAND_URI_TYPE.SELECT_SKILL_URI &&
                uri != NetworkBattleSender.HAND_URI_TYPE.SLIDE_OBJECT_URI)
            {
                return;
            }

            int sequenceNumber = ++localEmitSequence;
            if (agent != null)
            {
                agent.LastEmitSeqNumber = sequenceNumber;
            }

            List<object> stockHandData = new List<object>
            {
                (int)uri,
                LocalProfile?.ViewerId ?? P2PIdentity.ViewerId,
                string.Empty,
                sequenceNumber
            };
            if (parameters != null)
            {
                stockHandData.AddRange(parameters);
            }

            Dictionary<string, object> acknowledgement =
                new Dictionary<string, object>
                {
                    ["StockHandData"] = stockHandData,
                    ["pubSeq"] = sequenceNumber
                };
            Enqueue(() => agent?.OnAck?.Invoke(acknowledgement));
        }

        private static void CaptureLocalChoiceSelection(
            IList<object> parameters,
            NetworkBattleSender.HAND_URI_TYPE uri)
        {
            if (Role != P2PRole.Guest ||
                uri != NetworkBattleSender.HAND_URI_TYPE.SELECT_SKILL_URI ||
                parameters == null || parameters.Count == 0 ||
                !TryConvertAuthorityInt(parameters[0], out int operation))
            {
                return;
            }
            // SELECT_CHOICE_CARD and COMPLETE_CHOICE_SELECT carry the selected
            // hand-card indices as a comma separated operation number.
            if (operation == 0 || operation == 3 || operation == 5)
            {
                localAuthorityChoiceCardIndexes.Clear();
                return;
            }
            if (operation != 4 && operation != 6)
            {
                return;
            }
            if (parameters.Count < 4)
            {
                return;
            }
            string encoded = parameters[3]?.ToString();
            if (string.IsNullOrWhiteSpace(encoded))
            {
                return;
            }
            foreach (string part in encoded.Split(','))
            {
                if (int.TryParse(part, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int index) && index > 0 &&
                    !localAuthorityChoiceCardIndexes.Contains(index))
                {
                    localAuthorityChoiceCardIndexes.Add(index);
                }
            }
        }

        private static List<int> ResolveCachedChoiceIds(BattlePlayerBase player)
        {
            List<int> result = new List<int>();
            if (player == null)
            {
                return result;
            }
            foreach (int index in localAuthorityChoiceCardIndexes)
            {
                BattleCardBase card = NetworkBattleGenericTool.GetIndexToCardBase(
                    BattleManagerBase.GetIns(), player, index);
                if (card != null && card.CardId > 0)
                {
                    result.Add(card.BaseParameter?.CardId ?? card.CardId);
                }
            }
            return result;
        }

        internal static void QueueAcknowledgement(
            RealTimeNetworkAgent agent,
            string uri,
            Dictionary<string, object> data,
            Action onFinished,
            bool isGetableAck,
            bool isStockData,
            int fixedSeqNumber)
        {
            if (!isGetableAck)
            {
                Enqueue(() => onFinished?.Invoke());
                return;
            }

            int sequenceNumber = fixedSeqNumber;
            if (sequenceNumber < 0)
            {
                sequenceNumber = ++localEmitSequence;
                if (agent != null)
                {
                    agent.LastEmitSeqNumber = sequenceNumber;
                }
            }
            data["pubSeq"] = sequenceNumber;
            Dictionary<string, object> acknowledgement = P2PJson.CloneDictionary(data);
            acknowledgement["uri"] = uri;
            acknowledgement["viewerId"] = LocalProfile?.ViewerId ?? P2PIdentity.ViewerId;
            acknowledgement["bid"] = BattleId ?? string.Empty;
            Enqueue(() =>
            {
                try
                {
                    agent?.OnAck?.Invoke(acknowledgement);
                    if (agent != null &&
                        string.Equals(uri, NetworkBattleDefine.NetworkBattleURI.TurnStart.ToString(),
                            StringComparison.Ordinal))
                    {
                        agent.AddActionSequence();
                    }
                }
                finally
                {
                    onFinished?.Invoke();
                }
            });
        }

        private static void ResetBattleState()
        {
            hostInitBattle = false;
            guestInitBattle = false;
            matchedSent = false;
            hostLoaded = false;
            guestLoaded = false;
            battleStartSent = false;
            battleStartReceived = false;
            mulliganReadyReceived = false;
            finishResultSent = false;
            retiringHost = null;
            localRetired = false;
            shuffledHostDeck = null;
            shuffledGuestDeck = null;
            BattleCardTracker.Clear();
            LocalHiddenCardStateSignatures.Clear();
            LocalHiddenCardStates.Clear();
            ReceivedHiddenCardStates.Clear();
            PendingReceivedHiddenCardStates.Clear();
            PostActionHiddenCardStateKeys.Clear();
            NativeBaselineHiddenCardStateKeys.Clear();
            ReportedMissingNativePrivateIdentities.Clear();
            ReceivedHiddenCardStateSignatures.Clear();
            AppliedReceivedHiddenCardStates.Clear();
            NativePromotedReceivedHiddenCardStateSignatures.Clear();
            NativeReplacedReceivedHiddenCardStateSignatures.Clear();
            PrivateConditionWarnings.Clear();
            localPrivateStateSent = false;
            localPrivateStateAcknowledged = false;
            remotePrivateStateReceived = false;
            PendingFusionActions.Clear();
            PendingFusionActionSignatures.Clear();
            StagedPreNativeFusionActions.Clear();
            LocalAuthoritativeSkillTargets.Clear();
            PendingAuthoritativeSkillTargetBatches.Clear();
            activeAuthoritativeSkillTargetBatch = null;
            currentInjectedAuthoritativeSkillTargetBatch = null;
            localAuthoritativeSkillTargetSequence = 0;
            receivedAuthoritativeActionActive = false;
            LocalAuthoritativeSkillEvaluations.Clear();
            PendingAuthoritativeSkillEvaluationBatches.Clear();
            activeAuthoritativeSkillEvaluationBatch = null;
            currentInjectedAuthoritativeSkillEvaluationBatch = null;
            AuthoritativeSkillEvaluationScopes.Clear();
            PendingLocalConditionResults.Clear();
            localAuthoritativeSkillEvaluationSequence = 0;
            receivedAuthoritativeSkillEvaluationActive = false;
            localActionManifestSequence = 0;
            localActionCaptureActive = false;
            processingReceivedBattleAction = false;
            receivedBattleActionInjectionPending = false;
            receivedBattleActionPendingUntilVfx = false;
            receivedBattleActionOperationStarted = false;
            receivedBattleActionStartedUtc = DateTime.MinValue;
            receivedBattleActionStallReported = false;
            nativeReceivedMetadataActive = false;
            currentAuthorityReplayData = null;
            PendingReceivedBattleMessages.Clear();
            PendingAuthorityReplayActions.Clear();
            authorityReplayDispatchActive = false;
            LocalFusionIngredientSnapshots.Clear();
            ReceivedPlayerHistoryStates.Clear();
            PendingPreActionPlayerHistoryStates.Clear();
            AppliedPlayerHistoryRevisions.Clear();
            authorityTransitionSequence = 0;
            authorityResultActionSequence = 0;
            authorityGuestKnownIndices.Clear();
            foreach (HashSet<int> indices in authorityKnownPrivateIndicesByOwner.Values)
            {
                indices.Clear();
            }
            authorityPrivateStateSignatures.Clear();
            authorityPrivateStates.Clear();
            actionPreHiddenCardStates.Clear();
            authorityPlayerHistorySignatures.Clear();
            authorityPlayerHistoryRevisions.Clear();
            // A new RoomReady boundary starts a fresh battle while the TCP
            // session (and therefore BattleId) remains alive.  Request IDs are
            // only unique within one battle round; retaining the old dedupe
            // table would silently drop a valid request in the next round.
            processedAuthorityRequests.Clear();
            authorityRequestTimes.Clear();
            completedAuthorityRequestIds.Clear();
            appliedAuthorityResultBoundaries.Clear();
            guestAuthorityBusy = false;
            guestAuthorityRequestId = null;
            guestAuthorityRequestAction = null;
            guestAuthorityRequestSentUtc = DateTime.MinValue;
            receivedAuthorityRequestId = null;
            activeReceivedBattleActionUri = null;
            activeAuthorityExecutionRequestId = null;
            activeAuthorityExecutionStartedUtc = DateTime.MinValue;
            authorityLocalReplayActive = false;
            authorityRandomAttackCandidates = null;
            authorityResolvedAttackTarget = null;
            authorityReplayRandomAttackCandidateCount = 0;
            authorityReplayRandomAttackTargetIndex = -1;
            AuthorityActionProcessors.Clear();
            localPlayerHistoryStateSignature = string.Empty;
            localPlayerHistoryRevision = 0;
            localPlayerHistoryBaselineSignature = string.Empty;
            localPlayerHistoryBaselineState = null;
            localPlayerHistoryBaselineRevision = 0;
            localActionPreHistoryState = null;
            localActionPreHistoryRevision = 0;
            BattleSelectionTracker.Reset();
            PendingBattleStateChecks.Clear();
            hostMulliganHand = null;
            guestMulliganHand = null;
            hostSwapped = false;
            guestSwapped = false;
            mulliganReadySent = false;
            battleSeed = 0;
            DealState.Reset();
            P2PAuthoritativeServer.Reset();
        }

        internal static void FailJoin(string error)
        {
            LastError = string.IsNullOrWhiteSpace(error)
                ? "The room join failed."
                : error;
            JoinFinished = true;
            JoinSucceeded = false;
            transport?.Stop(false);
            Plugin.Logger.LogError("[P2P] Room join failed: " + LastError);
        }

        internal static void AbortFailedJoin()
        {
            string error = LastError;
            ResetSession();
            LastError = error;
        }

        internal static void LeaveRoom(string reason)
        {
            if (transport != null)
            {
                transport.Send(new P2PWireMessage
                {
                    Type = "close",
                    Error = reason
                });
            }
            ResetSession();
        }

        internal static void Shutdown()
        {
            ResetSession();
        }

        private static void CreateTransport()
        {
            int generation = sessionGeneration;
            transport = new P2PTransport();
            transport.Connected += () => Enqueue(generation, OnTransportConnected);
            transport.MessageReceived += message =>
                Enqueue(generation, () => HandleWireMessage(message));
            transport.Disconnected += error =>
                Enqueue(generation, () => OnTransportDisconnected(error));
        }

        private static void OnTransportConnected()
        {
            Plugin.Logger.LogInfo($"[P2P] Transport authenticated as {Role}.");
            if (Role == P2PRole.Guest)
            {
                SendWire(new P2PWireMessage
                {
                    Type = "join",
                    ViewerId = LocalProfile.ViewerId,
                    Profile = LocalProfile
                });
            }
        }

        internal static void RememberLocalCardMutation(
            int playIndex,
            int originalCardId,
            int originalCost,
            int mutationCardId,
            int mutationCost,
            int keyActionType)
        {
            RememberCardMutationForOwner(
                Role == P2PRole.Host,
                playIndex,
                originalCardId,
                originalCost,
                mutationCardId,
                mutationCost,
                keyActionType);
        }

        private static void RememberCardMutationForOwner(
            bool ownerIsHost,
            int playIndex,
            int originalCardId,
            int originalCost,
            int mutationCardId,
            int mutationCost,
            int keyActionType)
        {
            if (!IsActive)
            {
                return;
            }

            bool recorded = BattleCardTracker.RememberSourceCardMutation(
                ownerIsHost,
                playIndex,
                originalCardId,
                originalCost,
                mutationCardId,
                mutationCost,
                keyActionType);
            if (!recorded)
            {
                return;
            }

            Plugin.Logger.LogInfo(
                $"[P2P] Recorded {SideName(ownerIsHost)} card mutation: " +
                $"playIdx={playIndex}, " +
                $"type={keyActionType}, originalCardId={originalCardId}, " +
                $"originalCost={originalCost}, mutationCardId={mutationCardId}, " +
                $"mutationCost={mutationCost}.");
        }

        private static void OnTransportDisconnected(string error)
        {
            LastError = error;
            if (Role == P2PRole.Guest && !JoinFinished)
            {
                JoinFinished = true;
                JoinSucceeded = false;
            }
            else
            {
                HandlePeerDisconnected(error);
            }
            Plugin.Logger.LogWarning("[P2P] " + error);
        }

        private static void HandleWireMessage(P2PWireMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.Type))
            {
                return;
            }
            switch (message.Type)
            {
                case "join":
                    if (Role != P2PRole.Host || message.Profile == null)
                    {
                        return;
                    }
                    if (message.Profile.ViewerId == LocalProfile.ViewerId)
                    {
                        SendWire(new P2PWireMessage
                        {
                            Type = "join_reject",
                            Error = "Both players have the same local P2P identity."
                        });
                        return;
                    }
                    GuestDeliverySequence.Reset();
                    DeferredGuestDeliveries.Clear();
                    guestPlaySequence = 0;
                    RemoteProfile = message.Profile;
                    pendingOpponentSync = true;
                    SendWire(new P2PWireMessage
                    {
                        Type = "join_ok",
                        ViewerId = LocalProfile.ViewerId,
                        BattleId = BattleId,
                        Profile = LocalProfile,
                        Rules = Rules,
                        Data = new Dictionary<string, object> { ["roomId"] = RoomId }
                    });
                    Plugin.Logger.LogInfo($"[P2P] Guest '{RemoteProfile.UserName}' joined the transport session.");
                    break;
                case "join_ok":
                    HandleJoinAccepted(message);
                    break;
                case "rules_update":
                    if (Role != P2PRole.Guest || message.Rules == null)
                    {
                        return;
                    }
                    ApplyReceivedRoomRules(message.Rules);
                    Plugin.Logger.LogInfo(
                        $"[P2P] Received room rule update: " +
                        $"format={Rules.CustomFormatId}; initialMaxLife={Rules.InitialMaxLife}.");
                    break;
                case "join_reject":
                    LastError = message.Error ?? "The room host rejected the connection.";
                    JoinFinished = true;
                    JoinSucceeded = false;
                    transport?.Stop(false);
                    break;
                case "deck":
                    if (Role == P2PRole.Host)
                    {
                        RemoteDeck = message.Deck;
                        TrySendMatched();
                    }
                    break;
                case "emit":
                    if (Role == P2PRole.Host && message.Data != null)
                    {
                        HandleServerEmit(false, message.Data);
                    }
                    break;
                case P2PBattleProtocol.AuthorityRequestUri:
                    if (Role == P2PRole.Host)
                    {
                        // New P2P rounds use the native sender/receiver packet
                        // flow exclusively. Do not reactivate the former
                        // request/result replay architecture because a delayed
                        // legacy frame can otherwise suppress native emits and
                        // strand the current battle in an authority VFX gate.
                        SendAuthorityReject(message.RequestId,
                            "legacy authority requests are not supported by this P2P protocol version");
                        Plugin.Logger.LogWarning(
                            "[P2P] Rejected a legacy authority request; native " +
                            "client timing is required for this battle.");
                    }
                    break;
                case P2PBattleProtocol.AuthorityRejectUri:
                    Plugin.Logger.LogWarning(
                        "[P2P] Ignored a legacy authority rejection packet.");
                    break;
                case P2PBattleProtocol.AuthorityAckUri:
                    Plugin.Logger.LogWarning(
                        "[P2P] Ignored a legacy authority acknowledgement packet.");
                    break;
                case "deliver":
                    if (Role == P2PRole.Guest && message.Data != null)
                    {
                        if (!IsCurrentBattleMessage(message))
                        {
                            return;
                        }
                        string deliveredUri = GetUri(message.Data);
                        if (string.Equals(
                                deliveredUri,
                                PlayerController.ROOM_URI.RoomEntry.ToString(),
                                StringComparison.Ordinal))
                        {
                            Plugin.Logger.LogInfo(
                                $"[P2P] Received RoomEntry from the host; " +
                                $"agentReady={currentAgent != null}.");
                        }
                        if (message.Data.TryGetValue("playSeq", out object playSequence))
                        {
                            guestPlaySequence = Math.Max(guestPlaySequence,
                                Convert.ToInt32(playSequence));
                        }
                        if (currentAgent == null)
                        {
                            DeferAgentDelivery(message.Data);
                        }
                        else
                        {
                            Inject(message.Data);
                        }
                    }
                    break;
                case "private_state":
                    if (message.Data != null)
                    {
                        if (Role == P2PRole.Host)
                        {
                            // Host owns the authoritative protocol cache. Keep
                            // the Guest baseline here as wire data, before any
                            // legacy client-side compatibility adapter touches
                            // BattleEnemy card objects.
                            P2PAuthoritativeServer.RememberPrivateStateSnapshot(
                                message.Data);
                        }
                        RememberReceivedPrivateStateSnapshot(message.Data);
                        if (TryGetStateInt(message.Data, "owner",
                                out int receivedOwner) &&
                            (receivedOwner == 0 || receivedOwner == 1))
                        {
                            // A sender may omit its full private baseline only
                            // after the receiver has acknowledged this exact
                            // owner. Acknowledging both directions also closes
                            // the startup race where the Host emits its first
                            // action before the Guest has installed the Host
                            // hand/deck identity table.
                            SendPrivateStateAcknowledgement(receivedOwner);
                        }
                        // The host answers the guest's baseline with its own
                        // baseline. If cards are not loaded yet, Update() will
                        // retry this after the battle manager becomes ready.
                        if (Role == P2PRole.Host)
                        {
                            TrySendInitialPrivateStateSnapshot();
                        }
                    }
                    break;
                case P2PBattleProtocol.PrivateStateAckUri:
                    if (message.Data == null ||
                        !TryGetStateInt(message.Data, "owner",
                            out int acknowledgedOwner) ||
                        (acknowledgedOwner != 0 && acknowledgedOwner != 1) ||
                        acknowledgedOwner != (Role == P2PRole.Host ? 1 : 0))
                    {
                        // Ignore malformed acknowledgements and acknowledgements
                        // for the peer's baseline. The sender must only mark its
                        // local baseline as acknowledged.
                        break;
                    }
                    localPrivateStateAcknowledged = true;
                    Plugin.Logger.LogDebug(
                        "[P2P] Peer acknowledged the local private-state baseline " +
                        "(owner=" + acknowledgedOwner + ").");
                    break;
                case "diagnostic":
                    if (Role == P2PRole.Host && !string.IsNullOrEmpty(message.Error))
                    {
                        bool isDesync = IsDesyncDiagnostic(message.Error);
                        string severity = message.Data != null &&
                            message.Data.TryGetValue("severity", out object rawSeverity)
                            ? rawSeverity?.ToString()
                            : null;
                        if (string.Equals(severity, "error",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            isDesync = true;
                        }
                        if (isDesync)
                        {
                            Plugin.Logger.LogError(
                                "[P2P] Remote client diagnostic: " + message.Error);
                        }
                        else
                        {
                            Plugin.Logger.LogWarning(
                                "[P2P] Remote client diagnostic: " + message.Error);
                        }
                    }
                    break;
                case "close":
                    transport?.Stop(false);
                    HandlePeerDisconnected(
                        message.Error ?? "The room was closed by the other player.");
                    break;
            }
        }

        private static void HandleJoinAccepted(P2PWireMessage message)
        {
            if (Role != P2PRole.Guest)
            {
                return;
            }

            Plugin.Logger.LogInfo("[P2P] Received join_ok; synchronizing room rules.");
            try
            {
                if (message.Profile == null)
                {
                    throw new InvalidDataException(
                        "The room host did not provide its player profile.");
                }
                if (message.Rules == null)
                {
                    throw new InvalidDataException(
                        "The room host did not provide room rules.");
                }
                if (string.IsNullOrWhiteSpace(message.BattleId))
                {
                    throw new InvalidDataException(
                        "The room host did not provide a battle ID.");
                }

                string receivedRoomId = message.Data != null &&
                    message.Data.TryGetValue("roomId", out object roomId)
                        ? roomId?.ToString()
                        : message.BattleId;
                if (string.IsNullOrWhiteSpace(receivedRoomId))
                {
                    throw new InvalidDataException(
                        "The room host did not provide a room ID.");
                }

                ApplyReceivedRoomRules(message.Rules);
                RemoteProfile = message.Profile;
                BattleId = message.BattleId;
                RoomId = receivedRoomId;
                pendingOpponentSync = true;
                LastError = null;
                JoinSucceeded = true;
                JoinFinished = true;
                Plugin.Logger.LogInfo(
                    $"[P2P] Joined room hosted by '{RemoteProfile.UserName}' " +
                    $"(format={Rules.CustomFormatId}, openDeck={Rules.IsDeckOpen}, " +
                    $"twoPick={Rules.TwoPickType}, " +
                    $"twoPickRule={Rules.TwoPickRule?.Id ?? string.Empty}, " +
                    $"draftSize={Rules.TwoPickRule?.FinalDeckSize ?? 0}, " +
                    $"initialMaxLife={Rules.InitialMaxLife}).");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    "[P2P] Failed to apply the host's room rules: " + ex);
                FailJoin("The host's room rules could not be synchronized: " + ex.Message);
            }
        }

        private static void ApplyReceivedRoomRules(P2PRoomRules receivedRules)
        {
            P2PRoomRules synchronizedRules = receivedRules ??
                throw new ArgumentNullException(nameof(receivedRules));
            if (synchronizedRules.TwoPickType == (int)TwoPickFormat.Normal)
            {
                if (synchronizedRules.BattleType !=
                    (int)NetworkDefine.ServerBattleType.RoomTwoPick)
                {
                    throw new FormatException(
                        "Normal Two Pick rules require the RoomTwoPick battle type.");
                }
                synchronizedRules.TwoPickRule = P2PTwoPickRules.Normalize(
                    synchronizedRules.TwoPickRule ?? P2PTwoPickRules.Load());
            }
            CustomFormatDefinition definition = null;
            if (synchronizedRules.FormatDefinition != null)
            {
                try
                {
                    definition = CustomFormats.InstallRoomDefinition(
                        synchronizedRules.FormatDefinition.Clone());
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError(
                        "[P2P] Rejected the host's format definition: " + ex.Message);
                }
            }

            definition = definition ?? CustomFormats.Get(synchronizedRules.CustomFormatId);
            synchronizedRules.CustomFormatId = definition.Id;
            synchronizedRules.FormatDefinition = definition.Clone();

            if (synchronizedRules.TwoPickType == (int)TwoPickFormat.Normal)
            {
                P2PTwoPickRules.ResetDraft(synchronizedRules.TwoPickRule);
            }

            Rules = synchronizedRules;
            CustomFormatContext.RoomFormatId = definition.Id;
            Plugin.Logger.LogInfo(
                $"[P2P] Room rules synchronized: battleType={Rules.BattleType}, " +
                $"deckFormat={Rules.DeckFormat}, twoPick={Rules.TwoPickType}, " +
                $"twoPickRule={Rules.TwoPickRule?.Id ?? string.Empty}, " +
                $"draftSize={Rules.TwoPickRule?.FinalDeckSize ?? 0}.");
        }

        private static void HandleServerEmit(bool sourceIsHost, Dictionary<string, object> data)
        {
            string uri = data["uri"].ToString();
            if (TryGetRoomUri(data, uri, out PlayerController.ROOM_URI roomUri))
            {
                HandleRoomEmit(sourceIsHost, roomUri, data);
                return;
            }

            if (uri == NetworkBattleDefine.NetworkBattleURI.InitNetwork.ToString())
            {
                return;
            }
            if (uri == NetworkBattleDefine.NetworkBattleURI.InitRoomBattle.ToString() ||
                uri == NetworkBattleDefine.NetworkBattleURI.InitBattle.ToString())
            {
                if (sourceIsHost) hostInitBattle = true; else guestInitBattle = true;
                Plugin.Logger.LogInfo(
                    $"[P2P] {SideName(sourceIsHost)} initialized the battle session ({uri}).");
                TrySendMatched();
                return;
            }
            if (uri == NetworkBattleDefine.NetworkBattleURI.Loaded.ToString())
            {
                if (sourceIsHost) hostLoaded = true; else guestLoaded = true;
                Plugin.Logger.LogInfo(
                    $"[P2P] {SideName(sourceIsHost)} finished loading the battle scene.");
                TrySendBattleStart();
                return;
            }
            if (uri == NetworkBattleDefine.NetworkBattleURI.Deal.ToString())
            {
                SendDeal(sourceIsHost);
                return;
            }
            if (uri == NetworkBattleDefine.NetworkBattleURI.Swap.ToString())
            {
                HandleSwap(sourceIsHost, data);
                return;
            }
            if (uri == NetworkBattleDefine.NetworkBattleURI.Retire.ToString())
            {
                retiringHost = sourceIsHost;
                Dictionary<string, object> retireOther = P2PJson.CloneDictionary(data);
                retireOther["isWin"] = 1;
                Deliver(!sourceIsHost, retireOther, SourceViewerId(sourceIsHost), true);
                return;
            }
            if (uri == NetworkBattleDefine.NetworkBattleURI.JudgeResult.ToString())
            {
                SendFinishResult(sourceIsHost, data);
                return;
            }

            if (!P2PAuthoritativeServer.TryCreateAction(
                    sourceIsHost,
                    SourceViewerId(sourceIsHost),
                    data,
                    out P2PHostAuthoritativeAction action,
                    out string actionError))
            {
                Plugin.Logger.LogError(
                    "[P2P] Host rejected malformed client battle request: " +
                    actionError + ".");
                return;
            }

            bool revealed = P2PBattleProtocol.TryReadPreparedAction(
                action.Request.Data, out int playIndex, out int cardId);
            if (!revealed && uri == NetworkBattleDefine.NetworkBattleURI.PlayActions.ToString())
            {
                // Protocol v3 requires the originating NetworkBattleSender
                // path to supply native knownList/orderList/uList information
                // before the request reaches Host. Do not reconstruct a Guest
                // action from the Host client's BattleEnemy here: that was the
                // former custom replay path and can bind a response to an
                // already-mutated local card object.
                Plugin.Logger.LogDebug(
                    "[P2P] Host response actionId=" + action.ServerActionId +
                    " has no prepared play-card marker; preserving the native " +
                    "request envelope without Host-side card reconstruction.");
            }

            if (uri == NetworkBattleDefine.NetworkBattleURI.PlayActions.ToString())
            {
                Plugin.Logger.LogInfo(
                    $"[P2P] Host accepted client PlayActions from {SideName(sourceIsHost)}: " +
                    $"playIdx={playIndex}, cardId={(revealed ? cardId : 0)}, " +
                    $"requestSeq={action.Request.SourceSequence}, " +
                    $"serverActionId={action.ServerActionId}, " +
                    $"keys=[{string.Join(",", action.Request.Data.Keys)}]; " +
                    P2PBattleStateDiagnostics.DescribeBattleMessage(
                        action.ServerResponse) + ".");
            }

            if (!P2PAuthoritativeServer.TryCreateDelivery(
                    action, out P2PServerBattleDelivery delivery))
            {
                Plugin.Logger.LogInfo(
                    $"[P2P] Host consumed {uri} confirmation from " +
                    $"{SideName(sourceIsHost)}.");
                return;
            }

            if (uri == NetworkBattleDefine.NetworkBattleURI.TurnEndActions.ToString() ||
                uri == NetworkBattleDefine.NetworkBattleURI.TurnEnd.ToString() ||
                uri == NetworkBattleDefine.NetworkBattleURI.TurnStart.ToString() ||
                uri == NetworkBattleDefine.NetworkBattleURI.Judge.ToString())
            {
                Plugin.Logger.LogInfo(
                    $"[P2P] Host delivered actionId={delivery.ServerActionId}, {uri} " +
                    $"from {SideName(sourceIsHost)} to " +
                    $"{(delivery.ToHost ? "Host" : "Guest")} (turnState=0).");
            }
            Deliver(delivery.ToHost, delivery.Data, delivery.SourceViewerId);

            // Match the stock server/client turn transition. The client that
            // receives TurnEnd runs TurnEndOperation(false) and emits Judge;
            // the server routes that Judge back to its source. The source then
            // runs NetworkOperationCollection.JudgeOperation, which invokes
            // ControlTurnStartPlayer through the original state machine.
        }

        private static bool IsBattleFinished(NetworkBattleManagerBase manager)
        {
            if (manager == null)
            {
                return false;
            }
            try
            {
                return P2PBattleResult.IsTerminalResult(
                    (int)manager.JudgeCurrentFinishStatus());
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not evaluate the battle finish boundary: " +
                    ex.Message);
                return false;
            }
        }

        internal static bool TryInterceptGuestPlayCard(
            BattleCardBase card,
            bool isPlayer,
            List<BattleCardBase> selectedCards,
            bool isRecovery,
            List<int> selectChoiceId,
            bool isChoiceBrave,
            out Wizard.Battle.View.Vfx.VfxBase result)
        {
            result = null;
            if (UseNativeClientActionTiming)
            {
                // Let NetworkStandardBattleMgr -> NetworkBattleSender run.
                // It executes the Guest's action immediately and emits the
                // original PlayActions packet from the normal callback.
                return false;
            }
            if (ShouldBlockGuestAuthorityAction(isPlayer, isRecovery))
            {
                // A second touch can arrive while the Host is still executing
                // the previous request.  Swallow it instead of letting the
                // native OperateMgr mutate the Guest's local mirror.
                result = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
                return true;
            }
            if (!ShouldInterceptGuestAction(isPlayer, isRecovery))
            {
                return false;
            }
            if (card == null || !IsValidAuthorityActor(card))
            {
                RejectLocalAuthorityAction("the selected card is unavailable");
                result = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
                return true;
            }
            List<int> choiceIds = selectChoiceId == null || selectChoiceId.Count == 0
                ? ResolveCachedChoiceIds(card.SelfBattlePlayer)
                : new List<int>(selectChoiceId);
            List<int> selectSkillIndexes =
                BattleSelectionTracker.TakeAuthoritySkillIndexes();
            SendAuthorityRequest("play", card, selectedCards, null, choiceIds,
                isChoiceBrave, selectSkillIndexes);
            result = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
            return true;
        }

        internal static bool TryInterceptGuestEvolution(
            BattleCardBase card,
            bool isPlayer,
            List<BattleCardBase> selectedCards,
            List<int> selectChoiceId,
            out Wizard.Battle.View.Vfx.VfxBase result)
        {
            result = null;
            if (UseNativeClientActionTiming)
            {
                return false;
            }
            if (ShouldBlockGuestAuthorityAction(isPlayer, false))
            {
                result = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
                return true;
            }
            if (!ShouldInterceptGuestAction(isPlayer, false))
            {
                return false;
            }
            if (card == null || !IsValidAuthorityActor(card))
            {
                RejectLocalAuthorityAction("the selected evolution card is unavailable");
                result = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
                return true;
            }
            List<int> choiceIds = selectChoiceId == null || selectChoiceId.Count == 0
                ? ResolveCachedChoiceIds(card.SelfBattlePlayer)
                : new List<int>(selectChoiceId);
            List<int> selectSkillIndexes =
                BattleSelectionTracker.TakeAuthoritySkillIndexes();
            SendAuthorityRequest("evolution", card, selectedCards, null, choiceIds,
                false, selectSkillIndexes);
            result = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
            return true;
        }

        internal static bool TryInterceptGuestFusion(
            BattleCardBase card,
            bool isPlayer,
            List<BattleCardBase> selectedCards,
            out Wizard.Battle.View.Vfx.VfxBase result)
        {
            result = null;
            if (UseNativeClientActionTiming)
            {
                return false;
            }
            if (ShouldBlockGuestAuthorityAction(isPlayer, false))
            {
                result = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
                return true;
            }
            if (!ShouldInterceptGuestAction(isPlayer, false))
            {
                return false;
            }
            if (card == null || !IsValidAuthorityActor(card) || selectedCards == null ||
                selectedCards.Count == 0)
            {
                RejectLocalAuthorityAction("the fusion card or its ingredients are unavailable");
                result = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
                return true;
            }
            List<int> selectSkillIndexes =
                BattleSelectionTracker.TakeAuthoritySkillIndexes();
            SendAuthorityRequest("fusion", card, selectedCards, null, null, false,
                selectSkillIndexes);
            result = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
            return true;
        }

        internal static bool TryInterceptGuestAttack(
            BattleCardBase attacker,
            BattleCardBase target,
            bool isPlayer,
            out Wizard.Battle.View.Vfx.VfxBase result)
        {
            result = null;
            if (UseNativeClientActionTiming)
            {
                return false;
            }
            if (ShouldBlockGuestAuthorityAction(isPlayer, false))
            {
                result = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
                return true;
            }
            if (!ShouldInterceptGuestAction(isPlayer, false))
            {
                return false;
            }
            // Leaders use the reserved native index 0.  They are valid attack
            // sources/targets even though they are not present in a hand/deck
            // card list, so validate them through the same class-card rule used
            // by Choice Brave instead of requiring a positive zone index.
            if (!IsValidAuthorityActor(attacker) ||
                !IsValidAuthorityActor(target))
            {
                RejectLocalAuthorityAction("the attack target is unavailable");
                result = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
                return true;
            }
            SendAuthorityRequest("attack", attacker, null, target, null, false,
                null);
            result = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
            return true;
        }

        internal static bool TryInterceptGuestTurnEnd(
            bool isPlayer,
            bool isAuto,
            out Wizard.Battle.View.Vfx.VfxBase result)
        {
            result = null;
            if (UseNativeClientActionTiming)
            {
                return false;
            }
            if (ShouldBlockGuestAuthorityAction(isPlayer, false))
            {
                result = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
                return true;
            }
            if (!ShouldInterceptGuestAction(isPlayer, false))
            {
                return false;
            }
            SendAuthorityRequest("turn_end", null, null, null, null, false, null);
            result = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
            return true;
        }

        private static bool ShouldInterceptGuestAction(bool isPlayer, bool isRecovery)
        {
            return IsGuestAuthorityInput(isPlayer, isRecovery) &&
                !guestAuthorityBusy;
        }

        private static bool ShouldBlockGuestAuthorityAction(
            bool isPlayer,
            bool isRecovery)
        {
            return IsGuestAuthorityInput(isPlayer, isRecovery) &&
                guestAuthorityBusy;
        }

        private static bool IsGuestAuthorityInput(bool isPlayer, bool isRecovery)
        {
            return IsHostAuthorityMode && Role == P2PRole.Guest && isPlayer &&
                !isRecovery && !authorityLocalReplayActive && !peerDisconnected;
        }

        private static bool IsValidAuthorityActor(BattleCardBase card)
        {
            if (card == null || card.SelfBattlePlayer == null)
            {
                return false;
            }

            // The class/Choice Brave card uses the reserved native index 0;
            // all other playable cards must have a positive zone index.
            return card.Index > 0 ||
                ReferenceEquals(card, card.SelfBattlePlayer.Class);
        }

        private static void RejectLocalAuthorityAction(string reason)
        {
            guestAuthorityBusy = false;
            guestAuthorityRequestId = null;
            guestAuthorityRequestAction = null;
            guestAuthorityRequestSentUtc = DateTime.MinValue;
            LastError = reason;
            Plugin.Logger.LogWarning("[P2P] Local authority action rejected: " + reason + ".");
            TryEnableLocalBattleMenu();
        }

        private static void SendAuthorityRequest(
            string action,
            BattleCardBase card,
            IEnumerable<BattleCardBase> selectedCards,
            BattleCardBase target,
            IEnumerable<int> choiceIds,
            bool choiceBrave,
            IEnumerable<int> selectSkillIndexes)
        {
            if (Role != P2PRole.Guest || transport == null)
            {
                return;
            }

            string requestId = string.Format(
                CultureInfo.InvariantCulture,
                "{0}-{1}-{2}",
                BattleId ?? "battle",
                P2PIdentity.ViewerId,
                ++authorityRequestSequence);
            Dictionary<string, object> request = new Dictionary<string, object>
            {
                [P2PBattleProtocol.AuthorityRequestIdKey] = requestId,
                [P2PBattleProtocol.AuthorityActionKey] = action,
                [P2PBattleProtocol.AuthoritySourceKey] = 0,
                [P2PBattleProtocol.AuthorityTurnKey] = GetCurrentTurnNumber(),
                // Keep the envelope and payload bound to the same round. This
                // is not an anti-cheat measure; it prevents a delayed TCP
                // frame from a prior RoomReady round from being executed by
                // the current Host battle.
                ["bid"] = BattleId ?? string.Empty
            };
            if (card != null)
            {
                request[P2PBattleProtocol.AuthorityCardIndexKey] = card.Index;
                request["cardId"] = card.CardId;
                request["cost"] = card.Cost;
            }
            if (target != null)
            {
                request[P2PBattleProtocol.AuthorityTargetKey] = CaptureAuthorityReference(target);
            }
            request[P2PBattleProtocol.AuthorityTargetsKey] = CaptureAuthorityReferences(selectedCards);
            request[P2PBattleProtocol.AuthoritySelectedKey] = CaptureAuthorityReferences(selectedCards);
            request[P2PBattleProtocol.AuthorityChoiceKey] = (choiceIds ?? Enumerable.Empty<int>())
                .Where(value => value > 0).Select(value => (object)value).ToList();
            request[P2PBattleProtocol.AuthorityIsChoiceBraveKey] = choiceBrave ? 1 : 0;
            request[P2PBattleProtocol.AuthoritySelectSkillKey] =
                (selectSkillIndexes ?? Enumerable.Empty<int>())
                    .Where(value => value >= 0)
                    .Distinct()
                    .Select(value => (object)value)
                    .ToList();
            // The complete Guest hand/deck baseline is sent once and
            // acknowledged by the Host.  Until that acknowledgement arrives,
            // keep including the cards as a safe fallback; afterwards only the
            // compact history snapshot is needed for condition evaluation.
            request[P2PBattleProtocol.AuthorityStateKey] =
                CaptureAuthorityPrivateState(
                    !localPrivateStateSent || !localPrivateStateAcknowledged);

            guestAuthorityBusy = true;
            guestAuthorityRequestId = requestId;
            guestAuthorityRequestAction = action;
            guestAuthorityRequestSentUtc = DateTime.UtcNow;
            authorityRequestTimes[requestId] = DateTime.UtcNow;
            if (!SendWire(new P2PWireMessage
            {
                Type = P2PBattleProtocol.AuthorityRequestUri,
                RequestId = requestId,
                ActionSeq = authorityRequestSequence,
                ViewerId = LocalProfile?.ViewerId ?? P2PIdentity.ViewerId,
                BattleId = BattleId,
                Data = request
            }))
            {
                RejectLocalAuthorityAction("the P2P connection is not available");
                return;
            }
            TryDisableLocalBattleMenu();
            Plugin.Logger.LogDebug("[P2P] Authority request sent: " + action + " requestId=" + requestId + ".");
        }

        private static Dictionary<string, object> CaptureAuthorityPrivateState(
            bool includeCards)
        {
            NetworkBattleManagerBase manager = BattleManagerBase.GetIns() as NetworkBattleManagerBase;
            List<object> cards = new List<object>();
            if (includeCards && manager != null && manager.BattlePlayer != null)
            {
                foreach (BattleCardBase card in EnumeratePrivateCards(manager.BattlePlayer))
                {
                    if (card == null || card.Index <= 0 || card.CardId <= 0)
                    {
                        continue;
                    }
                    cards.Add(CreateHiddenCardState(card));
                }
            }
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["owner"] = 0
            };
            if (includeCards)
            {
                payload["cards"] = cards;
            }
            if (manager?.BattlePlayer != null)
            {
                // The request is the Guest's complete authoritative input.  A
                // card condition can depend on persistent history (destroyed,
                // fused, evolved, resonance, etc.), so the Host must consume
                // that history together with the hand/deck snapshot instead of
                // evaluating against a stale local copy.
                try
                {
                    payload["history"] = CapturePlayerHistoryState(
                        manager.BattlePlayer, false);
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogDebug(
                        "[P2P] Could not capture authority request history: " +
                        ex.Message);
                }
            }
            return payload;
        }

        private static Dictionary<string, object> CaptureAuthorityReference(BattleCardBase card)
        {
            // BattleCardBase.IsPlayer is relative to the current process.  The
            // authority wire format uses an absolute owner (Host=1,
            // Guest=0), so convert through the local role before serializing a
            // reference.  Without this conversion every Guest reference was
            // assigned to Host and attack/selection targets were resolved from
            // the wrong player's zones.
            bool ownerIsHost = card != null &&
                card.IsPlayer == (Role == P2PRole.Host);
            Dictionary<string, object> reference =
                new Dictionary<string, object>
            {
                ["owner"] = ownerIsHost ? 1 : 0,
                ["idx"] = card?.Index ?? -1
            };
            // The native class/leader object uses the reserved index 0 and
            // does not have a normal card ID. Sending cardId=0 turns a valid
            // leader target into an invalid card reference on the Host.
            if (card != null && card.Index > 0 && card.CardId > 0)
            {
                reference["cardId"] = card.CardId;
            }
            return reference;
        }

        private static List<object> CaptureAuthorityReferences(IEnumerable<BattleCardBase> cards)
        {
            return cards == null
                ? new List<object>()
                : cards.Where(IsValidAuthorityActor)
                    .Select(card => (object)CaptureAuthorityReference(card)).ToList();
        }

        private static int GetCurrentTurnNumber()
        {
            try
            {
                return (BattleManagerBase.GetIns() as NetworkBattleManagerBase)?.CurrentTurn ?? 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void HandleAuthorityReject(P2PWireMessage message)
        {
            if (!IsCurrentBattleMessage(message))
            {
                return;
            }
            string requestId = message?.RequestId;
            if (string.IsNullOrEmpty(requestId))
            {
                // A reject without an ID cannot be associated with a pending
                // request.  Do not unlock the input gate on an unrelated or
                // delayed malformed frame.
                Plugin.Logger.LogWarning(
                    "[P2P] Ignoring an authority rejection without requestId.");
                return;
            }
            if (string.Equals(requestId, guestAuthorityRequestId,
                    StringComparison.Ordinal))
            {
                completedAuthorityRequestIds.Add(requestId);
                TrimAuthorityResultHistory();
                guestAuthorityBusy = false;
                guestAuthorityRequestId = null;
                guestAuthorityRequestAction = null;
                guestAuthorityRequestSentUtc = DateTime.MinValue;
                receivedAuthorityRequestId = null;
                localAuthorityChoiceCardIndexes.Clear();
                LastError = message?.Error ?? "The host rejected the action.";
                TryEnableLocalBattleMenu();
            }
            Plugin.Logger.LogWarning("[P2P] Host rejected authority request " +
                (requestId ?? "?") + ": " + (message?.Error ?? "unknown reason") + ".");
        }

        private static void HandleAuthorityRequest(P2PWireMessage message)
        {
            if (Role != P2PRole.Host || message == null || message.Data == null)
            {
                return;
            }

            Dictionary<string, object> request = message.Data;
            string requestId = message.RequestId;
            if (string.IsNullOrEmpty(requestId) &&
                request.TryGetValue(P2PBattleProtocol.AuthorityRequestIdKey, out object rawRequestId))
            {
                requestId = rawRequestId?.ToString();
            }
            if (string.IsNullOrEmpty(requestId))
            {
                SendAuthorityReject(null, "the request had no requestId");
                return;
            }
            string envelopeError = null;
            if (RemoteProfile == null ||
                !P2PBattleProtocol.TryValidateAuthorityRequest(
                    message,
                    BattleId,
                    RemoteProfile.ViewerId,
                    out envelopeError))
            {
                SendAuthorityReject(requestId,
                    string.IsNullOrEmpty(envelopeError)
                        ? "the authority request envelope was invalid"
                        : envelopeError);
                return;
            }
            if (processedAuthorityRequests.Contains(requestId))
            {
                return;
            }

            if (!(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.BattlePlayer == null || manager.BattleEnemy == null)
            {
                SendAuthorityReject(requestId, "the battle manager is not ready");
                return;
            }

            if (manager.VfxMgr != null && !manager.VfxMgr.IsEnd)
            {
                SendAuthorityReject(requestId,
                    "the Host is still finishing the previous action");
                return;
            }
            if (!string.IsNullOrEmpty(activeAuthorityExecutionRequestId))
            {
                SendAuthorityReject(requestId,
                    "the Host is already executing an authoritative action");
                return;
            }

            string action = request.TryGetValue(P2PBattleProtocol.AuthorityActionKey,
                out object rawAction) ? rawAction?.ToString() : null;
            if (string.IsNullOrEmpty(action))
            {
                SendAuthorityReject(requestId, "the request had no action");
                return;
            }
            int requestedCardIndex = TryGetStateInt(
                    request,
                    P2PBattleProtocol.AuthorityCardIndexKey,
                    out int requestedIndex)
                ? requestedIndex
                : -1;
            int requestedCardId = ReadAuthorityCardId(request, "cardId");
            Plugin.Logger.LogInfo(
                "[P2P] Host received authority request: action=" + action +
                ", requestId=" + requestId +
                ", cardIdx=" + requestedCardIndex +
                ", cardId=" + requestedCardId + ".");
            if (!IsAuthorityTurnForGuest(action, manager))
            {
                SendAuthorityReject(requestId, "it is not the guest's turn");
                return;
            }

            if (request.TryGetValue(P2PBattleProtocol.AuthorityTurnKey,
                    out object rawTurn) &&
                TryConvertAuthorityInt(rawTurn, out int requestedTurn) &&
                requestedTurn > 0 && requestedTurn != manager.CurrentTurn)
            {
                // The turn number is advisory metadata from the Guest mirror.
                // The Host is authoritative and already checked the actual
                // IsSelfTurn flag above.  A mirror can legitimately lag by one
                // frame while a TurnStart VFX is draining, so do not reject a
                // valid card solely because this diagnostic value differs.
                Plugin.Logger.LogDebug(
                    "[P2P] Authority request turn hint differs from Host " +
                    "state; using Host state (requested=" + requestedTurn +
                    ", host=" + manager.CurrentTurn + ").");
            }

            // Do not consume the request until all transient admission checks
            // have passed. In particular, a Guest request that arrives while a
            // previous VFX queue is still draining is rejected but remains
            // retryable; recording its ID here would make a later retry look
            // like a duplicate and leave the Guest waiting forever.
            processedAuthorityRequests.Add(requestId);
            authorityRequestTimes[requestId] = DateTime.UtcNow;
            TrimAuthorityRequestHistory();

            try
            {
                ApplyAuthorityRequestPrivateState(request);
                if (!TryExecuteAuthorityRequest(manager, request, action, requestId,
                        out string error))
                {
                    SendAuthorityReject(requestId, error);
                    return;
                }
                SendWire(new P2PWireMessage
                {
                    Type = P2PBattleProtocol.AuthorityAckUri,
                    RequestId = requestId,
                    BattleId = BattleId
                });
            }
            catch (Exception ex)
            {
                CleanupFailedAuthorityRequest(manager, requestId);
                Plugin.Logger.LogError(
                    "[P2P] Authority request failed before completion for " +
                    requestId + ": " + ex);
                SendAuthorityReject(requestId,
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool IsCurrentBattleMessage(P2PWireMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(BattleId))
            {
                return false;
            }
            if (!string.IsNullOrWhiteSpace(message.BattleId) &&
                !string.Equals(message.BattleId, BattleId,
                    StringComparison.Ordinal))
            {
                Plugin.Logger.LogWarning(
                    "[P2P] Ignoring a message for a different battle: " +
                    (message.BattleId ?? "<missing>") + ".");
                return false;
            }
            if (message.Data != null &&
                message.Data.TryGetValue("bid", out object rawPayloadBattleId) &&
                !string.IsNullOrWhiteSpace(rawPayloadBattleId?.ToString()) &&
                !string.Equals(rawPayloadBattleId.ToString(), BattleId,
                    StringComparison.Ordinal))
            {
                Plugin.Logger.LogWarning(
                    "[P2P] Ignoring a message whose payload targets a different " +
                    "battle: " + rawPayloadBattleId + ".");
                return false;
            }
            return true;
        }

        private static void CleanupFailedAuthorityRequest(
            NetworkBattleManagerBase manager,
            string requestId)
        {
            if (!string.IsNullOrEmpty(activeAuthorityExecutionRequestId) &&
                (string.IsNullOrEmpty(requestId) ||
                 string.Equals(activeAuthorityExecutionRequestId, requestId,
                     StringComparison.Ordinal)))
            {
                activeAuthorityExecutionRequestId = null;
                activeAuthorityExecutionStartedUtc = DateTime.MinValue;
            }
            try
            {
                manager?.ClearRegisterCardList();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not clear failed authority register data: " +
                    ex.Message);
            }
            EndAuthorityActionCapture();
        }

        private static bool IsAuthorityTurnForGuest(string action,
            NetworkBattleManagerBase manager)
        {
            if (manager == null || manager.IsBattleEnd)
            {
                return false;
            }
            if (string.Equals(action, "turn_end", StringComparison.Ordinal))
            {
                return manager.BattleEnemy.IsSelfTurn;
            }
            return manager.BattleEnemy.IsSelfTurn;
        }

        private static void ApplyAuthorityRequestPrivateState(
            Dictionary<string, object> request)
        {
            if (request == null ||
                !request.TryGetValue(P2PBattleProtocol.AuthorityStateKey, out object rawState) ||
                !(rawState is Dictionary<string, object> state))
            {
                return;
            }
            RememberReceivedPrivateStateSnapshot(state);
            if (Role == P2PRole.Host &&
                TryGetStateInt(state, "owner", out int owner) && owner == 0)
            {
                // The request itself carries the Guest baseline when the
                // one-shot private_state frame was delayed. Treat it as an
                // equivalent baseline receipt so the Guest can switch back to
                // compact request payloads after this boundary.
                SendPrivateStateAcknowledgement(owner);
            }
            TryApplyPendingHiddenCardStates();
            if (state.TryGetValue("history", out object rawHistory) &&
                rawHistory is Dictionary<string, object> history &&
                BattleManagerBase.GetIns() is NetworkBattleManagerBase manager &&
                manager.BattleEnemy != null)
            {
                // Apply the Guest's pre-action history synchronously.  This is
                // a request boundary, not a native receive boundary, so the
                // Host must have the exact condition inputs before it executes
                // the requested operation.
                if (!ApplyPlayerHistoryState(manager.BattleEnemy, history,
                        out string unresolved) &&
                    !string.IsNullOrEmpty(unresolved))
                {
                    Plugin.Logger.LogWarning(
                        "[P2P] Authority request history is incomplete; " +
                        "continuing with the Host copy. Waiting for: " +
                        unresolved + ".");
                }
            }
            TryApplyPendingPlayerHistoryStates();
        }

        private static void SendPrivateStateAcknowledgement(int owner)
        {
            if (!IsActive || (owner != 0 && owner != 1))
            {
                return;
            }
            SendWire(new P2PWireMessage
            {
                Type = P2PBattleProtocol.PrivateStateAckUri,
                ViewerId = LocalProfile?.ViewerId ?? P2PIdentity.ViewerId,
                BattleId = BattleId,
                Data = new Dictionary<string, object>
                {
                    ["owner"] = owner
                }
            });
        }

        private static bool TryExecuteAuthorityRequest(
            NetworkBattleManagerBase manager,
            Dictionary<string, object> request,
            string action,
            string requestId,
            out string error)
        {
            error = string.Empty;
            BattleCardBase actor = null;
            BattleCardBase target = null;
            if (!IsSupportedAuthorityAction(action))
            {
                error = "unsupported authority action '" + action + "'";
                return false;
            }

            if (!TryResolveAuthorityCardList(
                    manager, request, P2PBattleProtocol.AuthoritySelectedKey, 0,
                    out List<BattleCardBase> selected, out error))
            {
                return false;
            }
            List<int> selectedHandIndices = selected
                .Where(card => card != null && card.IsInHand)
                .Select(card => card.Index)
                .Distinct()
                .ToList();
            List<int> choiceIds = ReadAuthorityIntList(request,
                P2PBattleProtocol.AuthorityChoiceKey);
            bool choiceBrave = ReadAuthorityBool(request,
                P2PBattleProtocol.AuthorityIsChoiceBraveKey);
            List<int> selectSkillIndexes = ReadAuthoritySkillIndexes(request,
                P2PBattleProtocol.AuthoritySelectSkillKey);

            if (!string.Equals(action, "turn_end", StringComparison.Ordinal))
            {
                if (!TryGetStateInt(request,
                        P2PBattleProtocol.AuthorityCardIndexKey, out int actorIndex) ||
                    actorIndex < 0)
                {
                    error = "the request had no valid acting card index";
                    return false;
                }
                // Choice Brave is represented by the player's class card,
                // whose native index is 0 and which is not present in any card
                // zone.  All other actions resolve through the normal indexed
                // card lookup.
                actor = actorIndex == 0
                    ? manager.BattleEnemy.Class
                    : NetworkBattleGenericTool.GetIndexToCardBase(
                        manager, manager.BattleEnemy, actorIndex);
                if (actor == null)
                {
                    error = "the acting card is not present in the guest zones";
                    return false;
                }
                int requestedCardId = ReadAuthorityCardId(request, "cardId");
                if (request.ContainsKey("cardId") && requestedCardId <= 0)
                {
                    error = "the acting card has an invalid cardId";
                    return false;
                }
                if (requestedCardId > 0 && actor.CardId != requestedCardId)
                {
                    // Card identity is a hint only.  Fusion/choice/accelerate
                    // can replace the Guest mirror before the request reaches
                    // the Host; the indexed Host object is the authoritative
                    // one to execute.
                    Plugin.Logger.LogDebug(
                        "[P2P] Authority card identity hint differs from Host " +
                        "state; using Host card (idx=" + actorIndex +
                        ", requested=" + requestedCardId + ", host=" +
                        actor.CardId + ").");
                }
                int requestedCost = ReadAuthorityCardCost(request);
                if (request.ContainsKey("cost") && requestedCost < 0)
                {
                    error = "the acting card has an invalid cost";
                    return false;
                }
                if (requestedCost >= 0 && actor.Cost != requestedCost)
                {
                    Plugin.Logger.LogDebug(
                        "[P2P] Authority card cost hint differs from Host " +
                        "state; using Host cost (idx=" + actorIndex +
                        ", requested=" + requestedCost + ", host=" +
                        actor.Cost + ").");
                }
            }
            if (string.Equals(action, "attack", StringComparison.Ordinal))
            {
                if (!request.TryGetValue(P2PBattleProtocol.AuthorityTargetKey,
                        out object rawTarget) ||
                    !(rawTarget is Dictionary<string, object> targetRef))
                {
                    error = "the attack request had no target";
                    return false;
                }
                if (!TryResolveAuthorityReference(
                        manager, targetRef, out target, out error))
                {
                    return false;
                }
            }

            manager.ClearRegisterCardList();
            BeginAuthorityActionCapture(manager.BattleEnemy);
            activeAuthorityExecutionRequestId = requestId;
            activeAuthorityExecutionStartedUtc = DateTime.UtcNow;
            AuthorityReceiveContext receiveContext = null;
            bool completionScheduled = false;
            try
            {
                receiveContext = CaptureAuthorityReceiveContext(manager);
                Wizard.Battle.View.Vfx.VfxBase vfx;
                switch (action)
                {
                    case "play":
                        // A Host-authoritative action must enter at the same
                        // boundary as a packet returned by the original battle
                        // server.  Do not manufacture ReceiveData and call
                        // ConductReceiveData directly: that bypasses
                        // NetworkBattleReceiver.ConvertReceiveDataToMakeData,
                        // which establishes card ownership, key actions, and
                        // the exact hand-card object subsequently consumed by
                        // SpellBattleCard.OnPlay.  Build the original wire
                        // payload and feed it to the stock receiver instead.
                        ExecuteAuthorityPlayThroughNativeReceiver(
                            manager, actor, selected, choiceIds, choiceBrave,
                            selectSkillIndexes, selectedHandIndices);
                        // ReceivedMessage registered the native operation with
                        // VfxMgr. Queue the authority completion after it;
                        // NullVfx is only a local placeholder for the common
                        // completion scheduling code below.
                        vfx = NullVfx.GetInstance();
                        break;
                    case "evolution":
                        vfx = manager.OperateMgr.EvolutionCard(actor, false,
                            selected, choiceIds);
                        break;
                    case "fusion":
                        vfx = manager.OperateMgr.FusionCard(actor, false, selected);
                        break;
                    case "attack":
                        BeginAuthorityRandomAttackCapture(actor);
                        vfx = manager.OperateMgr.Attack(actor, target, false);
                        target = ConsumeAuthorityResolvedAttackTarget(target);
                        break;
                    case "turn_end":
                        vfx = manager.OperateMgr.TurnEndOperation(false);
                        break;
                    default:
                        // IsSupportedAuthorityAction above makes this
                        // unreachable, but keep the switch defensive if a
                        // future action is added without an execution branch.
                        error = "unsupported authority action '" + action + "'";
                        return false;
                }
                if (vfx == null)
                {
                    vfx = Wizard.Battle.View.Vfx.NullVfx.GetInstance();
                }
                // OperateMgr only builds the VFX graph here.  Card movement,
                // skills, transforms, random effects, and register callbacks
                // run when that graph is played by VfxMgr.  Build and deliver
                // the authority result after the operation VFX has completed;
                // doing it before then publishes the pre-action state and
                // clears the capture context while native effects are still
                // executing.
                PendingAuthorityExecution execution =
                    new PendingAuthorityExecution
                    {
                        Manager = manager,
                        Action = action,
                        Actor = actor,
                        Target = target,
                        Selected = selected,
                        ChoiceIds = choiceIds,
                        ChoiceBrave = choiceBrave,
                        SelectSkillIndexes = selectSkillIndexes,
                        SelectedHandIndices = selectedHandIndices,
                        RequestId = requestId,
                        OriginalActorCardId = ReadAuthorityCardId(request, "cardId"),
                        OriginalActorCost = ReadAuthorityCardCost(request),
                        ReceiveContext = receiveContext
                    };
                VfxBase completionVfx = InstantVfx.Create(() =>
                    CompleteAuthorityExecution(execution));
                manager.VfxMgr.RegisterSequentialVfx<SequentialVfxPlayer>(
                    SequentialVfxPlayer.Create(new VfxBase[] { vfx, completionVfx }));
                completionScheduled = true;
                Plugin.Logger.LogInfo(
                    "[P2P] Host scheduled authority action: action=" + action +
                    ", requestId=" + requestId + ".");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                Plugin.Logger.LogError("[P2P] Host authority execution failed for " +
                    requestId + ": " + ex);
                return false;
            }
            finally
            {
                if (!completionScheduled)
                {
                    RestoreAuthorityReceiveContext(manager, receiveContext);
                    activeAuthorityExecutionRequestId = null;
                    activeAuthorityExecutionStartedUtc = DateTime.MinValue;
                    EndAuthorityActionCapture();
                }
            }
        }

        private static void ExecuteAuthorityPlayThroughNativeReceiver(
            NetworkBattleManagerBase manager,
            BattleCardBase actor,
            List<BattleCardBase> selected,
            List<int> choiceIds,
            bool choiceBrave,
            List<int> selectSkillIndexes,
            List<int> selectedHandIndices)
        {
            if (manager == null)
            {
                throw new InvalidOperationException(
                    "the network battle manager is unavailable for the authority play");
            }

            NetworkBattleReceiver receiver = manager.GetNetworkBattleReceiver();
            if (receiver == null)
            {
                throw new InvalidOperationException(
                    "the native network battle receiver is unavailable for the authority play");
            }

            Dictionary<string, object> payload = BuildAuthorityNativePlayPayload(
                manager, actor, selected, choiceIds, choiceBrave,
                selectSkillIndexes, selectedHandIndices);
            if (!receiver.ReceivedMessage(
                    NetworkBattleDefine.NetworkBattleURI.PlayActions,
                    true,
                    payload,
                    false,
                    null,
                    true))
            {
                throw new InvalidOperationException(
                    "the native receiver rejected the authoritative PlayActions payload");
            }
        }

        private sealed class PendingAuthorityExecution
        {
            internal NetworkBattleManagerBase Manager;
            internal string Action;
            internal BattleCardBase Actor;
            internal BattleCardBase Target;
            internal List<BattleCardBase> Selected;
            internal List<int> ChoiceIds;
            internal bool ChoiceBrave;
            internal List<int> SelectSkillIndexes;
            internal List<int> SelectedHandIndices;
            internal string RequestId;
            internal int OriginalActorCardId;
            internal int OriginalActorCost;
            internal AuthorityReceiveContext ReceiveContext;
        }

        private static void CompleteAuthorityExecution(
            PendingAuthorityExecution execution)
        {
            if (execution == null)
            {
                return;
            }

            bool succeeded = false;
            bool finalResultSent = false;
            bool isTurnEnd = string.Equals(
                execution.Action, "turn_end", StringComparison.Ordinal);
            try
            {
                if (peerDisconnected)
                {
                    Plugin.Logger.LogDebug(
                        "[P2P] Dropped completed authority execution after the " +
                        "peer disconnected: " +
                        (execution.RequestId ?? "?") + ".");
                    return;
                }
                if (isTurnEnd)
                {
                    Dictionary<string, object> turnEndActions =
                        BuildAuthorityTurnEndActionsData(
                            execution.Manager, execution.RequestId);
                    if (turnEndActions == null)
                    {
                        throw new InvalidOperationException(
                            "the native turn-end produced no replay data");
                    }
                    EnsureAuthorityRandomResultsAreValid(
                        turnEndActions, execution.RequestId);
                    DeliverAuthorityResult(turnEndActions);
                    Dictionary<string, object> turnEnd =
                        BuildAuthorityTurnEndData(
                            execution.Manager, execution.RequestId);
                    if (turnEnd == null)
                    {
                        throw new InvalidOperationException(
                            "the native turn-end produced no completion data");
                    }
                    DeliverAuthorityResult(turnEnd);
                    // Do not synthesize TurnEndFinal here. During local
                    // authority replay the Guest is the native action owner;
                    // its stock BattleFinishToTurnEndFinal path already runs
                    // through the suppressed send callback. Sending the same
                    // packet back to that Guest would create an extra Judge
                    // round-trip after Host has already determined the result.
                    finalResultSent = TrySendHostAuthoritativeFinishResult(
                        execution.Manager, execution.RequestId);
                    succeeded = true;
                    Plugin.Logger.LogInfo(
                        "[P2P] Host completed authority action: action=turn_end, " +
                        "requestId=" + (execution.RequestId ?? "?") + ".");
                }
                else
                {
                    Dictionary<string, object> response =
                        BuildAuthorityResultData(
                            execution.Manager,
                            execution.Action,
                            execution.Actor,
                            execution.Target,
                            execution.Selected,
                            execution.ChoiceIds,
                            execution.ChoiceBrave,
                            execution.SelectSkillIndexes,
                            execution.SelectedHandIndices,
                            execution.RequestId,
                            execution.OriginalActorCardId,
                            execution.OriginalActorCost);
                    if (response == null)
                    {
                        throw new InvalidOperationException(
                            "the native action produced no replay data");
                    }
                    EnsureAuthorityRandomResultsAreValid(
                        response, execution.RequestId);
                    DeliverAuthorityResult(response);
                    finalResultSent = TrySendHostAuthoritativeFinishResult(
                        execution.Manager, execution.RequestId);
                    succeeded = true;
                    Plugin.Logger.LogInfo(
                        "[P2P] Host completed authority action: action=" +
                        execution.Action + ", requestId=" +
                        (execution.RequestId ?? "?") + ".");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    "[P2P] Host authority completion failed for " +
                    (execution.RequestId ?? "?") + ": " + ex);
                SendAuthorityReject(
                    execution.RequestId,
                    ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try
                {
                    execution.Manager?.ClearRegisterCardList();
                }
                catch (Exception)
                {
                }
                RestoreAuthorityReceiveContext(
                    execution.Manager, execution.ReceiveContext);
                activeAuthorityExecutionRequestId = null;
                activeAuthorityExecutionStartedUtc = DateTime.MinValue;
                EndAuthorityActionCapture();
            }

            if (succeeded && isTurnEnd && !finalResultSent && !finishResultSent)
            {
                // The native server selects the next owner only after all
                // turn-end effects have completed.  Queue that transition
                // after cleanup so the next turn cannot observe the previous
                // action's receive context or capture buffers.
                QueueAuthorityNextTurnStart(
                    execution.Manager, execution.RequestId);
            }
        }

        private static bool TrySendHostAuthoritativeFinishResult(
            NetworkBattleManagerBase manager,
            string requestId)
        {
            if (manager == null || finishResultSent || peerDisconnected)
            {
                return finishResultSent;
            }

            int localResult;
            try
            {
                localResult = (int)manager.JudgeCurrentFinishStatus();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not evaluate authoritative finish status: " +
                    ex.Message);
                return false;
            }

            if (!P2PBattleResult.IsTerminalResult(localResult))
            {
                return false;
            }

            Dictionary<string, object> resultRequest =
                new Dictionary<string, object>
                {
                    ["p2pLocalResult"] = localResult
                };
            if (!string.IsNullOrEmpty(requestId))
            {
                resultRequest[P2PBattleProtocol.AuthorityResultRequestIdKey] =
                    requestId;
            }

            Plugin.Logger.LogInfo(
                "[P2P] Host-authoritative battle boundary reached final result " +
                localResult + "; delivering JudgeResult." +
                (string.IsNullOrEmpty(requestId)
                    ? string.Empty
                    : " requestId=" + requestId + "."));
            SendFinishResult(true, resultRequest);
            return finishResultSent;
        }

        private sealed class AuthorityReceiveContext
        {
            internal NetworkBattleReceiver.ReceiveData Previous;
        }

        private static AuthorityReceiveContext CaptureAuthorityReceiveContext(
            NetworkBattleManagerBase manager)
        {
            if (manager?.networkBattleData == null)
            {
                return null;
            }

            AuthorityReceiveContext context = new AuthorityReceiveContext
            {
                Previous = manager.networkBattleData.GetReceiveData()
            };
            return context;
        }

        private static void RestoreAuthorityReceiveContext(
            NetworkBattleManagerBase manager,
            AuthorityReceiveContext context)
        {
            if (manager?.networkBattleData == null || context == null)
            {
                return;
            }
            manager.networkBattleData.SetReceiveData(context.Previous);
        }

        // Converts a Guest input request into the same raw dictionary shape
        // consumed by NetworkBattleReceiver.  Keep this at the wire boundary:
        // the receiver, not P2P code, must create ReceiveData/CardDataModel.
        private static Dictionary<string, object> BuildAuthorityNativePlayPayload(
            NetworkBattleManagerBase manager,
            BattleCardBase actor,
            List<BattleCardBase> selected,
            List<int> choiceIds,
            bool choiceBrave,
            List<int> selectSkillIndexes,
            List<int> selectedHandIndices)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["playIdx"] = actor?.Index ?? -1,
                ["type"] = (int)AuthorityActionPlayType("play", selected)
            };
            List<object> knownCards = BuildAuthorityNativeReceiveKnownCards(
                manager, actor, selected);
            if (knownCards.Count > 0)
            {
                payload["knownList"] = knownCards;
            }

            List<object> targets = BuildAuthorityNativeReceiveTargets(
                manager, selected, selectSkillIndexes);
            if (targets.Count > 0)
            {
                payload["targetList"] = targets;
            }

            List<object> keyActions = BuildAuthorityNativePlayKeyActions(
                actor, choiceIds, choiceBrave, selectedHandIndices);
            if (keyActions.Count > 0)
            {
                payload["keyAction"] = keyActions;
            }
            return payload;
        }

        private static List<object> BuildAuthorityNativeReceiveKnownCards(
            NetworkBattleManagerBase manager,
            BattleCardBase actor,
            IEnumerable<BattleCardBase> selected)
        {
            List<object> result = new List<object>();
            IEnumerable<BattleCardBase> cards = new[] { actor }
                .Concat(selected ?? Enumerable.Empty<BattleCardBase>())
                .Where(card => card != null && card.Index > 0)
                // Card indexes are only unique within one player's zones;
                // preserve the owner when an action targets a card whose
                // index happens to match the acting card.
                .GroupBy(card => new
                {
                    IsGuestOwner = manager != null &&
                        ReferenceEquals(card.SelfBattlePlayer,
                            manager.BattleEnemy),
                    card.Index
                })
                .Select(group => group.First());
            foreach (BattleCardBase card in cards)
            {
                bool isGuestActionOwner = manager != null &&
                    ReferenceEquals(card.SelfBattlePlayer, manager.BattleEnemy);
                // The original Guest sender writes isSelf from the source
                // player's perspective.  On the Host this same raw packet is
                // received with isPlayer=false and is therefore applied to
                // BattleEnemy by NetworkOperationCollection.
                result.Add(new Dictionary<string, object>
                {
                    ["idx"] = card.Index,
                    ["cardId"] = card.CardId,
                    ["isSelf"] = isGuestActionOwner ? 1 : 0,
                    ["is_open"] = 1,
                    ["cost"] = card.Cost,
                    ["spellboost"] = card.SpellChargeCount,
                    ["from"] = (int)NetworkBattleGenericTool.GetCardPlaceState(
                        card.SelfBattlePlayer, card.Index)
                });
            }
            return result;
        }

        private static List<object> BuildAuthorityNativeReceiveTargets(
            NetworkBattleManagerBase manager,
            List<BattleCardBase> selected,
            List<int> selectSkillIndexes)
        {
            List<object> result = new List<object>();
            if (manager == null || selected == null)
            {
                return result;
            }

            List<int> skillIndexes = (selectSkillIndexes ??
                    Enumerable.Empty<int>())
                .Where(index => index >= 0)
                .Distinct()
                .ToList();
            foreach (BattleCardBase card in selected)
            {
                if (!IsValidAuthorityActor(card))
                {
                    continue;
                }

                bool isGuestActionOwner = ReferenceEquals(
                    card.SelfBattlePlayer, manager.BattleEnemy);
                Dictionary<string, object> target =
                    new Dictionary<string, object>
                    {
                        ["targetIdx"] = card.Index,
                        ["isSelf"] = isGuestActionOwner ? 1 : 0
                    };
                if (skillIndexes.Count > 0)
                {
                    target["selectSkillIndex"] = skillIndexes
                        .Select(index => (object)index)
                        .ToList();
                }
                result.Add(target);
            }
            return result;
        }

        private static List<object> BuildAuthorityNativePlayKeyActions(
            BattleCardBase actor,
            List<int> choiceIds,
            bool choiceBrave,
            IEnumerable<int> selectedHandIndices)
        {
            List<object> result = new List<object>();
            if (actor == null)
            {
                return result;
            }

            List<int> buried = (selectedHandIndices ?? Enumerable.Empty<int>())
                .Where(index => index > 0)
                .Distinct()
                .ToList();
            bool burialRite = buried.Count > 0 &&
                HasAuthorityBurialRiteSkill(actor, "play");
            foreach (SendKeyActionDataManager.KeyActionType type in
                ResolveAuthorityKeyActionTypes(
                    actor, "play", choiceIds, choiceBrave, burialRite))
            {
                Dictionary<string, object> keyAction =
                    new Dictionary<string, object>
                    {
                        ["type"] = (int)type,
                        ["cardId"] = actor.CardId
                    };
                switch (type)
                {
                    case SendKeyActionDataManager.KeyActionType.Choice:
                    case SendKeyActionDataManager.KeyActionType.HaveBeforeSkillChoice:
                    case SendKeyActionDataManager.KeyActionType.ChoiceEvolution:
                    case SendKeyActionDataManager.KeyActionType.ChoiceBrave:
                        keyAction["selectCard"] =
                            new Dictionary<string, object>
                            {
                                ["cardId"] = (choiceIds ?? new List<int>())
                                    .Select(value => (object)value)
                                    .ToList(),
                                ["open"] = 1
                            };
                        break;
                    case SendKeyActionDataManager.KeyActionType.BurialRate:
                        keyAction["cardIdx"] = buried
                            .Select(index => (object)index)
                            .ToList();
                        break;
                }
                result.Add(keyAction);
            }
            return result;
        }

        private static bool HasAuthorityBurialRiteSkill(
            BattleCardBase actor,
            string action)
        {
            if (actor == null ||
                (!string.Equals(action, "play", StringComparison.Ordinal) &&
                 !string.Equals(action, "evolution", StringComparison.Ordinal)))
            {
                return false;
            }

            try
            {
                bool isEvolution = string.Equals(
                    action, "evolution", StringComparison.Ordinal);
                return actor.GetSelectTypeSkill(
                        isEvolution, false, false, false, false)
                    .Any(skill => skill != null && skill.IsBurialRite);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static List<SendKeyActionDataManager.KeyActionType>
            ResolveAuthorityKeyActionTypes(
                BattleCardBase actor,
                string action,
                List<int> choiceIds,
                bool choiceBrave,
                bool burialRite)
        {
            List<SendKeyActionDataManager.KeyActionType> result =
                new List<SendKeyActionDataManager.KeyActionType>();
            if (string.Equals(action, "fusion", StringComparison.Ordinal))
            {
                result.Add(SendKeyActionDataManager.KeyActionType.Fusion);
            }

            if (choiceBrave)
            {
                result.Add(SendKeyActionDataManager.KeyActionType.ChoiceBrave);
            }
            else if (choiceIds != null && choiceIds.Count > 0)
            {
                bool isEvolution = string.Equals(
                    action, "evolution", StringComparison.Ordinal);
                bool haveBeforeSkillChoice = false;
                try
                {
                    SkillCollectionBase skills = isEvolution
                        ? actor?.EvolutionSkills
                        : actor?.Skills;
                    haveBeforeSkillChoice = skills != null &&
                        skills.HaveBeforeChoiceSkill();
                }
                catch (Exception)
                {
                }
                result.Add(isEvolution
                    ? SendKeyActionDataManager.KeyActionType.ChoiceEvolution
                    : (haveBeforeSkillChoice
                        ? SendKeyActionDataManager.KeyActionType.HaveBeforeSkillChoice
                        : SendKeyActionDataManager.KeyActionType.Choice));
            }

            if (actor != null &&
                string.Equals(action, "play", StringComparison.Ordinal))
            {
                try
                {
                    if (NetworkBattleGenericTool.IsAcceleratedCard(actor))
                    {
                        result.Add(SendKeyActionDataManager.KeyActionType.Accelerated);
                    }
                    else if (NetworkBattleGenericTool.IsCrystallizeCard(actor))
                    {
                        result.Add(SendKeyActionDataManager.KeyActionType.Crystallize);
                    }
                }
                catch (Exception)
                {
                }
            }

            if (burialRite)
            {
                result.Add(SendKeyActionDataManager.KeyActionType.BurialRate);
            }
            return result.Distinct().ToList();
        }

        private static NetworkBattleDefine.PlayActionType AuthorityActionPlayType(
            string action,
            List<BattleCardBase> selected)
        {
            if (string.Equals(action, "attack", StringComparison.Ordinal))
            {
                return NetworkBattleDefine.PlayActionType.ATTACK;
            }
            if (string.Equals(action, "evolution", StringComparison.Ordinal))
            {
                return selected != null && selected.Count > 0
                    ? NetworkBattleDefine.PlayActionType.EVOLUTION_SELECT
                    : NetworkBattleDefine.PlayActionType.EVOLUTION;
            }
            if (string.Equals(action, "fusion", StringComparison.Ordinal))
            {
                return NetworkBattleDefine.PlayActionType.FUSION;
            }
            if (string.Equals(action, "play", StringComparison.Ordinal))
            {
                return selected != null && selected.Count > 0
                    ? NetworkBattleDefine.PlayActionType.PLAY_HAND_SELECT
                    : NetworkBattleDefine.PlayActionType.PLAY_HAND;
            }
            return NetworkBattleDefine.PlayActionType.NONE;
        }

        private static void SendAuthorityReject(string requestId, string reason)
        {
            SendWire(new P2PWireMessage
            {
                Type = P2PBattleProtocol.AuthorityRejectUri,
                RequestId = requestId,
                BattleId = BattleId,
                Error = string.IsNullOrWhiteSpace(reason)
                    ? "The host rejected the action."
                    : reason
            });
        }

        private static void BeginAuthorityActionCapture(
            BattlePlayerBase sourcePlayer)
        {
            if (!IsActive || Role != P2PRole.Host)
            {
                return;
            }

            // A Host-authoritative operation can be owned by BattlePlayer or
            // BattleEnemy.  The existing local-action hooks only arm capture
            // for BattlePlayer, so arm the same manifest collectors explicitly
            // for Guest requests and Guest turn transitions.
            localActionCaptureActive = true;
            PendingLocalConditionResults.Clear();
            LocalAuthoritativeSkillTargets.Clear();
            LocalAuthoritativeSkillEvaluations.Clear();
            CaptureActionPreHiddenCardStates(manager: BattleManagerBase.GetIns()
                as NetworkBattleManagerBase);
            localActionPreHistoryState = null;
            localActionPreHistoryRevision = 0;

            if (sourcePlayer == null)
            {
                return;
            }

            try
            {
                bool ownerIsHost = sourcePlayer.IsPlayer;
                localActionPreHistoryState = CapturePlayerHistoryState(
                    sourcePlayer, ownerIsHost);
                localActionPreHistoryRevision = ownerIsHost
                    ? Math.Max(1, localPlayerHistoryRevision)
                    : 0;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not capture authority action-start history: " +
                    ex.Message);
            }
        }

        private static void EndAuthorityActionCapture()
        {
            localActionCaptureActive = false;
            PendingLocalConditionResults.Clear();
            // Non-Play authority transitions no longer publish a compatibility
            // manifest. Their captured values must not leak into a later
            // PlayActions result.
            LocalAuthoritativeSkillTargets.Clear();
            LocalAuthoritativeSkillEvaluations.Clear();
            localActionPreHistoryState = null;
            localActionPreHistoryRevision = 0;
            actionPreHiddenCardStates.Clear();
            authorityRandomAttackCandidates = null;
            authorityResolvedAttackTarget = null;
            ClearAuthorityRandomAttackReplay();
            AuthorityActionProcessors.Clear();
        }

        private static void BeginAuthorityRandomAttackCapture(
            BattleCardBase attacker)
        {
            authorityRandomAttackCandidates = null;
            authorityResolvedAttackTarget = null;
            if (attacker == null ||
                attacker.SkillApplyInformation == null ||
                attacker.SkillApplyInformation.RandomAttackCount <= 0 ||
                attacker.SelfBattlePlayer == null ||
                attacker.OpponentBattlePlayer == null)
            {
                return;
            }

            authorityRandomAttackCandidates =
                attacker.SelfBattlePlayer.ClassAndInPlayCardList
                    .Where(card => card != null && card != attacker)
                    .Concat(attacker.OpponentBattlePlayer.ClassAndInPlayCardList ??
                        Enumerable.Empty<BattleCardBase>())
                    .Where(card => card != null && (card.IsUnit || card.IsClass) &&
                        !card.CantBeFocusedAttack(attacker))
                    .ToList();

            if (authorityRandomAttackCandidates.Count == 0)
            {
                authorityRandomAttackCandidates = null;
            }
        }

        internal static void ObserveAuthorityRandomAttackRoll(
            int candidateCount,
            int selectedIndex)
        {
            if (authorityRandomAttackCandidates == null ||
                authorityRandomAttackCandidates.Count != candidateCount ||
                selectedIndex < 0 ||
                selectedIndex >= authorityRandomAttackCandidates.Count)
            {
                return;
            }

            authorityResolvedAttackTarget =
                authorityRandomAttackCandidates[selectedIndex];
            authorityRandomAttackCandidates = null;
        }

        internal static void PrepareAuthorityRandomAttackReplay(
            BattleCardBase attacker,
            BattleCardBase authoritativeTarget)
        {
            authorityReplayRandomAttackCandidateCount = 0;
            authorityReplayRandomAttackTargetIndex = -1;
            if (UseNativeClientActionTiming ||
                !IsAuthorityLocalReplayActive || Role != P2PRole.Guest ||
                attacker == null || authoritativeTarget == null ||
                attacker.SkillApplyInformation == null ||
                attacker.SkillApplyInformation.RandomAttackCount <= 0 ||
                attacker.SelfBattlePlayer == null ||
                attacker.OpponentBattlePlayer == null)
            {
                return;
            }

            List<BattleCardBase> candidates = attacker.SelfBattlePlayer
                .ClassAndInPlayCardList
                .Where(card => card != null && card != attacker)
                .Concat(attacker.OpponentBattlePlayer.ClassAndInPlayCardList ??
                    Enumerable.Empty<BattleCardBase>())
                .Where(card => card != null && (card.IsUnit || card.IsClass) &&
                    !card.CantBeFocusedAttack(attacker))
                .ToList();
            if (candidates.Count == 0)
            {
                return;
            }

            int selected = candidates.FindIndex(card =>
                ReferenceEquals(card, authoritativeTarget));
            if (selected < 0)
            {
                selected = candidates.FindIndex(card =>
                    card.Index == authoritativeTarget.Index &&
                    card.IsPlayer == authoritativeTarget.IsPlayer &&
                    card.CardId == authoritativeTarget.CardId);
            }
            if (selected < 0)
            {
                Plugin.Logger.LogWarning(
                    "[P2P] Authority random-attack target was not present in " +
                    "the Guest candidate list; native selection will be used.");
                return;
            }

            authorityReplayRandomAttackCandidateCount = candidates.Count;
            authorityReplayRandomAttackTargetIndex = selected;
        }

        internal static void OverrideAuthorityRandomAttackRoll(
            int candidateCount,
            ref int selectedIndex)
        {
            if (UseNativeClientActionTiming ||
                authorityReplayRandomAttackCandidateCount <= 0 ||
                candidateCount != authorityReplayRandomAttackCandidateCount ||
                authorityReplayRandomAttackTargetIndex < 0 ||
                authorityReplayRandomAttackTargetIndex >= candidateCount)
            {
                return;
            }

            selectedIndex = authorityReplayRandomAttackTargetIndex;
            authorityReplayRandomAttackCandidateCount = 0;
            authorityReplayRandomAttackTargetIndex = -1;
        }

        internal static void ClearAuthorityRandomAttackReplay()
        {
            authorityReplayRandomAttackCandidateCount = 0;
            authorityReplayRandomAttackTargetIndex = -1;
        }

        private static BattleCardBase ConsumeAuthorityResolvedAttackTarget(
            BattleCardBase fallback)
        {
            BattleCardBase resolved = authorityResolvedAttackTarget;
            authorityResolvedAttackTarget = null;
            authorityRandomAttackCandidates = null;
            return resolved ?? fallback;
        }

        private static void TrimAuthorityRequestHistory()
        {
            if (authorityRequestTimes.Count <= 256)
            {
                return;
            }
            foreach (string id in authorityRequestTimes
                .OrderBy(pair => pair.Value)
                .Take(authorityRequestTimes.Count - 192)
                .Select(pair => pair.Key)
                .ToList())
            {
                authorityRequestTimes.Remove(id);
                processedAuthorityRequests.Remove(id);
            }
        }

        private static bool IsSupportedAuthorityAction(string action)
        {
            return string.Equals(action, "play", StringComparison.Ordinal) ||
                string.Equals(action, "evolution", StringComparison.Ordinal) ||
                string.Equals(action, "fusion", StringComparison.Ordinal) ||
                string.Equals(action, "attack", StringComparison.Ordinal) ||
                string.Equals(action, "turn_end", StringComparison.Ordinal);
        }

        private static bool TryResolveAuthorityCardList(
            NetworkBattleManagerBase manager,
            Dictionary<string, object> request,
            string key,
            int defaultOwner,
            out List<BattleCardBase> result,
            out string error)
        {
            result = new List<BattleCardBase>();
            error = string.Empty;
            if (request == null || !request.TryGetValue(key, out object rawCards) ||
                rawCards == null)
            {
                return true;
            }
            if (rawCards is string || !(rawCards is IEnumerable cards))
            {
                error = "the authority field '" + key + "' is not a card list";
                return false;
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object rawCard in cards)
            {
                if (rawCard is Dictionary<string, object> reference)
                {
                    if (!TryResolveAuthorityReference(
                            manager, reference, out BattleCardBase card,
                            out error))
                    {
                        return false;
                    }
                    string identity = AuthorityReferenceIdentity(reference);
                    if (!seen.Add(identity))
                    {
                        error = "the authority field '" + key +
                            "' contains a duplicate card reference (" +
                            identity + ")";
                        return false;
                    }
                    result.Add(card);
                    continue;
                }
                if (!TryConvertAuthorityInt(rawCard, out int index) || index < 0)
                {
                    error = "the authority field '" + key +
                        "' contains an invalid card index";
                    return false;
                }
                BattlePlayerBase fallbackOwner =
                    manager.GetBattlePlayer(defaultOwner == 1);
                BattleCardBase fallback = index == 0
                    ? fallbackOwner?.Class
                    : NetworkBattleGenericTool.GetIndexToCardBase(
                        manager, fallbackOwner, index);
                if (fallback == null)
                {
                    error = "the authority field '" + key +
                        "' references a card that is not present (owner=" +
                        defaultOwner + ", idx=" + index + ")";
                    return false;
                }
                string fallbackIdentity = defaultOwner + ":" + index;
                if (!seen.Add(fallbackIdentity))
                {
                    error = "the authority field '" + key +
                        "' contains a duplicate card reference (" +
                        fallbackIdentity + ")";
                    return false;
                }
                result.Add(fallback);
            }
            return true;
        }

        private static string AuthorityReferenceIdentity(
            Dictionary<string, object> reference)
        {
            int owner = 0;
            int index = 0;
            TryGetStateInt(reference, "owner", out owner);
            TryGetStateInt(reference, "idx", out index);
            return owner + ":" + index;
        }

        private static BattleCardBase ResolveAuthorityReference(
            NetworkBattleManagerBase manager,
            Dictionary<string, object> reference)
        {
            return TryResolveAuthorityReference(
                    manager, reference, out BattleCardBase card, out _)
                ? card
                : null;
        }

        private static bool TryResolveAuthorityReference(
            NetworkBattleManagerBase manager,
            Dictionary<string, object> reference,
            out BattleCardBase card,
            out string error)
        {
            card = null;
            error = string.Empty;
            if (manager == null || reference == null ||
                !TryGetStateInt(reference, "idx", out int index) || index < 0)
            {
                error = "the authority card reference has no valid idx";
                return false;
            }
            int owner = 0;
            if (reference.ContainsKey("owner") &&
                (!TryGetStateInt(reference, "owner", out owner) ||
                 (owner != 0 && owner != 1)))
            {
                error = "the authority card reference has an invalid owner " +
                    "(idx=" + index + ")";
                return false;
            }
            owner = owner == 1 ? 1 : 0;
            BattlePlayerBase ownerPlayer = manager.GetBattlePlayer(owner == 1);
            card = index == 0
                ? ownerPlayer?.Class
                : NetworkBattleGenericTool.GetIndexToCardBase(
                    manager, ownerPlayer, index);
            if (card == null)
            {
                error = "the authority card reference is not present " +
                    "(owner=" + owner + ", idx=" + index + ")";
                return false;
            }
            if (reference.ContainsKey("cardId"))
            {
                if (!TryConvertAuthorityInt(reference["cardId"], out int cardId) ||
                    cardId <= 0)
                {
                    // Older Guests serialized a class/leader target as
                    // idx=0/cardId=0. Index 0 is already an unambiguous class
                    // reference, so accept that legacy representation.
                    if (index == 0)
                    {
                        return true;
                    }
                    error = "the authority card reference has an invalid cardId " +
                        "(owner=" + owner + ", idx=" + index + ")";
                    card = null;
                    return false;
                }
                if (card.CardId == cardId)
                {
                    return true;
                }
                // References are resolved by absolute owner + index.  The
                // optional cardId is diagnostic metadata and can be stale when
                // a card has just fused, transformed, or received an attached
                // skill.  Keep the Host object and continue with its state.
                Plugin.Logger.LogDebug(
                    "[P2P] Authority card reference identity differs from " +
                    "Host state; using Host card (owner=" + owner +
                    ", idx=" + index + ", requested=" + cardId + ", host=" +
                    card.CardId + ").");
                return true;
            }
            return true;
        }

        private static List<int> ReadAuthorityIntList(
            Dictionary<string, object> data,
            string key)
        {
            List<int> result = new List<int>();
            if (data == null || !data.TryGetValue(key, out object raw) ||
                raw is string || !(raw is IEnumerable values))
            {
                return result;
            }
            foreach (object value in values)
            {
                if (TryConvertAuthorityInt(value, out int converted) && converted > 0)
                {
                    result.Add(converted);
                }
            }
            return result;
        }

        private static List<int> ReadAuthoritySkillIndexes(
            Dictionary<string, object> data,
            string key)
        {
            List<int> result = new List<int>();
            if (data == null || !data.TryGetValue(key, out object raw) ||
                raw is string || !(raw is IEnumerable values))
            {
                return result;
            }

            foreach (object value in values)
            {
                if (TryConvertAuthorityInt(value, out int converted) &&
                    converted >= 0 && !result.Contains(converted))
                {
                    result.Add(converted);
                }
            }
            return result;
        }

        private static bool ReadAuthorityBool(
            Dictionary<string, object> data,
            string key)
        {
            if (data == null || !data.TryGetValue(key, out object raw))
            {
                return false;
            }
            if (raw is bool value)
            {
                return value;
            }
            return TryConvertAuthorityInt(raw, out int integer) && integer != 0;
        }

        private static int ReadAuthorityCardId(
            Dictionary<string, object> data,
            string key)
        {
            return data != null && TryGetStateInt(data, key, out int value) &&
                value > 0
                ? value
                : 0;
        }

        private static int ReadAuthorityCardCost(
            Dictionary<string, object> data)
        {
            return data != null && TryGetStateInt(data, "cost", out int value) &&
                value >= 0
                ? value
                : -1;
        }

        private static bool TryConvertAuthorityInt(object value, out int result)
        {
            try
            {
                result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                result = 0;
                return false;
            }
        }

        private static Dictionary<string, object> BuildAuthorityResultData(
            NetworkBattleManagerBase manager,
            string action,
            BattleCardBase actor,
            BattleCardBase target,
            List<BattleCardBase> selected,
            List<int> choiceIds,
            bool choiceBrave,
            List<int> selectSkillIndexes,
            List<int> selectedHandIndices,
            string requestId,
            int originalActorCardId,
            int originalActorCost)
        {
            if (manager == null)
            {
                return null;
            }
            Dictionary<string, object> data = new Dictionary<string, object>
            {
                ["uri"] = P2PBattleProtocol.PlayActionsUri,
                ["type"] = AuthorityActionType(action),
                ["turnState"] = 0,
                [P2PBattleProtocol.AuthorityResultRequestIdKey] = requestId,
                [P2PBattleProtocol.AuthoritySourceKey] = 0
            };
            if (actor != null)
            {
                // The Host executes a Guest request against BattleEnemy.  Some
                // operations (fusion metamorphose, choice transforms, and
                // accelerated/crystallize mutations) replace that object before
                // the replay packet is assembled.  The native receiver must
                // first locate the pre-action hand card; the orderList and the
                // post-action hidden snapshot then apply the transformed state.
                int nativeCardId = actor.CardId;
                int nativeCost = actor.Cost;
                if (TryGetActionPreHiddenCardState(
                        actor.IsPlayer, actor.Index,
                        out Dictionary<string, object> preActorState))
                {
                    if (TryGetStateInt(preActorState, "cardId", out int preCardId) &&
                        preCardId > 0)
                    {
                        nativeCardId = preCardId;
                    }
                    if (TryGetStateInt(preActorState, "cost", out int preCost) &&
                        preCost >= 0)
                    {
                        nativeCost = preCost;
                    }
                }
                if (originalActorCardId > 0 && !actor.IsPlayer)
                {
                    // The request carries the Guest's identity even when the
                    // temporary card object was already replaced before the
                    // action-start capture could see it.
                    nativeCardId = originalActorCardId;
                }
                if (originalActorCost >= 0 && !actor.IsPlayer)
                {
                    nativeCost = originalActorCost;
                }
                data["playIdx"] = actor.Index;
                // Class/Choice Brave actions use playIdx=0, but index 0 is not
                // a hand/deck card and must not be sent through
                // ReplaceReceivedCard.  The native ChoiceBrave operation
                // resolves the class directly from the acting player.
                if (actor.Index > 0)
                {
                    data["knownList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["idx"] = actor.Index,
                            ["cardId"] = nativeCardId,
                            ["isSelf"] = actor.IsPlayer ? 1 : 0,
                            ["is_open"] = 1,
                            ["cost"] = nativeCost
                        }
                    };
                }
            }
            if (target != null)
            {
                data["targetList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["targetIdx"] = target.Index,
                        ["isSelf"] = target.IsPlayer ? 1 : 0
                    }
                };
            }
            else if (selected != null && selected.Count > 0 &&
                (string.Equals(action, "play", StringComparison.Ordinal) ||
                 string.Equals(action, "evolution", StringComparison.Ordinal)))
            {
                data["targetList"] = BuildAuthorityTargetList(
                    manager, selected, selectSkillIndexes);
            }
            if (string.Equals(action, "fusion", StringComparison.Ordinal) &&
                selected != null && selected.Count > 0)
            {
                data["targetList"] = BuildAuthorityTargetList(
                    manager, selected, selectSkillIndexes);
            }
            if (string.Equals(action, "play", StringComparison.Ordinal) &&
                !data.ContainsKey("targetList"))
            {
                data["type"] = 30;
            }
            else if (string.Equals(action, "evolution", StringComparison.Ordinal))
            {
                // NetworkBattleSender.SendEvolData distinguishes a plain
                // evolution (20) from an evolution with selected targets (21).
                // The receiver uses that bit to decide whether it should read
                // targetList, so preserve it in the authoritative result.
                data["type"] = data.ContainsKey("targetList") ? 21 : 20;
            }

            BuildAuthorityRegisterData(
                manager, false, out List<object> orderList,
                out List<object> unapproved);
            if (orderList.Count > 0)
            {
                data["orderList"] = orderList;
            }
            if (unapproved.Count > 0)
            {
                data["uList"] = unapproved;
            }
            // The original sender's orderList/uList is the authority for card
            // creation and movement.  Ensure every private-zone destination
            // also has its identity in the same native knownList packet before
            // any P2P-only state is attached.  This covers both draw/return and
            // generated Token cards, including effects that affect the action
            // owner's opponent.
            EnsureNativePrivateMoveIdentities(manager, data);
            AppendAuthorityPreActionHistory(data);
            AttachAuthorityFusionMetamorphoseOriginals(
                data, orderList, actor, originalActorCardId, originalActorCost);
            bool nativeKeyActionsAdded =
                TryAppendAuthorityNativeKeyActions(manager, data, actor);
            List<object> keyActions = nativeKeyActionsAdded
                ? new List<object>()
                : BuildAuthorityKeyActions(
                    action, actor, selected, choiceIds, choiceBrave,
                    selectedHandIndices, originalActorCardId);
            if (!nativeKeyActionsAdded && keyActions.Count > 0)
            {
                data["keyAction"] = keyActions;
            }

            // The native sender prepares card identities from the action
            // source's perspective.  The Host's register data above is in the
            // Host perspective because the operation ran against BattleEnemy,
            // so run the same preparation on a temporary Guest-perspective
            // copy and flip the completed payload back.  This is important for
            // every authority result, not only explicit FusionCard requests:
            // accelerate/crystallize mutations, hidden draws, returned cards,
            // and fusion operations registered by a normal card effect all use
            // this path to produce the correct knownList/uList entries.
            PrepareAuthorityOutgoingAction(manager, data);

            AppendAuthorityResultMetadata(manager, data);
            AppendAuthorityExecutionMetadata(
                P2PBattleProtocol.PlayActionsUri, data);
            NormalizeAuthorityResultFieldOrder(data);
            // The register data is authored from the host's perspective because
            // the host executed the action against BattleEnemy. Convert it to
            // the guest's local perspective and mark it for the local replay
            // adapter. Do not use PrepareOpponentBattleMessage here: that path
            // intentionally routes an opponent action to BattleEnemy.
            Dictionary<string, object> guestData = P2PMessageTransform.FlipPerspective(data);
            FlipAuthorityTargetListPerspective(guestData);
            P2PMessageTransform.NormalizeAuthorityLocalReplayMessage(guestData);
            guestData["uri"] = P2PBattleProtocol.PlayActionsUri;
            guestData["p2pAuthorityLocalReplay"] = 1;
            guestData[P2PBattleProtocol.AuthorityResultRequestIdKey] = requestId;
            return guestData;
        }

        private static void PrepareAuthorityOutgoingAction(
            NetworkBattleManagerBase manager,
            Dictionary<string, object> data)
        {
            if (manager == null || data == null)
            {
                return;
            }

            try
            {
                // P2PBattleCardTracker intentionally models the sender as
                // "self" (isSelf=1).  Authority register data is currently
                // authored in the Host perspective, where the Guest source is
                // isSelf=0.  Convert only for the tracker pass, then restore
                // the Host perspective before the final result flip below.
                Dictionary<string, object> sourceData =
                    P2PMessageTransform.FlipPerspective(data);
                bool prepared = BattleCardTracker.PrepareOutgoingAction(
                    false,
                    sourceData,
                    out _,
                    out _,
                    index => ResolveAuthorityCardId(manager, false, index),
                    index => ResolveAuthorityCardCost(manager, false, index),
                    warning => Plugin.Logger.LogWarning(
                        "[P2P] Authority card synchronization: " +
                        warning + "."),
                    index => ResolveAuthorityFusionIngredients(
                        manager, false, index));
                if (!prepared)
                {
                    Plugin.Logger.LogDebug(
                        "[P2P] Authority action did not expose a resolvable " +
                        "playIdx/card identity; preserving native register data.");
                }

                Dictionary<string, object> hostData =
                    P2PMessageTransform.FlipPerspective(sourceData);
                data.Clear();
                foreach (KeyValuePair<string, object> field in hostData)
                {
                    data[field.Key] = field.Value;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    "[P2P] Could not prepare authority card data: " + ex.Message);
            }
        }

        private static void AttachAuthorityFusionActions(
            NetworkBattleManagerBase manager,
            Dictionary<string, object> data)
        {
            if (manager == null || data == null ||
                !data.TryGetValue("orderList", out object rawOrderList) ||
                rawOrderList is string || !(rawOrderList is IEnumerable))
            {
                return;
            }

            bool hasFusionOrder = false;
            foreach (object rawOrder in (IEnumerable)rawOrderList)
            {
                if (rawOrder is Dictionary<string, object> order &&
                    order.ContainsKey("fusion"))
                {
                    hasFusionOrder = true;
                    break;
                }
            }
            if (!hasFusionOrder)
            {
                return;
            }

            // Build the same cumulative fusion metadata that a local native
            // emit receives from P2PBattleCardTracker. The Host is executing
            // the Guest request against BattleEnemy, so all resolver lookups
            // must use absolute owner=Guest (0), while targetList/isSelf stays
            // in the action-source-relative native format until the final
            // perspective flip below.
            bool prepared = BattleCardTracker.PrepareOutgoingAction(
                false,
                data,
                out _,
                out _,
                index => ResolveAuthorityCardId(manager, false, index),
                index => ResolveAuthorityCardCost(manager, false, index),
                warning => Plugin.Logger.LogWarning(
                    "[P2P] Authority fusion synchronization: " + warning + "."),
                index => ResolveAuthorityFusionIngredients(manager, false, index));
            if (!prepared)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Authority fusion result did not expose a resolvable " +
                    "playIdx/card identity; native replay will use its current " +
                    "fusion state.");
            }
        }

        private static int ResolveAuthorityCardId(
            NetworkBattleManagerBase manager,
            bool ownerIsHost,
            int index)
        {
            BattleCardBase card = ResolveAuthorityCard(manager, ownerIsHost, index);
            return card?.CardId ?? 0;
        }

        private static int ResolveAuthorityCardCost(
            NetworkBattleManagerBase manager,
            bool ownerIsHost,
            int index)
        {
            BattleCardBase card = ResolveAuthorityCard(manager, ownerIsHost, index);
            return card?.Cost ?? -1;
        }

        private static BattleCardBase ResolveAuthorityCard(
            NetworkBattleManagerBase manager,
            bool ownerIsHost,
            int index)
        {
            if (manager == null || index <= 0)
            {
                return null;
            }

            // Absolute owner values are stable across the two processes. In
            // the Host process owner=1 is BattlePlayer and owner=0 is
            // BattleEnemy; the conditional also keeps this helper correct if
            // it is reused from a Guest-side diagnostic path.
            bool localPlayerOwnsCard = ownerIsHost == (Role == P2PRole.Host);
            BattlePlayerBase player = localPlayerOwnsCard
                ? manager.BattlePlayer
                : manager.BattleEnemy;
            return player == null
                ? null
                : NetworkBattleGenericTool.GetIndexToCardBase(
                    manager, player, index);
        }

        private static IEnumerable<P2PFusionIngredientState>
            ResolveAuthorityFusionIngredients(
                NetworkBattleManagerBase manager,
                bool ownerIsHost,
                int index)
        {
            BattleCardBase card = ResolveAuthorityCard(manager, ownerIsHost, index);
            SkillApplyInformation information = card?.SkillApplyInformation as
                SkillApplyInformation;
            List<P2PFusionIngredientState> current = information?.FusionIngredients == null
                ? new List<P2PFusionIngredientState>()
                : information.FusionIngredients
                    .Where(ingredient => ingredient?.Card != null &&
                        ingredient.Card.Index > 0)
                    .Select(ingredient => new P2PFusionIngredientState(
                        ingredient.Card.Index,
                        ingredient.Card.CardId,
                        ingredient.FusionTurn))
                    .ToList();

            string key = FusionIngredientSnapshotKey(ownerIsHost, index);
            if (current.Count > 0)
            {
                LocalFusionIngredientSnapshots[key] = current
                    .Select(item => new P2PFusionIngredientState(
                        item.Index, item.CardId, item.Turn))
                    .ToList();
                return current;
            }

            return LocalFusionIngredientSnapshots.TryGetValue(
                    key,
                    out List<P2PFusionIngredientState> snapshot)
                ? snapshot.ToList()
                : Enumerable.Empty<P2PFusionIngredientState>();
        }

        private static List<object> BuildAuthorityTargetList(
            NetworkBattleManagerBase manager,
            IEnumerable<BattleCardBase> cards,
            IEnumerable<int> selectSkillIndexes)
        {
            List<object> result = new List<object>();
            if (cards == null)
            {
                return result;
            }

            List<int> skillIndexes = (selectSkillIndexes ?? Enumerable.Empty<int>())
                .Where(index => index >= 0)
                .Distinct()
                .ToList();
            foreach (BattleCardBase card in cards)
            {
                if (!IsValidAuthorityActor(card))
                {
                    continue;
                }
                Dictionary<string, object> target = new Dictionary<string, object>
                {
                    ["targetIdx"] = card.Index,
                    ["isSelf"] = card.IsPlayer ? 1 : 0
                };
                if (skillIndexes.Count > 0)
                {
                    target["selectSkillIndex"] = skillIndexes
                        .Select(index => (object)index)
                        .ToList();
                }
                List<int> validateIndexes =
                    ReadAuthorityValidateSkillIndexes(manager, card);
                if (validateIndexes.Count > 0)
                {
                    target["skillIndex"] = validateIndexes
                        .Select(index => (object)index)
                        .ToList();
                }
                result.Add(target);
            }
            return result;
        }

        private static List<int> ReadAuthorityValidateSkillIndexes(
            NetworkBattleManagerBase manager,
            BattleCardBase target)
        {
            List<int> result = new List<int>();
            if (manager == null || target == null)
            {
                return result;
            }

            try
            {
                object rawList = null;
                if (TryFindInstanceProperty(manager.GetType(),
                        "validateSkillIndexList", out PropertyInfo property))
                {
                    rawList = property.GetValue(manager, null);
                }
                else if (TryFindInstanceField(manager.GetType(),
                        "validateSkillIndexList", out FieldInfo field))
                {
                    rawList = field.GetValue(manager);
                }
                if (rawList is string || !(rawList is IEnumerable values))
                {
                    return result;
                }

                foreach (object value in values)
                {
                    NetworkBattleManagerBase.ValidateSkillData validate =
                        value as NetworkBattleManagerBase.ValidateSkillData;
                    if (validate == null || validate.CardIndex != target.Index ||
                        validate.isPlayer != target.IsPlayer ||
                        validate.SkillIndex < 0 ||
                        result.Contains(validate.SkillIndex))
                    {
                        continue;
                    }
                    result.Add(validate.SkillIndex);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not read authoritative validate-skill " +
                    "indexes: " + ex.Message);
            }
            return result;
        }

        private static void NormalizeAuthorityResultFieldOrder(
            Dictionary<string, object> data)
        {
            if (data == null || !data.ContainsKey("keyAction"))
            {
                return;
            }

            // SendCardDataMaker.MakePlayActionsSendCardData writes keyAction as
            // part of the basic card payload, before orderList/uList.  The
            // NetworkBattleReceiver consumes dictionary entries in insertion
            // order and uses keyAction to establish transformBeforeCardId
            // before knownList is converted.  Rebuild the dictionary so the
            // manually assembled authority result has the same native order.
            if (!data.ContainsKey("knownList") && !data.ContainsKey("orderList"))
            {
                return;
            }

            List<KeyValuePair<string, object>> fields = data.ToList();
            data.Clear();
            HashSet<string> moved = new HashSet<string>(StringComparer.Ordinal)
            {
                "targetList", "keyAction", "knownList", "orderList", "uList"
            };
            foreach (KeyValuePair<string, object> field in fields)
            {
                if (moved.Contains(field.Key))
                {
                    continue;
                }
                data[field.Key] = field.Value;
            }
            foreach (string key in new[]
            {
                "targetList", "keyAction", "knownList", "orderList", "uList"
            })
            {
                KeyValuePair<string, object> field = fields.FirstOrDefault(item =>
                    string.Equals(item.Key, key, StringComparison.Ordinal));
                if (field.Key != null)
                {
                    data[field.Key] = field.Value;
                }
            }
        }

        private static bool TryAppendAuthorityNativeKeyActions(
            NetworkBattleManagerBase manager,
            Dictionary<string, object> data,
            BattleCardBase actor)
        {
            if (manager == null || data == null || actor == null)
            {
                return false;
            }

            try
            {
                if (!TryFindInstanceField(
                        manager.GetType(), "sendKeyActionDataManager",
                        out FieldInfo field))
                {
                    return false;
                }
                object keyActionManager = field.GetValue(manager);
                MethodInfo makeSendData = keyActionManager?.GetType().GetMethod(
                    "MakeSendData",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(Dictionary<string, object>), typeof(int) },
                    null);
                if (makeSendData == null)
                {
                    return false;
                }

                object value = makeSendData.Invoke(
                    keyActionManager, new object[] { data, actor.Index });
                return value is Dictionary<string, object> result &&
                    result.TryGetValue("keyAction", out object rawActions) &&
                    rawActions is IEnumerable actions &&
                    !(rawActions is string) && actions.Cast<object>().Any();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not reuse native key-action data for the " +
                    "authority result: " + ex.Message);
                return false;
            }
        }

        private static void BuildAuthorityRegisterData(
            NetworkBattleManagerBase manager,
            bool isTurnStart,
            out List<object> orderList,
            out List<object> unapproved)
        {
            orderList = new List<object>();
            unapproved = new List<object>();
            if (manager == null)
            {
                return;
            }

            try
            {
                List<RegisterUnapproved> source =
                    GetRegisterUnapprovedList(manager);
                SendCardDataMaker maker = new SendCardDataMaker(
                    manager, manager.RegisterActionManager, source);

                // Preserve the exact ordering used by the native sender.
                InvokePrivateSendMaker(maker, "SwapTransformMetamorphoseData");
                InvokePrivateSendMaker(maker, "DisCardCheckAndRemoveUlist");
                if (source.Count > 0)
                {
                    object rawUnapproved = InvokePrivateSendMaker(
                        maker, "MakeUList", source);
                    if (rawUnapproved is IEnumerable values)
                    {
                        foreach (object value in values)
                        {
                            unapproved.Add(value);
                        }
                    }
                }
                InvokePrivateSendMaker(maker, "GatheredRegisterCard");
                InvokePrivateSendMaker(
                    maker, "SettingStateChangeCardToSkillTarget");
                InvokePrivateSendMaker(
                    maker, "InsertionTokenAfterStateChange");
                InvokePrivateSendMaker(
                    maker, "InsertionExtractAfterValidate");
                object rawOrder = InvokePrivateSendMaker(
                    maker, "OrderListCreate", isTurnStart);
                if (rawOrder is IEnumerable orderValues)
                {
                    foreach (object value in orderValues)
                    {
                        orderList.Add(value);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    "[P2P] Could not build authoritative native register data: " +
                    ex.Message);
            }
        }

        // The native sender normally exposes every card movement through
        // orderList/uList.  In a friends-only authority room both sides may
        // know private identities, but those identities still have to travel in
        // the original knownList field before NetworkBattleData constructs the
        // operation.  Do not defer an identity replacement until after VFX.
        private static void EnsureNativePrivateMoveIdentities(
            NetworkBattleManagerBase manager,
            Dictionary<string, object> data)
        {
            if (manager == null || data == null)
            {
                return;
            }

            List<object> knownList = GetOrCreateKnownList(data);
            int ensured = 0;
            foreach (string listName in new[] { "orderList", "uList" })
            {
                if (!data.TryGetValue(listName, out object rawEntries) ||
                    rawEntries is string || !(rawEntries is IEnumerable entries))
                {
                    continue;
                }

                foreach (object rawEntry in entries)
                {
                    foreach (Dictionary<string, object> move in
                        EnumerateNativeMoveData(rawEntry))
                    {
                        if (!TryGetStateInt(move, "to", out int to) ||
                            !IsNativePrivateDestination(to) ||
                            !TryGetStateInt(move, "isSelf", out int rawSelf))
                        {
                            continue;
                        }

                        bool ownerIsHost = rawSelf != 0;
                        foreach (int index in EnumerateNativeMoveIndices(move))
                        {
                            if (index <= 0)
                            {
                                continue;
                            }

                            int cardId = TryGetStateInt(move, "cardId",
                                out int listedCardId)
                                ? listedCardId
                                : 0;
                            int cost = -1;
                            BattleCardBase card = ResolveAuthorityCard(
                                manager, ownerIsHost, index);
                            if (cardId <= 0)
                            {
                                cardId = card?.CardId ?? 0;
                            }
                            if (card != null)
                            {
                                cost = card.Cost;
                            }
                            else if (TryGetStateInt(move, "cost", out int listedCost))
                            {
                                cost = listedCost;
                            }

                            if (cardId <= 0)
                            {
                                Plugin.Logger.LogWarning(
                                    "[P2P] Native private move has no resolvable " +
                                    "card identity: owner=" +
                                    (ownerIsHost ? "Host" : "Guest") +
                                    ", idx=" + index + ", from=" +
                                    (TryGetStateInt(move, "from", out int from)
                                        ? from.ToString(CultureInfo.InvariantCulture)
                                        : "?") + ", to=" + to + ".");
                                continue;
                            }

                            UpsertNativeKnownCard(knownList, index, cardId,
                                ownerIsHost, cost, move);
                            ensured++;
                        }
                    }
                }
            }

            if (ensured > 0)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Added " + ensured +
                    " private movement identity entry(s) to native knownList.");
            }
        }

        private static IEnumerable<Dictionary<string, object>>
            EnumerateNativeMoveData(object rawEntry, int depth = 0)
        {
            if (rawEntry is Dictionary<string, object> entry)
            {
                if (entry.ContainsKey("from") && entry.ContainsKey("to") &&
                    (entry.ContainsKey("idx") || entry.ContainsKey("idxList")))
                {
                    yield return entry;
                    yield break;
                }

                if (depth >= 3)
                {
                    yield break;
                }
                foreach (object value in entry.Values)
                {
                    foreach (Dictionary<string, object> nested in
                        EnumerateNativeMoveData(value, depth + 1))
                    {
                        yield return nested;
                    }
                }
                yield break;
            }

            if (rawEntry is IEnumerable entries && !(rawEntry is string) &&
                depth < 3)
            {
                foreach (object item in entries)
                {
                    foreach (Dictionary<string, object> nested in
                        EnumerateNativeMoveData(item, depth + 1))
                    {
                        yield return nested;
                    }
                }
            }
        }

        private static IEnumerable<int> EnumerateNativeMoveIndices(
            Dictionary<string, object> move)
        {
            if (move == null)
            {
                yield break;
            }
            if (TryGetStateInt(move, "idx", out int scalar) && scalar > 0)
            {
                yield return scalar;
                yield break;
            }
            if (!move.TryGetValue("idxList", out object rawIndices) ||
                rawIndices is string || !(rawIndices is IEnumerable indices))
            {
                yield break;
            }
            foreach (object rawIndex in indices)
            {
                if (TryConvertAuthorityInt(rawIndex, out int index) && index > 0)
                {
                    yield return index;
                }
            }
        }

        private static bool IsNativePrivateDestination(int place)
        {
            return place == (int)NetworkBattleDefine.NetworkCardPlaceState.Deck ||
                place == (int)NetworkBattleDefine.NetworkCardPlaceState.Hand ||
                place == (int)NetworkBattleDefine.NetworkCardPlaceState.FusionIngredient ||
                place == (int)NetworkBattleDefine.NetworkCardPlaceState.Reservation;
        }

        private static void UpsertNativeKnownCard(
            List<object> knownList,
            int index,
            int cardId,
            bool ownerIsHost,
            int cost,
            Dictionary<string, object> move)
        {
            if (knownList == null || index <= 0 || cardId <= 0)
            {
                return;
            }

            Dictionary<string, object> canonical = null;
            foreach (Dictionary<string, object> entry in knownList
                .OfType<Dictionary<string, object>>()
                .Where(entry => IsSelfKnownCard(entry) == ownerIsHost &&
                    KnownCardContainsIndex(entry, index))
                .ToList())
            {
                if (TryGetStateInt(entry, "idx", out int scalarIndex) &&
                    scalarIndex == index)
                {
                    if (canonical == null)
                    {
                        canonical = entry;
                    }
                    else
                    {
                        knownList.Remove(entry);
                    }
                    continue;
                }

                if (entry.TryGetValue("idxList", out object rawIndices) &&
                    rawIndices is IEnumerable indices && !(rawIndices is string))
                {
                    List<object> remaining = indices.Cast<object>()
                        .Where(rawIndex => !TryConvertAuthorityInt(rawIndex,
                            out int groupedIndex) || groupedIndex != index)
                        .ToList();
                    if (remaining.Count == 0)
                    {
                        knownList.Remove(entry);
                    }
                    else
                    {
                        entry["idxList"] = remaining;
                    }
                }
            }

            if (canonical == null)
            {
                canonical = new Dictionary<string, object>();
                knownList.Add(canonical);
            }
            canonical.Remove("idxList");
            canonical["idx"] = index;
            canonical["cardId"] = cardId;
            canonical["isSelf"] = ownerIsHost ? 1 : 0;
            canonical["is_open"] = 1;
            if (cost >= 0)
            {
                canonical["cost"] = cost;
            }
            if (TryGetStateInt(move, "from", out int from))
            {
                canonical["from"] = from;
            }
            if (TryGetStateInt(move, "to", out int to))
            {
                canonical["to"] = to;
            }
        }

        private static int AuthorityActionType(string action)
        {
            switch (action)
            {
                case "attack": return 10;
                case "evolution": return 20;
                case "fusion": return 40;
                case "play": return 31;
                default: return 30;
            }
        }

        private static Dictionary<string, object> BuildAuthorityTurnEndActionsData(
            NetworkBattleManagerBase manager,
            string requestId)
        {
            if (manager == null)
            {
                return null;
            }
            Dictionary<string, object> data = new Dictionary<string, object>
            {
                ["uri"] = P2PBattleProtocol.TurnEndActionsUri,
                ["type"] = 0,
                ["turnState"] = 0,
                [P2PBattleProtocol.AuthorityResultRequestIdKey] = requestId,
                [P2PBattleProtocol.AuthoritySourceKey] = 0
            };
            BuildAuthorityRegisterData(
                manager, false, out List<object> orderList,
                out List<object> unapproved);
            if (orderList.Count > 0)
            {
                data["orderList"] = orderList;
            }
            if (unapproved.Count > 0)
            {
                data["uList"] = unapproved;
            }
            EnsureNativePrivateMoveIdentities(manager, data);
            AppendAuthorityPreActionHistory(data);
            AttachActionPreHiddenMetamorphoseOriginals(data);
            AppendAuthorityResultMetadata(manager, data);
            AppendAuthorityExecutionMetadata(
                P2PBattleProtocol.TurnEndActionsUri, data);
            Dictionary<string, object> endState = !IsHostAuthorityMode
                ? CaptureBattleState()
                : null;
            if (endState != null)
            {
                data[P2PBattleStateDiagnostics.StateKey] = endState;
            }
            Dictionary<string, object> guestData = P2PMessageTransform.FlipPerspective(data);
            guestData["uri"] = P2PBattleProtocol.TurnEndActionsUri;
            guestData["p2pAuthorityLocalReplay"] = 1;
            guestData[P2PBattleProtocol.AuthorityResultRequestIdKey] = requestId;
            return guestData;
        }

        private static Dictionary<string, object> BuildAuthorityTurnEndData(
            NetworkBattleManagerBase manager,
            string requestId)
        {
            if (manager == null)
            {
                return null;
            }

            // TurnEndActions carries registered skill/card operations. The
            // separate TurnEnd packet is not optional: the stock receiver uses
            // it to close the turn boundary, validate the consistency payload,
            // and decide whether to emit the compatibility Judge. Earlier
            // authority code sent only a marker here, which diverged from the
            // native NetworkBattleSender.SendTurnEnd envelope.
            Dictionary<string, object> data = new Dictionary<string, object>
            {
                ["uri"] = P2PBattleProtocol.TurnEndUri,
                ["type"] = 0,
                ["turnState"] = 0,
                [P2PBattleProtocol.AuthorityResultRequestIdKey] = requestId,
                [P2PBattleProtocol.AuthoritySourceKey] = 0,
                ["actionSeq"] = GetNativeTurnSequence(manager),
                ["cemetery"] = new List<object>
                {
                    manager.BattlePlayer?.CemeteryList?.Count ?? 0,
                    manager.BattleEnemy?.CemeteryList?.Count ?? 0
                }
            };
            Dictionary<string, object> consistency =
                BuildAuthorityConsistency(manager);
            if (consistency != null && consistency.Count > 0)
            {
                data["battleCode"] = consistency;
            }

            // Turn-end effects can still mutate a private zone after the last
            // register entry was assembled. Capture the final state at this
            // protocol boundary as the native server does for its final
            // TurnEnd envelope. Signature tracking keeps this empty when the
            // preceding TurnEndActions already published the same state.
            AppendAuthorityResultMetadata(manager, data);
            Dictionary<string, object> endState = !IsHostAuthorityMode
                ? CaptureBattleState()
                : null;
            if (endState != null)
            {
                data[P2PBattleStateDiagnostics.StateKey] = endState;
            }

            Dictionary<string, object> guestData =
                P2PMessageTransform.FlipPerspective(data);
            guestData["uri"] = P2PBattleProtocol.TurnEndUri;
            guestData["p2pAuthorityLocalReplay"] = 1;
            guestData[P2PBattleProtocol.AuthorityResultRequestIdKey] = requestId;
            return guestData;
        }

        private static Dictionary<string, object> BuildAuthorityConsistency(
            NetworkBattleManagerBase manager)
        {
            if (manager == null || !TryFindInstanceField(
                    manager.GetType(), "networkConsistency",
                    out FieldInfo field))
            {
                return null;
            }

            try
            {
                object consistency = field.GetValue(manager);
                if (consistency == null)
                {
                    return null;
                }
                consistency.GetType().GetMethod(
                        "SetupConsistency",
                        BindingFlags.Instance | BindingFlags.Public)
                    ?.Invoke(consistency, null);
                return consistency.GetType().GetMethod(
                        "GetConsistency",
                        BindingFlags.Instance | BindingFlags.Public)
                    ?.Invoke(consistency, null) as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not build authoritative TurnEnd consistency: " +
                    ex.Message);
                return null;
            }
        }

        private static int GetNativeTurnSequence(
            NetworkBattleManagerBase manager)
        {
            try
            {
                RealTimeNetworkAgent agent = currentAgent ??
                    ToolboxGame.RealTimeNetworkAgent;
                if (agent != null)
                {
                    int sequence = agent.GetTurnSequence();
                    if (sequence >= 0)
                    {
                        return sequence;
                    }
                }
            }
            catch (Exception)
            {
            }
            return manager?.CurrentTurn ?? 0;
        }

        private static Dictionary<string, object> BuildAuthorityTurnStartData(
            NetworkBattleManagerBase manager,
            string requestId,
            int turnOwner,
            bool extraTurn)
        {
            if (manager == null || (turnOwner != 0 && turnOwner != 1))
            {
                return null;
            }

            Dictionary<string, object> data = new Dictionary<string, object>
            {
                ["uri"] = P2PBattleProtocol.TurnStartUri,
                ["turnState"] = 0,
                [P2PBattleProtocol.AuthorityResultRequestIdKey] = requestId,
                [P2PBattleProtocol.AuthoritySourceKey] = 0,
                [P2PBattleProtocol.AuthorityTurnOwnerKey] = turnOwner,
                [P2PBattleProtocol.AuthorityTurnExtraKey] = extraTurn ? 1 : 0,
                ["actionSeq"] = GetNativeTurnSequence(manager)
            };

            BuildAuthorityRegisterData(
                manager, true, out List<object> orderList,
                out List<object> unapproved);
            if (orderList.Count > 0)
            {
                data["orderList"] = orderList;
            }
            if (unapproved.Count > 0)
            {
                data["uList"] = unapproved;
            }
            EnsureNativePrivateMoveIdentities(manager, data);
            AppendAuthorityPreActionHistory(data);
            AttachActionPreHiddenMetamorphoseOriginals(data);
            AppendAuthorityResultMetadata(manager, data);
            AppendAuthorityExecutionMetadata(
                P2PBattleProtocol.TurnStartUri, data);
            Dictionary<string, object> startState = !IsHostAuthorityMode
                ? CaptureBattleState()
                : null;
            if (startState != null)
            {
                data[P2PBattleStateDiagnostics.StateKey] = startState;
            }

            Dictionary<string, object> guestData =
                P2PMessageTransform.FlipPerspective(data);
            guestData["uri"] = P2PBattleProtocol.TurnStartUri;
            guestData["p2pAuthorityLocalReplay"] = 1;
            guestData[P2PBattleProtocol.AuthorityResultRequestIdKey] = requestId;
            return guestData;
        }

        private static List<object> BuildAuthorityOrderList(NetworkBattleManagerBase manager)
        {
            List<object> result = new List<object>();
            try
            {
                SendCardDataMaker maker = new SendCardDataMaker(
                    manager, manager.RegisterActionManager, GetRegisterUnapprovedList(manager));
                InvokePrivateSendMaker(maker, "DisCardCheckAndRemoveUlist");
                InvokePrivateSendMaker(maker, "GatheredRegisterCard");
                InvokePrivateSendMaker(maker, "SettingStateChangeCardToSkillTarget");
                InvokePrivateSendMaker(maker, "InsertionTokenAfterStateChange");
                InvokePrivateSendMaker(maker, "InsertionExtractAfterValidate");
                object raw = InvokePrivateSendMaker(maker, "OrderListCreate", false);
                if (raw is IEnumerable values)
                {
                    foreach (object value in values)
                    {
                        result.Add(value);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[P2P] Could not build authoritative orderList: " + ex.Message);
            }
            return result;
        }

        private static List<object> BuildAuthorityUnapprovedList(NetworkBattleManagerBase manager)
        {
            List<object> result = new List<object>();
            try
            {
                List<RegisterUnapproved> source = GetRegisterUnapprovedList(manager);
                if (source.Count == 0)
                {
                    return result;
                }
                SendCardDataMaker maker = new SendCardDataMaker(
                    manager, manager.RegisterActionManager, source);
                object raw = InvokePrivateSendMaker(maker, "MakeUList", source);
                if (raw is IEnumerable values)
                {
                    foreach (object value in values)
                    {
                        result.Add(value);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning("[P2P] Could not build authoritative uList: " + ex.Message);
            }
            return result;
        }

        private static object InvokePrivateSendMaker(
            SendCardDataMaker maker,
            string name,
            params object[] args)
        {
            MethodInfo method = maker.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new MissingMethodException(typeof(SendCardDataMaker).FullName, name);
            }
            return method.Invoke(maker, args);
        }

        private static List<RegisterUnapproved> GetRegisterUnapprovedList(
            NetworkBattleManagerBase manager)
        {
            return manager?.RegisterUnapprovedList ?? new List<RegisterUnapproved>();
        }

        private static List<object> BuildAuthorityKeyActions(
            string action,
            BattleCardBase actor,
            List<BattleCardBase> selected,
            List<int> choiceIds,
            bool choiceBrave,
            IEnumerable<int> selectedHandIndices,
            int originalCardIdOverride = 0)
        {
            List<object> result = new List<object>();
            if (actor == null)
            {
                return result;
            }
            int originalId = originalCardIdOverride > 0
                ? originalCardIdOverride
                : actor.CardId;
            if (string.Equals(action, "fusion", StringComparison.Ordinal))
            {
                if (selected != null && selected.Count > 0)
                {
                    result.Add(new Dictionary<string, object>
                    {
                        ["type"] = (int)SendKeyActionDataManager.KeyActionType.Fusion,
                        ["cardId"] = originalId,
                        ["selectCard"] = new Dictionary<string, object>
                        {
                            ["cardId"] = selected.Select(card => (object)card.CardId).ToList(),
                            ["open"] = 1
                        }
                    });
                }
                return result;
            }
            if (choiceBrave)
            {
                result.Add(new Dictionary<string, object>
                {
                    ["type"] = (int)SendKeyActionDataManager.KeyActionType.ChoiceBrave,
                    ["cardId"] = originalId,
                    ["selectCard"] = new Dictionary<string, object>
                    {
                        ["cardId"] = new List<object> { originalId },
                        ["open"] = 1
                    }
                });
                return result;
            }
            if (choiceIds != null && choiceIds.Count > 0)
            {
                result.Add(new Dictionary<string, object>
                {
                    ["type"] = (int)SendKeyActionDataManager.KeyActionType.Choice,
                    ["cardId"] = originalId,
                    ["selectCard"] = new Dictionary<string, object>
                    {
                        ["cardId"] = choiceIds.Select(value => (object)value).ToList(),
                        ["open"] = 1
                    }
                });
            }
            List<int> handIndices = selectedHandIndices == null
                ? new List<int>()
                : selectedHandIndices.Where(index => index > 0).Distinct().ToList();
            if (handIndices.Count > 0)
            {
                result.Add(new Dictionary<string, object>
                {
                    ["type"] = (int)SendKeyActionDataManager.KeyActionType.BurialRate,
                    ["cardId"] = originalId,
                    ["selectCard"] = new Dictionary<string, object>
                    {
                        ["cardIdx"] = handIndices
                            .Select(index => (object)index).ToList(),
                        ["open"] = 1
                    }
                });
            }
            return result;
        }

        private static void AppendAuthorityResultMetadata(
            NetworkBattleManagerBase manager,
            Dictionary<string, object> data)
        {
            if (manager == null || data == null)
            {
                return;
            }
            Dictionary<int, Dictionary<string, object>> hiddenSnapshots =
                new Dictionary<int, Dictionary<string, object>>();
            Dictionary<int, Dictionary<string, object>> historySnapshots =
                new Dictionary<int, Dictionary<string, object>>();

            // Until the peer has acknowledged this process's initial private
            // baseline, include every current private card in each ordered
            // authority result. This closes the startup race where an action
            // arrives before the one-shot baseline has reached the receiver.
            // Once the baseline is acknowledged, the same method falls back to
            // signature-based incremental snapshots.
            bool forceAll = !localPrivateStateAcknowledged;
            CaptureAuthorityPrivateSnapshot(
                manager.BattlePlayer, true, hiddenSnapshots, forceAll);
            CaptureAuthorityPrivateSnapshot(
                manager.BattleEnemy, false, hiddenSnapshots, forceAll);
            CaptureAuthorityPlayerHistorySnapshot(
                manager.BattlePlayer, true, historySnapshots);
            CaptureAuthorityPlayerHistorySnapshot(
                manager.BattleEnemy, false, historySnapshots);

            if (hiddenSnapshots.Count > 0)
            {
                data[P2PBattleProtocol.AuthorityHiddenStatesKey] =
                    hiddenSnapshots.Values
                        .Select(snapshot => (object)P2PJson.CloneDictionary(snapshot))
                        .ToList();
            }
            if (historySnapshots.Count > 0)
            {
                data[P2PBattleProtocol.AuthorityPlayerHistoryStatesKey] =
                    historySnapshots.Values
                        .Select(snapshot => (object)P2PJson.CloneDictionary(snapshot))
                        .ToList();
            }

            // Keep the original single-owner side channel populated for the
            // current protocol and for a peer that has not yet learned the
            // multi-owner extension.  Guest authority requests always use the
            // absolute Guest owner (0) as their source.
            if (hiddenSnapshots.TryGetValue(
                    0, out Dictionary<string, object> guestHidden))
            {
                data["p2pHiddenOwner"] = 0;
                data["p2pHiddenCards"] = guestHidden.TryGetValue(
                        "cards", out object rawCards)
                    ? P2PJson.CloneValue(rawCards)
                    : new List<object>();
                if (guestHidden.TryGetValue("removed", out object rawRemoved))
                {
                    data["p2pHiddenRemoved"] = P2PJson.CloneValue(rawRemoved);
                }
            }
            if (historySnapshots.TryGetValue(
                    0, out Dictionary<string, object> guestHistory))
            {
                data["p2pPlayerHistory"] = P2PJson.CloneDictionary(guestHistory);
            }
        }

        private static void AppendAuthorityPreActionHistory(
            Dictionary<string, object> data)
        {
            if (data == null || localActionPreHistoryState == null ||
                !TryGetStateInt(localActionPreHistoryState, "owner",
                    out int owner) || owner != 0)
            {
                return;
            }

            // The Guest request is evaluated by the Host against this
            // pre-action history. Reapply the same state before the Guest
            // invokes the native receiver; otherwise deterministic filters
            // can still observe the post-action counters too early.
            Dictionary<string, object> before =
                P2PJson.CloneDictionary(localActionPreHistoryState);
            before.Remove("revision");
            before["revision"] = Math.Max(1, localActionPreHistoryRevision);
            data[PlayerHistoryStateBeforeKey] = before;
        }

        private static void AppendAuthorityExecutionMetadata(
            string uri,
            Dictionary<string, object> data)
        {
            if (!ShouldPublishAuthoritativeActionManifest(uri, data))
            {
                return;
            }

            // Native network emits are suppressed while the Host executes a
            // Guest request, so the normal HandleEmitNow path cannot attach the
            // source-side random-target and private-condition manifest. Reuse
            // that exact path here before the result is perspective-transformed.
            DrainPendingLocalConditionResults();
            AppendLocalActionManifest(uri, data);
            AppendLocalAuthoritativeSkillTargets(uri, data);
            AppendLocalAuthoritativeSkillEvaluations(uri, data);
        }

        private static bool ShouldPublishAuthoritativeActionManifest(
            string uri,
            Dictionary<string, object> data)
        {
            // Protocol v3 uses the native server envelope exclusively.  The
            // manifest was a migration side-channel for the retired replay
            // implementation; publishing it during native timing would make
            // the receiver evaluate conditions/random targets twice.
            if (UseNativeClientActionTiming)
            {
                return false;
            }

            // A Host-generated native message is already the authoritative
            // result. Only an explicit Guest PlayActions replay still needs
            // the compatibility manifest while the remaining hidden-state
            // adapter is being retired. TurnStart/TurnEnd and normal Host
            // actions must not carry a second condition program.
            return IsHostAuthorityMode && Role == P2PRole.Host &&
                string.Equals(uri,
                    NetworkBattleDefine.NetworkBattleURI.PlayActions.ToString(),
                    StringComparison.Ordinal) &&
                data != null && data.TryGetValue(
                    P2PBattleProtocol.AuthorityResultRequestIdKey,
                    out object requestId) &&
                !string.IsNullOrEmpty(requestId?.ToString());
        }

        private static void CaptureAuthorityPrivateSnapshot(
            BattlePlayerBase player,
            bool ownerIsHost,
            Dictionary<int, Dictionary<string, object>> destination,
            bool forceAll = false)
        {
            if (player == null || destination == null)
            {
                return;
            }

            int owner = ownerIsHost ? 1 : 0;
            if (!authorityKnownPrivateIndicesByOwner.TryGetValue(
                    owner, out HashSet<int> knownIndices))
            {
                knownIndices = new HashSet<int>();
                authorityKnownPrivateIndicesByOwner[owner] = knownIndices;
            }

            HashSet<int> present = new HashSet<int>();
            List<object> changedCards = new List<object>();
            try
            {
                foreach (BattleCardBase card in EnumeratePrivateCards(player))
                {
                    if (card == null || card.Index <= 0 || card.CardId <= 0)
                    {
                        continue;
                    }

                    present.Add(card.Index);
                    Dictionary<string, object> state = CreateHiddenCardState(card);
                    string signature = JsonConvert.SerializeObject(
                        state, P2PJson.Settings);
                    string key = HiddenStateKey(ownerIsHost, card.Index);
                    authorityPrivateStates.TryGetValue(
                        key, out Dictionary<string, object> previousState);
                    bool changed = forceAll ||
                        !authorityPrivateStateSignatures.TryGetValue(
                            key, out string previous) ||
                        !string.Equals(previous, signature, StringComparison.Ordinal);
                    if (changed)
                    {
                        authorityPrivateStateSignatures[key] = signature;
                        authorityPrivateStates[key] =
                            P2PJson.CloneDictionary(state);
                        changedCards.Add(forceAll
                            ? P2PJson.CloneDictionary(state)
                            : CreateHiddenCardDelta(previousState, state)
                                ?? P2PJson.CloneDictionary(state));
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not capture authoritative private state for " +
                    SideName(ownerIsHost) + ": " + ex.Message);
            }

            List<object> removed = knownIndices
                .Where(index => !present.Contains(index))
                .Select(index => (object)index)
                .ToList();
            foreach (int index in removed)
            {
                authorityPrivateStateSignatures.Remove(
                    HiddenStateKey(ownerIsHost, index));
                authorityPrivateStates.Remove(
                    HiddenStateKey(ownerIsHost, index));
            }
            knownIndices.Clear();
            foreach (int index in present)
            {
                knownIndices.Add(index);
            }

            if (changedCards.Count == 0 && removed.Count == 0)
            {
                return;
            }

            destination[owner] = new Dictionary<string, object>
            {
                ["owner"] = owner,
                ["cards"] = changedCards,
                ["removed"] = removed
            };
        }

        private static void CaptureAuthorityPlayerHistorySnapshot(
            BattlePlayerBase player,
            bool ownerIsHost,
            Dictionary<int, Dictionary<string, object>> destination)
        {
            if (player == null || destination == null)
            {
                return;
            }

            int owner = ownerIsHost ? 1 : 0;
            Dictionary<string, object> state;
            try
            {
                state = CapturePlayerHistoryState(player, ownerIsHost);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not capture authoritative history for " +
                    SideName(ownerIsHost) + ": " + ex.Message);
                return;
            }

            string signature = JsonConvert.SerializeObject(state, P2PJson.Settings);
            if (authorityPlayerHistorySignatures.TryGetValue(
                    owner, out string previous) &&
                string.Equals(previous, signature, StringComparison.Ordinal))
            {
                return;
            }

            authorityPlayerHistorySignatures[owner] = signature;
            int revision = authorityPlayerHistoryRevisions.TryGetValue(
                    owner, out int currentRevision)
                ? currentRevision
                : 0;
            if (ownerIsHost == (Role == P2PRole.Host))
            {
                revision = Math.Max(revision, localPlayerHistoryRevision);
            }
            revision = Math.Max(1, revision + 1);
            authorityPlayerHistoryRevisions[owner] = revision;
            state["revision"] = revision;
            destination[owner] = state;
        }

        private static void FlipAuthorityTargetListPerspective(
            Dictionary<string, object> data)
        {
            if (data == null || !data.TryGetValue("targetList", out object raw) ||
                raw is string || !(raw is IEnumerable values))
            {
                return;
            }
            foreach (object value in values)
            {
                if (!(value is Dictionary<string, object> target) ||
                    !target.TryGetValue("isSelf", out object rawSelf) ||
                    !TryConvertAuthorityInt(rawSelf, out int isSelf))
                {
                    continue;
                }
                target["isSelf"] = isSelf == 0 ? 1 : 0;
            }
        }

        private static void EnsureAuthorityRandomResultsAreValid(
            Dictionary<string, object> data,
            string requestId)
        {
            if (data == null || Role != P2PRole.Host)
            {
                return;
            }

            // Guest authority actions are executed by the Host's native
            // NetworkOperationCollection.  The resulting RegisterUnapproved
            // values are therefore Host-generated. Validate them immediately
            // before delivery so an incomplete/stale compatibility manifest
            // cannot become a second random-result source.
            if (!P2PAuthoritativeServer.ValidateAndRecordNativeRandomResults(
                    data,
                    P2PAuthoritativeServer.StateRevision,
                    false,
                    out string error))
            {
                throw new InvalidOperationException(
                    "the Host-generated native random result was invalid for " +
                    (requestId ?? "<missing>") + ": " + error);
            }
        }

        private static void DeliverAuthorityResult(Dictionary<string, object> data)
        {
            if (data == null || Role != P2PRole.Host || peerDisconnected)
            {
                return;
            }
            data[P2PBattleProtocol.AuthorityResultActionIdKey] =
                ++authorityResultActionSequence;
            data["viewerId"] = RemoteProfile?.ViewerId ?? 0;
            data["bid"] = BattleId ?? string.Empty;
            data["p2pAuthorityLocalReplay"] = 1;
            // Authority results are ordered independently from the old host
            // action stream. Reuse the guest delivery sequence so the native
            // agent still observes monotonically increasing playSeq values.
            Deliver(false, data, RemoteProfile?.ViewerId ?? 0);
            Plugin.Logger.LogDebug("[P2P] Authority result delivered: actionId=" +
                authorityResultActionSequence + ", requestId=" +
                (data.TryGetValue(P2PBattleProtocol.AuthorityResultRequestIdKey, out object id)
                    ? id?.ToString() : "?") + ".");
        }

        private static void TryDisableLocalBattleMenu()
        {
            try
            {
                NetworkBattleManagerBase manager = BattleManagerBase.GetIns() as NetworkBattleManagerBase;
                manager?.BattleUIContainer?.DisableMenu(false);
                manager?.BattlePlayer?.BattleView?.TurnEndButtonUI?.HideBtn();
            }
            catch (Exception)
            {
            }
        }

        private static void TryEnableLocalBattleMenu()
        {
            try
            {
                NetworkBattleManagerBase manager = BattleManagerBase.GetIns() as NetworkBattleManagerBase;
                manager?.BattleUIContainer?.RequestEnableMenuWhenTouchable();
            }
            catch (Exception)
            {
            }
        }

        internal static bool TryProcessAuthorityLocalReplay(
            RealTimeNetworkBattleAgent agent,
            Dictionary<string, object> data,
            out bool result)
        {
            result = false;
            if (UseNativeClientActionTiming)
            {
                if (data != null && ReadAuthorityBool(
                        data, "p2pAuthorityLocalReplay"))
                {
                    // A packet from the retired request/result protocol must
                    // never fall through into the ordinary receiver: its
                    // perspective and target fields were authored for a local
                    // replay, not an opponent action. Consume it explicitly
                    // and keep the current native packet stream intact.
                    Plugin.Logger.LogWarning(
                        "[P2P] Dropped a legacy authority replay packet: uri=" +
                        GetUri(data) + ".");
                    result = true;
                    return true;
                }
                return false;
            }
            if (!IsHostAuthorityMode || Role != P2PRole.Guest ||
                agent == null || data == null ||
                !ReadAuthorityBool(data, "p2pAuthorityLocalReplay"))
            {
                return false;
            }

            if (!authorityReplayDispatchActive)
            {
                if (PendingAuthorityReplayActions.Count >=
                    MaxPendingReceivedBattleMessages)
                {
                    ReportBattleDiagnostic(
                        "Authority replay queue exceeded " +
                        MaxPendingReceivedBattleMessages + "; dropping uri=" +
                        GetUri(data) + ", actionId=" +
                        GetAuthorityActionId(data) + ".");
                    result = true;
                    return true;
                }

                // InjectNow reserves an ordered-packet boundary before the
                // realtime agent dispatches PlayReceiveData. Authority replay
                // is intercepted at that later point and moved to this queue,
                // so release the provisional reservation; the dispatcher will
                // establish the real native boundary when it calls
                // ReceivedMessage.
                ReleaseReservedReceivedBattleActionInjection(data);
                PendingAuthorityReplayActions.Enqueue(
                    new PendingAuthorityReplayAction(
                        agent,
                        P2PJson.CloneDictionary(data)));
                Plugin.Logger.LogDebug(
                    "[P2P] Queued authority result: actionId=" +
                    GetAuthorityActionId(data) + ", uri=" + GetUri(data) +
                    ", queued=" + PendingAuthorityReplayActions.Count + ".");
                // Consume the realtime-agent packet now. The dedicated queue
                // dispatches it only when the preceding replay transaction has
                // reached its native completion boundary.
                result = true;
                return true;
            }

            string replayUri = GetUri(data);
            string replayRequestId = data.TryGetValue(
                    P2PBattleProtocol.AuthorityResultRequestIdKey,
                    out object rawReplayRequestId)
                ? rawReplayRequestId?.ToString()
                : null;
            Plugin.Logger.LogInfo(
                "[P2P] Guest received authority result: uri=" + replayUri +
                ", requestId=" + (replayRequestId ?? "<missing>") + ".");
            NetworkBattleManagerBase manager = null;
            try
            {
                if (TryFindInstanceField(agent.GetType(), "_networkBattleManager",
                        out FieldInfo field))
                {
                    manager = field.GetValue(agent) as NetworkBattleManagerBase;
                }
                if (manager == null)
                {
                    manager = BattleManagerBase.GetIns() as NetworkBattleManagerBase;
                }
                if (manager == null || manager.GetNetworkBattleReceiver() == null ||
                    !data.TryGetValue("uri", out object rawUri) ||
                    !Enum.TryParse(rawUri?.ToString(), out NetworkBattleDefine.NetworkBattleURI uri))
                {
                    ReportBattleDiagnostic(
                        "Authority replay was dropped because the native battle " +
                        "manager or URI was unavailable (uri=" + replayUri +
                        ", requestId=" + (replayRequestId ?? "?") + ").");
                    if (Role == P2PRole.Guest &&
                        string.Equals(replayRequestId, guestAuthorityRequestId,
                            StringComparison.Ordinal))
                    {
                        receivedAuthorityRequestId = replayRequestId;
                    }
                    CompleteGuestAuthorityRequestIfMatching(replayUri, true);
                    result = true;
                    return true;
                }

                if (!TryAcceptAuthorityReplayBoundary(
                        replayUri, replayRequestId, GetAuthorityActionId(data),
                        out _))
                {
                    // A delayed or duplicate authority frame must be consumed
                    // at the transport boundary. Feeding it to the native
                    // receiver a second time can execute an action twice and
                    // strand the Guest in the turn-transition gate.
                    result = true;
                    return true;
                }
                // The normal RealTimeNetworkBattleAgent path calls
                // SetNetworkInfo before NetworkBattleReceiver.ReceivedMessage.
                // Authority replay bypasses that virtual method, so preserve
                // the native agent/network state update (especially turnState,
                // bid, and battle-start metadata) explicitly.
                // RealTimeNetworkAgent.PlayReceiveData first dispatches the
                // raw packet to OnReceivedEvent.  Keep that callback in the
                // authority path as well; room/player controllers use it for
                // receive sequence bookkeeping and lifecycle transitions.
                agent.OnReceivedEvent?.Invoke(data);
                NetworkBattleDefine.NetworkBattleURI networkUri = uri;
                agent.SetNetworkInfo(data, ref networkUri);
                authorityLocalReplayActive = true;
                PrepareAuthorityFusionMetamorphoseReplayData(data);
                result = manager.GetNetworkBattleReceiver().ReceivedMessage(
                    uri,
                    true,
                    data,
                    true,
                    null,
                    true);
                if (!result)
                {
                    ReportBattleDiagnostic(
                        "Authority replay was rejected by the native receiver: " +
                        "uri=" + replayUri + ", requestId=" +
                        (replayRequestId ?? "?") + ".");
                    // PrepareNativeReceivedActionMetadata has already bound
                    // the request ID when conversion reached the receiver.
                    // Force the Guest gate open so a malformed frame cannot
                    // leave the match permanently waiting for a result.
                    if (Role == P2PRole.Guest &&
                        string.Equals(replayRequestId, guestAuthorityRequestId,
                            StringComparison.Ordinal) &&
                        string.IsNullOrEmpty(receivedAuthorityRequestId))
                    {
                        receivedAuthorityRequestId = replayRequestId;
                    }
                    CompleteGuestAuthorityRequestIfMatching(replayUri, true);
                }
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError("[P2P] Authority local replay failed: " + ex);

                // The Harmony prefix consumes the native PlayReceiveData call.
                // If anything fails before (or outside) ReceivedMessage's own
                // finalizer, returning false would make the realtime agent keep
                // the same playSeq pending and leave the Guest input gate locked
                // forever.  Treat the frame as consumed after recording the
                // failure, and explicitly run the same cleanup path used for a
                // rejected native receive.
                try
                {
                    if (nativeReceivedMetadataActive)
                    {
                        CompleteNativeReceivedActionMetadata(data, false);
                    }
                }
                catch (Exception cleanupException)
                {
                    Plugin.Logger.LogDebug(
                        "[P2P] Authority replay cleanup failed: " +
                        cleanupException.Message);
                }

                authorityLocalReplayActive = false;
                localActionCaptureActive = false;
                currentAuthorityReplayData = null;
                currentInjectedAuthoritativeSkillTargetBatch = null;
                currentInjectedAuthoritativeSkillEvaluationBatch = null;
                processingReceivedBattleAction = false;
                receivedBattleActionInjectionPending = false;
                receivedBattleActionPendingUntilVfx = false;
                receivedBattleActionOperationStarted = false;
                receivedBattleActionStartedUtc = DateTime.MinValue;
                receivedBattleActionStallReported = false;
                activeReceivedBattleActionUri = null;
                localActionPreHistoryState = null;
                localActionPreHistoryRevision = 0;
                if (Role == P2PRole.Guest &&
                    string.Equals(replayRequestId, guestAuthorityRequestId,
                        StringComparison.Ordinal))
                {
                    receivedAuthorityRequestId = replayRequestId;
                    CompleteGuestAuthorityRequestIfMatching(replayUri, true);
                }

                // The packet has been handled (and diagnosed), so allow the
                // outer realtime-agent sequence bookkeeping to advance.
                result = true;
                return true;
            }
            finally
            {
                // Keep the replay context alive until the native VFX delegate
                // executes. NetworkOperationCollection schedules Play/Fusion
                // calls for a later frame, so clearing it at ReceivedMessage
                // return would route the action back to BattleEnemy.
                if (!result)
                {
                    authorityLocalReplayActive = false;
                }
            }
        }

        private static bool TryAcceptAuthorityReplayBoundary(
            string uri,
            string requestId,
            string actionId,
            out string boundaryKey)
        {
            boundaryKey = null;
            if (string.IsNullOrEmpty(requestId))
            {
                // Host-originated legacy messages do not carry an authority
                // request ID and remain on the original receive path.
                return true;
            }

            boundaryKey = !string.IsNullOrEmpty(actionId) &&
                !string.Equals(actionId, "<missing>",
                    StringComparison.Ordinal)
                ? "action:" + actionId
                : requestId + "|" + (uri ?? "?");
            if (appliedAuthorityResultBoundaries.Contains(boundaryKey))
            {
                Plugin.Logger.LogWarning(
                    "[P2P] Ignoring duplicate authority result boundary: " +
                    boundaryKey + ".");
                return false;
            }

            // Host-generated turn transitions use a separate ID namespace and
            // are valid even while the Guest has no pending input request.
            bool isHostTransition = requestId.IndexOf(
                "-host-turn-", StringComparison.Ordinal) >= 0;
            if (!isHostTransition &&
                (!guestAuthorityBusy ||
                 !string.Equals(requestId, guestAuthorityRequestId,
                     StringComparison.Ordinal)))
            {
                if (!completedAuthorityRequestIds.Contains(requestId))
                {
                    Plugin.Logger.LogWarning(
                        "[P2P] Ignoring stale authority result " +
                        requestId + " for " + (uri ?? "?") +
                        "; no matching Guest request is pending.");
                }
                completedAuthorityRequestIds.Add(requestId);
                TrimAuthorityResultHistory();
                return false;
            }

            appliedAuthorityResultBoundaries.Add(boundaryKey);
            TrimAuthorityResultHistory();
            return true;
        }

        private static void TrimAuthorityResultHistory()
        {
            const int maximumRequestIds = 512;
            const int maximumBoundaries = 1024;
            if (completedAuthorityRequestIds.Count > maximumRequestIds)
            {
                // Request IDs are monotonic within a battle. Retaining the
                // newest half is unnecessary complexity for a tiny dedupe
                // cache, so clear once the bound is reached.
                completedAuthorityRequestIds.Clear();
            }
            if (appliedAuthorityResultBoundaries.Count > maximumBoundaries)
            {
                appliedAuthorityResultBoundaries.Clear();
            }
        }

        private static void PrepareAuthorityFusionMetamorphoseReplayData(
            Dictionary<string, object> data)
        {
            if (data == null ||
                !data.TryGetValue(
                    P2PBattleProtocol.FusionMetamorphoseOriginalsKey,
                    out object rawEntries) ||
                rawEntries is string || !(rawEntries is IEnumerable entries))
            {
                return;
            }

            List<object> knownList = GetOrCreateKnownList(data);
            List<object> prepend = new List<object>();
            foreach (object rawEntry in entries)
            {
                if (!(rawEntry is Dictionary<string, object> entry) ||
                    !TryGetStateInt(entry, "owner", out int owner) ||
                    (owner != 0 && owner != 1) ||
                    !TryGetStateInt(entry, "idx", out int index) ||
                    index <= 0 ||
                    !TryGetStateInt(entry, "cardId", out int cardId) ||
                    cardId <= 0)
                {
                    continue;
                }

                // Authority owner values are absolute (Host=1, Guest=0).
                // Convert to the current receiver's native isSelf convention;
                // both Host and Guest use the same original knownList schema.
                int localOwner = Role == P2PRole.Host ? 1 : 0;
                int relativeSelf = owner == localOwner ? 1 : 0;

                // Native SendCardDataMaker is allowed to compact several
                // cards into one knownList entry by writing idxList.  A
                // metamorphose replay, however, needs one scalar idx entry for
                // the card that is about to be replaced.  If we simply append
                // that entry while leaving the compacted group intact,
                // ReplaceReceivedCard.SearchForDummyCardInHandAndDeck sees
                // the same index twice and its SingleOrDefault throws.  Split
                // the target index out of every matching idxList group and
                // remove duplicate scalar entries before inserting the one
                // canonical original-card entry.
                Dictionary<string, object> existing = null;
                List<Dictionary<string, object>> matching = knownList
                    .OfType<Dictionary<string, object>>()
                    .Where(known =>
                        IsSelfKnownCard(known) == (relativeSelf != 0) &&
                        KnownCardContainsIndex(known, index))
                    .ToList();
                foreach (Dictionary<string, object> known in matching)
                {
                    if (TryGetStateInt(known, "idx", out int scalarIndex) &&
                        scalarIndex == index)
                    {
                        if (existing == null)
                        {
                            existing = known;
                        }
                        else
                        {
                            knownList.Remove(known);
                        }
                        continue;
                    }

                    if (!known.TryGetValue("idxList", out object rawIndices) ||
                        rawIndices is string || !(rawIndices is IEnumerable indices))
                    {
                        continue;
                    }

                    List<object> remaining = new List<object>();
                    foreach (object rawIndex in indices)
                    {
                        if (!TryConvertAuthorityInt(rawIndex, out int groupedIndex) ||
                            groupedIndex != index)
                        {
                            remaining.Add(rawIndex);
                        }
                    }
                    if (remaining.Count == 0)
                    {
                        knownList.Remove(known);
                    }
                    else
                    {
                        known["idxList"] = remaining;
                    }
                }
                if (existing == null)
                {
                    existing = new Dictionary<string, object>
                    {
                        ["idx"] = index,
                        ["cardId"] = cardId,
                        ["isSelf"] = relativeSelf,
                        ["is_open"] = 1
                    };
                    if (TryGetStateInt(entry, "cost", out int cost) && cost >= 0)
                    {
                        existing["cost"] = cost;
                    }
                    prepend.Add(existing);
                    continue;
                }

                existing["cardId"] = cardId;
                existing["is_open"] = 1;
                if (TryGetStateInt(entry, "cost", out int existingCost) &&
                    existingCost >= 0)
                {
                    existing["cost"] = existingCost;
                }
                int existingIndex = knownList.IndexOf(existing);
                if (existingIndex > 0)
                {
                    knownList.RemoveAt(existingIndex);
                    prepend.Add(existing);
                }
            }

            if (prepend.Count > 0)
            {
                for (int i = prepend.Count - 1; i >= 0; i--)
                {
                    knownList.Insert(0, prepend[i]);
                }
            }
        }

        internal static bool TryProcessAuthorityTurnStart(
            NetworkOperationCollection operation)
        {
            if (!IsHostAuthorityMode || !authorityLocalReplayActive ||
                Role != P2PRole.Guest || operation == null ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.networkBattleData?.GetReceiveData() == null)
            {
                return false;
            }

            NetworkBattleReceiver.ReceiveData receiveData =
                manager.networkBattleData.GetReceiveData();
            if (!receiveData.dataUri.Equals(
                    NetworkBattleDefine.NetworkBattleURI.TurnStart))
            {
                return false;
            }

            Dictionary<string, object> rawData = currentAuthorityReplayData;
            if (rawData == null ||
                !TryGetStateInt(rawData,
                    P2PBattleProtocol.AuthorityTurnOwnerKey,
                    out int turnOwner) ||
                (turnOwner != 0 && turnOwner != 1))
            {
                return false;
            }

            bool localOwnerIsHost = Role == P2PRole.Host;
            bool localTurn = turnOwner == (localOwnerIsHost ? 1 : 0);
            int extraValue = 0;
            bool extraTurn = rawData.TryGetValue(
                    P2PBattleProtocol.AuthorityTurnExtraKey,
                    out object rawExtra) &&
                TryConvertAuthorityInt(rawExtra, out extraValue) &&
                extraValue != 0;

            try
            {
                BattlePlayerBase player = localTurn
                    ? manager.BattlePlayer
                    : manager.BattleEnemy;
                if (player == null)
                {
                    return false;
                }

                Wizard.Battle.View.Vfx.VfxBase vfx =
                    player.StartTurnControl(extraTurn ? "ExtraTurn" : "Normal");
                // ControlTurnStart on the Host consumes the selected player's
                // extra-turn counter immediately after constructing this VFX.
                // StartTurnControl itself does not decrement that counter, so
                // mirror the native operation exactly on the Guest.  Without
                // this, a replayed ExtraTurn remains queued locally and the
                // next transition can select the wrong owner.
                player.DecreasesExtraTurnCount();
                manager.VfxMgr.RegisterSequentialVfx<
                    Wizard.Battle.View.Vfx.VfxBase>(vfx);
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    "[P2P] Authority TurnStart replay failed: " + ex);
                return true;
            }
        }

        private static void RemovePrivateTwoPickDraftData(
            Dictionary<string, object> data,
            string uri)
        {
            if (!IsTwoPickRoom ||
                !TryGetRoomUri(data, uri, out PlayerController.ROOM_URI roomUri))
            {
                return;
            }

            switch (roomUri)
            {
                case PlayerController.ROOM_URI.BeginCreateDeck:
                    data.Remove("candidateClassIds");
                    break;
                case PlayerController.ROOM_URI.SelectClass:
                    data.Remove("classInfo");
                    data.Remove("candidateCardList");
                    break;
                case PlayerController.ROOM_URI.SelectCardSet:
                    data.Remove("deckInfo");
                    data.Remove("candidateCardList");
                    break;
            }
        }

        private static void HandleRoomEmit(
            bool sourceIsHost,
            PlayerController.ROOM_URI roomUri,
            Dictionary<string, object> data)
        {
            switch (roomUri)
            {
                case PlayerController.ROOM_URI.Reenter:
                    RoomRoundState.Reenter(sourceIsHost);
                    if (sourceIsHost)
                    {
                        hostPlaySequence = 0;
                    }
                    else
                    {
                        guestPlaySequence = 0;
                        GuestDeliverySequence.Reset();
                        GuestDeliverySequence.Open();
                        DeferredGuestDeliveries.Clear();
                    }
                    return;
                case PlayerController.ROOM_URI.RoomCreate:
                    Deliver(true, new Dictionary<string, object>
                    {
                        ["uri"] = roomUri.ToString(),
                        ["resultCode"] = 1,
                        ["isSelf"] = 1
                    }, LocalProfile.ViewerId);
                    return;
                case PlayerController.ROOM_URI.RoomEntry:
                    if (sourceIsHost || RemoteProfile == null)
                    {
                        return;
                    }
                    bool openedGuestDelivery = GuestDeliverySequence.Open();
                    if (openedGuestDelivery)
                    {
                        guestPlaySequence = 0;
                    }
                    Dictionary<string, object> guestAck = CreateRoomPlayerData(LocalProfile);
                    guestAck["uri"] = roomUri.ToString();
                    guestAck["resultCode"] = 1;
                    guestAck["isSelf"] = 1;
                    Deliver(false, guestAck, RemoteProfile.ViewerId);
                    if (openedGuestDelivery)
                    {
                        FlushDeferredGuestDeliveries();
                    }

                    Dictionary<string, object> hostEntry = CreateRoomPlayerData(RemoteProfile);
                    hostEntry["uri"] = roomUri.ToString();
                    hostEntry["resultCode"] = 1;
                    hostEntry["isSelf"] = 0;
                    Deliver(true, hostEntry, RemoteProfile.ViewerId);
                    Plugin.Logger.LogInfo("[P2P] RoomEntry delivered to both players.");
                    return;
                case PlayerController.ROOM_URI.SetupComplete:
                    bool shouldStartBattle = RoomRoundState.MarkReady(sourceIsHost);
                    Plugin.Logger.LogInfo(
                        $"[P2P] {SideName(sourceIsHost)} marked ready in the room " +
                        $"(host={RoomRoundState.HostReady}, guest={RoomRoundState.GuestReady}, " +
                        $"start={shouldStartBattle}).");
                    EchoRoomToBoth(sourceIsHost, data);
                    if (shouldStartBattle)
                    {
                        ResetBattleState();
                        Dictionary<string, object> ready = new Dictionary<string, object>
                        {
                            ["uri"] = PlayerController.ROOM_URI.RoomReady.ToString()
                        };
                        Deliver(true, ready, 0);
                        Deliver(false, ready, 0);
                        Plugin.Logger.LogInfo(
                            "[P2P] Both players are ready; starting battle matching.");
                    }
                    return;
                case PlayerController.ROOM_URI.SetupCancel:
                    RoomRoundState.CancelReady(sourceIsHost);
                    EchoRoomToBoth(sourceIsHost, data);
                    return;
                case PlayerController.ROOM_URI.Leave:
                case PlayerController.ROOM_URI.Release:
                case PlayerController.ROOM_URI.Kick:
                    EchoRoomToBoth(sourceIsHost, data);
                    return;
                case PlayerController.ROOM_URI.DeckEntry:
                    HandleDeckEntry(sourceIsHost, data);
                    return;
                case PlayerController.ROOM_URI.DeckSelect:
                case PlayerController.ROOM_URI.DeckConfirm:
                case PlayerController.ROOM_URI.ChatStamp:
                case PlayerController.ROOM_URI.RoomNotify:
                case PlayerController.ROOM_URI.TurnSelect:
                    Deliver(!sourceIsHost, data, SourceViewerId(sourceIsHost), true);
                    return;
                default:
                    Deliver(!sourceIsHost, data, SourceViewerId(sourceIsHost), true);
                    return;
            }
        }

        private static void HandleDeckEntry(
            bool sourceIsHost,
            Dictionary<string, object> data)
        {
            Dictionary<string, object> cached = P2PJson.CloneDictionary(data);
            if (sourceIsHost)
            {
                hostDeckEntry = cached;
            }
            else
            {
                guestDeckEntry = cached;
            }

            Deliver(!sourceIsHost, cached, SourceViewerId(sourceIsHost), true);
            Plugin.Logger.LogInfo(
                $"[P2P] {SideName(sourceIsHost)} submitted open-deck data " +
                $"(hostCached={hostDeckEntry != null}, guestCached={guestDeckEntry != null}).");

            // A deck entry can arrive while the peer's room listener is still starting or
            // while its deck dialog blocks node messages. Replaying the cached peer entry
            // after this side submits proves both UIs have reached the deck-exchange stage.
            Dictionary<string, object> peerEntry = sourceIsHost
                ? guestDeckEntry
                : hostDeckEntry;
            if (peerEntry == null)
            {
                return;
            }

            bool peerIsHost = !sourceIsHost;
            Deliver(
                sourceIsHost,
                peerEntry,
                SourceViewerId(peerIsHost),
                true);
            Plugin.Logger.LogInfo(
                $"[P2P] Replayed {SideName(peerIsHost)} open-deck data to " +
                $"{SideName(sourceIsHost)} after deck submission.");
        }

        private static bool TryGetRoomUri(
            Dictionary<string, object> data,
            string uri,
            out PlayerController.ROOM_URI roomUri)
        {
            roomUri = default;
            if (!data.TryGetValue("cat", out object category))
            {
                return false;
            }
            try
            {
                return Convert.ToInt32(category) ==
                        (int)RealTimeNetworkAgent.EmitCategory.room &&
                    Enum.TryParse(uri, out roomUri);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void EchoRoomToBoth(bool sourceIsHost, Dictionary<string, object> data)
        {
            Dictionary<string, object> hostData = sourceIsHost
                ? P2PJson.CloneDictionary(data)
                : P2PMessageTransform.FlipPerspective(data);
            Dictionary<string, object> guestData = sourceIsHost
                ? P2PMessageTransform.FlipPerspective(data)
                : P2PJson.CloneDictionary(data);
            hostData["isSelf"] = sourceIsHost ? 1 : 0;
            guestData["isSelf"] = sourceIsHost ? 0 : 1;
            hostData["resultCode"] =
                (int)NetworkBattleDefine.ReceiveNodeResultCode.Success;
            guestData["resultCode"] =
                (int)NetworkBattleDefine.ReceiveNodeResultCode.Success;
            int viewerId = SourceViewerId(sourceIsHost);
            Deliver(true, hostData, viewerId);
            Deliver(false, guestData, viewerId);
        }

        private static void TrySendMatched()
        {
            if (matchedSent || !hostInitBattle || !guestInitBattle ||
                LocalDeck == null || RemoteDeck == null)
            {
                return;
            }
            if (LocalDeck.Cards.Count == 0 || RemoteDeck.Cards.Count == 0)
            {
                LastError = "Both players must select a non-empty deck.";
                return;
            }

            shuffledHostDeck = Shuffle(LocalDeck.Cards);
            shuffledGuestDeck = Shuffle(RemoteDeck.Cards);
            BattleCardTracker.Reset(shuffledHostDeck, shuffledGuestDeck);
            battleSeed = CreatePositiveInt();
            DealState.Initialize(CreatePositiveInt(), CreatePositiveInt());
            hostFirst = (CreatePositiveInt() & 1) == 0;
            matchedSent = true;

            Deliver(true, CreateMatchedData(true), 0);
            Deliver(false, CreateMatchedData(false), 0);
            Plugin.Logger.LogInfo(
                $"[P2P] Matched delivered; {(hostFirst ? "Host" : "Guest")} goes first.");
        }

        private static Dictionary<string, object> CreateMatchedData(bool forHost)
        {
            P2PProfile selfProfile = forHost ? LocalProfile : RemoteProfile;
            P2PProfile oppoProfile = forHost ? RemoteProfile : LocalProfile;
            P2PDeckSnapshot selfDeck = forHost ? LocalDeck : RemoteDeck;
            P2PDeckSnapshot oppoDeck = forHost ? RemoteDeck : LocalDeck;
            List<int> cards = forHost ? shuffledHostDeck : shuffledGuestDeck;
            List<int> opponentCards = forHost ? shuffledGuestDeck : shuffledHostDeck;
            List<object> deckData = new List<object>(cards.Count);
            for (int i = 0; i < cards.Count; i++)
            {
                deckData.Add(new Dictionary<string, object>
                {
                    ["idx"] = i + 1,
                    ["cardId"] = cards[i]
                });
            }
            return new Dictionary<string, object>
            {
                ["uri"] = NetworkBattleDefine.NetworkBattleURI.Matched.ToString(),
                ["bid"] = BattleId,
                ["turnState"] = (forHost == hostFirst) ? 0 : 1,
                ["selfInfo"] = CreateBattleInfo(selfProfile, selfDeck, oppoProfile, oppoDeck,
                    battleSeed),
                ["oppoInfo"] = CreateBattleInfo(oppoProfile, oppoDeck, selfProfile, selfDeck,
                    battleSeed),
                ["selfDeck"] = deckData,
                [P2PBattleProtocol.OpponentDeckIdentityKey] =
                    P2PBattleProtocol.CreateDeckIdentityPayload(opponentCards)
            };
        }

        internal static void ApplyOpponentDeckIdentity(
            Dictionary<string, object> synchronizeData)
        {
            if (!IsActive || synchronizeData == null ||
                !synchronizeData.TryGetValue("uri", out object rawUri) ||
                (!string.Equals(
                    rawUri?.ToString(),
                    NetworkBattleDefine.NetworkBattleURI.Matched.ToString(),
                    StringComparison.Ordinal) &&
                 !string.Equals(
                     rawUri?.ToString(),
                     NetworkBattleDefine.NetworkBattleURI.BattleStart.ToString(),
                     StringComparison.Ordinal)))
            {
                return;
            }

            int expectedCount = 0;
            if (synchronizeData.TryGetValue("oppoInfo", out object rawOpponentInfo) &&
                rawOpponentInfo is Dictionary<string, object> opponentInfo &&
                opponentInfo.TryGetValue("deckCount", out object rawDeckCount))
            {
                try
                {
                    expectedCount = Convert.ToInt32(rawDeckCount);
                }
                catch (Exception)
                {
                    expectedCount = 0;
                }
            }
            if (expectedCount <= 0)
            {
                P2PDeckSnapshot expectedDeck = Role == P2PRole.Host
                    ? RemoteDeck
                    : LocalDeck;
                expectedCount = expectedDeck?.Cards?.Count ?? 0;
            }
            if (!P2PBattleProtocol.TryReadDeckIdentityPayload(
                    synchronizeData,
                    expectedCount,
                    out List<object> deckData,
                    out string error))
            {
                Plugin.Logger.LogError(
                    "[P2P] Opponent deck identity was rejected: " + error + ".");
                return;
            }

            try
            {
                GameMgr game = GameMgr.GetIns();
                NetworkUserInfoData networkInfo =
                    game?.GetNetworkUserInfoData();
                if (networkInfo == null)
                {
                    Plugin.Logger.LogError(
                        "[P2P] Could not install opponent deck identity: " +
                        "NetworkUserInfoData was unavailable.");
                    return;
                }
                // SetOppoDeck uses DataMgr as its size fallback while the Matched
                // callback has not populated oppoInfo yet.
                game.GetDataMgr()?.SetDeckMaxCount(expectedCount, false);
                networkInfo.SetOppoDeck(deckData);
                Plugin.Logger.LogInfo(
                    $"[P2P] Installed {deckData.Count}-card opponent deck identity " +
                    "before battle load.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    "[P2P] Failed to install opponent deck identity: " + ex);
            }
        }

        private static void TrySendBattleStart()
        {
            if (battleStartSent || !hostLoaded || !guestLoaded || !matchedSent)
            {
                return;
            }
            battleStartSent = true;
            Deliver(true, CreateBattleStartData(true), 0);
            Deliver(false, CreateBattleStartData(false), 0);
            Plugin.Logger.LogInfo("[P2P] BattleStart delivered to both players.");
        }

        private static Dictionary<string, object> CreateBattleStartData(bool forHost)
        {
            P2PProfile selfProfile = forHost ? LocalProfile : RemoteProfile;
            P2PProfile oppoProfile = forHost ? RemoteProfile : LocalProfile;
            P2PDeckSnapshot selfDeck = forHost ? LocalDeck : RemoteDeck;
            P2PDeckSnapshot oppoDeck = forHost ? RemoteDeck : LocalDeck;
            List<int> opponentCards = forHost ? shuffledGuestDeck : shuffledHostDeck;
            return new Dictionary<string, object>
            {
                ["uri"] = NetworkBattleDefine.NetworkBattleURI.BattleStart.ToString(),
                ["bid"] = BattleId,
                ["battleStartDate"] = UnixMilliseconds(),
                ["selfInfo"] = CreateBattleInfo(selfProfile, selfDeck, oppoProfile, oppoDeck,
                    battleSeed),
                ["oppoInfo"] = CreateBattleInfo(oppoProfile, oppoDeck, selfProfile, selfDeck,
                    battleSeed),
                [P2PBattleProtocol.OpponentDeckIdentityKey] =
                    P2PBattleProtocol.CreateDeckIdentityPayload(opponentCards)
            };
        }

        private static Dictionary<string, object> CreateBattleInfo(
            P2PProfile profile,
            P2PDeckSnapshot deck,
            P2PProfile opponent,
            P2PDeckSnapshot opponentDeck,
            int seed)
        {
            return new Dictionary<string, object>
            {
                ["viewerId"] = profile.ViewerId,
                ["oppoId"] = opponent.ViewerId,
                ["userName"] = profile.UserName ?? string.Empty,
                ["rank"] = profile.Rank,
                ["battlePoint"] = profile.BattlePoint,
                ["masterPoint"] = profile.MasterPoint,
                ["isMasterRank"] = profile.MasterPoint > 0 ? 1 : 0,
                ["classId"] = deck.ClassId,
                ["subclassId"] = deck.SubclassId,
                ["charaId"] = deck.CharaId,
                ["sleeveId"] = deck.SleeveId,
                ["emblemId"] = profile.EmblemId,
                ["degreeId"] = profile.DegreeId,
                ["country_code"] = profile.CountryCode ?? string.Empty,
                ["isOfficial"] = profile.IsOfficial,
                ["fieldId"] = 1,
                ["seed"] = seed,
                ["deckCount"] = deck.Cards.Count,
                ["oppoDeckCount"] = opponentDeck.Cards.Count
            };
        }

        private static void SendDeal(bool toHost)
        {
            if (!matchedSent || !DealState.TryClaim(
                    toHost,
                    out int idxChangeSeed,
                    out int opponentIdxChangeSeed))
            {
                return;
            }
            if (hostMulliganHand == null) hostMulliganHand = new List<int> { 1, 2, 3 };
            if (guestMulliganHand == null) guestMulliganHand = new List<int> { 1, 2, 3 };
            List<int> self = toHost ? hostMulliganHand : guestMulliganHand;
            List<int> oppo = toHost ? guestMulliganHand : hostMulliganHand;
            P2PAuthoritativeServer.ObserveDeal(
                hostMulliganHand, guestMulliganHand);
            Deliver(toHost, new Dictionary<string, object>
            {
                ["uri"] = NetworkBattleDefine.NetworkBattleURI.Deal.ToString(),
                ["idxChangeSeed"] = idxChangeSeed,
                ["oppoIdxChangeSeed"] = opponentIdxChangeSeed,
                ["self"] = CreateIndexList(self),
                ["oppo"] = CreateIndexList(oppo)
            }, 0);
            Plugin.Logger.LogInfo($"[P2P] Deal delivered to {SideName(toHost)}.");
        }

        private static void HandleSwap(bool sourceIsHost, Dictionary<string, object> data)
        {
            List<int> hand = sourceIsHost
                ? hostMulliganHand ?? new List<int> { 1, 2, 3 }
                : guestMulliganHand ?? new List<int> { 1, 2, 3 };
            List<int> selected = ToIntList(data.TryGetValue("idxList", out object value) ? value : null);
            int replacement = 4;
            foreach (int selectedIndex in selected)
            {
                int position = hand.IndexOf(selectedIndex);
                if (position >= 0)
                {
                    hand[position] = replacement++;
                }
            }
            if (sourceIsHost)
            {
                hostMulliganHand = hand;
                hostSwapped = true;
            }
            else
            {
                guestMulliganHand = hand;
                guestSwapped = true;
            }
            P2PAuthoritativeServer.ObserveMulligan(
                sourceIsHost, selected, hand);
            Plugin.Logger.LogInfo(
                $"[P2P] {SideName(sourceIsHost)} completed mulligan selection.");
            Deliver(sourceIsHost, new Dictionary<string, object>
            {
                ["uri"] = NetworkBattleDefine.NetworkBattleURI.Swap.ToString(),
                ["self"] = CreateIndexList(hand)
            }, SourceViewerId(sourceIsHost));

            if (hostSwapped && guestSwapped && !mulliganReadySent)
            {
                mulliganReadySent = true;
                Deliver(true, new Dictionary<string, object>
                {
                    ["uri"] = NetworkBattleDefine.NetworkBattleURI.Ready.ToString(),
                    ["self"] = CreateIndexList(hostMulliganHand),
                    ["oppo"] = CreateIndexList(guestMulliganHand)
                }, 0);
                Deliver(false, new Dictionary<string, object>
                {
                    ["uri"] = NetworkBattleDefine.NetworkBattleURI.Ready.ToString(),
                    ["self"] = CreateIndexList(guestMulliganHand),
                    ["oppo"] = CreateIndexList(hostMulliganHand)
                }, 0);
                Plugin.Logger.LogInfo("[P2P] Mulligan Ready delivered to both players.");
            }
        }

        private static void SendFinishResult(
            bool sourceIsHost,
            Dictionary<string, object> request)
        {
            if (finishResultSent)
            {
                return;
            }
            P2PBattleResultPair results;
            int authoritativeLocalResult;
            bool authoritativeSideIsHost;
            string authority;
            if (retiringHost.HasValue)
            {
                authoritativeSideIsHost = retiringHost.Value;
                authoritativeLocalResult = P2PBattleResult.RetireLose;
                authority = "retirement";
            }
            else if ((!IsHostAuthorityMode || sourceIsHost) &&
                TryReadReportedLocalResult(request, out int reportedLocalResult))
            {
                authoritativeSideIsHost = sourceIsHost;
                authoritativeLocalResult = reportedLocalResult;
                authority = SideName(sourceIsHost) + " report";
            }
            else
            {
                authoritativeSideIsHost = true;
                authoritativeLocalResult = GetLocalFinishResult();
                authority = IsHostAuthorityMode && !sourceIsHost
                    ? "Host authority"
                    : "Host fallback";
            }

            if (retiringHost.HasValue)
            {
                results = P2PBattleResult.FromRetirement(
                    retiringHost.Value);
            }
            else if (!P2PBattleResult.TryCreateResultPair(
                         authoritativeSideIsHost,
                         authoritativeLocalResult,
                         out results))
            {
                Deliver(sourceIsHost, new Dictionary<string, object>
                {
                    ["uri"] = NetworkBattleDefine.NetworkBattleURI.JudgeResult.ToString(),
                    ["result"] = (int)NetworkBattleReceiver.RESULT_CODE.NotFinish
                }, 0);
                Plugin.Logger.LogInfo(
                    $"[P2P] Battle result is not final yet ({authority}=" +
                    $"{authoritativeLocalResult}); requested a retry.");
                return;
            }

            finishResultSent = true;
            Dictionary<string, object> hostResult = new Dictionary<string, object>
            {
                ["uri"] = NetworkBattleDefine.NetworkBattleURI.JudgeResult.ToString(),
                ["result"] = results.Host
            };
            Dictionary<string, object> guestResult = new Dictionary<string, object>
            {
                ["uri"] = NetworkBattleDefine.NetworkBattleURI.JudgeResult.ToString(),
                ["result"] = results.Guest
            };
            if (request != null && request.TryGetValue(
                    P2PBattleProtocol.AuthorityResultRequestIdKey,
                    out object rawRequestId) && rawRequestId != null)
            {
                string requestId = rawRequestId.ToString();
                if (!string.IsNullOrEmpty(requestId))
                {
                    hostResult[P2PBattleProtocol.AuthorityResultRequestIdKey] =
                        requestId;
                    guestResult[P2PBattleProtocol.AuthorityResultRequestIdKey] =
                        requestId;
                }
            }
            Deliver(true, hostResult, 0);
            Deliver(false, guestResult, 0);
            Plugin.Logger.LogInfo(
                $"[P2P] Battle result delivered from {authority} " +
                $"({authoritativeLocalResult}): host receives local result " +
                $"{results.Host}, guest receives local result {results.Guest}.");
        }

        private static bool TryReadReportedLocalResult(
            Dictionary<string, object> request,
            out int result)
        {
            result = 0;
            if (request == null ||
                !request.TryGetValue("p2pLocalResult", out object value))
            {
                return false;
            }

            try
            {
                result = Convert.ToInt32(value);
                return true;
            }
            catch (Exception)
            {
                result = 0;
                return false;
            }
        }

        private static int GetLocalFinishResult()
        {
            if (!matchedSent || !battleStartReceived ||
                !mulliganReadyReceived)
            {
                return (int)NetworkBattleReceiver.RESULT_CODE.NotFinish;
            }
            NetworkBattleManagerBase manager =
                BattleManagerBase.GetIns() as NetworkBattleManagerBase;
            return manager == null || manager.BattlePlayer?.Class == null ||
                manager.BattleEnemy?.Class == null
                ? (int)NetworkBattleReceiver.RESULT_CODE.NotFinish
                : (int)manager.JudgeCurrentFinishStatus();
        }

        private static void Deliver(
            bool toHost,
            Dictionary<string, object> source,
            int viewerId,
            bool flipPerspective = false)
        {
            Dictionary<string, object> data = flipPerspective
                ? P2PMessageTransform.FlipPerspective(source)
                : P2PJson.CloneDictionary(source);
            data.Remove("pubSeq");
            data.Remove("cat");
            data.Remove("try");
            data["viewerId"] = viewerId;
            data["bid"] = BattleId ?? string.Empty;
            if (!toHost && Role == P2PRole.Host && !GuestDeliverySequence.IsOpen)
            {
                if (RemoteProfile != null)
                {
                    DeferredGuestDeliveries.Enqueue(data);
                    Plugin.Logger.LogInfo(
                        $"[P2P] Deferred '{GetUri(data)}' until the guest sends RoomEntry " +
                        $"(queued={DeferredGuestDeliveries.Count}).");
                }
                return;
            }

            int playSequence;
            if (toHost)
            {
                playSequence = ++hostPlaySequence;
            }
            else if (Role == P2PRole.Host)
            {
                if (!GuestDeliverySequence.TryNext(out playSequence))
                {
                    Plugin.Logger.LogError(
                        "[P2P] Dropped Host->Guest delivery because the guest " +
                        "battle stream is not open: uri=" + GetUri(data) + ".");
                    return;
                }
                guestPlaySequence = playSequence;
            }
            else
            {
                playSequence = ++guestPlaySequence;
            }
            data["playSeq"] = playSequence;
            data["time"] = UnixMilliseconds();
            if (toHost)
            {
                Enqueue(() => Inject(data));
            }
            else
            {
                if (!SendWire(new P2PWireMessage
                {
                    Type = "deliver",
                    ViewerId = viewerId,
                    BattleId = BattleId,
                    Data = data
                }))
                {
                    Plugin.Logger.LogError(
                        "[P2P] Failed to deliver Host->Guest message: uri=" +
                        GetUri(data) + ", playSeq=" + playSequence + ".");
                }
            }
        }

        private static void FlushDeferredGuestDeliveries()
        {
            int deferredCount = DeferredGuestDeliveries.Count;
            Plugin.Logger.LogInfo(
                $"[P2P] Guest room stream opened at playSeq=1; " +
                $"replaying {deferredCount} deferred message(s).");
            while (DeferredGuestDeliveries.Count > 0)
            {
                Dictionary<string, object> data = DeferredGuestDeliveries.Dequeue();
                if (!GuestDeliverySequence.TryNext(out int playSequence))
                {
                    DeferredGuestDeliveries.Clear();
                    return;
                }
                guestPlaySequence = playSequence;
                data["playSeq"] = playSequence;
                data["time"] = UnixMilliseconds();
                SendWire(new P2PWireMessage
                {
                    Type = "deliver",
                    ViewerId = data.TryGetValue("viewerId", out object viewerId)
                        ? Convert.ToInt32(viewerId)
                        : 0,
                    BattleId = BattleId,
                    Data = data
                });
            }
        }

        private static void DeferAgentDelivery(Dictionary<string, object> data)
        {
            string uri = GetUri(data);
            if (DeferredAgentDeliveries.Count >= MaxDeferredAgentDeliveries)
            {
                Plugin.Logger.LogError(
                    $"[P2P] Could not defer '{uri}' because the realtime-agent queue is full.");
                if (Role == P2PRole.Guest && !JoinFinished)
                {
                    FailJoin("Too many room messages arrived before the realtime agent was ready.");
                }
                return;
            }

            DeferredAgentDeliveries.Enqueue(P2PJson.CloneDictionary(data));
            Plugin.Logger.LogInfo(
                $"[P2P] Deferred incoming '{uri}' until the realtime agent is ready " +
                $"(queued={DeferredAgentDeliveries.Count}).");
        }

        private static void FlushDeferredAgentDeliveries()
        {
            if (currentAgent == null || DeferredAgentDeliveries.Count == 0)
            {
                return;
            }

            int count = DeferredAgentDeliveries.Count;
            Plugin.Logger.LogInfo(
                $"[P2P] Replaying {count} message(s) deferred before realtime-agent setup.");
            while (currentAgent != null && DeferredAgentDeliveries.Count > 0)
            {
                Inject(DeferredAgentDeliveries.Dequeue());
            }
        }

        private static string GetUri(Dictionary<string, object> data)
        {
            return data != null && data.TryGetValue("uri", out object uri)
                ? uri?.ToString() ?? "?"
                : "?";
        }

        private static string GetAuthorityActionId(
            Dictionary<string, object> data)
        {
            if (data != null && data.TryGetValue(
                    P2PBattleProtocol.AuthorityResultActionIdKey,
                    out object rawActionId) && rawActionId != null)
            {
                return rawActionId.ToString();
            }

            string requestId = data != null && data.TryGetValue(
                    P2PBattleProtocol.AuthorityResultRequestIdKey,
                    out object rawRequestId)
                ? rawRequestId?.ToString()
                : null;
            return string.IsNullOrEmpty(requestId)
                ? "<missing>"
                : requestId + "|" + GetUri(data);
        }

        private static void Inject(Dictionary<string, object> data)
        {
            string incomingUri = GetUri(data);
            if (IsOrderedReceivedBattleMessage(incomingUri) &&
                ShouldDeferReceivedPlayAction())
            {
                if (PendingReceivedBattleMessages.Count >=
                    MaxPendingReceivedBattleMessages)
                {
                    ReportBattleDiagnostic(
                        $"Received battle-message queue exceeded " +
                        $"{MaxPendingReceivedBattleMessages}; dropping " +
                        $"{incomingUri} playIdx={GetMessagePlayIndex(data)}.");
                    return;
                }

                PendingReceivedBattleMessages.Enqueue(P2PJson.CloneDictionary(data));
                Plugin.Logger.LogDebug(
                    $"[P2P] Deferred incoming {incomingUri} playIdx=" +
                    $"{GetMessagePlayIndex(data)} until the previous battle " +
                    "operation's VFX and state synchronization completed.");
                return;
            }

            InjectNow(data);
        }

        private static void InjectNow(Dictionary<string, object> data)
        {
            string uri = data != null && data.TryGetValue("uri", out object value)
                ? value?.ToString() ?? "?"
                : "?";
            if (string.Equals(uri,
                    PlayerController.ROOM_URI.RoomReady.ToString(),
                    StringComparison.Ordinal))
            {
                // The host resets when both players become ready. The guest must
                // reset at the same boundary so no previous round state can make
                // startup Judge messages appear final.
                ResetBattleState();
            }
            else if (string.Equals(uri,
                    NetworkBattleDefine.NetworkBattleURI.Matched.ToString(),
                    StringComparison.Ordinal))
            {
                matchedSent = true;
            }
            else if (string.Equals(uri,
                    NetworkBattleDefine.NetworkBattleURI.BattleStart.ToString(),
                    StringComparison.Ordinal))
            {
                battleStartReceived = true;
            }
            else if (string.Equals(uri,
                    NetworkBattleDefine.NetworkBattleURI.Ready.ToString(),
                    StringComparison.Ordinal))
            {
                mulliganReadyReceived = true;
            }
            Dictionary<string, object> expectedBattleState = null;
            if (data != null &&
                data.TryGetValue(P2PBattleStateDiagnostics.StateKey, out object rawState))
            {
                expectedBattleState = rawState as Dictionary<string, object>;
                data.Remove(P2PBattleStateDiagnostics.StateKey);
            }
            if (UseNativeClientActionTiming)
            {
                // A native v3 response is already the Host-authoritative
                // boundary. Any legacy full-state checkpoint is stale by
                // definition and must not create a second diagnostic path.
                expectedBattleState = null;
            }
            bool isOrderedReceivedBattleMessage =
                IsOrderedReceivedBattleMessage(uri);
            if (!isOrderedReceivedBattleMessage)
            {
                // Ordered packets may be held by the native playSeq queue. Their
                // action metadata is bound later, in ReceivedMessage, so it cannot
                // be overwritten by another packet while waiting for its turn.
                RememberReceivedHiddenCardStates(data, false);
                TryApplyPendingHiddenCardStates();
            }
            if (string.Equals(
                    uri,
                    NetworkBattleDefine.NetworkBattleURI.JudgeResult.ToString(),
                    StringComparison.Ordinal) &&
                data.TryGetValue("result", out object resultValue))
            {
                try
                {
                    if (P2PBattleResult.IsTerminalResult(
                            Convert.ToInt32(resultValue)))
                    {
                        finishResultSent = true;
                    }
                }
                catch (Exception)
                {
                }
            }
            if (currentAgent == null)
            {
                RealTimeNetworkAgent gameAgent = ToolboxGame.RealTimeNetworkAgent;
                if (gameAgent != null)
                {
                    SetCurrentAgent(gameAgent, "ToolboxGame fallback");
                }
                if (currentAgent == null)
                {
                    Plugin.Logger.LogWarning(
                        $"[P2P] Dropped realtime message '{uri}' because no agent is active " +
                        $"(game singleton available: {gameAgent != null}).");
                    return;
                }
            }
            // Matching starts battle loading from the Matched callback before
            // RealTimeNetworkBattleAgent applies SetNetworkInfo. Install the
            // private opponent deck table first so that callback can construct
            // real opponent cards instead of the all-Dummy fallback.
            if (string.Equals(
                    uri,
                    NetworkBattleDefine.NetworkBattleURI.Matched.ToString(),
                    StringComparison.Ordinal) ||
                string.Equals(
                    uri,
                    NetworkBattleDefine.NetworkBattleURI.BattleStart.ToString(),
                    StringComparison.Ordinal))
            {
                ApplyOpponentDeckIdentity(data);
            }
            PendingBattleStateCheck pendingStateCheck = null;
            if (expectedBattleState != null)
            {
                pendingStateCheck = new PendingBattleStateCheck(
                    uri,
                    P2PJson.CloneDictionary(expectedBattleState),
                    DateTime.UtcNow.AddSeconds(BattleStateCheckTimeoutSeconds),
                    GetAuthorityActionId(data));
                PendingBattleStateChecks.Enqueue(pendingStateCheck);
            }
            if (isOrderedReceivedBattleMessage)
            {
                BeginReceivedBattleActionInjection(uri, data);
            }
            try
            {
                if (uri == PlayerController.ROOM_URI.RoomEntry.ToString())
                {
                    string playSequence = data.TryGetValue("playSeq", out object sequence)
                        ? sequence?.ToString() ?? "?"
                        : "?";
                    Plugin.Logger.LogInfo(
                        $"[P2P] Injecting RoomEntry as {Role}; agent={currentAgent.GetType().Name}, " +
                        $"status={currentAgent.CurrentMatchingStatus}, playSeq={playSequence}.");
                }
                currentAgent.ProcessingRecivedData(data);
            }
            catch (Exception ex)
            {
                if (pendingStateCheck != null)
                {
                    pendingStateCheck.InjectionError =
                        ex.GetType().Name + ": " + ex.Message;
                }
                ReportBattleDiagnostic(
                    $"Failed to inject '{uri}' message; " +
                    P2PBattleStateDiagnostics.DescribeBattleMessage(data) +
                    $". Exception: {ex}");
            }
            finally
            {
                if (nativeReceivedMetadataActive)
                {
                    Plugin.Logger.LogWarning(
                        $"[P2P] Aborting incomplete native receive metadata for " +
                        $"{uri} playIdx={GetMessagePlayIndex(data)}.");
                    CompleteNativeReceivedActionMetadata(data, false);
                }
                currentInjectedAuthoritativeSkillTargetBatch = null;
                currentInjectedAuthoritativeSkillEvaluationBatch = null;
            }
        }

        private static void BeginReceivedBattleActionInjection(
            string uri,
            Dictionary<string, object> data)
        {
            if (receivedBattleActionInjectionPending ||
                receivedBattleActionPendingUntilVfx ||
                processingReceivedBattleAction)
            {
                ReportBattleDiagnostic(
                    "Ordered battle action was injected while another receive " +
                    "boundary was still active: uri=" + uri + ", actionId=" +
                    GetAuthorityActionId(data) + ".");
                return;
            }

            receivedBattleActionInjectionPending = true;
            receivedBattleActionStartedUtc = DateTime.UtcNow;
            receivedBattleActionStallReported = false;
            activeReceivedBattleActionUri = uri;
        }

        private static void ReleaseReservedReceivedBattleActionInjection(
            Dictionary<string, object> data)
        {
            if (!receivedBattleActionInjectionPending ||
                processingReceivedBattleAction ||
                !string.Equals(activeReceivedBattleActionUri, GetUri(data),
                    StringComparison.Ordinal))
            {
                return;
            }

            receivedBattleActionInjectionPending = false;
            receivedBattleActionStartedUtc = DateTime.MinValue;
            receivedBattleActionStallReported = false;
            activeReceivedBattleActionUri = null;
        }

        private static bool ShouldDeferReceivedPlayAction()
        {
            if (!IsActive)
            {
                return false;
            }

            if (receivedBattleActionInjectionPending ||
                receivedBattleActionPendingUntilVfx ||
                processingReceivedBattleAction)
            {
                return true;
            }

            NetworkBattleManagerBase manager =
                BattleManagerBase.GetIns() as NetworkBattleManagerBase;
            return manager?.VfxMgr != null && !manager.VfxMgr.IsEnd;
        }

        private static void TryInjectPendingReceivedPlayAction()
        {
            if (!IsActive || PendingReceivedBattleMessages.Count == 0 ||
                receivedBattleActionInjectionPending ||
                receivedBattleActionPendingUntilVfx ||
                processingReceivedBattleAction)
            {
                return;
            }

            if (HasPendingAuthorityReplayBeforeReceivedBattleMessage())
            {
                return;
            }

            NetworkBattleManagerBase manager =
                BattleManagerBase.GetIns() as NetworkBattleManagerBase;
            if (manager?.VfxMgr == null || !manager.VfxMgr.IsEnd)
            {
                return;
            }

            Inject(PendingReceivedBattleMessages.Dequeue());
        }

        private static void TryInjectPendingAuthorityReplayAction()
        {
            if (!IsActive || Role != P2PRole.Guest ||
                PendingAuthorityReplayActions.Count == 0 ||
                authorityReplayDispatchActive ||
                receivedBattleActionInjectionPending ||
                receivedBattleActionPendingUntilVfx ||
                processingReceivedBattleAction)
            {
                return;
            }

            if (PendingReceivedBattleMessages.Count > 0 &&
                !HasPendingAuthorityReplayBeforeReceivedBattleMessage())
            {
                return;
            }

            NetworkBattleManagerBase manager =
                BattleManagerBase.GetIns() as NetworkBattleManagerBase;
            if (manager?.VfxMgr == null || !manager.VfxMgr.IsEnd)
            {
                return;
            }

            PendingAuthorityReplayAction pending =
                PendingAuthorityReplayActions.Dequeue();
            authorityReplayDispatchActive = true;
            try
            {
                if (!TryProcessAuthorityLocalReplay(
                        pending.Agent, pending.Data, out bool consumed) ||
                    !consumed)
                {
                    ReportBattleDiagnostic(
                        "Authority replay dispatcher could not consume actionId=" +
                        GetAuthorityActionId(pending.Data) + ", uri=" +
                        GetUri(pending.Data) + ".");
                }
            }
            finally
            {
                authorityReplayDispatchActive = false;
            }
        }

        private static bool HasPendingAuthorityReplayBeforeReceivedBattleMessage()
        {
            if (PendingAuthorityReplayActions.Count == 0)
            {
                return false;
            }
            if (PendingReceivedBattleMessages.Count == 0)
            {
                return true;
            }

            Dictionary<string, object> authority =
                PendingAuthorityReplayActions.Peek().Data;
            Dictionary<string, object> received =
                PendingReceivedBattleMessages.Peek();
            bool hasAuthoritySequence = TryGetStateInt(
                authority, "playSeq", out int authoritySequence);
            bool hasReceivedSequence = TryGetStateInt(
                received, "playSeq", out int receivedSequence);
            if (hasAuthoritySequence && hasReceivedSequence)
            {
                return authoritySequence <= receivedSequence;
            }

            // Packets without a play sequence are lifecycle packets. Preserve
            // the native receive queue's existing order by letting its head go
            // first instead of guessing that an authority result is newer.
            return false;
        }

        private static bool IsOrderedReceivedBattleMessage(string uri)
        {
            return string.Equals(uri,
                       NetworkBattleDefine.NetworkBattleURI.PlayActions.ToString(),
                       StringComparison.Ordinal) ||
                string.Equals(uri,
                    NetworkBattleDefine.NetworkBattleURI.TurnEndActions.ToString(),
                    StringComparison.Ordinal) ||
                string.Equals(uri,
                    NetworkBattleDefine.NetworkBattleURI.TurnStart.ToString(),
                    StringComparison.Ordinal) ||
                string.Equals(uri,
                    NetworkBattleDefine.NetworkBattleURI.TurnEnd.ToString(),
                    StringComparison.Ordinal) ||
                string.Equals(uri,
                    NetworkBattleDefine.NetworkBattleURI.TurnEndFinal.ToString(),
                    StringComparison.Ordinal);
        }

        private static int GetMessagePlayIndex(Dictionary<string, object> data)
        {
            return data != null && TryGetStateInt(data, "playIdx", out int index)
                ? index
                : -1;
        }

        private static void TryCompleteReceivedBattleAction()
        {
            if (!receivedBattleActionInjectionPending &&
                !receivedBattleActionPendingUntilVfx)
            {
                return;
            }

            if (receivedBattleActionInjectionPending)
            {
                bool injectionTimedOut = receivedBattleActionStartedUtc !=
                    DateTime.MinValue &&
                    DateTime.UtcNow - receivedBattleActionStartedUtc >=
                    TimeSpan.FromSeconds(BattleStateCheckTimeoutSeconds);
                if (injectionTimedOut && !receivedBattleActionStallReported)
                {
                    receivedBattleActionStallReported = true;
                    ReportBattleDiagnostic(
                        "RECEIVE ACTION DISPATCH STALL: uri=" +
                        (activeReceivedBattleActionUri ?? "?") +
                        " did not reach NetworkBattleReceiver within " +
                        BattleStateCheckTimeoutSeconds + " seconds.");
                }
                return;
            }

            NetworkBattleManagerBase manager =
                BattleManagerBase.GetIns() as NetworkBattleManagerBase;
            bool timedOut = receivedBattleActionStartedUtc != DateTime.MinValue &&
                DateTime.UtcNow - receivedBattleActionStartedUtc >=
                TimeSpan.FromSeconds(BattleStateCheckTimeoutSeconds);
            if (!receivedBattleActionOperationStarted)
            {
                if (timedOut && !receivedBattleActionStallReported)
                {
                    receivedBattleActionStallReported = true;
                    ReportBattleDiagnostic(
                        "RECEIVE OPERATION START STALL: uri=" +
                        (activeReceivedBattleActionUri ?? "?") +
                        " reached NetworkBattleReceiver but did not reach " +
                        "OperateReceive within " +
                        BattleStateCheckTimeoutSeconds + " seconds.");
                }
                return;
            }
            if (manager?.VfxMgr == null || !manager.VfxMgr.IsEnd)
            {
                if (timedOut && !receivedBattleActionStallReported)
                {
                    receivedBattleActionStallReported = true;
                    ReportBattleDiagnostic(
                        $"RECEIVE ACTION STALL: the effect queue did not finish " +
                        $"within {BattleStateCheckTimeoutSeconds} seconds; " +
                        DescribeEffectQueue(manager) + ". Pending remote messages=" +
                        (PendingReceivedBattleMessages.Count +
                            PendingAuthorityReplayActions.Count) + ".");
                }
                return;
            }

            if (HasPendingReceivedPostActionState())
            {
                if (!timedOut)
                {
                    return;
                }

                if (!receivedBattleActionStallReported)
                {
                    receivedBattleActionStallReported = true;
                    ReportBattleDiagnostic(
                        $"RECEIVE STATE APPLY STALL: native effects completed, but " +
                        $"post-action state could not be resolved within " +
                        $"{BattleStateCheckTimeoutSeconds} seconds. " +
                        DescribePendingReceivedPostActionState() + ".");
                }
                // Do not discard a post-action private/history snapshot and
                // then admit the next action. That turns a recoverable delayed
                // state application into a real simulation divergence.
                return;
            }

            // Promote post-action hidden-zone state only after all effects from
            // this message have run. The next native action then sees the same
            // hand/deck modifiers as the source client.
            ApplyPendingReceivedHiddenCardStates();

            string completedUri = activeReceivedBattleActionUri;
            processingReceivedBattleAction = false;
            receivedBattleActionInjectionPending = false;
            receivedBattleActionPendingUntilVfx = false;
            receivedBattleActionOperationStarted = false;
            receivedBattleActionStartedUtc = DateTime.MinValue;
            receivedBattleActionStallReported = false;
            authorityLocalReplayActive = false;
            localActionCaptureActive = false;
            PendingLocalConditionResults.Clear();
            localActionPreHistoryState = null;
            localActionPreHistoryRevision = 0;
            activeReceivedBattleActionUri = null;
            currentAuthorityReplayData = null;
            TryEnableGuestMenuAfterReceivedTurnStart(manager, completedUri);
            CompleteGuestAuthorityRequestIfMatching(completedUri);
        }

        private static void TryEnableGuestMenuAfterReceivedTurnStart(
            NetworkBattleManagerBase manager,
            string completedUri)
        {
            if (Role != P2PRole.Guest || manager == null ||
                manager.IsBattleEnd || !string.Equals(completedUri,
                    NetworkBattleDefine.NetworkBattleURI.TurnStart.ToString(),
                    StringComparison.Ordinal) ||
                manager.BattlePlayer == null ||
                !manager.BattlePlayer.IsSelfTurn)
            {
                return;
            }

            // A Host-originated Guest TurnStart has no pending Guest request,
            // so CompleteGuestAuthorityRequestIfMatching cannot re-enable the
            // local menu. Do it at the same committed native boundary.
            TryEnableLocalBattleMenu();
        }

        private static bool HasPendingReceivedPostActionState()
        {
            if (PendingFusionActions.Count > 0)
            {
                return true;
            }

            int localOwner = Role == P2PRole.Host ? 1 : 0;
            return ReceivedPlayerHistoryStates.Values.Any(state =>
                state.ReadyToApply && state.Owner != localOwner);
        }

        private static void ApplyPendingReceivedHiddenCardStates()
        {
            if (!IsActive || PendingReceivedHiddenCardStates.Count == 0)
            {
                return;
            }

            foreach (KeyValuePair<string, Dictionary<string, object>> item in
                PendingReceivedHiddenCardStates.ToList())
            {
                ReceivedHiddenCardStates[item.Key] = item.Value;
                ReceivedHiddenCardStateSignatures[item.Key] =
                    JsonConvert.SerializeObject(item.Value, P2PJson.Settings);
                AppliedReceivedHiddenCardStates.Remove(item.Key);
            }
            PendingReceivedHiddenCardStates.Clear();
            TryApplyPendingHiddenCardStates();
        }

        private static string DescribePendingReceivedPostActionState()
        {
            int localOwner = Role == P2PRole.Host ? 1 : 0;
            string fusion = PendingFusionActions.Count == 0
                ? "none"
                : string.Join(",", PendingFusionActions.Select(action =>
                {
                    TryGetStateInt(action, "owner", out int owner);
                    TryGetStateInt(action, "targetIdx", out int targetIndex);
                    return $"owner={owner}/targetIdx={targetIndex}";
                }));
            string history = string.Join(",",
                ReceivedPlayerHistoryStates.Values
                    .Where(state => state.ReadyToApply &&
                        state.Owner != localOwner)
                    .OrderBy(state => state.Revision)
                    .Select(state =>
                        $"owner={state.Owner}/revision={state.Revision}/" +
                        $"attempts={state.Attempts}/unresolved=" +
                        (string.IsNullOrEmpty(state.LastUnresolved)
                            ? "unknown" : state.LastUnresolved)));
            return $"pendingFusion=[{fusion}], pendingHistory=[{history}], " +
                $"queuedMessages={PendingReceivedBattleMessages.Count}";
        }

        private static void DiscardUnresolvedReceivedPostActionState()
        {
            PendingFusionActions.Clear();
            PendingFusionActionSignatures.Clear();
            int localOwner = Role == P2PRole.Host ? 1 : 0;
            foreach (string key in ReceivedPlayerHistoryStates
                .Where(item => item.Value.ReadyToApply &&
                    item.Value.Owner != localOwner)
                .Select(item => item.Key)
                .ToList())
            {
                ReceivedPlayerHistoryStates.Remove(key);
            }
        }

        private static bool IsPrivateStateSyncActive =>
            localPrivateStateSent && remotePrivateStateReceived;

        internal static void SynchronizeAuthoritativeRandomSkillTargets(
            SkillBase skill,
            ref VfxWith<List<BattleCardBase>, Dictionary<int, BattleCardBase>> result)
        {
            if (!IsActive || skill?.SkillPrm?.ownerCard == null || result == null ||
                BattleManagerBase.IsForecast || !RegisterTool.IsSkillRandom(skill))
            {
                return;
            }

            // Protocol v3 restores the native sender/receiver contract. The
            // source's RegisterUnapproved data contains randomTargetIdx in
            // uList; NetworkBattleReceiver converts it to CardDataModel and
            // NetworkExecutionInfoCreator consumes it through
            // GetUnapprovedCardObj. Do not capture/replay a second random
            // target list on top of that original path.
            if (UseNativeClientActionTiming)
            {
                return;
            }

            // A local action can also activate a random skill owned by the
            // opponent (last words, reactions, etc.). Authority belongs to the
            // action source, not to skill.ownerCard.IsPlayer. The presence of a
            // staged source result is therefore the reliable receive-side test.
            if (!receivedAuthoritativeActionActive)
            {
                // A peer action can contain random skills even when the sender is
                // older and did not publish the authoritative side channel. Do not
                // capture those remote calculations as if they belonged to our next
                // local action.
                if (processingReceivedBattleAction)
                {
                    return;
                }
                CaptureAuthoritativeSkillTargets(
                    skill, result.Value_1, result.Value_2);
                return;
            }

            if (!TryConsumeAuthoritativeSkillTargets(
                    skill,
                    out List<BattleCardBase> targets,
                    out Dictionary<int, BattleCardBase> independentTargets,
                    out string diagnostic))
            {
                if (!string.IsNullOrEmpty(diagnostic))
                {
                    Plugin.Logger.LogWarning(
                        "[P2P] Could not apply authoritative random targets: " +
                        diagnostic + ".");
                }
                return;
            }

            List<BattleCardBase> originalTargets = result.Value_1 ??
                new List<BattleCardBase>();
            if (!originalTargets.Select(CardReferenceDiagnostic).SequenceEqual(
                    targets.Select(CardReferenceDiagnostic)))
            {
                Plugin.Logger.LogInfo(
                    $"[P2P] Replaced locally calculated random targets for " +
                    $"card idx={skill.SkillPrm.ownerCard.Index}, " +
                    $"skill={skill.GetType().Name}: " +
                    $"local=[{string.Join(",", originalTargets.Select(CardReferenceDiagnostic))}], " +
                    $"source=[{string.Join(",", targets.Select(CardReferenceDiagnostic))}].");
            }

            result = new VfxWith<List<BattleCardBase>, Dictionary<int, BattleCardBase>>(
                result.Vfx,
                targets,
                independentTargets);
        }

        private static void CaptureAuthoritativeSkillTargets(
            SkillBase skill,
            IEnumerable<BattleCardBase> targets,
            IDictionary<int, BattleCardBase> independentTargets)
        {
            BattleCardBase ownerCard = skill.SkillPrm.ownerCard;
            bool localOwnerIsHost = Role == P2PRole.Host;
            bool skillOwnerIsHost = ownerCard.IsPlayer
                ? localOwnerIsHost
                : !localOwnerIsHost;
            List<object> targetReferences = (targets ??
                    Enumerable.Empty<BattleCardBase>())
                .Where(IsValidAuthorityActor)
                .Select(card => (object)CapturePlayerCardReference(
                    card, localOwnerIsHost))
                .ToList();
            int movement = GetSkillMovement(skill);
            Dictionary<string, object> capture =
                new Dictionary<string, object>
                {
                    ["seq"] = ++localAuthoritativeSkillTargetSequence,
                    ["owner"] = skillOwnerIsHost ? 1 : 0,
                    ["ownerIdx"] = ownerCard.Index,
                    ["ownerCardId"] = ownerCard.CardId,
                    ["skillIndex"] = GetNetworkSkillIndex(skill),
                    ["published"] = NetworkBattleGenericTool.GetPublishSkillCount(skill),
                    ["movement"] = movement,
                    ["skillType"] = skill.GetType().FullName ??
                        skill.GetType().Name,
                    ["targets"] = targetReferences
                };
            if (independentTargets != null && independentTargets.Count > 0)
            {
                capture["independent"] = independentTargets
                    .Where(item => IsValidAuthorityActor(item.Value))
                    .Select(item => (object)new Dictionary<string, object>
                    {
                        ["slot"] = item.Key,
                        ["card"] = CapturePlayerCardReference(
                            item.Value, localOwnerIsHost)
                    })
                    .ToList();
            }
            LocalAuthoritativeSkillTargets.Add(capture);
        }

        private static void AppendLocalAuthoritativeSkillTargets(
            string uri,
            Dictionary<string, object> data)
        {
            if (data == null || LocalAuthoritativeSkillTargets.Count == 0 ||
                !IsOrderedLocalBattleMessage(uri))
            {
                return;
            }

            data[AuthoritativeSkillTargetsKey] =
                LocalAuthoritativeSkillTargets
                    .Select(target => (object)P2PJson.CloneDictionary(target))
                    .ToList();
            Plugin.Logger.LogDebug(
                $"[P2P] Attached {LocalAuthoritativeSkillTargets.Count} " +
                $"authoritative random skill target result(s) to {uri}.");
            LocalAuthoritativeSkillTargets.Clear();
        }

        private static void AppendLocalActionManifest(
            string uri,
            Dictionary<string, object> data)
        {
            if (data == null || !IsOrderedLocalBattleMessage(uri))
            {
                return;
            }

            bool hasTargets = LocalAuthoritativeSkillTargets.Count > 0;
            bool hasEvaluations = LocalAuthoritativeSkillEvaluations.Count > 0;
            // Hidden-card/history snapshots can be large and already have their
            // own incremental transport. Keep them out of the manifest to avoid
            // sending the same private payload twice; the manifest carries the
            // resolved action result, while the snapshot keys remain adjacent to
            // it for the native replacement path.
            if (!hasTargets && !hasEvaluations)
            {
                return;
            }

            Dictionary<string, object> manifest =
                new Dictionary<string, object>
                {
                    ["version"] = P2PBattleProtocol.ActionManifestVersion,
                    ["uri"] = uri,
                    ["source"] = ResolveActionManifestSource(data)
                };
            int playIndex = GetMessagePlayIndex(data);
            int actionSequence;
            if (TryGetStateInt(data, "pubSeq", out int publishedSequence) &&
                publishedSequence >= 0)
            {
                actionSequence = publishedSequence;
            }
            else
            {
                actionSequence = ++localActionManifestSequence;
            }
            manifest["seq"] = actionSequence;
            manifest["actionId"] = uri + ":" +
                actionSequence.ToString(CultureInfo.InvariantCulture);
            if (playIndex >= 0)
            {
                manifest["playIdx"] = playIndex;
            }
            foreach (Dictionary<string, object> entry in
                LocalAuthoritativeSkillTargets.Concat(LocalAuthoritativeSkillEvaluations))
            {
                entry["actionSeq"] = actionSequence;
            }
            if (hasTargets)
            {
                manifest[AuthoritativeSkillTargetsKey] =
                    LocalAuthoritativeSkillTargets
                        .Select(item => (object)P2PJson.CloneDictionary(item))
                        .ToList();
            }
            if (hasEvaluations)
            {
                manifest[AuthoritativeSkillEvaluationsKey] =
                    LocalAuthoritativeSkillEvaluations
                        .Select(item => (object)P2PJson.CloneDictionary(item))
                        .ToList();
            }
            data[P2PBattleProtocol.ActionManifestKey] = manifest;
            Plugin.Logger.LogDebug(
                $"[P2P] Attached action manifest seq={manifest["seq"]} " +
                $"to {uri}: evaluations={LocalAuthoritativeSkillEvaluations.Count}, " +
                $"targets={LocalAuthoritativeSkillTargets.Count}.");
        }

        private static int ResolveActionManifestSource(
            Dictionary<string, object> data)
        {
            if (data != null &&
                TryGetStateInt(data, P2PBattleProtocol.AuthoritySourceKey,
                    out int explicitSource) &&
                (explicitSource == 0 || explicitSource == 1))
            {
                return explicitSource;
            }
            return Role == P2PRole.Host ? 1 : 0;
        }

        private static void DrainPendingLocalConditionResults()
        {
            if (PendingLocalConditionResults.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<SkillBase, List<AuthoritativeSkillConditionResult>>
                pending in PendingLocalConditionResults.ToList())
            {
                SkillBase skill = pending.Key;
                if (skill?.SkillPrm?.ownerCard == null ||
                    pending.Value == null || pending.Value.Count == 0)
                {
                    continue;
                }
                Dictionary<string, object> entry =
                    CreateAuthoritativeSkillEvaluationEntry(skill);
                entry["conditions"] = pending.Value
                    .Select(result => (object)new Dictionary<string, object>
                    {
                        ["ordinal"] = result.Ordinal,
                        ["prePlay"] = result.IsPrePlay ? 1 : 0,
                        ["skipTarget"] = result.IsSkipTarget ? 1 : 0,
                        ["result"] = result.Result ? 1 : 0
                    })
                    .ToList();
                LocalAuthoritativeSkillEvaluations.Add(entry);
            }
            PendingLocalConditionResults.Clear();
        }

        private static void ExpandActionManifestToLegacy(
            Dictionary<string, object> data)
        {
            if (data == null ||
                !data.TryGetValue(P2PBattleProtocol.ActionManifestKey,
                    out object rawManifest) ||
                !(rawManifest is Dictionary<string, object> manifest))
            {
                return;
            }
            if (TryGetStateInt(manifest, "version", out int version) &&
                version > P2PBattleProtocol.ActionManifestVersion)
            {
                Plugin.Logger.LogWarning(
                    $"[P2P] Ignoring unsupported action manifest version {version}; " +
                    $"supported={P2PBattleProtocol.ActionManifestVersion}.");
                return;
            }

            ApplyManifestField(
                data, manifest, AuthoritativeSkillTargetsKey);
            ApplyManifestField(
                data, manifest, AuthoritativeSkillEvaluationsKey);
            if (manifest.TryGetValue("state", out object rawState) &&
                rawState is Dictionary<string, object> state)
            {
                foreach (KeyValuePair<string, object> item in state)
                {
                    ApplyManifestField(data, state, item.Key);
                }
            }
        }

        private static void ApplyManifestField(
            Dictionary<string, object> data,
            Dictionary<string, object> source,
            string key)
        {
            if (source == null || !source.TryGetValue(key, out object value))
            {
                return;
            }
            if (data.TryGetValue(key, out object legacyValue))
            {
                string legacySignature = JsonConvert.SerializeObject(
                    legacyValue, P2PJson.Settings);
                string manifestSignature = JsonConvert.SerializeObject(
                    value, P2PJson.Settings);
                if (!string.Equals(legacySignature, manifestSignature,
                        StringComparison.Ordinal))
                {
                    Plugin.Logger.LogWarning(
                        $"[P2P] Action manifest replaced conflicting legacy " +
                        $"field '{key}'.");
                }
            }
            data[key] = P2PJson.CloneValue(value);
        }

        internal static AuthoritativeSkillEvaluationScope
            BeginAuthoritativeSkillEvaluation(SkillBase skill)
        {
            if (!IsActive || skill?.SkillPrm?.ownerCard == null ||
                BattleManagerBase.IsForecast ||
                !ShouldCaptureActionSkillEvaluation(skill))
            {
                return null;
            }

            if (processingReceivedBattleAction ||
                receivedAuthoritativeSkillEvaluationActive)
            {
                if (!receivedAuthoritativeSkillEvaluationActive ||
                    activeAuthoritativeSkillEvaluationBatch == null)
                {
                    return null;
                }

                int entryIndex = FindAuthoritativeSkillEntry(
                    activeAuthoritativeSkillEvaluationBatch.Entries,
                    skill,
                    out bool usedFallback);
                if (entryIndex < 0)
                {
                    return null;
                }

                Dictionary<string, object> entry =
                    activeAuthoritativeSkillEvaluationBatch.Entries[entryIndex];
                activeAuthoritativeSkillEvaluationBatch.Entries.RemoveAt(entryIndex);
                if (usedFallback)
                {
                    Plugin.Logger.LogWarning(
                        $"[P2P] Matched authoritative private skill evaluation by " +
                        $"owner/index fallback: card idx={skill.SkillPrm.ownerCard.Index}, " +
                        $"skill={skill.GetType().Name}.");
                }

                AuthoritativeSkillEvaluationScope receivedScope =
                    new AuthoritativeSkillEvaluationScope(skill, false, entry);
                AuthoritativeSkillEvaluationScopes.Push(receivedScope);
                return receivedScope;
            }

            Dictionary<string, object> capture =
                CreateAuthoritativeSkillEvaluationEntry(skill);
            AuthoritativeSkillEvaluationScope sourceScope =
                new AuthoritativeSkillEvaluationScope(skill, true, capture);
            if (PendingLocalConditionResults.TryGetValue(
                    skill,
                    out List<AuthoritativeSkillConditionResult> pendingConditions))
            {
                sourceScope.ConditionResults.AddRange(pendingConditions);
                PendingLocalConditionResults.Remove(skill);
            }
            AuthoritativeSkillEvaluationScopes.Push(sourceScope);
            return sourceScope;
        }

        private static Dictionary<string, object>
            CreateAuthoritativeSkillEvaluationEntry(SkillBase skill)
        {
            BattleCardBase ownerCard = skill?.SkillPrm?.ownerCard;
            bool localOwnerIsHost = Role == P2PRole.Host;
            bool skillOwnerIsHost = ownerCard != null && ownerCard.IsPlayer
                ? localOwnerIsHost
                : !localOwnerIsHost;
            return new Dictionary<string, object>
            {
                ["seq"] = ++localAuthoritativeSkillEvaluationSequence,
                ["owner"] = skillOwnerIsHost ? 1 : 0,
                ["ownerIdx"] = ownerCard?.Index ?? 0,
                ["ownerCardId"] = ownerCard?.CardId ?? 0,
                ["skillIndex"] = GetNetworkSkillIndex(skill),
                ["published"] = NetworkBattleGenericTool.GetPublishSkillCount(skill),
                ["movement"] = GetSkillMovement(skill),
                ["skillType"] = skill?.GetType().FullName ??
                    skill?.GetType().Name ?? string.Empty
            };
        }

        internal static void CompleteAuthoritativeSkillEvaluation(
            AuthoritativeSkillEvaluationScope scope,
            bool completed)
        {
            if (scope == null)
            {
                return;
            }

            if (AuthoritativeSkillEvaluationScopes.Count > 0 &&
                ReferenceEquals(AuthoritativeSkillEvaluationScopes.Peek(), scope))
            {
                AuthoritativeSkillEvaluationScopes.Pop();
            }
            else
            {
                Plugin.Logger.LogWarning(
                    "[P2P] Authoritative private skill evaluation scopes completed " +
                    "out of order; clearing the transient scope stack.");
                AuthoritativeSkillEvaluationScopes.Clear();
            }

            if (!scope.IsSource && completed &&
                (scope.Values.Count > 0 ||
                    scope.PreprocessResults.Count > scope.NextPreprocessResult ||
                    scope.ConditionResults.Count > 0))
            {
                Plugin.Logger.LogWarning(
                    $"[P2P] Action manifest result(s) were not consumed: " +
                    $"cardIdx={scope.Skill?.SkillPrm?.ownerCard?.Index ?? 0}, " +
                    $"values={scope.Values.Count}, " +
                    $"preprocessRemaining={Math.Max(0,
                        scope.PreprocessResults.Count - scope.NextPreprocessResult)}, " +
                    $"conditions={scope.ConditionResults.Count}.");
            }

            if (!scope.IsSource || !completed ||
                (scope.Values.Count == 0 && scope.PreprocessResults.Count == 0 &&
                    scope.ConditionResults.Count == 0))
            {
                return;
            }

            if (scope.Values.Count > 0)
            {
                scope.Entry["values"] = scope.Values
                    .Select(item => (object)new Dictionary<string, object>
                    {
                        ["keyword"] = item.Keyword,
                        ["value"] = item.Value
                    })
                    .ToList();
            }
            if (scope.PreprocessResults.Count > 0)
            {
                scope.Entry["preprocess"] = scope.PreprocessResults
                    .Select(result => (object)(result ? 1 : 0))
                    .ToList();
            }
            if (scope.ConditionResults.Count > 0)
            {
                scope.Entry["conditions"] = scope.ConditionResults
                    .Select(result => (object)new Dictionary<string, object>
                    {
                        ["ordinal"] = result.Ordinal,
                        ["prePlay"] = result.IsPrePlay ? 1 : 0,
                        ["skipTarget"] = result.IsSkipTarget ? 1 : 0,
                        ["result"] = result.Result ? 1 : 0
                    })
                    .ToList();
            }
            LocalAuthoritativeSkillEvaluations.Add(scope.Entry);
        }

        internal static bool TryGetAuthoritativeSkillConditionResult(
            SkillBase skill,
            bool isPrePlay,
            bool isSkipTarget,
            out bool result)
        {
            result = false;
            if (!IsActive || !receivedAuthoritativeSkillEvaluationActive ||
                skill == null || activeAuthoritativeSkillEvaluationBatch == null)
            {
                return false;
            }

            AuthoritativeSkillEvaluationScope scope =
                AuthoritativeSkillEvaluationScopes.FirstOrDefault(item =>
                    !item.IsSource && ReferenceEquals(item.Skill, skill));
            if (scope == null)
            {
                // Older native builds can evaluate a condition while building
                // execution info, before SkillBase.CallStart creates the normal
                // scope. Consume the matching manifest entry directly in that
                // case instead of falling back to a hidden-state guess.
                return TryConsumeUnscopedAuthoritativeCondition(
                    skill, isPrePlay, isSkipTarget, out result);
            }

            int index = scope.ConditionResults.FindIndex(item =>
                item.IsPrePlay == isPrePlay && item.IsSkipTarget == isSkipTarget);
            if (index < 0)
            {
                // Some native versions call the same condition helper with a
                // different skip-target flag while building the execution info.
                // The ordered result is still authoritative, so consume the next
                // result for this skill rather than reevaluating hidden state.
                index = scope.ConditionResults.Count > 0 ? 0 : -1;
            }
            if (index < 0)
            {
                return false;
            }

            result = scope.ConditionResults[index].Result;
            scope.ConditionResults.RemoveAt(index);
            return true;
        }

        private static bool TryConsumeUnscopedAuthoritativeCondition(
            SkillBase skill,
            bool isPrePlay,
            bool isSkipTarget,
            out bool result)
        {
            result = false;
            List<Dictionary<string, object>> entries =
                activeAuthoritativeSkillEvaluationBatch?.Entries;
            if (entries == null || entries.Count == 0)
            {
                return false;
            }
            int entryIndex = FindAuthoritativeSkillEntry(
                entries, skill, out _);
            if (entryIndex < 0 ||
                !entries[entryIndex].TryGetValue(
                    "conditions", out object rawConditions) ||
                !(rawConditions is IList conditions))
            {
                return false;
            }

            int conditionIndex = -1;
            for (int i = 0; i < conditions.Count; i++)
            {
                if (!(conditions[i] is Dictionary<string, object> condition))
                {
                    continue;
                }
                bool conditionPrePlay =
                    TryGetStateInt(condition, "prePlay", out int rawPrePlay) &&
                    rawPrePlay != 0;
                bool conditionSkipTarget =
                    TryGetStateInt(condition, "skipTarget", out int rawSkipTarget) &&
                    rawSkipTarget != 0;
                if (conditionPrePlay == isPrePlay &&
                    conditionSkipTarget == isSkipTarget)
                {
                    conditionIndex = i;
                    break;
                }
            }
            if (conditionIndex < 0 && conditions.Count > 0)
            {
                conditionIndex = 0;
            }
            if (conditionIndex < 0 ||
                !(conditions[conditionIndex] is Dictionary<string, object> selected) ||
                !TryGetStateInt(selected, "result", out int rawResult))
            {
                return false;
            }

            result = rawResult != 0;
            conditions.RemoveAt(conditionIndex);
            Dictionary<string, object> entry = entries[entryIndex];
            if (conditions.Count == 0)
            {
                entry.Remove("conditions");
            }
            if (!entry.ContainsKey("conditions") &&
                !entry.ContainsKey("values") &&
                !entry.ContainsKey("preprocess"))
            {
                entries.RemoveAt(entryIndex);
            }
            Plugin.Logger.LogDebug(
                $"[P2P] Consumed an action-manifest condition before the " +
                $"skill scope was created: cardIdx={skill.SkillPrm?.ownerCard?.Index ?? 0}, " +
                $"result={result}.");
            return true;
        }

        internal static void ObserveAuthoritativeSkillConditionResult(
            SkillBase skill,
            bool isPrePlay,
            bool isSkipTarget,
            bool result)
        {
            if (!IsActive || skill == null || BattleManagerBase.IsForecast ||
                processingReceivedBattleAction ||
                receivedAuthoritativeSkillEvaluationActive)
            {
                return;
            }
            AuthoritativeSkillEvaluationScope scope =
                AuthoritativeSkillEvaluationScopes.FirstOrDefault(item =>
                    item.IsSource && ReferenceEquals(item.Skill, skill));
            if (scope == null)
            {
                if (!localActionCaptureActive)
                {
                    return;
                }
                if (!PendingLocalConditionResults.TryGetValue(
                        skill,
                        out List<AuthoritativeSkillConditionResult> pending))
                {
                    pending = new List<AuthoritativeSkillConditionResult>();
                    PendingLocalConditionResults[skill] = pending;
                }
                pending.Add(new AuthoritativeSkillConditionResult(
                    pending.Count,
                    isPrePlay,
                    isSkipTarget,
                    result));
                return;
            }
            scope.ConditionResults.Add(new AuthoritativeSkillConditionResult(
                scope.ConditionResults.Count,
                isPrePlay,
                isSkipTarget,
                result));
        }

        internal static void ObserveAuthoritativeSkillOptionValue(
            SkillOptionValue optionValue,
            SkillFilterCreator.ContentKeyword keyword,
            int value)
        {
            if (AuthoritativeSkillEvaluationScopes.Count == 0 ||
                processingReceivedBattleAction ||
                receivedAuthoritativeSkillEvaluationActive)
            {
                return;
            }
            AuthoritativeSkillEvaluationScope scope =
                AuthoritativeSkillEvaluationScopes.FirstOrDefault(item =>
                    item.IsSource && item.Skill?.OptionValue == optionValue);
            if (scope == null || !scope.IsSource || scope.Skill?.OptionValue != optionValue ||
                !scope.Skill.OptionValue.HasInfoByName(keyword))
            {
                return;
            }
            bool variableValue = false;
            try
            {
                variableValue = scope.Skill.OptionValue.IsVariableOptionValue(keyword);
            }
            catch (Exception)
            {
            }
            if (!variableValue &&
                !RegisterSkillConditionCheck.DoesSkillUsePrivateCount(
                    scope.Skill, false, false))
            {
                return;
            }
            scope.Values.Add(new AuthoritativeSkillOptionValue(
                keyword.ToString(), value));
        }

        internal static bool TryGetAuthoritativeSkillOptionValue(
            SkillOptionValue optionValue,
            SkillFilterCreator.ContentKeyword keyword,
            out int value)
        {
            value = 0;
            if (AuthoritativeSkillEvaluationScopes.Count == 0)
            {
                return false;
            }
            AuthoritativeSkillEvaluationScope scope =
                AuthoritativeSkillEvaluationScopes.FirstOrDefault(item =>
                    !item.IsSource && item.Skill?.OptionValue == optionValue);
            if (scope == null || scope.IsSource || scope.Skill?.OptionValue != optionValue)
            {
                return false;
            }

            string keywordName = keyword.ToString();
            int valueIndex = scope.Values.FindIndex(item =>
                string.Equals(item.Keyword, keywordName, StringComparison.Ordinal));
            if (valueIndex < 0)
            {
                return false;
            }
            value = scope.Values[valueIndex].Value;
            scope.Values.RemoveAt(valueIndex);
            return true;
        }

        internal static bool TryGetAuthoritativePreprocessResult(
            bool preexecutionCheck,
            out bool result)
        {
            result = false;
            if (!preexecutionCheck ||
                AuthoritativeSkillEvaluationScopes.Count == 0)
            {
                return false;
            }
            AuthoritativeSkillEvaluationScope scope =
                AuthoritativeSkillEvaluationScopes.Peek();
            if (scope.IsSource ||
                scope.NextPreprocessResult >= scope.PreprocessResults.Count)
            {
                return false;
            }
            result = scope.PreprocessResults[scope.NextPreprocessResult++];
            return true;
        }

        internal static void ObserveAuthoritativePreprocessResult(
            bool preexecutionCheck,
            bool result)
        {
            if (!preexecutionCheck ||
                AuthoritativeSkillEvaluationScopes.Count == 0)
            {
                return;
            }
            AuthoritativeSkillEvaluationScope scope =
                AuthoritativeSkillEvaluationScopes.Peek();
            if (scope.IsSource)
            {
                scope.PreprocessResults.Add(result);
            }
        }

        private static bool ShouldCaptureActionSkillEvaluation(
            SkillBase skill)
        {
            // Only create source scopes while a local action is being assembled.
            // Receive-side scopes are enabled by the authoritative batch itself.
            // This prevents UI previews, idle rule checks, and other native calls
            // outside an action from leaking stale entries into the next packet.
            return skill != null && skill.SkillPrm?.ownerCard != null &&
                (localActionCaptureActive ||
                    processingReceivedBattleAction ||
                    receivedAuthoritativeSkillEvaluationActive);
        }

        private static void AppendLocalAuthoritativeSkillEvaluations(
            string uri,
            Dictionary<string, object> data)
        {
            if (data == null || LocalAuthoritativeSkillEvaluations.Count == 0 ||
                !IsOrderedLocalBattleMessage(uri))
            {
                return;
            }

            data[AuthoritativeSkillEvaluationsKey] =
                LocalAuthoritativeSkillEvaluations
                    .Select(entry => (object)P2PJson.CloneDictionary(entry))
                    .ToList();
            Plugin.Logger.LogDebug(
                $"[P2P] Attached {LocalAuthoritativeSkillEvaluations.Count} " +
                $"authoritative private skill evaluation(s) to {uri}.");
            LocalAuthoritativeSkillEvaluations.Clear();
        }

        private static void RememberReceivedAuthoritativeSkillEvaluations(
            Dictionary<string, object> data)
        {
            if (data == null ||
                !ReadAuthorityBool(data, "p2pAuthorityLocalReplay") ||
                !data.TryGetValue(AuthoritativeSkillEvaluationsKey,
                    out object rawEvaluations) ||
                rawEvaluations is string ||
                !(rawEvaluations is IEnumerable evaluationEntries))
            {
                return;
            }

            List<Dictionary<string, object>> entries =
                new List<Dictionary<string, object>>();
            foreach (object rawEntry in evaluationEntries)
            {
                if (rawEntry is Dictionary<string, object> entry)
                {
                    entries.Add(P2PJson.CloneDictionary(entry));
                }
            }
            if (entries.Count == 0)
            {
                return;
            }

            AuthoritativeSkillEvaluationBatch batch =
                new AuthoritativeSkillEvaluationBatch(
                    entries, GetAuthorityActionId(data), GetUri(data));
            PendingAuthoritativeSkillEvaluationBatches.Enqueue(batch);
            currentInjectedAuthoritativeSkillEvaluationBatch = batch;
            ActivateNextAuthoritativeSkillEvaluationBatch();
        }

        private static void RememberReceivedAuthoritativeSkillTargets(
            Dictionary<string, object> data)
        {
            if (data == null ||
                !ReadAuthorityBool(data, "p2pAuthorityLocalReplay") ||
                !data.TryGetValue(AuthoritativeSkillTargetsKey, out object rawTargets) ||
                rawTargets is string || !(rawTargets is IEnumerable targetEntries))
            {
                return;
            }

            List<Dictionary<string, object>> entries =
                new List<Dictionary<string, object>>();
            foreach (object rawEntry in targetEntries)
            {
                if (rawEntry is Dictionary<string, object> entry)
                {
                    entries.Add(P2PJson.CloneDictionary(entry));
                }
            }
            if (entries.Count == 0)
            {
                return;
            }
            AuthoritativeSkillTargetBatch batch =
                new AuthoritativeSkillTargetBatch(
                    entries, GetAuthorityActionId(data), GetUri(data));
            PendingAuthoritativeSkillTargetBatches.Enqueue(batch);
            currentInjectedAuthoritativeSkillTargetBatch = batch;
            ActivateNextAuthoritativeSkillTargetBatch();
        }

        internal static void MarkReceivedAuthoritativeSkillTargetsReady()
        {
            if (IsActive && currentInjectedAuthoritativeSkillTargetBatch != null)
            {
                // ReceivedMessage only schedules the native operation. Keep its
                // source targets until the VFX queue has actually executed it.
                currentInjectedAuthoritativeSkillTargetBatch.ReadyForCleanup = true;
            }
            if (IsActive &&
                currentInjectedAuthoritativeSkillEvaluationBatch != null)
            {
                currentInjectedAuthoritativeSkillEvaluationBatch.ReadyForCleanup = true;
            }
        }

        internal static void PrepareNativeReceivedActionMetadata(
            NetworkBattleDefine.NetworkBattleURI uri,
            Dictionary<string, object> data)
        {
            if (!IsActive || data == null)
            {
                return;
            }

            // Protocol v3 does not use the migration manifest.  Keep decoding
            // only for the retired replay compatibility path; normal native
            // messages must enter the receiver unchanged so there is one
            // condition/random-result source.
            if (!UseNativeClientActionTiming)
            {
                ExpandActionManifestToLegacy(data);
            }
            // Apply the same pre-action identity normalization on both
            // receivers. Host receives Guest-originated emits into its local
            // BattleEnemy mirror; it must have the same canonical knownList
            // identities before NetworkBattleData builds the operation.
            // Guest uses the same path for Host-originated results. This keeps
            // compact idxList entries from leaving duplicate identities and
            // lets generated/drawn private cards enter the native receiver at
            // the original server boundary.
            PrepareAuthorityFusionMetamorphoseReplayData(data);
            PromoteReceivedHiddenCardStatesIntoNativeKnownList(data);

            // ProcessingRecivedData can stock an ordered packet and invoke the
            // native receiver later. Bind metadata here, at the actual receiver
            // call, so a stocked action cannot lose the hidden/history/fusion/
            // random state that belongs to it while waiting for its sequence.
            bool ordered = IsOrderedReceivedBattleMessage(uri.ToString());
            if (ordered)
            {
                bool matchesReservedInjection =
                    receivedBattleActionInjectionPending &&
                    string.Equals(activeReceivedBattleActionUri, uri.ToString(),
                        StringComparison.Ordinal);
                // Unity may run the native network-agent Update before the mod's
                // Update on the first idle frame. Finish the preceding operation's
                // post-state here before staging metadata for the next operation.
                TryApplyPendingPlayerHistoryStates();
                TryApplyPendingFusionActions();
                TryCompleteReceivedBattleAction();
                if ((receivedBattleActionInjectionPending ||
                        receivedBattleActionPendingUntilVfx ||
                        processingReceivedBattleAction) &&
                    !matchesReservedInjection)
                {
                    ReportBattleDiagnostic(
                        "A new native battle action started before the previous " +
                        "P2P post-action state finished applying; finalizing the " +
                        "previous boundary. " +
                        DescribePendingReceivedPostActionState() + ".");
                    DiscardUnresolvedReceivedPostActionState();
                    ApplyPendingReceivedHiddenCardStates();
                    processingReceivedBattleAction = false;
                    receivedBattleActionInjectionPending = false;
                    receivedBattleActionPendingUntilVfx = false;
                    receivedBattleActionOperationStarted = false;
                    receivedBattleActionStartedUtc = DateTime.MinValue;
                    receivedBattleActionStallReported = false;
                }
                TryClearConsumedAuthoritativeSkillTargets();
                TryClearConsumedAuthoritativeSkillEvaluations();

                processingReceivedBattleAction = true;
                receivedBattleActionInjectionPending = false;
                receivedBattleActionPendingUntilVfx = true;
                receivedBattleActionOperationStarted = false;
                receivedBattleActionStartedUtc = DateTime.UtcNow;
                receivedBattleActionStallReported = false;
                activeReceivedBattleActionUri = uri.ToString();
            }

            nativeReceivedMetadataActive = true;
            currentAuthorityReplayData = data != null
                ? P2PJson.CloneDictionary(data)
                : null;
            currentInjectedAuthoritativeSkillTargetBatch = null;
            currentInjectedAuthoritativeSkillEvaluationBatch = null;
            RememberReceivedHiddenCardStates(data, ordered);
            RememberReceivedPlayerHistoryBeforeState(data);
            RememberReceivedPlayerHistoryState(data, false);
            TryApplyPendingHiddenCardStates();
            RememberReceivedAuthoritativeSkillTargets(data);
            RememberReceivedAuthoritativeSkillEvaluations(data);
            ApplyReceivedFusionAction(data, false);
            if (Role == P2PRole.Guest && data.TryGetValue(
                    P2PBattleProtocol.AuthorityResultRequestIdKey,
                    out object rawAuthorityRequestId))
            {
                receivedAuthorityRequestId = rawAuthorityRequestId?.ToString();
            }
            else if (ordered)
            {
                // A Host-originated action has no Guest request ID. Do not
                // leave an older authority ID attached to this boundary.
                receivedAuthorityRequestId = null;
            }
        }

        internal static void CompleteNativeReceivedActionMetadata(
            Dictionary<string, object> data,
            bool accepted)
        {
            if (!IsActive)
            {
                return;
            }

            if (accepted)
            {
                MarkReceivedAuthoritativeSkillTargetsReady();
            }
            else
            {
                RejectReceivedAuthoritativeSkillTargets();
                DiscardReceivedPlayerHistoryState(data);
                PendingReceivedHiddenCardStates.Clear();
                NativePromotedReceivedHiddenCardStateSignatures.Clear();
                NativeReplacedReceivedHiddenCardStateSignatures.Clear();
                processingReceivedBattleAction = false;
                receivedBattleActionInjectionPending = false;
                receivedBattleActionPendingUntilVfx = false;
                receivedBattleActionOperationStarted = false;
                receivedBattleActionStartedUtc = DateTime.MinValue;
                receivedBattleActionStallReported = false;
                authorityLocalReplayActive = false;
                localActionCaptureActive = false;
                PendingLocalConditionResults.Clear();
                localActionPreHistoryState = null;
                localActionPreHistoryRevision = 0;
                activeReceivedBattleActionUri = null;
                CompleteGuestAuthorityRequestIfMatching(GetUri(data), true);
            }

            // Every safe pre-native hook has run by the time ReceivedMessage
            // returns. Unresolved pre-action entries must not leak into the next
            // action and roll its history/fusion state back to an older boundary.
            if (PendingPreActionPlayerHistoryStates.Count > 0)
            {
                Plugin.Logger.LogWarning(
                    "[P2P] Discarding unresolved pre-action player history after " +
                    "the native receive boundary: " +
                    string.Join(",", PendingPreActionPlayerHistoryStates.Keys) + ".");
                PendingPreActionPlayerHistoryStates.Clear();
            }
            StagedPreNativeFusionActions.Clear();
            currentInjectedAuthoritativeSkillTargetBatch = null;
            currentInjectedAuthoritativeSkillEvaluationBatch = null;
            nativeReceivedMetadataActive = false;
            if (!accepted || !IsOrderedReceivedBattleMessage(GetUri(data)))
            {
                currentAuthorityReplayData = null;
            }

            // A non-ordered message cannot have deferred native VFX that own
            // the replay context. Ordered battle messages are finalized by
            // TryCompleteReceivedBattleAction after their VFX queue drains.
            if (accepted && !IsOrderedReceivedBattleMessage(GetUri(data)))
            {
                // A JudgeResult/diagnostic can arrive while an earlier
                // authority PlayActions VFX is still draining. Do not clear
                // its suppression flag merely because this newer packet is
                // non-ordered: late native callbacks from that VFX would then
                // emit a duplicate action while the battle is closing.
                if (!receivedBattleActionPendingUntilVfx &&
                    !processingReceivedBattleAction)
                {
                    authorityLocalReplayActive = false;
                }
                CompleteGuestAuthorityRequestIfMatching(GetUri(data));
            }
        }

        private static void CompleteGuestAuthorityRequestIfMatching(
            string completedUri = null,
            bool force = false)
        {
            if (Role != P2PRole.Guest || !guestAuthorityBusy ||
                string.IsNullOrEmpty(guestAuthorityRequestId))
            {
                return;
            }

            bool isTurnEndRequest = string.Equals(
                guestAuthorityRequestAction, "turn_end",
                StringComparison.Ordinal);
            bool matchesRequest = !string.IsNullOrEmpty(
                    receivedAuthorityRequestId) &&
                string.Equals(receivedAuthorityRequestId,
                    guestAuthorityRequestId, StringComparison.Ordinal);
            // When a Guest turn ends and the Host becomes the next owner, the
            // following TurnStart is emitted by the Host's normal local path,
            // so it has no authority request ID. It is still the terminal
            // boundary for the Guest's turn-end request.
            bool untaggedHostTurnStart = isTurnEndRequest &&
                string.Equals(completedUri,
                    NetworkBattleDefine.NetworkBattleURI.TurnStart.ToString(),
                    StringComparison.Ordinal) &&
                string.IsNullOrEmpty(receivedAuthorityRequestId);
            if (!matchesRequest && !untaggedHostTurnStart)
            {
                return;
            }
            if (!force && isTurnEndRequest &&
                !string.Equals(completedUri,
                    NetworkBattleDefine.NetworkBattleURI.TurnStart.ToString(),
                    StringComparison.Ordinal) &&
                !string.Equals(completedUri,
                    NetworkBattleDefine.NetworkBattleURI.TurnEndFinal.ToString(),
                    StringComparison.Ordinal) &&
                !string.Equals(completedUri,
                    NetworkBattleDefine.NetworkBattleURI.JudgeResult.ToString(),
                    StringComparison.Ordinal))
            {
                // A native turn-end is a three-message transition:
                // TurnEndActions -> TurnEnd -> TurnStart (or a final result).
                // Keep the Guest input gate closed until the transition has
                // reached its terminal boundary.
                return;
            }

            guestAuthorityBusy = false;
            completedAuthorityRequestIds.Add(guestAuthorityRequestId);
            TrimAuthorityResultHistory();
            guestAuthorityRequestId = null;
            guestAuthorityRequestAction = null;
            guestAuthorityRequestSentUtc = DateTime.MinValue;
            receivedAuthorityRequestId = null;
            localAuthorityChoiceCardIndexes.Clear();
            TryEnableLocalBattleMenu();
        }

        private static void QueueAuthorityNextTurnStart(
            NetworkBattleManagerBase manager,
            string requestId)
        {
            if (manager?.VfxMgr == null || manager.IsBattleEnd)
            {
                return;
            }

            manager.VfxMgr.RegisterSequentialVfx<Wizard.Battle.View.Vfx.VfxBase>(
                Wizard.Battle.View.Vfx.InstantVfx.Create(() =>
                    StartAuthorityNextTurn(manager, requestId)));
        }

        private static void QueueHostTurnEndNextTurnStart(
            NetworkBattleManagerBase manager)
        {
            if (manager?.VfxMgr == null || manager.IsBattleEnd)
            {
                return;
            }

            manager.VfxMgr.RegisterSequentialVfx<Wizard.Battle.View.Vfx.VfxBase>(
                Wizard.Battle.View.Vfx.InstantVfx.Create(() =>
                    StartHostTurnEndNextTurn(manager)));
        }

        private static void StartHostTurnEndNextTurn(
            NetworkBattleManagerBase manager)
        {
            if (Role != P2PRole.Host || manager == null ||
                manager.IsBattleEnd || manager.BattlePlayer == null ||
                manager.BattleEnemy == null)
            {
                return;
            }
            if (IsBattleFinished(manager))
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Skipping Host next-turn transition because the " +
                    "turn-end boundary is already final.");
                return;
            }

            manager.ClearRegisterCardList();

            bool hostExtraTurn = manager.BattlePlayer.IsExtraTurn;
            bool guestExtraTurn = manager.BattleEnemy.IsExtraTurn;
            Wizard.Battle.View.Vfx.VfxBase turnStartVfx = null;

            if (hostExtraTurn)
            {
                // ControlTurnStartOpponent selects BattlePlayer when it has an
                // extra turn, matching the native transition after a Host turn.
                turnStartVfx = manager.ControlTurnStartOpponent();
                if (turnStartVfx != null)
                {
                    manager.VfxMgr.RegisterSequentialVfx<
                        Wizard.Battle.View.Vfx.VfxBase>(turnStartVfx);
                }
            }
            else if (guestExtraTurn)
            {
                // The Guest is BattleEnemy on the Host.  Its extra turn is
                // started by ControlTurnStartPlayer and must be replayed as a
                // local authoritative TurnStart on the Guest.  Keep the
                // capture context alive until the VFX has completed so draw,
                // turn-start skills, and private-state changes are included in
                // the result snapshot.
                QueueGuestAuthorityTurnStart(
                    manager,
                    null,
                    true,
                    () => manager.ControlTurnStartPlayer());
            }
            else
            {
                // A normal Guest turn is selected by ControlTurnStartOpponent
                // when neither side has an extra turn.  BattleEnemy does not
                // emit TurnStart locally, so send an explicit replay packet.
                QueueGuestAuthorityTurnStart(
                    manager,
                    null,
                    false,
                    () => manager.ControlTurnStartOpponent());
            }
        }

        private static string BuildAuthorityTransitionRequestId(
            NetworkBattleManagerBase manager)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}-host-turn-{1}-{2}",
                BattleId ?? "battle",
                manager?.CurrentTurn ?? 0,
                ++authorityTransitionSequence);
        }

        private sealed class PendingGuestTurnStart
        {
            internal NetworkBattleManagerBase Manager;
            internal string ResultRequestId;
            internal string TransitionId;
            internal bool ExtraTurn;
            internal VfxBase TurnStartVfx;
        }

        private static void QueueGuestAuthorityTurnStart(
            NetworkBattleManagerBase manager,
            string resultRequestId,
            bool extraTurn,
            Func<VfxBase> createTurnStartVfx)
        {
            if (manager?.VfxMgr == null || manager.IsBattleEnd ||
                createTurnStartVfx == null)
            {
                return;
            }

            string transitionId = BuildAuthorityTransitionRequestId(manager);
            BeginAuthorityActionCapture(manager.BattleEnemy);
            activeAuthorityExecutionRequestId = transitionId;
            activeAuthorityExecutionStartedUtc = DateTime.UtcNow;
            bool scheduled = false;
            try
            {
                VfxBase turnStartVfx = createTurnStartVfx() ??
                    NullVfx.GetInstance();
                PendingGuestTurnStart pending = new PendingGuestTurnStart
                {
                    Manager = manager,
                    ResultRequestId = resultRequestId,
                    TransitionId = transitionId,
                    ExtraTurn = extraTurn,
                    TurnStartVfx = turnStartVfx
                };
                VfxBase completionVfx = InstantVfx.Create(() =>
                    CompleteGuestAuthorityTurnStart(pending));
                manager.VfxMgr.RegisterSequentialVfx<SequentialVfxPlayer>(
                    SequentialVfxPlayer.Create(new VfxBase[]
                    {
                        turnStartVfx,
                        completionVfx
                    }));
                scheduled = true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    "[P2P] Could not schedule authoritative Guest TurnStart: " +
                    ex);
            }
            finally
            {
                if (!scheduled)
                {
                    activeAuthorityExecutionRequestId = null;
                    activeAuthorityExecutionStartedUtc = DateTime.MinValue;
                    EndAuthorityActionCapture();
                }
            }
        }

        private static void CompleteGuestAuthorityTurnStart(
            PendingGuestTurnStart pending)
        {
            if (pending == null)
            {
                return;
            }

            try
            {
                if (peerDisconnected)
                {
                    Plugin.Logger.LogDebug(
                        "[P2P] Dropped authoritative Guest TurnStart after the " +
                        "peer disconnected: " +
                        (pending.TransitionId ?? "?") + ".");
                    return;
                }
                Dictionary<string, object> turnStart =
                    BuildAuthorityTurnStartData(
                        pending.Manager,
                        pending.ResultRequestId ?? pending.TransitionId,
                        0,
                        pending.ExtraTurn);
                if (turnStart == null)
                {
                    throw new InvalidOperationException(
                        "the native turn-start produced no replay data");
                }
                EnsureAuthorityRandomResultsAreValid(
                    turnStart,
                    pending.ResultRequestId ?? pending.TransitionId);
                DeliverAuthorityResult(turnStart);
                TrySendHostAuthoritativeFinishResult(
                    pending.Manager,
                    pending.ResultRequestId ?? pending.TransitionId);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError(
                    "[P2P] Host authority TurnStart completion failed for " +
                    (pending.TransitionId ?? "?") + ": " + ex);
                SendAuthorityReject(
                    pending.ResultRequestId ?? pending.TransitionId,
                    ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try
                {
                    pending.Manager?.ClearRegisterCardList();
                }
                catch (Exception)
                {
                }
                activeAuthorityExecutionRequestId = null;
                activeAuthorityExecutionStartedUtc = DateTime.MinValue;
                EndAuthorityActionCapture();
            }
        }

        private static void StartAuthorityNextTurn(
            NetworkBattleManagerBase manager,
            string requestId)
        {
            if (Role != P2PRole.Host || manager == null ||
                manager.IsBattleEnd || manager.BattlePlayer == null ||
                manager.BattleEnemy == null)
            {
                return;
            }
            if (IsBattleFinished(manager))
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Skipping authoritative next-turn transition because " +
                    "the turn-end boundary is already final.");
                return;
            }

            manager.ClearRegisterCardList();

            bool guestExtraTurn = manager.BattleEnemy.IsExtraTurn;
            bool hostExtraTurn = manager.BattlePlayer.IsExtraTurn;
            // Match BattleManagerBase.ControlTurnStart exactly: the player
            // whose turn just ended is represented as the first argument, so
            // its ExtraTurn flag has priority even if the other side also has
            // a queued extra turn.  The previous && !hostExtraTurn test sent
            // the transition to Host whenever both sides had ExtraTurn,
            // leaving the Guest one turn behind.
            bool guestNextTurn = guestExtraTurn;
            Wizard.Battle.View.Vfx.VfxBase turnStartVfx = null;
            if (guestNextTurn)
            {
                QueueGuestAuthorityTurnStart(
                    manager,
                    requestId,
                    true,
                    () => manager.ControlTurnStartPlayer());
            }
            else
            {
                turnStartVfx = hostExtraTurn
                    ? manager.ControlTurnStartOpponent()
                    : manager.ControlTurnStartPlayer();
            }

            if (turnStartVfx != null)
            {
                manager.VfxMgr.RegisterSequentialVfx<
                    Wizard.Battle.View.Vfx.VfxBase>(turnStartVfx);
            }
        }

        internal static void RouteAuthorityReceivedCard(
            ReplaceReceivedCard receiver,
            ref BattlePlayerBase battlePlayer)
        {
            if (!IsActive || Role != P2PRole.Guest || receiver == null)
            {
                return;
            }

            bool hasOwnerHint = AuthorityReceivedCardOwnerHints.TryGetValue(
                receiver, out AuthorityReceivedCardOwnerHint ownerHint);
            if (!IsAuthorityLocalReplayActive && !hasOwnerHint)
            {
                // Ordinary opponent packets retain the original client route
                // (BattleEnemy). Only the scalar knownList entries promoted
                // from an authoritative private snapshot may target the
                // Guest's own hand/deck in that path.
                return;
            }

            NetworkBattleManagerBase manager = null;
            try
            {
                if (TryFindInstanceField(
                        receiver.GetType(), "_networkBattleMgr",
                        out FieldInfo managerField))
                {
                    manager = managerField.GetValue(receiver) as
                        NetworkBattleManagerBase;
                }
                if (manager == null)
                {
                    manager = BattleManagerBase.GetIns() as
                        NetworkBattleManagerBase;
                }
            }
            catch (Exception)
            {
                // Keep the native argument when the reflection fallback is
                // unavailable; an incorrect owner is worse than no reroute.
            }

            if (manager == null)
            {
                return;
            }

            if (hasOwnerHint)
            {
                BattlePlayerBase hintedPlayer = ownerHint.GuestOwnsCard
                    ? manager.BattlePlayer
                    : manager.BattleEnemy;
                if (hintedPlayer != null)
                {
                    battlePlayer = hintedPlayer;
                    return;
                }
            }

            int cardIndex = ReadPrivateIntField(receiver, "CardIdx", -1);
            int cardId = ReadPrivateIntField(receiver, "CardId", -1);
            if (cardIndex <= 0 ||
                !TryResolveAuthorityReceivedCardOwner(
                    manager, cardIndex, cardId, out bool guestOwnsCard))
            {
                return;
            }

            BattlePlayerBase resolved = guestOwnsCard
                ? manager.BattlePlayer
                : manager.BattleEnemy;
            if (resolved == null)
            {
                return;
            }

            if (!ReferenceEquals(battlePlayer, resolved))
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Authority replay routed received card: " +
                    "idx=" + cardIndex + ", cardId=" + cardId + ", owner=" +
                    (guestOwnsCard ? "Guest/BattlePlayer" : "Host/BattleEnemy") +
                    ".");
                battlePlayer = resolved;
            }
        }

        internal static void RememberAuthorityReceivedCardOwner(
            ReplaceReceivedCard receiver,
            CardDataModel cardData)
        {
            if (!IsActive || Role != P2PRole.Guest || receiver == null ||
                cardData == null ||
                (!IsAuthorityLocalReplayActive &&
                 !IsCurrentNativePromotedReceivedCard(cardData)))
            {
                return;
            }

            try
            {
                AuthorityReceivedCardOwnerHints.Remove(receiver);
                AuthorityReceivedCardOwnerHints.Add(
                    receiver,
                    new AuthorityReceivedCardOwnerHint
                    {
                        GuestOwnsCard = !cardData.isOpponent
                    });
            }
            catch (Exception)
            {
            }
        }

        private static bool IsCurrentNativePromotedReceivedCard(
            CardDataModel cardData)
        {
            if (cardData == null || cardData.Index <= 0 ||
                currentAuthorityReplayData == null ||
                !currentAuthorityReplayData.TryGetValue(
                    "knownList", out object rawKnownList) ||
                rawKnownList is string ||
                !(rawKnownList is IEnumerable entries))
            {
                return false;
            }

            bool expectedSelf = !cardData.isOpponent;
            foreach (object rawEntry in entries)
            {
                if (!(rawEntry is Dictionary<string, object> entry) ||
                    !ReadAuthorityBool(entry, "p2pNativeHiddenState") ||
                    IsSelfKnownCard(entry) != expectedSelf ||
                    !KnownCardContainsIndex(entry, cardData.Index))
                {
                    continue;
                }

                // Accelerate/crystallize temporarily rewrites CardDataModel's
                // CardId to the original card before replacement. Index plus
                // owner is therefore the stable native identity here.
                return true;
            }
            return false;
        }

        private static bool TryResolveAuthorityReceivedCardOwner(
            NetworkBattleManagerBase manager,
            int cardIndex,
            int cardId,
            out bool guestOwnsCard)
        {
            guestOwnsCard = false;
            int bestScore = int.MinValue;

            if (currentAuthorityReplayData != null)
            {
                // knownList is the normal replacement source; uList carries
                // private/unapproved entries such as hidden draw cards.  A
                // scalar idx+cardId match outranks a grouped idxList match so
                // an index collision between the two players is deterministic.
                foreach (string listName in new[] { "knownList", "uList" })
                {
                    if (!currentAuthorityReplayData.TryGetValue(
                            listName, out object rawList) ||
                        rawList is string || !(rawList is IEnumerable entries))
                    {
                        continue;
                    }

                    foreach (object rawEntry in entries)
                    {
                        if (!(rawEntry is Dictionary<string, object> entry) ||
                            !TryGetAuthorityEntryOwner(
                                entry, cardIndex, cardId,
                                out bool entryGuestOwnsCard,
                                out int score))
                        {
                            continue;
                        }

                        // Prefer knownList over uList only when all other
                        // fields are equal.  The list order is otherwise part
                        // of the native packet semantics and should not cause
                        // a random owner choice.
                        if (string.Equals(listName, "knownList",
                                StringComparison.Ordinal))
                        {
                            score += 1;
                        }
                        if (score > bestScore)
                        {
                            bestScore = score;
                            guestOwnsCard = entryGuestOwnsCard;
                        }
                    }
                }
            }

            if (bestScore != int.MinValue)
            {
                return true;
            }

            // The native conversion has already produced CardDataModel
            // entries by the time ReplaceReceivedCard runs.  Use that as a
            // fallback for packets whose raw list was compacted or omitted by
            // a compatibility path.
            try
            {
                NetworkBattleReceiver.ReceiveData receiveData =
                    manager.networkBattleData?.GetReceiveData();
                if (receiveData == null)
                {
                    return false;
                }

                IEnumerable<CardDataModel> candidates =
                    (receiveData.knownCardList ?? new List<CardDataModel>())
                        .Concat(receiveData.unapprovedList ??
                            new List<CardDataModel>());
                CardDataModel exact = candidates.FirstOrDefault(card =>
                    card != null && card.Index == cardIndex &&
                    cardId > 0 && card.CardId == cardId);
                CardDataModel fallback = exact ?? candidates.FirstOrDefault(card =>
                    card != null && card.Index == cardIndex);
                if (fallback == null)
                {
                    return false;
                }

                guestOwnsCard = !fallback.isOpponent;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryGetAuthorityEntryOwner(
            Dictionary<string, object> entry,
            int cardIndex,
            int cardId,
            out bool guestOwnsCard,
            out int score)
        {
            guestOwnsCard = false;
            score = int.MinValue;
            if (entry == null ||
                !entry.TryGetValue("isSelf", out object rawSelf) ||
                !TryConvertAuthorityInt(rawSelf, out int isSelf) ||
                (isSelf != 0 && isSelf != 1))
            {
                return false;
            }

            bool containsIndex = false;
            bool scalarIndex = false;
            if (entry.TryGetValue("idx", out object rawIndex) &&
                TryConvertAuthorityInt(rawIndex, out int scalar) &&
                scalar == cardIndex)
            {
                containsIndex = true;
                scalarIndex = true;
            }
            if (!containsIndex && entry.TryGetValue(
                    "idxList", out object rawIndices) &&
                rawIndices is IEnumerable indices && !(rawIndices is string))
            {
                foreach (object rawGroupedIndex in indices)
                {
                    if (TryConvertAuthorityInt(rawGroupedIndex,
                            out int groupedIndex) && groupedIndex == cardIndex)
                    {
                        containsIndex = true;
                        break;
                    }
                }
            }
            if (!containsIndex)
            {
                return false;
            }

            score = scalarIndex ? 2 : 1;
            if (entry.TryGetValue("cardId", out object rawCardId) &&
                TryConvertAuthorityInt(rawCardId, out int entryCardId) &&
                entryCardId > 0 && cardId > 0)
            {
                if (entryCardId == cardId)
                {
                    score += 8;
                }
                else
                {
                    // A known card with a different identity is still a
                    // possible grouped entry, but it must not beat an exact
                    // identity match from the other owner.
                    score -= 4;
                }
            }

            guestOwnsCard = isSelf == 1;
            return true;
        }

        private static void TryCheckAuthorityRequestTimeout()
        {
            if (Role != P2PRole.Guest || !guestAuthorityBusy ||
                guestAuthorityRequestSentUtc == DateTime.MinValue ||
                DateTime.UtcNow - guestAuthorityRequestSentUtc <
                    TimeSpan.FromSeconds(BattleStateCheckTimeoutSeconds * 2))
            {
                return;
            }

            string requestId = guestAuthorityRequestId ?? "?";
            if (!string.IsNullOrEmpty(guestAuthorityRequestId))
            {
                completedAuthorityRequestIds.Add(guestAuthorityRequestId);
                TrimAuthorityResultHistory();
            }
            guestAuthorityBusy = false;
            guestAuthorityRequestId = null;
            guestAuthorityRequestAction = null;
            guestAuthorityRequestSentUtc = DateTime.MinValue;
            receivedAuthorityRequestId = null;
            localAuthorityChoiceCardIndexes.Clear();
            LastError = "The Host did not finish the authoritative action in time.";
            Plugin.Logger.LogError(
                "[P2P] Authority request timed out: requestId=" + requestId +
                "; terminating the P2P battle to avoid executing a later action " +
                "against an unknown Host state.");

            // The Host may still be inside the native VFX graph. Unlocking the
            // Guest here would allow a second request to race that graph and
            // permanently diverge the two simulations. Close the session and
            // resolve the disconnect locally instead; the Host receives the
            // close frame and follows the same disconnect-result path.
            try
            {
                SendWire(new P2PWireMessage
                {
                    Type = "close",
                    BattleId = BattleId,
                    Error = LastError
                });
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not send the authority-timeout close frame: " +
                    ex.Message);
            }
            transport?.Stop(false);
            HandlePeerDisconnected(LastError);
        }

        private static void TryCheckAuthorityExecutionTimeout()
        {
            if (Role != P2PRole.Host || peerDisconnected ||
                string.IsNullOrEmpty(activeAuthorityExecutionRequestId) ||
                activeAuthorityExecutionStartedUtc == DateTime.MinValue ||
                DateTime.UtcNow - activeAuthorityExecutionStartedUtc <
                    TimeSpan.FromSeconds(AuthorityExecutionTimeoutSeconds))
            {
                return;
            }

            string requestId = activeAuthorityExecutionRequestId;
            string error =
                "The Host did not finish the authoritative operation in time " +
                "(requestId=" + requestId + ").";
            Plugin.Logger.LogError(
                "[P2P] AUTHORITY EXECUTION STALL: " + error +
                " Closing the battle rather than accepting a later input " +
                "against a partially executed Host state.");

            // Do not clear activeAuthorityExecutionRequestId here. Native VFX
            // callbacks can still emit after this point; keeping the authority
            // marker set suppresses those late packets until the completion
            // callback restores the original receive context. The disconnect
            // path resolves the local result immediately instead.
            try
            {
                SendWire(new P2PWireMessage
                {
                    Type = "close",
                    BattleId = BattleId,
                    Error = error
                });
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not send the Host authority-stall close frame: " +
                    ex.Message);
            }
            transport?.Stop(false);
            HandlePeerDisconnected(error);
        }

        private static void DiscardReceivedPlayerHistoryState(
            Dictionary<string, object> data)
        {
            if (data == null ||
                !data.TryGetValue(PlayerHistoryStateKey, out object rawState) ||
                !(rawState is Dictionary<string, object> state) ||
                !TryGetStateInt(state, "owner", out int owner) ||
                !TryGetStateInt(state, "revision", out int revision))
            {
                return;
            }

            ReceivedPlayerHistoryStates.Remove(
                PlayerHistoryStateKeyFor(owner, revision));
        }

        internal static void RejectReceivedAuthoritativeSkillTargets()
        {
            if (!IsActive)
            {
                return;
            }
            if (currentInjectedAuthoritativeSkillTargetBatch != null)
            {
                currentInjectedAuthoritativeSkillTargetBatch.Rejected = true;
                currentInjectedAuthoritativeSkillTargetBatch.ReadyForCleanup = true;
            }
            if (currentInjectedAuthoritativeSkillEvaluationBatch != null)
            {
                currentInjectedAuthoritativeSkillEvaluationBatch.Rejected = true;
                currentInjectedAuthoritativeSkillEvaluationBatch.ReadyForCleanup = true;
            }
        }

        private static void ActivateNextAuthoritativeSkillTargetBatch()
        {
            if (activeAuthoritativeSkillTargetBatch != null ||
                PendingAuthoritativeSkillTargetBatches.Count == 0)
            {
                return;
            }
            activeAuthoritativeSkillTargetBatch =
                PendingAuthoritativeSkillTargetBatches.Dequeue();
            receivedAuthoritativeActionActive = true;
        }

        private static void ActivateNextAuthoritativeSkillEvaluationBatch()
        {
            if (activeAuthoritativeSkillEvaluationBatch != null ||
                PendingAuthoritativeSkillEvaluationBatches.Count == 0)
            {
                return;
            }
            activeAuthoritativeSkillEvaluationBatch =
                PendingAuthoritativeSkillEvaluationBatches.Dequeue();
            receivedAuthoritativeSkillEvaluationActive = true;
        }

        private static void TryClearConsumedAuthoritativeSkillTargets()
        {
            if (!receivedAuthoritativeActionActive ||
                activeAuthoritativeSkillTargetBatch == null ||
                !activeAuthoritativeSkillTargetBatch.ReadyForCleanup ||
                processingReceivedBattleAction ||
                receivedBattleActionInjectionPending ||
                receivedBattleActionPendingUntilVfx ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.VfxMgr == null || !manager.VfxMgr.IsEnd)
            {
                return;
            }

            if (activeAuthoritativeSkillTargetBatch.Rejected)
            {
                Plugin.Logger.LogWarning(
                    "[P2P] Discarded authoritative random target results for a " +
                    "native action that was rejected by the receiver: actionId=" +
                    activeAuthoritativeSkillTargetBatch.ActionId + ", uri=" +
                    activeAuthoritativeSkillTargetBatch.Uri + ".");
            }
            else if (activeAuthoritativeSkillTargetBatch.Entries.Count > 0)
            {
                Plugin.Logger.LogWarning(
                    $"[P2P] Unconsumed authoritative random targets: actionId=" +
                    $"{activeAuthoritativeSkillTargetBatch.ActionId}, uri=" +
                    $"{activeAuthoritativeSkillTargetBatch.Uri}, count=" +
                    activeAuthoritativeSkillTargetBatch.Entries.Count + ".");
            }
            activeAuthoritativeSkillTargetBatch = null;
            receivedAuthoritativeActionActive = false;
            ActivateNextAuthoritativeSkillTargetBatch();
        }

        private static void TryClearConsumedAuthoritativeSkillEvaluations()
        {
            if (!receivedAuthoritativeSkillEvaluationActive ||
                activeAuthoritativeSkillEvaluationBatch == null ||
                !activeAuthoritativeSkillEvaluationBatch.ReadyForCleanup ||
                processingReceivedBattleAction ||
                receivedBattleActionInjectionPending ||
                receivedBattleActionPendingUntilVfx ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.VfxMgr == null || !manager.VfxMgr.IsEnd)
            {
                return;
            }

            if (activeAuthoritativeSkillEvaluationBatch.Rejected)
            {
                Plugin.Logger.LogWarning(
                    "[P2P] Discarded authoritative private skill evaluations for " +
                    "a native action that was rejected by the receiver: actionId=" +
                    activeAuthoritativeSkillEvaluationBatch.ActionId + ", uri=" +
                    activeAuthoritativeSkillEvaluationBatch.Uri + ".");
            }
            else if (activeAuthoritativeSkillEvaluationBatch.Entries.Count > 0)
            {
                Plugin.Logger.LogWarning(
                    $"[P2P] Unconsumed authoritative private skill evaluations: " +
                    $"actionId={activeAuthoritativeSkillEvaluationBatch.ActionId}, " +
                    $"uri={activeAuthoritativeSkillEvaluationBatch.Uri}, count=" +
                    activeAuthoritativeSkillEvaluationBatch.Entries.Count + ".");
            }
            activeAuthoritativeSkillEvaluationBatch = null;
            receivedAuthoritativeSkillEvaluationActive = false;
            ActivateNextAuthoritativeSkillEvaluationBatch();
        }

        private static bool TryConsumeAuthoritativeSkillTargets(
            SkillBase skill,
            out List<BattleCardBase> targets,
            out Dictionary<int, BattleCardBase> independentTargets,
            out string diagnostic)
        {
            targets = null;
            independentTargets = new Dictionary<int, BattleCardBase>();
            diagnostic = string.Empty;
            if (activeAuthoritativeSkillTargetBatch == null ||
                activeAuthoritativeSkillTargetBatch.Entries.Count == 0)
            {
                return false;
            }

            int entryIndex = FindAuthoritativeSkillTargetEntry(skill,
                out bool usedFallback);
            if (entryIndex < 0)
            {
                diagnostic =
                    $"no source result matched card idx={skill.SkillPrm.ownerCard.Index}, " +
                    $"cardId={skill.SkillPrm.ownerCard.CardId}, " +
                    $"skill={skill.GetType().Name}, " +
                    $"pending={activeAuthoritativeSkillTargetBatch.Entries.Count}";
                return false;
            }

            if (usedFallback)
            {
                Plugin.Logger.LogWarning(
                    $"[P2P] Matched authoritative random target by " +
                    $"owner/index fallback: card idx={skill.SkillPrm.ownerCard.Index}, " +
                    $"skill={skill.GetType().Name}.");
            }

            Dictionary<string, object> selected =
                activeAuthoritativeSkillTargetBatch.Entries[entryIndex];
            if (!selected.TryGetValue("targets", out object rawReferences) ||
                rawReferences is string || !(rawReferences is IEnumerable references))
            {
                diagnostic = "the matched source result had no target array";
                activeAuthoritativeSkillTargetBatch.Entries.RemoveAt(entryIndex);
                return false;
            }

            List<BattleCardBase> resolved = new List<BattleCardBase>();
            if (!TryResolveAuthoritativeReferences(
                    references, resolved, out diagnostic))
            {
                return false;
            }

            if (selected.TryGetValue("independent", out object rawIndependent) &&
                rawIndependent is IEnumerable independentEntries &&
                !(rawIndependent is string))
            {
                foreach (object rawEntry in independentEntries)
                {
                    if (!(rawEntry is Dictionary<string, object> entry) ||
                        !TryGetStateInt(entry, "slot", out int slot) ||
                        !entry.TryGetValue("card", out object rawReference) ||
                        !(rawReference is Dictionary<string, object> reference))
                    {
                        diagnostic = "an independent source target entry was malformed";
                        return false;
                    }
                    List<BattleCardBase> one = new List<BattleCardBase>();
                    if (!TryResolveAuthoritativeReferences(
                            new[] { (object)reference }, one, out diagnostic))
                    {
                        return false;
                    }
                    if (one.Count > 0)
                    {
                        independentTargets[slot] = one[0];
                    }
                }
            }

            activeAuthoritativeSkillTargetBatch.Entries.RemoveAt(entryIndex);
            targets = resolved;
            return true;
        }

        private static int FindAuthoritativeSkillTargetEntry(
            SkillBase skill,
            out bool usedFallback)
        {
            return FindAuthoritativeSkillEntry(
                activeAuthoritativeSkillTargetBatch?.Entries,
                skill,
                out usedFallback);
        }

        private static int FindAuthoritativeSkillEntry(
            List<Dictionary<string, object>> entries,
            SkillBase skill,
            out bool usedFallback)
        {
            usedFallback = false;
            if (entries == null || skill == null)
            {
                return -1;
            }

            int strict = entries.FindIndex(
                entry => AuthoritativeSkillTargetMatches(entry, skill));
            if (strict >= 0)
            {
                return strict;
            }

            BattleCardBase owner = skill.SkillPrm?.ownerCard;
            if (owner == null)
            {
                return -1;
            }
            bool localOwnerIsHost = Role == P2PRole.Host;
            bool ownerIsHost = owner.IsPlayer
                ? localOwnerIsHost
                : !localOwnerIsHost;
            int expectedOwner = ownerIsHost ? 1 : 0;
            int ownerIndex = owner.Index;
            int ownerCardId = owner.CardId;
            string skillType = skill.GetType().FullName ?? skill.GetType().Name;

            // Fusion metamorphose and attached/copied skills can rebuild the
            // concrete SkillBase and publish counters between the source and
            // receiver. The card's absolute owner/index remains stable for the
            // duration of this ordered action, so use it as the deterministic
            // fallback and keep FIFO ordering for repeated skills.
            int fallback = entries.FindIndex(
                entry =>
                TryGetStateInt(entry, "ownerIdx", out int idx) && idx == ownerIndex &&
                (!TryGetStateInt(entry, "owner", out int side) || side == expectedOwner) &&
                (!TryGetStateInt(entry, "ownerCardId", out int id) ||
                    id <= 0 || ownerCardId <= 0 || id == ownerCardId) &&
                (!entry.TryGetValue("skillType", out object rawType) ||
                    string.Equals(rawType?.ToString(), skillType,
                        StringComparison.Ordinal)));
            if (fallback < 0)
            {
                // Last-resort owner/index match. This is only possible inside the
                // current action batch and is preferable to executing a random
                // selection locally and silently diverging.
                fallback = entries.FindIndex(
                    entry =>
                    TryGetStateInt(entry, "ownerIdx", out int idx) &&
                        idx == ownerIndex &&
                    (!TryGetStateInt(entry, "owner", out int side) ||
                        side == expectedOwner));
            }
            if (fallback >= 0)
            {
                usedFallback = true;
            }
            return fallback;
        }

        private static bool TryResolveAuthoritativeReferences(
            IEnumerable references,
            List<BattleCardBase> resolved,
            out string diagnostic)
        {
            diagnostic = string.Empty;
            if (references == null || resolved == null)
            {
                return true;
            }
            foreach (object rawReference in references)
            {
                if (!(rawReference is Dictionary<string, object> reference))
                {
                    diagnostic = "a source target reference was malformed";
                    return false;
                }
                BattleCardBase card = ResolveCardReference(reference);
                if (card == null)
                {
                    diagnostic =
                        "source target " + FormatCardReference(reference) +
                        " is not present in the synchronized battle history";
                    return false;
                }
                if (TryGetStateInt(reference, "cardId", out int cardId) &&
                    cardId > 0 && card.CardId != cardId)
                {
                    diagnostic =
                        $"source target {FormatCardReference(reference)} resolved " +
                        $"to cardId={card.CardId}";
                    return false;
                }
                resolved.Add(card);
            }
            return true;
        }

        private static bool AuthoritativeSkillTargetMatches(
            Dictionary<string, object> entry,
            SkillBase skill)
        {
            BattleCardBase owner = skill?.SkillPrm?.ownerCard;
            if (entry == null || owner == null ||
                !TryGetStateInt(entry, "ownerIdx", out int ownerIndex) ||
                ownerIndex != owner.Index)
            {
                return false;
            }
            if (TryGetStateInt(entry, "ownerCardId", out int ownerCardId) &&
                ownerCardId > 0 && owner.CardId != ownerCardId)
            {
                return false;
            }
            bool localOwnerIsHost = Role == P2PRole.Host;
            bool ownerIsHost = owner.IsPlayer
                ? localOwnerIsHost
                : !localOwnerIsHost;
            if (TryGetStateInt(entry, "owner", out int expectedOwner) &&
                (expectedOwner == 0 || expectedOwner == 1) &&
                expectedOwner != (ownerIsHost ? 1 : 0))
            {
                return false;
            }
            int skillIndex = GetNetworkSkillIndex(skill);
            int published = NetworkBattleGenericTool.GetPublishSkillCount(skill);
            int movement = GetSkillMovement(skill);
            bool indexMatches =
                !TryGetStateInt(entry, "skillIndex", out int expectedIndex) ||
                expectedIndex < 0 || expectedIndex == skillIndex;
            bool publishedMatches =
                !TryGetStateInt(entry, "published", out int expectedPublished) ||
                expectedPublished < 0 || published < 0 ||
                expectedPublished == published;
            bool movementMatches =
                !TryGetStateInt(entry, "movement", out int expectedMovement) ||
                expectedMovement < 0 || movement < 0 ||
                expectedMovement == movement;
            bool typeMatches =
                !entry.TryGetValue("skillType", out object rawSkillType) ||
                string.Equals(rawSkillType?.ToString(),
                    skill.GetType().FullName ?? skill.GetType().Name,
                    StringComparison.Ordinal);
            // Attached and copied skills can have different concrete network
            // wrapper types on the two clients. Stable native identifiers take
            // precedence; type is only a fallback when those identifiers are all
            // unavailable.
            bool hasStableIdentity =
                (TryGetStateInt(entry, "skillIndex", out int storedIndex) &&
                    storedIndex >= 0 && skillIndex >= 0) ||
                (TryGetStateInt(entry, "published", out int storedPublished) &&
                    storedPublished >= 0 && published >= 0) ||
                (TryGetStateInt(entry, "movement", out int storedMovement) &&
                    storedMovement >= 0 && movement >= 0);
            return indexMatches && publishedMatches && movementMatches &&
                (hasStableIdentity || typeMatches);
        }

        private static int GetNetworkSkillIndex(SkillBase skill)
        {
            try
            {
                return NetworkBattleGenericTool.GetSkillIndex(skill);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static int GetSkillMovement(SkillBase skill)
        {
            try
            {
                return skill?._executionInfoCreator is NetworkExecutionInfoCreator creator
                    ? creator.GetSkillMovementNum()
                    : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static string CardReferenceDiagnostic(BattleCardBase card)
        {
            if (card == null)
            {
                return "null";
            }
            return (card.IsPlayer ? "self:" : "opponent:") +
                card.Index.ToString(CultureInfo.InvariantCulture) + ":" +
                card.CardId.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatCardReference(
            Dictionary<string, object> reference)
        {
            string owner = reference != null &&
                TryGetStateInt(reference, "owner", out int ownerValue)
                    ? ownerValue.ToString(CultureInfo.InvariantCulture)
                    : "?";
            string index = reference != null &&
                TryGetStateInt(reference, "idx", out int indexValue)
                    ? indexValue.ToString(CultureInfo.InvariantCulture)
                    : "?";
            string cardId = reference != null &&
                TryGetStateInt(reference, "cardId", out int cardIdValue)
                    ? cardIdValue.ToString(CultureInfo.InvariantCulture)
                    : "?";
            return $"owner={owner}/idx={index}/cardId={cardId}";
        }

        private static void TrySendInitialPrivateStateSnapshot()
        {
            if (!IsActive || !battleStartReceived || localPrivateStateSent ||
                transport == null ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.BattlePlayer == null)
            {
                return;
            }

            List<Dictionary<string, object>> cards = new List<Dictionary<string, object>>();
            HashSet<int> initializedDeckIndices = new HashSet<int>();
            HashSet<int> capturedIndices = new HashSet<int>();
            try
            {
                if (manager.BattlePlayer.AllCards != null)
                {
                    foreach (BattleCardBase card in manager.BattlePlayer.AllCards)
                    {
                        if (card != null && card.Index > 0 && card.CardId > 0)
                        {
                            initializedDeckIndices.Add(card.Index);
                        }

                        // AllCards is the stable deck identity table.  During
                        // the opening deal/mulligan animation some cards may
                        // temporarily be in a staging list instead of
                        // HandCardList/DeckCardList; scanning only those two
                        // zones can permanently publish an incomplete baseline.
                        if (card != null && card.Index > 0 && card.CardId > 0 &&
                            capturedIndices.Add(card.Index))
                        {
                            cards.Add(CreateHiddenCardState(card));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not capture the initial private state: " + ex.Message);
                return;
            }

            int expectedCardCount = LocalDeck?.Cards?.Count ?? 0;
            if (expectedCardCount <= 0 || cards.Count == 0 ||
                Enumerable.Range(1, expectedCardCount)
                    .Any(index => !initializedDeckIndices.Contains(index)))
            {
                // BattlePlayer becomes visible before all deck and hand cards
                // have necessarily been created. Sending at that point would
                // permanently establish an incomplete one-shot baseline.
                return;
            }

            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["owner"] = Role == P2PRole.Host ? 1 : 0,
                ["cards"] = cards.Select(card => (object)card).ToList()
            };
            if (Role == P2PRole.Host)
            {
                // The Host's own baseline does not traverse the wire before it
                // is needed for the first server response. Record it in the
                // same authoritative cache used for the Guest baseline.
                P2PAuthoritativeServer.RememberPrivateStateSnapshot(payload);
            }
            if (!SendWire(new P2PWireMessage
            {
                Type = "private_state",
                ViewerId = LocalProfile?.ViewerId ?? P2PIdentity.ViewerId,
                BattleId = BattleId,
                Data = payload
            }))
            {
                return;
            }

            localPrivateStateSent = true;
            foreach (Dictionary<string, object> card in cards)
            {
                if (!TryGetStateInt(card, "idx", out int index) || index <= 0)
                {
                    continue;
                }
                LocalHiddenCardStates[index] = P2PJson.CloneDictionary(card);
                LocalHiddenCardStateSignatures[index] =
                    JsonConvert.SerializeObject(card, P2PJson.Settings);
                int owner = Role == P2PRole.Host ? 1 : 0;
                if (!authorityKnownPrivateIndicesByOwner.TryGetValue(
                        owner, out HashSet<int> knownIndices))
                {
                    knownIndices = new HashSet<int>();
                    authorityKnownPrivateIndicesByOwner[owner] = knownIndices;
                }
                knownIndices.Add(index);
                authorityPrivateStateSignatures[HiddenStateKey(
                        owner == 1, index)] =
                    JsonConvert.SerializeObject(card, P2PJson.Settings);
                authorityPrivateStates[HiddenStateKey(owner == 1, index)] =
                    P2PJson.CloneDictionary(card);
            }
            Plugin.Logger.LogInfo(
                $"[P2P] Sent initial private state ({cards.Count} cards, " +
                $"owner={(Role == P2PRole.Host ? "Host" : "Guest")}); " +
                $"incremental snapshots active={IsPrivateStateSyncActive}.");
        }

        private static void RememberReceivedPrivateStateSnapshot(
            Dictionary<string, object> payload)
        {
            if (!IsActive || payload == null ||
                !TryGetStateInt(payload, "owner", out int owner) ||
                (owner != 0 && owner != 1) ||
                owner == (Role == P2PRole.Host ? 1 : 0))
            {
                return;
            }

            bool ownerIsHost = owner == 1;
            if (payload.TryGetValue("cards", out object rawCards) &&
                rawCards is IEnumerable cards && !(rawCards is string))
            {
                foreach (object rawCard in cards)
                {
                    if (!(rawCard is Dictionary<string, object> card) ||
                        !TryGetStateInt(card, "idx", out int index) || index <= 0)
                    {
                        continue;
                    }
                    StoreReceivedHiddenCardState(ownerIsHost, card);
                    NativeBaselineHiddenCardStateKeys.Add(
                        HiddenStateKey(ownerIsHost, index));
                    if (!authorityKnownPrivateIndicesByOwner.TryGetValue(
                            owner, out HashSet<int> knownIndices))
                    {
                        knownIndices = new HashSet<int>();
                        authorityKnownPrivateIndicesByOwner[owner] = knownIndices;
                    }
                    knownIndices.Add(index);
                    authorityPrivateStateSignatures[HiddenStateKey(
                            ownerIsHost, index)] =
                        JsonConvert.SerializeObject(card, P2PJson.Settings);
                    authorityPrivateStates[HiddenStateKey(ownerIsHost, index)] =
                        P2PJson.CloneDictionary(card);
                }
            }

            remotePrivateStateReceived = true;
            Plugin.Logger.LogInfo(
                $"[P2P] Received initial private state from {SideName(ownerIsHost)}; " +
                $"incremental snapshots active={IsPrivateStateSyncActive}.");
        }

        // The official server places the identity of cards that will be moved
        // by an operation in knownList. NetworkBattleData consumes that list
        // before OperateReceive.StartOperate; this lets ReplaceReceivedCard
        // replace a dummy while it is still in DeckCardList, rather than after
        // its hand view has retained the dummy object. Authority snapshots are
        // absolute-owner data, so convert them to the current receiver's
        // native relative isSelf representation at this boundary only.
        private static void PromoteReceivedHiddenCardStatesIntoNativeKnownList(
            Dictionary<string, object> data)
        {
            if (!IsActive || data == null)
            {
                return;
            }

            List<object> knownList = GetOrCreateKnownList(data);
            HashSet<string> promoted = new HashSet<string>(
                StringComparer.Ordinal);
            int promotedCount = 0;

            Action<int, IEnumerable> promoteCards = (owner, cards) =>
            {
                if ((owner != 0 && owner != 1) || cards == null)
                {
                    return;
                }

                foreach (object rawCard in cards)
                {
                    if (!(rawCard is Dictionary<string, object> card) ||
                        !TryGetStateInt(card, "idx", out int index) ||
                        index <= 0 ||
                        !TryGetStateInt(card, "cardId", out int cardId) ||
                        cardId <= 0)
                    {
                        continue;
                    }

                    // A delta is merged against the receiver's last complete
                    // state before it reaches the native knownList boundary.
                    // This keeps the original CardDataModel construction
                    // unchanged while allowing the wire payload to omit
                    // untouched P2P-only fields.
                    Dictionary<string, object> effectiveCard =
                        MergeIncomingHiddenCardState(owner == 1, index, card);
                    if (!TryGetStateInt(effectiveCard, "cardId",
                            out int effectiveCardId) || effectiveCardId <= 0)
                    {
                        continue;
                    }

                    string key = HiddenStateKey(owner == 1, index);
                    if (!promoted.Add(key))
                    {
                        continue;
                    }

                    if (PromoteReceivedHiddenCardStateToNativeKnownList(
                            knownList, owner, effectiveCard))
                    {
                        NativePromotedReceivedHiddenCardStateSignatures[key] =
                            JsonConvert.SerializeObject(effectiveCard,
                                P2PJson.Settings);
                        promotedCount++;
                    }
                }
            };

            bool hasAuthoritySnapshots = false;
            if (data.TryGetValue(P2PBattleProtocol.AuthorityHiddenStatesKey,
                    out object rawSnapshots) && rawSnapshots is IEnumerable snapshots &&
                !(rawSnapshots is string))
            {
                hasAuthoritySnapshots = true;
                foreach (object rawSnapshot in snapshots)
                {
                    if (!(rawSnapshot is Dictionary<string, object> snapshot) ||
                        !TryGetStateInt(snapshot, "owner", out int owner) ||
                        !snapshot.TryGetValue("cards", out object rawCards) ||
                        rawCards is string || !(rawCards is IEnumerable cards))
                    {
                        continue;
                    }

                    promoteCards(owner, cards);
                }
            }

            // Current peers include this legacy field together with the
            // two-owner extension. Do not process it twice, but keep support
            // for a peer that only knows the original single-owner form.
            if (!hasAuthoritySnapshots &&
                data.TryGetValue("p2pHiddenOwner", out object rawOwner) &&
                TryConvertAuthorityInt(rawOwner, out int legacyOwner) &&
                data.TryGetValue("p2pHiddenCards", out object rawLegacyCards) &&
                rawLegacyCards is IEnumerable legacyCards &&
                !(rawLegacyCards is string))
            {
                promoteCards(legacyOwner, legacyCards);
            }

            if (promotedCount > 0)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Promoted " + promotedCount +
                    " authoritative hidden card identity/state entry(s) into " +
                    "native knownList before receive operation construction.");
            }
        }

        private static bool PromoteReceivedHiddenCardStateToNativeKnownList(
            List<object> knownList,
            int absoluteOwner,
            Dictionary<string, object> state)
        {
            if (knownList == null || state == null ||
                !TryGetStateInt(state, "idx", out int index) || index <= 0 ||
                !TryGetStateInt(state, "cardId", out int cardId) || cardId <= 0)
            {
                return false;
            }

            // Convert absolute ownership (Host=1, Guest=0) to the current
            // receiver's native isSelf convention. This is the same
            // perspective conversion used by NetworkBattleReceiver for the
            // original server response.
            int localOwner = Role == P2PRole.Host ? 1 : 0;
            bool isSelf = absoluteOwner == localOwner;
            Dictionary<string, object> canonical = null;
            List<Dictionary<string, object>> matches = knownList
                .OfType<Dictionary<string, object>>()
                .Where(entry => IsSelfKnownCard(entry) == isSelf &&
                    KnownCardContainsIndex(entry, index))
                .ToList();

            foreach (Dictionary<string, object> entry in matches)
            {
                if (TryGetStateInt(entry, "idx", out int scalarIndex) &&
                    scalarIndex == index)
                {
                    if (canonical == null)
                    {
                        canonical = entry;
                    }
                    else
                    {
                        knownList.Remove(entry);
                    }
                    continue;
                }

                if (!entry.TryGetValue("idxList", out object rawIndexes) ||
                    rawIndexes is string || !(rawIndexes is IEnumerable indexes))
                {
                    continue;
                }

                List<object> remaining = new List<object>();
                foreach (object rawIndex in indexes)
                {
                    if (!TryConvertAuthorityInt(rawIndex, out int groupedIndex) ||
                        groupedIndex != index)
                    {
                        remaining.Add(rawIndex);
                    }
                }
                if (remaining.Count == 0)
                {
                    knownList.Remove(entry);
                }
                else
                {
                    entry["idxList"] = remaining;
                }
            }

            if (canonical == null)
            {
                // A post-action snapshot may describe a modifier on an
                // existing private card. It is not permission to create a new
                // native identity entry after the action has started. Every
                // movement/creation identity must already be present in the
                // original knownList/orderList/uList data produced by Host.
                return false;
            }

            // A scalar entry is required: ReplaceReceivedCard searches a
            // single index. Leaving idxList on the canonical object would
            // recreate the duplicate-index SingleOrDefault failure.
            canonical.Remove("idxList");
            canonical["idx"] = index;
            canonical["cardId"] = cardId;
            canonical["isSelf"] = isSelf ? 1 : 0;
            canonical["is_open"] = 1;
            canonical["p2pNativeHiddenState"] = 1;

            // Copy only fields understood by CardDataModel. Everything else is
            // intentionally kept in the deferred P2P snapshot so the native
            // action cannot observe its post-action generic state early.
            foreach (string field in NativeKnownCardStateFields)
            {
                if (state.TryGetValue(field, out object value))
                {
                    canonical[field] = P2PJson.CloneValue(value);
                }
                else
                {
                    canonical.Remove(field);
                }
            }
            return true;
        }

        private static readonly string[] NativeKnownCardStateFields =
        {
            "cost",
            "spellboost",
            "setAtk",
            "setLife",
            "setChantCount",
            "unionburst",
            "skyboundArt",
            "clan",
            "tribe",
            "attachTarget",
            "fusion"
        };

        private static void RememberReceivedHiddenCardStates(
            Dictionary<string, object> data,
            bool deferForCurrentAction)
        {
            if (data == null)
            {
                return;
            }

            // Authority results can contain changed private cards for both
            // absolute owners.  Process this extension first; the legacy
            // single-owner fields below are still accepted for older messages.
            RememberReceivedAuthorityHiddenStates(data, deferForCurrentAction);

            if (!data.TryGetValue("p2pHiddenOwner", out object rawOwner))
            {
                return;
            }

            int owner;
            try
            {
                owner = Convert.ToInt32(rawOwner, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return;
            }

            if (owner != 0 && owner != 1)
            {
                return;
            }

            bool ownerIsHost = owner == 1;

            // A tombstone means the card has left a hidden zone. Do not consume
            // its cached state here: BeforeSettingReceiveData still needs that
            // state while replacing the card for this very action. Cleanup runs
            // from the ReceivedMessage postfix after native processing succeeds.

            if (!data.TryGetValue("p2pHiddenCards", out object rawCards) ||
                rawCards is string || !(rawCards is IEnumerable cards))
            {
                return;
            }

            foreach (object rawCard in cards)
            {
                Dictionary<string, object> card =
                    rawCard as Dictionary<string, object>;
                if (card == null || !TryGetStateInt(card, "idx", out int index) ||
                    index <= 0)
                {
                    continue;
                }

                StoreReceivedHiddenCardState(
                    ownerIsHost,
                    card,
                    deferForCurrentAction &&
                    !ContainsReceivedHiddenRemoval(data, index));
            }
        }

        private static void RememberReceivedAuthorityHiddenStates(
            Dictionary<string, object> data,
            bool deferForCurrentAction)
        {
            if (!data.TryGetValue(
                    P2PBattleProtocol.AuthorityHiddenStatesKey,
                    out object rawSnapshots) ||
                rawSnapshots is string || !(rawSnapshots is IEnumerable snapshots))
            {
                return;
            }

            foreach (object rawSnapshot in snapshots)
            {
                if (!(rawSnapshot is Dictionary<string, object> snapshot) ||
                    !TryGetStateInt(snapshot, "owner", out int owner) ||
                    (owner != 0 && owner != 1))
                {
                    continue;
                }

                HashSet<int> removed = new HashSet<int>();
                if (snapshot.TryGetValue("removed", out object rawRemoved) &&
                    rawRemoved is IEnumerable removedValues &&
                    !(rawRemoved is string))
                {
                    foreach (object rawIndex in removedValues)
                    {
                        if (TryConvertAuthorityInt(rawIndex, out int index) &&
                            index > 0)
                        {
                            removed.Add(index);
                        }
                    }
                }

                if (snapshot.TryGetValue("cards", out object rawCards) &&
                    rawCards is IEnumerable cards && !(rawCards is string))
                {
                    foreach (object rawCard in cards)
                    {
                        if (!(rawCard is Dictionary<string, object> card) ||
                            !TryGetStateInt(card, "idx", out int index) ||
                            index <= 0)
                        {
                            continue;
                        }
                        StoreReceivedHiddenCardState(
                            owner == 1,
                            card,
                            deferForCurrentAction && !removed.Contains(index));
                    }
                }
            }
        }

        private static bool ContainsReceivedHiddenRemoval(
            Dictionary<string, object> data,
            int index)
        {
            if (data == null || index <= 0 ||
                !data.TryGetValue("p2pHiddenRemoved", out object rawRemoved) ||
                rawRemoved is string || !(rawRemoved is IEnumerable removed))
            {
                return false;
            }

            foreach (object rawIndex in removed)
            {
                if (TryGetStateInt(
                        new Dictionary<string, object> { ["value"] = rawIndex },
                        "value",
                        out int removedIndex) && removedIndex == index)
                {
                    return true;
                }
            }
            return false;
        }

        private static void StoreReceivedHiddenCardState(
            bool ownerIsHost,
            Dictionary<string, object> card,
            bool deferForCurrentAction = false)
        {
            if (card == null || !TryGetStateInt(card, "idx", out int index) ||
                index <= 0)
            {
                return;
            }
            string key = HiddenStateKey(ownerIsHost, index);
            Dictionary<string, object> clone = MergeIncomingHiddenCardState(
                ownerIsHost, index, card);
            if (deferForCurrentAction)
            {
                // This is action-scoped post-state, not the initial baseline.
                // It must never become eligible for a later compatibility
                // replacement of the native card object.
                NativeBaselineHiddenCardStateKeys.Remove(key);
                PendingReceivedHiddenCardStates[key] = clone;
                PostActionHiddenCardStateKeys.Add(key);
                return;
            }

            PendingReceivedHiddenCardStates.Remove(key);
            PostActionHiddenCardStateKeys.Remove(key);
            ReceivedHiddenCardStates[key] = clone;
            ReceivedHiddenCardStateSignatures[key] =
                JsonConvert.SerializeObject(clone, P2PJson.Settings);
        }

        private static bool IsHiddenCardStateDelta(
            Dictionary<string, object> state)
        {
            return state != null &&
                TryGetStateInt(state, HiddenCardStateDeltaKey,
                    out int delta) && delta != 0;
        }

        private static Dictionary<string, object> MergeIncomingHiddenCardState(
            bool ownerIsHost,
            int index,
            Dictionary<string, object> source)
        {
            if (source == null)
            {
                return new Dictionary<string, object>();
            }

            Dictionary<string, object> previous = null;
            string key = HiddenStateKey(ownerIsHost, index);
            if (IsHiddenCardStateDelta(source))
            {
                if (!PendingReceivedHiddenCardStates.TryGetValue(key,
                        out previous))
                {
                    ReceivedHiddenCardStates.TryGetValue(key, out previous);
                }
                if (previous == null)
                {
                    Plugin.Logger.LogWarning(
                        $"[P2P] Received a hidden-card delta without a " +
                        $"baseline: owner={SideName(ownerIsHost)}, idx={index}.");
                }
            }

            Dictionary<string, object> merged = previous == null
                ? new Dictionary<string, object>()
                : P2PJson.CloneDictionary(previous);
            foreach (KeyValuePair<string, object> pair in source)
            {
                if (string.Equals(pair.Key, HiddenCardStateDeltaKey,
                        StringComparison.Ordinal) ||
                    string.Equals(pair.Key, HiddenCardStateRemovedFieldsKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                merged[pair.Key] = P2PJson.CloneValue(pair.Value);
            }

            if (source.TryGetValue(HiddenCardStateRemovedFieldsKey,
                    out object rawRemoved) && rawRemoved is IEnumerable removed &&
                !(rawRemoved is string))
            {
                foreach (object rawField in removed)
                {
                    string field = rawField?.ToString();
                    if (string.IsNullOrEmpty(field) ||
                        string.Equals(field, "idx", StringComparison.Ordinal) ||
                        string.Equals(field, "cardId", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    merged.Remove(field);
                }
            }

            merged["idx"] = index;
            if (!merged.ContainsKey("cardId") &&
                source.TryGetValue("cardId", out object rawCardId))
            {
                merged["cardId"] = P2PJson.CloneValue(rawCardId);
            }
            return merged;
        }

        private static Dictionary<string, object> CreateHiddenCardDelta(
            Dictionary<string, object> previous,
            Dictionary<string, object> current)
        {
            if (current == null)
            {
                return null;
            }
            if (previous == null)
            {
                return P2PJson.CloneDictionary(current);
            }

            Dictionary<string, object> delta = new Dictionary<string, object>
            {
                ["idx"] = current.TryGetValue("idx", out object rawIndex)
                    ? P2PJson.CloneValue(rawIndex)
                    : 0,
                ["cardId"] = current.TryGetValue("cardId", out object rawCardId)
                    ? P2PJson.CloneValue(rawCardId)
                    : 0,
                [HiddenCardStateDeltaKey] = 1
            };
            bool changed = false;
            foreach (KeyValuePair<string, object> pair in current)
            {
                if (string.Equals(pair.Key, "idx", StringComparison.Ordinal) ||
                    string.Equals(pair.Key, HiddenCardStateDeltaKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                if (!previous.TryGetValue(pair.Key, out object oldValue) ||
                    !StateValuesEqual(oldValue, pair.Value))
                {
                    delta[pair.Key] = P2PJson.CloneValue(pair.Value);
                    changed = true;
                }
            }

            List<object> removed = previous.Keys
                .Where(key => !current.ContainsKey(key) &&
                    !string.Equals(key, "idx", StringComparison.Ordinal))
                .OrderBy(key => key, StringComparer.Ordinal)
                .Select(key => (object)key)
                .ToList();
            if (removed.Count > 0)
            {
                delta[HiddenCardStateRemovedFieldsKey] = removed;
            }

            // cardId is mandatory for the native identity boundary even when
            // every P2P-only field stayed unchanged.
            return changed || removed.Count > 0 ? delta : null;
        }

        private static bool StateValuesEqual(object left, object right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }
            if (left == null || right == null)
            {
                return false;
            }
            try
            {
                return string.Equals(
                    JsonConvert.SerializeObject(left, P2PJson.Settings),
                    JsonConvert.SerializeObject(right, P2PJson.Settings),
                    StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return Equals(left, right);
            }
        }

        internal static void FinalizeReceivedHiddenCardRemovals(
            Dictionary<string, object> data)
        {
            if (!IsActive || data == null)
            {
                return;
            }

            if (data.TryGetValue(
                    P2PBattleProtocol.AuthorityHiddenStatesKey,
                    out object rawSnapshots) &&
                rawSnapshots is IEnumerable snapshots && !(rawSnapshots is string))
            {
                foreach (object rawSnapshot in snapshots)
                {
                    if (!(rawSnapshot is Dictionary<string, object> snapshot) ||
                        !TryGetStateInt(snapshot, "owner", out int snapshotOwner) ||
                        (snapshotOwner != 0 && snapshotOwner != 1) ||
                        !snapshot.TryGetValue("removed", out object rawSnapshotRemoved) ||
                        rawSnapshotRemoved is string ||
                        !(rawSnapshotRemoved is IEnumerable snapshotRemoved))
                    {
                        continue;
                    }
                    RemoveReceivedHiddenStates(snapshotOwner == 1, snapshotRemoved);
                }
            }

            if (!data.TryGetValue("p2pHiddenOwner", out object rawOwner) ||
                !data.TryGetValue("p2pHiddenRemoved", out object rawRemoved) ||
                rawRemoved is string || !(rawRemoved is IEnumerable removed))
            {
                return;
            }

            int owner;
            try
            {
                owner = Convert.ToInt32(rawOwner, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return;
            }
            if (owner != 0 && owner != 1)
            {
                return;
            }

            RemoveReceivedHiddenStates(owner == 1, removed);
        }

        private static void RemoveReceivedHiddenStates(
            bool ownerIsHost,
            IEnumerable removed)
        {
            if (removed == null)
            {
                return;
            }

            foreach (object rawIndex in removed)
            {
                if (!TryConvertAuthorityInt(rawIndex, out int index))
                {
                    continue;
                }
                if (index <= 0)
                {
                    continue;
                }

                string key = HiddenStateKey(ownerIsHost, index);
                ReceivedHiddenCardStates.Remove(key);
                ReceivedHiddenCardStateSignatures.Remove(key);
                AppliedReceivedHiddenCardStates.Remove(key);
                NativePromotedReceivedHiddenCardStateSignatures.Remove(key);
                NativeReplacedReceivedHiddenCardStateSignatures.Remove(key);
                PostActionHiddenCardStateKeys.Remove(key);
                ReportedMissingNativePrivateIdentities.Remove(key);
            }
        }

        private static string HiddenStateKey(bool ownerIsHost, int index)
        {
            return (ownerIsHost ? "1:" : "0:") +
                index.ToString(CultureInfo.InvariantCulture);
        }

        private static void RememberReceivedPlayerHistoryState(
            Dictionary<string, object> data,
            bool readyToApply)
        {
            if (data == null)
            {
                return;
            }

            if (data.TryGetValue(
                    P2PBattleProtocol.AuthorityPlayerHistoryStatesKey,
                    out object rawSnapshots) &&
                rawSnapshots is IEnumerable snapshots && !(rawSnapshots is string))
            {
                foreach (object rawSnapshot in snapshots)
                {
                    if (rawSnapshot is Dictionary<string, object> snapshot)
                    {
                        RememberReceivedPlayerHistorySnapshot(
                            snapshot, readyToApply);
                    }
                }
            }

            if (data.TryGetValue(PlayerHistoryStateKey, out object rawState) &&
                rawState is Dictionary<string, object> state)
            {
                RememberReceivedPlayerHistorySnapshot(state, readyToApply);
            }
        }

        private static void RememberReceivedPlayerHistorySnapshot(
            Dictionary<string, object> state,
            bool readyToApply)
        {
            if (state == null ||
                !TryGetStateInt(state, "owner", out int owner) ||
                (owner != 0 && owner != 1) ||
                !TryGetStateInt(state, "revision", out int revision) ||
                revision <= 0)
            {
                return;
            }

            if (AppliedPlayerHistoryRevisions.TryGetValue(
                    owner, out int appliedRevision) &&
                revision <= appliedRevision)
            {
                return;
            }

            string key = PlayerHistoryStateKeyFor(owner, revision);
            if (!ReceivedPlayerHistoryStates.TryGetValue(
                    key, out PendingPlayerHistoryState pending))
            {
                pending = new PendingPlayerHistoryState
                {
                    Owner = owner,
                    Revision = revision,
                    State = P2PJson.CloneDictionary(state),
                    FirstSeenUtc = DateTime.UtcNow
                };
                ReceivedPlayerHistoryStates[key] = pending;
            }
            if (readyToApply)
            {
                pending.ReadyToApply = true;
            }
        }

        internal static void MarkReceivedPlayerHistoryStateReady(
            Dictionary<string, object> data)
        {
            if (!IsActive)
            {
                return;
            }
            RememberReceivedPlayerHistoryState(data, true);
        }

        private static string PlayerHistoryStateKeyFor(int owner, int revision)
        {
            return owner.ToString(CultureInfo.InvariantCulture) + ":" +
                revision.ToString(CultureInfo.InvariantCulture);
        }

        internal static void TryApplyPendingPlayerHistoryStates()
        {
            if (applyingReceivedPlayerHistoryStates ||
                !IsActive || ReceivedPlayerHistoryStates.Count == 0 ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.BattlePlayer == null || manager.BattleEnemy == null ||
                manager.VfxMgr == null || !manager.VfxMgr.IsEnd)
            {
                return;
            }

            applyingReceivedPlayerHistoryStates = true;
            try
            {
                DateTime now = DateTime.UtcNow;
                int localOwner = Role == P2PRole.Host ? 1 : 0;
                List<PendingPlayerHistoryState> candidates =
                    ReceivedPlayerHistoryStates.Values
                        .Where(state => state.ReadyToApply &&
                            state.NextAttemptUtc <= now)
                        .GroupBy(state => state.Owner)
                        .Select(group => group.OrderByDescending(
                            state => state.Revision).First())
                        .ToList();
                foreach (PendingPlayerHistoryState pending in candidates)
                {
                    if (AppliedPlayerHistoryRevisions.TryGetValue(
                            pending.Owner, out int appliedRevision) &&
                        pending.Revision <= appliedRevision)
                    {
                        RemovePlayerHistoryStatesThrough(
                            pending.Owner, appliedRevision);
                        continue;
                    }

                    BattlePlayerBase target = pending.Owner == localOwner
                        ? manager.BattlePlayer
                        : manager.BattleEnemy;
                    bool complete = ApplyPlayerHistoryState(
                        target, pending.State, out string unresolved);
                    pending.Attempts++;
                    pending.LastUnresolved = unresolved;
                    if (!complete)
                    {
                        pending.NextAttemptUtc = now.AddMilliseconds(100);
                        if (!pending.WarningLogged &&
                            now - pending.FirstSeenUtc >= TimeSpan.FromSeconds(5))
                        {
                            pending.WarningLogged = true;
                            Plugin.Logger.LogWarning(
                                $"[P2P] Player history revision {pending.Revision} " +
                                $"is still waiting for card references: {unresolved}.");
                        }
                        continue;
                    }

                    AppliedPlayerHistoryRevisions[pending.Owner] = pending.Revision;
                    RemovePlayerHistoryStatesThrough(
                        pending.Owner, pending.Revision);
                    Plugin.Logger.LogDebug(
                        $"[P2P] Applied player history revision " +
                        $"{pending.Revision} for owner={pending.Owner}.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not apply player history state: " + ex.Message);
            }
            finally
            {
                applyingReceivedPlayerHistoryStates = false;
            }
        }

        private static void RemovePlayerHistoryStatesThrough(
            int owner,
            int revision)
        {
            foreach (string key in ReceivedPlayerHistoryStates
                .Where(item => item.Value.Owner == owner &&
                    item.Value.Revision <= revision)
                .Select(item => item.Key)
                .ToList())
            {
                ReceivedPlayerHistoryStates.Remove(key);
            }
        }

        private static bool ApplyPlayerHistoryState(
            BattlePlayerBase player,
            Dictionary<string, object> state,
            out string unresolved)
        {
            unresolved = string.Empty;
            if (player == null || state == null)
            {
                unresolved = "player unavailable";
                return false;
            }

            List<string> unresolvedLists = new List<string>();
            if (!state.TryGetValue("lists", out object rawLists) ||
                !(rawLists is Dictionary<string, object> lists))
            {
                lists = new Dictionary<string, object>();
            }

            // Resolve every reference before mutating anything. A failed history
            // revision is retried later, so partially clearing lists here would
            // corrupt the native card-zone topology between attempts.
            List<KeyValuePair<IList, List<object>>> preparedLists =
                new List<KeyValuePair<IList, List<object>>>();
            foreach (KeyValuePair<string, object> listState in lists)
            {
                if (!PlayerHistoryListNameSet.Contains(listState.Key) ||
                    !TryGetPlayerHistoryListMember(
                        player, listState.Key, out Type listType,
                        out object rawTarget) ||
                    !listType.IsGenericType ||
                    listType.GetGenericTypeDefinition() != typeof(List<>))
                {
                    continue;
                }
                if (!(rawTarget is IList target))
                {
                    continue;
                }

                Type elementType = listType.GetGenericArguments()[0];
                if (!TryBuildPlayerHistoryList(
                        listState.Value, elementType,
                        out List<object> replacements,
                        out string listUnresolved))
                {
                    unresolvedLists.Add(listState.Key + "=" + listUnresolved);
                    continue;
                }
                preparedLists.Add(
                    new KeyValuePair<IList, List<object>>(target, replacements));
            }

            unresolved = string.Join(", ", unresolvedLists);
            if (unresolvedLists.Count > 0)
            {
                return false;
            }

            int previousPp = player.Pp;
            int previousPpTotal = player.PpTotal;
            if (state.TryGetValue("scalars", out object rawScalars) &&
                rawScalars is Dictionary<string, object> scalars)
            {
                ApplyPlayerHistoryScalars(player, scalars);
                if (previousPp != player.Pp || previousPpTotal != player.PpTotal)
                {
                    try
                    {
                        player.StatusPanelControl?.SetPp(
                            player.Pp, player.PpTotal, false);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            foreach (KeyValuePair<IList, List<object>> prepared in preparedLists)
            {
                prepared.Key.Clear();
                foreach (object replacement in prepared.Value)
                {
                    prepared.Key.Add(replacement);
                }
            }
            return true;
        }

        internal static void RepairDuplicateReceivedCardZoneIndices(
            BattlePlayerBase player,
            int cardIndex,
            int expectedCardId)
        {
            if (!IsActive || player == null || cardIndex <= 0)
            {
                return;
            }

            RepairDuplicateReceivedCardZoneIndex(
                player.DeckCardList, "deck", player, cardIndex, expectedCardId);
            RepairDuplicateReceivedCardZoneIndex(
                player.HandCardList, "hand", player, cardIndex, expectedCardId);
        }

        private static void RepairDuplicateReceivedCardZoneIndex(
            List<BattleCardBase> zone,
            string zoneName,
            BattlePlayerBase player,
            int cardIndex,
            int expectedCardId)
        {
            if (zone == null)
            {
                return;
            }

            List<BattleCardBase> matches = zone
                .Where(card => card != null && card.Index == cardIndex)
                .ToList();
            if (matches.Count <= 1)
            {
                return;
            }

            bool localOwnerIsHost = Role == P2PRole.Host;
            bool ownerIsHost = player.IsPlayer
                ? localOwnerIsHost
                : !localOwnerIsHost;
            string key = HiddenStateKey(ownerIsHost, cardIndex);
            AppliedReceivedHiddenCardStates.TryGetValue(
                key, out AppliedHiddenCardState applied);
            BattleCardBase canonical = matches.FirstOrDefault(card =>
                    applied != null && ReferenceEquals(card, applied.Card)) ??
                matches.FirstOrDefault(card => card.CardId == expectedCardId) ??
                matches[matches.Count - 1];

            int position = zone.FindIndex(card =>
                card != null && card.Index == cardIndex);
            zone.RemoveAll(card => card != null && card.Index == cardIndex);
            zone.Insert(Math.Min(Math.Max(position, 0), zone.Count), canonical);

            Plugin.Logger.LogWarning(
                $"[P2P] Repaired duplicate card index before native replacement: " +
                $"zone={zoneName}, idx={cardIndex}, expectedCardId={expectedCardId}, " +
                $"keptCardId={canonical.CardId}, candidates=[" +
                $"{string.Join(",", matches.Select(card => card.CardId))}].");
        }

        private static void ApplyPlayerHistoryScalars(
            BattlePlayerBase player,
            Dictionary<string, object> scalars)
        {
            foreach (KeyValuePair<string, object> value in scalars)
            {
                if (!PlayerHistoryScalarNameSet.Contains(value.Key) ||
                    !TryFindInstanceProperty(player.GetType(), value.Key,
                        out PropertyInfo property) ||
                    !IsSimpleStateType(property.PropertyType))
                {
                    continue;
                }

                try
                {
                    object converted = ConvertStateValue(
                        value.Value, property.PropertyType);
                    if (TryFindBackingField(player.GetType(), value.Key,
                            out FieldInfo backingField) &&
                        !backingField.IsInitOnly && !backingField.IsLiteral)
                    {
                        backingField.SetValue(player, converted);
                        continue;
                    }

                    string explicitFieldName = GetPlayerHistoryScalarFieldName(
                        value.Key);
                    if (!string.IsNullOrEmpty(explicitFieldName) &&
                        TryFindInstanceField(player.GetType(), explicitFieldName,
                            out FieldInfo explicitField) &&
                        !explicitField.IsInitOnly && !explicitField.IsLiteral)
                    {
                        explicitField.SetValue(player, converted);
                        continue;
                    }

                    MethodInfo setter = property.GetSetMethod(true);
                    setter?.Invoke(player, new[] { converted });
                }
                catch (Exception)
                {
                }
            }
        }

        private static string GetPlayerHistoryScalarFieldName(string propertyName)
        {
            switch (propertyName)
            {
                case "PpTotal":
                    return "_ppTotal";
                case "EpTotal":
                    return "m_EpTotal";
                case "GameUsedEpCount":
                    return "_gameUsedEpCount";
                case "TurnUsedEpCount":
                    return "_turnUsedEpCount";
                default:
                    return string.Empty;
            }
        }

        private static bool TryBuildPlayerHistoryList(
            object rawList,
            Type elementType,
            out List<object> replacements,
            out string unresolved)
        {
            replacements = new List<object>();
            unresolved = string.Empty;
            if (rawList is string || !(rawList is IEnumerable values))
            {
                return true;
            }

            int position = 0;
            foreach (object rawValue in values)
            {
                if (elementType == typeof(BattleCardBase))
                {
                    if (!TryResolvePlayerHistoryCard(rawValue, out BattleCardBase card))
                    {
                        unresolved = DescribeUnresolvedCard(rawValue, position);
                        return false;
                    }
                    replacements.Add(card);
                }
                else if (elementType == typeof(BattlePlayerBase.TurnAndCard))
                {
                    if (!(rawValue is Dictionary<string, object> item) ||
                        !TryResolvePlayerHistoryCard(
                            item.TryGetValue("card", out object rawCard)
                                ? rawCard : null,
                            out BattleCardBase card))
                    {
                        unresolved = DescribeUnresolvedCard(rawValue, position);
                        return false;
                    }
                    TryGetStateInt(item, "turn", out int turn);
                    TryGetStateInt(item, "end", out int end);
                    replacements.Add(new BattlePlayerBase.TurnAndCard(
                        turn, GetLocalTurnFlag(item), card, end != 0));
                }
                else if (elementType == typeof(BattlePlayerBase.CardAndTribe))
                {
                    if (!(rawValue is Dictionary<string, object> item) ||
                        !TryResolvePlayerHistoryCard(
                            item.TryGetValue("card", out object rawCard)
                                ? rawCard : null,
                            out BattleCardBase card))
                    {
                        unresolved = DescribeUnresolvedCard(rawValue, position);
                        return false;
                    }
                    List<CardBasePrm.TribeType> tribes = item.TryGetValue(
                            "tribes", out object rawTribes)
                        ? ToIntArray(rawTribes)
                            .Select(value => (CardBasePrm.TribeType)value)
                            .ToList()
                        : new List<CardBasePrm.TribeType>();
                    replacements.Add(
                        new BattlePlayerBase.CardAndTribe(card, tribes));
                }
                else if (elementType == typeof(BattlePlayerBase.CardAndId))
                {
                    if (!(rawValue is Dictionary<string, object> item) ||
                        !TryResolvePlayerHistoryCard(
                            item.TryGetValue("card", out object rawCard)
                                ? rawCard : null,
                            out BattleCardBase card))
                    {
                        unresolved = DescribeUnresolvedCard(rawValue, position);
                        return false;
                    }
                    TryGetStateInt(item, "id", out int id);
                    replacements.Add(new BattlePlayerBase.CardAndId(card, id));
                }
                else if (elementType == typeof(BattlePlayerBase.CardAndValue))
                {
                    if (!(rawValue is Dictionary<string, object> item) ||
                        !TryResolvePlayerHistoryCard(
                            item.TryGetValue("card", out object rawCard)
                                ? rawCard : null,
                            out BattleCardBase card))
                    {
                        unresolved = DescribeUnresolvedCard(rawValue, position);
                        return false;
                    }
                    TryGetStateInt(item, "value", out int value);
                    replacements.Add(
                        new BattlePlayerBase.CardAndValue(card, value));
                }
                else if (elementType == typeof(TurnAndIntValue))
                {
                    if (!(rawValue is Dictionary<string, object> item))
                    {
                        position++;
                        continue;
                    }
                    TryGetStateInt(item, "value", out int value);
                    TryGetStateInt(item, "turn", out int turn);
                    replacements.Add(
                        new TurnAndIntValue(
                            value, turn, GetLocalTurnFlag(item)));
                }
                else if (elementType == typeof(int))
                {
                    try
                    {
                        replacements.Add(Convert.ToInt32(
                            rawValue, CultureInfo.InvariantCulture));
                    }
                    catch (Exception)
                    {
                        replacements.Add(0);
                    }
                }
                else if (elementType == typeof(List<BattleCardBase>))
                {
                    List<BattleCardBase> nested = new List<BattleCardBase>();
                    if (!(rawValue is string) && rawValue is IEnumerable rawCards)
                    {
                        foreach (object rawCard in rawCards)
                        {
                            if (!TryResolvePlayerHistoryCard(
                                    rawCard, out BattleCardBase card))
                            {
                                unresolved = DescribeUnresolvedCard(
                                    rawCard, position);
                                return false;
                            }
                            nested.Add(card);
                        }
                    }
                    replacements.Add(nested);
                }
                else
                {
                    return true;
                }
                position++;
            }
            return true;
        }

        private static bool GetLocalTurnFlag(
            Dictionary<string, object> item)
        {
            if (TryGetStateInt(item, "turnOwner", out int turnOwner) &&
                (turnOwner == 0 || turnOwner == 1))
            {
                bool turnOwnerIsHost = turnOwner == 1;
                return turnOwnerIsHost == (Role == P2PRole.Host);
            }
            return TryGetStateInt(item, "self", out int legacySelf) &&
                legacySelf != 0;
        }

        private static bool TryResolvePlayerHistoryCard(
            object rawReference,
            out BattleCardBase card)
        {
            card = null;
            if (!(rawReference is Dictionary<string, object> reference) ||
                !TryGetStateInt(reference, "idx", out int index))
            {
                return false;
            }
            if (index <= 0)
            {
                return true;
            }
            card = ResolveCardReference(reference);
            return card != null;
        }

        private static string DescribeUnresolvedCard(object rawReference, int position)
        {
            Dictionary<string, object> reference = rawReference as
                Dictionary<string, object>;
            if (reference != null && reference.TryGetValue(
                    "card", out object nested))
            {
                reference = nested as Dictionary<string, object>;
            }
            string owner = reference != null &&
                reference.TryGetValue("owner", out object rawOwner)
                    ? rawOwner?.ToString() ?? "?"
                    : "?";
            string index = reference != null &&
                reference.TryGetValue("idx", out object rawIndex)
                    ? rawIndex?.ToString() ?? "?"
                    : "?";
            return $"entry {position} owner={owner} idx={index}";
        }

        internal static void ApplyReceivedHiddenCardStateAfterNativeReplacement(
            BattleCardBase card)
        {
            if (!IsActive || card == null || card.Index <= 0)
            {
                return;
            }

            // The current packet's snapshot describes the state after its
            // operation. It was intentionally promoted only for the stock
            // CardDataModel replacement pass. Applying its generic values now
            // would let a condition in that very operation inspect the future
            // state. TryApplyPendingHiddenCardStates applies it after VFX.
            bool localOwnerIsHost = Role == P2PRole.Host;
            bool ownerIsHost = card.IsPlayer
                ? localOwnerIsHost
                : !localOwnerIsHost;
            string key = HiddenStateKey(ownerIsHost, card.Index);
            if (NativePromotedReceivedHiddenCardStateSignatures.TryGetValue(
                    key, out string promotedSignature))
            {
                NativeReplacedReceivedHiddenCardStateSignatures[key] =
                    promotedSignature;
                return;
            }

            ApplyReceivedHiddenCardState(card, true);
        }

        internal static void ApplyReceivedHiddenCardState(
            BattleCardBase card,
            bool nativeStateInherited = false)
        {
            if (!IsActive || card == null || card.Index <= 0)
            {
                return;
            }

            bool localOwnerIsHost = Role == P2PRole.Host;
            bool ownerIsHost = card.IsPlayer ? localOwnerIsHost : !localOwnerIsHost;
            string key = HiddenStateKey(ownerIsHost, card.Index);
            if (!ReceivedHiddenCardStates.TryGetValue(
                    key,
                    out Dictionary<string, object> state))
            {
                return;
            }

            try
            {
                ReceivedHiddenCardStateSignatures.TryGetValue(
                    key, out string signature);
                AppliedReceivedHiddenCardStates.TryGetValue(
                    key, out AppliedHiddenCardState previous);
                bool sameCoreState = previous != null &&
                    ReferenceEquals(previous.Card, card) &&
                    string.Equals(previous.Signature, signature ?? string.Empty,
                        StringComparison.Ordinal);

                // Unresolved card references can remain pending for several frames.
                // Apply absolute modifiers only once per card/signature and retry only
                // the reference-bearing portions, otherwise an unresolved token would
                // repeatedly clear and rebuild cost/attack/skill modifiers every frame.
                if (!sameCoreState)
                {
                    ApplyNativeCompatibleState(card, state);
                    ApplyCardModifierState(card, state);
                    ApplyPrimitiveState(card, state, "p2pCardPrimitive");
                    ApplySkillPrimitiveState(card, state);
                    ApplyCardSkillState(card, state);
                    ApplyGenericState(card, state);
                    ApplyIntegerListState(card, state);
                    ApplyStructuredSkillCollectionState(card, state);
                    ApplyDamagedCounterState(card, state);
                    ApplyAttackCountState(card, state);
                    ApplySkillActivationState(card, state);
                    ApplySkillCounterState(card, state);
                }
                bool referenceStateComplete = ApplyCardReferenceState(card, state);
                bool preprocessStateComplete = ApplyPreprocessState(card, state);
                bool nativeStateComplete = nativeStateInherited ||
                    (sameCoreState && previous.NativeStateInherited);
                bool stateComplete = ApplyFusionState(card, state) &&
                    referenceStateComplete &&
                    (preprocessStateComplete || nativeStateComplete);
                AppliedReceivedHiddenCardStates[key] = new AppliedHiddenCardState
                {
                    Card = card,
                    Signature = signature ?? string.Empty,
                    NativeStateInherited = nativeStateComplete,
                    StateComplete = stateComplete,
                    NextRetryUtc = stateComplete
                        ? DateTime.MinValue
                        : DateTime.UtcNow.AddMilliseconds(100)
                };
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[P2P] Could not apply hidden card state idx={card.Index}: " +
                    ex.Message);
            }
        }

        internal static void ApplyReceivedFusionAction(
            Dictionary<string, object> data,
            bool afterNative)
        {
            if (!IsActive || data == null ||
                !data.TryGetValue("p2pFusionActions", out object rawActions) ||
                rawActions is string)
            {
                return;
            }

            if (rawActions is Dictionary<string, object> singleAction)
            {
                if (!afterNative)
                {
                    StagedPreNativeFusionActions.Clear();
                }
                ApplyFusionActionAtBoundary(singleAction, afterNative);
                return;
            }
            if (!(rawActions is IEnumerable actions))
            {
                return;
            }
            if (!afterNative)
            {
                // A non-fusion message must not discard an unresolved pre-fusion
                // snapshot from the operation currently waiting for card
                // replacement. Only a message that actually owns fusion metadata
                // may replace the staged boundary state.
                StagedPreNativeFusionActions.Clear();
            }
            foreach (object rawAction in actions)
            {
                if (rawAction is Dictionary<string, object> action)
                {
                    ApplyFusionActionAtBoundary(action, afterNative);
                }
            }
        }

        private static void ApplyFusionActionAtBoundary(
            Dictionary<string, object> action,
            bool afterNative)
        {
            if (!afterNative)
            {
                StagedPreNativeFusionActions.Add(
                    P2PJson.CloneDictionary(action));
                return;
            }

            // ReceivedMessage returns after the fusion VFX was scheduled, not after
            // it ran. Applying the post-action snapshot here would make the queued
            // native FusionMaterialized append the current ingredients a second
            // time. Always defer the N-state calibration until the shared VFX queue
            // is idle and the native operation has actually completed.
            Dictionary<string, object> copy = P2PJson.CloneDictionary(action);
            string signature = JsonConvert.SerializeObject(copy, P2PJson.Settings);
            if (PendingFusionActionSignatures.Add(signature))
            {
                PendingFusionActions.Enqueue(copy);
            }
        }

        internal static void ApplyStagedPreNativeFusionActions()
        {
            if (!IsActive || StagedPreNativeFusionActions.Count == 0)
            {
                return;
            }

            List<Dictionary<string, object>> actions =
                StagedPreNativeFusionActions.ToList();
            StagedPreNativeFusionActions.Clear();
            foreach (Dictionary<string, object> action in actions)
            {
                if (!TryApplyFusionAction(action, false))
                {
                    // The native receiver may call this hook before its
                    // ReplaceReceivedCards pass has created the real card. Keep
                    // the action staged and retry after that pass instead of
                    // silently losing the N-1 fusion state.
                    StagedPreNativeFusionActions.Add(action);
                    Plugin.Logger.LogWarning(
                        "[P2P] Could not restore the pre-fusion cumulative state " +
                        "immediately before native fusion processing; using the " +
                        "currently resolved card state; retrying after card replacement.");
                }
            }
        }

        private static void TryApplyPendingFusionActions()
        {
            if (!IsActive || PendingFusionActions.Count == 0)
            {
                return;
            }
            if (!(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.VfxMgr == null || !manager.VfxMgr.IsEnd)
            {
                return;
            }

            int count = PendingFusionActions.Count;
            for (int i = 0; i < count; i++)
            {
                Dictionary<string, object> action = PendingFusionActions.Dequeue();
                if (!TryApplyFusionAction(action, true))
                {
                    PendingFusionActions.Enqueue(action);
                    continue;
                }
                PendingFusionActionSignatures.Remove(
                    JsonConvert.SerializeObject(action, P2PJson.Settings));
            }
        }

        private static bool TryApplyFusionAction(
            Dictionary<string, object> action,
            bool afterNative)
        {
            if (action == null ||
                !TryGetStateInt(action, "owner", out int owner) ||
                (owner != 0 && owner != 1) ||
                !TryGetStateInt(action, "targetIdx", out int targetIndex) ||
                targetIndex <= 0 || !(BattleManagerBase.GetIns() is
                    NetworkBattleManagerBase manager))
            {
                return true;
            }

            bool ownerIsHost = owner == 1;
            bool localOwnerIsHost = Role == P2PRole.Host ? true : false;
            if (ownerIsHost == localOwnerIsHost)
            {
                return true;
            }

            BattleCardBase target = ResolveCardReference(new Dictionary<string, object>
            {
                ["idx"] = targetIndex,
                ["owner"] = owner
            });
            if (target == null ||
                !(action.TryGetValue("ingredients", out object rawIngredients)) ||
                rawIngredients is string || !(rawIngredients is IEnumerable ingredients))
            {
                return target == null ? false : true;
            }

            if (afterNative)
            {
                if (!action.TryGetValue("afterIngredients", out object rawAfter) ||
                    rawAfter is string || !(rawAfter is IEnumerable afterIngredients))
                {
                    // Older peers did not publish a post-action cumulative snapshot.
                    // The native fusion operation has already appended the current
                    // ingredients, so leaving it untouched is the safest fallback.
                    return true;
                }
                return TryApplyFusionIngredients(
                    target, ownerIsHost, afterIngredients);
            }

            if (action.TryGetValue("beforeIngredients", out object rawBefore) &&
                !(rawBefore is string) && rawBefore is IEnumerable beforeIngredients &&
                TryApplyFusionIngredients(target, ownerIsHost, beforeIngredients))
            {
                return true;
            }

            // The message carries a post-action hidden-card snapshot. For an older
            // peer, or while a referenced historical ingredient is still resolving,
            // derive the pre-action state by removing this operation's materials.
            return TryRemoveCurrentFusionIngredients(target, ingredients);
        }

        private static bool TryRemoveCurrentFusionIngredients(
            BattleCardBase target,
            IEnumerable currentIngredients)
        {
            SkillApplyInformation information =
                target?.SkillApplyInformation as SkillApplyInformation;
            if (information?.FusionIngredients == null)
            {
                return false;
            }

            Dictionary<int, int> remainingCurrent = new Dictionary<int, int>();
            foreach (object rawIngredient in currentIngredients)
            {
                if (!(rawIngredient is Dictionary<string, object> ingredient) ||
                    !TryGetStateInt(ingredient, "idx", out int index) || index <= 0)
                {
                    continue;
                }
                remainingCurrent.TryGetValue(index, out int count);
                remainingCurrent[index] = count + 1;
            }

            if (remainingCurrent.Count == 0)
            {
                return true;
            }

            List<FusionIngredientInfo> before = new List<FusionIngredientInfo>();
            foreach (FusionIngredientInfo existing in information.FusionIngredients)
            {
                int index = existing?.Card?.Index ?? -1;
                if (index > 0 && remainingCurrent.TryGetValue(index, out int count) &&
                    count > 0)
                {
                    if (count == 1)
                    {
                        remainingCurrent.Remove(index);
                    }
                    else
                    {
                        remainingCurrent[index] = count - 1;
                    }
                    continue;
                }
                before.Add(existing);
            }

            // If the target still contains its true pre-action state, none of the
            // current indices will be present. Do not clear valid historical data.
            if (remainingCurrent.Count > 0)
            {
                return true;
            }

            information.FusionIngredients.Clear();
            information.FusionIngredients.AddRange(before);
            return true;
        }

        private static void ApplyNativeCompatibleState(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            if (card == null || state == null)
            {
                return;
            }

            if (TryGetStateInt(state, "cardId", out int cardId) &&
                cardId > 0 && card.CardId != cardId)
            {
                Plugin.Logger.LogDebug(
                    $"[P2P] Deferred hidden state idx={card.Index} currently has " +
                    $"cardId={card.CardId}; authoritative cardId={cardId}.");
            }

            if (TryGetStateInt(state, "spellboost", out int spellboost))
            {
                card.SetSpellChargeCount(spellboost);
            }
            if (TryGetStateInt(state, "cost", out int cost))
            {
                card.ClearCostModifier();
                card.AddCostModifier(new CostSetModifier(Math.Max(0, cost), false),
                    null, false);
            }

            SkillApplyInformation information =
                card.SkillApplyInformation as SkillApplyInformation;
            if (information == null)
            {
                return;
            }

            information.OffenseModifierList.Clear();
            information.LifeModifierList.Clear();
            information.ChantCountModifierList.Clear();
            if (TryGetStateInt(state, "setAtk", out int attack))
            {
                information.AddOffenseModifier(new OffenseSetModifier(attack));
            }
            if (TryGetStateInt(state, "setLife", out int life))
            {
                information.AddLifeModifier(new LifeSetModifier(life));
            }
            if (TryGetStateInt(state, "setChantCount", out int chantCount))
            {
                information.GiveChantCount(
                    new ChantCountSetModifier(chantCount));
            }

            if (state.ContainsKey("clan") || state.ContainsKey("tribe"))
            {
                information.ForceDepriveChangeAffiliation();
                CardBasePrm.ClanType clan = card.BaseParameter.Clan;
                if (TryGetStateInt(state, "clan", out int rawClan))
                {
                    clan = (CardBasePrm.ClanType)rawClan;
                }
                CardBasePrm.TribeInfo tribe = null;
                if (state.TryGetValue("tribe", out object rawTribe) &&
                    !string.IsNullOrEmpty(rawTribe?.ToString()) &&
                    !string.Equals(rawTribe.ToString(), "NONE",
                        StringComparison.Ordinal))
                {
                    tribe = new CardBasePrm.TribeInfo(
                        CardParameter.CreateTribeList(rawTribe.ToString()),
                        CardBasePrm.TribeChangeType.CHANGE);
                }
                information.GiveChangeAffiliation(clan, tribe, false);
            }
        }

        private static void ApplySkillPrimitiveState(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            ApplyPrimitiveState(card.SkillApplyInformation, state,
                "p2pSkillPrimitive");
        }

        private static void ApplyCardSkillState(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            if (TryGetStateInt(state, "p2pSkillActivatedCount",
                    out int activatedCount))
            {
                card.SetSkillActivatedCount(activatedCount);
            }
            if (TryGetStateInt(state, "p2pSkillActivatedWrap",
                    out int wrapValue))
            {
                card.SetSkillActivatedCountWrapValue(wrapValue);
            }

            SkillApplyInformation information =
                card.SkillApplyInformation as SkillApplyInformation;
            if (information == null)
            {
                return;
            }
            bool hasRandomArray = TryGetStateInt(state,
                "p2pSkillRandomArrayPresent", out int randomArrayPresent) &&
                randomArrayPresent != 0;
            information.GiveSkillRandomArray(hasRandomArray &&
                state.TryGetValue("p2pSkillRandomArray", out object rawRandomArray)
                    ? ToIntArray(rawRandomArray)
                    : null);
        }

        private static void ApplyPrimitiveState(
            object target,
            Dictionary<string, object> state,
            string stateKey)
        {
            if (target == null || state == null ||
                !(state.TryGetValue(stateKey, out object rawValues) &&
                    rawValues is Dictionary<string, object> values))
            {
                return;
            }

            foreach (KeyValuePair<string, object> value in values)
            {
                if (!TryFindBackingField(target.GetType(), value.Key,
                        out FieldInfo field) || field.IsInitOnly || field.IsLiteral ||
                    !IsSimpleStateType(field.FieldType))
                {
                    continue;
                }

                try
                {
                    object converted = ConvertStateValue(value.Value, field.FieldType);
                    field.SetValue(target, converted);
                }
                catch (Exception)
                {
                    // A field may be a runtime-version-specific implementation
                    // detail. Ignore only that field and keep the rest of the
                    // snapshot usable.
                }
            }
        }

        private static void ApplyGenericState(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            SkillApplyInformation information =
                card.SkillApplyInformation as SkillApplyInformation;
            if (information == null)
            {
                return;
            }

            bool hasArray = TryGetStateInt(state, "p2pGenericArrayPresent",
                out int arrayPresent) && arrayPresent != 0;
            if (hasArray && state.TryGetValue("p2pGenericArray", out object rawArray))
            {
                information.SetSkillGenericArray(ToIntArray(rawArray));
            }
            else
            {
                information.SetSkillGenericArray(null);
            }

            information.SkillGenericKeyAndValue.Clear();
            if (state.TryGetValue("p2pGenericKeys", out object rawKeys) &&
                rawKeys is Dictionary<string, object> keys)
            {
                foreach (KeyValuePair<string, object> key in keys)
                {
                    try
                    {
                        information.SetSkillGenericKeyAndValue(key.Key,
                            Convert.ToInt32(key.Value, CultureInfo.InvariantCulture));
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private static void ApplyIntegerListState(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            SkillApplyInformation information =
                card.SkillApplyInformation as SkillApplyInformation;
            if (information == null ||
                !state.TryGetValue("p2pIntLists", out object rawLists) ||
                !(rawLists is Dictionary<string, object> lists))
            {
                return;
            }

            ReplaceIntList(information.CantAtkUnitBaseCardIdList,
                lists, "cantAtkBaseIds");
            ReplaceIntList(information.DecreaseTurnStartPPList,
                lists, "decreaseTurnStartPP");
            ReplaceIntList(information.CantEvolutionList,
                lists, "cantEvolution");
            ReplaceIntList(information.SkillHealList,
                lists, "skillHeal");
        }

        private static void ReplaceIntList(
            List<int> target,
            Dictionary<string, object> lists,
            string key)
        {
            if (target == null || !lists.TryGetValue(key, out object rawValues))
            {
                return;
            }
            target.Clear();
            target.AddRange(ToIntArray(rawValues));
        }

        private static void ApplyStructuredSkillCollectionState(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            SkillApplyInformation information =
                card.SkillApplyInformation as SkillApplyInformation;
            if (information == null ||
                !state.TryGetValue("p2pSkillCollections", out object rawState) ||
                !(rawState is Dictionary<string, object> collections))
            {
                return;
            }

            information.TurnBuffCountList.Clear();
            if (collections.TryGetValue("turnBuff", out object rawTurnBuff) &&
                !(rawTurnBuff is string) && rawTurnBuff is IEnumerable turnBuffs)
            {
                foreach (object rawTurn in turnBuffs)
                {
                    if (!(rawTurn is Dictionary<string, object> turn) ||
                        !TryGetStateInt(turn, "turn", out int turnNumber))
                    {
                        continue;
                    }
                    information.TurnBuffCountList.Add(
                        new BuffCountInfo(turnNumber, GetLocalTurnFlag(turn)));
                }
            }

            information.TokenDrawModifiers.Clear();
            if (collections.TryGetValue("tokenDraw", out object rawTokenDraw) &&
                !(rawTokenDraw is string) && rawTokenDraw is IEnumerable tokenDraws)
            {
                foreach (object rawModifier in tokenDraws)
                {
                    if (!(rawModifier is Dictionary<string, object> modifier) ||
                        !TryGetStateInt(modifier, "cardId", out int cardId) ||
                        !TryGetStateInt(modifier, "count", out int count))
                    {
                        continue;
                    }
                    information.TokenDrawModifiers.Add(
                        new TokenDrawModifier(cardId, count));
                }
            }

            if (!HasExactLifeModifierState(state) &&
                collections.TryGetValue("lifeHistory", out object rawLifeHistory))
            {
                information.LifeModifierList.RemoveAll(modifier =>
                    modifier is DamageCardParameterModifier ||
                    modifier is HealCardParameterModifier ||
                    modifier is HiddenCardLifeStateModifier);
                if (!(rawLifeHistory is string) &&
                    rawLifeHistory is IEnumerable lifeHistory)
                {
                    foreach (object rawModifier in lifeHistory)
                    {
                        if (!(rawModifier is Dictionary<string, object> modifier) ||
                            !TryGetStateInt(modifier, "value", out int value) ||
                            !TryGetStateInt(modifier, "turn", out int turn))
                        {
                            continue;
                        }

                        string kind = modifier.TryGetValue(
                                "kind", out object rawKind)
                            ? rawKind?.ToString()
                            : string.Empty;
                        if (string.Equals(kind, "damage",
                                StringComparison.Ordinal))
                        {
                            information.LifeModifierList.Add(
                                new DamageCardParameterModifier(
                                    value, turn, GetLocalTurnFlag(modifier)));
                        }
                        else if (string.Equals(kind, "heal",
                                     StringComparison.Ordinal))
                        {
                            information.LifeModifierList.Add(
                                new HealCardParameterModifier(
                                    value, turn, GetLocalTurnFlag(modifier)));
                        }
                    }
                }

                CorrectHiddenCardLifeState(card, information, state);
            }

            ReplaceTurnValueCollection(
                information.CausedDamageModifierList,
                collections,
                "causedDamage",
                (value, turn, isSelfTurn) =>
                    new CausedDamageCardParameterModifier(
                        value, turn, isSelfTurn));
            ReplaceTurnValueCollection(
                information.PpModifierList,
                collections,
                "ppAdd",
                (value, turn, isSelfTurn) =>
                    new PpAddModifier(value, turn, isSelfTurn));
        }

        private static bool HasExactLifeModifierState(
            Dictionary<string, object> state)
        {
            return state.TryGetValue("p2pModifiers", out object rawModifiers) &&
                rawModifiers is Dictionary<string, object> modifiers &&
                modifiers.ContainsKey("life");
        }

        private static void ApplyCardModifierState(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            if (card == null ||
                !state.TryGetValue("p2pModifiers", out object rawModifiers) ||
                !(rawModifiers is Dictionary<string, object> modifiers))
            {
                return;
            }

            SkillApplyInformation information =
                card.SkillApplyInformation as SkillApplyInformation;
            if (information == null)
            {
                return;
            }

            if (TryGetStateCollection(modifiers, "offense", out IEnumerable offense))
            {
                information.OffenseModifierList.Clear();
                foreach (object rawModifier in offense)
                {
                    ICardOffenseModifier modifier =
                        CreateOffenseModifier(rawModifier);
                    if (modifier != null)
                    {
                        information.OffenseModifierList.Add(modifier);
                    }
                }
            }

            if (TryGetStateCollection(modifiers, "life", out IEnumerable life))
            {
                information.LifeModifierList.Clear();
                foreach (object rawModifier in life)
                {
                    ICardLifeModifier modifier = CreateLifeModifier(rawModifier);
                    if (modifier != null)
                    {
                        information.LifeModifierList.Add(modifier);
                    }
                }
                CorrectHiddenCardLifeState(card, information, state);
            }

            if (TryGetStateCollection(modifiers, "cost", out IEnumerable cost))
            {
                card.CostModifierList.Clear();
                foreach (object rawModifier in cost)
                {
                    ICardCostModifier modifier = CreateCostModifier(rawModifier);
                    if (modifier != null)
                    {
                        card.CostModifierList.Add(modifier);
                    }
                }
            }

            if (TryGetStateCollection(modifiers, "chant", out IEnumerable chant))
            {
                information.ChantCountModifierList.Clear();
                foreach (object rawModifier in chant)
                {
                    ICardChantCountModifier modifier =
                        CreateChantCountModifier(rawModifier);
                    if (modifier != null)
                    {
                        information.ChantCountModifierList.Add(modifier);
                    }
                }
            }
        }

        private static bool TryGetStateCollection(
            Dictionary<string, object> state,
            string key,
            out IEnumerable values)
        {
            values = null;
            if (!state.TryGetValue(key, out object rawValues) ||
                rawValues is string || !(rawValues is IEnumerable collection))
            {
                return false;
            }
            values = collection;
            return true;
        }

        private static ICardOffenseModifier CreateOffenseModifier(object rawState)
        {
            if (!(rawState is Dictionary<string, object> state) ||
                !TryGetModifierKindAndValue(state, out string kind, out int value))
            {
                return null;
            }
            switch (kind)
            {
                case "add":
                    return new OffenseAddModifier(value);
                case "set":
                    return new OffenseSetModifier(value);
                case "multiply":
                    return new OffenseMultiplyModifier(value);
                default:
                    return null;
            }
        }

        private static ICardLifeModifier CreateLifeModifier(object rawState)
        {
            if (!(rawState is Dictionary<string, object> state) ||
                !TryGetModifierKindAndValue(state, out string kind, out int value))
            {
                return null;
            }
            switch (kind)
            {
                case "add":
                    return new LifeAddModifier(value);
                case "set":
                    return new LifeSetModifier(value);
                case "multiply":
                    return new LifeMultiplyModifier(value);
                case "damage":
                    return TryGetStateInt(state, "turn", out int damageTurn)
                        ? new DamageCardParameterModifier(
                            value, damageTurn, GetLocalTurnFlag(state))
                        : null;
                case "heal":
                    return TryGetStateInt(state, "turn", out int healTurn)
                        ? new HealCardParameterModifier(
                            value, healTurn, GetLocalTurnFlag(state))
                        : null;
                default:
                    return null;
            }
        }

        private static ICardCostModifier CreateCostModifier(object rawState)
        {
            if (!(rawState is Dictionary<string, object> state) ||
                !state.TryGetValue("kind", out object rawKind))
            {
                return null;
            }
            string kind = rawKind?.ToString();
            bool resident = TryGetStateInt(state, "resident", out int rawResident) &&
                rawResident != 0;
            switch (kind)
            {
                case "add":
                    return TryGetStateInt(state, "value", out int add)
                        ? new CostAddModifier(add, resident)
                        : null;
                case "set":
                    return TryGetStateInt(state, "value", out int set)
                        ? new CostSetModifier(set, resident)
                        : null;
                case "halfUp":
                    return new CostHalfRoundUpModifier(resident);
                case "halfDown":
                    return new CostHalfRoundDownModifier(resident);
                default:
                    return null;
            }
        }

        private static ICardChantCountModifier CreateChantCountModifier(
            object rawState)
        {
            if (!(rawState is Dictionary<string, object> state) ||
                !TryGetModifierKindAndValue(state, out string kind, out int value))
            {
                return null;
            }
            switch (kind)
            {
                case "add":
                    return new ChantCountAddModifier(value);
                case "set":
                    return new ChantCountSetModifier(value);
                default:
                    return null;
            }
        }

        private static bool TryGetModifierKindAndValue(
            Dictionary<string, object> state,
            out string kind,
            out int value)
        {
            kind = null;
            value = 0;
            if (!state.TryGetValue("kind", out object rawKind) ||
                !TryGetStateInt(state, "value", out value))
            {
                return false;
            }
            kind = rawKind?.ToString();
            return !string.IsNullOrEmpty(kind);
        }

        private static void CorrectHiddenCardLifeState(
            BattleCardBase card,
            SkillApplyInformation information,
            Dictionary<string, object> state)
        {
            if (!state.TryGetValue("p2pLifeState", out object rawLifeState) ||
                !(rawLifeState is Dictionary<string, object> lifeState) ||
                !TryGetStateInt(lifeState, "life", out int life) ||
                !TryGetStateInt(lifeState, "maxLife", out int maxLife) ||
                (card.Life == life && card.MaxLife == maxLife))
            {
                return;
            }

            // Damage and healing entries double as condition history and life
            // modifiers. Their original ordering relative to max-life buffs is
            // private, so retain the authoritative final life values as well.
            information.LifeModifierList.Add(
                new HiddenCardLifeStateModifier(life, maxLife));
        }

        private static void ReplaceTurnValueCollection<T>(
            List<T> target,
            Dictionary<string, object> collections,
            string key,
            Func<int, int, bool, T> create)
        {
            if (target == null || !collections.TryGetValue(key, out object rawValues))
            {
                return;
            }

            target.Clear();
            if (rawValues is string || !(rawValues is IEnumerable values))
            {
                return;
            }
            foreach (object rawValue in values)
            {
                if (!(rawValue is Dictionary<string, object> value) ||
                    !TryGetStateInt(value, "value", out int amount) ||
                    !TryGetStateInt(value, "turn", out int turn))
                {
                    continue;
                }
                target.Add(create(amount, turn, GetLocalTurnFlag(value)));
            }
        }

        private static void ApplyDamagedCounterState(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            if (card?.DamagedCounter == null ||
                !state.TryGetValue("p2pDamagedCounter", out object rawCounter) ||
                !(rawCounter is Dictionary<string, object> counter))
            {
                return;
            }

            TryGetStateInt(counter, "selfTurn", out int selfTurn);
            TryGetStateInt(counter, "opponentTurn", out int opponentTurn);
            card.DamagedCounter.Clear();
            for (int i = 0; i < Math.Max(0, selfTurn); i++)
            {
                card.DamagedCounter.AddDamageCount(true);
            }
            for (int i = 0; i < Math.Max(0, opponentTurn); i++)
            {
                card.DamagedCounter.AddDamageCount(false);
            }
        }

        private static void ApplySkillActivationState(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            if (card?.SkillActivationList == null ||
                !state.TryGetValue("p2pSkillActivationIds", out object rawIds) ||
                rawIds is string || !(rawIds is IEnumerable ids))
            {
                return;
            }

            card.SkillActivationList.Clear();
            foreach (object rawId in ids)
            {
                try
                {
                    long id = Convert.ToInt64(
                        rawId, CultureInfo.InvariantCulture);
                    card.SkillActivationList.Add(
                        new BattleCardBase.SkillActivationInfo(id, null));
                }
                catch (Exception)
                {
                }
            }
        }

        private static void ApplyAttackCountState(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            if (card?.attackCountinfo == null ||
                !TryGetStateInt(state, "p2pMaxAttackableCount", out int count))
            {
                return;
            }

            RepairInvalidAttackCountState(card, "hidden-state apply");
            int current = CalculateAttackCount(card.attackCountinfo);
            if (current == count)
            {
                // The native action already rebuilt the real Skill_attack_count
                // relationship. Preserve it so VirtualClone and skill removal
                // can continue to dereference the originating skill safely.
                return;
            }

            if (count == 1)
            {
                card.attackCountinfo.Clear();
                return;
            }

            Skill_attack_count skill = FindAttackCountSkill(card);
            if (skill == null)
            {
                Plugin.Logger.LogWarning(
                    $"[P2P] Could not synchronize maximum attack count for " +
                    $"idx={card.Index}, cardId={card.CardId}: expected={count}, " +
                    $"actual={current}; no real Skill_attack_count was available. " +
                    "Kept the native state instead of creating an unsafe null-skill record.");
                return;
            }

            card.attackCountinfo.Clear();
            if (SafeIsSetAttackCount(skill))
            {
                card.attackCountinfo.Add(
                    new BattleCardBase.SetAttackCountInfo(skill, count));
            }
            else
            {
                card.attackCountinfo.Add(
                    new BattleCardBase.AddAttackCountInfo(skill, count - 1));
            }
        }

        internal static void RepairInvalidAttackCountState(
            BattleCardBase card,
            string boundary)
        {
            if (!IsActive || card?.attackCountinfo == null)
            {
                return;
            }

            int invalidCount = card.attackCountinfo.Count(info =>
                info == null || info.Skill == null);
            if (invalidCount == 0)
            {
                return;
            }

            int expected = CalculateAttackCount(card.attackCountinfo);
            card.attackCountinfo.RemoveAll(info =>
                info == null || info.Skill == null);
            int repaired = CalculateAttackCount(card.attackCountinfo);
            if (repaired != expected && expected != 1)
            {
                Skill_attack_count skill = FindAttackCountSkill(card);
                if (skill != null)
                {
                    card.attackCountinfo.Clear();
                    if (SafeIsSetAttackCount(skill))
                    {
                        card.attackCountinfo.Add(
                            new BattleCardBase.SetAttackCountInfo(skill, expected));
                    }
                    else
                    {
                        card.attackCountinfo.Add(
                            new BattleCardBase.AddAttackCountInfo(
                                skill, expected - 1));
                    }
                    repaired = CalculateAttackCount(card.attackCountinfo);
                }
            }

            Plugin.Logger.LogWarning(
                $"[P2P] Removed {invalidCount} unsafe attack-count record(s) " +
                $"before {boundary ?? "card operation"}: idx={card.Index}, " +
                $"cardId={card.CardId}, expected={expected}, repaired={repaired}.");
        }

        private static int CalculateAttackCount(
            IEnumerable<BattleCardBase.AttackCountInfo> entries)
        {
            int count = 1;
            if (entries == null)
            {
                return count;
            }
            foreach (BattleCardBase.AttackCountInfo entry in entries)
            {
                if (entry != null)
                {
                    count = entry.CalcAttackCount(count);
                }
            }
            return count;
        }

        private static Skill_attack_count FindAttackCountSkill(
            BattleCardBase card)
        {
            if (card == null)
            {
                return null;
            }

            List<Skill_attack_count> candidates = new List<Skill_attack_count>();
            if (card.attackCountinfo != null)
            {
                candidates.AddRange(card.attackCountinfo
                    .Where(info => info?.Skill != null)
                    .Select(info => info.Skill));
            }
            if (card.BuffInfoList != null)
            {
                candidates.AddRange(card.BuffInfoList
                    .Where(buff => buff?.SkillFrom is Skill_attack_count)
                    .Select(buff => (Skill_attack_count)buff.SkillFrom));
            }
            if (card.NormalSkills != null)
            {
                candidates.AddRange(card.NormalSkills
                    .OfType<Skill_attack_count>());
            }
            if (card.EvolutionSkills != null)
            {
                candidates.AddRange(card.EvolutionSkills
                    .OfType<Skill_attack_count>());
            }

            List<Skill_attack_count> distinct = candidates
                .Where(skill => skill != null)
                .Distinct()
                .ToList();
            return distinct.FirstOrDefault(SafeIsSetAttackCount) ??
                distinct.FirstOrDefault(SafeIsAddAttackCount);
        }

        private static bool SafeIsSetAttackCount(Skill_attack_count skill)
        {
            try
            {
                return skill != null && skill.IsSetAttackCount();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool SafeIsAddAttackCount(Skill_attack_count skill)
        {
            try
            {
                return skill != null && skill.IsAddAttackCount();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ApplyCardReferenceState(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            SkillApplyInformation information =
                card.SkillApplyInformation as SkillApplyInformation;
            if (information == null ||
                !state.TryGetValue("p2pCardReferences", out object rawLists) ||
                !(rawLists is Dictionary<string, object> lists))
            {
                return true;
            }

            bool complete = true;
            complete &= ReplaceCardReferenceList(
                information.RandomSelectedCardList, lists, "randomSelected");
            complete &= ReplaceCardReferenceList(
                information.SkillDrewCardList, lists, "skillDrew");
            complete &= ReplaceCardReferenceList(
                information.SavedTargetList, lists, "savedTargets");
            complete &= ReplaceCardReferenceList(
                information.SavedBurialRiteTargetList, lists,
                "savedBurialTargets");
            complete &= ReplaceCardReferenceList(
                information.LastBurialRiteCardList, lists,
                "lastBurialTargets");
            complete &= ReplaceCardReferenceList(
                information.GetOnCards, lists, "getOn");
            complete &= ReplaceCardReferenceList(
                card.GetOffCards, lists, "getOff");

            information.SavedTargetCardIdDict.Clear();
            if (state.TryGetValue("p2pSavedTargetIds", out object rawSavedIds) &&
                rawSavedIds is Dictionary<string, object> savedIds)
            {
                foreach (KeyValuePair<string, object> saved in savedIds)
                {
                    if (long.TryParse(saved.Key, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out long id))
                    {
                        information.SavedTargetCardIdDict[id] =
                            ToIntArray(saved.Value).ToList();
                    }
                }
            }
            return complete;
        }

        private static bool ApplyPreprocessState(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            if (!state.TryGetValue("p2pPreprocess", out object rawState) ||
                !(rawState is Dictionary<string, object> preprocessState))
            {
                return true;
            }

            bool normalComplete = ApplyPreprocessCollection(
                card.NormalSkills, preprocessState, "normal");
            bool evolutionComplete = ApplyPreprocessCollection(
                card.EvolutionSkills, preprocessState, "evolution");
            return normalComplete && evolutionComplete;
        }

        private static bool ApplyPreprocessCollection(
            IEnumerable<SkillBase> skills,
            Dictionary<string, object> preprocessState,
            string key)
        {
            if (!preprocessState.TryGetValue(key, out object rawSkills) ||
                rawSkills is string || !(rawSkills is IEnumerable skillStates))
            {
                return true;
            }

            List<SkillBase> actualSkills = skills?.ToList() ??
                new List<SkillBase>();
            List<object> expectedSkills = skillStates.Cast<object>().ToList();
            bool complete = actualSkills.Count == expectedSkills.Count;
            for (int i = 0; i < expectedSkills.Count; i++)
            {
                if (!(expectedSkills[i] is Dictionary<string, object> expected) ||
                    i >= actualSkills.Count || actualSkills[i] == null)
                {
                    complete = false;
                    continue;
                }

                SkillBase actual = actualSkills[i];
                if (!expected.TryGetValue("type", out object rawType) ||
                    !string.Equals(rawType?.ToString(),
                        actual.GetType().FullName, StringComparison.Ordinal))
                {
                    complete = false;
                    continue;
                }
                if (!expected.TryGetValue("items", out object rawItems) ||
                    rawItems is string || !(rawItems is IEnumerable itemStates))
                {
                    continue;
                }

                List<SkillPreprocessBase> actualItems = actual.PreprocessList?
                    .ToList() ?? new List<SkillPreprocessBase>();
                List<object> expectedItems = itemStates.Cast<object>().ToList();
                if (actualItems.Count != expectedItems.Count)
                {
                    complete = false;
                }
                for (int j = 0; j < expectedItems.Count; j++)
                {
                    if (!(expectedItems[j] is Dictionary<string, object> item) ||
                        j >= actualItems.Count || actualItems[j] == null ||
                        !item.TryGetValue("type", out object rawItemType) ||
                        !string.Equals(rawItemType?.ToString(),
                            actualItems[j].GetType().FullName,
                            StringComparison.Ordinal))
                    {
                        complete = false;
                        continue;
                    }
                    if (item.TryGetValue("fields", out object rawFields) &&
                        rawFields is Dictionary<string, object> fields)
                    {
                        ApplyMutableSimpleFields(actualItems[j], fields);
                    }
                }
            }
            return complete;
        }

        private static void ApplyMutableSimpleFields(
            object target,
            Dictionary<string, object> values)
        {
            if (target == null || values == null)
            {
                return;
            }
            foreach (KeyValuePair<string, object> value in values)
            {
                if (!TryFindInstanceField(target.GetType(), value.Key,
                        out FieldInfo field) || field.IsStatic || field.IsInitOnly ||
                    field.IsLiteral || !IsSimpleStateType(field.FieldType))
                {
                    continue;
                }
                try
                {
                    field.SetValue(target,
                        ConvertStateValue(value.Value, field.FieldType));
                }
                catch (Exception)
                {
                }
            }
        }

        private static bool ReplaceCardReferenceList(
            List<BattleCardBase> target,
            Dictionary<string, object> lists,
            string key)
        {
            if (target == null || !lists.TryGetValue(key, out object rawValues) ||
                rawValues is string || !(rawValues is IEnumerable values))
            {
                return true;
            }

            List<BattleCardBase> replacements = new List<BattleCardBase>();
            foreach (object rawValue in values)
            {
                BattleCardBase resolved = ResolveCardReference(
                    rawValue as Dictionary<string, object>);
                if (resolved == null)
                {
                    return false;
                }
                replacements.Add(resolved);
            }
            target.Clear();
            target.AddRange(replacements);
            return true;
        }

        private static BattleCardBase ResolveCardReference(
            Dictionary<string, object> reference)
        {
            if (reference == null ||
                !TryGetStateInt(reference, "idx", out int index) || index < 0 ||
                !TryGetStateInt(reference, "owner", out int owner) ||
                (owner != 0 && owner != 1) ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager))
            {
                return null;
            }

            bool ownerIsHost = owner == 1;
            bool isLocalPlayer = ownerIsHost == (Role == P2PRole.Host);
            BattlePlayerBase player = isLocalPlayer
                ? manager.BattlePlayer
                : manager.BattleEnemy;
            if (player == null)
            {
                return null;
            }

            // Leader/class cards use the reserved index 0 and are not always
            // exposed through AllCardsWithSkillIngredient. Resolve that index
            // directly so random effects and targeted actions can carry leader
            // references through the authority protocol.
            BattleCardBase resolved = index == 0
                ? player.Class
                : player.AllCardsWithSkillIngredient?
                    .FirstOrDefault(candidate => candidate != null &&
                        candidate.Index == index) ??
                    player.AllCards?.FirstOrDefault(candidate => candidate != null &&
                        candidate.Index == index);
            return resolved ?? FindCardInPlayerReferences(player, index);
        }

        private static BattleCardBase FindCardInPlayerReferences(
            BattlePlayerBase player,
            int index)
        {
            IEnumerable<IEnumerable<BattleCardBase>> coreLists =
                new IEnumerable<BattleCardBase>[]
                {
                    player.HandCardList,
                    player.DeckCardList,
                    player.ClassAndInPlayCardList,
                    player.CemeteryList,
                    player.BanishList
                };
            foreach (IEnumerable<BattleCardBase> list in coreLists)
            {
                BattleCardBase coreCard = list?.FirstOrDefault(card =>
                    card != null && card.Index == index);
                if (coreCard != null)
                {
                    return coreCard;
                }
            }

            foreach (string name in
                P2PPlayerHistoryPolicy.SynchronizedListNames)
            {
                if (!TryGetPlayerHistoryListMember(
                        player, name, out _, out object rawList))
                {
                    continue;
                }
                BattleCardBase card = FindCardInHistoryValue(rawList, index);
                if (card != null)
                {
                    return card;
                }
            }
            return null;
        }

        private static BattleCardBase FindCardInHistoryValue(
            object value,
            int index)
        {
            if (value is BattleCardBase card)
            {
                return card.Index == index ? card : null;
            }
            if (value is BattlePlayerBase.TurnAndCard turnAndCard)
            {
                card = turnAndCard.Card as BattleCardBase;
                return card != null && card.Index == index ? card : null;
            }
            if (value is BattlePlayerBase.CardAndTribe cardAndTribe)
            {
                card = cardAndTribe.Card as BattleCardBase;
                return card != null && card.Index == index ? card : null;
            }
            if (value is BattlePlayerBase.CardAndId cardAndId)
            {
                card = cardAndId.Card as BattleCardBase;
                return card != null && card.Index == index ? card : null;
            }
            if (value is BattlePlayerBase.CardAndValue cardAndValue)
            {
                card = cardAndValue.Card as BattleCardBase;
                return card != null && card.Index == index ? card : null;
            }
            if (value is string || !(value is IEnumerable values))
            {
                return null;
            }
            foreach (object item in values)
            {
                card = FindCardInHistoryValue(item, index);
                if (card != null)
                {
                    return card;
                }
            }
            return null;
        }

        private static int[] ToIntArray(object raw)
        {
            if (raw == null || raw is string || !(raw is IEnumerable values))
            {
                return Array.Empty<int>();
            }

            List<int> result = new List<int>();
            foreach (object value in values)
            {
                try
                {
                    result.Add(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                }
                catch (Exception)
                {
                    result.Add(0);
                }
            }
            return result.ToArray();
        }

        private static void ApplySkillCounterState(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            SkillApplyInformation information =
                card.SkillApplyInformation as SkillApplyInformation;
            if (information == null)
            {
                return;
            }

            if (TryGetStateInt(state, "p2pUnionBurstCount", out int unionBurst))
            {
                information.UnionBurstCountModifierList.Clear();
                information.GiveUnionBurstCount(
                    new UnionBurstCountAddModifier(unionBurst - 10));
            }
            if (TryGetStateInt(state, "p2pSkyboundArtCount", out int skyboundArt))
            {
                information.SkyboundArtCountModifierList.Clear();
                information.GiveSkyboundArtCount(
                    new SkyboundArtCountAddModifier(skyboundArt - 10));
            }
            if (TryGetStateInt(state, "p2pSuperSkyboundArtCount",
                    out int superSkyboundArt))
            {
                information.SuperSkyboundArtCountModifierList.Clear();
                information.GiveSuperSkyboundArtCount(
                    new SuperSkyboundArtCountAddModifier(superSkyboundArt - 15));
            }
        }

        private static bool ApplyFusionState(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            SkillApplyInformation information =
                card.SkillApplyInformation as SkillApplyInformation;
            if (information == null)
            {
                return false;
            }

            if (!state.TryGetValue("p2pFusion", out object rawFusion) ||
                rawFusion is string || !(rawFusion is IEnumerable ingredients))
            {
                information.FusionIngredients.Clear();
                return true;
            }

            bool ownerIsHost = card.IsPlayer == (Role == P2PRole.Host);
            return TryApplyFusionIngredients(card, ownerIsHost, ingredients);
        }

        private static bool TryApplyFusionIngredients(
            BattleCardBase card,
            bool ownerIsHost,
            IEnumerable ingredients)
        {
            if (card == null || ingredients == null ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager))
            {
                return false;
            }

            BattlePlayerBase owner = ownerIsHost == (Role == P2PRole.Host)
                ? manager.BattlePlayer : manager.BattleEnemy;
            SkillApplyInformation information =
                card.SkillApplyInformation as SkillApplyInformation;
            if (owner == null || information == null)
            {
                return false;
            }

            List<FusionIngredientInfo> replacements =
                new List<FusionIngredientInfo>();
            foreach (object rawIngredient in ingredients)
            {
                Dictionary<string, object> ingredient =
                    rawIngredient as Dictionary<string, object>;
                if (ingredient == null ||
                    !TryGetStateInt(ingredient, "idx", out int index))
                {
                    continue;
                }

                BattleCardBase ingredientCard = ResolveCardReference(
                    new Dictionary<string, object>
                    {
                        ["idx"] = index,
                        ["owner"] = ownerIsHost ? 1 : 0
                    });
                if (ingredientCard == null)
                {
                    return false;
                }

                int turn = owner.Turn;
                TryGetStateInt(ingredient, "turn", out turn);
                replacements.Add(new FusionIngredientInfo(turn, ingredientCard));
            }
            information.FusionIngredients.Clear();
            information.FusionIngredients.AddRange(replacements);
            return true;
        }

        internal static void TryApplyPendingHiddenCardStates()
        {
            if (applyingReceivedHiddenCardStates)
            {
                return;
            }

            applyingReceivedHiddenCardStates = true;
            try
            {
                TryApplyPendingHiddenCardStatesCore();
            }
            finally
            {
                applyingReceivedHiddenCardStates = false;
            }
        }

        internal static void WarnIfPrivateConditionHasDummyCards(
            SkillBase skill)
        {
            if (!IsActive || skill?.SkillPrm?.ownerCard == null)
            {
                return;
            }

            BattlePlayerBase owner = skill.SkillPrm.ownerCard.SelfBattlePlayer;
            if (owner == null)
            {
                return;
            }

            List<int> hand = FindUnresolvedPrivateCardIndices(owner.HandCardList);
            List<int> deck = FindUnresolvedPrivateCardIndices(owner.DeckCardList);
            if (hand.Count == 0 && deck.Count == 0)
            {
                return;
            }

            string warningKey = skill.SkillPrm.ownerCard.Index + ":" +
                skill.GetType().FullName + ":" + string.Join(",", hand) + ":" +
                string.Join(",", deck);
            if (!PrivateConditionWarnings.Add(warningKey))
            {
                return;
            }

            Plugin.Logger.LogWarning(
                $"[P2P] Private hand/deck condition still contains unresolved " +
                $"Dummy cards: ownerIdx={skill.SkillPrm.ownerCard.Index}, " +
                $"skill={skill.GetType().Name}, handIdx=[{string.Join(",", hand)}], " +
                $"deckIdx=[{string.Join(",", deck)}].");
        }

        private static List<int> FindUnresolvedPrivateCardIndices(
            IEnumerable<BattleCardBase> cards)
        {
            if (cards == null)
            {
                return new List<int>();
            }
            return cards
                .Where(card => card != null && card.Index > 0 &&
                    (card is NullBattleCard || card.CardId <= 0))
                .Select(card => card.Index)
                .Distinct()
                .OrderBy(index => index)
                .ToList();
        }

        private static void TryApplyPendingHiddenCardStatesCore()
        {
            if (!IsActive || ReceivedHiddenCardStates.Count == 0 ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.BattlePlayer == null || manager.BattleEnemy == null)
            {
                return;
            }

            bool canReplace = manager.VfxMgr != null && manager.VfxMgr.IsEnd;
            DateTime now = DateTime.UtcNow;
            List<BattleCardBase> replacementsToLoad =
                new List<BattleCardBase>();
            BattlePlayerBase[] owners = { manager.BattlePlayer, manager.BattleEnemy };
            foreach (BattlePlayerBase player in owners)
            {
                if (player == null)
                {
                    continue;
                }

                bool ownerIsHost = player.IsPlayer == (Role == P2PRole.Host);
                List<BattleCardBase> privateCards;
                try
                {
                    privateCards = EnumeratePrivateCards(player).ToList();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (BattleCardBase card in privateCards)
                {
                    if (card == null || card.Index <= 0)
                    {
                        continue;
                    }

                    string key = HiddenStateKey(ownerIsHost, card.Index);
                    if (!ReceivedHiddenCardStates.TryGetValue(
                            key, out Dictionary<string, object> state) ||
                        !ReceivedHiddenCardStateSignatures.TryGetValue(
                            key, out string signature))
                    {
                        continue;
                    }

                    AppliedReceivedHiddenCardStates.TryGetValue(
                        key, out AppliedHiddenCardState applied);
                    bool identityMismatch =
                        NeedsPrivateCardIdentityReplacement(card, state);
                    bool nativeActionState = UseNativeClientActionTiming &&
                        !NativeBaselineHiddenCardStateKeys.Contains(key);
                    if (nativeActionState && identityMismatch)
                    {
                        // A native action snapshot is not allowed to repair a
                        // card object after the original receiver boundary. If
                        // knownList/uList omitted the identity, preserve the
                        // native object and report the protocol defect once.
                        if (ReportedMissingNativePrivateIdentities.Add(key))
                        {
                            Plugin.Logger.LogError(
                                "[P2P] Native action omitted the identity for a " +
                                "private-zone card; refusing post-action replacement: owner=" +
                                SideName(ownerIsHost) + ", idx=" + card.Index +
                                ", expectedCardId=" +
                                (TryGetStateInt(state, "cardId", out int expectedId)
                                    ? expectedId.ToString(CultureInfo.InvariantCulture)
                                    : "?") + ", actualCardId=" + card.CardId + ".");
                        }
                        continue;
                    }
                    bool sameApplication = applied != null &&
                        ReferenceEquals(applied.Card, card) &&
                        string.Equals(applied.Signature, signature,
                            StringComparison.Ordinal);
                    bool retryDue = applied == null ||
                        applied.NextRetryUtc <= now;
                    if (!sameApplication || (!applied.StateComplete && retryDue))
                    {
                        bool wasReplacedByNativeReceive =
                            NativeReplacedReceivedHiddenCardStateSignatures.TryGetValue(
                                key, out string replacedSignature) &&
                            string.Equals(replacedSignature, signature,
                                StringComparison.Ordinal);
                        ApplyReceivedHiddenCardState(
                            card, wasReplacedByNativeReceive);
                        AppliedReceivedHiddenCardStates.TryGetValue(
                            key, out applied);
                        if (applied != null &&
                            ReferenceEquals(applied.Card, card) &&
                            string.Equals(applied.Signature, signature,
                                StringComparison.Ordinal))
                        {
                            // This promoted snapshot has crossed its action
                            // boundary. Retain the native-inherited bit in
                            // AppliedHiddenCardState, but do not let it affect
                            // a later snapshot for the same card index.
                            NativePromotedReceivedHiddenCardStateSignatures.Remove(
                                key);
                            NativeReplacedReceivedHiddenCardStateSignatures.Remove(
                                key);
                        }
                    }

                    if (!canReplace || applied == null ||
                        applied.NativeStateInherited ||
                        !CanReplacePrivateCard(player, card) ||
                        !identityMismatch)
                    {
                        continue;
                    }

                    if (UseNativeClientActionTiming &&
                        !NativeBaselineHiddenCardStateKeys.Contains(key))
                    {
                        // Native v3 never performs deferred object replacement.
                        // The only exception is the one-time private_state
                        // baseline, which is established before any ordered
                        // native action is committed.
                        continue;
                    }

                    if (PostActionHiddenCardStateKeys.Contains(key))
                    {
                        // This state was attached to a completed native action.
                        // Replacing it here would detach the card object from a
                        // hand view/touch processor created by that action.
                        // Treat a missing native identity as a protocol error,
                        // not as permission for the P2P side channel to repair
                        // the action after the fact.
                        if (ReportedMissingNativePrivateIdentities.Add(key))
                        {
                            Plugin.Logger.LogError(
                                "[P2P] Native action omitted the identity for a " +
                                "private-zone card; refusing late replacement: owner=" +
                                SideName(ownerIsHost) + ", idx=" + card.Index +
                                ", expectedCardId=" +
                                (TryGetStateInt(state, "cardId", out int expectedId)
                                    ? expectedId.ToString(CultureInfo.InvariantCulture)
                                    : "?") + ", actualCardId=" + card.CardId + ".");
                        }
                        continue;
                    }

                    BattleCardBase replacement = TryReplacePendingHiddenCard(
                        manager, player, state, key, card);
                    if (replacement != null)
                    {
                        replacementsToLoad.Add(replacement);
                    }
                }
            }

            if (replacementsToLoad.Count > 0 && !manager.IsRecovery &&
                manager.VfxMgr != null)
            {
                // A complete baseline can replace an entire enemy deck at once.
                // Load all resulting card resources through one sequential job
                // instead of creating one loader per card.
                manager.VfxMgr.RegisterSequentialVfx<
                    Wizard.Battle.View.Vfx.VfxBase>(
                    manager.LoadCardResources(replacementsToLoad, false));
            }
        }

        private static bool CanReplacePrivateCard(
            BattlePlayerBase player,
            BattleCardBase card)
        {
            return player.HandCardList.Contains(card) ||
                player.DeckCardList.Contains(card) ||
                player.ReservedCardList.Contains(card) ||
                player.NecromanceZoneList.Contains(card);
        }

        private static bool NeedsPrivateCardIdentityReplacement(
            BattleCardBase card,
            Dictionary<string, object> state)
        {
            if (card == null || state == null ||
                !TryGetStateInt(state, "cardId", out int expectedCardId) ||
                expectedCardId <= 0)
            {
                return false;
            }

            // State synchronization is not a reason to replace a real hand
            // object. The old adapter did so for every changed snapshot, which
            // left HandCardView/TouchControl holding an orphaned object until
            // the next turn. Fallback replacement is now only for a genuine
            // dummy/identity mismatch (initial baseline or an older peer).
            return card is NullBattleCard || card.CardId <= 0 ||
                card.CardId != expectedCardId;
        }

        private static BattleCardBase TryReplacePendingHiddenCard(
            NetworkBattleManagerBase manager,
            BattlePlayerBase player,
            Dictionary<string, object> state,
            string key,
            BattleCardBase oldCard)
        {
            try
            {
                CardDataModel model = CreateHiddenCardDataModel(state, player);
                if (model == null)
                {
                    return null;
                }

                BattleCardBase replacement =
                    new ReplaceReceivedCard(manager, model)
                        .ReplaceCard(player);
                if (replacement == null)
                {
                    return null;
                }
                Plugin.Logger.LogDebug(
                    $"[P2P] Replayed deferred hidden state idx={oldCard.Index}, " +
                    $"cardId={replacement.CardId} after the card entered its zone.");
                return replacement;
            }
            catch (Exception ex)
            {
                // Keep the entry pending. It can be retried after the next action
                // instead of losing the authoritative state permanently.
                AppliedReceivedHiddenCardStates.Remove(key);
                Plugin.Logger.LogDebug(
                    $"[P2P] Could not replay deferred hidden state idx={oldCard.Index}: " +
                    ex.Message);
                return null;
            }
        }

        private static CardDataModel CreateHiddenCardDataModel(
            Dictionary<string, object> state,
            BattlePlayerBase owner)
        {
            if (!TryGetStateInt(state, "idx", out int index) || index <= 0 ||
                !TryGetStateInt(state, "cardId", out int cardId) || cardId <= 0)
            {
                return null;
            }

            CardDataModel model = new CardDataModel
            {
                Index = index,
                CardId = cardId,
                // ReplaceReceivedCard uses isOpponent to choose its default
                // search zone. Authority snapshots cover both absolute owners,
                // so this must be derived from the target player instead of
                // assuming every deferred replacement belongs to the opponent.
                // A wrong value routes a local hand/deck card into the other
                // side and can create duplicate indexes or a stalled replay.
                // BattlePlayerBase.IsPlayer is already relative to the current
                // process (true for BattlePlayer, false for BattleEnemy).  Do
                // not fold the P2P role into this test: on the Guest process
                // the Guest is still BattlePlayer, so comparing IsPlayer with
                // (Role == Host) reverses both owners and routes every deferred
                // snapshot into the wrong hand/deck.
                isOpponent = owner == null || !owner.IsPlayer
            };
            if (TryGetStateInt(state, "cost", out int cost))
            {
                model.playCardCost = cost;
            }
            if (TryGetStateInt(state, "spellboost", out int spellboost))
            {
                model.Spellboost = spellboost;
            }
            if (TryGetStateInt(state, "setAtk", out int attack))
            {
                model.SetAtk = attack;
            }
            if (TryGetStateInt(state, "setLife", out int life))
            {
                model.SetLife = life;
            }
            if (TryGetStateInt(state, "setChantCount", out int chantCount))
            {
                model.SetChantCount = chantCount;
            }
            if (TryGetStateInt(state, "unionburst", out int unionBurst))
            {
                model.UnionBurstCount = unionBurst;
            }
            if (TryGetStateInt(state, "skyboundArt", out int skyboundArt))
            {
                model.SkyboundArtCount = skyboundArt;
            }
            if (TryGetStateInt(state, "clan", out int clan))
            {
                model.Clan = clan;
            }
            if (state.TryGetValue("tribe", out object rawTribe))
            {
                model.Tribe = rawTribe?.ToString() ?? "NONE";
            }
            if (state.TryGetValue("attachTarget", out object rawAttach))
            {
                model.SetAttachTarget(rawAttach?.ToString() ?? string.Empty);
            }
            if (state.TryGetValue("fusion", out object rawFusion))
            {
                model.FusionIngredientList = ToIntArray(rawFusion).ToList();
            }
            return model;
        }

        private static bool TryGetStateInt(
            Dictionary<string, object> data,
            string key,
            out int value)
        {
            value = 0;
            if (data == null || !data.TryGetValue(key, out object rawValue))
            {
                return false;
            }
            try
            {
                value = Convert.ToInt32(rawValue, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static object ConvertStateValue(object value, Type targetType)
        {
            if (value == null)
            {
                return null;
            }

            Type underlyingType = Nullable.GetUnderlyingType(targetType);
            Type effectiveType = underlyingType ?? targetType;
            if (effectiveType.IsEnum)
            {
                return Enum.ToObject(effectiveType,
                    Convert.ToInt32(value, CultureInfo.InvariantCulture));
            }
            if (effectiveType == typeof(string))
            {
                return value.ToString();
            }
            if (effectiveType == typeof(bool))
            {
                if (value is string text && int.TryParse(text,
                        NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int boolValue))
                {
                    return boolValue != 0;
                }
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            return Convert.ChangeType(value, effectiveType,
                CultureInfo.InvariantCulture);
        }

        private static bool IsSimpleStateType(Type type)
        {
            Type effectiveType = Nullable.GetUnderlyingType(type) ?? type;
            return effectiveType.IsEnum || effectiveType == typeof(string) ||
                effectiveType == typeof(decimal) ||
                effectiveType.IsPrimitive;
        }

        private static bool TryFindBackingField(
            Type type,
            string propertyName,
            out FieldInfo field)
        {
            return TryFindInstanceField(type,
                "<" + propertyName + ">k__BackingField", out field);
        }

        private static bool TryFindInstanceProperty(
            Type type,
            string propertyName,
            out PropertyInfo property)
        {
            property = null;
            for (Type current = type; current != null; current = current.BaseType)
            {
                property = current.GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryFindInstanceField(
            Type type,
            string fieldName,
            out FieldInfo field)
        {
            field = null;
            for (Type current = type; current != null; current = current.BaseType)
            {
                field = current.GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return true;
                }
            }
            return false;
        }

        private static int ReadPrivateIntField(
            object target,
            string fieldName,
            int fallback)
        {
            if (target == null || !TryFindInstanceField(target.GetType(), fieldName,
                    out FieldInfo field))
            {
                return fallback;
            }
            try
            {
                return Convert.ToInt32(field.GetValue(target),
                    CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static void CacheLocalBattleCardIdentities()
        {
            if (!IsActive || !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.BattlePlayer == null)
            {
                return;
            }

            try
            {
                foreach (BattleCardBase card in manager.BattlePlayer.AllCards)
                {
                    if (card != null)
                    {
                        BattleCardTracker.RememberSourceCard(
                            Role == P2PRole.Host,
                            card.Index,
                            card.CardId,
                            card.Cost);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not refresh the local card identity cache: " +
                    ex.Message);
            }
        }

        private static void ObserveLocalHiddenCardStates()
        {
            if (!IsActive ||
                IsPrivateStateSyncActive ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.BattlePlayer == null)
            {
                return;
            }

            try
            {
                foreach (BattleCardBase card in EnumeratePrivateCards(
                    manager.BattlePlayer))
                {
                    if (card == null || card.Index <= 0 || card.CardId <= 0)
                    {
                        continue;
                    }
                    LocalHiddenCardStates[card.Index] =
                        P2PJson.CloneDictionary(CreateHiddenCardState(card));
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not observe local hidden card state: " +
                    ex.Message);
            }
        }

        private static BattleCardBase FindLocalPrivateCard(
            BattlePlayerBase player,
            int index)
        {
            if (player == null || index <= 0)
            {
                return null;
            }

            // Resolve only the indexes named by the native action.  This keeps
            // the extension on the same bounded-card path as knownList/uList
            // instead of walking every private card once per action.
            IEnumerable<IEnumerable<BattleCardBase>> zones =
                new IEnumerable<BattleCardBase>[]
                {
                    player.HandCardList,
                    player.DeckCardList,
                    player.DeckSkillCardList,
                    player.ReservedCardList,
                    player.NecromanceZoneList,
                    player.FusionIngredientList
                };
            foreach (IEnumerable<BattleCardBase> zone in zones)
            {
                BattleCardBase card = zone?.FirstOrDefault(
                    candidate => candidate != null && candidate.Index == index);
                if (card != null)
                {
                    return card;
                }
            }
            return null;
        }

        private static void AppendLocalHiddenCardState(
            string uri,
            Dictionary<string, object> data)
        {
            if (data == null || string.Equals(uri, P2PBattleProtocol.EchoUri,
                    StringComparison.Ordinal) ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.BattlePlayer == null)
            {
                return;
            }

            // The initial private_state message establishes the complete
            // baseline.  Subsequent native actions must not scan every hand or
            // deck card: the official server only serializes cards registered
            // by the current operation in knownList/uList/orderList.  Restrict
            // the P2P extension to those same native references and use it only
            // for fields that CardDataModel cannot carry.
            HashSet<int> candidateIndices = CollectLocalCardIndices(data);
            if (candidateIndices.Count == 0)
            {
                return;
            }
            Dictionary<int, BattleCardBase> presentCards =
                new Dictionary<int, BattleCardBase>();
            List<Dictionary<string, object>> changedCards =
                new List<Dictionary<string, object>>();
            Dictionary<int, Dictionary<string, object>>
                fusionMetamorphoseOriginalStates =
                new Dictionary<int, Dictionary<string, object>>();

            try
            {
                foreach (int candidateIndex in candidateIndices)
                {
                    BattleCardBase card =
                        FindLocalPrivateCard(manager.BattlePlayer, candidateIndex);
                    if (card == null || card.Index <= 0 || card.CardId <= 0)
                    {
                        continue;
                    }

                    presentCards[card.Index] = card;
                    Dictionary<string, object> state;
                    string signature;
                    try
                    {
                        state = CreateHiddenCardState(card);
                        signature = JsonConvert.SerializeObject(state,
                            P2PJson.Settings);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Logger.LogDebug(
                            $"[P2P] Could not capture hidden card idx={card.Index}: " +
                            ex.Message);
                        continue;
                    }

                    // EmitMsg observes the hand after a fusion metamorphose. Keep
                    // the cached pre-action object in knownList so the receiver's
                    // native Skill_fusion_metamorphose can perform ReplaceInHand
                    // and play its animation. The new state remains in the P2P
                    // post-action snapshot below.
                    if (P2PBattleProtocol.IsFusionMetamorphoseTarget(
                            data, card.Index) &&
                        TryGetActionPreHiddenCardState(
                            Role == P2PRole.Host,
                            card.Index,
                            out Dictionary<string, object> originalState) &&
                        TryGetStateInt(originalState, "cardId",
                            out int originalCardId) && originalCardId > 0)
                    {
                        Dictionary<string, object> originalClone =
                            P2PJson.CloneDictionary(originalState);
                        fusionMetamorphoseOriginalStates[card.Index] =
                            originalClone;
                        AttachFusionMetamorphoseOriginal(
                            data, card.Index, originalClone);
                    }
                    LocalHiddenCardStates.TryGetValue(
                        card.Index,
                        out Dictionary<string, object> previousState);
                    if (LocalHiddenCardStateSignatures.TryGetValue(
                            card.Index, out string previousSignature) &&
                        string.Equals(previousSignature, signature,
                            StringComparison.Ordinal))
                    {
                        LocalHiddenCardStates[card.Index] =
                            P2PJson.CloneDictionary(state);
                        continue;
                    }

                    LocalHiddenCardStateSignatures[card.Index] = signature;
                    LocalHiddenCardStates[card.Index] =
                        P2PJson.CloneDictionary(state);
                    changedCards.Add(CreateHiddenCardDelta(previousState, state)
                        ?? P2PJson.CloneDictionary(state));
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not capture hidden card state: " + ex.Message);
                return;
            }

            IEnumerable<int> removalCandidates = candidateIndices;
            List<int> removedIndices = removalCandidates
                .Where(index => LocalHiddenCardStateSignatures.ContainsKey(index) &&
                    !presentCards.ContainsKey(index))
                .Distinct()
                .OrderBy(index => index)
                .ToList();
            foreach (int index in removedIndices)
            {
                // EmitMsg runs after the local action. Use the state observed
                // before that action so a played or discarded card retains its
                // hidden buffs, attached skills, counters, and saved targets
                // while the peer performs the matching native replacement.
                if (!IsAcceleratedOrCrystallizedDeparture(data, index) &&
                    LocalHiddenCardStates.TryGetValue(
                        index, out Dictionary<string, object> departedState))
                {
                    changedCards.Add(P2PJson.CloneDictionary(departedState));
                }
                LocalHiddenCardStateSignatures.Remove(index);
                LocalHiddenCardStates.Remove(index);
            }

            if (changedCards.Count == 0 && removedIndices.Count == 0)
            {
                return;
            }

            if (changedCards.Count > 0)
            {
                List<object> knownList = GetOrCreateKnownList(data);
                foreach (Dictionary<string, object> state in changedCards)
                {
                    Dictionary<string, object> nativeState = state;
                    if (TryGetStateInt(state, "idx", out int stateIndex) &&
                        fusionMetamorphoseOriginalStates.TryGetValue(
                            stateIndex,
                            out Dictionary<string, object> originalState))
                    {
                        nativeState = originalState;
                    }
                    MergeKnownCardState(knownList, nativeState);
                }
                data["p2pHiddenCards"] = changedCards
                    .Select(state => (object)P2PJson.CloneDictionary(state))
                    .ToList();
            }
            data["p2pHiddenOwner"] = Role == P2PRole.Host ? 1 : 0;
            if (removedIndices.Count > 0)
            {
                data["p2pHiddenRemoved"] = removedIndices
                    .Select(index => (object)index)
                    .ToList();
            }

            Plugin.Logger.LogDebug(
                $"[P2P] Attached {changedCards.Count} hidden hand/deck state " +
                $"snapshot(s) and {removedIndices.Count} tombstone(s) to {uri}" +
                ".");
        }

        private static void AttachFusionMetamorphoseOriginal(
            Dictionary<string, object> data,
            int index,
            Dictionary<string, object> state)
        {
            AttachFusionMetamorphoseOriginal(
                data, Role == P2PRole.Host, index, state);
        }

        private static void AttachFusionMetamorphoseOriginal(
            Dictionary<string, object> data,
            bool ownerIsHost,
            int index,
            Dictionary<string, object> state)
        {
            if (data == null || state == null || index <= 0 ||
                !TryGetStateInt(state, "cardId", out int cardId) || cardId <= 0)
            {
                return;
            }

            List<object> entries;
            if (data.TryGetValue(
                    P2PBattleProtocol.FusionMetamorphoseOriginalsKey,
                    out object rawEntries) && rawEntries is List<object> existing)
            {
                entries = existing;
            }
            else
            {
                entries = new List<object>();
                data[P2PBattleProtocol.FusionMetamorphoseOriginalsKey] = entries;
            }

            int owner = ownerIsHost ? 1 : 0;
            Dictionary<string, object> entry = entries
                .OfType<Dictionary<string, object>>()
                .FirstOrDefault(candidate =>
                    TryGetStateInt(candidate, "owner", out int candidateOwner) &&
                    candidateOwner == owner &&
                    TryGetStateInt(candidate, "idx", out int candidateIndex) &&
                    candidateIndex == index);
            if (entry == null)
            {
                entry = new Dictionary<string, object>();
                entries.Add(entry);
            }
            entry["owner"] = owner;
            entry["idx"] = index;
            entry["cardId"] = cardId;
            if (TryGetStateInt(state, "cost", out int cost) && cost >= 0)
            {
                entry["cost"] = cost;
            }
        }

        private static void AttachActionPreHiddenMetamorphoseOriginals(
            Dictionary<string, object> data)
        {
            if (data == null ||
                !data.TryGetValue("orderList", out object rawOrders) ||
                rawOrders is string || !(rawOrders is IEnumerable orders))
            {
                return;
            }

            foreach (object rawOrder in orders)
            {
                if (!(rawOrder is Dictionary<string, object> order) ||
                    !order.TryGetValue("metamorphose", out object rawMetamorphose) ||
                    !(rawMetamorphose is Dictionary<string, object> metamorphose) ||
                    !TryGetStateInt(metamorphose, "isFusion", out int isFusion) ||
                    isFusion == 0)
                {
                    continue;
                }

                bool ownerIsHost = TryGetStateInt(
                        metamorphose, "isSelf", out int isSelf) && isSelf != 0
                    ? Role == P2PRole.Host
                    : Role != P2PRole.Host;
                foreach (int index in ReadStateIndices(metamorphose))
                {
                    if (!TryGetActionPreHiddenCardState(
                            ownerIsHost, index,
                            out Dictionary<string, object> originalState))
                    {
                        continue;
                    }
                    AttachFusionMetamorphoseOriginal(
                        data, ownerIsHost, index, originalState);
                }
            }
        }

        private static void AttachAuthorityFusionMetamorphoseOriginals(
            Dictionary<string, object> data,
            IEnumerable<object> orderList,
            BattleCardBase actor,
            int originalActorCardId,
            int originalActorCost)
        {
            if (data == null || orderList == null)
            {
                return;
            }

            foreach (object rawOrder in orderList)
            {
                if (!(rawOrder is Dictionary<string, object> order) ||
                    !order.TryGetValue("metamorphose", out object rawMetamorphose) ||
                    !(rawMetamorphose is Dictionary<string, object> metamorphose) ||
                    !TryGetStateInt(metamorphose, "isFusion", out int isFusion) ||
                    isFusion == 0)
                {
                    continue;
                }

                bool ownerIsHost = TryGetStateInt(
                        metamorphose, "isSelf", out int isSelf) && isSelf != 0
                    ? true
                    : false;
                foreach (int index in ReadStateIndices(metamorphose))
                {
                    Dictionary<string, object> originalState = null;
                    if (!TryGetActionPreHiddenCardState(
                            ownerIsHost, index, out originalState) &&
                        actor != null && index == actor.Index &&
                        originalActorCardId > 0)
                    {
                        originalState = new Dictionary<string, object>
                        {
                            ["idx"] = index,
                            ["cardId"] = originalActorCardId,
                            ["isSelf"] = 1,
                            ["cost"] = originalActorCost
                        };
                    }

                    if (originalState == null)
                    {
                        continue;
                    }
                    AttachFusionMetamorphoseOriginal(
                        data, ownerIsHost, index, originalState);
                }
            }
        }

        private static IEnumerable<int> ReadStateIndices(
            Dictionary<string, object> state)
        {
            if (state == null)
            {
                yield break;
            }

            if (state.TryGetValue("idx", out object rawIndex) &&
                TryConvertAuthorityInt(rawIndex, out int index) && index > 0)
            {
                yield return index;
                yield break;
            }

            if (!state.TryGetValue("idxList", out object rawIndices) ||
                rawIndices is string || !(rawIndices is IEnumerable indices))
            {
                yield break;
            }
            foreach (object rawValue in indices)
            {
                if (TryConvertAuthorityInt(rawValue, out int value) && value > 0)
                {
                    yield return value;
                }
            }
        }

        private static HashSet<int> CollectLocalCardIndices(
            Dictionary<string, object> data)
        {
            HashSet<int> indices = new HashSet<int>();
            CollectLocalCardIndices(data, true, indices);
            return indices;
        }

        private static void CollectLocalCardIndices(
            object value,
            bool inheritedIsLocal,
            HashSet<int> indices)
        {
            if (value == null || indices == null || value is string)
            {
                return;
            }

            if (value is Dictionary<string, object> dictionary)
            {
                bool isLocal = inheritedIsLocal;
                if (dictionary.TryGetValue("isSelf", out object rawSide))
                {
                    try
                    {
                        isLocal = Convert.ToInt32(rawSide,
                            CultureInfo.InvariantCulture) != 0;
                    }
                    catch (Exception)
                    {
                    }
                }

                if (isLocal)
                {
                    AddCardIndices(dictionary, "idx", indices);
                    AddCardIndices(dictionary, "idxList", indices);
                    AddCardIndices(dictionary, "playIdx", indices);
                    AddCardIndices(dictionary, "targetIdx", indices);
                    AddCardIndices(dictionary, "ingredients", indices);
                    AddCardIndices(dictionary, "baseIdx", indices);
                    AddCardIndices(dictionary, "baseCardIdx", indices);
                    AddCardIndices(dictionary, "handIdxList", indices);
                    AddCardIndices(dictionary, "skillKeyCardIdx", indices);
                    AddCardIndices(dictionary, "randomTargetIdx", indices);
                    AddCardIndices(dictionary, "hasGuard", indices);
                }

                foreach (KeyValuePair<string, object> field in dictionary)
                {
                    CollectLocalCardIndices(field.Value, isLocal, indices);
                }
                return;
            }

            if (value is IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                {
                    CollectLocalCardIndices(item, inheritedIsLocal, indices);
                }
            }
        }

        private static void AddCardIndices(
            Dictionary<string, object> data,
            string key,
            HashSet<int> indices)
        {
            if (!data.TryGetValue(key, out object rawIndices) ||
                rawIndices == null || rawIndices is string)
            {
                if (TryGetStateInt(data, key, out int singleIndex) &&
                    singleIndex > 0)
                {
                    indices.Add(singleIndex);
                }
                return;
            }

            if (rawIndices is IEnumerable values)
            {
                foreach (object rawIndex in values)
                {
                    try
                    {
                        int index = Convert.ToInt32(rawIndex,
                            CultureInfo.InvariantCulture);
                        if (index > 0)
                        {
                            indices.Add(index);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                return;
            }

            if (TryGetStateInt(data, key, out int indexValue) && indexValue > 0)
            {
                indices.Add(indexValue);
            }
        }

        private static bool IsAcceleratedOrCrystallizedDeparture(
            Dictionary<string, object> data,
            int index)
        {
            if (!TryGetStateInt(data, "playIdx", out int playIndex) ||
                playIndex != index ||
                !data.TryGetValue("keyAction", out object rawActions) ||
                rawActions is string || !(rawActions is IEnumerable actions))
            {
                return false;
            }

            foreach (object rawAction in actions)
            {
                if (rawAction is Dictionary<string, object> action &&
                    TryGetStateInt(action, "type", out int type) &&
                    (type == (int)SendKeyActionDataManager.KeyActionType.Accelerated ||
                     type == (int)SendKeyActionDataManager.KeyActionType.Crystallize))
                {
                    return true;
                }
            }
            return false;
        }

        private static void AppendLocalPlayerHistoryState(
            string uri,
            Dictionary<string, object> data,
            Dictionary<string, object> preActionState = null,
            int preActionRevision = 0)
        {
            if (data == null || !IsOrderedLocalBattleMessage(uri) ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.BattlePlayer == null)
            {
                return;
            }

            try
            {
                bool ownerIsHost = Role == P2PRole.Host;
                Dictionary<string, object> state =
                    CapturePlayerHistoryState(manager.BattlePlayer, ownerIsHost);
                string signature = JsonConvert.SerializeObject(
                    state, P2PJson.Settings);
                bool ordered = IsOrderedLocalBattleMessage(uri);
                Dictionary<string, object> beforeState =
                    ordered && P2PPlayerHistoryPolicy.ShouldAttachPreActionHistory(
                        uri, manager.BattlePlayer.Turn)
                    ? preActionState ?? localPlayerHistoryBaselineState
                    : null;
                int beforeRevision = preActionRevision > 0
                    ? preActionRevision
                    : localPlayerHistoryBaselineRevision;
                if (ordered && beforeState != null)
                {
                    Dictionary<string, object> before =
                        P2PJson.CloneDictionary(beforeState);
                    before.Remove("revision");
                    string beforeSignature = JsonConvert.SerializeObject(
                        before, P2PJson.Settings);
                    bool isNormalAction = string.Equals(
                        uri,
                        NetworkBattleDefine.NetworkBattleURI.PlayActions.ToString(),
                        StringComparison.Ordinal);
                    bool receiverAlreadyHasBeforeState = isNormalAction &&
                        !string.IsNullOrEmpty(localPlayerHistoryStateSignature) &&
                        string.Equals(beforeSignature,
                            localPlayerHistoryStateSignature,
                            StringComparison.Ordinal);
                    if (!receiverAlreadyHasBeforeState)
                    {
                        before["revision"] = Math.Max(1, beforeRevision);
                        data[PlayerHistoryStateBeforeKey] = before;
                        Plugin.Logger.LogDebug(
                            $"[P2P] Attached pre-action player history revision " +
                            $"{beforeRevision} to {uri}.");
                    }
                }

                if (!string.Equals(signature, localPlayerHistoryStateSignature,
                        StringComparison.Ordinal))
                {
                    localPlayerHistoryStateSignature = signature;
                    state["revision"] = ++localPlayerHistoryRevision;
                    data[PlayerHistoryStateKey] = state;
                    Plugin.Logger.LogDebug(
                        $"[P2P] Attached player history revision " +
                        $"{localPlayerHistoryRevision} to {uri}.");
                }

                // This is the first stable point after the action's synchronous
                // state mutations and VFX have completed. Use it as the next
                // action's fallback baseline instead of reflecting and serializing
                // every player-history field once per rendered frame.
                localPlayerHistoryBaselineSignature = signature;
                localPlayerHistoryBaselineState = P2PJson.CloneDictionary(state);
                localPlayerHistoryBaselineState.Remove("revision");
                localPlayerHistoryBaselineRevision = Math.Max(1,
                    localPlayerHistoryRevision);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not capture player history state: " + ex.Message);
            }
        }

        internal static void CaptureLocalActionStart(BattleCardBase sourceCard)
        {
            if (UseNativeClientActionTiming)
            {
                CaptureLocalPreActionPlayerHistoryState();
                CaptureLocalSourceCardPreActionState(sourceCard);
                return;
            }
            if (!IsActive || sourceCard == null || !sourceCard.IsPlayer ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.BattlePlayer == null)
            {
                return;
            }

            localActionCaptureActive = true;

            try
            {
                CaptureActionPreHiddenCardStates(manager);
                bool ownerIsHost = Role == P2PRole.Host;
                Dictionary<string, object> state = CapturePlayerHistoryState(
                    manager.BattlePlayer, ownerIsHost);
                localActionPreHistoryState = state;
                localActionPreHistoryRevision = Math.Max(1,
                    localPlayerHistoryRevision);
                localPlayerHistoryBaselineState = P2PJson.CloneDictionary(state);
                localPlayerHistoryBaselineSignature = JsonConvert.SerializeObject(
                    state, P2PJson.Settings);
                localPlayerHistoryBaselineRevision = localActionPreHistoryRevision;
                Plugin.Logger.LogDebug(
                    $"[P2P] Captured action-start player history revision " +
                    $"{localActionPreHistoryRevision} for card idx={sourceCard.Index}.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not capture action-start player history: " +
                    ex.Message);
            }
        }

        internal static void CaptureLocalActionStart()
        {
            if (UseNativeClientActionTiming)
            {
                CaptureLocalPreActionPlayerHistoryState();
                actionPreHiddenCardStates.Clear();
                return;
            }
            if (!IsActive ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.BattlePlayer == null || !manager.BattlePlayer.IsSelfTurn)
            {
                return;
            }

            localActionCaptureActive = true;

            try
            {
                CaptureActionPreHiddenCardStates(manager);
                bool ownerIsHost = Role == P2PRole.Host;
                Dictionary<string, object> state = CapturePlayerHistoryState(
                    manager.BattlePlayer, ownerIsHost);
                localActionPreHistoryState = state;
                localActionPreHistoryRevision = Math.Max(1,
                    localPlayerHistoryRevision);
                localPlayerHistoryBaselineState = P2PJson.CloneDictionary(state);
                localPlayerHistoryBaselineSignature = JsonConvert.SerializeObject(
                    state, P2PJson.Settings);
                localPlayerHistoryBaselineRevision = localActionPreHistoryRevision;
                Plugin.Logger.LogDebug(
                    $"[P2P] Captured action-start player history revision " +
                    $"{localActionPreHistoryRevision}.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not capture action-start player history: " +
                    ex.Message);
            }
        }

        internal static void CaptureLocalAutomaticActionStart(
            BattlePlayerBase player,
            string boundary)
        {
            if (UseNativeClientActionTiming)
            {
                CaptureLocalPreActionPlayerHistoryState();
                actionPreHiddenCardStates.Clear();
                return;
            }
            if (!IsActive || player == null || !player.IsPlayer ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                !ReferenceEquals(player, manager.BattlePlayer))
            {
                return;
            }

            localActionCaptureActive = true;

            try
            {
                CaptureActionPreHiddenCardStates(manager);
                bool ownerIsHost = Role == P2PRole.Host;
                Dictionary<string, object> state = CapturePlayerHistoryState(
                    manager.BattlePlayer, ownerIsHost);
                localActionPreHistoryState = state;
                localActionPreHistoryRevision = Math.Max(1,
                    localPlayerHistoryRevision);
                localPlayerHistoryBaselineState = P2PJson.CloneDictionary(state);
                localPlayerHistoryBaselineSignature = JsonConvert.SerializeObject(
                    state, P2PJson.Settings);
                localPlayerHistoryBaselineRevision = localActionPreHistoryRevision;
                Plugin.Logger.LogDebug(
                    $"[P2P] Captured {boundary ?? "automatic"} action-start " +
                    $"player history revision {localActionPreHistoryRevision}.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    $"[P2P] Could not capture {boundary ?? "automatic"} " +
                    "action-start player history: " + ex.Message);
            }
        }

        private static void CaptureActionPreHiddenCardStates(
            NetworkBattleManagerBase manager)
        {
            actionPreHiddenCardStates.Clear();
            if (!IsActive || manager == null || manager.BattlePlayer == null ||
                manager.BattleEnemy == null)
            {
                return;
            }

            try
            {
                CaptureActionPreHiddenCardStatesForPlayer(
                    manager.BattlePlayer, Role == P2PRole.Host);
                CaptureActionPreHiddenCardStatesForPlayer(
                    manager.BattleEnemy, Role != P2PRole.Host);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not capture action-start hidden card states: " +
                    ex.Message);
            }
        }

        private static void CaptureLocalSourceCardPreActionState(
            BattleCardBase sourceCard)
        {
            actionPreHiddenCardStates.Clear();
            if (!IsActive || sourceCard == null || !sourceCard.IsPlayer ||
                sourceCard.Index <= 0 || sourceCard.CardId <= 0)
            {
                return;
            }

            try
            {
                actionPreHiddenCardStates[
                    HiddenStateKey(Role == P2PRole.Host, sourceCard.Index)] =
                    P2PJson.CloneDictionary(CreateHiddenCardState(sourceCard));
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not capture the source card's pre-action " +
                    "identity: " + ex.Message);
            }
        }

        private static void CaptureLocalPreActionPlayerHistoryState()
        {
            if (!IsActive ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.BattlePlayer == null)
            {
                return;
            }

            try
            {
                bool ownerIsHost = Role == P2PRole.Host;
                Dictionary<string, object> state = CapturePlayerHistoryState(
                    manager.BattlePlayer, ownerIsHost);
                localPlayerHistoryBaselineState = state;
                localPlayerHistoryBaselineSignature = JsonConvert.SerializeObject(
                    state, P2PJson.Settings);
                localPlayerHistoryBaselineRevision = Math.Max(1,
                    localPlayerHistoryRevision);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogDebug(
                    "[P2P] Could not capture native pre-action player history: " +
                    ex.Message);
            }
        }

        private static void CaptureActionPreHiddenCardStatesForPlayer(
            BattlePlayerBase player,
            bool ownerIsHost)
        {
            if (player == null)
            {
                return;
            }

            foreach (BattleCardBase card in EnumeratePrivateCards(player))
            {
                // Only cards that can be replaced by the native hidden-card
                // path need a full pre-action snapshot. Public cards are
                // already represented by orderList/uList and capturing them
                // would add unnecessary per-action work.
                if (card == null || card.Index <= 0 || card.CardId <= 0 ||
                    !CanReplacePrivateCard(player, card))
                {
                    continue;
                }

                try
                {
                    actionPreHiddenCardStates[
                        HiddenStateKey(ownerIsHost, card.Index)] =
                        P2PJson.CloneDictionary(CreateHiddenCardState(card));
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogDebug(
                        $"[P2P] Could not capture action-start hidden card " +
                        $"idx={card.Index}: {ex.Message}");
                }
            }
        }

        private static bool TryGetActionPreHiddenCardState(
            bool ownerIsHost,
            int index,
            out Dictionary<string, object> state)
        {
            state = null;
            if (index <= 0)
            {
                return false;
            }

            if (actionPreHiddenCardStates.TryGetValue(
                    HiddenStateKey(ownerIsHost, index), out state))
            {
                return true;
            }

            // The local observation cache predates the transient action-start
            // capture and remains a useful fallback when a card was created
            // between the input hook and the native action callback.
            if (ownerIsHost == (Role == P2PRole.Host) &&
                LocalHiddenCardStates.TryGetValue(index, out state))
            {
                state = P2PJson.CloneDictionary(state);
                return true;
            }
            return false;
        }

        private static void RememberReceivedPlayerHistoryBeforeState(
            Dictionary<string, object> data)
        {
            if (data == null ||
                !data.TryGetValue(PlayerHistoryStateBeforeKey,
                    out object rawState) ||
                !(rawState is Dictionary<string, object> state) ||
                !TryGetStateInt(state, "owner", out int owner) ||
                (owner != 0 && owner != 1) ||
                owner == (Role == P2PRole.Host ? 1 : 0))
            {
                return;
            }

            PendingPreActionPlayerHistoryStates[owner] =
                P2PJson.CloneDictionary(state);
        }

        internal static void ApplyPendingPreActionPlayerHistoryState()
        {
            if (!IsActive || PendingPreActionPlayerHistoryStates.Count == 0 ||
                !(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager))
            {
                return;
            }

            foreach (KeyValuePair<int, Dictionary<string, object>> item in
                PendingPreActionPlayerHistoryStates.ToList())
            {
                BattlePlayerBase target = item.Key ==
                    (Role == P2PRole.Host ? 1 : 0)
                    ? manager.BattlePlayer
                    : manager.BattleEnemy;
                if (ApplyPlayerHistoryState(target, item.Value,
                        out string unresolved))
                {
                    PendingPreActionPlayerHistoryStates.Remove(item.Key);
                    Plugin.Logger.LogDebug(
                        $"[P2P] Applied pre-action player history for owner=" +
                        $"{item.Key}.");
                }
                else if (!string.IsNullOrEmpty(unresolved))
                {
                    Plugin.Logger.LogDebug(
                        $"[P2P] Pre-action player history owner={item.Key} " +
                        $"is waiting for {unresolved}.");
                }
            }
        }

        private static Dictionary<string, object> CapturePlayerHistoryState(
            BattlePlayerBase player,
            bool ownerIsHost)
        {
            Dictionary<string, object> scalars =
                new Dictionary<string, object>();
            foreach (string propertyName in PlayerHistoryScalarNames)
            {
                if (!TryFindInstanceProperty(player.GetType(), propertyName,
                        out PropertyInfo property) ||
                    !IsSimpleStateType(property.PropertyType))
                {
                    continue;
                }
                try
                {
                    scalars[propertyName] = property.GetValue(player, null);
                }
                catch (Exception)
                {
                }
            }

            Dictionary<string, object> lists =
                new Dictionary<string, object>();
            foreach (string propertyName in
                P2PPlayerHistoryPolicy.SynchronizedListNames)
            {
                if (!TryGetPlayerHistoryListMember(
                        player, propertyName, out Type listType,
                        out object listValue) ||
                    !listType.IsGenericType ||
                    listType.GetGenericTypeDefinition() != typeof(List<>))
                {
                    continue;
                }
                try
                {
                    Type elementType = listType.GetGenericArguments()[0];
                    object captured = CapturePlayerHistoryList(
                        listValue, elementType, ownerIsHost);
                    if (captured != null)
                    {
                        lists[propertyName] = captured;
                    }
                }
                catch (Exception)
                {
                }
            }

            return new Dictionary<string, object>
            {
                ["owner"] = ownerIsHost ? 1 : 0,
                ["scalars"] = scalars,
                ["lists"] = lists
            };
        }

        private static object CapturePlayerHistoryList(
            object rawList,
            Type elementType,
            bool defaultOwnerIsHost)
        {
            if (!(rawList is IEnumerable values))
            {
                return new List<object>();
            }

            List<object> result = new List<object>();
            if (elementType == typeof(BattleCardBase))
            {
                foreach (object value in values)
                {
                    result.Add(CapturePlayerCardReference(
                        value as BattleCardBase, defaultOwnerIsHost));
                }
                return result;
            }
            if (elementType == typeof(BattlePlayerBase.TurnAndCard))
            {
                foreach (BattlePlayerBase.TurnAndCard value in values)
                {
                    if (value == null)
                    {
                        continue;
                    }
                    result.Add(new Dictionary<string, object>
                    {
                        ["card"] = CapturePlayerCardReference(
                            value.Card as BattleCardBase, defaultOwnerIsHost),
                        ["turn"] = value.Turn,
                        ["turnOwner"] = GetAbsoluteTurnOwner(value.IsSelfTurn),
                        ["end"] = value.IsTurnEnd ? 1 : 0
                    });
                }
                return result;
            }
            if (elementType == typeof(BattlePlayerBase.CardAndTribe))
            {
                foreach (BattlePlayerBase.CardAndTribe value in values)
                {
                    if (value == null)
                    {
                        continue;
                    }
                    result.Add(new Dictionary<string, object>
                    {
                        ["card"] = CapturePlayerCardReference(
                            value.Card as BattleCardBase, defaultOwnerIsHost),
                        ["tribes"] = value.Tribes == null
                            ? new List<object>()
                            : value.Tribes.Select(tribe => (object)(int)tribe)
                                .ToList()
                    });
                }
                return result;
            }
            if (elementType == typeof(BattlePlayerBase.CardAndId))
            {
                foreach (BattlePlayerBase.CardAndId value in values)
                {
                    if (value == null)
                    {
                        continue;
                    }
                    result.Add(new Dictionary<string, object>
                    {
                        ["card"] = CapturePlayerCardReference(
                            value.Card as BattleCardBase, defaultOwnerIsHost),
                        ["id"] = value.Id
                    });
                }
                return result;
            }
            if (elementType == typeof(BattlePlayerBase.CardAndValue))
            {
                foreach (BattlePlayerBase.CardAndValue value in values)
                {
                    if (value == null)
                    {
                        continue;
                    }
                    result.Add(new Dictionary<string, object>
                    {
                        ["card"] = CapturePlayerCardReference(
                            value.Card as BattleCardBase, defaultOwnerIsHost),
                        ["value"] = value.Value
                    });
                }
                return result;
            }
            if (elementType == typeof(TurnAndIntValue))
            {
                foreach (TurnAndIntValue value in values)
                {
                    if (value == null)
                    {
                        continue;
                    }
                    result.Add(new Dictionary<string, object>
                    {
                        ["value"] = value.Value,
                        ["turn"] = value.Turn,
                        ["turnOwner"] = GetAbsoluteTurnOwner(value.IsSelfTurn)
                    });
                }
                return result;
            }
            if (elementType == typeof(int))
            {
                foreach (object value in values)
                {
                    result.Add(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                }
                return result;
            }
            if (elementType == typeof(List<BattleCardBase>))
            {
                foreach (object value in values)
                {
                    List<object> nested = new List<object>();
                    if (value is IEnumerable cards)
                    {
                        foreach (object card in cards)
                        {
                            nested.Add(CapturePlayerCardReference(
                                card as BattleCardBase, defaultOwnerIsHost));
                        }
                    }
                    result.Add(nested);
                }
                return result;
            }
            return null;
        }

        private static int GetAbsoluteTurnOwner(bool isLocalPlayerTurn)
        {
            bool localOwnerIsHost = Role == P2PRole.Host;
            bool turnOwnerIsHost = isLocalPlayerTurn
                ? localOwnerIsHost
                : !localOwnerIsHost;
            return turnOwnerIsHost ? 1 : 0;
        }

        private static Dictionary<string, object> CapturePlayerCardReference(
            BattleCardBase card,
            bool defaultOwnerIsHost)
        {
            bool ownerIsHost = defaultOwnerIsHost;
            if (card != null)
            {
                bool localOwnerIsHost = Role == P2PRole.Host;
                ownerIsHost = card.IsPlayer
                    ? localOwnerIsHost
                    : !localOwnerIsHost;
            }
            return new Dictionary<string, object>
            {
                ["idx"] = card?.Index ?? 0,
                ["cardId"] = card?.CardId ?? 0,
                ["owner"] = ownerIsHost ? 1 : 0
            };
        }

        private static IEnumerable<BattleCardBase> EnumeratePrivateCards(
            BattlePlayerBase player)
        {
            HashSet<int> seen = new HashSet<int>();
            IEnumerable<IEnumerable<BattleCardBase>> zones =
                new IEnumerable<BattleCardBase>[]
                {
                    player.HandCardList,
                    player.DeckCardList,
                    // Deck-skill cards are created outside DeckCardList but
                    // participate in turn-start/turn-end and private-count
                    // conditions (for example, cards generated by a deck
                    // effect).  They must share the same authoritative state
                    // channel as ordinary hand/deck cards.
                    player.DeckSkillCardList,
                    player.ClassAndInPlayCardList,
                    player.CemeteryList,
                    player.BanishList,
                    player.FusionIngredientList,
                    player.TurnFusionCards,
                    player.ReservedCardList,
                    player.DiscardedCardList,
                    player.FusionIngredientAndDiscardedCardList,
                    player.GetOnList,
                    player.UniteList,
                    player.BlackHole,
                    player.ChoiceBraveCardList,
                    player.ChoiceBraveCards,
                    player.InHandCards,
                    // Necromance cards are not always public in the local
                    // network representation, but the original replacement
                    // path can resolve this zone by index as well.
                    player.NecromanceZoneList
                };
            foreach (IEnumerable<BattleCardBase> zone in zones)
            {
                if (zone == null)
                {
                    continue;
                }
                foreach (BattleCardBase card in zone)
                {
                    if (card != null && seen.Add(card.Index))
                    {
                        yield return card;
                    }
                }
            }
        }

        private static Dictionary<string, object> CreateHiddenCardState(
            BattleCardBase card)
        {
            Dictionary<string, object> state = new Dictionary<string, object>
            {
                ["idx"] = card.Index,
                ["cardId"] = card.CardId,
                ["isSelf"] = 1,
                ["cost"] = card.Cost,
                ["spellboost"] = card.SpellChargeCount
            };

            SkillApplyInformation skillInformation =
                card.SkillApplyInformation as SkillApplyInformation;
            state["p2pCardPrimitive"] = CaptureSimpleBackingProperties(
                card, HiddenCardPrimitiveExclusions);
            state["p2pSkillPrimitive"] = CaptureSimpleBackingProperties(
                skillInformation, HiddenSkillPrimitiveExclusions);
            state["p2pLifeState"] = new Dictionary<string, object>
            {
                ["life"] = card.Life,
                ["maxLife"] = card.MaxLife
            };
            state["p2pDamagedCounter"] = new Dictionary<string, object>
            {
                ["selfTurn"] = card.DamagedCounter?.SelfTurnDamage ?? 0,
                ["opponentTurn"] =
                    card.DamagedCounter?.OpponentTurnDamage ?? 0
            };
            state["p2pModifiers"] = CaptureCardModifierState(
                card, skillInformation);
            state["p2pSkillActivationIds"] = card.SkillActivationList == null
                ? new List<object>()
                : card.SkillActivationList
                    .Select(value => (object)value.SkillId)
                    .ToList();
            state["p2pMaxAttackableCount"] = card.MaxAttackableCount;
            state["p2pSkillActivatedCount"] = card.SkillActivatedCount;
            state["p2pSkillActivatedWrap"] = ReadPrivateIntField(
                card, "_skillActivatedCountWrapValue", -1);
            state["p2pSkillRandomArrayPresent"] =
                skillInformation?.SkillRandomArray != null ? 1 : 0;
            if (skillInformation?.SkillRandomArray != null)
            {
                state["p2pSkillRandomArray"] = skillInformation.SkillRandomArray
                    .Select(value => (object)value).ToList();
            }
            state["p2pGenericArrayPresent"] =
                skillInformation?.SkillGenericValueArray != null ? 1 : 0;
            if (skillInformation?.SkillGenericValueArray != null)
            {
                state["p2pGenericArray"] = skillInformation.SkillGenericValueArray
                    .Select(value => (object)value).ToList();
            }
            Dictionary<string, object> genericKeys = new Dictionary<string, object>();
            if (skillInformation?.SkillGenericKeyAndValue != null)
            {
                foreach (KeyValuePair<string, int> key in
                    skillInformation.SkillGenericKeyAndValue)
                {
                    genericKeys[key.Key] = key.Value;
                }
            }
            state["p2pGenericKeys"] = genericKeys;
            if (skillInformation != null)
            {
                state["p2pIntLists"] = new Dictionary<string, object>
                {
                    ["cantAtkBaseIds"] = ToObjectList(
                        skillInformation.CantAtkUnitBaseCardIdList),
                    ["decreaseTurnStartPP"] = ToObjectList(
                        skillInformation.DecreaseTurnStartPPList),
                    ["cantEvolution"] = ToObjectList(
                        skillInformation.CantEvolutionList),
                    ["skillHeal"] = ToObjectList(
                        skillInformation.SkillHealList)
                };
                state["p2pSkillCollections"] =
                    new Dictionary<string, object>
                    {
                        ["turnBuff"] = skillInformation.TurnBuffCountList
                            .Where(value => value != null)
                            .Select(value => (object)new Dictionary<string, object>
                            {
                                ["turn"] = value.Turn,
                                ["turnOwner"] =
                                    GetAbsoluteTurnOwner(value.IsSelfTurn)
                            }).ToList(),
                        ["tokenDraw"] = skillInformation.TokenDrawModifiers
                            .Where(value => value != null)
                            .Select(value => (object)new Dictionary<string, object>
                            {
                                ["cardId"] = value.CardId,
                                ["count"] = value.MultiplyCount
                            }).ToList(),
                        ["lifeHistory"] = CaptureLifeHistory(
                            skillInformation.LifeModifierList),
                        ["causedDamage"] = CaptureTurnValueCollection(
                            skillInformation.CausedDamageModifierList),
                        ["ppAdd"] = CaptureTurnValueCollection(
                            skillInformation.PpAddList)
                    };
                state["p2pCardReferences"] = new Dictionary<string, object>
                {
                    ["randomSelected"] = CaptureCardReferences(
                        skillInformation.RandomSelectedCardList),
                    ["skillDrew"] = CaptureCardReferences(
                        skillInformation.SkillDrewCardList),
                    ["savedTargets"] = CaptureCardReferences(
                        skillInformation.SavedTargetList),
                    ["savedBurialTargets"] = CaptureCardReferences(
                        skillInformation.SavedBurialRiteTargetList),
                    ["lastBurialTargets"] = CaptureCardReferences(
                        skillInformation.LastBurialRiteCardList),
                    ["getOn"] = CaptureCardReferences(
                        skillInformation.GetOnCards),
                    ["getOff"] = CaptureCardReferences(card.GetOffCards)
                };
                Dictionary<string, object> savedTargetIds =
                    new Dictionary<string, object>();
                foreach (KeyValuePair<long, List<int>> saved in
                    skillInformation.SavedTargetCardIdDict.OrderBy(
                        value => value.Key))
                {
                    savedTargetIds[saved.Key.ToString(
                        CultureInfo.InvariantCulture)] = ToObjectList(saved.Value);
                }
                state["p2pSavedTargetIds"] = savedTargetIds;
                state["p2pPreprocess"] = new Dictionary<string, object>
                {
                    ["normal"] = CapturePreprocessCollection(card.NormalSkills),
                    ["evolution"] = CapturePreprocessCollection(
                        card.EvolutionSkills)
                };
            }
            if (skillInformation != null)
            {
                state["p2pUnionBurstCount"] = skillInformation.UnionBurstCount;
                state["p2pSkyboundArtCount"] = skillInformation.SkyboundArtCount;
                state["p2pSuperSkyboundArtCount"] =
                    skillInformation.SuperSkyboundArtCount;

                List<object> fusionState = new List<object>();
                if (skillInformation.FusionIngredients != null)
                {
                    foreach (FusionIngredientInfo ingredient in
                        skillInformation.FusionIngredients)
                    {
                        if (ingredient?.Card == null || ingredient.Card.Index <= 0)
                        {
                            continue;
                        }
                        fusionState.Add(new Dictionary<string, object>
                        {
                            ["idx"] = ingredient.Card.Index,
                            ["cardId"] = ingredient.Card.CardId,
                            ["turn"] = ingredient.FusionTurn
                        });
                    }
                }
                state["p2pFusion"] = fusionState;
            }
            else
            {
                state["p2pGenericKeys"] = genericKeys;
                state["p2pUnionBurstCount"] = 10;
                state["p2pSkyboundArtCount"] = 10;
                state["p2pSuperSkyboundArtCount"] = 15;
                state["p2pFusion"] = new List<object>();
            }

            CardParameter baseParameter = card.BaseParameter;
            if (baseParameter == null)
            {
                return state;
            }

            // Use absolute values for the few card properties accepted by
            // CardDataModel.  This makes repeated P2P snapshots idempotent even
            // when the original modifier was an add/half/temporary modifier.
            if (card.IsUnit && card.Atk != card.BaseAtk)
            {
                state["setAtk"] = card.Atk;
            }
            if (card.IsUnit && card.MaxLife != card.BaseMaxLife)
            {
                state["setLife"] = card.MaxLife;
            }
            if (card.ChantCount != baseParameter.ChantCount)
            {
                state["setChantCount"] = card.ChantCount;
            }
            // ReplaceReceivedCard interprets these two wire values as the
            // reduction from the built-in defaults, rather than the remaining
            // count.  Sending the current count here would apply the modifier
            // twice and make hand/deck condition checks disagree.
            if (card.HasUnionBurst &&
                card.SkillApplyInformation != null &&
                card.SkillApplyInformation.UnionBurstCount != 10)
            {
                state["unionburst"] = 10 -
                    card.SkillApplyInformation.UnionBurstCount;
            }
            if (card.HasSkyboundArt &&
                card.SkillApplyInformation != null &&
                card.SkillApplyInformation.SkyboundArtCount != 10)
            {
                state["skyboundArt"] = 10 -
                    card.SkillApplyInformation.SkyboundArtCount;
            }
            if (card.Clan != baseParameter.Clan)
            {
                state["clan"] = (int)card.Clan;
            }
            if (card.Tribe != null && baseParameter.Tribe != null &&
                !card.Tribe.SequenceEqual(baseParameter.Tribe))
            {
                state["tribe"] = string.Join(",", card.Tribe.Select(
                    tribe => tribe.ToString()));
            }

            string attachedSkills = GetAttachedSkillState(card);
            if (!string.IsNullOrEmpty(attachedSkills))
            {
                state["attachTarget"] = attachedSkills;
            }

            List<BattleCardBase> fusionIngredients = card.FusionIngredients;
            if (fusionIngredients != null && fusionIngredients.Count > 0)
            {
                state["fusion"] = fusionIngredients
                    .Where(ingredient => ingredient != null && ingredient.Index > 0)
                    .Select(ingredient => (object)ingredient.Index)
                    .ToList();
            }
            return state;
        }

        private static Dictionary<string, object> CaptureCardModifierState(
            BattleCardBase card,
            SkillApplyInformation information)
        {
            return new Dictionary<string, object>
            {
                ["offense"] = CaptureOffenseModifiers(
                    information?.OffenseModifierList),
                ["life"] = CaptureLifeModifiers(
                    information?.LifeModifierList),
                ["cost"] = CaptureCostModifiers(card?.CostModifierList),
                ["chant"] = CaptureChantCountModifiers(
                    information?.ChantCountModifierList)
            };
        }

        private static List<object> CaptureOffenseModifiers(
            IEnumerable<ICardOffenseModifier> modifiers)
        {
            List<object> result = new List<object>();
            if (modifiers == null)
            {
                return result;
            }
            foreach (ICardOffenseModifier modifier in modifiers)
            {
                if (modifier is OffenseAddModifier add)
                {
                    result.Add(CaptureModifier("add", add.Offense));
                }
                else if (modifier is OffenseSetModifier set)
                {
                    result.Add(CaptureModifier("set", set.Offense));
                }
                else if (modifier is OffenseMultiplyModifier multiply)
                {
                    result.Add(CaptureModifier("multiply", multiply.Multipli));
                }
            }
            return result;
        }

        private static List<object> CaptureLifeModifiers(
            IEnumerable<ICardLifeModifier> modifiers)
        {
            List<object> result = new List<object>();
            if (modifiers == null)
            {
                return result;
            }
            foreach (ICardLifeModifier modifier in modifiers)
            {
                Dictionary<string, object> captured = null;
                if (modifier is LifeAddModifier add)
                {
                    captured = CaptureModifier("add", add.Life);
                }
                else if (modifier is LifeSetModifier set)
                {
                    captured = CaptureModifier("set", set.Life);
                }
                else if (modifier is LifeMultiplyModifier multiply)
                {
                    captured = CaptureModifier("multiply", multiply.Multipli);
                }
                else if (modifier is DamageCardParameterModifier damage)
                {
                    captured = CaptureTurnModifier("damage", damage);
                }
                else if (modifier is HealCardParameterModifier heal)
                {
                    captured = CaptureTurnModifier("heal", heal);
                }
                if (captured != null)
                {
                    result.Add(captured);
                }
            }
            return result;
        }

        private static List<object> CaptureCostModifiers(
            IEnumerable<ICardCostModifier> modifiers)
        {
            List<object> result = new List<object>();
            if (modifiers == null)
            {
                return result;
            }
            foreach (ICardCostModifier modifier in modifiers)
            {
                Dictionary<string, object> captured = null;
                if (modifier is CostAddModifier add)
                {
                    captured = CaptureModifier("add", add.Cost);
                }
                else if (modifier is CostSetModifier set)
                {
                    captured = CaptureModifier("set", set.Cost);
                }
                else if (modifier is CostHalfRoundUpModifier)
                {
                    captured = new Dictionary<string, object>
                    {
                        ["kind"] = "halfUp"
                    };
                }
                else if (modifier is CostHalfRoundDownModifier)
                {
                    captured = new Dictionary<string, object>
                    {
                        ["kind"] = "halfDown"
                    };
                }
                if (captured != null)
                {
                    captured["resident"] = modifier.IsResidentModifier ? 1 : 0;
                    result.Add(captured);
                }
            }
            return result;
        }

        private static List<object> CaptureChantCountModifiers(
            IEnumerable<ICardChantCountModifier> modifiers)
        {
            List<object> result = new List<object>();
            if (modifiers == null)
            {
                return result;
            }
            foreach (ICardChantCountModifier modifier in modifiers)
            {
                if (modifier is ChantCountAddModifier add)
                {
                    result.Add(CaptureModifier("add", add.ChantCount));
                }
                else if (modifier is ChantCountSetModifier set)
                {
                    result.Add(CaptureModifier("set", set.ChantCount));
                }
            }
            return result;
        }

        private static Dictionary<string, object> CaptureModifier(
            string kind,
            int value)
        {
            return new Dictionary<string, object>
            {
                ["kind"] = kind,
                ["value"] = value
            };
        }

        private static Dictionary<string, object> CaptureTurnModifier(
            string kind,
            TurnAndIntValue value)
        {
            Dictionary<string, object> result = CaptureModifier(kind, value.Value);
            result["turn"] = value.Turn;
            result["turnOwner"] = GetAbsoluteTurnOwner(value.IsSelfTurn);
            return result;
        }

        private static List<object> CaptureLifeHistory(
            IEnumerable<ICardLifeModifier> modifiers)
        {
            List<object> result = new List<object>();
            if (modifiers == null)
            {
                return result;
            }

            foreach (ICardLifeModifier modifier in modifiers)
            {
                string kind;
                TurnAndIntValue value;
                if (modifier is DamageCardParameterModifier damage)
                {
                    kind = "damage";
                    value = damage;
                }
                else if (modifier is HealCardParameterModifier heal)
                {
                    kind = "heal";
                    value = heal;
                }
                else
                {
                    continue;
                }
                result.Add(new Dictionary<string, object>
                {
                    ["kind"] = kind,
                    ["value"] = value.Value,
                    ["turn"] = value.Turn,
                    ["turnOwner"] = GetAbsoluteTurnOwner(value.IsSelfTurn)
                });
            }
            return result;
        }

        private static List<object> CaptureTurnValueCollection(
            IEnumerable<TurnAndIntValue> values)
        {
            return values == null
                ? new List<object>()
                : values.Where(value => value != null)
                    .Select(value => (object)new Dictionary<string, object>
                    {
                        ["value"] = value.Value,
                        ["turn"] = value.Turn,
                        ["turnOwner"] = GetAbsoluteTurnOwner(value.IsSelfTurn)
                    }).ToList();
        }

        private static List<object> ToObjectList(IEnumerable<int> values)
        {
            return values == null
                ? new List<object>()
                : values.Select(value => (object)value).ToList();
        }

        private static List<object> CaptureCardReferences(
            IEnumerable<BattleCardBase> cards)
        {
            List<object> result = new List<object>();
            if (cards == null)
            {
                return result;
            }

            bool localOwnerIsHost = Role == P2PRole.Host;
            foreach (BattleCardBase referencedCard in cards)
            {
                if (referencedCard == null || referencedCard.Index <= 0)
                {
                    continue;
                }
                bool ownerIsHost = referencedCard.IsPlayer
                    ? localOwnerIsHost
                    : !localOwnerIsHost;
                result.Add(new Dictionary<string, object>
                {
                    ["idx"] = referencedCard.Index,
                    ["cardId"] = referencedCard.CardId,
                    ["owner"] = ownerIsHost ? 1 : 0
                });
            }
            return result;
        }

        private static List<object> CapturePreprocessCollection(
            IEnumerable<SkillBase> skills)
        {
            List<object> result = new List<object>();
            if (skills == null)
            {
                return result;
            }

            foreach (SkillBase skill in skills)
            {
                if (skill == null)
                {
                    result.Add(new Dictionary<string, object>());
                    continue;
                }

                List<object> items = new List<object>();
                foreach (SkillPreprocessBase preprocess in
                    skill.PreprocessList ?? new List<SkillPreprocessBase>())
                {
                    if (preprocess == null)
                    {
                        items.Add(new Dictionary<string, object>());
                        continue;
                    }
                    items.Add(new Dictionary<string, object>
                    {
                        ["type"] = preprocess.GetType().FullName,
                        ["fields"] = CaptureMutableSimpleFields(preprocess)
                    });
                }
                result.Add(new Dictionary<string, object>
                {
                    ["type"] = skill.GetType().FullName,
                    ["items"] = items
                });
            }
            return result;
        }

        private static Dictionary<string, object> CaptureMutableSimpleFields(
            object target)
        {
            Dictionary<string, object> result =
                new Dictionary<string, object>();
            if (target == null)
            {
                return result;
            }

            for (Type current = target.GetType(); current != null;
                current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .OrderBy(field => field.Name, StringComparer.Ordinal))
                {
                    if (field.IsStatic || field.IsInitOnly || field.IsLiteral ||
                        !IsSimpleStateType(field.FieldType) ||
                        result.ContainsKey(field.Name))
                    {
                        continue;
                    }
                    try
                    {
                        result[field.Name] = field.GetValue(target);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            return result;
        }

        private static Dictionary<string, object> CaptureSimpleBackingProperties(
            object target,
            HashSet<string> excludedNames)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            if (target == null)
            {
                return result;
            }

            foreach (PropertyInfo property in target.GetType().GetProperties(
                BindingFlags.Instance | BindingFlags.Public |
                    BindingFlags.NonPublic))
            {
                if (property.GetIndexParameters().Length > 0 ||
                    excludedNames.Contains(property.Name) ||
                    !IsSimpleStateType(property.PropertyType) ||
                    !TryFindBackingField(target.GetType(), property.Name,
                        out FieldInfo field) || field.IsStatic || field.IsInitOnly ||
                    field.IsLiteral)
                {
                    continue;
                }

                try
                {
                    result[property.Name] = field.GetValue(target);
                }
                catch (Exception)
                {
                }
            }
            return result;
        }

        private static readonly HashSet<string> HiddenCardPrimitiveExclusions =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "CardId",
                "IsPlayer",
                "IsFirstTurn",
                "IsOnMove",
                "IsSelfTurn",
                "IsTokenLoad",
                "NormalIndividualId",
                "EvolutionIndividualId",
                "BaseAtk",
                "BaseCost",
                "BaseMaxLife",
                "Atk",
                "Cost",
                "Life",
                "MaxLife",
                "SpellChargeCount",
                "ChantCount",
                "GenericValueArray"
            };

        private static readonly HashSet<string> HiddenSkillPrimitiveExclusions =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "Player",
                "Enemy",
                "SkillGenericValueArray",
                "SkillGenericKeyAndValue",
                "UnionBurstCount",
                "SkyboundArtCount",
                "SuperSkyboundArtCount",
                "UnionBurstCountModifierList",
                "SkyboundArtCountModifierList",
                "SuperSkyboundArtCountModifierList",
                "FusionIngredients",
                "AttachedSkillsInfo",
                "GetOnCards"
            };

        private static string GetAttachedSkillState(BattleCardBase card)
        {
            try
            {
                AttachedSkillInformation attached = card.SkillApplyInformation?
                    .AttachedSkillsInfo;
                if (attached?.CreatorSkillList == null)
                {
                    return string.Empty;
                }

                List<string> publishedSkills = new List<string>();
                for (int i = 0; i < attached.CreatorSkillList.Count; i++)
                {
                    SkillBase skill = attached.CreatorSkillList[i];
                    if (skill == null)
                    {
                        continue;
                    }
                    int count = NetworkBattleGenericTool.GetPublishSkillCount(skill);
                    if (count >= 0)
                    {
                        publishedSkills.Add(count.ToString(
                            System.Globalization.CultureInfo.InvariantCulture));
                        continue;
                    }

                    // Private creator skills do not receive a published count.
                    // ReplaceReceivedCard also accepts ownerCardId|skillIndex|evo,
                    // which lets the peer reconstruct these attachments directly.
                    if (i < attached.OwnerCardIdList.Count &&
                        i < attached.CreatorSkillIndexList.Count)
                    {
                        int ownerCardId = attached.OwnerCardIdList[i];
                        int skillIndex = attached.CreatorSkillIndexList[i];
                        if (ownerCardId > 0 && skillIndex >= 0)
                        {
                            bool isEvolution = skill.SkillPrm?.ownerCard?.EvolutionSkills
                                ?.Contains(skill) == true;
                            publishedSkills.Add(string.Join("|", new[]
                            {
                                ownerCardId.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture),
                                skillIndex.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture),
                                isEvolution ? "1" : "0"
                            }));
                        }
                    }
                }
                return string.Join(",", publishedSkills);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static List<object> GetOrCreateKnownList(
            Dictionary<string, object> data)
        {
            if (data.TryGetValue("knownList", out object rawKnownList) &&
                rawKnownList is List<object> knownList)
            {
                return knownList;
            }

            List<object> result = new List<object>();
            if (rawKnownList is IEnumerable enumerable && !(rawKnownList is string))
            {
                foreach (object item in enumerable)
                {
                    result.Add(item);
                }
            }
            data["knownList"] = result;
            return result;
        }

        private static void MergeKnownCardState(
            List<object> knownList,
            Dictionary<string, object> state)
        {
            int index = Convert.ToInt32(state["idx"]);
            foreach (object item in knownList)
            {
                if (!(item is Dictionary<string, object> known) ||
                    !IsSelfKnownCard(known) || !KnownCardContainsIndex(known, index))
                {
                    continue;
                }

                // Native messages may group several indices under idxList.  A
                // snapshot carries one complete card state, so keep it as a
                // separate entry instead of overwriting the group's scalar idx.
                if (known.ContainsKey("idxList") && !known.ContainsKey("idx"))
                {
                    continue;
                }

                bool isDelta = IsHiddenCardStateDelta(state);
                if (!isDelta)
                {
                    foreach (string stateKey in HiddenCardStateKeys)
                    {
                        if (!state.ContainsKey(stateKey))
                        {
                            known.Remove(stateKey);
                        }
                    }
                }
                foreach (KeyValuePair<string, object> field in state)
                {
                    if (string.Equals(field.Key, HiddenCardStateDeltaKey,
                            StringComparison.Ordinal) ||
                        string.Equals(field.Key, HiddenCardStateRemovedFieldsKey,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }
                    known[field.Key] = field.Value;
                }
                if (state.TryGetValue(HiddenCardStateRemovedFieldsKey,
                        out object rawRemoved) && rawRemoved is IEnumerable removed &&
                    !(rawRemoved is string))
                {
                    foreach (object rawField in removed)
                    {
                        string removedField = rawField?.ToString();
                        if (!string.IsNullOrEmpty(removedField))
                        {
                            known.Remove(removedField);
                        }
                    }
                }
                return;
            }
            knownList.Add(state);
        }

        private static readonly string[] HiddenCardStateKeys =
        {
            "cost",
            "spellboost",
            "setAtk",
            "setLife",
            "setChantCount",
            "unionburst",
            "skyboundArt",
            "clan",
            "tribe",
            "attachTarget",
            "fusion",
            "p2pCardPrimitive",
            "p2pSkillPrimitive",
            "p2pLifeState",
            "p2pDamagedCounter",
            "p2pModifiers",
            "p2pSkillActivationIds",
            "p2pMaxAttackableCount",
            "p2pSkillActivatedCount",
            "p2pSkillActivatedWrap",
            "p2pSkillRandomArrayPresent",
            "p2pSkillRandomArray",
            "p2pGenericArrayPresent",
            "p2pGenericArray",
            "p2pGenericKeys",
            "p2pIntLists",
            "p2pSkillCollections",
            "p2pCardReferences",
            "p2pSavedTargetIds",
            "p2pPreprocess",
            "p2pUnionBurstCount",
            "p2pSkyboundArtCount",
            "p2pSuperSkyboundArtCount",
            "p2pFusion"
        };

        private static readonly HashSet<string> PlayerHistoryListNameSet =
            new HashSet<string>(
                P2PPlayerHistoryPolicy.SynchronizedListNames,
                StringComparer.Ordinal);

        private static readonly string[] PlayerHistoryScalarNames =
            P2PPlayerHistoryPolicy.SynchronizedScalarNames.ToArray();

        private static readonly HashSet<string> PlayerHistoryScalarNameSet =
            new HashSet<string>(PlayerHistoryScalarNames, StringComparer.Ordinal);

        private static bool TryGetPlayerHistoryListMember(
            BattlePlayerBase player,
            string name,
            out Type listType,
            out object value)
        {
            listType = null;
            value = null;
            if (player == null || !PlayerHistoryListNameSet.Contains(name))
            {
                return false;
            }
            try
            {
                if (TryFindInstanceProperty(player.GetType(), name,
                        out PropertyInfo property))
                {
                    listType = property.PropertyType;
                    value = property.GetValue(player, null);
                    return true;
                }
                if (TryFindInstanceField(player.GetType(), name,
                        out FieldInfo field))
                {
                    listType = field.FieldType;
                    value = field.GetValue(player);
                    return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        private static bool IsSelfKnownCard(Dictionary<string, object> known)
        {
            if (known == null || !known.TryGetValue("isSelf", out object rawSelf))
            {
                return false;
            }
            try
            {
                return Convert.ToInt32(rawSelf) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool KnownCardContainsIndex(
            Dictionary<string, object> known,
            int index)
        {
            if (known.TryGetValue("idx", out object rawIndex))
            {
                try
                {
                    return Convert.ToInt32(rawIndex) == index;
                }
                catch (Exception)
                {
                }
            }
            if (!known.TryGetValue("idxList", out object rawIndices) ||
                rawIndices is string || !(rawIndices is IEnumerable enumerable))
            {
                return false;
            }
            foreach (object rawValue in enumerable)
            {
                try
                {
                    if (Convert.ToInt32(rawValue) == index)
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

        private static Dictionary<string, object> CaptureBattleState()
        {
            if (!(BattleManagerBase.GetIns() is NetworkBattleManagerBase manager) ||
                manager.BattlePlayer == null || manager.BattleEnemy == null)
            {
                return null;
            }

            BattlePlayerBase host = Role == P2PRole.Host
                ? manager.BattlePlayer
                : manager.BattleEnemy;
            BattlePlayerBase guest = Role == P2PRole.Host
                ? manager.BattleEnemy
                : manager.BattlePlayer;
            return new Dictionary<string, object>
            {
                ["host"] = CapturePlayerState(host),
                ["guest"] = CapturePlayerState(guest)
            };
        }

        private static Dictionary<string, object> CapturePlayerState(
            BattlePlayerBase player)
        {
            bool localOwnerIsHost = Role == P2PRole.Host;
            bool ownerIsHost = player.IsPlayer
                ? localOwnerIsHost
                : !localOwnerIsHost;
            return new Dictionary<string, object>
            {
                ["life"] = player.Class?.Life ?? 0,
                ["maxLife"] = player.Class?.MaxLife ?? 0,
                ["pp"] = player.Pp,
                ["ppTotal"] = player.PpTotal,
                ["ep"] = player.CurrentEpCount,
                ["turn"] = player.Turn,
                ["isTurn"] = player.IsSelfTurn,
                ["extraTurn"] = player.extraTurnCount,
                ["deckCount"] = player.DeckCardList?.Count ?? 0,
                ["deck"] = FormatCardIndices(player.DeckCardList),
                ["deckState"] = FormatPrivateCardStates(player.DeckCardList),
                ["hand"] = FormatCardIndices(player.HandCardList),
                ["handState"] = FormatPrivateCardStates(player.HandCardList),
                ["cemetery"] = FormatPublicCards(player.CemeteryList),
                ["banish"] = FormatCardIndices(player.BanishList),
                ["field"] = FormatFieldCards(player.InPlayCards),
                ["history"] = CapturePlayerHistoryDiagnosticState(
                    player, ownerIsHost)
            };
        }

        private static Dictionary<string, object>
            CapturePlayerHistoryDiagnosticState(
                BattlePlayerBase player,
                bool ownerIsHost)
        {
            Dictionary<string, object> state =
                CapturePlayerHistoryState(player, ownerIsHost);
            Dictionary<string, object> result =
                new Dictionary<string, object>();
            if (state.TryGetValue("scalars", out object rawScalars) &&
                rawScalars is Dictionary<string, object> scalars)
            {
                foreach (KeyValuePair<string, object> value in scalars)
                {
                    result["scalar." + value.Key] = value.Value;
                }
            }
            if (state.TryGetValue("lists", out object rawLists) &&
                rawLists is Dictionary<string, object> lists)
            {
                foreach (KeyValuePair<string, object> value in lists)
                {
                    result["list." + value.Key] = JsonConvert.SerializeObject(
                        value.Value, P2PJson.Settings);
                }
            }
            return result;
        }

        private static string FormatCardIndices(IEnumerable<BattleCardBase> cards)
        {
            return cards == null
                ? string.Empty
                : string.Join(",", cards.Where(card => card != null)
                    .Select(card => $"{card.Index}:{card.CardId}"));
        }

        private static string FormatPrivateCardStates(
            IEnumerable<BattleCardBase> cards)
        {
            if (cards == null)
            {
                return string.Empty;
            }

            List<string> states = new List<string>();
            foreach (BattleCardBase card in cards.Where(card => card != null)
                .OrderBy(card => card.Index))
            {
                try
                {
                    string attach = GetAttachedSkillState(card);
                    string tribe = card.Tribe == null
                        ? string.Empty
                        : string.Join(",", card.Tribe.Select(value => value.ToString()));
                    SkillApplyInformation skillInformation =
                        card.SkillApplyInformation as SkillApplyInformation;
                    string fusion = skillInformation?.FusionIngredients == null
                        ? string.Empty
                        : string.Join(",", skillInformation.FusionIngredients
                            .Where(value => value?.Card != null)
                            .Select(value => value.Card.Index.ToString(
                                CultureInfo.InvariantCulture) + "@" +
                                value.FusionTurn.ToString(CultureInfo.InvariantCulture)));
                    string genericArray = skillInformation?.SkillGenericValueArray == null
                        ? "-"
                        : string.Join(",", skillInformation.SkillGenericValueArray
                            .Select(value => value.ToString(CultureInfo.InvariantCulture)));
                    string genericKeys = skillInformation?.SkillGenericKeyAndValue == null
                        ? string.Empty
                        : string.Join(",", skillInformation.SkillGenericKeyAndValue
                            .OrderBy(value => value.Key, StringComparer.Ordinal)
                            .Select(value => value.Key + "=" +
                                value.Value.ToString(CultureInfo.InvariantCulture)));
                    string randomArray = skillInformation?.SkillRandomArray == null
                        ? "-"
                        : string.Join(",", skillInformation.SkillRandomArray
                            .Select(value => value.ToString(CultureInfo.InvariantCulture)));
                    string referenceState = skillInformation == null
                        ? string.Empty
                        : string.Join("|", new[]
                        {
                            "random=" + FormatCardReferences(
                                skillInformation.RandomSelectedCardList),
                            "drew=" + FormatCardReferences(
                                skillInformation.SkillDrewCardList),
                            "saved=" + FormatCardReferences(
                                skillInformation.SavedTargetList),
                            "burial=" + FormatCardReferences(
                                skillInformation.LastBurialRiteCardList),
                            "getOn=" + FormatCardReferences(
                                skillInformation.GetOnCards),
                            "getOff=" + FormatCardReferences(card.GetOffCards)
                        });
                    string savedTargetIds = skillInformation == null
                        ? string.Empty
                        : string.Join(",", skillInformation.SavedTargetCardIdDict
                            .OrderBy(value => value.Key)
                            .Select(value => value.Key.ToString(
                                    CultureInfo.InvariantCulture) + "=" +
                                string.Join("/", value.Value)));
                    string modifierState = FormatCardModifierDiagnosticState(
                        card, skillInformation);
                    string turnHistory = skillInformation == null
                        ? string.Empty
                        : "caused=" + JsonConvert.SerializeObject(
                            CaptureTurnValueCollection(
                                skillInformation.CausedDamageModifierList),
                            P2PJson.Settings) + ",ppAdd=" +
                            JsonConvert.SerializeObject(
                                CaptureTurnValueCollection(
                                    skillInformation.PpAddList),
                                P2PJson.Settings);
                    string activationIds = card.SkillActivationList == null
                        ? string.Empty
                        : string.Join(",", card.SkillActivationList
                            .Select(value => value.SkillId.ToString(
                                CultureInfo.InvariantCulture)));
                    states.Add(
                        $"idx={card.Index}[id={card.CardId},cost={card.Cost}," +
                        $"spellboost={card.SpellChargeCount},atk={card.Atk}," +
                        $"life={card.Life}/{card.MaxLife},chant={card.ChantCount}," +
                        $"union={skillInformation?.UnionBurstCount.ToString(CultureInfo.InvariantCulture) ?? "-"}," +
                        $"skybound={skillInformation?.SkyboundArtCount.ToString(CultureInfo.InvariantCulture) ?? "-"}," +
                        $"superSkybound={skillInformation?.SuperSkyboundArtCount.ToString(CultureInfo.InvariantCulture) ?? "-"}," +
                        $"clan={(int)card.Clan},tribe={tribe},attach={attach}," +
                        $"fusion={fusion},skillCount={card.SkillActivatedCount}," +
                        $"generic=[{genericArray}],genericKeys=[{genericKeys}]," +
                        $"random=[{randomArray}],refs=[{referenceState}]," +
                        $"savedIds=[{savedTargetIds}],mods=[{modifierState}]," +
                        $"turnHistory=[{turnHistory}],damageCount=" +
                        $"{card.DamagedCounter?.SelfTurnDamage ?? 0}/" +
                        $"{card.DamagedCounter?.OpponentTurnDamage ?? 0}," +
                        $"maxAttackCount={card.MaxAttackableCount}," +
                        $"activationIds=[{activationIds}]]");
                }
                catch (Exception ex)
                {
                    // A partially initialized token should not prevent the
                    // remaining battle state from being compared or logged.
                    states.Add($"{card.Index}:{card.CardId}:state-error:{ex.GetType().Name}");
                }
            }
            return string.Join(";", states);
        }

        private static string FormatCardModifierDiagnosticState(
            BattleCardBase card,
            SkillApplyInformation information)
        {
            Dictionary<string, object> modifiers =
                CaptureCardModifierState(card, information);
            List<string> result = new List<string>();
            foreach (KeyValuePair<string, object> entry in modifiers)
            {
                if (entry.Value is ICollection collection && collection.Count == 0)
                {
                    continue;
                }
                result.Add(entry.Key + "=" + JsonConvert.SerializeObject(
                    entry.Value, P2PJson.Settings));
            }
            return string.Join("|", result);
        }

        private static string FormatCardReferences(
            IEnumerable<BattleCardBase> cards)
        {
            if (cards == null)
            {
                return string.Empty;
            }
            bool localOwnerIsHost = Role == P2PRole.Host;
            return string.Join(",", cards.Where(card => card != null)
                .Select(card =>
                {
                    bool ownerIsHost = card.IsPlayer
                        ? localOwnerIsHost
                        : !localOwnerIsHost;
                    return (ownerIsHost ? "H" : "G") + ":" +
                        card.Index.ToString(CultureInfo.InvariantCulture);
                }));
        }

        private static string FormatPublicCards(IEnumerable<BattleCardBase> cards)
        {
            return cards == null
                ? string.Empty
                : string.Join(",", cards.Where(card => card != null)
                    .Select(card => $"{card.Index}:{card.CardId}"));
        }

        private static string FormatFieldCards(IEnumerable<BattleCardBase> cards)
        {
            return cards == null
                ? string.Empty
                : string.Join(",", cards.Where(card => card != null)
                    .Select(card =>
                        $"{card.Index}:{card.CardId}:{card.Atk}:{card.Life}:" +
                        $"{card.MaxLife}:{(card.IsEvolution ? 1 : 0)}:{card.ChantCount}"));
        }

        private static void TryCheckPendingBattleStates()
        {
            if (PendingBattleStateChecks.Count == 0)
            {
                return;
            }

            // A checkpoint is meaningful only after the exact ordered action
            // that carried it has left the native receive pipeline. Comparing
            // while that action is still queued or animating turns a valid
            // following PlayActions result into a false TurnStart desync.
            if (receivedBattleActionInjectionPending ||
                receivedBattleActionPendingUntilVfx ||
                processingReceivedBattleAction)
            {
                return;
            }

            NetworkBattleManagerBase manager =
                BattleManagerBase.GetIns() as NetworkBattleManagerBase;
            bool effectsComplete = manager?.VfxMgr != null && manager.VfxMgr.IsEnd;
            DateTime now = DateTime.UtcNow;
            while (PendingBattleStateChecks.Count > 0)
            {
                PendingBattleStateCheck pending = PendingBattleStateChecks.Peek();
                bool timedOut = now >= pending.DeadlineUtc;
                if (!effectsComplete)
                {
                    if (timedOut && !pending.StallReported)
                    {
                        pending.StallReported = true;
                        Plugin.Logger.LogWarning(
                            "[P2P] State checkpoint is waiting for actionId=" +
                            pending.ActionId + ", uri=" + pending.Uri + "; " +
                            DescribeEffectQueue(manager) + ".");
                    }
                    return;
                }

                Dictionary<string, object> actual = CaptureBattleState();
                if (actual == null)
                {
                    if (!timedOut)
                    {
                        return;
                    }
                    PendingBattleStateChecks.Dequeue();
                    ReportBattleDiagnostic(
                        $"State check after actionId={pending.ActionId}, " +
                        $"uri={pending.Uri} failed: " +
                        "the network battle manager is unavailable.");
                    continue;
                }

                IReadOnlyList<string> differences =
                    P2PBattleStateDiagnostics.Compare(pending.Expected, actual);
                PendingBattleStateChecks.Dequeue();
                if (differences.Count == 0)
                {
                    if (!string.IsNullOrEmpty(pending.InjectionError))
                    {
                        ReportBattleDiagnostic(
                            $"Message injection failed after actionId={pending.ActionId}, " +
                            $"uri={pending.Uri}, although the " +
                            "state snapshot currently matches the peer: " +
                            pending.InjectionError);
                        continue;
                    }
                    Plugin.Logger.LogDebug(
                        $"[P2P] State synchronized after actionId={pending.ActionId}, " +
                        $"uri={pending.Uri}.");
                    continue;
                }

                string injectionReason = string.IsNullOrEmpty(pending.InjectionError)
                    ? string.Empty
                    : " Message injection failed: " + pending.InjectionError;
                ReportBattleDiagnostic(
                    $"DATA DESYNC after actionId={pending.ActionId}, " +
                    $"uri={pending.Uri}." +
                    injectionReason + " " +
                    P2PBattleStateDiagnostics.DescribeDifferences(differences));
            }
        }

        private static string DescribeEffectQueue(NetworkBattleManagerBase manager)
        {
            try
            {
                string current = manager?.VfxMgr?.CurrentVfxName;
                List<string> queued = manager?.VfxMgr?.GetSequentialVfxPlayerNames();
                List<string> names = queued ?? new List<string>();
                const int maxNames = 4;
                string preview = string.Join(",", names.Take(maxNames));
                if (names.Count > maxNames)
                {
                    preview += $",+{names.Count - maxNames}";
                }
                return $"currentVfx={current ?? "<none>"}, " +
                    $"queuedCount={names.Count}, queuedVfx=[{preview}]";
            }
            catch (Exception ex)
            {
                return "effect queue details unavailable: " + ex.Message;
            }
        }

        private static void ReportBattleDiagnostic(string message)
        {
            bool isDesync = IsDesyncDiagnostic(message);
            if (isDesync)
            {
                Plugin.Logger.LogError("[P2P] " + message);
            }
            else
            {
                Plugin.Logger.LogWarning("[P2P] " + message);
            }
            if (Role == P2PRole.Guest && IsActive)
            {
                SendWire(new P2PWireMessage
                {
                    Type = "diagnostic",
                    BattleId = BattleId,
                    Error = message,
                    Data = new Dictionary<string, object>
                    {
                        ["severity"] = isDesync ? "error" : "warning"
                    }
                });
            }
        }

        private static bool IsDesyncDiagnostic(string message)
        {
            return !string.IsNullOrEmpty(message) &&
                (message.StartsWith("DATA DESYNC", StringComparison.Ordinal) ||
                 message.IndexOf("state mismatch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 message.IndexOf("differing field", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void TrySynchronizeOpponentRoomState()
        {
            if (!pendingOpponentSync || !IsActive || RemoteProfile == null)
            {
                return;
            }

            RoomBase room = RoomBase.GetInstance();
            RoomConnectController controller = RoomBase.ConnectController;
            if (room == null || controller == null || controller.OppoCtrl == null ||
                !room.IsInitializeDone)
            {
                return;
            }

            Player opponent = controller.OppoCtrl.Target;
            bool targetWasValid = opponent != null && opponent.IsValid;
            bool roomHadOpponent = room.IsExistOppo;
            if (targetWasValid && roomHadOpponent)
            {
                pendingOpponentSync = false;
                Plugin.Logger.LogInfo(
                    $"[P2P] Native room state already contains opponent " +
                    $"'{opponent.Name}' ({opponent.ViewerId}); fallback was not needed.");
                return;
            }

            try
            {
                Dictionary<string, object> received = CreateRoomPlayerData(RemoteProfile);
                controller.InitializeOpponentPlayer();
                controller.OppoCtrl.Target.DeckCreateNumber = 0;
                controller.OppoCtrl.Target.OnEnter(received);
                controller.FormatEventHandler.OnEnterOpponent();
                controller.OppoCtrl.EnterRoomServer(string.Empty);

                // Visitors normally get this state during SetupFirstOnly. If the initial
                // RoomEntry arrived before the regular listener existed, refresh it here.
                if (!room.IsExistOppo)
                {
                    room.SetExistOpponent(true, true);
                }

                pendingOpponentSync = false;
                Plugin.Logger.LogInfo(
                    $"[P2P] Synchronized opponent room state as {Role}: " +
                    $"'{RemoteProfile.UserName}' ({RemoteProfile.ViewerId}); " +
                    $"before targetValid={targetWasValid}, roomHasOpponent={roomHadOpponent}; " +
                    $"after targetValid={controller.OppoCtrl.Target.IsValid}, " +
                    $"roomHasOpponent={room.IsExistOppo}.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError("[P2P] Failed to synchronize opponent room state: " + ex);
            }
        }

        private static bool SendWire(P2PWireMessage message)
        {
            if (transport == null || !transport.Send(message))
            {
                LastError = "The P2P connection is not available.";
                return false;
            }
            return true;
        }

        private static void HandlePeerDisconnected(string error)
        {
            if (peerDisconnected)
            {
                return;
            }
            peerDisconnected = true;
            LastError = error;

            if (!string.IsNullOrEmpty(activeAuthorityExecutionRequestId))
            {
                Plugin.Logger.LogWarning(
                    "[P2P] Peer disconnected while Host authority execution " +
                    "was still active (requestId=" +
                    activeAuthorityExecutionRequestId + "). Late native emits " +
                    "will remain suppressed until its VFX callback exits.");
            }

            if (BattleManagerBase.GetIns() is NetworkBattleManagerBase)
            {
                InjectPeerDisconnectResult();
            }
        }

        private static void InjectPeerDisconnectResult()
        {
            if (finishResultSent || currentAgent == null)
            {
                return;
            }

            int localResult = P2PBattleResult.ResolveLocalResultAfterDisconnect(
                localRetired,
                GetLocalFinishResult());

            finishResultSent = true;
            Inject(new Dictionary<string, object>
            {
                ["uri"] = NetworkBattleDefine.NetworkBattleURI.JudgeResult.ToString(),
                ["result"] = localResult,
                ["viewerId"] = 0,
                ["bid"] = BattleId ?? string.Empty,
                ["playSeq"] = Role == P2PRole.Host
                    ? ++hostPlaySequence
                    : ++guestPlaySequence,
                ["time"] = UnixMilliseconds()
            });
            Plugin.Logger.LogInfo(
                $"[P2P] Peer disconnected; received local result {localResult}, " +
                $"localRetired={localRetired}.");
        }

        private static void InjectRoomRelease()
        {
            if (roomReleaseInjected)
            {
                return;
            }
            roomReleaseInjected = true;
            Dictionary<string, object> release = new Dictionary<string, object>
            {
                ["uri"] = PlayerController.ROOM_URI.Release.ToString(),
                ["resultCode"] = (int)NetworkBattleDefine.ReceiveNodeResultCode.Success,
                ["isSelf"] = 0,
                ["viewerId"] = RemoteProfile?.ViewerId ?? 0,
                ["bid"] = BattleId ?? string.Empty,
                ["playSeq"] = Role == P2PRole.Host
                    ? ++hostPlaySequence
                    : ++guestPlaySequence,
                ["time"] = UnixMilliseconds()
            };
            Inject(release);
        }

        private static void ForceExitDisconnectedRoom(RoomBase room)
        {
            if (roomReleaseInjected || room == null)
            {
                return;
            }

            roomReleaseInjected = true;
            try
            {
                // Prevent CheckMatching from starting after the disconnect dialog is created.
                if (room._isRoomReady && !room._isMatchingStart)
                {
                    room._isRoomReady = false;
                }
                room.DisconnectForceExitRoom();
            }
            catch (Exception ex)
            {
                roomReleaseInjected = false;
                Plugin.Logger.LogError("[P2P] Failed to exit the disconnected room: " + ex);
            }
        }

        private static bool IsBattleScene()
        {
            try
            {
                UIManager manager = UIManager.GetInstance();
                return manager != null && manager.GetCurrentScene() == UIManager.ViewScene.Battle;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsRoomAgentReady()
        {
            return currentAgent.CurrentMatchingStatus == RealTimeNetworkAgent.MatchingStatus.Room ||
                currentAgent.CurrentMatchingStatus == RealTimeNetworkAgent.MatchingStatus.RoomReady;
        }

        private static Dictionary<string, object> CreateRoomPlayerData(P2PProfile profile)
        {
            return new Dictionary<string, object>
            {
                ["userName"] = profile.UserName ?? string.Empty,
                ["emblemId"] = profile.EmblemId,
                ["degreeId"] = profile.DegreeId,
                ["countryCode"] = profile.CountryCode ?? string.Empty,
                ["rank"] = profile.Rank,
                ["maxRank"] = profile.Rank,
                ["isGuildMember"] = false,
                ["isGuildJoined"] = false,
                ["oppoId"] = profile.ViewerId,
                ["isOfficial"] = profile.IsOfficial ? 1 : 0,
                ["isFriend"] = 0
            };
        }

        private static P2PProfile CreateLocalProfile()
        {
            return ProfileOfflineData.CreateP2PProfile(P2PIdentity.ViewerId);
        }

        private static List<int> Shuffle(IEnumerable<int> cards)
        {
            List<int> result = cards.ToList();
            Random random = new Random(CreatePositiveInt());
            for (int i = result.Count - 1; i > 0; i--)
            {
                int target = random.Next(i + 1);
                int value = result[i];
                result[i] = result[target];
                result[target] = value;
            }
            return result;
        }

        private static List<object> CreateIndexList(IList<int> indices)
        {
            List<object> result = new List<object>(indices.Count);
            for (int i = 0; i < indices.Count; i++)
            {
                result.Add(new Dictionary<string, object>
                {
                    ["pos"] = i,
                    ["idx"] = indices[i]
                });
            }
            return result;
        }

        private static List<int> ToIntList(object value)
        {
            if (value is IEnumerable<object> objectValues)
            {
                return objectValues.Select(Convert.ToInt32).ToList();
            }
            if (value is IEnumerable<int> intValues)
            {
                return intValues.ToList();
            }
            return new List<int>();
        }

        private static int SourceViewerId(bool sourceIsHost)
        {
            return sourceIsHost ? LocalProfile.ViewerId : RemoteProfile?.ViewerId ?? 0;
        }

        private static int ResolveLocalCardId(int index)
        {
            BattleCardBase card = ResolveLocalCard(index);
            return card?.CardId ?? 0;
        }

        private static int ResolveLocalCardCost(int index)
        {
            BattleCardBase card = ResolveLocalCard(index);
            return card?.Cost ?? -1;
        }

        private static IEnumerable<P2PFusionIngredientState>
            ResolveLocalFusionIngredients(int index)
        {
            BattleCardBase card = ResolveLocalCard(index);
            bool ownerIsHost = Role == P2PRole.Host;
            SkillApplyInformation information = card?
                .SkillApplyInformation as SkillApplyInformation;
            List<P2PFusionIngredientState> current = information?.FusionIngredients == null
                ? new List<P2PFusionIngredientState>()
                : information.FusionIngredients
                .Where(ingredient => ingredient?.Card != null &&
                    ingredient.Card.Index > 0)
                .Select(ingredient => new P2PFusionIngredientState(
                    ingredient.Card.Index,
                    ingredient.Card.CardId,
                    ingredient.FusionTurn))
                .ToList();

            string key = FusionIngredientSnapshotKey(ownerIsHost, index);
            if (current.Count > 0)
            {
                LocalFusionIngredientSnapshots[key] = current
                    .Select(item => new P2PFusionIngredientState(
                        item.Index, item.CardId, item.Turn))
                    .ToList();
                return current;
            }

            return LocalFusionIngredientSnapshots.TryGetValue(key,
                    out List<P2PFusionIngredientState> snapshot)
                ? snapshot.ToList()
                : Enumerable.Empty<P2PFusionIngredientState>();
        }

        internal static void RememberLocalFusionIngredientState(
            BattleCardBase fusionCard)
        {
            if (!IsActive || fusionCard == null || fusionCard.Index <= 0)
            {
                return;
            }

            SkillApplyInformation information = fusionCard.SkillApplyInformation as
                SkillApplyInformation;
            if (information?.FusionIngredients == null)
            {
                return;
            }

            // IsPlayer is relative to this process. Keep the cache keyed by
            // absolute owner as a Host can execute a Guest fusion against its
            // BattleEnemy object, and both players may legitimately reuse the
            // same card index.
            bool ownerIsHost = fusionCard.IsPlayer == (Role == P2PRole.Host);
            string key = FusionIngredientSnapshotKey(ownerIsHost, fusionCard.Index);
            LocalFusionIngredientSnapshots[key] = information.FusionIngredients
                .Where(ingredient => ingredient?.Card != null &&
                    ingredient.Card.Index > 0)
                .Select(ingredient => new P2PFusionIngredientState(
                    ingredient.Card.Index,
                    ingredient.Card.CardId,
                    ingredient.FusionTurn))
                .ToList();
            Plugin.Logger.LogDebug(
                $"[P2P] Captured cumulative fusion state for {SideName(ownerIsHost)} " +
                $"idx={fusionCard.Index}: " +
                $"[{string.Join(",", LocalFusionIngredientSnapshots[key].Select(
                    item => item.Index.ToString(CultureInfo.InvariantCulture)))}].");
        }

        private static string FusionIngredientSnapshotKey(
            bool ownerIsHost,
            int index)
        {
            return HiddenStateKey(ownerIsHost, index);
        }

        private static IEnumerable<int> ResolveLocalBurialRiteSkillIndexes(int index)
        {
            BattleCardBase card = ResolveLocalCard(index);
            if (card?.Skills == null)
            {
                return Enumerable.Empty<int>();
            }

            List<int> result = new List<int>();
            int skillIndex = 0;
            foreach (SkillBase skill in card.Skills)
            {
                if (skill != null && skill.IsBurialRite)
                {
                    result.Add(skillIndex);
                }
                skillIndex++;
            }
            return result;
        }

        private static BattleCardBase ResolveLocalCard(int index)
        {
            NetworkBattleManagerBase manager =
                BattleManagerBase.GetIns() as NetworkBattleManagerBase;
            if (manager == null || manager.BattlePlayer == null)
            {
                return null;
            }

            return NetworkBattleGenericTool.GetIndexToCardBase(
                manager, manager.BattlePlayer, index);
        }

        private static string SideName(bool isHost)
        {
            return isHost ? "Host" : "Guest";
        }

        private static void ResetSession()
        {
            sessionGeneration++;
            transport?.Stop(false);
            transport = null;
            currentAgent = null;
            Role = P2PRole.None;
            IsActive = false;
            ConnectionCode = null;
            RoomId = null;
            BattleId = null;
            LocalProfile = null;
            RemoteProfile = null;
            LocalDeck = null;
            RemoteDeck = null;
            Rules = null;
            JoinFinished = false;
            JoinSucceeded = false;
            LastError = null;
            hostPlaySequence = 0;
            guestPlaySequence = 0;
            GuestDeliverySequence.Reset();
            DeferredGuestDeliveries.Clear();
            DeferredAgentDeliveries.Clear();
            RoomRoundState.Reset();
            ResetBattleState();
            localEmitSequence = 1;
            peerDisconnected = false;
            roomReleaseInjected = false;
            pendingOpponentSync = false;
            hostDeckEntry = null;
            guestDeckEntry = null;
            authorityRequestSequence = 0;
            authorityTransitionSequence = 0;
            authorityResultActionSequence = 0;
            guestAuthorityBusy = false;
            guestAuthorityRequestId = null;
            guestAuthorityRequestSentUtc = DateTime.MinValue;
            receivedAuthorityRequestId = null;
            activeAuthorityExecutionRequestId = null;
            activeAuthorityExecutionStartedUtc = DateTime.MinValue;
            processedAuthorityRequests.Clear();
            authorityRequestTimes.Clear();
            completedAuthorityRequestIds.Clear();
            appliedAuthorityResultBoundaries.Clear();
            localAuthorityChoiceCardIndexes.Clear();
            authorityGuestKnownIndices.Clear();
            foreach (HashSet<int> indices in authorityKnownPrivateIndicesByOwner.Values)
            {
                indices.Clear();
            }
            authorityPrivateStateSignatures.Clear();
            authorityPrivateStates.Clear();
            actionPreHiddenCardStates.Clear();
            authorityPlayerHistorySignatures.Clear();
            authorityPlayerHistoryRevisions.Clear();
            authorityLocalReplayActive = false;
            authorityReplayDispatchActive = false;
        }

        private static void Enqueue(Action action)
        {
            Enqueue(sessionGeneration, action);
        }

        private static void Enqueue(int generation, Action action)
        {
            MainThreadActions.Enqueue(() =>
            {
                if (generation == sessionGeneration)
                {
                    action();
                }
            });
        }

        private static string CreateNumericId()
        {
            long value = 100000000000L + (uint)CreatePositiveInt();
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static int CreatePositiveInt()
        {
            byte[] bytes = new byte[4];
            using (RandomNumberGenerator random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return BitConverter.ToInt32(bytes, 0) & int.MaxValue;
        }

        private static long UnixMilliseconds()
        {
            return (DateTime.UtcNow.Ticks - 621355968000000000L) /
                TimeSpan.TicksPerMillisecond;
        }

        private static IPAddress ParseAddress(string value, IPAddress fallback)
        {
            if (IPAddress.TryParse(value, out IPAddress address))
            {
                return address;
            }
            if (fallback != null)
            {
                return fallback;
            }
            throw new FormatException("Invalid IP address: " + value);
        }

        private static IPAddress FindAdvertisedAddress(AddressFamily preferredFamily)
        {
            IEnumerable<UnicastIPAddressInformation> addresses = NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up &&
                    network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses);
            IPAddress preferred = addresses
                .Select(item => item.Address)
                .FirstOrDefault(address => address.AddressFamily == preferredFamily &&
                    IsUsableAdvertisedAddress(address) && !IPAddress.IsLoopback(address));
            if (preferred != null)
            {
                return preferred;
            }
            return null;
        }

        private static bool IsUsableAdvertisedAddress(IPAddress address)
        {
            if (address == null || IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address))
            {
                return false;
            }
            return address.AddressFamily != AddressFamily.InterNetworkV6 ||
                (!address.IsIPv6LinkLocal && !address.IsIPv6Multicast && address.ScopeId == 0);
        }

        private sealed class PendingBattleStateCheck
        {
            internal PendingBattleStateCheck(
                string uri,
                Dictionary<string, object> expected,
                DateTime deadlineUtc,
                string actionId)
            {
                Uri = uri;
                Expected = expected;
                DeadlineUtc = deadlineUtc;
                ActionId = string.IsNullOrEmpty(actionId)
                    ? "<missing>"
                    : actionId;
            }

            internal string Uri { get; }
            internal Dictionary<string, object> Expected { get; }
            internal DateTime DeadlineUtc { get; }
            internal string ActionId { get; }
            internal string InjectionError { get; set; }
            internal bool StallReported { get; set; }
        }

        private sealed class PendingAuthorityReplayAction
        {
            internal PendingAuthorityReplayAction(
                RealTimeNetworkBattleAgent agent,
                Dictionary<string, object> data)
            {
                Agent = agent;
                Data = data;
            }

            internal RealTimeNetworkBattleAgent Agent { get; }
            internal Dictionary<string, object> Data { get; }
        }

        private sealed class AppliedHiddenCardState
        {
            internal BattleCardBase Card { get; set; }
            internal string Signature { get; set; }
            internal bool NativeStateInherited { get; set; }
            internal bool StateComplete { get; set; }
            internal DateTime NextRetryUtc { get; set; }
        }

        private sealed class HiddenCardLifeStateModifier : ICardLifeModifier
        {
            private readonly int life;
            private readonly int maxLife;

            internal HiddenCardLifeStateModifier(int life, int maxLife)
            {
                this.life = life;
                this.maxLife = maxLife;
            }

            public bool IsChangeMaxLife => false;
            public bool IsClearBeforeModifier => false;
            public int CalcLife(int baseLife) => life;
            public int CalcMaxLife(int baseMaxLife) => maxLife;
        }

        private sealed class PendingPlayerHistoryState
        {
            internal int Owner { get; set; }
            internal int Revision { get; set; }
            internal Dictionary<string, object> State { get; set; }
            internal bool ReadyToApply { get; set; }
            internal int Attempts { get; set; }
            internal DateTime FirstSeenUtc { get; set; }
            internal DateTime NextAttemptUtc { get; set; }
            internal bool WarningLogged { get; set; }
            internal string LastUnresolved { get; set; }
        }

        private sealed class AuthoritativeSkillTargetBatch
        {
            internal AuthoritativeSkillTargetBatch(
                IEnumerable<Dictionary<string, object>> entries,
                string actionId,
                string uri)
            {
                Entries = entries?.ToList() ??
                    new List<Dictionary<string, object>>();
                ActionId = string.IsNullOrEmpty(actionId)
                    ? "<missing>"
                    : actionId;
                Uri = uri ?? "?";
            }

            internal List<Dictionary<string, object>> Entries { get; }
            internal string ActionId { get; }
            internal string Uri { get; }
            internal bool ReadyForCleanup { get; set; }
            internal bool Rejected { get; set; }
        }

        internal sealed class AuthoritativeSkillEvaluationScope
        {
            internal AuthoritativeSkillEvaluationScope(
                SkillBase skill,
                bool isSource,
                Dictionary<string, object> entry)
            {
                Skill = skill;
                IsSource = isSource;
                Entry = entry ?? new Dictionary<string, object>();
                Values = new List<AuthoritativeSkillOptionValue>();
                PreprocessResults = new List<bool>();
                ConditionResults = new List<AuthoritativeSkillConditionResult>();

                if (isSource)
                {
                    return;
                }
                if (Entry.TryGetValue("values", out object rawValues) &&
                    rawValues is IEnumerable values && !(rawValues is string))
                {
                    foreach (object rawValue in values)
                    {
                        if (!(rawValue is Dictionary<string, object> item) ||
                            !item.TryGetValue("keyword", out object rawKeyword) ||
                            string.IsNullOrEmpty(rawKeyword?.ToString()) ||
                            !TryGetStateInt(item, "value", out int value))
                        {
                            continue;
                        }
                        Values.Add(new AuthoritativeSkillOptionValue(
                            rawKeyword.ToString(), value));
                    }
                }
                if (Entry.TryGetValue("preprocess", out object rawPreprocess) &&
                    rawPreprocess is IEnumerable preprocess &&
                    !(rawPreprocess is string))
                {
                    foreach (object rawResult in preprocess)
                    {
                        try
                        {
                            PreprocessResults.Add(Convert.ToInt32(
                                rawResult, CultureInfo.InvariantCulture) != 0);
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                if (Entry.TryGetValue("conditions", out object rawConditions) &&
                    rawConditions is IEnumerable conditions && !(rawConditions is string))
                {
                    foreach (object rawCondition in conditions)
                    {
                        if (!(rawCondition is Dictionary<string, object> item) ||
                            !TryGetStateInt(item, "result", out int conditionResult))
                        {
                            continue;
                        }
                        bool prePlay = TryGetStateInt(item, "prePlay", out int rawPrePlay) &&
                            rawPrePlay != 0;
                        bool skipTarget = TryGetStateInt(item, "skipTarget", out int rawSkipTarget) &&
                            rawSkipTarget != 0;
                        int ordinal = TryGetStateInt(item, "ordinal", out int rawOrdinal)
                            ? rawOrdinal : ConditionResults.Count;
                        ConditionResults.Add(new AuthoritativeSkillConditionResult(
                            ordinal, prePlay, skipTarget, conditionResult != 0));
                    }
                }
            }

            internal SkillBase Skill { get; }
            internal bool IsSource { get; }
            internal Dictionary<string, object> Entry { get; }
            internal List<AuthoritativeSkillOptionValue> Values { get; }
            internal List<bool> PreprocessResults { get; }
            internal List<AuthoritativeSkillConditionResult> ConditionResults { get; }
            internal int NextPreprocessResult { get; set; }
        }

        internal sealed class AuthoritativeSkillOptionValue
        {
            internal AuthoritativeSkillOptionValue(string keyword, int value)
            {
                Keyword = keyword ?? string.Empty;
                Value = value;
            }

            internal string Keyword { get; }
            internal int Value { get; }
        }

        internal sealed class AuthoritativeSkillConditionResult
        {
            internal AuthoritativeSkillConditionResult(
                int ordinal,
                bool isPrePlay,
                bool isSkipTarget,
                bool result)
            {
                Ordinal = ordinal;
                IsPrePlay = isPrePlay;
                IsSkipTarget = isSkipTarget;
                Result = result;
            }

            internal int Ordinal { get; }
            internal bool IsPrePlay { get; }
            internal bool IsSkipTarget { get; }
            internal bool Result { get; }
        }

        private sealed class AuthoritativeSkillEvaluationBatch
        {
            internal AuthoritativeSkillEvaluationBatch(
                IEnumerable<Dictionary<string, object>> entries,
                string actionId,
                string uri)
            {
                Entries = entries?.ToList() ??
                    new List<Dictionary<string, object>>();
                ActionId = string.IsNullOrEmpty(actionId)
                    ? "<missing>"
                    : actionId;
                Uri = uri ?? "?";
            }

            internal List<Dictionary<string, object>> Entries { get; }
            internal string ActionId { get; }
            internal string Uri { get; }
            internal bool ReadyForCleanup { get; set; }
            internal bool Rejected { get; set; }
        }
    }
}
