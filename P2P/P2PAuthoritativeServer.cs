using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace Shadowbus
{
    // The wire payload emitted by NetworkBattleSender is a client request. It
    // is not automatically the payload that another NetworkBattleReceiver is
    // allowed to consume. Keep the two concepts separate so Host-only server
    // fields can be populated in one place as this refactor progresses.
    internal sealed class P2PClientBattleRequest
    {
        internal P2PClientBattleRequest(
            bool sourceIsHost,
            int sourceViewerId,
            string uri,
            int sourceSequence,
            Dictionary<string, object> data)
        {
            SourceIsHost = sourceIsHost;
            SourceViewerId = sourceViewerId;
            Uri = uri ?? string.Empty;
            SourceSequence = sourceSequence;
            Data = data ?? new Dictionary<string, object>();
        }

        internal bool SourceIsHost { get; }
        internal int SourceViewerId { get; }
        internal string Uri { get; }
        internal int SourceSequence { get; }
        internal Dictionary<string, object> Data { get; }
    }

    // This object is the Host's canonical representation of one accepted
    // client request. Later stages add Host-owned state validation, private
    // card identity resolution, condition results, and RNG results here.
    internal sealed class P2PHostAuthoritativeAction
    {
        internal P2PHostAuthoritativeAction(
            P2PClientBattleRequest request,
            P2PBattleRoute route,
            int serverActionId,
            Dictionary<string, object> serverResponse)
        {
            Request = request;
            Route = route;
            ServerActionId = serverActionId;
            ServerResponse = serverResponse;
        }

        internal P2PClientBattleRequest Request { get; }
        internal P2PBattleRoute Route { get; }
        // Monotonic within one P2P battle round. This is a Host-side routing
        // identity, not a replacement for the native playSeq assigned when
        // the response is delivered to a client.
        internal int ServerActionId { get; }
        internal Dictionary<string, object> ServerResponse { get; }
    }

    // A recipient-specific, server-authored message. It deliberately contains
    // only a native battle payload; transport fields such as playSeq, viewerId
    // and bid are assigned by P2PRuntime.Deliver.
    internal sealed class P2PServerBattleDelivery
    {
        internal P2PServerBattleDelivery(
            bool toHost,
            int sourceViewerId,
            string uri,
            int serverActionId,
            Dictionary<string, object> data)
        {
            ToHost = toHost;
            SourceViewerId = sourceViewerId;
            Uri = uri ?? string.Empty;
            ServerActionId = serverActionId;
            Data = data ?? new Dictionary<string, object>();
        }

        internal bool ToHost { get; }
        internal int SourceViewerId { get; }
        internal string Uri { get; }
        internal int ServerActionId { get; }
        internal Dictionary<string, object> Data { get; }
    }

    internal static class P2PAuthoritativeServer
    {
        internal const int ZoneUnknown = -1;
        internal const int ZoneDeck = 0;
        internal const int ZoneHand = 10;
        internal const int ZoneField = 20;
        internal const int ZoneCemetery = 30;
        internal const int ZoneBanish = 40;
        internal const int ZoneNone = 50;
        internal const int ZoneFusionIngredient = 60;
        internal const int ZoneRiding = 70;
        internal const int ZoneReservation = 80;
        internal const int ZoneUnite = 90;
        internal const int ZoneBlackHole = 999;

        // NetworkBattleReceiver ignores unknown fields. Keeping an explicit
        // Host action identity on every response lets diagnostics trace a
        // receiver failure back to exactly one client request without changing
        // the native playSeq stream.
        internal const string ServerActionIdKey = "p2pServerActionId";
        internal const string ClientSequenceKey = "p2pClientSequence";
        internal const string ServerStateRevisionKey = "p2pServerStateRevision";

        private static int nextServerActionId;
        // These are the fields NetworkBattleReceiver.MakeReceiveCardData
        // already knows how to map into CardDataModel. Preserve them in the
        // Host cache so hand/deck mutations travel through knownList/uList,
        // rather than through a post-operation private snapshot.
        private static readonly string[] NativeCardStateKeys =
        {
            "cardId",
            "cost",
            "is_open",
            "addAtk",
            "setAtk",
            "addLife",
            "setLife",
            "clan",
            "tribe",
            "attachTarget",
            "spellboost",
            "addChantCount",
            "setChantCount",
            "unionburst",
            "skyboundArt",
            "fusion",
            "isInvoke"
        };
        private static readonly string[] InternalCardStateKeys =
        {
            "skill",
            "skillKeyCardIdx",
            "randomTargetIdx",
            "attachTarget",
            "isInvoke",
            "p2pZone",
            // These fields are emitted by RegisterUnapproved.MakeUList and
            // are not all copied into CardDataModel. Keep them in the Host
            // ledger so later condition checks and fusion/activation actions
            // can use the same registration context as the native sender.
            "skillCardIdx",
            "publishedActiveSkillCount",
            "movement",
            "skillKeyCardIdxList",
            "attachedSkillsPublishCount",
            "fusionCount",
            "isShortageDeck",
            "isFlood",
            "hasGuard",
            "isWhenDraw",
            "skillTarget",
            "byOppo",
            "highlander"
        };
        // Absolute owner: Host=1, Guest=0. The server stores only protocol
        // state here; it must not inspect or replace a client-side BattleCard
        // object while preparing a response.
        private static readonly Dictionary<int, Dictionary<int, Dictionary<string, object>>>
            privateCardsByOwner = new Dictionary<int, Dictionary<int, Dictionary<string, object>>>
            {
                [0] = new Dictionary<int, Dictionary<string, object>>(),
                [1] = new Dictionary<int, Dictionary<string, object>>()
            };
        private static readonly Dictionary<int, Dictionary<int, int>>
            cardZonesByOwner = new Dictionary<int, Dictionary<int, int>>
            {
                [0] = new Dictionary<int, int>(),
                [1] = new Dictionary<int, int>()
            };
        private static readonly List<Dictionary<string, object>> actionHistory =
            new List<Dictionary<string, object>>();
        private static readonly HashSet<string> actionHistoryCounterKeys =
            new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<int, Dictionary<string, int>>
            historyCountersByOwner = new Dictionary<int, Dictionary<string, int>>
            {
                [0] = new Dictionary<string, int>(StringComparer.Ordinal),
                [1] = new Dictionary<string, int>(StringComparer.Ordinal)
            };
        private static readonly Dictionary<int, int> fusionCountsByOwner =
            new Dictionary<int, int> { [0] = 0, [1] = 0 };
        private static readonly Dictionary<int, Dictionary<string, object>>
            playerHistoryByOwner =
            new Dictionary<int, Dictionary<string, object>>
            {
                [0] = null,
                [1] = null
            };
        // State already delivered to each native receiver. The Host ledger
        // stores complete cards, while deliveries can be reduced to a field
        // delta relative to the receiver's private_state baseline.
        private static readonly Dictionary<string, Dictionary<string, object>>
            deliveredPrivateStates =
            new Dictionary<string, Dictionary<string, object>>(
                StringComparer.Ordinal);
        private static readonly List<Dictionary<string, object>> registerHistory =
            new List<Dictionary<string, object>>();
        // Random targets are generated by the native action executor on the
        // authoritative Host. Keep a compact audit trail of the exact native
        // uList/orderList values that crossed the server boundary. This is not
        // a second RNG: it verifies that the Host-generated result is present,
        // canonical, and never replaced by a client-side manifest.
        private static readonly List<Dictionary<string, object>> randomResultHistory =
            new List<Dictionary<string, object>>();
        private const int MaximumActionHistory = 4096;
        private const int MaximumRandomResultHistory = 1024;
        private const string HiddenStateDeltaKey = "p2pStateDelta";
        private const string HiddenStateRemovedFieldsKey = "p2pRemovedFields";
        private static int stateRevision;

        internal static void Reset()
        {
            nextServerActionId = 0;
            foreach (Dictionary<int, Dictionary<string, object>> cards in
                privateCardsByOwner.Values)
            {
                cards.Clear();
            }
            foreach (Dictionary<int, int> zones in cardZonesByOwner.Values)
            {
                zones.Clear();
            }
            actionHistory.Clear();
            actionHistoryCounterKeys.Clear();
            foreach (Dictionary<string, int> counters in historyCountersByOwner.Values)
            {
                counters.Clear();
            }
            fusionCountsByOwner[0] = 0;
            fusionCountsByOwner[1] = 0;
            playerHistoryByOwner[0] = null;
            playerHistoryByOwner[1] = null;
            deliveredPrivateStates.Clear();
            registerHistory.Clear();
            randomResultHistory.Clear();
            stateRevision = 0;
        }

        // private_state is the one-time source of both players' hidden-card
        // identities. Host receives Guest's copy over the wire and captures
        // its own copy locally. The structure intentionally mirrors the
        // existing payload instead of inventing a second card schema.
        internal static int RememberPrivateStateSnapshot(
            Dictionary<string, object> payload)
        {
            if (payload == null || !TryReadInt(payload, "owner", out int owner) ||
                (owner != 0 && owner != 1) ||
                !payload.TryGetValue("cards", out object rawCards) ||
                rawCards is string || !(rawCards is IEnumerable cards))
            {
                return 0;
            }

            Dictionary<int, Dictionary<string, object>> ownerCards =
                privateCardsByOwner[owner];
            ownerCards.Clear();
            cardZonesByOwner[owner].Clear();
            int stored = 0;
            foreach (object rawCard in cards)
            {
                if (!(rawCard is Dictionary<string, object> card) ||
                    !TryReadInt(card, "idx", out int index) || index <= 0 ||
                    !TryReadInt(card, "cardId", out int cardId) || cardId <= 0)
                {
                    continue;
                }
                Dictionary<string, object> clone = P2PJson.CloneDictionary(card);
                ownerCards[index] = clone;
                cardZonesByOwner[owner][index] =
                    TryReadInt(clone, "p2pZone", out int initialZone)
                        ? initialZone
                        : ZoneUnknown;
                stored++;
                // A player's private baseline is delivered to the opposing
                // native receiver. Seed that receiver's delivery baseline so
                // the first authoritative action can already be a delta.
                deliveredPrivateStates[PrivateDeliveryStateKey(1 - owner, owner,
                        index)] = P2PJson.CloneDictionary(clone);
            }
            return stored;
        }

        internal static bool TryGetCardState(
            int owner,
            int index,
            out Dictionary<string, object> state)
        {
            state = null;
            return (owner == 0 || owner == 1) && index > 0 &&
                privateCardsByOwner[owner].TryGetValue(index, out state);
        }

        internal static bool TryGetCardZone(
            int owner,
            int index,
            out int zone)
        {
            zone = ZoneUnknown;
            return (owner == 0 || owner == 1) && index > 0 &&
                cardZonesByOwner[owner].TryGetValue(index, out zone);
        }

        internal static int StateRevision => stateRevision;

        internal static IReadOnlyList<Dictionary<string, object>>
            GetActionHistorySnapshot()
        {
            return actionHistory
                .Select(entry => P2PJson.CloneDictionary(entry))
                .ToList();
        }

        internal static IReadOnlyDictionary<string, int>
            GetHistoryCounters(int owner)
        {
            if (owner != 0 && owner != 1)
            {
                return new Dictionary<string, int>();
            }
            return new Dictionary<string, int>(
                historyCountersByOwner[owner], StringComparer.Ordinal);
        }

        internal static int GetFusionCount(int owner)
        {
            return (owner == 0 || owner == 1) &&
                fusionCountsByOwner.TryGetValue(owner, out int count)
                ? count
                : 0;
        }

        internal static bool TryGetPlayerHistoryState(
            int owner,
            out Dictionary<string, object> state)
        {
            state = null;
            return (owner == 0 || owner == 1) &&
                playerHistoryByOwner.TryGetValue(owner,
                    out Dictionary<string, object> stored) &&
                stored != null &&
                (state = P2PJson.CloneDictionary(stored)) != null;
        }

        internal static IReadOnlyList<Dictionary<string, object>>
            GetRegisterHistorySnapshot()
        {
            return registerHistory
                .Select(entry => P2PJson.CloneDictionary(entry))
                .ToList();
        }

        internal static IReadOnlyList<Dictionary<string, object>>
            GetRandomResultHistorySnapshot()
        {
            return randomResultHistory
                .Select(entry => P2PJson.CloneDictionary(entry))
                .ToList();
        }

        internal static bool ValidateAndRecordNativeRandomResults(
            Dictionary<string, object> data,
            int serverActionId,
            bool sourceIsHost,
            out string error)
        {
            error = string.Empty;
            if (data == null)
            {
                return true;
            }

            HashSet<Dictionary<string, object>> visited =
                new HashSet<Dictionary<string, object>>();
            foreach (string listKey in new[] { "uList", "orderList" })
            {
                if (!data.TryGetValue(listKey, out object rawList))
                {
                    continue;
                }

                foreach (Dictionary<string, object> entry in
                    EnumerateRegisterDictionaries(rawList))
                {
                    if (!visited.Add(entry) ||
                        !entry.TryGetValue("randomTargetIdx", out object rawRandom))
                    {
                        continue;
                    }

                    List<object> values = NormalizeRandomTargetList(rawRandom);
                    if (values.Count == 0 && rawRandom != null)
                    {
                        error = "native randomTargetIdx was present but contained no integer result";
                        return false;
                    }
                    if (values.Any(value => !TryConvertInt(value, out int result) || result < 0))
                    {
                        error = "native randomTargetIdx contained a negative or invalid result";
                        return false;
                    }

                    // Re-write the exact field shape consumed by
                    // NetworkBattleReceiver.  The Host is the only producer of
                    // the result; the client request and compatibility manifest
                    // are never consulted here.
                    entry["randomTargetIdx"] = values;
                    randomResultHistory.Add(new Dictionary<string, object>
                    {
                        ["actionId"] = serverActionId,
                        ["sourceIsHost"] = sourceIsHost ? 1 : 0,
                        ["path"] = listKey,
                        ["values"] = CloneStateValue(values)
                    });
                }
            }

            if (randomResultHistory.Count > MaximumRandomResultHistory)
            {
                randomResultHistory.RemoveRange(
                    0, randomResultHistory.Count - MaximumRandomResultHistory);
            }
            return true;
        }

        internal static void ObserveDeal(
            IEnumerable<int> hostHandIndices,
            IEnumerable<int> guestHandIndices)
        {
            SetOwnerZones(1, hostHandIndices);
            SetOwnerZones(0, guestHandIndices);
        }

        internal static void ObserveMulligan(
            bool sourceIsHost,
            IEnumerable<int> returnedIndices,
            IEnumerable<int> currentHandIndices)
        {
            int owner = sourceIsHost ? 1 : 0;
            if (returnedIndices != null)
            {
                foreach (int index in returnedIndices.Where(index => index > 0))
                {
                    SetZone(owner, index, ZoneDeck);
                }
            }
            SetOwnerZones(owner, currentHandIndices);
        }

        internal static bool TryCreateAction(
            bool sourceIsHost,
            int sourceViewerId,
            Dictionary<string, object> sourceData,
            out P2PHostAuthoritativeAction action,
            out string error)
        {
            action = null;
            error = string.Empty;
            if (sourceData == null || !sourceData.TryGetValue("uri", out object rawUri) ||
                string.IsNullOrWhiteSpace(rawUri?.ToString()))
            {
                error = "the client request had no battle URI";
                return false;
            }

            string uri = rawUri.ToString();
            Dictionary<string, object> requestData = P2PJson.CloneDictionary(sourceData);
            int sourceSequence = TryReadInt(requestData, "pubSeq", out int sequence)
                ? sequence
                : TryReadInt(requestData, "actionSeq", out sequence)
                    ? sequence
                    : 0;
            P2PClientBattleRequest request = new P2PClientBattleRequest(
                sourceIsHost, sourceViewerId, uri, sourceSequence, requestData);
            P2PBattleRoute route = P2PBattleProtocol.GetRoute(uri);
            int serverActionId = ++nextServerActionId;
            ObserveClientRequest(request, serverActionId);

            // Build a distinct response object. The client request remains a
            // record of what NetworkBattleSender emitted; only this object is
            // allowed to enter a peer's NetworkBattleReceiver.
            Dictionary<string, object> response = BuildServerResponse(
                request, route, serverActionId);
            if (!ValidateAndRecordNativeRandomResults(
                    response, serverActionId, sourceIsHost, out string randomError))
            {
                error = randomError;
                return false;
            }
            if (!TryValidateServerResponse(request, route, response, out error))
            {
                return false;
            }

            action = new P2PHostAuthoritativeAction(
                request, route, serverActionId, response);
            return true;
        }

        internal static bool TryCreateDelivery(
            P2PHostAuthoritativeAction action,
            out P2PServerBattleDelivery delivery)
        {
            delivery = null;
            if (action == null || action.Route == P2PBattleRoute.Consume)
            {
                return false;
            }

            bool toHost = action.Route == P2PBattleRoute.Source
                ? action.Request.SourceIsHost
                : !action.Request.SourceIsHost;
            delivery = new P2PServerBattleDelivery(
                toHost,
                action.Request.SourceViewerId,
                action.Request.Uri,
                action.ServerActionId,
                BuildDeliveryPayload(action.ServerResponse, toHost ? 1 : 0));
            return true;
        }

        private static string PrivateDeliveryStateKey(
            int recipientOwner,
            int cardOwner,
            int index)
        {
            return recipientOwner.ToString() + ":" + cardOwner.ToString() +
                ":" + index.ToString();
        }

        private static Dictionary<string, object> BuildDeliveryPayload(
            Dictionary<string, object> response,
            int recipientOwner)
        {
            Dictionary<string, object> result =
                P2PJson.CloneDictionary(response);
            if (result.TryGetValue(P2PBattleProtocol.AuthorityHiddenStatesKey,
                    out object rawSnapshots) && rawSnapshots is IEnumerable snapshots &&
                !(rawSnapshots is string))
            {
                List<object> reducedSnapshots = new List<object>();
                foreach (object rawSnapshot in snapshots)
                {
                    if (!(rawSnapshot is Dictionary<string, object> snapshot) ||
                        !TryReadInt(snapshot, "owner", out int owner) ||
                        (owner != 0 && owner != 1))
                    {
                        continue;
                    }

                    List<object> reducedCards = new List<object>();
                    if (snapshot.TryGetValue("cards", out object rawCards) &&
                        rawCards is IEnumerable cards && !(rawCards is string))
                    {
                        foreach (object rawCard in cards)
                        {
                            if (!(rawCard is Dictionary<string, object> card) ||
                                !TryReadInt(card, "idx", out int index) || index <= 0)
                            {
                                continue;
                            }
                            string key = PrivateDeliveryStateKey(
                                recipientOwner, owner, index);
                            deliveredPrivateStates.TryGetValue(key,
                                out Dictionary<string, object> previous);
                            Dictionary<string, object> current =
                                P2PJson.CloneDictionary(card);
                            Dictionary<string, object> delta =
                                CreatePrivateCardDelta(previous, current);
                            if (delta != null)
                            {
                                reducedCards.Add(delta);
                            }
                            deliveredPrivateStates[key] = current;
                        }
                    }

                    List<object> reducedRemoved = new List<object>();
                    if (snapshot.TryGetValue("removed", out object rawRemoved) &&
                        rawRemoved is IEnumerable removed && !(rawRemoved is string))
                    {
                        foreach (object rawIndex in removed)
                        {
                            if (!TryConvertInt(rawIndex, out int index) || index <= 0)
                            {
                                continue;
                            }
                            reducedRemoved.Add(index);
                            deliveredPrivateStates.Remove(
                                PrivateDeliveryStateKey(recipientOwner,
                                    owner, index));
                        }
                    }

                    if (reducedCards.Count > 0 || reducedRemoved.Count > 0)
                    {
                        reducedSnapshots.Add(new Dictionary<string, object>
                        {
                            ["owner"] = owner,
                            ["cards"] = reducedCards,
                            ["removed"] = reducedRemoved
                        });
                    }
                }

                result.Remove("p2pHiddenOwner");
                result.Remove("p2pHiddenCards");
                result.Remove("p2pHiddenRemoved");
                if (reducedSnapshots.Count > 0)
                {
                    result[P2PBattleProtocol.AuthorityHiddenStatesKey] =
                        reducedSnapshots;
                    Dictionary<string, object> first =
                        reducedSnapshots.OfType<Dictionary<string, object>>()
                            .FirstOrDefault();
                    if (first != null && TryReadInt(first, "owner",
                            out int firstOwner))
                    {
                        result["p2pHiddenOwner"] = firstOwner;
                        result["p2pHiddenCards"] = first.TryGetValue(
                                "cards", out object cards)
                            ? P2PJson.CloneValue(cards)
                            : new List<object>();
                        result["p2pHiddenRemoved"] = first.TryGetValue(
                                "removed", out object removed)
                            ? P2PJson.CloneValue(removed)
                            : new List<object>();
                    }
                }
                else
                {
                    result.Remove(P2PBattleProtocol.AuthorityHiddenStatesKey);
                }
            }
            return result;
        }

        private static Dictionary<string, object> CreatePrivateCardDelta(
            Dictionary<string, object> previous,
            Dictionary<string, object> current)
        {
            if (current == null)
            {
                return null;
            }
            if (previous == null)
            {
                return current;
            }

            Dictionary<string, object> delta = new Dictionary<string, object>
            {
                ["idx"] = current.TryGetValue("idx", out object rawIndex)
                    ? CloneStateValue(rawIndex)
                    : 0,
                ["cardId"] = current.TryGetValue("cardId", out object rawCardId)
                    ? CloneStateValue(rawCardId)
                    : 0,
                [HiddenStateDeltaKey] = 1
            };
            bool changed = false;
            foreach (KeyValuePair<string, object> pair in current)
            {
                if (string.Equals(pair.Key, "idx", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!previous.TryGetValue(pair.Key, out object oldValue) ||
                    !StateValuesEqual(oldValue, pair.Value))
                {
                    delta[pair.Key] = CloneStateValue(pair.Value);
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
                delta[HiddenStateRemovedFieldsKey] = removed;
            }
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
                    Newtonsoft.Json.JsonConvert.SerializeObject(left),
                    Newtonsoft.Json.JsonConvert.SerializeObject(right),
                    StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return Equals(left, right);
            }
        }

        private static Dictionary<string, object> BuildServerResponse(
            P2PClientBattleRequest request,
            P2PBattleRoute route,
            int serverActionId)
        {
            // The original client sender already serializes orderList, uList,
            // keyAction and validation information from its native register
            // managers. The Host performs the server-side conversion from that
            // request representation to the receiver representation here.
            Dictionary<string, object> responseInput =
                P2PJson.CloneDictionary(request.Data);
            // The client extension is input to the Host ledger only. Rebuild
            // the extension from that ledger before the response is routed so
            // a Guest cannot accidentally become the author of a server
            // response. Native knownList/uList/orderList remain untouched and
            // continue to carry the operation itself.
            RebuildPrivateStateExtensionsFromLedger(request.Data,
                responseInput);
            if (route == P2PBattleRoute.Opponent)
            {
                    // NetworkBattleData.BeforeSettingReceiveData consumes
                // knownList before the operation begins. Populate any missing
                // identity from the Host cache now, at the same boundary the
                // official server provides its response data. This is not a
                // post-action snapshot or a client-object replacement.
                EnsureNativeKnownIdentities(request.SourceIsHost, responseInput);
                if (!request.SourceIsHost)
                {
                    // The official server, not the requesting client, owns
                    // private condition results.  When Guest input is routed
                    // into the Host's native receiver, remove only the
                    // client-produced skillConditionCheck registrations. With
                    // that list absent, NetworkExecutionInfoCreator follows
                    // its original local CheckCondition path against the
                    // Host's authoritative mirror. The Host's subsequent
                    // NetworkBattleSender response contains the canonical
                    // condition data for Guest consumption.
                    StripClientConditionRegistrations(responseInput);
                    StripClientEvaluationSideChannels(responseInput);
                }
            }

            // The native sender includes the fields it knows at the moment a
            // register is created.  A later action may reference the same
            // card without repeating every field, so fill only missing values
            // from the Host ledger before the response enters
            // NetworkBattleData.BeforeSettingReceiveData.  Explicit fields in
            // the native payload always win; this preserves the original
            // server response semantics and avoids post-operation snapshots.
            MergeCachedNativeStates(request.SourceIsHost, responseInput);
            NormalizeNativeRandomTargetFields(responseInput);

            Dictionary<string, object> response = route == P2PBattleRoute.Opponent
                ? P2PMessageTransform.PrepareOpponentBattleMessage(responseInput)
                : responseInput;

            // These values belong to the server response contract. They must
            // not be written back into request.Data, otherwise later logging
            // and validation can accidentally treat a response as a request.
            response[ServerActionIdKey] = serverActionId;
            if (request.SourceSequence != 0)
            {
                response[ClientSequenceKey] = request.SourceSequence;
            }
            if (P2PBattleProtocol.RequiresActiveTurnState(request.Uri))
            {
                response["turnState"] = 0;
            }
            response[ServerStateRevisionKey] = stateRevision;
            return response;
        }

        private static void RebuildPrivateStateExtensionsFromLedger(
            Dictionary<string, object> requestData,
            Dictionary<string, object> responseData)
        {
            if (requestData == null || responseData == null)
            {
                return;
            }

            Dictionary<int, HashSet<int>> requested =
                new Dictionary<int, HashSet<int>>
                {
                    [0] = new HashSet<int>(),
                    [1] = new HashSet<int>()
                };
            Dictionary<int, HashSet<int>> removed =
                new Dictionary<int, HashSet<int>>
                {
                    [0] = new HashSet<int>(),
                    [1] = new HashSet<int>()
                };

            Action<int, IEnumerable> collectCards = (owner, rawCards) =>
            {
                if ((owner != 0 && owner != 1) || rawCards == null)
                {
                    return;
                }
                foreach (object rawCard in rawCards)
                {
                    if (rawCard is Dictionary<string, object> card &&
                        TryReadInt(card, "idx", out int index) && index > 0)
                    {
                        requested[owner].Add(index);
                    }
                }
            };
            Action<int, IEnumerable> collectRemoved = (owner, rawValues) =>
            {
                if ((owner != 0 && owner != 1) || rawValues == null)
                {
                    return;
                }
                foreach (object rawIndex in rawValues)
                {
                    if (TryConvertInt(rawIndex, out int index) && index > 0)
                    {
                        removed[owner].Add(index);
                    }
                }
            };

            bool hadExtension = false;
            if (requestData.TryGetValue(
                    P2PBattleProtocol.AuthorityHiddenStatesKey,
                    out object rawSnapshots) && rawSnapshots is IEnumerable snapshots &&
                !(rawSnapshots is string))
            {
                hadExtension = true;
                foreach (object rawSnapshot in snapshots)
                {
                    if (!(rawSnapshot is Dictionary<string, object> snapshot) ||
                        !TryReadInt(snapshot, "owner", out int owner) ||
                        (owner != 0 && owner != 1))
                    {
                        continue;
                    }
                    if (snapshot.TryGetValue("cards", out object rawCards) &&
                        rawCards is IEnumerable cards && !(rawCards is string))
                    {
                        collectCards(owner, cards);
                    }
                    if (snapshot.TryGetValue("removed", out object rawRemoved) &&
                        rawRemoved is IEnumerable removedValues &&
                        !(rawRemoved is string))
                    {
                        collectRemoved(owner, removedValues);
                    }
                }
            }
            else if (requestData.TryGetValue("p2pHiddenOwner", out object rawOwner) &&
                TryConvertInt(rawOwner, out int owner) && (owner == 0 || owner == 1))
            {
                hadExtension = true;
                if (requestData.TryGetValue("p2pHiddenCards", out object rawCards) &&
                    rawCards is IEnumerable cards && !(rawCards is string))
                {
                    collectCards(owner, cards);
                }
                if (requestData.TryGetValue("p2pHiddenRemoved", out object rawRemoved) &&
                    rawRemoved is IEnumerable removedValues &&
                    !(rawRemoved is string))
                {
                    collectRemoved(owner, removedValues);
                }
            }

            responseData.Remove(P2PBattleProtocol.AuthorityHiddenStatesKey);
            responseData.Remove("p2pHiddenOwner");
            responseData.Remove("p2pHiddenCards");
            responseData.Remove("p2pHiddenRemoved");
            if (!hadExtension)
            {
                return;
            }

            List<object> rebuiltSnapshots = new List<object>();
            for (int owner = 0; owner <= 1; owner++)
            {
                List<object> cards = new List<object>();
                foreach (int index in requested[owner].OrderBy(value => value))
                {
                    if (privateCardsByOwner[owner].TryGetValue(index,
                            out Dictionary<string, object> state))
                    {
                        cards.Add(P2PJson.CloneDictionary(state));
                    }
                }
                if (cards.Count == 0 && removed[owner].Count == 0)
                {
                    continue;
                }
                rebuiltSnapshots.Add(new Dictionary<string, object>
                {
                    ["owner"] = owner,
                    ["cards"] = cards,
                    ["removed"] = removed[owner].OrderBy(value => value)
                        .Select(value => (object)value).ToList()
                });
            }

            if (rebuiltSnapshots.Count > 0)
            {
                responseData[P2PBattleProtocol.AuthorityHiddenStatesKey] =
                    rebuiltSnapshots;
                // Keep the legacy shape for current peers. It is generated
                // from the same Host ledger, never copied from the request.
                Dictionary<string, object> first =
                    rebuiltSnapshots.OfType<Dictionary<string, object>>()
                        .FirstOrDefault();
                if (first != null && TryReadInt(first, "owner", out int legacyOwner))
                {
                    responseData["p2pHiddenOwner"] = legacyOwner;
                    responseData["p2pHiddenCards"] = first.TryGetValue(
                            "cards", out object legacyCards)
                        ? P2PJson.CloneValue(legacyCards)
                        : new List<object>();
                    responseData["p2pHiddenRemoved"] = first.TryGetValue(
                            "removed", out object legacyRemoved)
                        ? P2PJson.CloneValue(legacyRemoved)
                        : new List<object>();
                }
            }
        }

        private static void NormalizeNativeRandomTargetFields(
            Dictionary<string, object> data)
        {
            if (data == null)
            {
                return;
            }

            // RegisterUnapproved.RandomTargetIdx is serialized by the native
            // sender as a list and consumed from uList by
            // NetworkBattleReceiver. Keep that representation canonical and
            // never source it from the compatibility action manifest.
            foreach (string listKey in new[] { "uList", "orderList" })
            {
                if (!data.TryGetValue(listKey, out object rawList))
                {
                    continue;
                }

                foreach (Dictionary<string, object> entry in
                    EnumerateRegisterDictionaries(rawList))
                {
                    if (entry.TryGetValue("randomTargetIdx", out object rawRandom))
                    {
                        entry["randomTargetIdx"] =
                            NormalizeRandomTargetList(rawRandom);
                        continue;
                    }

                }
            }
        }

        private static List<object> NormalizeRandomTargetList(object rawRandom)
        {
            List<object> result = new List<object>();
            if (rawRandom == null || rawRandom is string)
            {
                if (TryConvertInt(rawRandom, out int single))
                {
                    result.Add(single);
                }
                return result;
            }
            if (TryConvertInt(rawRandom, out int scalar))
            {
                result.Add(scalar);
                return result;
            }
            if (!(rawRandom is IEnumerable values))
            {
                return result;
            }
            foreach (object rawValue in values)
            {
                if (TryConvertInt(rawValue, out int value))
                {
                    result.Add(value);
                }
            }
            return result;
        }

        private static void StripClientConditionRegistrations(
            Dictionary<string, object> data)
        {
            if (data == null ||
                !data.TryGetValue("orderList", out object rawOrders) ||
                rawOrders is string || !(rawOrders is IEnumerable orders))
            {
                return;
            }

            List<object> filtered = new List<object>();
            foreach (object rawOrder in orders)
            {
                if (!(rawOrder is Dictionary<string, object> wrapper))
                {
                    filtered.Add(rawOrder);
                    continue;
                }

                Dictionary<string, object> clone =
                    P2PJson.CloneDictionary(wrapper);
                clone.Remove("skillConditionCheck");
                if (clone.Count > 0)
                {
                    filtered.Add(clone);
                }
            }
            data["orderList"] = filtered;
        }

        private static void StripClientEvaluationSideChannels(
            Dictionary<string, object> data)
        {
            if (data == null)
            {
                return;
            }

            // These fields are migration-only P2P manifests. Native random
            // target and condition semantics are carried by uList/orderList.
            // Leaving a Guest manifest on the Host request would give Harmony
            // a second client-authoritative evaluation path.
            data.Remove(P2PBattleProtocol.ActionManifestKey);
            data.Remove("p2pAuthoritativeSkillEvaluations");
            data.Remove("p2pAuthoritativeSkillTargets");
        }

        private static bool TryValidateServerResponse(
            P2PClientBattleRequest request,
            P2PBattleRoute route,
            Dictionary<string, object> response,
            out string error)
        {
            error = string.Empty;
            if (response == null || !response.TryGetValue("uri", out object rawUri) ||
                !string.Equals(rawUri?.ToString(), request.Uri,
                    StringComparison.Ordinal))
            {
                error = "the Host response did not preserve the request URI";
                return false;
            }

            if (route != P2PBattleRoute.Opponent)
            {
                return true;
            }

            // NetworkOperationCollection always reads opponent actions from
            // OpponentTargetDataList. NetworkBattleReceiver only fills that
            // list from a server result's oppoTargetList. A targetList is the
            // request-side encoding and must never leak into this response.
            bool requestHasTargets = request.Data.ContainsKey("targetList");
            bool responseHasTargets = response.ContainsKey("oppoTargetList");
            if (response.ContainsKey("targetList") ||
                (requestHasTargets && !responseHasTargets))
            {
                error = "the Host response did not convert targetList to oppoTargetList";
                return false;
            }
            return true;
        }

        private static void ObserveClientRequest(
            P2PClientBattleRequest request,
            int serverActionId)
        {
            if (request == null || request.Data == null)
            {
                return;
            }

            stateRevision = Math.Max(stateRevision, serverActionId);

            // Native-timing actions carry incremental private-card mutations
            // outside the stock knownList/uList envelope. Absorb those
            // mutations into the Host protocol ledger before it builds the
            // response; otherwise a later action would fall back to the
            // pre-mutation card state even though the receiver already saw the
            // incremental extension.
            ObservePrivateStateDelta(request.SourceIsHost, request.Data);
            ObservePlayerHistoryState(request.SourceIsHost, request.Data);
            ObserveKnownCards(request.SourceIsHost, request.Data, "knownList");
            HashSet<string> recordedMoves = new HashSet<string>(
                StringComparer.Ordinal);
            HashSet<string> recordedHistoryEvents = new HashSet<string>(
                StringComparer.Ordinal);
            int primaryPlayIndex = TryReadInt(request.Data, "playIdx",
                out int parsedPlayIndex) ? parsedPlayIndex : -1;
            ObserveMoveCards(request.SourceIsHost, request.Data, "uList",
                request.Uri, serverActionId, recordedMoves, primaryPlayIndex,
                recordedHistoryEvents);
            ObserveMoveCards(request.SourceIsHost, request.Data, "orderList",
                request.Uri, serverActionId, recordedMoves, primaryPlayIndex,
                recordedHistoryEvents);
            ObserveRegisterMetadata(request.SourceIsHost, request.Data,
                request.Uri, serverActionId);
            ObserveActionEnvelope(request.SourceIsHost, request.Data,
                request.Uri, serverActionId);
        }

        private static void ObservePrivateStateDelta(
            bool sourceIsHost,
            Dictionary<string, object> data)
        {
            if (data == null)
            {
                return;
            }
            int defaultOwner = sourceIsHost ? 1 : 0;

            Action<int, IEnumerable> absorb = (owner, rawCards) =>
            {
                if ((owner != 0 && owner != 1) || rawCards == null)
                {
                    return;
                }

                foreach (object rawCard in rawCards)
                {
                    if (!(rawCard is Dictionary<string, object> card) ||
                        !TryReadInt(card, "idx", out int index) || index <= 0)
                    {
                        continue;
                    }
                    MergePrivateCardState(owner, index, card);
                }
            };

            IEnumerable snapshotEnumerable = null;
            if (data.TryGetValue(P2PBattleProtocol.AuthorityHiddenStatesKey,
                    out object rawSnapshots) &&
                rawSnapshots is IEnumerable parsedSnapshots &&
                !(rawSnapshots is string))
            {
                snapshotEnumerable = parsedSnapshots;
            }
            bool hasAuthoritySnapshots = snapshotEnumerable != null;
            if (hasAuthoritySnapshots)
            {
                foreach (object rawSnapshot in snapshotEnumerable)
                {
                    if (!(rawSnapshot is Dictionary<string, object> snapshot) ||
                        !TryReadInt(snapshot, "owner", out int snapshotOwner) ||
                        !snapshot.TryGetValue("cards", out object snapshotCardsValue) ||
                        snapshotCardsValue is string ||
                        !(snapshotCardsValue is IEnumerable snapshotCards))
                    {
                        continue;
                    }
                    absorb(snapshotOwner, snapshotCards);
                    if (snapshot.TryGetValue("removed", out object rawRemoved) &&
                        rawRemoved is IEnumerable removed && !(rawRemoved is string))
                    {
                        foreach (object rawIndex in removed)
                        {
                            if (TryConvertInt(rawIndex, out int index) && index > 0)
                            {
                                SetZone(snapshotOwner, index, ZoneUnknown);
                            }
                        }
                    }
                }
            }

            if (!hasAuthoritySnapshots &&
                data.TryGetValue("p2pHiddenCards", out object hiddenCardsValue) &&
                hiddenCardsValue is IEnumerable hiddenCards &&
                !(hiddenCardsValue is string))
            {
                int hiddenOwner = defaultOwner;
                if (data.TryGetValue("p2pHiddenOwner", out object rawOwner) &&
                    TryConvertInt(rawOwner, out int parsedOwner) &&
                    (parsedOwner == 0 || parsedOwner == 1))
                {
                    hiddenOwner = parsedOwner;
                }
                absorb(hiddenOwner, hiddenCards);
                if (data.TryGetValue("p2pHiddenRemoved", out object rawRemoved) &&
                    rawRemoved is IEnumerable removed && !(rawRemoved is string))
                {
                    foreach (object rawIndex in removed)
                    {
                        if (TryConvertInt(rawIndex, out int index) && index > 0)
                        {
                            SetZone(hiddenOwner, index, ZoneUnknown);
                        }
                    }
                }
            }

        }

        private static void ObservePlayerHistoryState(
            bool sourceIsHost,
            Dictionary<string, object> data)
        {
            if (data == null)
            {
                return;
            }

            Action<Dictionary<string, object>> store = state =>
            {
                if (state == null || !TryReadInt(state, "owner", out int owner) ||
                    (owner != 0 && owner != 1))
                {
                    return;
                }
                playerHistoryByOwner[owner] = P2PJson.CloneDictionary(state);
            };

            // A native request may carry only the pre-action history during a
            // startup/ordering race. Apply it first; a post-action revision
            // below must remain the ledger's latest baseline.
            if (data.TryGetValue("p2pPlayerHistoryBefore", out object rawBefore) &&
                rawBefore is Dictionary<string, object> before)
            {
                store(before);
            }

            if (data.TryGetValue(
                    P2PBattleProtocol.AuthorityPlayerHistoryStatesKey,
                    out object rawSnapshots) &&
                rawSnapshots is IEnumerable snapshots &&
                !(rawSnapshots is string))
            {
                foreach (object rawSnapshot in snapshots)
                {
                    store(rawSnapshot as Dictionary<string, object>);
                }
            }

            if (data.TryGetValue("p2pPlayerHistory", out object rawHistory) &&
                rawHistory is Dictionary<string, object> history)
            {
                store(history);
            }
        }

        private static void AddPrivateSnapshotKnownIdentities(
            bool sourceIsHost,
            Dictionary<string, object> data,
            List<object> knownList,
            HashSet<string> existing)
        {
            if (data == null || knownList == null || existing == null)
            {
                return;
            }

            Action<int, IEnumerable> addCards = (owner, cards) =>
            {
                if ((owner != 0 && owner != 1) || cards == null)
                {
                    return;
                }
                bool isSelf = owner == (sourceIsHost ? 1 : 0);
                foreach (object rawCard in cards)
                {
                    if (!(rawCard is Dictionary<string, object> card) ||
                        !TryReadInt(card, "idx", out int index) || index <= 0)
                    {
                        continue;
                    }
                    AddKnownCardIfCached(knownList, existing, owner, index,
                        isSelf);
                }
            };

            if (data.TryGetValue(P2PBattleProtocol.AuthorityHiddenStatesKey,
                    out object rawSnapshots) &&
                rawSnapshots is IEnumerable snapshots &&
                !(rawSnapshots is string))
            {
                foreach (object rawSnapshot in snapshots)
                {
                    if (!(rawSnapshot is Dictionary<string, object> snapshot) ||
                        !TryReadInt(snapshot, "owner", out int owner) ||
                        !snapshot.TryGetValue("cards", out object rawCards) ||
                        rawCards is string || !(rawCards is IEnumerable cards))
                    {
                        continue;
                    }
                    addCards(owner, cards);
                }
                return;
            }

            if (data.TryGetValue("p2pHiddenOwner", out object rawOwner) &&
                TryConvertInt(rawOwner, out int hiddenOwner) &&
                data.TryGetValue("p2pHiddenCards", out object rawHiddenCards) &&
                rawHiddenCards is IEnumerable hiddenCards &&
                !(rawHiddenCards is string))
            {
                addCards(hiddenOwner, hiddenCards);
            }
        }

        private static void MergePrivateCardState(
            int owner,
            int index,
            Dictionary<string, object> source)
        {
            if ((owner != 0 && owner != 1) || index <= 0 || source == null)
            {
                return;
            }

            privateCardsByOwner[owner].TryGetValue(index,
                out Dictionary<string, object> previous);
            Dictionary<string, object> state = IsPrivateCardDelta(source)
                ? (previous == null
                    ? new Dictionary<string, object>()
                    : P2PJson.CloneDictionary(previous))
                : new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> pair in source)
            {
                if (string.Equals(pair.Key, HiddenStateDeltaKey,
                        StringComparison.Ordinal) ||
                    string.Equals(pair.Key, HiddenStateRemovedFieldsKey,
                        StringComparison.Ordinal))
                {
                    continue;
                }
                state[pair.Key] = CloneStateValue(pair.Value);
            }
            if (source.TryGetValue(HiddenStateRemovedFieldsKey,
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
                    state.Remove(field);
                }
            }
            state["idx"] = index;
            if (!TryReadInt(state, "cardId", out int cardId) || cardId <= 0)
            {
                return;
            }
            privateCardsByOwner[owner][index] = state;
            if (TryReadInt(state, "p2pZone", out int zone))
            {
                SetZone(owner, index, zone);
            }
        }

        private static bool IsPrivateCardDelta(
            Dictionary<string, object> state)
        {
            return state != null &&
                state.TryGetValue(HiddenStateDeltaKey, out object rawDelta) &&
                TryConvertInt(rawDelta, out int delta) && delta != 0;
        }

        private static void ObserveActionEnvelope(
            bool sourceIsHost,
            Dictionary<string, object> data,
            string uri,
            int serverActionId)
        {
            if (data == null)
            {
                return;
            }

            int owner = sourceIsHost ? 1 : 0;
            HashSet<string> events = new HashSet<string>(StringComparer.Ordinal);

            if (TryReadInt(data, "type", out int playType))
            {
                switch (playType)
                {
                    case 10: events.Add("attack"); break;
                    case 20:
                    case 21: events.Add("evolve"); break;
                    case 30:
                    case 31: events.Add("play"); break;
                    case 40: events.Add("fusion"); break;
                }
            }

            if (data.TryGetValue("keyAction", out object rawKeyActions) &&
                rawKeyActions is IEnumerable keyActions &&
                !(rawKeyActions is string))
            {
                foreach (object rawKeyAction in keyActions)
                {
                    if (!(rawKeyAction is Dictionary<string, object> keyAction) ||
                        !TryReadInt(keyAction, "type", out int keyType))
                    {
                        continue;
                    }
                    switch (keyType)
                    {
                        case 2: events.Add("accelerated"); break;
                        case 3: events.Add("crystallize"); break;
                        case 4: events.Add("fusion"); break;
                        case 6: events.Add("burialRite"); break;
                        case 7: events.Add("evolve"); break;
                        case 8: events.Add("choiceBrave"); break;
                    }
                }
            }

            foreach (string eventName in events)
            {
                IncrementHistoryCounter(owner, eventName);
                actionHistory.Add(new Dictionary<string, object>
                {
                    ["actionId"] = serverActionId,
                    ["uri"] = uri ?? string.Empty,
                    ["owner"] = owner,
                    ["event"] = eventName
                });
            }
            if (actionHistory.Count > MaximumActionHistory)
            {
                actionHistory.RemoveRange(
                    0, actionHistory.Count - MaximumActionHistory);
            }
        }

        private static void ObserveKnownCards(
            bool sourceIsHost,
            Dictionary<string, object> data,
            string listKey)
        {
            if (data == null || !data.TryGetValue(listKey, out object rawCards) ||
                rawCards is string || !(rawCards is IEnumerable cards))
            {
                return;
            }

            foreach (object rawCard in cards)
            {
                if (!(rawCard is Dictionary<string, object> card))
                {
                    continue;
                }
                bool ownerIsSource = !TryReadInt(card, "isSelf", out int isSelf) ||
                    isSelf != 0;
                int owner = ownerIsSource == sourceIsHost ? 1 : 0;
                RememberCard(owner, card, GetIndices(card));
                if (TryReadInt(card, "to", out int to))
                {
                    foreach (int index in GetIndices(card))
                    {
                        SetZone(owner, index, to);
                    }
                }
                else if (TryReadInt(card, "p2pZone", out int p2pZone))
                {
                    foreach (int index in GetIndices(card))
                    {
                        SetZone(owner, index, p2pZone);
                    }
                }
            }

        }

        private static void ObserveMoveCards(
            bool sourceIsHost,
            Dictionary<string, object> data,
            string listKey,
            string uri,
            int serverActionId,
            HashSet<string> recordedMoves,
            int primaryPlayIndex,
            HashSet<string> recordedHistoryEvents)
        {
            if (data == null || !data.TryGetValue(listKey, out object root))
            {
                return;
            }

            foreach (Dictionary<string, object> move in EnumerateMoves(root))
            {
                if (!TryReadInt(move, "isSelf", out int isSelf))
                {
                    continue;
                }
                bool ownerIsSource = isSelf != 0;
                int owner = ownerIsSource == sourceIsHost ? 1 : 0;
                RememberCard(owner, move, GetIndices(move));
                TryReadInt(move, "from", out int from);
                TryReadInt(move, "to", out int to);
                foreach (int index in GetIndices(move))
                {
                    SetZone(owner, index, to);
                    string moveKey = owner + ":" + index + ":" + from + ":" + to;
                    if (recordedMoves == null || recordedMoves.Add(moveKey))
                    {
                        bool isPrimaryPlay = index == primaryPlayIndex;
                        string historyKey = isPrimaryPlay
                            ? owner + ":" + index + ":play"
                            : owner + ":" + index + ":" +
                                from + ":" + to + ":movement";
                        bool shouldRecordHistory = recordedHistoryEvents == null ||
                            recordedHistoryEvents.Add(historyKey);
                        RecordActionMove(
                            requestUri: uri,
                            serverActionId: serverActionId,
                            owner: owner,
                            index: index,
                            from: from,
                            to: to,
                            card: GetCachedCard(owner, index),
                            move: move,
                            isPrimaryPlay: isPrimaryPlay,
                            recordHistory: shouldRecordHistory);
                    }
                }
            }
        }

        private static Dictionary<string, object> GetCachedCard(
            int owner,
            int index)
        {
            return (owner == 0 || owner == 1) &&
                privateCardsByOwner[owner].TryGetValue(index,
                    out Dictionary<string, object> state)
                ? state
                : null;
        }

        private static void RecordActionMove(
            string requestUri,
            int serverActionId,
            int owner,
            int index,
            int from,
            int to,
            Dictionary<string, object> card,
            Dictionary<string, object> move,
            bool isPrimaryPlay = false,
            bool recordHistory = true)
        {
            Dictionary<string, object> entry = new Dictionary<string, object>
            {
                ["actionId"] = serverActionId,
                ["uri"] = requestUri ?? string.Empty,
                ["owner"] = owner,
                ["idx"] = index,
                ["from"] = from,
                ["to"] = to
            };
            if (card != null && TryReadInt(card, "cardId", out int cardId) &&
                cardId > 0)
            {
                entry["cardId"] = cardId;
            }
            if (move != null && move.TryGetValue("skill", out object skill))
            {
                entry["skill"] = skill?.ToString() ?? string.Empty;
            }
            if (move != null && move.TryGetValue("skillKeyCardIdx",
                    out object skillKeyCardIdx))
            {
                entry["skillKeyCardIdx"] = CloneStateValue(skillKeyCardIdx);
            }
            if (move != null && move.TryGetValue("randomTargetIdx",
                    out object randomTargetIdx))
            {
                entry["randomTargetIdx"] = CloneStateValue(randomTargetIdx);
            }
            if (recordHistory)
            {
                string counter = HistoryCounterForMove(
                    requestUri, from, to, isPrimaryPlay);
                string counterKey = serverActionId + ":" + owner + ":" +
                    index + ":" + (counter ?? string.Empty);
                if (actionHistoryCounterKeys.Add(counterKey))
                {
                    IncrementHistoryCounter(owner, counter);
                }
            }
            actionHistory.Add(entry);
            if (actionHistory.Count > MaximumActionHistory)
            {
                actionHistory.RemoveRange(
                    0, actionHistory.Count - MaximumActionHistory);
            }
        }

        private static void ObserveRegisterMetadata(
            bool sourceIsHost,
            Dictionary<string, object> data,
            string uri,
            int serverActionId)
        {
            if (data == null)
            {
                return;
            }

            if (data.TryGetValue("uList", out object rawUnapproved))
            {
                foreach (Dictionary<string, object> entry in
                    EnumerateRegisterDictionaries(rawUnapproved))
                {
                    ObserveUnapprovedEntry(sourceIsHost, entry, uri,
                        serverActionId);
                }
            }

            if (!data.TryGetValue("orderList", out object rawOrderList))
            {
                return;
            }

            foreach (Dictionary<string, object> wrapper in
                EnumerateRegisterDictionaries(rawOrderList))
            {
                foreach (KeyValuePair<string, object> pair in wrapper)
                {
                    if (!(pair.Value is Dictionary<string, object> entry))
                    {
                        continue;
                    }

                    string kind = pair.Key ?? string.Empty;
                    switch (kind)
                    {
                        case "fusion":
                            ObserveFusionEntry(sourceIsHost, entry, uri,
                                serverActionId);
                            break;
                        case "metamorphose":
                            ObserveMetamorphoseEntry(sourceIsHost, entry,
                                uri, serverActionId);
                            break;
                        case "add":
                            ObserveAddEntry(sourceIsHost, entry, uri,
                                serverActionId);
                            break;
                        case "alter":
                            ObserveAlterEntry(sourceIsHost, entry, uri,
                                serverActionId);
                            break;
                        case "skillConditionCheck":
                            RecordRegisterEntry("skillConditionCheck",
                                sourceIsHost, entry, uri, serverActionId);
                            break;
                        case "move":
                            // Movement fields are already applied by
                            // ObserveMoveCards. Recording the full native
                            // registration here retains the fields that are
                            // not copied into CardDataModel (for example
                            // shortage-deck and attached-skill metadata).
                            RecordRegisterEntry("move", sourceIsHost, entry,
                                uri, serverActionId);
                            break;
                    }
                }
            }
        }

        private static IEnumerable<Dictionary<string, object>>
            EnumerateRegisterDictionaries(object value, int depth = 0)
        {
            if (value == null || depth > 5)
            {
                yield break;
            }
            if (value is Dictionary<string, object> dictionary)
            {
                yield return dictionary;
                foreach (object nested in dictionary.Values)
                {
                    foreach (Dictionary<string, object> child in
                        EnumerateRegisterDictionaries(nested, depth + 1))
                    {
                        yield return child;
                    }
                }
                yield break;
            }
            if (value is IEnumerable values && !(value is string))
            {
                foreach (object nested in values)
                {
                    foreach (Dictionary<string, object> child in
                        EnumerateRegisterDictionaries(nested, depth + 1))
                    {
                        yield return child;
                    }
                }
            }
        }

        private static void ObserveUnapprovedEntry(
            bool sourceIsHost,
            Dictionary<string, object> entry,
            string uri,
            int serverActionId)
        {
            if (entry == null)
            {
                return;
            }

            bool ownerIsSource = !TryReadInt(entry, "isSelf", out int isSelf) ||
                isSelf != 0;
            int owner = ownerIsSource == sourceIsHost ? 1 : 0;
            bool shortage = ContainsRawIndex(entry, -99);
            Dictionary<string, object> state = P2PJson.CloneDictionary(entry);
            if (shortage)
            {
                state["isShortageDeck"] = 1;
            }

            List<int> indices = GetIndices(entry).ToList();
            foreach (int index in indices)
            {
                RememberCard(owner, state, new[] { index });
            }
            RecordRegisterEntry("unapproved", sourceIsHost, state, uri,
                serverActionId);
        }

        private static void ObserveFusionEntry(
            bool sourceIsHost,
            Dictionary<string, object> entry,
            string uri,
            int serverActionId)
        {
            if (entry == null)
            {
                return;
            }

            bool ownerIsSource = !TryReadInt(entry, "isSelf", out int isSelf) ||
                isSelf != 0;
            int owner = ownerIsSource == sourceIsHost ? 1 : 0;
            List<int> targets = GetIndices(entry).ToList();
            List<int> ingredients = GetIntegerList(entry, "ingredients");
            fusionCountsByOwner[owner] = GetFusionCount(owner) + 1;

            foreach (int target in targets)
            {
                Dictionary<string, object> targetState =
                    new Dictionary<string, object>
                    {
                        ["idx"] = target,
                        ["isSelf"] = ownerIsSource ? 1 : 0,
                        ["fusion"] = ingredients.ToList(),
                        ["fusionCount"] = GetFusionCount(owner)
                    };
                RememberCard(owner, targetState, new[] { target });
            }

            foreach (int ingredient in ingredients)
            {
                int fromZone = TryGetCardZone(owner, ingredient,
                    out int cachedZone) && cachedZone != ZoneUnknown
                    ? cachedZone
                    : ZoneHand;
                SetZone(owner, ingredient, ZoneFusionIngredient);
                RecordActionMove(uri, serverActionId, owner, ingredient,
                    fromZone, ZoneFusionIngredient,
                    GetCachedCard(owner, ingredient), entry);
            }

            Dictionary<string, object> history = P2PJson.CloneDictionary(entry);
            history["ingredients"] = ingredients.ToList();
            history["fusionNumber"] = GetFusionCount(owner);
            RecordRegisterEntry("fusion", sourceIsHost, history, uri,
                serverActionId);
        }

        private static void ObserveMetamorphoseEntry(
            bool sourceIsHost,
            Dictionary<string, object> entry,
            string uri,
            int serverActionId)
        {
            if (entry == null)
            {
                return;
            }

            bool ownerIsSource = !TryReadInt(entry, "isSelf", out int isSelf) ||
                isSelf != 0;
            int owner = ownerIsSource == sourceIsHost ? 1 : 0;
            int afterCardId = 0;
            if (entry.TryGetValue("after", out object rawAfter) &&
                rawAfter is Dictionary<string, object> after)
            {
                TryReadInt(after, "cardId", out afterCardId);
            }

            if (afterCardId > 0)
            {
                foreach (int index in GetIndices(entry))
                {
                    Dictionary<string, object> state =
                        new Dictionary<string, object>
                        {
                            ["idx"] = index,
                            ["cardId"] = afterCardId,
                            ["isSelf"] = ownerIsSource ? 1 : 0
                        };
                    RememberCard(owner, state, new[] { index });
                }
            }
            RecordRegisterEntry("metamorphose", sourceIsHost, entry, uri,
                serverActionId);
        }

        private static void ObserveAddEntry(
            bool sourceIsHost,
            Dictionary<string, object> entry,
            string uri,
            int serverActionId)
        {
            if (entry == null)
            {
                return;
            }

            bool ownerIsSource = !TryReadInt(entry, "isSelf", out int isSelf) ||
                isSelf != 0;
            int owner = ownerIsSource == sourceIsHost ? 1 : 0;
            int cardId = 0;
            if (entry.TryGetValue("card", out object rawCard) &&
                rawCard is Dictionary<string, object> card)
            {
                TryReadInt(card, "cardId", out cardId);
            }
            if (cardId > 0)
            {
                Dictionary<string, object> state =
                    new Dictionary<string, object>
                    {
                        ["cardId"] = cardId,
                        ["isSelf"] = ownerIsSource ? 1 : 0
                    };
                foreach (int index in GetIndices(entry))
                {
                    RememberCard(owner, state, new[] { index });
                }
            }
            RecordRegisterEntry("add", sourceIsHost, entry, uri,
                serverActionId);
        }

        private static void ObserveAlterEntry(
            bool sourceIsHost,
            Dictionary<string, object> entry,
            string uri,
            int serverActionId)
        {
            if (entry == null || !entry.TryGetValue("cost", out object rawCost) ||
                !TryParseCostOperation(rawCost?.ToString(), out char operation,
                    out int value))
            {
                RecordRegisterEntry("alter", sourceIsHost, entry, uri,
                    serverActionId);
                return;
            }

            bool ownerIsSource = !TryReadInt(entry, "isSelf", out int isSelf) ||
                isSelf != 0;
            int owner = ownerIsSource == sourceIsHost ? 1 : 0;
            bool isDelete = entry.TryGetValue("type", out object rawType) &&
                string.Equals(rawType?.ToString(), "del",
                    StringComparison.Ordinal);
            foreach (int index in GetIndices(entry))
            {
                if (!privateCardsByOwner[owner].TryGetValue(index,
                        out Dictionary<string, object> state) ||
                    !TryReadInt(state, "cost", out int current))
                {
                    continue;
                }
                if (isDelete && operation != 'a')
                {
                    continue;
                }
                int next = current;
                switch (operation)
                {
                    case 'a': next = current + value; break;
                    case 's': next = value; break;
                    case 'd': next = (current + 1) / 2; break;
                    case 'D': next = current / 2; break;
                }
                state["cost"] = Math.Max(0, next);
            }
            RecordRegisterEntry("alter", sourceIsHost, entry, uri,
                serverActionId);
        }

        private static bool TryParseCostOperation(
            string encoded,
            out char operation,
            out int value)
        {
            operation = '\0';
            value = 0;
            if (string.IsNullOrEmpty(encoded) || encoded.Length < 2)
            {
                return false;
            }
            operation = encoded[0];
            if (operation != 'a' && operation != 's' &&
                operation != 'd' && operation != 'D')
            {
                operation = '\0';
                return false;
            }
            return int.TryParse(encoded.Substring(1),
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private static void RecordRegisterEntry(
            string kind,
            bool sourceIsHost,
            Dictionary<string, object> entry,
            string uri,
            int serverActionId)
        {
            if (entry == null)
            {
                return;
            }
            Dictionary<string, object> record = P2PJson.CloneDictionary(entry);
            record["kind"] = kind ?? string.Empty;
            record["actionId"] = serverActionId;
            record["uri"] = uri ?? string.Empty;
            record["sourceOwner"] = sourceIsHost ? 1 : 0;
            registerHistory.Add(record);
            if (registerHistory.Count > MaximumActionHistory)
            {
                registerHistory.RemoveRange(0,
                    registerHistory.Count - MaximumActionHistory);
            }
        }

        private static List<int> GetIntegerList(
            Dictionary<string, object> data,
            string key)
        {
            List<int> result = new List<int>();
            if (data == null || !data.TryGetValue(key, out object raw) ||
                raw is string)
            {
                return result;
            }
            if (TryConvertInt(raw, out int single))
            {
                result.Add(single);
                return result;
            }
            if (!(raw is IEnumerable values))
            {
                return result;
            }
            foreach (object value in values)
            {
                if (TryConvertInt(value, out int parsed))
                {
                    result.Add(parsed);
                }
            }
            return result;
        }

        private static string HistoryCounterForMove(
            string uri,
            int from,
            int to,
            bool isPrimaryPlay = false)
        {
            // A PlayActions envelope contains both the card being played and
            // possible discard effects. Only the movement for playIdx is a
            // native "play" event; other hand-to-cemetery movements remain
            // discards. This mirrors the server's separate play/discard
            // history registration instead of conflating the two.
            if (string.Equals(uri, P2PBattleProtocol.PlayActionsUri,
                    StringComparison.Ordinal) && isPrimaryPlay &&
                from == ZoneHand)
            {
                return "play";
            }
            if (from == ZoneDeck && to == ZoneHand)
            {
                return "draw";
            }
            if (from == ZoneHand && to == ZoneCemetery)
            {
                return "discard";
            }
            if (from == ZoneField && to == ZoneCemetery)
            {
                return "destroy";
            }
            if (from == ZoneField && to == ZoneHand)
            {
                return "return";
            }
            if (from == ZoneHand && to == ZoneFusionIngredient)
            {
                return "fusion";
            }
            if (to == ZoneField &&
                (from == ZoneUnknown || from == ZoneNone ||
                    from == ZoneReservation ||
                    from == ZoneFusionIngredient || from == ZoneBlackHole))
            {
                return "summon";
            }
            if (to == ZoneBanish)
            {
                return "banish";
            }
            return null;
        }

        private static void IncrementHistoryCounter(int owner, string counter)
        {
            if ((owner != 0 && owner != 1) || string.IsNullOrEmpty(counter))
            {
                return;
            }
            Dictionary<string, int> counters = historyCountersByOwner[owner];
            counters[counter] = counters.TryGetValue(counter, out int value)
                ? value + 1
                : 1;
        }

        private static void SetOwnerZones(
            int owner,
            IEnumerable<int> handIndices)
        {
            if ((owner != 0 && owner != 1) || handIndices == null)
            {
                return;
            }

            HashSet<int> hand = new HashSet<int>(
                handIndices.Where(index => index > 0));
            foreach (int index in privateCardsByOwner[owner].Keys.ToList())
            {
                SetZone(owner, index, hand.Contains(index)
                    ? ZoneHand : ZoneDeck);
            }
            foreach (int index in hand)
            {
                SetZone(owner, index, ZoneHand);
            }
        }

        private static void SetZone(int owner, int index, int zone)
        {
            if ((owner != 0 && owner != 1) || index <= 0)
            {
                return;
            }
            cardZonesByOwner[owner][index] = zone;
            if (privateCardsByOwner[owner].TryGetValue(index,
                    out Dictionary<string, object> state))
            {
                state["p2pZone"] = zone;
            }
        }

        private static void EnsureNativeKnownIdentities(
            bool sourceIsHost,
            Dictionary<string, object> responseInput)
        {
            if (responseInput == null)
            {
                return;
            }

            List<object> knownList = GetOrCreateObjectList(responseInput, "knownList");
            HashSet<string> existing = new HashSet<string>(StringComparer.Ordinal);
            foreach (Dictionary<string, object> known in knownList
                .OfType<Dictionary<string, object>>().ToList())
            {
                bool ownerIsSource = !TryReadInt(known, "isSelf", out int isSelf) ||
                    isSelf != 0;
                int owner = ownerIsSource == sourceIsHost ? 1 : 0;
                foreach (int index in GetIndices(known))
                {
                    existing.Add(CardKey(owner, index));
                }
            }

            // PlayActions is a request-side hand/field reference. It needs the
            // same native known-card entry as any movement list before the
            // receiver calls NetworkBattleData.GetPlayCard().
            if (TryReadInt(responseInput, "playIdx", out int playIndex) &&
                playIndex > 0)
            {
                AddKnownCardIfCached(knownList, existing,
                    sourceIsHost ? 1 : 0, playIndex, true);
            }

            // Effects may target a card in a private zone (for example a
            // selected hand card). The native receiver still resolves that
            // target before operation construction, so expose its identity at
            // the same knownList boundary when the Host ledger has it.
            if (responseInput.TryGetValue("targetList", out object rawTargets) &&
                rawTargets is IEnumerable targets && !(rawTargets is string))
            {
                foreach (object rawTarget in targets)
                {
                    if (!(rawTarget is Dictionary<string, object> target) ||
                        !TryReadInt(target, "targetIdx", out int targetIndex) ||
                        targetIndex <= 0)
                    {
                        continue;
                    }
                    bool targetIsSelf = !TryReadInt(target, "isSelf",
                        out int targetSelf) || targetSelf != 0;
                    int targetOwner = targetIsSelf == sourceIsHost ? 1 : 0;
                    AddKnownCardIfCached(knownList, existing, targetOwner,
                        targetIndex, targetIsSelf);
                }
            }

            foreach (string listKey in new[] { "uList", "orderList" })
            {
                if (!responseInput.TryGetValue(listKey, out object root))
                {
                    continue;
                }
                foreach (Dictionary<string, object> move in EnumerateMoves(root))
                {
                    if (!TryReadInt(move, "isSelf", out int rawIsSelf) ||
                        !TryReadInt(move, "from", out int from) ||
                        !TryReadInt(move, "to", out int to) ||
                        !TouchesPrivateZone(from, to))
                    {
                        continue;
                    }

                    bool ownerIsSource = rawIsSelf != 0;
                    int owner = ownerIsSource == sourceIsHost ? 1 : 0;
                    foreach (int index in GetIndices(move))
                    {
                        AddKnownCardIfCached(knownList, existing, owner, index,
                            ownerIsSource);
                    }
                }
            }

            // Generated/drawn cards may be described only by the incremental
            // private-state extension, without a movement registration. Their
            // identity still has to exist before the native receiver builds
            // the operation, matching the official server boundary.
            AddPrivateSnapshotKnownIdentities(
                sourceIsHost, responseInput, knownList, existing);
        }

        private static void AddKnownCardIfCached(
            List<object> knownList,
            HashSet<string> existing,
            int owner,
            int index,
            bool isSelf)
        {
            if (index <= 0 || knownList == null || existing == null ||
                !privateCardsByOwner.TryGetValue(owner,
                    out Dictionary<int, Dictionary<string, object>> cards) ||
                !cards.TryGetValue(index, out Dictionary<string, object> state) ||
                !TryReadInt(state, "cardId", out int cardId) || cardId <= 0)
            {
                return;
            }

            // A request can contain a placeholder knownList entry without a
            // cardId. Update that entry instead of appending a second record
            // for the same owner/index, because ReplaceReceivedCard expects a
            // single canonical identity record.
            foreach (Dictionary<string, object> known in knownList
                .OfType<Dictionary<string, object>>())
            {
                if ((!TryReadInt(known, "isSelf", out int knownIsSelf) ||
                        (knownIsSelf != 0) != isSelf) ||
                    !GetIndices(known).Contains(index))
                {
                    continue;
                }
                List<int> knownIndices = GetIndices(known).ToList();
                if (knownIndices.Count <= 1)
                {
                    known["idx"] = index;
                    known.Remove("idxList");
                    known["cardId"] = cardId;
                    known["is_open"] = 1;
                    ApplyNativeCardState(known, state);
                    existing.Add(CardKey(owner, index));
                    return;
                }

                // A grouped native entry can cover several cards. Split the
                // requested index into a scalar entry so the receiver's
                // ReplaceReceivedCard lookup remains unique, while retaining
                // the other grouped indexes in their original record.
                List<object> remaining = knownIndices
                    .Where(value => value != index)
                    .Select(value => (object)value)
                    .ToList();
                if (known.ContainsKey("idxList"))
                {
                    known["idxList"] = remaining;
                }
                else
                {
                    known["idx"] = remaining;
                }
                Dictionary<string, object> scalar =
                    new Dictionary<string, object>
                    {
                        ["idx"] = index,
                        ["cardId"] = cardId,
                        ["isSelf"] = isSelf ? 1 : 0,
                        ["is_open"] = 1
                    };
                ApplyNativeCardState(scalar, state);
                knownList.Add(scalar);
                existing.Add(CardKey(owner, index));
                return;
            }

            if (!existing.Add(CardKey(owner, index)))
            {
                return;
            }

            Dictionary<string, object> knownCard = new Dictionary<string, object>
            {
                ["idx"] = index,
                ["cardId"] = cardId,
                ["isSelf"] = isSelf ? 1 : 0,
                ["is_open"] = 1
            };
                ApplyNativeCardState(knownCard, state);
            knownList.Add(knownCard);
        }

        private static void MergeCachedNativeStates(
            bool sourceIsHost,
            Dictionary<string, object> data)
        {
            if (data == null)
            {
                return;
            }

            foreach (string listKey in new[] { "knownList", "uList" })
            {
                if (!data.TryGetValue(listKey, out object rawList))
                {
                    continue;
                }
                foreach (Dictionary<string, object> entry in
                    EnumerateRegisterDictionaries(rawList))
                {
                    bool ownerIsSource = !TryReadInt(entry, "isSelf",
                        out int isSelf) || isSelf != 0;
                    int owner = ownerIsSource == sourceIsHost ? 1 : 0;
                    foreach (int index in GetIndices(entry))
                    {
                        if (!privateCardsByOwner[owner].TryGetValue(index,
                                out Dictionary<string, object> state))
                        {
                            continue;
                        }
                        ApplyMissingNativeCardState(entry, state);
                    }
                }
            }

            // Only movement records in orderList are card-state carriers. Do
            // not merge a cached card's fields into skillConditionCheck,
            // validate, trigger, or playerParam dictionaries; those records
            // have their own native schema and must remain byte-for-byte
            // equivalent to the sender's registration output.
            if (data.TryGetValue("orderList", out object rawOrders))
            {
                foreach (Dictionary<string, object> move in
                    EnumerateMoves(rawOrders))
                {
                    bool ownerIsSource = !TryReadInt(move, "isSelf",
                        out int isSelf) || isSelf != 0;
                    int owner = ownerIsSource == sourceIsHost ? 1 : 0;
                    foreach (int index in GetIndices(move))
                    {
                        if (privateCardsByOwner[owner].TryGetValue(index,
                                out Dictionary<string, object> state))
                        {
                            ApplyMissingNativeCardState(move, state);
                        }
                    }
                }
            }
        }

        private static void ApplyMissingNativeCardState(
            Dictionary<string, object> destination,
            Dictionary<string, object> source)
        {
            if (destination == null || source == null)
            {
                return;
            }
            foreach (string key in NativeCardStateKeys)
            {
                if (destination.ContainsKey(key) ||
                    !source.TryGetValue(key, out object value))
                {
                    continue;
                }
                destination[key] = CloneStateValue(value);
            }
            foreach (string key in InternalCardStateKeys)
            {
                if (destination.ContainsKey(key) ||
                    !source.TryGetValue(key, out object value))
                {
                    continue;
                }
                destination[key] = CloneStateValue(value);
            }

            // RegisterUnapproved fields are encoded differently from the
            // CardDataModel fields. Reconstruct only missing values from the
            // cached native registration context; never overwrite a value
            // explicitly produced by the sender.
            if (!destination.ContainsKey("skill") &&
                TryReadInt(source, "skillCardIdx", out int skillCardIdx) &&
                TryReadInt(source, "publishedActiveSkillCount",
                    out int published) &&
                TryReadInt(source, "movement", out int movement))
            {
                destination["skill"] = skillCardIdx + "|" +
                    published + "|" + movement;
            }
            if (!destination.ContainsKey("skillKeyCardIdx") &&
                source.TryGetValue("skillKeyCardIdxList", out object rawKeys))
            {
                destination["skillKeyCardIdx"] = CloneStateValue(rawKeys);
            }
            if (!destination.ContainsKey("attachTarget") &&
                source.TryGetValue("attachedSkillsPublishCount",
                    out object rawAttached))
            {
                destination["attachTarget"] = FormatCommaSeparatedIntegers(
                    rawAttached);
            }
            if (!destination.ContainsKey("isInvoke") &&
                source.TryGetValue("isInvoked", out object rawInvoked))
            {
                destination["isInvoke"] = CloneStateValue(rawInvoked);
            }
        }

        private static string FormatCommaSeparatedIntegers(object value)
        {
            if (value is IEnumerable values && !(value is string))
            {
                return string.Join(",", values.Cast<object>()
                    .Select(item => item?.ToString() ?? string.Empty));
            }
            return value?.ToString() ?? string.Empty;
        }

        private static void RememberCard(
            int owner,
            Dictionary<string, object> source,
            IEnumerable<int> indices)
        {
            if ((owner != 0 && owner != 1) || source == null || indices == null)
            {
                return;
            }

            Dictionary<int, Dictionary<string, object>> cards =
                privateCardsByOwner[owner];
            foreach (int index in indices.Where(index => index > 0))
            {
                Dictionary<string, object> state = cards.TryGetValue(index,
                    out Dictionary<string, object> previous)
                    ? P2PJson.CloneDictionary(previous)
                    : new Dictionary<string, object>();
                state["idx"] = index;
                foreach (string key in NativeCardStateKeys)
                {
                    if (source.TryGetValue(key, out object value))
                    {
                        state[key] = CloneStateValue(value);
                    }
                }
                foreach (string key in InternalCardStateKeys)
                {
                    if (source.TryGetValue(key, out object value))
                    {
                        state[key] = CloneStateValue(value);
                    }
                }

                ApplyRegisterUnapprovedMetadata(state, source);

                // A state-only RegisterStateChangeCard does not carry cardId.
                // It can update an existing baseline entry, but cannot create
                // a new identity record without a native cardId source.
                if (!TryReadInt(state, "cardId", out int cardId) || cardId <= 0)
                {
                    continue;
                }
                cards[index] = state;
            }
        }

        private static void ApplyRegisterUnapprovedMetadata(
            Dictionary<string, object> destination,
            Dictionary<string, object> source)
        {
            if (destination == null || source == null)
            {
                return;
            }

            if (source.TryGetValue("skill", out object rawSkill) &&
                rawSkill != null)
            {
                string[] parts = rawSkill.ToString().Split('|');
                if (parts.Length > 0 && TryConvertInt(parts[0], out int skillCard))
                {
                    destination["skillCardIdx"] = skillCard;
                }
                if (parts.Length > 1 && TryConvertInt(parts[1], out int published))
                {
                    destination["publishedActiveSkillCount"] = published;
                }
                if (parts.Length > 2 && TryConvertInt(parts[2], out int movement))
                {
                    destination["movement"] = movement;
                }
            }

            if (source.TryGetValue("skillKeyCardIdx", out object rawKeys))
            {
                destination["skillKeyCardIdxList"] = CloneStateValue(rawKeys);
            }
            if (source.TryGetValue("randomTargetIdx", out object rawRandom))
            {
                destination["randomTargetIdx"] = CloneStateValue(rawRandom);
            }
            if (source.TryGetValue("attachTarget", out object rawAttach))
            {
                destination["attachedSkillsPublishCount"] =
                    ParseCommaSeparatedIntegers(rawAttach?.ToString());
            }
            if (source.TryGetValue("isInvoke", out object rawInvoke) &&
                TryConvertInt(rawInvoke, out int isInvoke))
            {
                destination["isInvoked"] = isInvoke != 0 ? 1 : 0;
            }
            if (source.TryGetValue("isShortageDeck", out object rawShortage))
            {
                destination["isShortageDeck"] = CloneStateValue(rawShortage);
            }
        }

        private static List<int> ParseCommaSeparatedIntegers(string value)
        {
            List<int> result = new List<int>();
            if (string.IsNullOrWhiteSpace(value))
            {
                return result;
            }
            foreach (string part in value.Split(','))
            {
                if (int.TryParse(part, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out int parsed))
                {
                    result.Add(parsed);
                }
            }
            return result;
        }

        private static void ApplyNativeCardState(
            Dictionary<string, object> destination,
            Dictionary<string, object> source)
        {
            if (destination == null || source == null)
            {
                return;
            }
            foreach (string key in NativeCardStateKeys)
            {
                if (string.Equals(key, "cardId", StringComparison.Ordinal) ||
                    !source.TryGetValue(key, out object value))
                {
                    continue;
                }
                destination[key] = CloneStateValue(value);
            }
        }

        private static object CloneStateValue(object value)
        {
            if (value is Dictionary<string, object> dictionary)
            {
                return P2PJson.CloneDictionary(dictionary);
            }
            if (value is IEnumerable values && !(value is string))
            {
                List<object> clone = new List<object>();
                foreach (object item in values)
                {
                    clone.Add(CloneStateValue(item));
                }
                return clone;
            }
            return value;
        }

        private static IEnumerable<Dictionary<string, object>> EnumerateMoves(
            object value,
            int depth = 0)
        {
            if (value == null || depth > 4)
            {
                yield break;
            }

            if (value is Dictionary<string, object> dictionary)
            {
                if (dictionary.ContainsKey("from") && dictionary.ContainsKey("to") &&
                    (dictionary.ContainsKey("idx") || dictionary.ContainsKey("idxList")))
                {
                    yield return dictionary;
                    yield break;
                }
                foreach (object nested in dictionary.Values)
                {
                    foreach (Dictionary<string, object> move in
                        EnumerateMoves(nested, depth + 1))
                    {
                        yield return move;
                    }
                }
                yield break;
            }

            if (value is IEnumerable values && !(value is string))
            {
                foreach (object nested in values)
                {
                    foreach (Dictionary<string, object> move in
                        EnumerateMoves(nested, depth + 1))
                    {
                        yield return move;
                    }
                }
            }
        }

        private static IEnumerable<int> GetIndices(
            Dictionary<string, object> data)
        {
            if (data == null)
            {
                yield break;
            }

            object rawIndices;
            if (!data.TryGetValue("idx", out rawIndices) &&
                !data.TryGetValue("idxList", out rawIndices))
            {
                yield break;
            }

            if (TryConvertInt(rawIndices, out int index))
            {
                yield return index;
                yield break;
            }
            if (rawIndices is string || !(rawIndices is IEnumerable indices))
            {
                yield break;
            }
            foreach (object rawIndex in indices)
            {
                int parsed;
                try
                {
                    parsed = Convert.ToInt32(rawIndex);
                }
                catch (Exception)
                {
                    continue;
                }
                if (parsed > 0)
                {
                    yield return parsed;
                }
            }
        }

        private static bool ContainsRawIndex(
            Dictionary<string, object> data,
            int expected)
        {
            if (data == null)
            {
                return false;
            }
            foreach (string key in new[] { "idx", "idxList" })
            {
                if (!data.TryGetValue(key, out object raw) ||
                    raw is string)
                {
                    continue;
                }
                if (TryConvertInt(raw, out int single) && single == expected)
                {
                    return true;
                }
                if (!(raw is IEnumerable values))
                {
                    continue;
                }
                foreach (object value in values)
                {
                    if (TryConvertInt(value, out int parsed) &&
                        parsed == expected)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static List<object> GetOrCreateObjectList(
            Dictionary<string, object> data,
            string key)
        {
            if (data.TryGetValue(key, out object raw) && raw is List<object> list)
            {
                return list;
            }
            List<object> result = raw is IEnumerable values && !(raw is string)
                ? values.Cast<object>().ToList()
                : new List<object>();
            data[key] = result;
            return result;
        }

        private static bool TouchesPrivateZone(int from, int to)
        {
            return IsPrivateZone(from) || IsPrivateZone(to);
        }

        private static bool IsPrivateZone(int place)
        {
            return place == 0 || place == 10 || place == 60 || place == 80 ||
                place == 90 || place == 999;
        }

        private static string CardKey(int owner, int index)
        {
            return owner.ToString() + ":" + index.ToString();
        }

        private static bool TryReadInt(
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
                value = Convert.ToInt32(rawValue);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryConvertInt(object value, out int result)
        {
            try
            {
                result = Convert.ToInt32(value,
                    System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                result = 0;
                return false;
            }
        }
    }
}
