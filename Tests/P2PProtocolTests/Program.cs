using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Shadowbus
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                TestConnectionCodes();
                TestLocalDeckCodes();
                TestJsonConversion();
                TestAuthorityEnvelope();
                TestAuthorityRequestValidation();
                TestRoomRules();
                TestTwoPickRuleFiles();
                TestPerspectiveTransform();
                TestSkillTargetPerspectiveTransform();
                TestHiddenSnapshotPerspectiveTransform();
                TestBattleResults();
                TestBattleProtocol();
                TestHostAuthoritativeServer();
                TestOpenMyCardsServerBoundary();
                TestHostAuthoritativeRegisterState();
                TestHostPrivateStateDeltaAndRandomCanonicalization();
                TestPreparedPrivateIdentityPromotion();
                TestPublicTargetIndexDoesNotConsumeOtherOwnerPrivateIdentity();
                TestPrivateStateFieldDeltaRoundTrip();
                TestPlayerHistoryPolicy();
                TestBattleStateDiagnostics();
                TestDealState();
                TestDeliverySequence();
                TestDisconnectPolicy();
                TestRoomRoundState();
                TestTransportRejectsThenAccepts();
                Console.WriteLine("P2P protocol tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static void TestPlayerHistoryPolicy()
        {
            HashSet<string> synchronized = new HashSet<string>(
                P2PPlayerHistoryPolicy.SynchronizedListNames,
                StringComparer.Ordinal);
            foreach (string required in new[]
            {
                "BattleStartDeckCardList",
                "DeckSkillCardList",
                "TurnFusionCountInfo",
                "EvolvedCards",
                "GameTurnPlayCards",
                "TurnDestroyCards",
                "DestroyedWhenDestroyCards",
                "ChoiceBraveCards",
                "LastTargetCardsList"
            })
            {
                Assert(synchronized.Contains(required),
                    "A persistent player-history list is no longer synchronized: " +
                    required);
            }

            HashSet<string> synchronizedScalars = new HashSet<string>(
                P2PPlayerHistoryPolicy.SynchronizedScalarNames,
                StringComparer.Ordinal);
            foreach (string required in new[]
            {
                "Turn",
                "IsSelfTurn",
                "Pp",
                "PpTotal",
                "CurrentEpCount",
                "GameUsedPpCount",
                "GameResonanceStartCount",
                "_cumulativeEvolutionCount"
            })
            {
                Assert(synchronizedScalars.Contains(required),
                    "A native condition scalar is no longer synchronized: " +
                    required);
            }

            foreach (string temporary in new[]
            {
                "ClassAndInPlayCardList",
                "PredictionCemeteryRandomCards",
                "PredictionDamageRandomCards",
                "PredictionBanishRandomCards",
                "ReturnList",
                "InHandCards",
                "SkillDiscards",
                "SelfDiscardList",
                "SkillBanishCards",
                "HealingCards",
                "SkillSummonedCards",
                "SummonedCards"
            })
            {
                Assert(!synchronized.Contains(temporary),
                    "A native action work list entered player-history sync: " +
                    temporary);
            }

            foreach (string nativeZone in
                P2PPlayerHistoryPolicy.NativeCardZoneListNames)
            {
                Assert(!synchronized.Contains(nativeZone),
                    "A native card zone entered reflective player-history sync: " +
                    nativeZone);
            }

            Assert(!P2PPlayerHistoryPolicy.ShouldAttachPreActionHistory(
                    "TurnStart", 1),
                "The initial TurnStart would overwrite the native opening state.");
            Assert(P2PPlayerHistoryPolicy.ShouldAttachPreActionHistory(
                    "TurnStart", 2) &&
                P2PPlayerHistoryPolicy.ShouldAttachPreActionHistory(
                    "PlayActions", 1),
                "A normal action lost its pre-action player-history state.");
        }

        private static void TestLocalDeckCodes()
        {
            var source = new LocalDeckCodePayload
            {
                ClanId = 6,
                FormatId = "modern",
                DeckName = "\u6d4b\u8bd5\u724c\u7ec4",
                SleeveId = 3000011,
                SkinId = 123,
                CardIds = Enumerable.Range(0, 50)
                    .Select(index => 100000001 + index * 10)
                    .ToList()
            };
            string code = LocalDeckCode.Encode(source);
            Assert(code.Length <= LocalDeckCode.MaximumLength,
                "A generated local deck code exceeded the input limit.");
            Assert(LocalDeckCode.TryDecode(
                    " \r\n" + code + "\r\n ",
                    out LocalDeckCodePayload decoded,
                    out string error),
                "A generated local deck code could not be decoded: " + error);
            Assert(decoded.ClanId == source.ClanId &&
                decoded.FormatId == source.FormatId &&
                decoded.DeckName == source.DeckName &&
                decoded.SleeveId == source.SleeveId &&
                decoded.SkinId == source.SkinId &&
                decoded.CardIds.SequenceEqual(source.CardIds),
                "The local deck-code payload changed during round-trip.");

            int separator = code.IndexOf('.') + 1;
            char replacement = code[separator] == 'A' ? 'B' : 'A';
            string damaged = code.Substring(0, separator) + replacement +
                code.Substring(separator + 1);
            Assert(!LocalDeckCode.TryDecode(damaged, out _, out _),
                "A damaged local deck code passed its checksum.");
            Assert(!LocalDeckCode.TryDecode("ABCD", out _, out _),
                "An official short code was accepted as a local self-contained code.");
        }

        private static void TestConnectionCodes()
        {
            byte[] token = CreateToken(1);
            AssertRoundTrip(IPAddress.Parse("192.0.2.10"), 29600, token);
            AssertRoundTrip(IPAddress.Parse("2001:db8::1234"), 65535, token);

            string code = P2PConnectionCode.Create(IPAddress.Loopback, 29600, token);
            const string alphabet =
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
            int lastValue = alphabet.IndexOf(code[code.Length - 1]);
            string tampered = code.Substring(0, code.Length - 1) +
                alphabet[lastValue ^ 0x10];
            Assert(!P2PConnectionCode.TryDecode(tampered, out _),
                "A modified connection code was accepted.");

            string nonCanonical = code.Substring(0, code.Length - 1) +
                alphabet[(lastValue & 0x30) | ((lastValue + 1) & 0x0f)];
            Assert(!P2PConnectionCode.TryDecode(nonCanonical, out _),
                "A non-canonical connection code was accepted.");
            Assert(!P2PConnectionCode.TryDecode("12345", out _),
                "A non-P2P room ID was accepted as a connection code.");
        }

        private static void TestTwoPickRuleFiles()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "shadowbus-twopick-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(
                    Path.Combine(directory, "alternate.json"),
                    "{\"id\":\"alternate\",\"displayName\":\"Alternate\"}");
                File.WriteAllText(
                    Path.Combine(directory, "custom.json"),
                    "{\"displayName\":\"Custom\"}");
                File.WriteAllText(
                    Path.Combine(directory, "normal.json"),
                    "{\"id\":\"normal\",\"displayName\":\"Standard\"}");
                File.WriteAllText(
                    Path.Combine(directory, "z-duplicate.json"),
                    "{\"id\":\"alternate\",\"displayName\":\"Duplicate\"}");
                File.WriteAllText(Path.Combine(directory, "z-invalid.json"), "{");

                List<string> errors = new List<string>();
                IReadOnlyList<P2PTwoPickRuleDefinition> definitions =
                    P2PTwoPickRuleFiles.Load(
                        directory,
                        P2PJson.Settings,
                        (source, fileId) =>
                        {
                            source.Id = string.IsNullOrWhiteSpace(source.Id)
                                ? fileId
                                : source.Id.Trim();
                            source.DisplayName = string.IsNullOrWhiteSpace(source.DisplayName)
                                ? source.Id
                                : source.DisplayName.Trim();
                            return source;
                        },
                        errors.Add);

                Assert(definitions.Select(rule => rule.Id).SequenceEqual(
                        new[] { "alternate", "custom", "normal" }),
                    "Two Pick JSON files were not discovered in stable filename order.");
                Assert(definitions[1].DisplayName == "Custom",
                    "A Two Pick rule display name was not loaded.");
                Assert(errors.Count == 2 &&
                        errors.Any(error => error.Contains("Duplicate")) &&
                        errors.Any(error => error.Contains("Failed to load")),
                    "Invalid or duplicate Two Pick rule files were not isolated.");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        private static void AssertRoundTrip(IPAddress address, int port, byte[] token)
        {
            string first = P2PConnectionCode.Create(address, port, token);
            string second = P2PConnectionCode.Create(address, port, token);
            Assert(first.StartsWith(P2PConnectionCode.Prefix, StringComparison.Ordinal),
                "The connection-code prefix is missing.");
            Assert(first.Length <= P2PConnectionCode.MaximumLength,
                "A generated connection code does not fit in the room password input.");
            Assert(first != second, "Connection-code encryption reused its IV.");
            Assert(!first.Contains(address.ToString()), "The address is visible in the connection code.");
            Assert(P2PConnectionCode.TryDecode(" \r\n" + first + " \r\n", out P2PConnectionInfo decoded),
                "A generated connection code could not be decoded.");
            Assert(decoded.Address.Equals(address), "The decoded address differs from the source.");
            Assert(decoded.Port == port, "The decoded port differs from the source.");
            Assert(EqualBytes(decoded.Token, token), "The decoded room token differs from the source.");
        }

        private static void TestJsonConversion()
        {
            P2PWireMessage source = new P2PWireMessage
            {
                Type = "probe",
                Rules = new P2PRoomRules
                {
                    IsDeckOpen = true,
                    CustomFormatId = "modern",
                    FormatDefinition = new CustomFormatDefinition
                    {
                        Id = "modern",
                        DisplayName = "Modern",
                        TokenCardTotalLimit = 10,
                        CardLimits = new Dictionary<int, int>
                        {
                            [123456] = 1
                        }
                    },
                    TwoPickType = 1,
                    TwoPickRule = new P2PTwoPickRuleDefinition
                    {
                        Id = "custom-draft",
                        DisplayName = "Custom Draft",
                        FinalDeckSize = 40,
                        CandidateClasses = new List<int> { 1, 2, 3, 4 },
                        ClassRules = new Dictionary<int, P2PTwoPickClassRuleDefinition>
                        {
                            [1] = new P2PTwoPickClassRuleDefinition
                            {
                                DisplayName = "Forest and Sword",
                                CardClasses = new List<int> { 0, 1, 2 },
                                AdditionalCards = new List<int> { 1005 },
                                Description = "Forest and Sword mix"
                            }
                        },
                        RoundRules = new List<P2PTwoPickRoundRuleDefinition>
                        {
                            new P2PTwoPickRoundRuleDefinition
                            {
                                Rounds = new List<int> { 1, 10, 20 },
                                Costs = new List<int> { 2, 3 },
                                Rarities = new List<int> { 3, 4 }
                            },
                            new P2PTwoPickRoundRuleDefinition
                            {
                                Rounds = new List<int> { 5 },
                                Cards = new List<int> { 1001, 1002, 1003, 1004 }
                            }
                        },
                        CardPool = new List<int> { 1001, 1002, 1003, 1004 },
                        CardWeights = new Dictionary<int, int> { [1001] = 3 }
                    },
                    InitialMaxLife = 137
                },
                Data = new Dictionary<string, object>
                {
                    ["number"] = 42,
                    ["items"] = new List<object> { 1, "two", true }
                }
            };
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(source, P2PJson.Settings);
            Assert(json.Contains("\"deckSizeLimit\":null") &&
                json.Contains("\"sameCardLimit\":null") &&
                json.Contains("\"tokenSameCardLimit\":null"),
                "Unlimited format fields were omitted from the wire snapshot.");
            P2PWireMessage decoded = P2PJson.DeserializeMessage(json);
            Assert(decoded.Type == "probe", "The wire-message type was not preserved.");
            Assert(decoded.Rules != null && decoded.Rules.IsDeckOpen,
                "The open-deck room rule was not preserved.");
            Assert(decoded.Rules.InitialMaxLife == 137,
                "The initial maximum life room rule was not preserved.");
            Assert(decoded.Rules.TwoPickRule.ClassRules[1].DisplayName ==
                    "Forest and Sword",
                "The Two Pick candidate display name was not preserved.");
            Assert(decoded.Rules.CustomFormatId == "modern",
                "The custom room format ID was not preserved.");
            Assert(decoded.Rules.TwoPickRule != null &&
                decoded.Rules.TwoPickRule.Id == "custom-draft" &&
                decoded.Rules.TwoPickRule.FinalDeckSize == 40 &&
                decoded.Rules.TwoPickRule.CardPool.Count == 4 &&
                decoded.Rules.TwoPickRule.CardWeights[1001] == 3 &&
                decoded.Rules.TwoPickRule.ClassRules[1].CardClasses.Count == 3 &&
                decoded.Rules.TwoPickRule.ClassRules[1].AdditionalCards[0] == 1005 &&
                decoded.Rules.TwoPickRule.ClassRules[1].Description ==
                    "Forest and Sword mix" &&
                decoded.Rules.TwoPickRule.RoundRules.Count == 2 &&
                decoded.Rules.TwoPickRule.RoundRules[0].Rarities[1] == 4 &&
                decoded.Rules.TwoPickRule.RoundRules[1].Cards.Count == 4,
                "The complete Two Pick rule was not preserved.");
            Assert(decoded.Rules.FormatDefinition != null &&
                decoded.Rules.FormatDefinition.Id == "modern" &&
                decoded.Rules.FormatDefinition.DeckSizeLimit == null &&
                decoded.Rules.FormatDefinition.TokenCardTotalLimit == 10 &&
                decoded.Rules.FormatDefinition.CardLimits[123456] == 1,
                "The complete custom format definition was not preserved.");
            Assert(decoded.Data["number"] is int number && number == 42,
                "A small JSON integer was not converted to Int32.");
            Assert(decoded.Data["items"] is List<object> items && items.Count == 3,
                "A JSON array was not converted to the expected list type.");
        }

        private static void TestAuthorityEnvelope()
        {
            P2PWireMessage source = new P2PWireMessage
            {
                Type = P2PBattleProtocol.AuthorityRequestUri,
                RequestId = "battle-guest-7",
                ActionSeq = 7,
                Protocol = P2PTransport.ProtocolVersion,
                ModVersion = P2PTransport.ModVersion,
                Authority = P2PTransport.AuthorityMode,
                Data = new Dictionary<string, object>
                {
                    [P2PBattleProtocol.AuthorityActionKey] = "play",
                    [P2PBattleProtocol.AuthorityCardIndexKey] = 12,
                    [P2PBattleProtocol.AuthorityStateKey] =
                        new Dictionary<string, object>
                        {
                            ["owner"] = 0,
                            ["cards"] = new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["idx"] = 12,
                                    ["cardId"] = 100001
                                }
                            }
                        }
                }
            };

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(
                source, P2PJson.Settings);
            P2PWireMessage decoded = P2PJson.DeserializeMessage(json);
            Assert(decoded.Type == P2PBattleProtocol.AuthorityRequestUri &&
                decoded.RequestId == source.RequestId &&
                decoded.ActionSeq == source.ActionSeq &&
                decoded.Protocol == P2PTransport.ProtocolVersion &&
                decoded.ModVersion == P2PTransport.ModVersion &&
                decoded.Authority == P2PTransport.AuthorityMode,
                "Authority envelope fields did not survive JSON round-trip.");
            Assert(decoded.Data != null &&
                decoded.Data[P2PBattleProtocol.AuthorityActionKey].ToString() == "play" &&
                Convert.ToInt32(decoded.Data[
                    P2PBattleProtocol.AuthorityCardIndexKey]) == 12,
                "Authority request action fields did not survive JSON round-trip.");

            Dictionary<string, object> result = new Dictionary<string, object>
            {
                [P2PBattleProtocol.AuthorityHiddenStatesKey] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["owner"] = 0,
                        ["cards"] = new List<object>(),
                        ["removed"] = new List<object> { 12 }
                    },
                    new Dictionary<string, object>
                    {
                        ["owner"] = 1,
                        ["cards"] = new List<object>(),
                        ["removed"] = new List<object>()
                    }
                },
                [P2PBattleProtocol.AuthorityPlayerHistoryStatesKey] =
                    new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["owner"] = 0,
                            ["revision"] = 3,
                            ["scalars"] = new Dictionary<string, object>()
                        }
                    }
            };
            Dictionary<string, object> flipped =
                P2PMessageTransform.FlipPerspective(result);
            Assert(flipped.ContainsKey(P2PBattleProtocol.AuthorityHiddenStatesKey) &&
                flipped.ContainsKey(P2PBattleProtocol.AuthorityPlayerHistoryStatesKey),
                "Authority absolute-owner snapshots were lost during perspective conversion.");
            Dictionary<string, object> hidden =
                ((List<object>)flipped[P2PBattleProtocol.AuthorityHiddenStatesKey])[0]
                    as Dictionary<string, object>;
            Assert(Convert.ToInt32(hidden["owner"]) == 0,
                "Authority hidden-state owner was incorrectly perspective-flipped.");
        }

        private static void TestAuthorityRequestValidation()
        {
            P2PWireMessage valid = new P2PWireMessage
            {
                Type = P2PBattleProtocol.AuthorityRequestUri,
                RequestId = "battle-guest-1",
                BattleId = "battle-1",
                ViewerId = 24680,
                Data = new Dictionary<string, object>
                {
                    [P2PBattleProtocol.AuthorityRequestIdKey] = "battle-guest-1",
                    [P2PBattleProtocol.AuthoritySourceKey] = 0,
                    [P2PBattleProtocol.AuthorityActionKey] = "play",
                    ["bid"] = "battle-1"
                }
            };
            Assert(P2PBattleProtocol.TryValidateAuthorityRequest(
                    valid, "battle-1", 24680, out string validError) &&
                string.IsNullOrEmpty(validError),
                "A valid authority request envelope was rejected: " + validError);

            P2PWireMessage staleBattle = new P2PWireMessage
            {
                Type = valid.Type,
                RequestId = valid.RequestId,
                BattleId = "battle-old",
                ViewerId = valid.ViewerId,
                Data = P2PJson.CloneDictionary(valid.Data)
            };
            Assert(!P2PBattleProtocol.TryValidateAuthorityRequest(
                    staleBattle, "battle-1", 24680, out string staleError) &&
                staleError.Contains("different battle"),
                "A stale authority request was not rejected by BattleId.");

            P2PWireMessage wrongViewer = new P2PWireMessage
            {
                Type = valid.Type,
                RequestId = valid.RequestId,
                BattleId = valid.BattleId,
                ViewerId = 13579,
                Data = P2PJson.CloneDictionary(valid.Data)
            };
            Assert(!P2PBattleProtocol.TryValidateAuthorityRequest(
                    wrongViewer, "battle-1", 24680, out string viewerError) &&
                viewerError.Contains("unknown Guest"),
                "An authority request from an unknown Guest was accepted.");

            Dictionary<string, object> wrongSourceData =
                P2PJson.CloneDictionary(valid.Data);
            wrongSourceData[P2PBattleProtocol.AuthoritySourceKey] = 1;
            P2PWireMessage wrongSource = new P2PWireMessage
            {
                Type = valid.Type,
                RequestId = valid.RequestId,
                BattleId = valid.BattleId,
                ViewerId = valid.ViewerId,
                Data = wrongSourceData
            };
            Assert(!P2PBattleProtocol.TryValidateAuthorityRequest(
                    wrongSource, "battle-1", 24680, out string sourceError) &&
                sourceError.Contains("source=0"),
                "An authority request with a Host source was accepted.");

            Dictionary<string, object> mismatchedIdData =
                P2PJson.CloneDictionary(valid.Data);
            mismatchedIdData[P2PBattleProtocol.AuthorityRequestIdKey] =
                "battle-guest-other";
            P2PWireMessage mismatchedId = new P2PWireMessage
            {
                Type = valid.Type,
                RequestId = valid.RequestId,
                BattleId = valid.BattleId,
                ViewerId = valid.ViewerId,
                Data = mismatchedIdData
            };
            Assert(!P2PBattleProtocol.TryValidateAuthorityRequest(
                    mismatchedId, "battle-1", 24680, out string idError) &&
                idError.Contains("requestId"),
                "A requestId mismatch between envelope and payload was accepted.");

            Dictionary<string, object> stalePayloadData =
                P2PJson.CloneDictionary(valid.Data);
            stalePayloadData["bid"] = "battle-old";
            P2PWireMessage stalePayload = new P2PWireMessage
            {
                Type = valid.Type,
                RequestId = valid.RequestId,
                BattleId = valid.BattleId,
                ViewerId = valid.ViewerId,
                Data = stalePayloadData
            };
            Assert(!P2PBattleProtocol.TryValidateAuthorityRequest(
                    stalePayload, "battle-1", 24680,
                    out string payloadBattleError) &&
                payloadBattleError.Contains("payload targeted a different battle"),
                "A stale payload battle ID was accepted.");

            Dictionary<string, object> unsupportedActionData =
                P2PJson.CloneDictionary(valid.Data);
            unsupportedActionData[P2PBattleProtocol.AuthorityActionKey] =
                "retire";
            P2PWireMessage unsupportedAction = new P2PWireMessage
            {
                Type = valid.Type,
                RequestId = valid.RequestId,
                BattleId = valid.BattleId,
                ViewerId = valid.ViewerId,
                Data = unsupportedActionData
            };
            Assert(!P2PBattleProtocol.TryValidateAuthorityRequest(
                    unsupportedAction, "battle-1", 24680,
                    out string unsupportedActionError) &&
                unsupportedActionError.Contains("unsupported action"),
                "An unsupported authority action was accepted.");
        }

        private static void TestRoomRules()
        {
            P2PRoomRules rules = new P2PRoomRules();
            Assert(rules.InitialMaxLife == P2PRoomRules.DefaultInitialMaxLife,
                "The initial maximum life default is incorrect.");
            Assert(rules.CustomFormatId == "unlimited",
                "The custom room format default is incorrect.");
            Assert(rules.TwoPickRule == null,
                "A constructed room unexpectedly has a Two Pick rule.");

            P2PWireMessage legacy = P2PJson.DeserializeMessage(
                "{\"type\":\"probe\",\"rules\":{\"isDeckOpen\":true}}");
            Assert(legacy.Rules.InitialMaxLife == P2PRoomRules.DefaultInitialMaxLife,
                "A legacy room message did not use the initial life default.");
            Assert(legacy.Rules.CustomFormatId == "unlimited",
                "A legacy room message did not use the Unlimited custom format default.");
            Assert(legacy.Rules.FormatDefinition == null,
                "A legacy room message unexpectedly created a format definition.");
            Assert(legacy.Rules.TwoPickRule == null,
                "A legacy room message unexpectedly created a Two Pick definition.");

            rules.InitialMaxLife = 19;
            Assert(rules.InitialMaxLife == 20,
                "Initial maximum life was not clamped to the lower limit.");
            rules.InitialMaxLife = 20;
            Assert(rules.InitialMaxLife == 20,
                "The lower initial maximum life limit was changed.");
            rules.InitialMaxLife = 200;
            Assert(rules.InitialMaxLife == 200,
                "The upper initial maximum life limit was changed.");
            rules.InitialMaxLife = 201;
            Assert(rules.InitialMaxLife == 200,
                "Initial maximum life was not clamped to the upper limit.");
        }

        private static void TestTransportRejectsThenAccepts()
        {
            byte[] roomToken = CreateToken(10);
            byte[] wrongToken = CreateToken(30);
            using (P2PTransport host = new P2PTransport())
            using (P2PTransport rejectedGuest = new P2PTransport())
            using (P2PTransport guest = new P2PTransport())
            using (ManualResetEventSlim rejected = new ManualResetEventSlim())
            using (ManualResetEventSlim hostConnected = new ManualResetEventSlim())
            using (ManualResetEventSlim guestConnected = new ManualResetEventSlim())
            using (ManualResetEventSlim hostReceived = new ManualResetEventSlim())
            using (ManualResetEventSlim guestReceived = new ManualResetEventSlim())
            {
                int hostDisconnects = 0;
                host.Disconnected += _ => Interlocked.Increment(ref hostDisconnects);
                host.Connected += hostConnected.Set;
                host.MessageReceived += message =>
                {
                    if (message.Type == "guest_probe")
                    {
                        hostReceived.Set();
                    }
                };
                rejectedGuest.Disconnected += _ => rejected.Set();
                guest.Connected += guestConnected.Set;
                guest.MessageReceived += message =>
                {
                    if (message.Type == "host_probe")
                    {
                        guestReceived.Set();
                    }
                };

                host.StartHost(IPAddress.Loopback, 0, roomToken);
                int port = host.BoundPort;
                Assert(port > 0, "The host did not bind an ephemeral port.");

                rejectedGuest.Connect(IPAddress.Loopback, port, wrongToken);
                Wait(rejected, "The invalid room token was not rejected.");
                Assert(Volatile.Read(ref hostDisconnects) == 0,
                    "Rejecting one client stopped the host listener.");

                using (TcpClient incompleteGuest = new TcpClient(AddressFamily.InterNetwork))
                {
                    incompleteGuest.Connect(IPAddress.Loopback, port);
                }

                guest.Connect(IPAddress.Loopback, port, roomToken);
                Wait(hostConnected, "The host did not accept the valid client after a rejection.");
                Wait(guestConnected, "The valid client did not finish its handshake.");
                Assert(guest.Send(new P2PWireMessage { Type = "guest_probe" }),
                    "The guest could not send a framed message.");
                Wait(hostReceived, "The host did not receive the guest message.");
                Assert(host.Send(new P2PWireMessage { Type = "host_probe" }),
                    "The host could not send a framed message.");
                Wait(guestReceived, "The guest did not receive the host message.");
                Assert(Volatile.Read(ref hostDisconnects) == 0,
                    "An incomplete handshake stopped the host listener.");
            }
        }

        private static void TestPerspectiveTransform()
        {
            Dictionary<string, object> source = new Dictionary<string, object>
            {
                ["isSelf"] = 1,
                ["idxChangeSeed"] = 11,
                ["oppoIdxChangeSeed"] = 22,
                ["targetList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["isSelf"] = 0,
                        ["cards"] = new List<object>
                        {
                            new Dictionary<string, object> { ["isSelf"] = true }
                        }
                    }
                },
                ["knownList"] = new List<object>
                {
                    new Dictionary<string, object> { ["isSelf"] = 1 }
                }
            };

            Dictionary<string, object> flipped = P2PMessageTransform.FlipPerspective(source);
            Assert(Convert.ToInt32(flipped["isSelf"]) == 0,
                "The root player perspective was not flipped.");
            Assert(Convert.ToInt32(flipped["idxChangeSeed"]) == 22 &&
                Convert.ToInt32(flipped["oppoIdxChangeSeed"]) == 11,
                "The player-specific index seeds were not swapped.");
            List<object> targets = (List<object>)flipped["targetList"];
            Dictionary<string, object> target = (Dictionary<string, object>)targets[0];
            Assert(Convert.ToInt32(target["isSelf"]) == 0,
                "An action-relative target side was incorrectly flipped.");
            List<object> cards = (List<object>)target["cards"];
            Dictionary<string, object> card = (Dictionary<string, object>)cards[0];
            Assert(card["isSelf"] is bool cardSide && cardSide,
                "A nested action-target side was incorrectly flipped.");
            List<object> knownCards = (List<object>)flipped["knownList"];
            Dictionary<string, object> knownCard =
                (Dictionary<string, object>)knownCards[0];
            Assert(Convert.ToInt32(knownCard["isSelf"]) == 0,
                "A receiver-relative known-card side was not flipped.");
            Assert(Convert.ToInt32(source["isSelf"]) == 1,
                "Perspective conversion modified the source message.");

            Dictionary<string, object> oneSeed = P2PMessageTransform.FlipPerspective(
                new Dictionary<string, object> { ["idxChangeSeed"] = 33 });
            Assert(!oneSeed.ContainsKey("idxChangeSeed") &&
                Convert.ToInt32(oneSeed["oppoIdxChangeSeed"]) == 33,
                "A single player index seed was not moved to the opponent side.");

            Dictionary<string, object> ownFollowerBuff =
                P2PMessageTransform.PrepareOpponentBattleMessage(
                    new Dictionary<string, object>
                {
                    ["targetList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["targetIdx"] = 7,
                            ["isSelf"] = 1
                        }
                    },
                    ["knownList"] = new List<object>
                    {
                        new Dictionary<string, object> { ["isSelf"] = 1 }
                    }
                });
            Assert(!ownFollowerBuff.ContainsKey("targetList"),
                "A client target list was not converted for the opponent receiver.");
            Dictionary<string, object> buffTarget = (Dictionary<string, object>)
                ((List<object>)ownFollowerBuff["oppoTargetList"])[0];
            Assert(Convert.ToInt32(buffTarget["isSelf"]) == 1,
                "An acting player's own buff target changed sides in transit.");
            Dictionary<string, object> buffKnownCard = (Dictionary<string, object>)
                ((List<object>)ownFollowerBuff["knownList"])[0];
            Assert(Convert.ToInt32(buffKnownCard["isSelf"]) == 0,
                "Receiver-relative known cards were not flipped with battle targets.");

            Dictionary<string, object> chatStamp =
                P2PMessageTransform.PrepareOpponentBattleMessage(
                    new Dictionary<string, object>
                    {
                        ["uri"] = "ChatStamp",
                        ["stamp"] = "3"
                    });
            Assert(!chatStamp.ContainsKey("stamp") &&
                chatStamp.TryGetValue("chatStamp", out object rawChatStamp) &&
                rawChatStamp is Dictionary<string, object> chatStampPayload &&
                Convert.ToString(chatStampPayload["stamp"]) == "3",
                "Battle ChatStamp was not wrapped in the server-compatible payload.");

            Dictionary<string, object> opponentLeaderAttack =
                P2PMessageTransform.PrepareOpponentBattleMessage(
                    new Dictionary<string, object>
                {
                    ["targetList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["targetIdx"] = 0,
                            ["isSelf"] = 0
                        }
                    }
                });
            Dictionary<string, object> attackTarget = (Dictionary<string, object>)
                ((List<object>)opponentLeaderAttack["oppoTargetList"])[0];
            Assert(Convert.ToInt32(attackTarget["isSelf"]) == 0,
                "An opponent leader attack was redirected to the acting side.");

            Dictionary<string, object> authorityLocalAttack =
                new Dictionary<string, object>
                {
                    ["targetList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["targetIdx"] = 35,
                            ["isSelf"] = 0
                        }
                    }
                };
            P2PMessageTransform.NormalizeAuthorityLocalReplayMessage(
                authorityLocalAttack);
            Assert(!authorityLocalAttack.ContainsKey("targetList") &&
                authorityLocalAttack.ContainsKey("oppoTargetList"),
                "A local authority attack did not use the server-compatible opponent target field.");
            List<object> authorityTargets =
                authorityLocalAttack["oppoTargetList"] as List<object>;
            Assert(authorityTargets != null && authorityTargets.Count == 1,
                "The local authority attack target list was malformed.");
            Dictionary<string, object> authorityAttackTarget =
                authorityTargets[0] as Dictionary<string, object>;
            Assert(authorityAttackTarget != null &&
                Convert.ToInt32(authorityAttackTarget["targetIdx"]) == 35 &&
                Convert.ToInt32(authorityAttackTarget["isSelf"]) == 0,
                "Authority target data changed while converting it to the native server field.");
        }

        private static void TestSkillTargetPerspectiveTransform()
        {
            Dictionary<string, object> source = new Dictionary<string, object>
            {
                ["skillTarget"] = "10123",
                ["skillTargetList"] = new List<object>
                    { "01234", "1234", 10005, "invalid", 0 },
                ["orderList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["move"] = new Dictionary<string, object>
                        {
                            ["skillTarget"] = "10007"
                        }
                    }
                },
                ["uList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["idxList"] = new List<object> { 7 },
                        ["from"] = 10,
                        ["to"] = 30,
                        ["isSelf"] = 1
                    }
                },
                ["uList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["idxList"] = new List<object> { 7 },
                        ["from"] = 10,
                        ["to"] = 30,
                        ["isSelf"] = 1
                    }
                }
            };

            Dictionary<string, object> flipped = P2PMessageTransform.FlipPerspective(source);
            Assert((string)flipped["skillTarget"] == "123",
                "A skill target with source-player side 1 was not flipped.");
            List<object> targets = (List<object>)flipped["skillTargetList"];
            Assert((string)targets[0] == "11234" &&
                (string)targets[1] == "11234" &&
                Convert.ToInt32(targets[2]) == 5,
                "Skill target list entries did not preserve their low digits while flipping sides.");
            Assert((string)targets[3] == "invalid" && Convert.ToInt32(targets[4]) == 0,
                "Invalid skill target values were unexpectedly changed.");
            Dictionary<string, object> move = (Dictionary<string, object>)
                ((List<object>)flipped["orderList"])[0];
            Dictionary<string, object> nestedMove = (Dictionary<string, object>)move["move"];
            Assert((string)nestedMove["skillTarget"] == "7",
                "A nested skill target was not flipped.");
            Assert((string)source["skillTarget"] == "10123",
                "Skill target perspective conversion modified the source message.");
        }

        private static void TestHiddenSnapshotPerspectiveTransform()
        {
            Dictionary<string, object> source = new Dictionary<string, object>
            {
                ["p2pHiddenOwner"] = 1,
                ["p2pHiddenRemoved"] = new List<object> { 17, 29 },
                ["p2pHiddenCards"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["idx"] = 42,
                        ["isSelf"] = 1,
                        ["p2pGenericKeys"] = new Dictionary<string, object>
                        {
                            ["isSelf"] = 7
                        },
                        ["p2pDamagedCounter"] = new Dictionary<string, object>
                        {
                            ["selfTurn"] = 3,
                            ["opponentTurn"] = 4
                        },
                        ["p2pMaxAttackableCount"] = 3,
                        ["p2pModifiers"] = new Dictionary<string, object>
                        {
                            ["cost"] = new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["kind"] = "add",
                                    ["value"] = -2,
                                    ["resident"] = 1
                                }
                            }
                        },
                        ["p2pSkillCollections"] = new Dictionary<string, object>
                        {
                            ["lifeHistory"] = new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["kind"] = "damage",
                                    ["value"] = 2,
                                    ["turn"] = 6,
                                    ["turnOwner"] = 1
                                }
                            }
                        }
                    }
                },
                ["p2pPlayerHistory"] = new Dictionary<string, object>
                {
                    ["owner"] = 1,
                    ["revision"] = 4,
                    ["class"] = new Dictionary<string, object>
                    {
                        ["p2pSkillActivationIds"] = new List<object>
                        {
                            9000000001L
                        },
                        ["p2pDamagedCounter"] = new Dictionary<string, object>
                        {
                            ["selfTurn"] = 8,
                            ["opponentTurn"] = 1
                        }
                    },
                    ["lists"] = new Dictionary<string, object>
                    {
                        ["CemeteryList"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["owner"] = 1,
                                ["idx"] = 17,
                                ["cardId"] = 101001001
                            }
                        },
                        ["DestroyedWhenDestroyCards"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["owner"] = 1,
                                ["idx"] = 17,
                                ["cardId"] = 101001001
                            }
                        },
                        ["GameTurnPlayCards"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["turnOwner"] = 1,
                                ["card"] = new Dictionary<string, object>
                                {
                                    ["owner"] = 1,
                                    ["idx"] = 42,
                                    ["isSelf"] = 9
                                }
                            }
                        }
                    }
                },
                ["p2pPlayerHistoryBefore"] = new Dictionary<string, object>
                {
                    ["owner"] = 1,
                    ["revision"] = 3,
                    ["lists"] = new Dictionary<string, object>
                    {
                        ["TurnDestroyCards"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["turnOwner"] = 1,
                                ["card"] = new Dictionary<string, object>
                                {
                                    ["owner"] = 1,
                                    ["idx"] = 17,
                                    ["cardId"] = 101001001
                                }
                            }
                        }
                    }
                },
                ["p2pAuthoritativeSkillTargets"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["owner"] = 1,
                        ["ownerIdx"] = 42,
                        ["targets"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["owner"] = 1,
                                ["idx"] = 17,
                                ["cardId"] = 101001001
                            },
                            new Dictionary<string, object>
                            {
                                ["owner"] = 0,
                                ["idx"] = 29,
                                ["cardId"] = 102001001
                            }
                        }
                    }
                },
                ["p2pAuthoritativeSkillEvaluations"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["owner"] = 1,
                        ["ownerIdx"] = 42,
                        ["values"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["keyword"] = "damage",
                                ["value"] = 6,
                                ["isSelf"] = 9
                            }
                        },
                        ["preprocess"] = new List<object> { 1, 0 }
                    }
                },
                [P2PBattleProtocol.AuthorityResultActionIdKey] = 42,
                [P2PBattleProtocol.ActionManifestKey] = new Dictionary<string, object>
                {
                    ["version"] = P2PBattleProtocol.ActionManifestVersion,
                    ["seq"] = 7,
                    ["uri"] = "PlayActions",
                    ["p2pAuthoritativeSkillEvaluations"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["owner"] = 1,
                            ["ownerIdx"] = 42,
                            ["conditions"] = new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["ordinal"] = 0,
                                    ["prePlay"] = 0,
                                    ["skipTarget"] = 0,
                                    ["result"] = 1
                                }
                            }
                        }
                    },
                    ["state"] = new Dictionary<string, object>
                    {
                        ["p2pHiddenOwner"] = 1
                    }
                }
            };

            Dictionary<string, object> flipped =
                P2PMessageTransform.FlipPerspective(source);
            Assert(Convert.ToInt32(flipped["p2pHiddenOwner"]) == 1,
                "Hidden snapshot owner was unexpectedly perspective-flipped.");
            List<object> removed = (List<object>)flipped["p2pHiddenRemoved"];
            Assert(Convert.ToInt32(removed[0]) == 17 &&
                Convert.ToInt32(removed[1]) == 29,
                "Hidden snapshot tombstones were unexpectedly perspective-flipped.");
            Dictionary<string, object> hidden = (Dictionary<string, object>)
                ((List<object>)flipped["p2pHiddenCards"])[0];
            Assert(Convert.ToInt32(hidden["isSelf"]) == 1,
                "Hidden snapshot card metadata was unexpectedly perspective-flipped.");
            Dictionary<string, object> genericKeys =
                (Dictionary<string, object>)hidden["p2pGenericKeys"];
            Assert(Convert.ToInt32(genericKeys["isSelf"]) == 7,
                "A generic skill key named isSelf was corrupted by perspective conversion.");
            Dictionary<string, object> damagedCounter =
                (Dictionary<string, object>)hidden["p2pDamagedCounter"];
            Assert(Convert.ToInt32(damagedCounter["selfTurn"]) == 3 &&
                Convert.ToInt32(damagedCounter["opponentTurn"]) == 4,
                "Card-owner-relative damage counters were perspective-flipped.");
            Assert(Convert.ToInt32(hidden["p2pMaxAttackableCount"]) == 3,
                "A hidden card's maximum attack count was perspective-flipped.");
            Dictionary<string, object> modifiers =
                (Dictionary<string, object>)hidden["p2pModifiers"];
            Dictionary<string, object> costModifier =
                (Dictionary<string, object>)
                    ((List<object>)modifiers["cost"])[0];
            Assert(Convert.ToInt32(costModifier["resident"]) == 1 &&
                Convert.ToInt32(costModifier["value"]) == -2,
                "A hidden-card modifier was corrupted by perspective conversion.");
            Dictionary<string, object> skillCollections =
                (Dictionary<string, object>)hidden["p2pSkillCollections"];
            Dictionary<string, object> lifeHistory =
                (Dictionary<string, object>)
                    ((List<object>)skillCollections["lifeHistory"])[0];
            Assert(Convert.ToInt32(lifeHistory["turnOwner"]) == 1,
                "An absolute hidden-card history turn owner was perspective-flipped.");
            Dictionary<string, object> history =
                (Dictionary<string, object>)flipped["p2pPlayerHistory"];
            Assert(Convert.ToInt32(history["owner"]) == 1 &&
                Convert.ToInt32(history["revision"]) == 4,
                "Player history ownership was unexpectedly perspective-flipped.");
            Dictionary<string, object> classState =
                (Dictionary<string, object>)history["class"];
            List<object> activationIds =
                (List<object>)classState["p2pSkillActivationIds"];
            Assert(Convert.ToInt64(activationIds[0]) == 9000000001L,
                "A class skill-activation history ID was perspective-flipped.");
            Dictionary<string, object> historyLists =
                (Dictionary<string, object>)history["lists"];
            Dictionary<string, object> historyEntry =
                (Dictionary<string, object>)
                    ((List<object>)historyLists["GameTurnPlayCards"])[0];
            Dictionary<string, object> historyCard =
                (Dictionary<string, object>)historyEntry["card"];
            Assert(Convert.ToInt32(historyEntry["turnOwner"]) == 1 &&
                Convert.ToInt32(historyCard["owner"]) == 1 &&
                Convert.ToInt32(historyCard["isSelf"]) == 9,
                "Player history metadata was corrupted by perspective conversion.");
            Dictionary<string, object> cemeteryCard =
                (Dictionary<string, object>)
                    ((List<object>)historyLists["CemeteryList"])[0];
            Dictionary<string, object> destroyedCard =
                (Dictionary<string, object>)
                    ((List<object>)historyLists["DestroyedWhenDestroyCards"])[0];
            Assert(Convert.ToInt32(cemeteryCard["owner"]) == 1 &&
                Convert.ToInt32(destroyedCard["owner"]) == 1,
                "Destroyed-card history ownership was perspective-flipped.");
            Dictionary<string, object> preHistory =
                (Dictionary<string, object>)flipped["p2pPlayerHistoryBefore"];
            Dictionary<string, object> preHistoryLists =
                (Dictionary<string, object>)preHistory["lists"];
            Dictionary<string, object> turnDestroyEntry =
                (Dictionary<string, object>)
                    ((List<object>)preHistoryLists["TurnDestroyCards"])[0];
            Dictionary<string, object> turnDestroyCard =
                (Dictionary<string, object>)turnDestroyEntry["card"];
            Assert(Convert.ToInt32(preHistory["owner"]) == 1 &&
                Convert.ToInt32(preHistory["revision"]) == 3 &&
                Convert.ToInt32(turnDestroyEntry["turnOwner"]) == 1 &&
                Convert.ToInt32(turnDestroyCard["owner"]) == 1,
                "Pre-action destroyed-card history was perspective-flipped.");
            Dictionary<string, object> authoritativeTargets =
                (Dictionary<string, object>)
                    ((List<object>)flipped["p2pAuthoritativeSkillTargets"])[0];
            List<object> targetReferences =
                (List<object>)authoritativeTargets["targets"];
            Assert(Convert.ToInt32(authoritativeTargets["owner"]) == 1 &&
                Convert.ToInt32(
                    ((Dictionary<string, object>)targetReferences[0])["owner"]) == 1 &&
                Convert.ToInt32(
                    ((Dictionary<string, object>)targetReferences[1])["owner"]) == 0,
                "Authoritative random target ownership was perspective-flipped.");
            Dictionary<string, object> authoritativeEvaluation =
                (Dictionary<string, object>)
                    ((List<object>)flipped[
                        "p2pAuthoritativeSkillEvaluations"])[0];
            Dictionary<string, object> authoritativeValue =
                (Dictionary<string, object>)
                    ((List<object>)authoritativeEvaluation["values"])[0];
            List<object> preprocessResults =
                (List<object>)authoritativeEvaluation["preprocess"];
            Assert(Convert.ToInt32(authoritativeEvaluation["owner"]) == 1 &&
                Convert.ToInt32(authoritativeValue["value"]) == 6 &&
                Convert.ToInt32(authoritativeValue["isSelf"]) == 9 &&
                Convert.ToInt32(preprocessResults[0]) == 1 &&
                Convert.ToInt32(preprocessResults[1]) == 0,
                "Authoritative private skill evaluation was perspective-flipped.");
            Assert(Convert.ToInt32(flipped[
                    P2PBattleProtocol.AuthorityResultActionIdKey]) == 42,
                "Authority replay action ID was perspective-flipped or lost.");
            Dictionary<string, object> manifest =
                (Dictionary<string, object>)flipped[P2PBattleProtocol.ActionManifestKey];
            Dictionary<string, object> manifestEvaluation =
                (Dictionary<string, object>)((List<object>)manifest[
                    "p2pAuthoritativeSkillEvaluations"])[0];
            Dictionary<string, object> manifestCondition =
                (Dictionary<string, object>)((List<object>)manifestEvaluation[
                    "conditions"])[0];
            Assert(Convert.ToInt32(manifest["version"]) ==
                    P2PBattleProtocol.ActionManifestVersion &&
                Convert.ToInt32(manifest["seq"]) == 7 &&
                Convert.ToInt32(manifestCondition["result"]) == 1 &&
                Convert.ToInt32(((Dictionary<string, object>)manifest["state"])[
                    "p2pHiddenOwner"]) == 1,
                "Action manifest metadata was perspective-flipped or lost.");
        }

        private static void TestDealState()
        {
            P2PDealState state = new P2PDealState();
            Assert(!state.TryClaim(true, out _, out _),
                "A deal seed was available before initialization.");

            state.Initialize(123, 456);
            Assert(state.TryClaim(
                    true,
                    out int hostSeed,
                    out int hostOpponentSeed) &&
                hostSeed == 123 && hostOpponentSeed == 456,
                "The host did not receive the host/guest index-change seed pair.");
            Assert(state.TryClaim(
                    false,
                    out int guestSeed,
                    out int guestOpponentSeed) &&
                guestSeed == 456 && guestOpponentSeed == 123,
                "The guest did not receive the guest/host index-change seed pair.");
            Assert(!state.TryClaim(true, out _, out _) &&
                !state.TryClaim(false, out _, out _),
                "A duplicate deal could reset an active index-change generator.");

            state.Reset();
            Assert(!state.TryClaim(true, out _, out _) &&
                !state.TryClaim(false, out _, out _),
                "Reset retained index-change seeds from the previous round.");
            state.Initialize(789, 987);
            Assert(state.TryClaim(
                    false,
                    out guestSeed,
                    out guestOpponentSeed) &&
                guestSeed == 987 && guestOpponentSeed == 789,
                "A second round reused the previous guest seed.");
        }

        private static void TestDisconnectPolicy()
        {
            Assert(P2PDisconnectPolicy.Evaluate(
                    true, false, false, true, true, false, false, false) ==
                P2PDisconnectAction.BattleResult,
                "A battle disconnect did not request a final battle result.");
            Assert(P2PDisconnectPolicy.Evaluate(
                    true, false, false, false, false, true, true, false) ==
                P2PDisconnectAction.ForceRoomExit,
                "A disconnect during room-to-battle startup could leave matching stuck.");
            Assert(P2PDisconnectPolicy.Evaluate(
                    true, false, false, false, false, true, false, true) ==
                P2PDisconnectAction.RoomRelease,
                "A room disconnect did not use the normal room release path.");
            Assert(P2PDisconnectPolicy.Evaluate(
                    true, false, true, false, false, true, true, false) ==
                P2PDisconnectAction.None,
                "A handled room disconnect was processed twice.");
            Assert(P2PDisconnectPolicy.Evaluate(
                    false, false, false, true, true, false, false, false) ==
                P2PDisconnectAction.None,
                "Disconnect recovery ran while the peer was still connected.");
        }

        private static void TestDeliverySequence()
        {
            P2PDeliverySequence sequence = new P2PDeliverySequence();
            Assert(!sequence.TryNext(out int blocked) && blocked == 0,
                "A guest sequence was allocated before RoomEntry.");
            Assert(sequence.Open(), "The guest delivery sequence did not open.");
            Assert(sequence.TryNext(out int first) && first == 1,
                "The first guest delivery after RoomEntry was not sequence 1.");
            Assert(sequence.TryNext(out int second) && second == 2,
                "Guest delivery sequencing did not advance consecutively.");
            Assert(!sequence.Open(),
                "A duplicate RoomEntry unexpectedly reset the guest sequence.");
            Assert(sequence.TryNext(out int third) && third == 3,
                "A duplicate RoomEntry introduced a sequence gap or reset.");
            sequence.Reset();
            Assert(!sequence.TryNext(out blocked) && blocked == 0,
                "Reset left the guest delivery sequence open.");
            Assert(sequence.Open() && sequence.TryNext(out first) && first == 1,
                "A new room session did not restart guest sequencing at 1.");
        }

        private static void TestRoomRoundState()
        {
            P2PRoomRoundState state = new P2PRoomRoundState();
            Assert(!state.MarkReady(true), "The host started a round without the guest.");
            Assert(state.MarkReady(false), "Two ready players did not start a round.");
            Assert(state.ReadySent && !state.HostReady && !state.GuestReady,
                "Starting a round did not consume both ready states.");
            Assert(!state.MarkReady(false), "A duplicate ready message started another round.");
            Assert(!state.HostReady && !state.GuestReady,
                "A duplicate ready message contaminated the next round.");

            state.Reenter(false);
            Assert(!state.MarkReady(false),
                "The early-returning guest started a round without the host.");
            state.Reenter(true);
            Assert(state.GuestReady,
                "The host returning later erased the guest's early ready state.");
            Assert(state.MarkReady(true),
                "Players returning at different times could not start a second round.");

            state.Reenter(true);
            Assert(!state.MarkReady(true),
                "The early-returning host started a round without the guest.");
            state.Reenter(false);
            Assert(state.HostReady,
                "The guest returning later erased the host's early ready state.");
            state.CancelReady(true);
            Assert(!state.MarkReady(false), "A cancelled ready state was not cleared.");
            Assert(state.MarkReady(true), "Ready state did not recover after cancellation.");
        }

        private static void TestBattleResults()
        {
            P2PBattleResultPair hostLifeWin = P2PBattleResult.FromHostLocalResult(101);
            Assert(hostLifeWin.Host == 101 && hostLifeWin.Guest == 102,
                "A host life win was not delivered as each client's local result.");

            P2PBattleResultPair guestLifeWin =
                P2PBattleResult.FromLocalResult(false, 101);
            Assert(guestLifeWin.Host == 102 && guestLifeWin.Guest == 101,
                "A guest life win was not converted from the reporting side.");

            P2PBattleResultPair hostRetired = P2PBattleResult.FromHostLocalResult(106);
            Assert(hostRetired.Host == 106 && hostRetired.Guest == 105,
                "A host retirement produced the wrong winner.");
            P2PBattleResultPair guestRetiredExplicit =
                P2PBattleResult.FromRetirement(false);
            Assert(guestRetiredExplicit.Host == P2PBattleResult.RetireWin &&
                guestRetiredExplicit.Guest == P2PBattleResult.RetireLose,
                "The explicit Host retirement result mapping is incorrect.");

            P2PBattleResultPair guestRetired =
                P2PBattleResult.FromLocalResult(false, 106);
            Assert(guestRetired.Host == 105 && guestRetired.Guest == 106,
                "A guest retirement produced the wrong winner.");

            Assert(P2PBattleResult.Invert(P2PBattleResult.DisconnectWin) ==
                    P2PBattleResult.DisconnectLose,
                "A peer disconnect was not converted to a local victory result.");
            Assert(P2PBattleResult.Invert(1) == 1,
                "A non-paired result code was unexpectedly changed.");
            Assert(P2PBattleResult.IsPairedResult(108) &&
                P2PBattleResult.IsPairedResult(208) &&
                !P2PBattleResult.IsPairedResult(0),
                "Final paired battle result validation is incorrect.");
            Assert(P2PBattleResult.IsTerminalResult(210) &&
                !P2PBattleResult.IsTerminalResult(209),
                "Reserved max-turn and non-terminal result handling is incorrect.");
            Assert(P2PBattleResult.TryCreateResultPair(true, 210,
                    out P2PBattleResultPair maxTurnResult) &&
                maxTurnResult.Host == 210 && maxTurnResult.Guest == 210,
                "Max-turn result was incorrectly inverted between Host and Guest.");
            Assert(P2PBattleResult.ResolveLocalResultAfterDisconnect(true, 0) == 106,
                "A locally retired player was awarded a disconnect victory.");
            Assert(P2PBattleResult.ResolveLocalResultAfterDisconnect(false, 102) == 102,
                "A known local defeat was overwritten after disconnect.");
            Assert(P2PBattleResult.ResolveLocalResultAfterDisconnect(false, 210) == 210,
                "A known terminal max-turn result was overwritten after disconnect.");
            Assert(P2PBattleResult.ResolveLocalResultAfterDisconnect(false, 0) == 201,
                "An unresolved peer disconnect did not award the connected player.");
        }

        private static void TestBattleProtocol()
        {
            Assert(P2PBattleProtocol.GetRoute("PlayActions") == P2PBattleRoute.Opponent,
                "A play action was not routed to the opponent.");
            Assert(P2PBattleProtocol.GetRoute("Judge") == P2PBattleRoute.Source,
                "Judge was not routed back to the player who must start their turn.");
            Assert(P2PBattleProtocol.GetRoute("Echo") == P2PBattleRoute.Consume,
                "A consistency echo was incorrectly delivered as a battle operation.");
            Assert(P2PBattleProtocol.RequiresActiveTurnState("TurnEnd") &&
                P2PBattleProtocol.RequiresActiveTurnState("TurnEndFinal") &&
                P2PBattleProtocol.RequiresActiveTurnState("TurnStart"),
                "Turn transition messages did not activate the runtime turn state.");
            Assert(P2PBattleProtocol.CarriesBattleStateCheckpoint("TurnEndActions") &&
                P2PBattleProtocol.CarriesBattleStateCheckpoint("TurnEnd") &&
                P2PBattleProtocol.CarriesBattleStateCheckpoint("TurnEndFinal") &&
                P2PBattleProtocol.CarriesBattleStateCheckpoint("TurnStart") &&
                !P2PBattleProtocol.CarriesBattleStateCheckpoint("PlayActions"),
                "Battle-state checkpoints do not cover the complete turn transition.");

            List<int> hiddenDeck = new List<int> { 900001, 900002, 900003 };
            Dictionary<string, object> matchedWithDeck = new Dictionary<string, object>
            {
                [P2PBattleProtocol.OpponentDeckIdentityKey] =
                    P2PBattleProtocol.CreateDeckIdentityPayload(hiddenDeck)
            };
            Assert(P2PBattleProtocol.TryReadDeckIdentityPayload(
                    matchedWithDeck,
                    hiddenDeck.Count,
                    out List<object> decodedDeck,
                    out string deckError),
                "A valid opponent deck identity table was rejected: " + deckError);
            Assert(decodedDeck.Count == hiddenDeck.Count &&
                Convert.ToInt32(((Dictionary<string, object>)decodedDeck[1])["idx"]) == 2 &&
                Convert.ToInt32(((Dictionary<string, object>)decodedDeck[1])["cardId"]) == 900002,
                "An opponent deck identity table changed during validation.");
            Dictionary<string, object> clonedMatchedWithDeck =
                P2PJson.CloneDictionary(matchedWithDeck);
            Assert(P2PBattleProtocol.TryReadDeckIdentityPayload(
                    clonedMatchedWithDeck,
                    hiddenDeck.Count,
                    out _,
                    out _),
                "An opponent deck identity table did not survive wire JSON conversion.");

            Dictionary<string, object> wrongCount = new Dictionary<string, object>
            {
                [P2PBattleProtocol.OpponentDeckIdentityKey] =
                    P2PBattleProtocol.CreateDeckIdentityPayload(hiddenDeck).Take(2).ToList()
            };
            Assert(!P2PBattleProtocol.TryReadDeckIdentityPayload(
                    wrongCount, hiddenDeck.Count, out _, out _),
                "A truncated opponent deck identity table was accepted.");

            List<object> invalidCard = P2PBattleProtocol.CreateDeckIdentityPayload(hiddenDeck);
            ((Dictionary<string, object>)invalidCard[1]).Remove("cardId");
            Dictionary<string, object> missingCardId = new Dictionary<string, object>
            {
                [P2PBattleProtocol.OpponentDeckIdentityKey] = invalidCard
            };
            Assert(!P2PBattleProtocol.TryReadDeckIdentityPayload(
                    missingCardId, hiddenDeck.Count, out _, out _),
                "An opponent deck identity entry without cardId was accepted.");

            List<object> zeroCardId = P2PBattleProtocol.CreateDeckIdentityPayload(hiddenDeck);
            ((Dictionary<string, object>)zeroCardId[0])["cardId"] = 0;
            Dictionary<string, object> invalidCardId = new Dictionary<string, object>
            {
                [P2PBattleProtocol.OpponentDeckIdentityKey] = zeroCardId
            };
            Assert(!P2PBattleProtocol.TryReadDeckIdentityPayload(
                    invalidCardId, hiddenDeck.Count, out _, out _),
                "An opponent deck identity entry with an invalid cardId was accepted.");

            List<object> invalidIndex = P2PBattleProtocol.CreateDeckIdentityPayload(hiddenDeck);
            ((Dictionary<string, object>)invalidIndex[2])["idx"] = 1;
            Dictionary<string, object> duplicateIndex = new Dictionary<string, object>
            {
                [P2PBattleProtocol.OpponentDeckIdentityKey] = invalidIndex
            };
            Assert(!P2PBattleProtocol.TryReadDeckIdentityPayload(
                    duplicateIndex, hiddenDeck.Count, out _, out _),
                "An opponent deck identity table with duplicate indexes was accepted.");

            P2PBattleSelectionTracker selectionTracker =
                new P2PBattleSelectionTracker();
            Assert(selectionTracker.RecordHandData(
                    2,
                    new List<object>
                    {
                        0, false, true, "7", "0", new List<int> { 4 }
                    },
                    out _),
                "A burial-rite StartSelect hand message was not recorded.");
            Assert(selectionTracker.RecordHandData(
                    2,
                    new List<object> { 7, false, true, "1003" },
                    out _),
                "A burial-rite CompleteSelect hand message was not recorded.");

            Dictionary<string, object> selectedBurialPlay =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["playIdx"] = 7,
                    ["type"] = 30,
                    ["orderList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["move"] = new Dictionary<string, object>
                            {
                                ["idx"] = new List<object> { 3 },
                                ["from"] = 10,
                                ["to"] = 20,
                                ["isSelf"] = 1
                            }
                        },
                        new Dictionary<string, object>
                        {
                            ["move"] = new Dictionary<string, object>
                            {
                                ["idx"] = new List<object> { 3 },
                                ["from"] = 20,
                                ["to"] = 30,
                                ["isSelf"] = 1
                            }
                        }
                    }
                };
            Assert(selectionTracker.PrepareOutgoingAction(
                    selectedBurialPlay,
                    _ => new List<int> { 9 },
                    out _),
                "A recorded burial-rite selection was not attached to PlayActions.");
            Assert(Convert.ToInt32(selectedBurialPlay["type"]) == 31,
                "A burial-rite play was not marked as PLAY_HAND_SELECT.");
            Dictionary<string, object> selectedBurialTarget =
                (Dictionary<string, object>)
                ((List<object>)selectedBurialPlay["targetList"])[0];
            List<int> selectedBurialSkills =
                (List<int>)selectedBurialTarget["selectSkillIndex"];
            Assert(Convert.ToInt32(selectedBurialTarget["targetIdx"]) == 3 &&
                Convert.ToInt32(selectedBurialTarget["isSelf"]) == 1 &&
                selectedBurialSkills.Count == 1 && selectedBurialSkills[0] == 4,
                "The burial material or active burial skill index was not preserved.");

            P2PBattleCardTracker selectedBurialCardTracker =
                new P2PBattleCardTracker();
            selectedBurialCardTracker.Reset(new List<int>(), new List<int>());
            selectedBurialCardTracker.PrepareOutgoingAction(
                true,
                selectedBurialPlay,
                out _,
                out _,
                cardIndex => cardIndex == 3 ? 103 :
                    cardIndex == 7 ? 107 : 0,
                cardIndex => cardIndex == 3 ? 4 :
                    cardIndex == 7 ? 6 : -1);
            List<object> selectedBurialKnown =
                (List<object>)selectedBurialPlay["knownList"];
            Dictionary<string, object> selectedBurialMaterial =
                (Dictionary<string, object>)selectedBurialKnown[0];
            Assert(Convert.ToInt32(selectedBurialMaterial["idx"]) == 3 &&
                Convert.ToInt32(selectedBurialMaterial["cardId"]) == 103 &&
                Convert.ToInt32(selectedBurialMaterial["cost"]) == 4 &&
                Convert.ToInt32(selectedBurialMaterial["from"]) == 10 &&
                Convert.ToInt32(selectedBurialMaterial["to"]) == 20,
                "The selected burial material was not revealed with its movement state.");

            Dictionary<string, object> receivedSelectedBurial =
                P2PMessageTransform.PrepareOpponentBattleMessage(
                    selectedBurialPlay);
            Assert(!receivedSelectedBurial.ContainsKey("targetList") &&
                receivedSelectedBurial.ContainsKey("oppoTargetList"),
                "The burial target list was not converted for the opponent receiver.");
            Dictionary<string, object> receivedSelectedBurialTarget =
                (Dictionary<string, object>)
                ((List<object>)receivedSelectedBurial["oppoTargetList"])[0];
            Dictionary<string, object> receivedSelectedBurialMaterial =
                (Dictionary<string, object>)
                ((List<object>)receivedSelectedBurial["knownList"])[0];
            Assert(Convert.ToInt32(receivedSelectedBurialTarget["isSelf"]) == 1 &&
                Convert.ToInt32(receivedSelectedBurialMaterial["isSelf"]) == 0,
                "Burial action targets and revealed cards used the wrong perspectives.");

            selectedBurialPlay["keyAction"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["type"] = 6,
                    ["selectCard"] = new Dictionary<string, object>
                    {
                        ["cardIdx"] = new List<object> { 3 },
                        ["open"] = 1
                    }
                }
            };
            receivedSelectedBurial =
                P2PMessageTransform.PrepareOpponentBattleMessage(
                    selectedBurialPlay);
            Dictionary<string, object> receivedBurialKeyAction =
                (Dictionary<string, object>)
                ((List<object>)receivedSelectedBurial["keyAction"])[0];
            Assert(receivedBurialKeyAction.TryGetValue("cardIdx", out object rawBurialIndexes) &&
                ((List<object>)rawBurialIndexes).Count == 1 &&
                Convert.ToInt32(((List<object>)rawBurialIndexes)[0]) == 3,
                "The burial key action was not normalized for the battle receiver.");

            foreach (int choiceType in new[] { 1, 5, 7, 8 })
            {
                Dictionary<string, object> choicePlay =
                    new Dictionary<string, object>
                    {
                        ["uri"] = "PlayActions",
                        ["keyAction"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["type"] = choiceType,
                                ["cardId"] = 123456,
                                ["selectCard"] = new Dictionary<string, object>
                                {
                                    ["cardId"] = new List<object> { 654321 },
                                    ["open"] = 0
                                }
                            }
                        }
                    };

                Dictionary<string, object> receivedChoice =
                    P2PMessageTransform.PrepareOpponentBattleMessage(choicePlay);
                Dictionary<string, object> receivedChoiceKeyAction =
                    (Dictionary<string, object>)
                    ((List<object>)receivedChoice["keyAction"])[0];
                Assert(receivedChoiceKeyAction["selectCard"] is List<object> selectedChoiceIds &&
                    selectedChoiceIds.Count == 1 &&
                    Convert.ToInt32(selectedChoiceIds[0]) == 654321,
                    $"Choice key action type {choiceType} was not normalized for the battle receiver.");
                Assert(Convert.ToInt32(receivedChoiceKeyAction["cardId"]) == 123456,
                    $"Choice key action type {choiceType} lost its source card ID.");
            }

            P2PBattleSelectionTracker discardSelectionTracker =
                new P2PBattleSelectionTracker();
            Assert(discardSelectionTracker.RecordHandData(
                    2,
                    new List<object>
                    {
                        0, false, false, "12", "0", new List<int> { 6 }
                    },
                    out _),
                "A non-burial discard selection was not recorded.");
            Assert(discardSelectionTracker.RecordHandData(
                    2,
                    new List<object> { 7, false, false, "1008" },
                    out _),
                "A selected discard target was not recorded.");
            Dictionary<string, object> discardPlay =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["playIdx"] = 12,
                    ["type"] = 30,
                    ["orderList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["move"] = new Dictionary<string, object>
                            {
                                ["idx"] = new List<object> { 8 },
                                ["from"] = 10,
                                ["to"] = 30,
                                ["isSelf"] = 1
                            }
                        }
                    }
                };
            Assert(discardSelectionTracker.PrepareOutgoingAction(
                    discardPlay, null, out _),
                "A non-burial discard target was not attached to PlayActions.");
            Dictionary<string, object> discardTarget =
                (Dictionary<string, object>)
                ((List<object>)discardPlay["targetList"])[0];
            Assert(Convert.ToInt32(discardPlay["type"]) == 31 &&
                Convert.ToInt32(discardTarget["targetIdx"]) == 8 &&
                ((List<int>)discardTarget["selectSkillIndex"])[0] == 6,
                "The selected discard target or skill index was not preserved.");

            P2PBattleSelectionTracker evolutionSelectionTracker =
                new P2PBattleSelectionTracker();
            evolutionSelectionTracker.RecordHandData(2,
                new List<object> { 0, true, false, "5" }, out _);
            evolutionSelectionTracker.RecordHandData(2,
                new List<object> { 7, true, false, "0004" }, out _);
            Dictionary<string, object> evolution = new Dictionary<string, object>
            {
                ["uri"] = "PlayActions",
                ["playIdx"] = 5,
                ["type"] = 20
            };
            Assert(evolutionSelectionTracker.PrepareOutgoingAction(
                    evolution, null, out _) &&
                Convert.ToInt32(evolution["type"]) == 21,
                "A repaired evolution selection was assigned the play-card action type.");

            P2PBattleCardTracker cachedDiscardTracker =
                new P2PBattleCardTracker();
            cachedDiscardTracker.Reset(new List<int>(), new List<int>());
            cachedDiscardTracker.RememberSourceCard(true, 8, 808, 3);
            cachedDiscardTracker.PrepareOutgoingAction(
                true, discardPlay, out _, out _);
            Dictionary<string, object> discardedKnownCard =
                (Dictionary<string, object>)
                ((List<object>)discardPlay["knownList"])[0];
            Assert(Convert.ToInt32(discardedKnownCard["cardId"]) == 808 &&
                Convert.ToInt32(discardedKnownCard["cost"]) == 3 &&
                Convert.ToInt32(discardedKnownCard["from"]) == 10 &&
                Convert.ToInt32(discardedKnownCard["to"]) == 30,
                "A discarded card could not be revealed from the pre-action cache.");

            P2PBattleCardTracker hiddenCostTracker =
                new P2PBattleCardTracker();
            hiddenCostTracker.Reset(new List<int>(), new List<int>());
            hiddenCostTracker.RememberSourceCard(true, 15, 1500, 7);
            Dictionary<string, object> hiddenCostChange =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["orderList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["alter"] = new Dictionary<string, object>
                            {
                                ["idx"] = new List<object> { 15 },
                                ["isSelf"] = 1,
                                ["type"] = "add",
                                ["cost"] = "a2"
                            }
                        }
                    }
                };
            hiddenCostTracker.PrepareOutgoingAction(true, hiddenCostChange,
                out _, out _);
            Dictionary<string, object> hiddenCostPlay =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["playIdx"] = 15
                };
            Assert(hiddenCostTracker.PrepareOutgoingAction(true, hiddenCostPlay,
                    out _, out _) &&
                Convert.ToInt32(((Dictionary<string, object>)
                    ((List<object>)hiddenCostPlay["knownList"])[0])["cost"]) == 9,
                "A hidden hand cost change was not retained for a later card reveal.");

            Dictionary<string, object> hiddenCostRemoval =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["orderList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["alter"] = new Dictionary<string, object>
                            {
                                ["idx"] = new List<object> { 15 },
                                ["isSelf"] = 1,
                                ["type"] = "del",
                                ["cost"] = "a2"
                            }
                        }
                    }
                };
            hiddenCostTracker.PrepareOutgoingAction(true, hiddenCostRemoval,
                out _, out _);
            Dictionary<string, object> hiddenCostPlayAfterRemoval =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["playIdx"] = 15
                };
            Assert(hiddenCostTracker.PrepareOutgoingAction(true,
                    hiddenCostPlayAfterRemoval, out _, out _) &&
                Convert.ToInt32(((Dictionary<string, object>)
                    ((List<object>)hiddenCostPlayAfterRemoval["knownList"])[0])["cost"]) == 7,
                "A removed hidden hand cost change was not reversed in the cache.");

            P2PBattleCardTracker tracker = new P2PBattleCardTracker();
            tracker.Reset(
                new List<int> { 101, 102, 103 },
                new List<int> { 201, 202, 203 });
            Dictionary<string, object> play = new Dictionary<string, object>
            {
                ["uri"] = "PlayActions",
                ["playIdx"] = 2,
                ["type"] = 30
            };
            Assert(tracker.PrepareOutgoingAction(true, play, out int index, out int cardId) &&
                index == 2 && cardId == 102,
                "A host deck card could not be revealed from its stable battle index.");
            List<object> known = (List<object>)play["knownList"];
            Dictionary<string, object> revealed = (Dictionary<string, object>)known[0];
            Assert(Convert.ToInt32(revealed["idx"]) == 2 &&
                Convert.ToInt32(revealed["cardId"]) == 102 &&
                Convert.ToInt32(revealed["isSelf"]) == 1,
                "The generated known-card entry has the wrong identity or perspective.");

            foreach (int mutationType in new[] { 2, 3 })
            {
                int originalCardId = mutationType == 2 ? 100001 : 200001;
                int mutationCardId = mutationType == 2 ? 100002 : 200002;
                int mutationCost = mutationType == 2 ? 2 : 1;
                P2PBattleCardTracker mutationTracker =
                    new P2PBattleCardTracker();
                mutationTracker.Reset(new List<int>(), new List<int>());
                mutationTracker.RememberSourceCard(
                    true, 42, originalCardId, 9);
                mutationTracker.RememberSourceCardMutation(
                    true,
                    42,
                    originalCardId,
                    9,
                    mutationCardId,
                    mutationCost,
                    mutationType);
                Dictionary<string, object> mutationPlay =
                    new Dictionary<string, object>
                    {
                        ["uri"] = "PlayActions",
                        ["playIdx"] = 42,
                        ["type"] = 30,
                        ["keyAction"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["type"] = mutationType,
                                ["cardId"] = originalCardId
                            }
                        },
                        ["orderList"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["metamorphose"] = new Dictionary<string, object>
                                {
                                    ["idx"] = new List<object> { 42 },
                                    ["isSelf"] = 1,
                                    ["after"] = new Dictionary<string, object>
                                    {
                                        ["cardId"] = originalCardId
                                    }
                                }
                            },
                            new Dictionary<string, object>
                            {
                                ["move"] = new Dictionary<string, object>
                                {
                                    ["idx"] = new List<object> { 43 },
                                    ["from"] = 10,
                                    ["to"] = 30,
                                    ["isSelf"] = 1
                                }
                            }
                        },
                        ["uList"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["idxList"] = new List<object> { 43 },
                                ["cardId"] = 300043,
                                ["cost"] = 3,
                                ["from"] = 10,
                                ["to"] = 30,
                                ["isSelf"] = 1,
                                ["skill"] = "43|1|0"
                            }
                        },
                        ["knownList"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["idx"] = 99,
                                ["cardId"] = 999999,
                                ["isSelf"] = 1
                            }
                        }
                    };

                Assert(mutationTracker.PrepareOutgoingAction(
                        true,
                        mutationPlay,
                        out int mutationPlayIndex,
                        out int revealedMutationCardId,
                        cardIndex => cardIndex == 42
                            ? originalCardId
                            : cardIndex == 43 ? 300043 : 0,
                        cardIndex => cardIndex == 42
                            ? 9
                            : cardIndex == 43 ? 3 : -1) &&
                    mutationPlayIndex == 42 &&
                    revealedMutationCardId == mutationCardId,
                    $"Mutation key action type {mutationType} did not reveal its changed card.");
                Assert(P2PBattleProtocol.TryReadPreparedAction(
                        mutationPlay, out int preparedIndex,
                        out int preparedCardId) && preparedIndex == 42 &&
                    preparedCardId == mutationCardId,
                    $"Mutation key action type {mutationType} did not retain its " +
                    "source-side prepared identity marker.");
                Dictionary<string, object> mutationKeyAction =
                    (Dictionary<string, object>)
                    ((List<object>)mutationPlay["keyAction"])[0];
                Dictionary<string, object> mutationKnownCard =
                    (Dictionary<string, object>)
                    ((List<object>)mutationPlay["knownList"])[0];
                Assert(Convert.ToInt32(mutationKeyAction["cardId"]) == originalCardId,
                    $"Mutation key action type {mutationType} lost the original card ID.");
                Assert(Convert.ToInt32(mutationKnownCard["idx"]) == 42 &&
                    Convert.ToInt32(mutationKnownCard["cardId"]) == mutationCardId &&
                    Convert.ToInt32(mutationKnownCard["cost"]) == 9,
                    $"Mutation key action type {mutationType} did not put its changed " +
                    "card ID and original card cost first in knownList.");
                List<string> mutationKeys = mutationPlay.Keys.ToList();
                int keyActionPosition = mutationKeys.IndexOf("keyAction");
                int knownListPosition = mutationKeys.IndexOf("knownList");
                Assert(knownListPosition == keyActionPosition + 1 &&
                    knownListPosition < mutationKeys.IndexOf("orderList") &&
                    knownListPosition < mutationKeys.IndexOf("uList"),
                    $"Mutation key action type {mutationType} did not place knownList " +
                    "immediately after keyAction and before card-bearing action lists.");
                Dictionary<string, object> discardedEffectCard =
                    ((List<object>)mutationPlay["knownList"])
                    .Cast<Dictionary<string, object>>()
                    .Single(card => Convert.ToInt32(card["idx"]) == 43);
                Assert(Convert.ToInt32(discardedEffectCard["cardId"]) == 300043,
                    $"Mutation key action type {mutationType} overwrote the identity " +
                    "of a card with an on-discard effect.");

                Assert(mutationTracker.PrepareOutgoingAction(
                        true, mutationPlay, out _, out revealedMutationCardId) &&
                    revealedMutationCardId == mutationCardId &&
                    Convert.ToInt32(((Dictionary<string, object>)
                        ((List<object>)mutationPlay["knownList"])[0])["cardId"]) ==
                        mutationCardId,
                    $"Mutation key action type {mutationType} was overwritten during " +
                    "the host's second outgoing preparation.");
                mutationKeys = mutationPlay.Keys.ToList();
                Assert(mutationKeys.IndexOf("knownList") ==
                        mutationKeys.IndexOf("keyAction") + 1,
                    $"Mutation key action type {mutationType} lost its field ordering " +
                    "during the host's second outgoing preparation.");

                P2PBattleCardTracker relayTracker =
                    new P2PBattleCardTracker();
                relayTracker.Reset(new List<int>(), new List<int>());
                Assert(relayTracker.PrepareOutgoingAction(
                        true, mutationPlay, out _, out revealedMutationCardId) &&
                    revealedMutationCardId == mutationCardId,
                    $"Mutation key action type {mutationType} was overwritten while " +
                    "being relayed by the remote host.");

                Dictionary<string, object> receivedMutation =
                    P2PMessageTransform.PrepareOpponentBattleMessage(mutationPlay);
                Dictionary<string, object> receivedMutationKeyAction =
                    (Dictionary<string, object>)
                    ((List<object>)receivedMutation["keyAction"])[0];
                Dictionary<string, object> receivedMutationKnownCard =
                    (Dictionary<string, object>)
                    ((List<object>)receivedMutation["knownList"])[0];
                List<string> receivedMutationKeys = receivedMutation.Keys.ToList();
                Assert(Convert.ToInt32(receivedMutationKeyAction["cardId"]) ==
                        originalCardId &&
                    Convert.ToInt32(receivedMutationKnownCard["cardId"]) ==
                        mutationCardId &&
                    Convert.ToInt32(receivedMutationKnownCard["cost"]) ==
                        9 &&
                    Convert.ToInt32(receivedMutationKnownCard["isSelf"]) == 0,
                    $"Mutation key action type {mutationType} lost its mutation data " +
                    "or perspective on the receiving client.");
                Assert(receivedMutationKeys.IndexOf("knownList") ==
                        receivedMutationKeys.IndexOf("keyAction") + 1 &&
                    receivedMutationKeys.IndexOf("knownList") <
                        receivedMutationKeys.IndexOf("uList"),
                    $"Mutation key action type {mutationType} lost its field ordering " +
                    "during the receiving-client perspective transform.");
            }

            Dictionary<string, object> tokenCreation = new Dictionary<string, object>
            {
                ["orderList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["add"] = new Dictionary<string, object>
                        {
                            ["idx"] = new List<object> { 41 },
                            ["isSelf"] = 1,
                            ["card"] = new Dictionary<string, object> { ["cardId"] = 999 }
                        }
                    }
                }
            };
            tracker.PrepareOutgoingAction(true, tokenCreation, out _, out _);
            Dictionary<string, object> tokenPlay = new Dictionary<string, object>
            {
                ["playIdx"] = 41,
                ["type"] = 30
            };
            Assert(tracker.PrepareOutgoingAction(true, tokenPlay, out index, out cardId) &&
                cardId == 999,
                "A generated hand card was not retained for a later play action.");

            Dictionary<string, object> echoTokenCreation = new Dictionary<string, object>
            {
                ["uri"] = "Echo",
                ["orderList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["add"] = new Dictionary<string, object>
                        {
                            ["idx"] = new List<object> { 42 },
                            ["isSelf"] = 0,
                            ["card"] = new Dictionary<string, object> { ["cardId"] = 1000 }
                        }
                    }
                }
            };
            tracker.PrepareOutgoingAction(false, echoTokenCreation, out _, out _);
            Dictionary<string, object> echoedTokenPlay = new Dictionary<string, object>
            {
                ["playIdx"] = 42,
                ["type"] = 30
            };
            Assert(tracker.PrepareOutgoingAction(true, echoedTokenPlay, out index, out cardId) &&
                cardId == 1000,
                "A generated card reported by the opponent's Echo was not retained.");

            Dictionary<string, object> guestPlay = new Dictionary<string, object>
            {
                ["playIdx"] = 1,
                ["type"] = 30,
                ["knownList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["idx"] = 1,
                        ["cardId"] = 0,
                        ["isSelf"] = 1
                    }
                }
            };
            Assert(tracker.PrepareOutgoingAction(
                    false,
                    guestPlay,
                    out index,
                    out cardId,
                    guestIndex => guestIndex == 1 ? 201 : 0,
                    guestIndex => guestIndex == 1 ? 2 : -1) &&
                cardId == 201,
                "A guest deck card could not be revealed.");
            known = (List<object>)guestPlay["knownList"];
            Assert(known.Count == 1 &&
                Convert.ToInt32(((Dictionary<string, object>)known[0])["cardId"]) == 201 &&
                Convert.ToInt32(((Dictionary<string, object>)known[0])["cost"]) == 2,
                "An existing known-card entry was duplicated instead of completed.");

            Dictionary<string, object> castleSummon = new Dictionary<string, object>
            {
                ["uList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["idxList"] = new List<object> { 3 },
                        ["from"] = 0,
                        ["to"] = 20,
                        ["isSelf"] = 1
                    }
                },
                ["orderList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["move"] = new Dictionary<string, object>
                        {
                            ["idx"] = new List<object> { 3 },
                            ["from"] = 0,
                            ["to"] = 20,
                            ["isSelf"] = 1
                        }
                    }
                }
            };
            tracker.PrepareOutgoingAction(true, castleSummon, out _, out _);
            known = (List<object>)castleSummon["knownList"];
            Assert(known.Count == 1,
                "A deck-to-field move was revealed more than once.");
            Dictionary<string, object> summoned = (Dictionary<string, object>)known[0];
            Assert(Convert.ToInt32(summoned["idx"]) == 3 &&
                Convert.ToInt32(summoned["cardId"]) == 103 &&
                Convert.ToInt32(summoned["isSelf"]) == 1 &&
                Convert.ToInt32(summoned["is_open"]) == 1,
                "A follower summoned from the deck was not revealed correctly.");

            Dictionary<string, object> receivedCastleSummon =
                P2PMessageTransform.PrepareOpponentBattleMessage(castleSummon);
            Dictionary<string, object> receivedSummoned = (Dictionary<string, object>)
                ((List<object>)receivedCastleSummon["knownList"])[0];
            Assert(Convert.ToInt32(receivedSummoned["isSelf"]) == 0,
                "A revealed summoned follower has the wrong receiver perspective.");

            Dictionary<string, object> summonedAttack = new Dictionary<string, object>
            {
                ["playIdx"] = 3,
                ["type"] = 10,
                ["targetList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["targetIdx"] = 0,
                        ["isSelf"] = 0
                    }
                }
            };
            Assert(tracker.PrepareOutgoingAction(true, summonedAttack,
                    out index, out cardId) && cardId == 103,
                "A follower summoned from the deck was not retained as an attacker.");

            Dictionary<string, object> revealHandResident =
                new Dictionary<string, object>
            {
                ["uri"] = "TurnEndActions",
                ["orderList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["openMyCards"] = new Dictionary<string, object>
                        {
                            ["idx"] = new List<object> { 2 }
                        }
                    }
                }
            };
            tracker.PrepareOutgoingAction(true, revealHandResident, out _, out _);
            known = (List<object>)revealHandResident["knownList"];
            Assert(known.Count == 1,
                "An opened hand-resident card was not added to the known-card list.");
            Dictionary<string, object> openedHandCard =
                (Dictionary<string, object>)known[0];
            Assert(Convert.ToInt32(openedHandCard["idx"]) == 2 &&
                Convert.ToInt32(openedHandCard["cardId"]) == 102 &&
                Convert.ToInt32(openedHandCard["isSelf"]) == 1 &&
                Convert.ToInt32(openedHandCard["is_open"]) == 1,
                "An opened hand-resident card has the wrong identity or visibility.");

            Dictionary<string, object> receivedHandResident =
                P2PMessageTransform.PrepareOpponentBattleMessage(revealHandResident);
            Dictionary<string, object> receivedOpenedHandCard =
                (Dictionary<string, object>)
                ((List<object>)receivedHandResident["knownList"])[0];
            Assert(Convert.ToInt32(receivedOpenedHandCard["isSelf"]) == 0 &&
                Convert.ToInt32(receivedOpenedHandCard["is_open"]) == 1,
                "An opened hand-resident card has the wrong receiver perspective.");
            Dictionary<string, object> receivedOpenOrder =
                (Dictionary<string, object>)
                ((List<object>)receivedHandResident["orderList"])[0];
            Dictionary<string, object> receivedOpenData =
                (Dictionary<string, object>)receivedOpenOrder["openMyCards"];
            Assert(!receivedOpenData.ContainsKey("isSelf") &&
                ((List<object>)receivedOpenData["idx"]).Count == 1,
                "Opening a hand-resident card changed its order-list payload.");

            Dictionary<string, object> ordinaryDraw = new Dictionary<string, object>
            {
                ["uri"] = "TurnEndActions",
                ["orderList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["move"] = new Dictionary<string, object>
                        {
                            ["idx"] = new List<object> { 1 },
                            ["from"] = 0,
                            ["to"] = 10,
                            ["isSelf"] = 1
                        }
                    }
                }
            };
            tracker.PrepareOutgoingAction(true, ordinaryDraw, out _, out _,
                drawIndex => drawIndex == 1 ? 101 : 0);
            known = (List<object>)ordinaryDraw["knownList"];
            Assert(known.Count == 1,
                "An ordinary deck draw did not synchronize its private identity.");
            Dictionary<string, object> privateDrawCard =
                (Dictionary<string, object>)known[0];
            Assert(Convert.ToInt32(privateDrawCard["cardId"]) == 101 &&
                Convert.ToInt32(privateDrawCard["is_open"]) == 0 &&
                Convert.ToInt32(privateDrawCard["from"]) == 0 &&
                Convert.ToInt32(privateDrawCard["to"]) == 10,
                "An ordinary draw was exposed visually or lost its real identity.");

            Dictionary<string, object> receivedPrivateDraw =
                P2PMessageTransform.PrepareOpponentBattleMessage(ordinaryDraw);
            Dictionary<string, object> receivedPrivateDrawCard =
                (Dictionary<string, object>)
                ((List<object>)receivedPrivateDraw["knownList"])[0];
            Assert(Convert.ToInt32(receivedPrivateDrawCard["isSelf"]) == 0 &&
                Convert.ToInt32(receivedPrivateDrawCard["is_open"]) == 0,
                "A private draw has the wrong receiver perspective or visibility.");

            Dictionary<string, object> generatedHandCard =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["orderList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["add"] = new Dictionary<string, object>
                            {
                                ["idx"] = new List<object> { 44 },
                                ["isSelf"] = 1,
                                ["card"] = new Dictionary<string, object>
                                {
                                    ["cardId"] = 888
                                }
                            }
                        },
                        new Dictionary<string, object>
                        {
                            ["move"] = new Dictionary<string, object>
                            {
                                ["idx"] = new List<object> { 44 },
                                ["from"] = 50,
                                ["to"] = 10,
                                ["isSelf"] = 1
                            }
                        }
                    }
                };
            tracker.PrepareOutgoingAction(true, generatedHandCard, out _, out _);
            Dictionary<string, object> generatedKnown =
                (Dictionary<string, object>)
                ((List<object>)generatedHandCard["knownList"])[0];
            Assert(Convert.ToInt32(generatedKnown["idx"]) == 44 &&
                Convert.ToInt32(generatedKnown["cardId"]) == 888 &&
                Convert.ToInt32(generatedKnown["is_open"]) == 0 &&
                Convert.ToInt32(generatedKnown["from"]) == 50 &&
                Convert.ToInt32(generatedKnown["to"]) == 10,
                "A generated private-zone card lost its identity or became public.");

            Dictionary<string, object> returnedHandCard =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["orderList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["move"] = new Dictionary<string, object>
                            {
                                ["idx"] = new List<object> { 3 },
                                ["from"] = 20,
                                ["to"] = 10,
                                ["isSelf"] = 1
                            }
                        }
                    }
                };
            tracker.PrepareOutgoingAction(true, returnedHandCard, out _, out _,
                movedIndex => movedIndex == 3 ? 103 : 0);
            Dictionary<string, object> returnedKnown =
                (Dictionary<string, object>)
                ((List<object>)returnedHandCard["knownList"])[0];
            Assert(Convert.ToInt32(returnedKnown["cardId"]) == 103 &&
                Convert.ToInt32(returnedKnown["is_open"]) == 0 &&
                Convert.ToInt32(returnedKnown["from"]) == 20 &&
                Convert.ToInt32(returnedKnown["to"]) == 10,
                "A card returned to hand lost its private identity.");

            Dictionary<string, object> openDraw = new Dictionary<string, object>
            {
                ["uri"] = "TurnEndActions",
                ["orderList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["move"] = new Dictionary<string, object>
                        {
                            ["idx"] = new List<object> { 1, 2 },
                            ["from"] = 0,
                            ["to"] = 10,
                            ["isSelf"] = 1,
                            ["is_open"] = new List<object> { 1 }
                        }
                    }
                }
            };
            tracker.PrepareOutgoingAction(true, openDraw, out _, out _,
                drawIndex => drawIndex == 2 ? 777 : 101,
                drawIndex => drawIndex == 2 ? 7 : 1);
            known = (List<object>)openDraw["knownList"];
            Assert(known.Count == 2,
                "A mixed private/open draw did not synchronize every card identity.");
            Dictionary<string, object> hiddenDrawCard =
                (Dictionary<string, object>)known[0];
            Dictionary<string, object> openedDrawCard =
                (Dictionary<string, object>)known[1];
            Assert(Convert.ToInt32(hiddenDrawCard["idx"]) == 1 &&
                Convert.ToInt32(hiddenDrawCard["cardId"]) == 101 &&
                Convert.ToInt32(hiddenDrawCard["is_open"]) == 0,
                "The private card in a mixed draw was exposed or lost its identity.");
            Assert(Convert.ToInt32(openedDrawCard["idx"]) == 2 &&
                Convert.ToInt32(openedDrawCard["cardId"]) == 777 &&
                Convert.ToInt32(openedDrawCard["isSelf"]) == 1 &&
                Convert.ToInt32(openedDrawCard["is_open"]) == 1 &&
                Convert.ToInt32(openedDrawCard["cost"]) == 7 &&
                Convert.ToInt32(openedDrawCard["from"]) == 0 &&
                Convert.ToInt32(openedDrawCard["to"]) == 10,
                "An open draw did not preserve its current identity, cost, or movement.");

            Dictionary<string, object> receivedOpenDraw =
                P2PMessageTransform.PrepareOpponentBattleMessage(openDraw);
            Dictionary<string, object> receivedOpenDrawCard =
                (Dictionary<string, object>)
                ((List<object>)receivedOpenDraw["knownList"])[1];
            Assert(Convert.ToInt32(receivedOpenDrawCard["isSelf"]) == 0 &&
                Convert.ToInt32(receivedOpenDrawCard["cardId"]) == 777 &&
                Convert.ToInt32(receivedOpenDrawCard["cost"]) == 7,
                "An open draw has the wrong identity, cost, or receiver perspective.");

            Dictionary<string, object> burialRite = new Dictionary<string, object>
            {
                ["targetList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["targetIdx"] = 3,
                        ["isSelf"] = 1,
                        ["skillIndex"] = new List<object> { 4 }
                    }
                },
                ["orderList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["move"] = new Dictionary<string, object>
                        {
                            ["idx"] = new List<object> { 3 },
                            ["from"] = 10,
                            ["to"] = 20,
                            ["isSelf"] = 1
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["move"] = new Dictionary<string, object>
                        {
                            ["idx"] = new List<object> { 3 },
                            ["from"] = 20,
                            ["to"] = 30,
                            ["isSelf"] = 1
                        }
                    }
                }
            };
            tracker.PrepareOutgoingAction(true, burialRite, out _, out _,
                burialIndex => burialIndex == 3 ? 103 : 0,
                burialIndex => burialIndex == 3 ? 4 : -1);
            Dictionary<string, object> burialCard = (Dictionary<string, object>)
                ((List<object>)burialRite["knownList"])[0];
            Assert(Convert.ToInt32(burialCard["cardId"]) == 103 &&
                Convert.ToInt32(burialCard["cost"]) == 4 &&
                Convert.ToInt32(burialCard["from"]) == 10 &&
                Convert.ToInt32(burialCard["to"]) == 20,
                "A burial-rite target was not revealed before its field transition.");

            Dictionary<string, object> receivedBurial =
                P2PMessageTransform.PrepareOpponentBattleMessage(burialRite);
            Dictionary<string, object> receivedBurialTarget =
                (Dictionary<string, object>)
                ((List<object>)receivedBurial["oppoTargetList"])[0];
            Dictionary<string, object> receivedBurialCard =
                (Dictionary<string, object>)
                ((List<object>)receivedBurial["knownList"])[0];
            Assert(Convert.ToInt32(receivedBurialTarget["isSelf"]) == 1 &&
                Convert.ToInt32(receivedBurialCard["isSelf"]) == 0,
                "Burial-rite target and known-card perspectives were conflated.");

            Dictionary<string, object> fusion = new Dictionary<string, object>
            {
                ["orderList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["fusion"] = new Dictionary<string, object>
                        {
                            ["idx"] = new List<object> { 2 },
                            ["ingredients"] = new List<object> { 1, 3 },
                            ["isSelf"] = 1
                        }
                    }
                }
            };
            tracker.PrepareOutgoingAction(true, fusion, out _, out _,
                fusionIndex => fusionIndex == 1 ? 101 :
                    fusionIndex == 3 ? 103 : 0,
                fusionIndex => fusionIndex == 1 ? 1 :
                    fusionIndex == 3 ? 5 : -1,
                null,
                fusionIndex => fusionIndex == 2
                    ? new[]
                    {
                        new P2PFusionIngredientState(9, 109, 1),
                        new P2PFusionIngredientState(1, 101, 2),
                        new P2PFusionIngredientState(3, 103, 2)
                    }
                    : Enumerable.Empty<P2PFusionIngredientState>());
            List<object> fusionKnown = (List<object>)fusion["knownList"];
            Assert(fusionKnown.Count == 2,
                "Fusion did not reveal every consumed ingredient.");
            Dictionary<string, object> firstIngredient =
                (Dictionary<string, object>)fusionKnown[0];
            Dictionary<string, object> secondIngredient =
                (Dictionary<string, object>)fusionKnown[1];
            Assert(Convert.ToInt32(firstIngredient["cardId"]) == 101 &&
                Convert.ToInt32(firstIngredient["cost"]) == 1 &&
                Convert.ToInt32(firstIngredient["from"]) == 10 &&
                Convert.ToInt32(firstIngredient["to"]) == 60 &&
                Convert.ToInt32(secondIngredient["cardId"]) == 103 &&
                Convert.ToInt32(secondIngredient["cost"]) == 5,
                "Fusion ingredients retained dummy identities or stale costs.");

            List<object> fusionActions = (List<object>)fusion["p2pFusionActions"];
            Assert(fusionActions.Count == 1,
                "Fusion did not publish its target and ingredient metadata.");
            Dictionary<string, object> fusionAction =
                (Dictionary<string, object>)fusionActions[0];
            Assert(Convert.ToInt32(fusionAction["owner"]) == 1 &&
                Convert.ToInt32(fusionAction["targetIdx"]) == 2 &&
                ((List<object>)fusionAction["ingredients"]).Count == 2 &&
                ((List<object>)fusionAction["beforeIngredients"]).Count == 1 &&
                ((List<object>)fusionAction["afterIngredients"]).Count == 3,
                "Fusion metadata lost its absolute owner, target, or ingredients.");
            Dictionary<string, object> previousFusionIngredient =
                (Dictionary<string, object>)((List<object>)
                    fusionAction["beforeIngredients"])[0];
            Assert(Convert.ToInt32(previousFusionIngredient["idx"]) == 9 &&
                Convert.ToInt32(previousFusionIngredient["turn"]) == 1,
                "Fusion metadata did not preserve the pre-action cumulative state.");

            Dictionary<string, object> receivedFusion =
                P2PMessageTransform.PrepareOpponentBattleMessage(fusion);
            Dictionary<string, object> receivedFusionAction =
                (Dictionary<string, object>)((List<object>)
                    receivedFusion["p2pFusionActions"])[0];
            Assert(Convert.ToInt32(receivedFusionAction["owner"]) == 1 &&
                Convert.ToInt32(receivedFusionAction["targetIdx"]) == 2 &&
                ((List<object>)receivedFusionAction["beforeIngredients"]).Count == 1 &&
                ((List<object>)receivedFusionAction["afterIngredients"]).Count == 3,
                "Fusion metadata ownership was incorrectly perspective-flipped.");

            P2PBattleCardTracker fusionMetamorphoseTracker =
                new P2PBattleCardTracker();
            fusionMetamorphoseTracker.Reset(new List<int>(), new List<int>());
            fusionMetamorphoseTracker.RememberSourceCard(
                true, 12, 130234011, 2);
            fusionMetamorphoseTracker.RememberSourceCard(
                true, 13, 130001001, 1);
            Dictionary<string, object> fusionMetamorphose =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["type"] = 40,
                    ["playIdx"] = 12,
                    ["knownList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["idx"] = 12,
                            ["cardId"] = 130234011,
                            ["cost"] = 2,
                            ["isSelf"] = 1,
                            ["is_open"] = 1
                        }
                    },
                    ["orderList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["fusion"] = new Dictionary<string, object>
                            {
                                ["idx"] = new List<object> { 12 },
                                ["ingredients"] = new List<object> { 13 },
                                ["isSelf"] = 1
                            }
                        },
                        new Dictionary<string, object>
                        {
                            ["metamorphose"] = new Dictionary<string, object>
                            {
                                ["idx"] = new List<object> { 12 },
                                ["isSelf"] = 1,
                                ["isFusion"] = 1,
                                ["after"] = new Dictionary<string, object>
                                {
                                    ["cardId"] = 920234011
                                }
                            }
                        }
                    },
                    [P2PBattleProtocol.FusionMetamorphoseOriginalsKey] =
                        new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["owner"] = 1,
                                ["idx"] = 12,
                                ["cardId"] = 130234011,
                                ["cost"] = 2
                            }
                        },
                    ["p2pHiddenCards"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["idx"] = 12,
                            ["cardId"] = 920234011,
                            ["cost"] = 1
                        }
                    },
                    ["p2pHiddenOwner"] = 1
                };
            Assert(fusionMetamorphoseTracker.PrepareOutgoingAction(
                    true, fusionMetamorphose,
                    out int fusionMetamorphoseIndex,
                    out int fusionMetamorphoseCardId,
                    index => index == 12 ? 920234011 :
                        index == 13 ? 130001001 : 0,
                    index => index == 12 || index == 13 ? 1 : -1),
                "A fusion-metamorphose action was not prepared.");
            Dictionary<string, object> originalFusionCard =
                ((List<object>)fusionMetamorphose["knownList"])
                .OfType<Dictionary<string, object>>()
                .Single(card => Convert.ToInt32(card["idx"]) == 12);
            Dictionary<string, object> transformedFusionCard =
                (Dictionary<string, object>)((List<object>)
                    fusionMetamorphose["p2pHiddenCards"])[0];
            Dictionary<string, object> fusionMetamorphoseResult =
                (Dictionary<string, object>)((List<object>)
                    fusionMetamorphose["p2pMetamorphoses"])[0];
            Assert(fusionMetamorphoseIndex == 12 &&
                fusionMetamorphoseCardId == 130234011 &&
                Convert.ToInt32(originalFusionCard["cardId"]) == 130234011 &&
                Convert.ToInt32(originalFusionCard["cost"]) == 2,
                "Fusion metamorphose exposed the transformed identity before " +
                "the native fusion skill executed.");
            Assert(Convert.ToInt32(transformedFusionCard["cardId"]) == 920234011 &&
                Convert.ToInt32(fusionMetamorphoseResult["cardId"]) == 920234011,
                "Fusion metamorphose lost its post-action transformed identity.");

            Dictionary<string, object> receivedFusionMetamorphose =
                P2PMessageTransform.PrepareOpponentBattleMessage(
                    fusionMetamorphose);
            Dictionary<string, object> receivedOriginalFusionCard =
                ((List<object>)receivedFusionMetamorphose["knownList"])
                .OfType<Dictionary<string, object>>()
                .Single(card => Convert.ToInt32(card["idx"]) == 12);
            Dictionary<string, object> receivedFusionOriginalMetadata =
                (Dictionary<string, object>)((List<object>)
                    receivedFusionMetamorphose[
                        P2PBattleProtocol.FusionMetamorphoseOriginalsKey])[0];
            Assert(Convert.ToInt32(receivedOriginalFusionCard["isSelf"]) == 0 &&
                Convert.ToInt32(receivedOriginalFusionCard["cardId"]) == 130234011 &&
                Convert.ToInt32(receivedFusionOriginalMetadata["owner"]) == 1 &&
                Convert.ToInt32(receivedFusionOriginalMetadata["cardId"]) == 130234011,
                "Fusion metamorphose pre-action identity was corrupted while " +
                "converting the receiver perspective.");

            Dictionary<string, object> ordinaryMetamorphose =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["playIdx"] = 51,
                    ["orderList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["metamorphose"] = new Dictionary<string, object>
                            {
                                ["idx"] = new List<object> { 52 },
                                ["isSelf"] = 1,
                                ["after"] = new Dictionary<string, object>
                                {
                                    ["cardId"] = 900052
                                }
                            }
                        }
                    }
                };
            fusionMetamorphoseTracker.PrepareOutgoingAction(
                true, ordinaryMetamorphose, out _, out _,
                index => index == 51 ? 900051 :
                    index == 52 ? 900052 : 0,
                index => 3);
            Dictionary<string, object> ordinaryMetamorphosedCard =
                ((List<object>)ordinaryMetamorphose["knownList"])
                .OfType<Dictionary<string, object>>()
                .Single(card => Convert.ToInt32(card["idx"]) == 52);
            Assert(Convert.ToInt32(
                    ordinaryMetamorphosedCard["cardId"]) == 900052,
                "The fusion-specific identity delay affected an ordinary " +
                "metamorphose action.");

            Dictionary<string, object> randomTargetMessage = new Dictionary<string, object>
            {
                ["p2pAuthoritativeSkillTargets"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["owner"] = 1,
                        ["ownerIdx"] = 2,
                        ["targets"] = new List<object>(),
                        ["independent"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["slot"] = 0,
                                ["card"] = new Dictionary<string, object>
                                {
                                    ["owner"] = 0,
                                    ["idx"] = 77,
                                    ["cardId"] = 1077
                                }
                            }
                        }
                    }
                }
            };
            Dictionary<string, object> flippedRandomTargetMessage =
                P2PMessageTransform.PrepareOpponentBattleMessage(randomTargetMessage);
            Dictionary<string, object> flippedRandomTarget =
                (Dictionary<string, object>)((List<object>)
                    flippedRandomTargetMessage["p2pAuthoritativeSkillTargets"])[0];
            Dictionary<string, object> flippedIndependent =
                (Dictionary<string, object>)((List<object>)
                    flippedRandomTarget["independent"])[0];
            Dictionary<string, object> flippedIndependentCard =
                (Dictionary<string, object>)flippedIndependent["card"];
            Assert(Convert.ToInt32(flippedRandomTarget["owner"]) == 1 &&
                Convert.ToInt32(flippedIndependentCard["owner"]) == 0,
                "Independent authoritative target ownership was perspective-flipped.");

            List<P2PFusionIngredientState> cumulativeFusion =
                new List<P2PFusionIngredientState>();
            for (int fusionNumber = 1; fusionNumber <= 3; fusionNumber++)
            {
                int ingredientIndex = 10 + fusionNumber;
                cumulativeFusion.Add(new P2PFusionIngredientState(
                    ingredientIndex, 100 + ingredientIndex, fusionNumber));
                Dictionary<string, object> repeatedFusion =
                    new Dictionary<string, object>
                    {
                        ["orderList"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["fusion"] = new Dictionary<string, object>
                                {
                                    ["idx"] = new List<object> { 50 },
                                    ["ingredients"] = new List<object>
                                    {
                                        ingredientIndex
                                    },
                                    ["isSelf"] = 1
                                }
                            }
                        }
                    };
                tracker.PrepareOutgoingAction(
                    true, repeatedFusion, out _, out _,
                    index => 100 + index,
                    index => 1,
                    null,
                    index => index == 50
                        ? cumulativeFusion.ToList()
                        : Enumerable.Empty<P2PFusionIngredientState>());
                Dictionary<string, object> repeatedAction =
                    (Dictionary<string, object>)((List<object>)
                        repeatedFusion["p2pFusionActions"])[0];
                Assert(((List<object>)repeatedAction["beforeIngredients"]).Count ==
                        fusionNumber - 1 &&
                    ((List<object>)repeatedAction["afterIngredients"]).Count ==
                        fusionNumber,
                    $"Fusion #{fusionNumber} did not preserve its N-1/N " +
                    "cumulative-state boundary.");
            }
        }

        private static void TestHostAuthoritativeServer()
        {
            P2PAuthoritativeServer.Reset();
            int stored = P2PAuthoritativeServer.RememberPrivateStateSnapshot(
                new Dictionary<string, object>
                {
                    ["owner"] = 0,
                    ["cards"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["idx"] = 7,
                            ["cardId"] = 777001,
                            ["cost"] = 3
                        }
                    }
                });
            Assert(stored == 1,
                "The Host did not retain a Guest private-card baseline.");

            Dictionary<string, object> request = new Dictionary<string, object>
            {
                ["uri"] = "PlayActions",
                ["playIdx"] = 7,
                ["type"] = 30,
                ["targetList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["targetIdx"] = 8,
                        ["isSelf"] = 0
                    }
                },
                ["orderList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["move"] = new Dictionary<string, object>
                        {
                            ["idx"] = 7,
                            ["isSelf"] = 1,
                            ["from"] = 10,
                            ["to"] = 20,
                            ["setAtk"] = 4,
                            ["spellboost"] = 2,
                            ["attachTarget"] = "1,3",
                            ["skill"] = "11|17|2"
                        },
                        ["activate"] = 1,
                        ["count"] = 2,
                        ["param"] = 9
                    }
                },
                ["uList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["idxList"] = new List<object> { 7 },
                        ["from"] = 10,
                        ["to"] = 30,
                        ["isSelf"] = 1
                    }
                }
            };

            Assert(P2PAuthoritativeServer.TryCreateAction(
                    false, 200, request, out P2PHostAuthoritativeAction action,
                    out string error),
                "The Host rejected a valid native PlayActions request: " + error);
            Assert(action.ServerActionId == 1 &&
                action.Request.Data.ContainsKey("targetList") &&
                !action.Request.Data.ContainsKey("oppoTargetList"),
                "The request object was mutated into a server response.");
            Assert(action.ServerResponse.ContainsKey("oppoTargetList") &&
                !action.ServerResponse.ContainsKey("targetList") &&
                Convert.ToInt32(action.ServerResponse[
                    P2PAuthoritativeServer.ServerActionIdKey]) == 1 &&
                Convert.ToInt32(action.ServerResponse[
                    P2PAuthoritativeServer.ServerStateRevisionKey]) == 1,
                "The Host did not create the required opponent response envelope.");
            int firstActionRevision = P2PAuthoritativeServer.StateRevision;

            Dictionary<string, object> conditionRequest =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["playIdx"] = 7,
                    ["type"] = 30,
                    [P2PBattleProtocol.ActionManifestKey] =
                        new Dictionary<string, object>
                        {
                            ["p2pAuthoritativeSkillEvaluations"] =
                                new List<object> { new Dictionary<string, object>() }
                        },
                    ["p2pAuthoritativeSkillEvaluations"] =
                        new List<object> { new Dictionary<string, object>() },
                    ["p2pAuthoritativeSkillTargets"] =
                        new List<object> { new Dictionary<string, object>() },
                    ["orderList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["skillConditionCheck"] =
                                new Dictionary<string, object>
                                {
                                    ["idx"] = 7,
                                    ["isSelf"] = 1,
                                    ["activate"] = 1,
                                    ["count"] = 3
                                }
                        },
                        new Dictionary<string, object>
                        {
                            ["move"] = new Dictionary<string, object>
                            {
                                ["idx"] = 7,
                                ["isSelf"] = 1,
                                ["from"] = 10,
                                ["to"] = 20
                            }
                        }
                    }
                };
            Assert(P2PAuthoritativeServer.TryCreateAction(
                    false, 200, conditionRequest,
                    out P2PHostAuthoritativeAction conditionAction,
                    out error),
                "The Host rejected a condition-bearing native request: " + error);
            List<object> conditionResponseOrders =
                conditionAction.ServerResponse["orderList"] as List<object>;
            Assert(conditionResponseOrders != null &&
                !conditionResponseOrders.OfType<Dictionary<string, object>>()
                    .Any(entry => entry.ContainsKey("skillConditionCheck")) &&
                conditionResponseOrders.OfType<Dictionary<string, object>>()
                    .Any(entry => entry.ContainsKey("move")) &&
                conditionRequest["orderList"] is List<object> originalConditionOrders &&
                originalConditionOrders.OfType<Dictionary<string, object>>()
                    .Any(entry => entry.ContainsKey("skillConditionCheck")),
                "The Host did not isolate client private condition results while " +
                "preserving the original request object and native movement data.");
            Assert(!conditionAction.ServerResponse.ContainsKey(
                        P2PBattleProtocol.ActionManifestKey) &&
                    !conditionAction.ServerResponse.ContainsKey(
                        "p2pAuthoritativeSkillEvaluations") &&
                    !conditionAction.ServerResponse.ContainsKey(
                        "p2pAuthoritativeSkillTargets") &&
                conditionRequest.ContainsKey(P2PBattleProtocol.ActionManifestKey),
                "The Host did not isolate compatibility evaluation manifests from " +
                "the native request boundary.");

            List<object> responseOrders = action.ServerResponse["orderList"]
                as List<object>;
            Dictionary<string, object> responseOrder = responseOrders?
                .OfType<Dictionary<string, object>>().SingleOrDefault();
            Assert(responseOrder != null &&
                Convert.ToInt32(responseOrder["activate"]) == 1 &&
                Convert.ToInt32(responseOrder["count"]) == 2 &&
                Convert.ToInt32(responseOrder["param"]) == 9,
                "The Host response lost native condition/validation fields.");

            List<object> known = action.ServerResponse["knownList"] as List<object>;
            Dictionary<string, object> knownCard = known?
                .OfType<Dictionary<string, object>>()
                .SingleOrDefault(card => Convert.ToInt32(card["idx"]) == 7);
            Assert(knownCard != null &&
                Convert.ToInt32(knownCard["cardId"]) == 777001 &&
                Convert.ToInt32(knownCard["isSelf"]) == 0 &&
                Convert.ToInt32(knownCard["cost"]) == 3 &&
                Convert.ToInt32(knownCard["setAtk"]) == 4 &&
                Convert.ToInt32(knownCard["spellboost"]) == 2 &&
                knownCard["attachTarget"].ToString() == "1,3",
                "The Host did not inject cached identity and native card state into knownList.");
            Dictionary<string, object> responseUnapproved =
                (action.ServerResponse["uList"] as List<object>)?
                    .OfType<Dictionary<string, object>>()
                    .SingleOrDefault();
            Assert(responseUnapproved != null &&
                responseUnapproved["cardId"].ToString() == "777001" &&
                responseUnapproved["cost"].ToString() == "3" &&
                responseUnapproved["skill"].ToString() == "11|17|2" &&
                responseUnapproved["attachTarget"].ToString() == "1,3",
                "The Host did not enrich missing RegisterUnapproved fields from its ledger.");
            Assert(P2PAuthoritativeServer.TryGetCardZone(0, 7, out int zone) &&
                    zone == 20 && Convert.ToInt32(action.ServerResponse[
                        P2PAuthoritativeServer.ServerStateRevisionKey]) ==
                        firstActionRevision,
                "The Host did not update the authoritative card zone/revision: zone=" +
                zone + ", revision=" + P2PAuthoritativeServer.StateRevision);
            IReadOnlyList<Dictionary<string, object>> history =
                P2PAuthoritativeServer.GetActionHistorySnapshot();
            Assert(history.Any(entry => Convert.ToInt32(entry["owner"]) == 0 &&
                    Convert.ToInt32(entry["idx"]) == 7 &&
                    Convert.ToInt32(entry["to"]) == 20),
                "The Host did not record the native card movement history.");
            IReadOnlyDictionary<string, int> counters =
                P2PAuthoritativeServer.GetHistoryCounters(0);
            Assert(counters.ContainsKey("play") && counters["play"] >= 1,
                "The Host did not update the native play history counter.");

            Assert(P2PAuthoritativeServer.TryCreateAction(
                    false, 200, new Dictionary<string, object>
                    {
                        ["uri"] = "PlayActions",
                        ["playIdx"] = 7,
                        ["type"] = 30
                    }, out P2PHostAuthoritativeAction laterAction, out error),
                "The Host rejected a later native action: " + error);
            List<object> laterKnown = laterAction.ServerResponse["knownList"]
                as List<object>;
            Dictionary<string, object> laterCard = laterKnown?
                .OfType<Dictionary<string, object>>()
                .SingleOrDefault(card => Convert.ToInt32(card["idx"]) == 7);
            Assert(laterCard != null &&
                Convert.ToInt32(laterCard["setAtk"]) == 4 &&
                Convert.ToInt32(laterCard["spellboost"]) == 2 &&
                laterCard["attachTarget"].ToString() == "1,3",
                "The Host did not retain native hand/deck mutation fields across actions.");

            Assert(P2PAuthoritativeServer.TryCreateDelivery(action,
                    out P2PServerBattleDelivery delivery) && delivery.ToHost &&
                delivery.ServerActionId == action.ServerActionId,
                "The Host did not route the server response to the opposing client.");
        }

        private static void TestOpenMyCardsServerBoundary()
        {
            P2PAuthoritativeServer.Reset();
            Assert(P2PAuthoritativeServer.RememberPrivateStateSnapshot(
                    new Dictionary<string, object>
                    {
                        ["owner"] = 1,
                        ["cards"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["idx"] = 41,
                                ["cardId"] = 900444041,
                                ["cost"] = 4,
                                ["p2pZone"] = 10
                            }
                        }
                    }) == 1,
                "The openMyCards Host baseline was not stored.");

            Dictionary<string, object> request = new Dictionary<string, object>
            {
                ["uri"] = "TurnEndActions",
                ["orderList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["openMyCards"] = new Dictionary<string, object>
                        {
                            ["idx"] = new List<object> { 41 }
                        }
                    }
                }
            };

            Assert(P2PAuthoritativeServer.TryCreateAction(
                    true, 100, request,
                    out P2PHostAuthoritativeAction action,
                    out string error),
                "The Host rejected an openMyCards TurnEndActions request: " +
                error);
            List<object> known = action.ServerResponse["knownList"] as List<object>;
            Dictionary<string, object> opened = known?.OfType<Dictionary<string, object>>()
                .SingleOrDefault(entry =>
                    entry.TryGetValue("idx", out object rawIndex) &&
                    Convert.ToInt32(rawIndex) == 41);
            Assert(opened != null &&
                Convert.ToInt32(opened["cardId"]) == 900444041 &&
                Convert.ToInt32(opened["is_open"]) == 1 &&
                Convert.ToInt32(opened["isSelf"]) == 0,
                "The Host did not convert openMyCards into a revealed native identity " +
                "with the recipient perspective.");
        }

        private static void TestHostAuthoritativeRegisterState()
        {
            P2PAuthoritativeServer.Reset();
            int stored = P2PAuthoritativeServer.RememberPrivateStateSnapshot(
                new Dictionary<string, object>
                {
                    ["owner"] = 0,
                    ["cards"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["idx"] = 1, ["cardId"] = 1001, ["cost"] = 4
                        },
                        new Dictionary<string, object>
                        {
                            ["idx"] = 2, ["cardId"] = 1002, ["cost"] = 2
                        },
                        new Dictionary<string, object>
                        {
                            ["idx"] = 3, ["cardId"] = 1003, ["cost"] = 1
                        }
                    }
                });
            Assert(stored == 3, "The register-state baseline was incomplete.");

            Dictionary<string, object> request = new Dictionary<string, object>
            {
                ["uri"] = "PlayActions",
                ["playIdx"] = 1,
                ["type"] = 30,
                ["knownList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["idx"] = new List<object> { 1, 3 },
                        ["isSelf"] = 1
                    }
                },
                ["orderList"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["move"] = new Dictionary<string, object>
                        {
                            // RegisterActionBase.MakeSendData uses idx for a
                            // list; this is the representation that previously
                            // bypassed the Host zone ledger.
                            ["idx"] = new List<object> { 1, 3 },
                            ["isSelf"] = 1,
                            ["from"] = 10,
                            ["to"] = 20,
                            ["skill"] = "11|17|2",
                            ["skillKeyCardIdx"] = new List<object> { 7 },
                            ["randomTargetIdx"] = new List<object> { 41, 42 },
                            ["attachTarget"] = "3,5",
                            ["isInvoke"] = 1
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["fusion"] = new Dictionary<string, object>
                        {
                            ["idx"] = new List<object> { 1 },
                            ["ingredients"] = new List<object> { 2 },
                            ["isSelf"] = 1
                        }
                    }
                }
            };

            Assert(P2PAuthoritativeServer.TryCreateAction(
                    false, 200, request,
                    out P2PHostAuthoritativeAction action,
                    out string error),
                "The Host rejected a list-based native register action: " +
                error);
            Assert(P2PAuthoritativeServer.TryGetCardZone(0, 1,
                    out int targetZone) && targetZone == 20 &&
                P2PAuthoritativeServer.TryGetCardZone(0, 3,
                    out int secondZone) && secondZone == 20,
                "The Host did not apply all idx-list movement entries.");
            Assert(P2PAuthoritativeServer.TryGetCardZone(0, 2,
                    out int ingredientZone) &&
                ingredientZone == P2PAuthoritativeServer.ZoneFusionIngredient,
                "The Host did not record the fusion ingredient zone.");
            Assert(P2PAuthoritativeServer.GetFusionCount(0) == 1,
                "The Host did not advance the native fusion counter.");
            List<object> canonicalKnown = action.ServerResponse["knownList"] as List<object>;
            Assert(canonicalKnown != null &&
                canonicalKnown.OfType<Dictionary<string, object>>()
                    .Any(entry => entry.TryGetValue("idx", out object idx) &&
                        Convert.ToInt32(idx) == 1 &&
                        Convert.ToInt32(entry["cardId"]) == 1001) &&
                !canonicalKnown.OfType<Dictionary<string, object>>()
                    .Any(entry => entry.TryGetValue("idxList", out object raw) &&
                        raw is IEnumerable values && values.Cast<object>()
                            .Any(value => Convert.ToInt32(value) == 1)),
                "The Host did not split a grouped knownList placeholder into a " +
                "unique scalar identity record.");

            Assert(P2PAuthoritativeServer.TryGetCardState(0, 1,
                    out Dictionary<string, object> state) &&
                Convert.ToInt32(state["skillCardIdx"]) == 11 &&
                Convert.ToInt32(state["publishedActiveSkillCount"]) == 17 &&
                Convert.ToInt32(state["movement"]) == 2 &&
                state["attachedSkillsPublishCount"] is List<object> attached &&
                attached.Select(Convert.ToInt32).SequenceEqual(new[] { 3, 5 }) &&
                ((List<object>)state["fusion"]).Count == 1,
                "The complete RegisterUnapproved metadata was not retained.");

            IReadOnlyList<Dictionary<string, object>> registers =
                P2PAuthoritativeServer.GetRegisterHistorySnapshot();
            Assert(registers.Any(entry =>
                    entry.TryGetValue("kind", out object kind) &&
                    kind.ToString() == "move") &&
                registers.Any(entry =>
                    entry.TryGetValue("kind", out object kind) &&
                    kind.ToString() == "fusion"),
                "Native register history did not retain move and fusion entries.");
            Assert(action.ServerResponse.ContainsKey("knownList"),
                "The Host response lost the native knownList boundary.");
        }

        private static void TestHostPrivateStateDeltaAndRandomCanonicalization()
        {
            P2PAuthoritativeServer.Reset();
            Assert(P2PAuthoritativeServer.RememberPrivateStateSnapshot(
                    new Dictionary<string, object>
                    {
                        ["owner"] = 0,
                        ["cards"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["idx"] = 8,
                                ["cardId"] = 8008,
                                ["cost"] = 5
                            }
                        }
                    }) == 1,
                "The Host delta test baseline was not stored.");

            Dictionary<string, object> deltaRequest =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["playIdx"] = 8,
                    ["type"] = 30,
                    ["p2pHiddenOwner"] = 0,
                    ["p2pHiddenCards"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["idx"] = 8,
                            ["cardId"] = 9008,
                            ["p2pStateDelta"] = 1,
                            ["cost"] = 1,
                            ["p2pGenericKeys"] = new Dictionary<string, object>
                            {
                                ["mechanical"] = 1
                            },
                            ["randomTargetIdx"] = new List<object> { 41, 42 }
                        },
                        new Dictionary<string, object>
                        {
                            ["idx"] = 9,
                            ["cardId"] = 9009,
                            ["cost"] = 2
                        }
                    },
                    ["p2pPlayerHistory"] = new Dictionary<string, object>
                    {
                        ["owner"] = 0,
                        ["revision"] = 3,
                        ["scalars"] = new Dictionary<string, object>
                        {
                            ["GameResonanceStartCount"] = 2,
                            ["_cumulativeEvolutionCount"] = 1
                        },
                        ["lists"] = new Dictionary<string, object>()
                    },
                    ["p2pPlayerHistoryBefore"] = new Dictionary<string, object>
                    {
                        ["owner"] = 0,
                        ["revision"] = 2,
                        ["scalars"] = new Dictionary<string, object>(),
                        ["lists"] = new Dictionary<string, object>()
                    },
                    ["uList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["idx"] = 8,
                            ["isSelf"] = 1,
                            ["from"] = 10,
                            ["to"] = 20
                        }
                    }
                };

            Assert(P2PAuthoritativeServer.TryCreateAction(
                    false, 200, deltaRequest,
                    out P2PHostAuthoritativeAction deltaAction,
                    out string error),
                "The Host rejected a private-state delta request: " + error);
            Assert(P2PAuthoritativeServer.TryGetCardState(0, 8,
                    out Dictionary<string, object> state) &&
                Convert.ToInt32(state["cost"]) == 1 &&
                Convert.ToInt32(state["cardId"]) == 8008 &&
                state.ContainsKey("p2pGenericKeys"),
                "The Host ledger did not absorb the incremental private-card state " +
                "without allowing a delta to replace native card identity.");

            List<object> deltaCards = deltaAction.ServerResponse
                .TryGetValue("p2pHiddenCards", out object rawDeltaCards) &&
                rawDeltaCards is IEnumerable deltaValues &&
                !(rawDeltaCards is string)
                ? deltaValues.Cast<object>().ToList()
                : null;
            Assert(deltaCards != null && deltaCards.Count > 0,
                "The Host response did not retain the private-state delta.");
            Assert(P2PAuthoritativeServer.TryGetPlayerHistoryState(0,
                    out Dictionary<string, object> history) &&
                Convert.ToInt32(history["revision"]) == 3,
                "The Host did not retain the revisioned player-history state.");

            Dictionary<string, object> responseUList =
                (deltaAction.ServerResponse["uList"] as List<object>)?
                    .OfType<Dictionary<string, object>>().SingleOrDefault();
            Assert(responseUList != null &&
                responseUList["randomTargetIdx"] is List<object> randomTargets &&
                randomTargets.Select(Convert.ToInt32).SequenceEqual(new[] { 41, 42 }),
                "The native randomTargetIdx result was not canonicalized in uList.");
            Assert(P2PAuthoritativeServer.ValidateAndRecordNativeRandomResults(
                    new Dictionary<string, object>
                    {
                        ["uList"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["randomTargetIdx"] = 9
                            }
                        }
                    },
                    77,
                    false,
                    out error),
                "The Host rejected a valid scalar native random result: " + error);
            IReadOnlyList<Dictionary<string, object>> randomHistory =
                P2PAuthoritativeServer.GetRandomResultHistorySnapshot();
            Assert(randomHistory.Count > 0 &&
                randomHistory.Last()["values"] is List<object> recordedRandom &&
                recordedRandom.Select(Convert.ToInt32).SequenceEqual(new[] { 9 }),
                "The Host did not retain the native random result audit entry.");
            Assert(!P2PAuthoritativeServer.ValidateAndRecordNativeRandomResults(
                    new Dictionary<string, object>
                    {
                        ["uList"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["randomTargetIdx"] = new List<object> { -1 }
                            }
                        }
                    },
                    78,
                    false,
                    out error) &&
                error.Contains("negative"),
                "The Host accepted an invalid native random result.");
            IReadOnlyDictionary<string, int> deltaCounters =
                P2PAuthoritativeServer.GetHistoryCounters(0);
            Assert(deltaCounters.ContainsKey("play") &&
                    deltaCounters["play"] >= 1,
                "The Host did not record the native play envelope event.");

            Dictionary<string, object> laterRequest =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["playIdx"] = 8,
                    ["type"] = 30
                };
            Assert(P2PAuthoritativeServer.TryCreateAction(
                    false, 200, laterRequest,
                    out P2PHostAuthoritativeAction laterAction,
                    out error),
                "The Host rejected a later action after a private-state delta: " +
                error);
            Dictionary<string, object> laterKnown =
                (laterAction.ServerResponse["knownList"] as List<object>)?
                    .OfType<Dictionary<string, object>>()
                    .SingleOrDefault(card => Convert.ToInt32(card["idx"]) == 8);
            Assert(laterKnown != null && Convert.ToInt32(laterKnown["cost"]) == 1,
                "The Host did not reuse the updated private-card state in knownList.");
        }

        private static void TestPreparedPrivateIdentityPromotion()
        {
            P2PAuthoritativeServer.Reset();
            Assert(P2PAuthoritativeServer.RememberPrivateStateSnapshot(
                    new Dictionary<string, object>
                    {
                        ["owner"] = 1,
                        ["cards"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["idx"] = 4,
                                ["cardId"] = 118611011,
                                ["cost"] = 2
                            }
                        }
                    }) == 1,
                "The prepared-identity test baseline was not stored.");

            Dictionary<string, object> request =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["playIdx"] = 4,
                    ["type"] = 30,
                    [P2PBattleProtocol.PreparedActionKey] = 1,
                    [P2PBattleProtocol.PreparedCardIdKey] = 709314011,
                    ["knownList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["idx"] = 4,
                            ["cardId"] = 118611011,
                            ["isSelf"] = 1
                        }
                    },
                    ["orderList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["move"] = new Dictionary<string, object>
                            {
                                ["idx"] = 4,
                                ["isSelf"] = 1,
                                ["from"] = 10,
                                ["to"] = 30
                            }
                        }
                    }
                };

            Assert(P2PAuthoritativeServer.TryCreateAction(
                    true, 100, request,
                    out P2PHostAuthoritativeAction action,
                    out string error),
                "The Host rejected a prepared private identity: " + error);
            Dictionary<string, object> revealed =
                (action.ServerResponse["knownList"] as List<object>)?
                    .OfType<Dictionary<string, object>>()
                    .SingleOrDefault(entry =>
                        entry.TryGetValue("idx", out object rawIndex) &&
                        Convert.ToInt32(rawIndex) == 4);
            Assert(revealed != null &&
                Convert.ToInt32(revealed["cardId"]) == 709314011 &&
                Convert.ToInt32(revealed["isSelf"]) == 0,
                "The Host response did not replace an old private baseline " +
                "identity with the prepared native play identity.");
            Assert(P2PAuthoritativeServer.TryGetCardState(1, 4,
                    out Dictionary<string, object> state) &&
                Convert.ToInt32(state["cardId"]) == 709314011,
                "The Host ledger did not retain the prepared private identity.");
        }

        private static void
            TestPublicTargetIndexDoesNotConsumeOtherOwnerPrivateIdentity()
        {
            P2PAuthoritativeServer.Reset();
            Assert(P2PAuthoritativeServer.RememberPrivateStateSnapshot(
                    new Dictionary<string, object>
                    {
                        ["owner"] = 0,
                        ["cards"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["idx"] = 4,
                                ["cardId"] = 2004,
                                ["p2pZone"] = 10
                            }
                        }
                    }) == 1 &&
                P2PAuthoritativeServer.RememberPrivateStateSnapshot(
                    new Dictionary<string, object>
                    {
                        ["owner"] = 1,
                        ["cards"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["idx"] = 4,
                                ["cardId"] = 1004,
                                ["p2pZone"] = 10
                            }
                        }
                    }) == 1,
                "The public-target collision baseline was not stored.");

            Dictionary<string, object> request =
                new Dictionary<string, object>
                {
                    ["uri"] = "PlayActions",
                    ["playIdx"] = 4,
                    ["type"] = 10,
                    ["targetList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["targetIdx"] = 4,
                            ["isSelf"] = 0
                        }
                    },
                    ["orderList"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["move"] = new Dictionary<string, object>
                            {
                                ["idx"] = 4,
                                ["isSelf"] = 1,
                                ["from"] = 20,
                                ["to"] = 30
                            }
                        }
                    },
                    // This is a post-action snapshot of the Host card.  It is
                    // deliberately public now; it must not be promoted as a
                    // Guest private identity just because targetIdx is also 4.
                    ["p2pHiddenOwner"] = 1,
                    ["p2pHiddenCards"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["idx"] = 4,
                            ["cardId"] = 1004,
                            ["p2pZone"] = 20
                        }
                    }
                };

            Assert(P2PAuthoritativeServer.TryCreateAction(
                    true, 100, request,
                    out P2PHostAuthoritativeAction action,
                    out string error),
                "The Host rejected a public target collision request: " +
                error);
            List<object> known = action.ServerResponse["knownList"] as List<object>;
            Assert(known != null &&
                !known.OfType<Dictionary<string, object>>().Any(entry =>
                    entry.TryGetValue("isSelf", out object rawSelf) &&
                    Convert.ToInt32(rawSelf) == 0 &&
                    entry.TryGetValue("cardId", out object rawCardId) &&
                    Convert.ToInt32(rawCardId) == 2004),
                "A public target index incorrectly consumed the other owner's " +
                "private identity from the Host ledger.");
        }

        private static void TestPrivateStateFieldDeltaRoundTrip()
        {
            P2PAuthoritativeServer.Reset();
            Assert(P2PAuthoritativeServer.RememberPrivateStateSnapshot(
                    new Dictionary<string, object>
                    {
                        ["owner"] = 0,
                        ["cards"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["idx"] = 8,
                                ["cardId"] = 8008,
                                ["cost"] = 5,
                                ["attachTarget"] = "1,2",
                                ["p2pGenericKeys"] = new Dictionary<string, object>
                                {
                                    ["old"] = 1
                                }
                            }
                        }
                    }) == 1,
                "The field-delta baseline was not stored.");

            Dictionary<string, object> request = new Dictionary<string, object>
            {
                ["uri"] = "PlayActions",
                ["playIdx"] = 8,
                ["type"] = 30,
                ["p2pHiddenOwner"] = 0,
                ["p2pHiddenCards"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["idx"] = 8,
                        ["cardId"] = 8008,
                        ["p2pStateDelta"] = 1,
                        ["cost"] = 1,
                        ["p2pRemovedFields"] = new List<object>
                        {
                            "attachTarget"
                        }
                    }
                }
            };

            Assert(P2PAuthoritativeServer.TryCreateAction(
                    false, 200, request,
                    out P2PHostAuthoritativeAction action,
                    out string error),
                "The Host rejected a field-level private-state delta: " + error);
            Assert(P2PAuthoritativeServer.TryGetCardState(0, 8,
                    out Dictionary<string, object> merged) &&
                Convert.ToInt32(merged["cost"]) == 1 &&
                !merged.ContainsKey("attachTarget") &&
                merged["p2pGenericKeys"] is Dictionary<string, object> keys &&
                Convert.ToInt32(keys["old"]) == 1,
                "The Host did not merge changed and removed private-state fields.");

            List<object> responseCards = action.ServerResponse
                .TryGetValue("p2pHiddenCards", out object rawCards) &&
                rawCards is IEnumerable cards && !(rawCards is string)
                ? cards.Cast<object>().ToList()
                : null;
            Dictionary<string, object> responseCard = responseCards?
                .OfType<Dictionary<string, object>>()
                .SingleOrDefault(card => Convert.ToInt32(card["idx"]) == 8);
            Assert(responseCard != null &&
                !responseCard.ContainsKey("p2pStateDelta") &&
                Convert.ToInt32(responseCard["cost"]) == 1 &&
                !responseCard.ContainsKey("attachTarget") &&
                responseCard["p2pGenericKeys"] is Dictionary<string, object> responseKeys &&
                Convert.ToInt32(responseKeys["old"]) == 1,
                "The Host response was not rebuilt from the authoritative full state.");

            Assert(P2PAuthoritativeServer.TryCreateDelivery(action,
                    out P2PServerBattleDelivery delivery),
                "The Host could not create a delivery for the field-delta action.");
            List<object> deliverySnapshots = delivery.Data
                .TryGetValue(P2PBattleProtocol.AuthorityHiddenStatesKey,
                    out object rawSnapshots) && rawSnapshots is IEnumerable snapshots &&
                !(rawSnapshots is string)
                ? snapshots.Cast<object>().ToList()
                : null;
            Dictionary<string, object> deliveryCard = deliverySnapshots?
                .OfType<Dictionary<string, object>>()
                .SelectMany(snapshot => snapshot.TryGetValue("cards",
                        out object snapshotCards) && snapshotCards is IEnumerable cards &&
                    !(snapshotCards is string)
                        ? cards.OfType<Dictionary<string, object>>()
                        : Enumerable.Empty<Dictionary<string, object>>())
                .SingleOrDefault(card => Convert.ToInt32(card["idx"]) == 8);
            Assert(deliveryCard != null &&
                Convert.ToInt32(deliveryCard["p2pStateDelta"]) == 1 &&
                Convert.ToInt32(deliveryCard["cost"]) == 1 &&
                deliveryCard["p2pRemovedFields"] is List<object> removed &&
                removed.Any(value => value.ToString() == "attachTarget"),
                "The ServerBattleDelivery did not reduce the ledger state to a field delta.");
        }

        private static void TestBattleStateDiagnostics()
        {
            Assert(P2PBattleStateDiagnostics.DecideCheck(false, true, false) ==
                    P2PBattleStateCheckDecision.Wait,
                "A state mismatch was reported before the checkpoint deadline.");
            Assert(P2PBattleStateDiagnostics.DecideCheck(true, true, false) ==
                    P2PBattleStateCheckDecision.Synchronized,
                "A completed matching checkpoint was not accepted.");
            Assert(P2PBattleStateDiagnostics.DecideCheck(false, true, true) ==
                    P2PBattleStateCheckDecision.Desynchronized,
                "A timed-out state mismatch was not reported.");
            Assert(P2PBattleStateDiagnostics.DecideCheck(true, false, true) ==
                    P2PBattleStateCheckDecision.Stalled,
                "A timed-out effect queue was not reported as stalled.");

            Dictionary<string, object> expected = new Dictionary<string, object>
            {
                ["turn"] = 6,
                ["host"] = new Dictionary<string, object>
                {
                    ["life"] = 20,
                    ["hand"] = "1,4,8",
                    ["cemetery"] = "3:103"
                },
                ["guest"] = new Dictionary<string, object>
                {
                    ["life"] = 17,
                    ["hand"] = "2,5"
                }
            };
            Dictionary<string, object> actual = P2PJson.CloneDictionary(expected);
            ((Dictionary<string, object>)actual["host"])["cemetery"] =
                "3:103,8:808";
            ((Dictionary<string, object>)actual["guest"])["life"] = 14;

            IReadOnlyList<string> differences =
                P2PBattleStateDiagnostics.Compare(expected, actual);
            Assert(differences.Count == 2 &&
                differences.Any(value => value.StartsWith("guest.life:")) &&
                differences.Any(value => value.StartsWith("host.cemetery:")),
                "Battle-state diagnostics did not identify the differing fields.");
            Assert(P2PBattleStateDiagnostics.Compare(expected,
                    P2PJson.CloneDictionary(expected)).Count == 0,
                "Equal battle states were reported as desynchronized.");

            Dictionary<string, object> largeExpected = new Dictionary<string, object>
            {
                ["handState"] = new string('x', 512)
            };
            Dictionary<string, object> largeActual = new Dictionary<string, object>
            {
                ["handState"] = new string('y', 512)
            };
            IReadOnlyList<string> largeDifferences =
                P2PBattleStateDiagnostics.Compare(largeExpected, largeActual);
            Assert(largeDifferences.Count == 1 &&
                largeDifferences[0].Length < 180 &&
                largeDifferences[0].Contains("len=512"),
                "Large diagnostic values were not summarized.");
            Assert(P2PBattleStateDiagnostics.DescribeDifferences(
                    Enumerable.Range(0, 15).Select(index => "field" + index)
                        .ToList())
                    .Contains("+3 differing field(s)"),
                "Diagnostic difference output was not capped.");
        }

        private static void Wait(ManualResetEventSlim signal, string error)
        {
            Assert(signal.Wait(TimeSpan.FromSeconds(5)), error);
        }

        private static byte[] CreateToken(int start)
        {
            byte[] result = new byte[16];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = (byte)(start + i);
            }
            return result;
        }

        private static bool EqualBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }
            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }

    internal static class Plugin
    {
        internal static readonly TestLogger Logger = new TestLogger();
    }

    internal sealed class TestLogger
    {
        internal void LogWarning(object value)
        {
            Console.WriteLine(value);
        }
    }
}
