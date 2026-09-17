using Cute;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.InputSystem;
using Wizard;
using Wizard.Battle.Resource;
using Wizard.Battle.View.Vfx;

namespace Shadowbus
{
    public class CardParameterPatch
    {
        private static readonly HashSet<string> VariantIdentityFields = new HashSet<string>
        {
            nameof(CardParameter.CardId),
            nameof(CardParameter.IsFoil),
            nameof(CardParameter.CardHashId)
        };

        public bool newCard = false;
        public int cardId = 0;
        public int templateCardId;
        // Optional. A normal or foil CardId from the original game whose foil
        // material is used as this card's visual-effect template.
        public int? foilEffectCardId;
        // Optional artwork borrowing, one slot at a time. Each value is a card id
        // from the original game whose card face this card uses for that slot;
        // absent or 0 keeps the engine's own resolution.
        //   normalArtCardId    - face shown before evolution (follower art)
        //   evolutionArtCardId - face shown after evolution ("<id>1_M")
        //   spellArtCardId     - spell/amulet face, i.e. 咒术 and 魔法阵 art; both
        //                        come from the same material set and only the card's
        //                        own CharType decides how it is drawn
        // A slot may also be borrowed across card kinds: a follower card can use a
        // spell/amulet face and the other way round, because the material is fetched
        // from the bundle the source card actually lives in.
        // Foil (闪卡) is not a separate face here - it is the foil material of
        // foilEffectCardId above.
        public int? normalArtCardId;
        public int? evolutionArtCardId;
        public int? spellArtCardId;
        // Which face of the SOURCE card each slot takes. Leaving one out keeps that
        // slot in step with the card's own stage (normal slot -> source normal face,
        // evolved slot -> source evolved face), which is what plain borrowing did.
        // Setting one decouples the two, so the source face may come from the other
        // stage: "normalArtFromEvolved": true puts the source's EVOLVED face on this
        // card's normal side, and "evolutionArtFromEvolved": false puts the source's
        // NORMAL face on this card's evolved side.
        public bool? normalArtFromEvolved;
        public bool? evolutionArtFromEvolved;
        public bool? spellArtFromEvolved;
        public Dictionary<string, bool> boolFields = [];
        public Dictionary<string, int> intFields = [];
        // Single-valued float CardParameter properties (SummonTime, EvolTime, ...).
        // intFields is Int32-only and the string group assigns raw strings, so these
        // had no usable JSON channel at all before.
        public Dictionary<string, float> floatFields = [];
        // Optional. Lets this card play another card's voice.
        // Voices live in one ACB per card id ("v/vo_<cardId>.acb") and the game
        // only ever loads the card's own bank, so a cross-id voice entry would
        // otherwise point at a bank that was never loaded and stay silent.
        // Every entry is a voice id as written in the master voice columns
        // (PlayVoice/EvoVoice/AtkVoice/DestroyVoice/SkillVoice), for example
        // "125641030_4"; only the part before the first '_' matters, because the
        // game itself derives the bank name that way. Listing "125641030" and
        // "125641030_4" loads the same bank. Missing or empty loads nothing extra.
        public string[] extraVoiceIds = [];
        public Dictionary<string, int[]> intArrayFields = [];
        public Dictionary<string, string> stringChangeFields = [];
        public Dictionary<string, string> stringAppendFields = [];
        public Dictionary<string, string[]> stringArrayFields = [];
        public Dictionary<string, string> localizationFields = [];
        public AttackEffectParameterPatch attackEffectFields = new AttackEffectParameterPatch();
        // Optional. Local audio files inside the card's own folder, used as this card's
        // voices. Applied right after PatchTemplate, because the cue names it produces are
        // written straight onto the card's voice fields.
        public CardVoiceFilePatch voiceFiles;
        // Optional. Local artwork files inside the card's own folder. Names are free;
        // without it the folder's conventional card.png / card_evo.png are used.
        public CardImageFilePatch imageFiles;

        public void PatchTemplate(CardParameter card, bool preserveVariantIdentity = false)
        {
            if (card == null)
            {
                Plugin.Logger.LogWarning($"Cannot patch null card for template {templateCardId}");
                return;
            }

            ApplyFields(card, boolFields, preserveVariantIdentity);
            ApplyFields(card, intFields, preserveVariantIdentity);
            ApplyFields(card, floatFields, preserveVariantIdentity);
            ApplyIntArrayFields(card, preserveVariantIdentity);
            ApplyFields(card, stringChangeFields, preserveVariantIdentity);

            if (stringAppendFields != null)
            {
                foreach (var kvp in stringAppendFields)
                {
                    TrySetProperty(card, kvp.Key, property =>
                    {
                        string oldValue = (string)property.GetValue(card);
                        return (oldValue ?? string.Empty) + kvp.Value;
                    }, preserveVariantIdentity);
                }
            }

            if (stringArrayFields != null)
            {
                foreach (var kvp in stringArrayFields)
                {
                    string[] value = kvp.Value == null ? null : (string[])kvp.Value.Clone();
                    TrySetProperty(card, kvp.Key, _ => value, preserveVariantIdentity);
                }
            }

            ApplyAttackEffectFields(card);

            if (localizationFields != null)
            {
                foreach (var kvp in localizationFields)
                {
                    if (!string.IsNullOrEmpty(kvp.Value))
                    {
                        CardMasterPatcher.CustomLocalization[$"{card.CardId}_{kvp.Key}"] = kvp.Value;
                    }
                }
            }
        }

        private void ApplyAttackEffectFields(CardParameter card)
        {
            if (attackEffectFields == null || card?.AtkEffectParameter == null)
            {
                return;
            }

            if (attackEffectFields.effectPath != null)
            {
                card.AtkEffectParameter._effectPath = ToStringPairList(attackEffectFields.effectPath);
            }

            if (attackEffectFields.se != null)
            {
                card.AtkEffectParameter._se = ToStringPairList(attackEffectFields.se);
            }

            if (attackEffectFields.moveType != null)
            {
                card.AtkEffectParameter._moveType = ToPairList(
                    attackEffectFields.moveType,
                    value => ParseEnum(value, EffectMgr.MoveType.NONE));
            }

            if (attackEffectFields.effectEnginType != null)
            {
                card.AtkEffectParameter._effectEnginType = ToPairList(
                    attackEffectFields.effectEnginType,
                    value => ParseEnum(value, EffectMgr.EngineType.NONE));
            }

            if (attackEffectFields.time != null)
            {
                card.AtkEffectParameter._time = ToPairList(attackEffectFields.time, value => value);
            }
        }

        private void ApplyIntArrayFields(
            CardParameter card,
            bool preserveVariantIdentity)
        {
            if (intArrayFields == null)
            {
                return;
            }

            foreach (var kvp in intArrayFields)
            {
                TrySetProperty(
                    card,
                    kvp.Key,
                    property => ConvertIntArrayValue(property.PropertyType, kvp.Value),
                    preserveVariantIdentity);
            }
        }

        private static object ConvertIntArrayValue(Type propertyType, int[] values)
        {
            values = values ?? Array.Empty<int>();
            Type elementType;
            if (propertyType.IsArray)
            {
                elementType = propertyType.GetElementType();
                Array array = Array.CreateInstance(elementType, values.Length);
                for (int index = 0; index < values.Length; index++)
                {
                    array.SetValue(ConvertIntValue(values[index], elementType), index);
                }
                return array;
            }

            if (propertyType.IsGenericType &&
                propertyType.GetGenericTypeDefinition() == typeof(List<>))
            {
                elementType = propertyType.GetGenericArguments()[0];
                System.Collections.IList list =
                    (System.Collections.IList)Activator.CreateInstance(propertyType);
                foreach (int value in values)
                {
                    list.Add(ConvertIntValue(value, elementType));
                }
                return list;
            }

            throw new InvalidOperationException(
                $"Property type '{propertyType}' is not an integer array or list.");
        }

        private static object ConvertIntValue(int value, Type targetType)
        {
            return targetType.IsEnum
                ? Enum.ToObject(targetType, value)
                : Convert.ChangeType(value, targetType);
        }

        private static List<string> ToStringPairList(IEnumerable<string> values)
        {
            List<string> list = values == null
                ? new List<string>()
                : values.Select(value => value ?? string.Empty).ToList();
            if (list.Count == 0)
            {
                return new List<string> { string.Empty, string.Empty };
            }

            if (list.Count == 1)
            {
                list.Add(list[0]);
            }
            else if (list.Count > 2)
            {
                list = list.Take(2).ToList();
            }

            return list;
        }

        private static List<TOut> ToPairList<TIn, TOut>(IEnumerable<TIn> values, Func<TIn, TOut> converter)
        {
            List<TOut> list = values == null
                ? new List<TOut>()
                : values.Select(converter).ToList();
            if (list.Count == 0)
            {
                return new List<TOut> { default(TOut), default(TOut) };
            }

            if (list.Count == 1)
            {
                list.Add(list[0]);
            }
            else if (list.Count > 2)
            {
                list = list.Take(2).ToList();
            }

            return list;
        }

        private static T ParseEnum<T>(string value, T fallback) where T : struct
        {
            if (string.IsNullOrEmpty(value))
            {
                return fallback;
            }

            if (int.TryParse(value, out int number))
            {
                return (T)Enum.ToObject(typeof(T), number);
            }

            return Enum.TryParse(value, true, out T parsed) ? parsed : fallback;
        }

        private void ApplyFields<T>(
            CardParameter card,
            Dictionary<string, T> fields,
            bool preserveVariantIdentity)
        {
            if (fields == null)
            {
                return;
            }

            foreach (var kvp in fields)
            {
                TrySetProperty(card, kvp.Key, property => ConvertValue(property, kvp.Value),
                    preserveVariantIdentity);
            }
        }

        private void TrySetProperty(
            CardParameter card,
            string propertyName,
            Func<PropertyInfo, object> valueFactory,
            bool preserveVariantIdentity)
        {
            if (preserveVariantIdentity && VariantIdentityFields.Contains(propertyName))
            {
                return;
            }

            try
            {
                PropertyInfo property = AccessTools.Property(typeof(CardParameter), propertyName);
                if (property == null || !property.CanWrite)
                {
                    Plugin.Logger.LogWarning(
                        $"CardParameter property '{propertyName}' is missing or read-only; skipping it");
                    return;
                }

                property.SetValue(card, valueFactory(property));
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError(
                    $"Error patching card {card.CardId} property '{propertyName}': {e.Message}");
            }
        }

        private static object ConvertValue<T>(PropertyInfo property, T value)
        {
            if (property.PropertyType.IsEnum && value is int enumValue)
            {
                return Enum.ToObject(property.PropertyType, enumValue);
            }

            if (value == null)
            {
                return null;
            }

            Type targetType = property.PropertyType;
            if (targetType.IsAssignableFrom(value.GetType()))
            {
                return value;
            }

            // CardParameter owns Single fields (SummonTime / EvolTime / ...). An Int32
            // coming from intFields was widened by SetValue itself, but a raw string
            // from stringChangeFields threw
            // "Object of type 'System.String' cannot be converted to type 'System.Single'".
            // Convert explicitly so numeric text can drive numeric properties.
            try
            {
                return Convert.ChangeType(value, targetType, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return value;
            }
        }

        public void ConvertFrom(CardParameter original)
        {
            PropertyInfo[] properties = typeof(CardParameter).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            templateCardId = original.CardId;
            foreach (PropertyInfo property in properties)
            {
                if (!property.CanWrite||!property.CanRead || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (property.Name == "CardId")
                {
                    continue;
                }

                object value = property.GetValue(original);
                Type propType = property.PropertyType;

                if (propType == typeof(int))
                {
                    this.intFields[property.Name] = (int)value;
                }
                else if (propType == typeof(float))
                {
                    this.floatFields[property.Name] = (float)value;
                }
                else if (propType == typeof(bool))
                {
                    this.boolFields[property.Name] = (bool)value;
                }
                else if (propType == typeof(string))
                {
                    if (value != null)
                    {
                        this.stringChangeFields[property.Name] = (string)value;
                    }
                }
                else if (propType == typeof(string[]))
                {
                    if (value != null)
                    {
                        this.stringArrayFields[property.Name] = (string[])value;
                    }
                }
                else if (TryConvertIntArray(propType, value, out int[] intArray))
                {
                    this.intArrayFields[property.Name] = intArray;
                }
                else if (propType.IsEnum)
                {
                    this.intFields[property.Name] = (int)value;
                }
            }

            if (original.AtkEffectParameter != null)
            {
                this.attackEffectFields = new AttackEffectParameterPatch
                {
                    effectPath = new[]
                    {
                        original.AtkEffectParameter.GetEffectPath(false),
                        original.AtkEffectParameter.GetEffectPath(true)
                    },
                    se = new[]
                    {
                        original.AtkEffectParameter.GetSe(false),
                        original.AtkEffectParameter.GetSe(true)
                    },
                    moveType = new[]
                    {
                        original.AtkEffectParameter.GetMoveType(false).ToString(),
                        original.AtkEffectParameter.GetMoveType(true).ToString()
                    },
                    effectEnginType = new[]
                    {
                        original.AtkEffectParameter.GetEffectEnginType(false).ToString(),
                        original.AtkEffectParameter.GetEffectEnginType(true).ToString()
                    },
                    time = new[]
                    {
                        original.AtkEffectParameter.GetTime(false),
                        original.AtkEffectParameter.GetTime(true)
                    }
                };
            }
        }

        private static bool TryConvertIntArray(
            Type propertyType,
            object value,
            out int[] result)
        {
            Type elementType = null;
            if (propertyType.IsArray)
            {
                elementType = propertyType.GetElementType();
            }
            else if (propertyType.IsGenericType &&
                propertyType.GetGenericTypeDefinition() == typeof(List<>))
            {
                elementType = propertyType.GetGenericArguments()[0];
            }

            if (elementType == null ||
                (!elementType.IsEnum && elementType != typeof(int)))
            {
                result = null;
                return false;
            }

            if (value == null)
            {
                result = Array.Empty<int>();
                return true;
            }

            result = ((System.Collections.IEnumerable)value)
                .Cast<object>()
                .Select(Convert.ToInt32)
                .ToArray();
            return true;
        }
    }

    /// <summary>
    /// The game's attack effect data is stored in a nested AttackEffectParameter
    /// rather than as writable CardParameter properties. Keep it as a small,
    /// human-editable pair of normal/evolved values in the patch JSON.
    /// </summary>
    public class AttackEffectParameterPatch
    {
        public string[] effectPath;
        public string[] se;
        public string[] moveType;
        public string[] effectEnginType;
        public float[] time;
    }
    public class CardMasterPatcher
    {
        public static Dictionary<int,CardParameter> CardParameterBackup = [];
        public static Dictionary<string, string> CustomLocalization = [];
        private static readonly Dictionary<int, FoilEffectBinding>
            FoilEffectsByTargetResourceId = [];
        private static readonly Dictionary<int, HashSet<int>>
            SourceBundleResourcesByTargetBundleResourceId = [];
        private static readonly Dictionary<int, ResourcesManager.AssetLoadPathType>
            TargetBundleMaterialTypes = [];
        private static readonly HashSet<int> DisabledFoilEffectResourceIds = [];
        private static readonly HashSet<string> FoilEffectWarnings = [];
        private static readonly HashSet<Material> GeneratedCardMaterials = [];

        // cardId -> extra voice bank ACB paths ("v/vo_<cardId>.acb") declared by
        // that card's patch. Filled while the CardMaster patches are applied and
        // consumed by the voice-loading hooks below.
        private static readonly Dictionary<int, List<string>> ExtraVoiceAcbPathsByCardId = [];
        // Banks already requested in the current battle. Trying to load the same
        // ACB on every attack/destroy would spam the loader, so each bank is
        // requested once; a battle start clears this set again.
        private static readonly HashSet<string> LoadedExtraVoiceAcbs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Target resource id -> per-slot artwork sources. The engine resolves every
        // card face through ResourcesManager.FindCardMaterial(targetResourceCardId,
        // ...), so a single entry here can redirect the face of one card without
        // touching anything else, as long as the target resource id belongs to that
        // card alone.
        private static readonly Dictionary<int, ArtSlotBinding> ArtSlotsByTargetResourceId = [];
        // Bundle type each target resource id uses, needed to recognise the target
        // bundle while the game preloads assets.
        private static readonly Dictionary<int, ResourcesManager.AssetLoadPathType>
            ArtSlotTargetBundleTypes = [];
        // Source bundles already requested in this scene, so the artwork load is not
        // repeated for every lookup.
        private static readonly HashSet<string> RequestedArtSourceBundles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> ArtSlotWarnings = [];
        private static readonly HashSet<int> DisabledArtSlotResourceIds = [];
        // Bundles pulled in by the on-demand fallback in GetArtSlotMaterial. Kept
        // apart from RequestedArtSourceBundles so a bundle the preload path already
        // asked for can still be retried if it turns out not to be resident.
        private static readonly HashSet<string> ArtSourceBundlesLoadedOnDemand =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool IsLoadingArtSourceBundle;
        // Set once the borrowed-art bundles have been appended to an asset group, so
        // the relaxed guard in the preload hook only fires on the first such load.
        private static bool ArtSourceBundlesRequestedInGroup;

        [ThreadStatic]
        private static bool IsResolvingFoilEffectTemplate;

        // Guards the artwork rescue below, which goes back into FindCardMaterial.
        [ThreadStatic]
        private static bool IsResolvingArtworkFallback;

        private sealed class FoilEffectBinding
        {
            public int TargetFoilCardId;
            public int TargetResourceCardId;
            public int SourceFoilCardId;
            public int SourceFoilResourceCardId;
            public int SourceNormalResourceCardId;
        }

        private sealed class FoilEffectRequest
        {
            public int TargetCardId;
            public int SourceCardId;
        }

        private sealed class ArtSlotRequest
        {
            public int CardId;
            public CardParameterPatch Patch;

            public static bool DeclaresAnySlot(CardParameterPatch patch)
            {
                return patch != null &&
                    (patch.normalArtCardId.GetValueOrDefault() != 0 ||
                     patch.evolutionArtCardId.GetValueOrDefault() != 0 ||
                     patch.spellArtCardId.GetValueOrDefault() != 0);
            }
        }

        // One resolved artwork source: the resource id whose material set the slot
        // draws from, plus the bundle that resource id lives in. The bundle is not
        // always the one the target card would use, which is what makes borrowing a
        // spell/amulet face onto a follower (and the other way round) work.
        private sealed class ArtSlotBinding
        {
            public int TargetResourceCardId;
            public int OwnerCardId;
            public int NormalResourceCardId;
            public ResourcesManager.AssetLoadPathType NormalBundleType;
            // Stage of the SOURCE face each slot draws from. Kept apart from the
            // stage of this card so a slot can borrow across stages.
            public bool NormalSourceEvolved;
            public int EvolutionResourceCardId;
            public ResourcesManager.AssetLoadPathType EvolutionBundleType;
            public bool EvolutionSourceEvolved;
            public int SpellResourceCardId;
            public ResourcesManager.AssetLoadPathType SpellBundleType;
            public bool SpellSourceEvolved;
        }

        private static readonly ConditionalWeakTable<CardParameter, RuntimeCardText>
            RuntimeCardTexts = new ConditionalWeakTable<CardParameter, RuntimeCardText>();

        private sealed class RuntimeCardText
        {
            public string CardName;
            public string TribeName;
            public string SkillDescription;
            public string EvoSkillDescription;
            public string Description;
            public string EvoDescription;
        }

        public static void SetRuntimeCardText(
            CardParameter parameter,
            string cardName,
            string tribeName,
            string skillDescription,
            string evoSkillDescription,
            string description,
            string evoDescription)
        {
            if (parameter == null)
            {
                return;
            }

            RuntimeCardTexts.Remove(parameter);
            RuntimeCardTexts.Add(parameter, new RuntimeCardText
            {
                CardName = cardName,
                TribeName = tribeName,
                SkillDescription = skillDescription,
                EvoSkillDescription = evoSkillDescription,
                Description = description,
                EvoDescription = evoDescription
            });
        }


        [HarmonyPatch(typeof(CardParameter), nameof(CardParameter.CardName), MethodType.Getter)]
        [HarmonyPrefix]
        public static bool CardParameter_CardName_Get(ref CardParameter __instance, ref string __result)
        {
            if (RuntimeCardTexts.TryGetValue(__instance, out RuntimeCardText runtimeText))
            {
                __result = runtimeText.CardName;
                return false;
            }

            var id = __instance.CardId;
            var key = $"{id}_CardName";
            if (CustomLocalization.TryGetValue(key, out string result)) {
                
                __result = result;
                return false;
            }
            return true;
        }
        [HarmonyPatch(typeof(CardParameter), nameof(CardParameter.TribeName), MethodType.Getter)]
        [HarmonyPrefix]
        public static bool CardParameter_TribeName_Get(ref CardParameter __instance, ref string __result)
        {
            if (RuntimeCardTexts.TryGetValue(__instance, out RuntimeCardText runtimeText))
            {
                __result = runtimeText.TribeName;
                return false;
            }

            // TribeName has no setter and the engine derives it from the template's
            // _tribeNameId, so a card the game's localisation table has never heard of
            // renders an empty type line. localizationFields stores its text under the
            // same "<cardId>_<field>" key as every other field, so read it here too -
            // the siblings below have always done this, this getter simply was missed.
            var id = __instance.CardId;
            var key = $"{id}_TribeName";
            if (CustomLocalization.TryGetValue(key, out string result))
            {

                __result = result;
                return false;
            }
            return true;
        }
        [HarmonyPatch(typeof(CardParameter), nameof(CardParameter.SkillDescription), MethodType.Getter)]
        [HarmonyPrefix]
        public static bool CardParameter_SkillDescription_Get(ref CardParameter __instance, ref string __result)
        {
            if (RuntimeCardTexts.TryGetValue(__instance, out RuntimeCardText runtimeText))
            {
                __result = runtimeText.SkillDescription;
                return false;
            }

            var id = __instance.CardId;
            var key = $"{id}_SkillDescription";
            if (CustomLocalization.TryGetValue(key, out string result))
            {

                __result = result;
                return false;
            }
            return true;
        }
        [HarmonyPatch(typeof(CardParameter), nameof(CardParameter.EvoSkillDescription), MethodType.Getter)]
        [HarmonyPrefix]
        public static bool CardParameter_EvoSkillDescription_Get(ref CardParameter __instance, ref string __result)
        {
            if (RuntimeCardTexts.TryGetValue(__instance, out RuntimeCardText runtimeText))
            {
                __result = runtimeText.EvoSkillDescription;
                return false;
            }

            var id = __instance.CardId;
            var key = $"{id}_EvoSkillDescription";
            if (CustomLocalization.TryGetValue(key, out string result))
            {

                __result = result;
                return false;
            }
            return true;
        }
        [HarmonyPatch(typeof(CardParameter), nameof(CardParameter.Description), MethodType.Getter)]
        [HarmonyPrefix]
        public static bool CardParameter_Description_Get(ref CardParameter __instance, ref string __result)
        {
            if (RuntimeCardTexts.TryGetValue(__instance, out RuntimeCardText runtimeText))
            {
                __result = runtimeText.Description;
                return false;
            }

            var id = __instance.CardId;
            var key = $"{id}_Description";
            if (CustomLocalization.TryGetValue(key, out string result))
            {

                __result = result;
                return false;
            }
            return true;
        }
        [HarmonyPatch(typeof(CardParameter), nameof(CardParameter.EvoDescription), MethodType.Getter)]
        [HarmonyPrefix]
        public static bool CardParameter_EvoDescription_Get(ref CardParameter __instance, ref string __result)
        {
            if (RuntimeCardTexts.TryGetValue(__instance, out RuntimeCardText runtimeText))
            {
                __result = runtimeText.EvoDescription;
                return false;
            }

            var id = __instance.CardId;
            var key = $"{id}_EvoDescription";
            if (CustomLocalization.TryGetValue(key, out string result))
            {

                __result = result;
                return false;
            }
            return true;
        }


        public static void BackupCardMaster(CardMaster master)
        {
            Plugin.Logger.LogDebug("Backup Current CardMaster");
            IDictionary<int, CardParameter> masterDict = (IDictionary<int, CardParameter>)AccessTools.Field(typeof(CardMaster), "m_cardParameters").GetValue(master);
            CardParameterBackup.Clear();
            foreach (var kvp in masterDict)
            {
                CardParameterBackup.Add(kvp.Key, kvp.Value.Clone());
            }
        }
        public static void RevokeCardMasterPatches(CardMaster master = null)
        {
            Plugin.Logger.LogDebug("Revoke CardMaster mods");
            master ??= CardMaster.GetInstanceForBattle();
            IDictionary<int, CardParameter> masterDict = (IDictionary<int, CardParameter>)AccessTools.Field(typeof(CardMaster), "m_cardParameters").GetValue(master);
            masterDict.Clear();
            CustomLocalization.Clear();
            ClearFoilEffectRegistry();
            LocalCardVoicePatches.Clear();
            ExtraVoiceAcbPathsByCardId.Clear();
            LoadedExtraVoiceAcbs.Clear();
            ArtSlotsByTargetResourceId.Clear();
            ArtSlotTargetBundleTypes.Clear();
            RequestedArtSourceBundles.Clear();
            ArtSourceBundlesLoadedOnDemand.Clear();
            ArtSourceBundlesRequestedInGroup = false;
            // The registries above are rebuilt from the patch files, so a resource that
            // was disabled by an earlier conflict has to become eligible again - that is
            // what makes fixing the JSON and reopening the deck list actually take effect.
            DisabledArtSlotResourceIds.Clear();
            ArtSlotWarnings.Clear();
            foreach (var kvp in CardParameterBackup)
            {
                masterDict.Add(kvp.Key,kvp.Value.Clone());
            }
        }

        private static void ClearFoilEffectRegistry()
        {
            FoilEffectsByTargetResourceId.Clear();
            SourceBundleResourcesByTargetBundleResourceId.Clear();
            TargetBundleMaterialTypes.Clear();
            DisabledFoilEffectResourceIds.Clear();
            FoilEffectWarnings.Clear();
            GeneratedCardMaterials.Clear();
        }

        private static void WarnFoilEffectOnce(string key, string message)
        {
            if (FoilEffectWarnings.Add(key))
            {
                Plugin.Logger.LogWarning(message);
            }
        }

        // Turns the patch's extraVoiceIds into the voice bank paths that have to
        // be loaded on top of the card's own bank.
        // The in-play card mesh frames the artwork with the card's own texture tiling and
        // offset: UnitCardCreator and FieldCardCreator both push CardParameter.NormalTilling
        // and NormalOffset onto the artwork material as mainTextureScale/mainTextureOffset.
        // Those values are authored per card so that its own artwork sits correctly inside
        // the card frame. A card that borrows another card's face therefore has to borrow
        // these values as well - keeping the template's ones frames a different artwork and
        // shows only a magnified piece of the borrowed one.
        private static void InheritArtworkFraming(
            CardParameter target,
            CardParameterPatch patch,
            CardMaster master)
        {
            if (target == null || patch == null || master == null)
            {
                return;
            }

            CardParameter normalSource = ResolveArtSourceParameter(master, patch.normalArtCardId);
            if (normalSource != null)
            {
                // A cross-stage borrow takes its framing from the source's other stage.
                bool fromEvolved = patch.normalArtFromEvolved ?? false;
                target.NormalTilling = fromEvolved ? normalSource.EvolTilling : normalSource.NormalTilling;
                target.NormalOffset = fromEvolved ? normalSource.EvolOffset : normalSource.NormalOffset;
            }

            CardParameter evolutionSource = ResolveArtSourceParameter(master, patch.evolutionArtCardId) ?? normalSource;
            if (evolutionSource != null)
            {
                bool fromEvolved = patch.evolutionArtFromEvolved ?? true;
                target.EvolTilling = fromEvolved ? evolutionSource.EvolTilling : evolutionSource.NormalTilling;
                target.EvolOffset = fromEvolved ? evolutionSource.EvolOffset : evolutionSource.NormalOffset;
            }
        }

        private static CardParameter ResolveArtSourceParameter(CardMaster master, int? cardId)
        {
            if (!cardId.HasValue || cardId.Value <= 0)
            {
                return null;
            }

            return master.GetCardParameterFromId(cardId.Value);
        }

        private static void RegisterExtraVoices(int cardId, CardParameterPatch patch)
        {
            if (patch.extraVoiceIds == null || patch.extraVoiceIds.Length == 0 || cardId <= 0)
            {
                return;
            }

            List<string> paths = null;
            foreach (string voiceId in patch.extraVoiceIds)
            {
                if (string.IsNullOrEmpty(voiceId))
                {
                    continue;
                }

                // The game derives the bank name by cutting the voice id at the
                // first '_' (VoiceDictionaries.GetVoiceIDBeforeUnderBar), so
                // "125641030_4" and "125641030" both mean v/vo_125641030.acb.
                string bankId = voiceId.Trim().Split('_')[0];
                if (bankId.Length == 0)
                {
                    Plugin.Logger.LogWarning(
                        $"card {cardId} has an unusable extra voice id '{voiceId}'");
                    continue;
                }

                string path = "v/vo_" + bankId + ".acb";
                paths ??= [];
                if (!paths.Contains(path))
                {
                    paths.Add(path);
                }
            }

            if (paths == null)
            {
                return;
            }

            if (!ExtraVoiceAcbPathsByCardId.TryGetValue(cardId, out List<string> registered))
            {
                ExtraVoiceAcbPathsByCardId[cardId] = paths;
                Plugin.Logger.LogDebug(
                    $"card {cardId} gets extra voice bank(s): {string.Join(", ", paths)}");
                return;
            }

            foreach (string path in paths)
            {
                if (!registered.Contains(path))
                {
                    registered.Add(path);
                    Plugin.Logger.LogDebug($"card {cardId} gets extra voice bank: {path}");
                }
            }
        }

        // Runs on every voice use of a card. Only the first call per bank does
        // any work, and when no card declares extraVoiceIds the registry is empty
        // and this returns immediately.
        private static void EnsureExtraVoicesLoaded(int cardId)
        {
            if (cardId <= 0 || ExtraVoiceAcbPathsByCardId.Count == 0 ||
                !ExtraVoiceAcbPathsByCardId.TryGetValue(cardId, out List<string> paths))
            {
                return;
            }

            List<string> pending = null;
            foreach (string path in paths)
            {
                if (LoadedExtraVoiceAcbs.Add(path))
                {
                    pending ??= [];
                    pending.Add(path);
                }
            }

            if (pending == null)
            {
                return;
            }

            try
            {
                ResourcesManager resourcesManager = Toolbox.ResourcesManager;
                if (resourcesManager == null)
                {
                    foreach (string path in pending)
                    {
                        LoadedExtraVoiceAcbs.Remove(path);
                    }

                    return;
                }

                // Same call the game's own WaitLoadVoiceResourceVfx uses, so the
                // bank lands in the same atom cue sheet the card plays from.
                resourcesManager.StartCoroutine_LoadAssetGroupAsync(pending, null, true);
                Plugin.Logger.LogDebug(
                    $"loading extra voice bank(s) for card {cardId}: {string.Join(", ", pending)}");
            }
            catch (Exception e)
            {
                foreach (string path in pending)
                {
                    LoadedExtraVoiceAcbs.Remove(path);
                }

                Plugin.Logger.LogWarning(
                    $"failed to load extra voice bank(s) for card {cardId}: {e.Message}");
            }
        }

        private static bool UsesUnitCardMaterial(CardParameter parameter)
        {
            return parameter != null &&
                (parameter.CharType == CardBasePrm.CharaType.NORMAL ||
                 CardMaster.IsMutationCardCheck(parameter.BaseCardId));
        }

        private static void BuildFoilEffectRegistry(
            CardMaster master,
            IEnumerable<FoilEffectRequest> requests)
        {
            foreach (FoilEffectRequest request in requests)
            {
                if (request.SourceCardId <= 0)
                {
                    WarnFoilEffectOnce(
                        $"invalid-source:{request.TargetCardId}",
                        $"foilEffectCardId for {request.TargetCardId} must be a positive CardId");
                    continue;
                }

                CardParameter target = master.GetCardParameterFromId(request.TargetCardId);
                if (target == null)
                {
                    WarnFoilEffectOnce(
                        $"missing-target:{request.TargetCardId}",
                        $"foilEffectCardId target card {request.TargetCardId} was not found");
                    continue;
                }

                if (!target.IsFoil)
                {
                    WarnFoilEffectOnce(
                        $"nonfoil-target:{target.CardId}",
                        $"foilEffectCardId on non-foil card {target.CardId} is ignored");
                    continue;
                }

                if (DisabledFoilEffectResourceIds.Contains(target.ResourceCardId))
                {
                    continue;
                }

                if (!CardParameterBackup.ContainsKey(request.SourceCardId))
                {
                    WarnFoilEffectOnce(
                        $"custom-source:{request.SourceCardId}",
                        $"foilEffectCardId {request.SourceCardId} for {target.CardId} must reference an original game card");
                    continue;
                }

                CardParameter sourceReference = CardParameterBackup[request.SourceCardId];
                CardParameter sourceFoil = CardParameterBackup.TryGetValue(
                    sourceReference.FoilCardId,
                    out CardParameter originalFoil)
                    ? originalFoil
                    : null;
                if (sourceFoil == null || !sourceFoil.IsFoil ||
                    !CardParameterBackup.ContainsKey(sourceFoil.CardId))
                {
                    WarnFoilEffectOnce(
                        $"invalid-source-foil:{request.SourceCardId}",
                        $"foilEffectCardId {request.SourceCardId} for {target.CardId} does not resolve to an original foil card");
                    continue;
                }

                if (UsesUnitCardMaterial(target) != UsesUnitCardMaterial(sourceFoil))
                {
                    WarnFoilEffectOnce(
                        $"type-mismatch:{target.CardId}:{sourceFoil.CardId}",
                        $"foilEffectCardId {sourceFoil.CardId} is incompatible with {target.CardId}: follower and spell/amulet materials cannot be mixed");
                    continue;
                }

                CardParameter sourceNormal = CardParameterBackup.TryGetValue(
                    sourceFoil.NormalCardId,
                    out CardParameter originalNormal)
                    ? originalNormal
                    : null;
                if (sourceNormal == null)
                {
                    WarnFoilEffectOnce(
                        $"missing-source-normal:{sourceFoil.CardId}",
                        $"foil effect source {sourceFoil.CardId} has no valid normal-card resource record");
                    continue;
                }

                CardParameter targetNormal = master.GetCardParameterFromId(target.NormalCardId) ?? target;
                if (target.NormalCardId != target.CardId &&
                    targetNormal.ResourceCardId == target.ResourceCardId)
                {
                    WarnFoilEffectOnce(
                        $"shared-target-resource:{target.ResourceCardId}",
                        $"foil effect for {target.CardId} is disabled because its normal and foil records share ResourceCardId {target.ResourceCardId}; use distinct resource IDs to keep the effect foil-only");
                    continue;
                }

                FoilEffectBinding binding = new FoilEffectBinding
                {
                    TargetFoilCardId = target.CardId,
                    TargetResourceCardId = target.ResourceCardId,
                    SourceFoilCardId = sourceFoil.CardId,
                    SourceFoilResourceCardId = sourceFoil.ResourceCardId,
                    SourceNormalResourceCardId = sourceNormal.ResourceCardId
                };

                if (FoilEffectsByTargetResourceId.TryGetValue(target.ResourceCardId, out FoilEffectBinding existing) &&
                    existing.SourceFoilCardId != binding.SourceFoilCardId)
                {
                    FoilEffectsByTargetResourceId.Remove(target.ResourceCardId);
                    DisabledFoilEffectResourceIds.Add(target.ResourceCardId);
                    WarnFoilEffectOnce(
                        $"resource-conflict:{target.ResourceCardId}",
                        $"multiple foil effects target ResourceCardId {target.ResourceCardId}; the effect override is disabled for this shared resource");
                    continue;
                }

                FoilEffectsByTargetResourceId[target.ResourceCardId] = binding;
                ResourcesManager.AssetLoadPathType materialType = UsesUnitCardMaterial(target)
                    ? ResourcesManager.AssetLoadPathType.UnitCardMaterial
                    : ResourcesManager.AssetLoadPathType.SpellCardMaterial;
                AddSourceBundleMapping(
                    targetNormal.ResourceCardId,
                    materialType,
                    sourceNormal.ResourceCardId,
                    sourceFoil.ResourceCardId);
                if (target.ResourceCardId != targetNormal.ResourceCardId)
                {
                    AddSourceBundleMapping(
                        target.ResourceCardId,
                        materialType,
                        sourceNormal.ResourceCardId,
                        sourceFoil.ResourceCardId);
                }
            }
        }

        // Resolves one declared art slot into the resource id and the bundle that
        // resource id lives in. A slot value is a card id from the original game;
        // when that card is unknown the number is used as resource id directly, so a
        // raw resource id works as well.
        private static bool TryResolveArtSource(
            CardMaster master,
            int declaredCardId,
            out int resourceCardId,
            out ResourcesManager.AssetLoadPathType bundleType)
        {
            resourceCardId = 0;
            bundleType = ResourcesManager.AssetLoadPathType.UnitCardMaterial;
            if (declaredCardId == 0)
            {
                return false;
            }

            CardParameter source = master.GetCardParameterFromId(declaredCardId);
            resourceCardId = source?.ResourceCardId ?? declaredCardId;
            if (resourceCardId <= 0)
            {
                return false;
            }

            bundleType = source != null && UsesUnitCardMaterial(source)
                ? ResourcesManager.AssetLoadPathType.UnitCardMaterial
                : ResourcesManager.AssetLoadPathType.SpellCardMaterial;
            return true;
        }

        private static void BuildArtSlotRegistry(
            CardMaster master,
            IEnumerable<ArtSlotRequest> requests)
        {
            foreach (ArtSlotRequest request in requests)
            {
                CardParameter target = master.GetCardParameterFromId(request.CardId);
                if (target == null)
                {
                    continue;
                }

                int targetResourceCardId = target.ResourceCardId;
                if (targetResourceCardId <= 0 ||
                    DisabledArtSlotResourceIds.Contains(targetResourceCardId))
                {
                    continue;
                }

                var binding = new ArtSlotBinding
                {
                    TargetResourceCardId = targetResourceCardId,
                    OwnerCardId = request.CardId
                };
                // Absent flags keep the original one-to-one behaviour: the normal slot
                // takes the source's normal face, the evolved slot takes its evolved
                // face. Setting a flag moves that slot to the source's other stage.
                binding.NormalSourceEvolved = request.Patch.normalArtFromEvolved ?? false;
                binding.EvolutionSourceEvolved = request.Patch.evolutionArtFromEvolved ?? true;
                binding.SpellSourceEvolved = request.Patch.spellArtFromEvolved ?? false;
                TryResolveArtSource(
                    master,
                    request.Patch.normalArtCardId.GetValueOrDefault(),
                    out binding.NormalResourceCardId,
                    out binding.NormalBundleType);
                TryResolveArtSource(
                    master,
                    request.Patch.evolutionArtCardId.GetValueOrDefault(),
                    out binding.EvolutionResourceCardId,
                    out binding.EvolutionBundleType);
                TryResolveArtSource(
                    master,
                    request.Patch.spellArtCardId.GetValueOrDefault(),
                    out binding.SpellResourceCardId,
                    out binding.SpellBundleType);

                if (ArtSlotsByTargetResourceId.TryGetValue(
                        targetResourceCardId,
                        out ArtSlotBinding existing))
                {
                    if (SameArtSlots(existing, binding))
                    {
                        continue;
                    }

                    // Two cards sharing one resource id want different faces. Any
                    // choice would show the wrong artwork on the other card, so the
                    // override is dropped and both keep the engine's own artwork.
                    ArtSlotsByTargetResourceId.Remove(targetResourceCardId);
                    ArtSlotTargetBundleTypes.Remove(targetResourceCardId);
                    DisabledArtSlotResourceIds.Add(targetResourceCardId);
                    WarnArtSlotOnce(
                        $"resource-conflict:{targetResourceCardId}",
                        $"resource id {targetResourceCardId} is used by several cards with different artwork slots; the artwork override is disabled for this shared resource (give each new card its own ResourceCardId or drop intFields.ResourceCardId)");
                    continue;
                }

                ArtSlotsByTargetResourceId[targetResourceCardId] = binding;
                ArtSlotTargetBundleTypes[targetResourceCardId] = UsesUnitCardMaterial(target)
                    ? ResourcesManager.AssetLoadPathType.UnitCardMaterial
                    : ResourcesManager.AssetLoadPathType.SpellCardMaterial;
                Plugin.Logger.LogDebug(
                    $"card {request.CardId} (resource {targetResourceCardId}) artwork slots: " +
                    $"normal={DescribeArtSlot(binding.NormalResourceCardId, binding.NormalBundleType)}, " +
                    $"evolution={DescribeArtSlot(binding.EvolutionResourceCardId, binding.EvolutionBundleType)}, " +
                    $"spell={DescribeArtSlot(binding.SpellResourceCardId, binding.SpellBundleType)}");
            }
        }

        private static bool SameArtSlots(ArtSlotBinding left, ArtSlotBinding right)
        {
            return left.NormalResourceCardId == right.NormalResourceCardId &&
                   left.NormalBundleType == right.NormalBundleType &&
                   left.NormalSourceEvolved == right.NormalSourceEvolved &&
                   left.EvolutionResourceCardId == right.EvolutionResourceCardId &&
                   left.EvolutionBundleType == right.EvolutionBundleType &&
                   left.EvolutionSourceEvolved == right.EvolutionSourceEvolved &&
                   left.SpellResourceCardId == right.SpellResourceCardId &&
                   left.SpellBundleType == right.SpellBundleType &&
                   left.SpellSourceEvolved == right.SpellSourceEvolved;
        }

        private static string DescribeArtSlot(
            int resourceCardId,
            ResourcesManager.AssetLoadPathType bundleType)
        {
            return resourceCardId == 0 ? "engine" : $"{resourceCardId}@{bundleType}";
        }

        private static void WarnArtSlotOnce(string key, string message)
        {
            if (ArtSlotWarnings.Add(key))
            {
                Plugin.Logger.LogWarning(message);
            }
        }

        // Picks the declared source for the slot the engine is asking for.
        // A missing slot falls back to the natural sibling: an evolved face without
        // its own source uses the normal source's evolved artwork.
        private static bool TryGetArtSlot(
            int targetResourceCardId,
            ResourcesManager.AssetLoadPathType type,
            bool isEvolution,
            out int resourceCardId,
            out ResourcesManager.AssetLoadPathType bundleType,
            out bool sourceIsEvolution)
        {
            resourceCardId = 0;
            bundleType = type;
            sourceIsEvolution = isEvolution;
            if (!ArtSlotsByTargetResourceId.TryGetValue(
                    targetResourceCardId,
                    out ArtSlotBinding binding))
            {
                return false;
            }

            if (type == ResourcesManager.AssetLoadPathType.SpellCardMaterial)
            {
                if (binding.SpellResourceCardId != 0)
                {
                    resourceCardId = binding.SpellResourceCardId;
                    bundleType = binding.SpellBundleType;
                    sourceIsEvolution = binding.SpellSourceEvolved;
                    return true;
                }

                if (binding.NormalResourceCardId != 0)
                {
                    resourceCardId = binding.NormalResourceCardId;
                    bundleType = binding.NormalBundleType;
                    sourceIsEvolution = binding.NormalSourceEvolved;
                    return true;
                }

                if (isEvolution && binding.EvolutionResourceCardId != 0)
                {
                    resourceCardId = binding.EvolutionResourceCardId;
                    bundleType = binding.EvolutionBundleType;
                    sourceIsEvolution = binding.EvolutionSourceEvolved;
                    return true;
                }

                return false;
            }

            if (isEvolution)
            {
                if (binding.EvolutionResourceCardId != 0)
                {
                    resourceCardId = binding.EvolutionResourceCardId;
                    bundleType = binding.EvolutionBundleType;
                    sourceIsEvolution = binding.EvolutionSourceEvolved;
                    return true;
                }

                if (binding.NormalResourceCardId != 0)
                {
                    resourceCardId = binding.NormalResourceCardId;
                    bundleType = binding.NormalBundleType;
                    sourceIsEvolution = binding.NormalSourceEvolved;
                    return true;
                }

                if (binding.SpellResourceCardId != 0)
                {
                    resourceCardId = binding.SpellResourceCardId;
                    bundleType = binding.SpellBundleType;
                    sourceIsEvolution = binding.SpellSourceEvolved;
                    return true;
                }

                return false;
            }

            if (binding.NormalResourceCardId != 0)
            {
                resourceCardId = binding.NormalResourceCardId;
                bundleType = binding.NormalBundleType;
                sourceIsEvolution = binding.NormalSourceEvolved;
                return true;
            }

            if (binding.SpellResourceCardId != 0)
            {
                resourceCardId = binding.SpellResourceCardId;
                bundleType = binding.SpellBundleType;
                sourceIsEvolution = binding.SpellSourceEvolved;
                return true;
            }

            return false;
        }

        // Fetches the borrowed face. It goes back into the very same
        // FindCardMaterial the game uses, so whatever the source card's own face is
        // (follower, spell, amulet, evolved, another bundle) is returned as is.
        private static Material GetArtSlotMaterial(
            int resourceCardId,
            ResourcesManager.AssetLoadPathType bundleType,
            bool isEvolution,
            bool isMutation,
            CardBasePrm.CharaType originalType,
            bool isChoiceBrave)
        {
            if (resourceCardId == 0 || Toolbox.ResourcesManager == null)
            {
                return null;
            }

            Material material = LookupArtSlotMaterial(
                resourceCardId,
                bundleType,
                isEvolution,
                isMutation,
                originalType,
                isChoiceBrave);

            // A mod card owns a resource id that no bundle contains, so the game never
            // asks for that bundle and the borrowed source bundle can stay unloaded.
            // The lookup then returns null and the face renders blank until something
            // else happens to pull the bundle in. Bring it in here and retry once, so
            // the very first render already has the face.
            if (material == null &&
                TryLoadArtSourceBundleOnDemand(resourceCardId, bundleType))
            {
                material = LookupArtSlotMaterial(
                    resourceCardId,
                    bundleType,
                    isEvolution,
                    isMutation,
                    originalType,
                    isChoiceBrave);
            }

            if (material == null)
            {
                WarnArtSlotOnce(
                    $"material-unavailable:{resourceCardId}:{bundleType}:{isEvolution}",
                    $"artwork source {resourceCardId} ({bundleType}) is unavailable; the engine artwork is used instead");
            }

            return material;
        }

        private static Material LookupArtSlotMaterial(
            int resourceCardId,
            ResourcesManager.AssetLoadPathType bundleType,
            bool isEvolution,
            bool isMutation,
            CardBasePrm.CharaType originalType,
            bool isChoiceBrave)
        {
            try
            {
                IsResolvingFoilEffectTemplate = true;
                return Toolbox.ResourcesManager.FindCardMaterial(
                    resourceCardId,
                    bundleType,
                    isEvolution,
                    isMutation,
                    originalType,
                    isChoiceBrave);
            }
            finally
            {
                IsResolvingFoilEffectTemplate = false;
            }
        }

        // Loads one borrowed-art bundle on the spot. Returns true when the caller
        // should retry the lookup. Each bundle is attempted once per CardMaster
        // generation, so a genuinely missing bundle does not spin.
        private static bool TryLoadArtSourceBundleOnDemand(
            int resourceCardId,
            ResourcesManager.AssetLoadPathType bundleType)
        {
            ResourcesManager resourcesManager = Toolbox.ResourcesManager;
            if (resourcesManager == null || IsLoadingArtSourceBundle)
            {
                return false;
            }

            // ResourcesManager is a component of the object named UIManager, and Unity
            // refuses to start a coroutine on a component whose object is inactive - it
            // only logs "Coroutine couldn't be started because the game object 'UIManager'
            // is inactive!". Nothing can be loaded in that state anyway, so stay quiet.
            if (!resourcesManager.gameObject.activeInHierarchy)
            {
                return false;
            }

            string bundle;
            try
            {
                bundle = resourcesManager.GetAssetTypePath(
                    resourceCardId.ToString(),
                    bundleType,
                    false);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning(
                    $"could not resolve the bundle of artwork source {resourceCardId}: {e.Message}");
                return false;
            }

            if (string.IsNullOrEmpty(bundle) || !ArtSourceBundlesLoadedOnDemand.Add(bundle))
            {
                return false;
            }

            try
            {
                if (resourcesManager.IsLoadedAssetBundle(bundle))
                {
                    return false;
                }
            }
            catch (Exception)
            {
                // The probe is only an optimisation; fall through and try to load.
            }

            try
            {
                IsLoadingArtSourceBundle = true;
                resourcesManager.StartCoroutine_LoadAssetGroupSync(
                    new List<string> { bundle },
                    null,
                    true);
                Plugin.Logger.LogDebug($"loading borrowed artwork bundle on demand: {bundle}");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning(
                    $"failed to load borrowed artwork bundle {bundle}: {e.Message}");
                return false;
            }
            finally
            {
                IsLoadingArtSourceBundle = false;
            }
        }

        private static void AddArtSourceBundlesToLoadList(
            ResourcesManager resourcesManager,
            HashSet<string> loaded,
            List<string> expanded,
            int targetResourceCardId)
        {
            if (!ArtSlotsByTargetResourceId.TryGetValue(
                    targetResourceCardId,
                    out ArtSlotBinding binding))
            {
                return;
            }

            AddArtSourceBundle(
                resourcesManager,
                loaded,
                expanded,
                binding.NormalResourceCardId,
                binding.NormalBundleType);
            AddArtSourceBundle(
                resourcesManager,
                loaded,
                expanded,
                binding.EvolutionResourceCardId,
                binding.EvolutionBundleType);
            AddArtSourceBundle(
                resourcesManager,
                loaded,
                expanded,
                binding.SpellResourceCardId,
                binding.SpellBundleType);
        }

        private static void AddArtSourceBundle(
            ResourcesManager resourcesManager,
            HashSet<string> loaded,
            List<string> expanded,
            int resourceCardId,
            ResourcesManager.AssetLoadPathType bundleType)
        {
            if (resourceCardId == 0)
            {
                return;
            }

            string bundle = resourcesManager.GetAssetTypePath(
                resourceCardId.ToString(),
                bundleType,
                false);
            if (string.IsNullOrEmpty(bundle) || !loaded.Add(bundle))
            {
                return;
            }

            expanded.Add(bundle);
            RequestedArtSourceBundles.Add(bundle);
            Plugin.Logger.LogDebug(
                $"loading borrowed artwork bundle for resource {resourceCardId}: {bundle}");
        }

        private static void AddSourceBundleMapping(
            int targetResourceCardId,
            ResourcesManager.AssetLoadPathType materialType,
            int sourceNormalResourceCardId,
            int sourceFoilResourceCardId)
        {
            if (!SourceBundleResourcesByTargetBundleResourceId.TryGetValue(
                    targetResourceCardId,
                    out HashSet<int> sourceBundles))
            {
                sourceBundles = [];
                SourceBundleResourcesByTargetBundleResourceId.Add(
                    targetResourceCardId,
                    sourceBundles);
            }
            sourceBundles.Add(sourceNormalResourceCardId);
            sourceBundles.Add(sourceFoilResourceCardId);
            TargetBundleMaterialTypes[targetResourceCardId] = materialType;
        }
        public static void ApplyCardMasterPatches(CardMaster master = null)
        {
            Plugin.Logger.LogInfo("[Begin apply CardMaster mods]");
            // 逐卡的细节走 Debug，Info 只留一条汇总，免得每次打开卡组列表都刷几百行。
            int newCardCount = 0;
            int foilCompanionCount = 0;
            int patchedVariantCount = 0;
            int skippedCardCount = 0;
            master ??= CardMaster.GetInstanceForBattle();  
            RevokeCardMasterPatches(master);
            ModCardAssets.Reset();
            Dictionary<int, CardParameter> masterDict = (Dictionary<int, CardParameter>)AccessTools.Field(typeof(CardMaster), "m_cardParameters").GetValue(master);
            var card_master_folder = Directory.CreateDirectory(Plugin.CardMasterPath);
            // 每个含 json 的一级子文件夹就是一张卡，json、卡图、语音都在里面。
            // 先扫卡文件夹再扫根目录：两张卡抢同一个卡号时，新结构优先，
            // 根目录里那条旧记录会被跳过并告警。只扫一层，这样 Reference
            // 之类的资料目录不会被当成卡。
            var patches = new List<FileInfo>();
            foreach (DirectoryInfo cardFolder in card_master_folder.GetDirectories())
            {
                patches.AddRange(cardFolder.GetFiles("*.json"));
            }
            patches.AddRange(card_master_folder.GetFiles("*.json"));
            List<FoilEffectRequest> foilEffectRequests = [];
            List<ArtSlotRequest> artSlotRequests = [];

            // Every card id the patch files ask for. The automatic foil companion must
            // never steal an id that another record already claims, so explicitly
            // written foil records always win over a generated one.
            HashSet<int> requestedNewCardIds = [];
            // 顺便筛掉解析不了的文件。主扫描里若让一个坏 json 抛出去，整批卡都会
            // 加载失败（所有对战模式一起受影响），所以坏文件在这里点名跳过，其余照常。
            List<FileInfo> readablePatches = new List<FileInfo>();
            foreach (var declaredFile in patches)
            {
                try
                {
                    foreach (CardParameterPatch declared in JsonConvert.DeserializeObject<List<CardParameterPatch>>(
                        File.ReadAllText(declaredFile.FullName)) ?? [])
                    {
                        if (declared != null && declared.newCard && declared.cardId > 0)
                        {
                            requestedNewCardIds.Add(declared.cardId);
                        }
                    }

                    readablePatches.Add(declaredFile);
                }
                catch (Exception exception)
                {
                    Plugin.Logger.LogError(
                        $"CardMaster patch file {declaredFile.Name} is not a valid patch array and is skipped: " +
                        exception.Message);
                }
            }
            foreach (var pat in readablePatches)
            {
                string json = File.ReadAllText(pat.FullName);
                string cardFolder = GetCardFolder(pat, card_master_folder);
                List<CardParameterPatch> card_patches = JsonConvert.DeserializeObject<List<CardParameterPatch>>(json);
                if (card_patches == null)
                {
                    Plugin.Logger.LogWarning($"CardMaster patch file {pat.Name} is not a JSON array");
                    continue;
                }
                foreach (var patch in card_patches)
                {
                    if (patch == null)
                    {
                        Plugin.Logger.LogWarning($"CardMaster patch file {pat.Name} contains a null patch entry");
                        continue;
                    }
                    var template = master.GetCardParameterFromId(patch.templateCardId);
                    if (template == null)
                    {
                        Plugin.Logger.LogWarning($"template card {patch.templateCardId} not found");
                    }
                    else if (!patch.newCard)
                    {
                        Plugin.Logger.LogDebug($"patching card {template.CardId}");
                        HashSet<int> variantIds = new HashSet<int>
                        {
                            template.CardId,
                            template.NormalCardId,
                            template.FoilCardId
                        };
                        foreach (int variantId in variantIds)
                        {
                            CardParameter variant = master.GetCardParameterFromId(variantId);
                            if (variant == null)
                            {
                                Plugin.Logger.LogWarning(
                                    $"related card version {variantId} for {template.CardId} not found");
                                continue;
                            }

                            patch.PatchTemplate(variant, preserveVariantIdentity: true);
                            LocalCardVoicePatches.ApplyVoiceFiles(
                                variant,
                                patch.voiceFiles,
                                string.Format("{0}:{1}", pat.Name, variant.CardId),
                                cardFolder);
                            ModCardAssets.Register(
                                variant.ResourceCardId,
                                cardFolder,
                                patch.imageFiles?.normal,
                                patch.imageFiles?.evolved);
                            patchedVariantCount++;
                            RegisterExtraVoices(variant.CardId, patch);
                            InheritArtworkFraming(variant, patch, master);
                            if (ArtSlotRequest.DeclaresAnySlot(patch))
                            {
                                artSlotRequests.Add(new ArtSlotRequest
                                {
                                    CardId = variant.CardId,
                                    Patch = patch
                                });
                            }
                            // 闪光特效只对闪卡记录有意义。非闪卡的那几条变体不登记，
                            // 免得刷一堆 "foilEffectCardId on non-foil card ... is ignored"；
                            // 同一份声明会在模板的闪卡变体（或自动配对的闪卡）上生效。
                            if (patch.foilEffectCardId.HasValue && variant.IsFoil)
                            {
                                foilEffectRequests.Add(new FoilEffectRequest
                                {
                                    TargetCardId = variant.CardId,
                                    SourceCardId = patch.foilEffectCardId.Value
                                });
                            }
                        }
                    }
                    else
                    {
                        Plugin.Logger.LogDebug($"adding new card {patch.cardId} with template: {template.CardId}");
                        if (masterDict.ContainsKey(patch.cardId))
                        {
                            skippedCardCount++;
                            Plugin.Logger.LogWarning($"card {patch.cardId} already exists, skipping");
                        }
                        else
                        {
                            var newCard = CardParameterCloner.DeepClone(template);

                            // Battle image refreshes (evolve, recovery and return-to-hand)
                            // resolve the card through BaseParameter.CardId. A cloned card
                            // must not keep the template's internal identity even though it
                            // is inserted into the master dictionary under patch.cardId.
                            // Set it before PatchTemplate so localizationFields are also
                            // registered under the new card's ID.
                            newCard.CardId = patch.cardId;
                            patch.PatchTemplate(newCard);
                            LocalCardVoicePatches.ApplyVoiceFiles(
                                newCard,
                                patch.voiceFiles,
                                string.Format("{0}:{1}", pat.Name, newCard.CardId),
                                cardFolder);

                            // A new card that borrows artwork per slot keeps a resource
                            // id of its own, so those overrides can never leak into an
                            // original card that happens to share the template's
                            // resource id. The card then has no bundle of its own and
                            // its faces come entirely from the declared slots.
                            if (!HasExplicitIntField(patch, nameof(CardParameter.ResourceCardId)) &&
                                ArtSlotRequest.DeclaresAnySlot(patch))
                            {
                                newCard.ResourceCardId = patch.cardId;
                            }
                            if (HasExplicitIntField(patch, nameof(CardParameter.CardId)) &&
                                newCard.CardId != patch.cardId)
                            {
                                Plugin.Logger.LogWarning(
                                    $"new card {patch.cardId} ignores intFields.CardId={newCard.CardId}; " +
                                    "the card's internal CardId must match its master key");
                            }

                            newCard.CardId = patch.cardId;
                            if (!HasExplicitIntField(patch, nameof(CardParameter.BaseCardId)))
                            {
                                newCard.BaseCardId = patch.cardId;
                            }

                            if (!HasExplicitIntField(patch, nameof(CardParameter.NormalCardId)))
                            {
                                newCard.NormalCardId = patch.cardId;
                            }

                            if (!HasExplicitIntField(patch, nameof(CardParameter.FoilCardId)))
                            {
                                newCard.FoilCardId = patch.cardId;
                            }

                            masterDict.Add(patch.cardId, newCard);
                            newCardCount++;
                            // 登记在这张卡自己的文件夹上，卡图与语音就能按资源卡号找回文件夹。
                            // 必须在 TryAddFoilCompanion 之前：它会用 HasExternalTexture 判断
                            // 闪卡要不要共用普通卡的资源卡号。
                            ModCardAssets.Register(
                                newCard.ResourceCardId,
                                cardFolder,
                                patch.imageFiles?.normal,
                                patch.imageFiles?.evolved);
                            RegisterExtraVoices(patch.cardId, patch);
                            InheritArtworkFraming(newCard, patch, master);
                            if (ArtSlotRequest.DeclaresAnySlot(patch))
                            {
                                artSlotRequests.Add(new ArtSlotRequest
                                {
                                    CardId = patch.cardId,
                                    Patch = patch
                                });
                            }
                            // 同上：只有本记录本身是闪卡时才登记，普通版交给自动配对的闪卡。
                            if (patch.foilEffectCardId.HasValue && newCard.IsFoil)
                            {
                                foilEffectRequests.Add(new FoilEffectRequest
                                {
                                    TargetCardId = newCard.CardId,
                                    SourceCardId = patch.foilEffectCardId.Value
                                });
                            }

                            // Pair the new card with a foil version of itself, so a patch
                            // that only describes the normal card still yields both.
                            int foilCompanionId = TryAddFoilCompanion(
                                master,
                                masterDict,
                                requestedNewCardIds,
                                patch,
                                newCard,
                                template,
                                artSlotRequests);
                            if (foilCompanionId != 0)
                            {
                                foilCompanionCount++;
                                // 闪卡是从模板的闪卡版本克隆出来的（见 TryAddFoilCompanion），
                                // 它带着模板自己的 PlayVoice 等语音字段，没有跟着普通卡走。
                                // 不补这一步，闪卡就会去播模板原本的语音。
                                LocalCardVoicePatches.ApplyVoiceFiles(
                                    master.GetCardParameterFromId(foilCompanionId),
                                    patch.voiceFiles,
                                    string.Format("{0}:{1}", pat.Name, foilCompanionId),
                                    cardFolder);
                                ModCardAssets.Register(
                                    master.GetCardParameterFromId(foilCompanionId)?.ResourceCardId ?? 0,
                                    cardFolder,
                                    patch.imageFiles?.normal,
                                    patch.imageFiles?.evolved);
                                RegisterExtraVoices(foilCompanionId, patch);
                                InheritArtworkFraming(
                                    master.GetCardParameterFromId(foilCompanionId),
                                    patch,
                                    master);
                                if (patch.foilEffectCardId.HasValue)
                                {
                                    foilEffectRequests.Add(new FoilEffectRequest
                                    {
                                        TargetCardId = foilCompanionId,
                                        SourceCardId = patch.foilEffectCardId.Value
                                    });
                                }
                            }
                        }
                    }
                }
            }

            BuildFoilEffectRegistry(master, foilEffectRequests);
            BuildArtSlotRegistry(master, artSlotRequests);
            LocalCardVoicePatches.BeginPreload();

            // 关键词注册放在「把全卡加入本地收藏」之前：后者依赖 Data.Load，
            // 联机/房间对战时本地数据不一定就绪，不能让它把关键词一起带下去。
            RegisterCardNameKeywords(master);

            UnlockAllCardsLocally(master);
            Plugin.Logger.LogInfo(
                $"[End apply CardMaster mods] new cards: {newCardCount} (+{foilCompanionCount} foil), " +
                $"patched records: {patchedVariantCount}, skipped: {skippedCardCount}, " +
                $"patch files: {readablePatches.Count}");
        }

        /// <summary>
        /// 把 CardMaster 里的全部卡（含 mod 卡）塞进本地收藏，每张 99 张，
        /// 这样自制卡在牌组编辑里能直接拿来用。
        ///
        /// 这套本地数据只在离线/单人场景就绪，联机与房间对战时不一定存在，
        /// 所以整体兜住：拿不到就跳过，不影响已经应用好的卡牌数据。
        /// </summary>
        private static void UnlockAllCardsLocally(CardMaster master)
        {
            try
            {
                if (Data.Load == null || Data.Load.data == null || Data.Load.data.UserCardList == null)
                {
                    return;
                }

                Data.Load.data.UserCardList.Clear();
                List<int> all = master.GetAllCardIds();
                for (int i = 0; i < all.Count; i++)
                {
                    UserCard userCard = new UserCard();
                    userCard.card_id = all[i];
                    userCard.number = 99;
                    Data.Load.data.UserCardList.Add(userCard);
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    "[CardMaster] Could not refresh the local card collection; mod cards stay " +
                    "usable in battle, they just may not show up in offline deck building.\n" +
                    exception.Message);
            }
        }

        public static void RegisterCardNameKeywords(CardMaster cardMaster)
        {
            if (cardMaster == null)
            {
                return;
            }
            Master master = Data.Master;
            if (master == null)
            {
                return;
            }
            IDictionary battleKeyWordDic = (IDictionary)master.BattleKeyWordDic;
            if (battleKeyWordDic == null)
            {
                return;
            }
            IDictionary customLocalization = (IDictionary)CustomLocalization;
            if (customLocalization == null)
            {
                return;
            }
            List<int> allCardIds = cardMaster.GetAllCardIds();
            allCardIds.Sort();
            IList cardIdList = (IList)allCardIds;
            if (cardIdList == null)
            {
                return;
            }
            for (int i = 0; i < cardIdList.Count; i++)
            {
                int cardId = (int)cardIdList[i];
                string key = cardId + "_CardName";
                if (customLocalization.Contains(key))
                {
                    string cardName = (string)customLocalization[key];
                    if (!string.IsNullOrEmpty(cardName) && !battleKeyWordDic.Contains(cardName))
                    {
                        battleKeyWordDic.Add(cardName, "[card]" + cardId + "[/card]");
                    }
                }
            }
        }

        [HarmonyPatch(typeof(Master), "StartLoadBattleKeyWordText", MethodType.Normal)]
        [HarmonyPostfix]
        public static void Master_StartLoadBattleKeyWordText_Postfix()
        {
            RegisterCardNameKeywords(CardMaster.GetInstanceForBattle());
            Plugin.Logger.LogInfo("Shadowbus: card-name keywords re-registered after BattleKeyWordDic reload");
        }

        public static int CompareCardIdByCost(int x, int y)
        {
            CardMaster master = CardMaster.GetInstanceForBattle();
            if (master == null)
            {
                return x - y;
            }
            CardParameter cardParameter = master.GetCardParameterFromId(x);
            CardParameter cardParameter2 = master.GetCardParameterFromId(y);
            if (cardParameter != null && cardParameter2 != null)
            {
                int num = cardParameter.Cost - cardParameter2.Cost;
                if (num != 0)
                {
                    return num;
                }
                return cardParameter.SortIndex - cardParameter2.SortIndex;
            }
            return x - y;
        }

        public static void SortCardIdListByCost(IList cardIds)
        {
            if (cardIds == null)
            {
                return;
            }
            int count = cardIds.Count;
            for (int i = 1; i < count; i++)
            {
                int num = (int)cardIds[i];
                int j = i - 1;
                while (j >= 0 && CompareCardIdByCost((int)cardIds[j], num) > 0)
                {
                    cardIds[j + 1] = cardIds[j];
                    j--;
                }
                cardIds[j + 1] = num;
            }
        }

        [HarmonyPatch(typeof(UIBase_CardManager), "SelectAllCardIDInConditionMask", MethodType.Normal)]
        [HarmonyPostfix]
        public static void SelectAllCardIDInConditionMask_Postfix(IList<int> __result)
        {
            IList cardIds = (IList)__result;
            if (cardIds == null)
            {
                return;
            }
            SortCardIdListByCost(cardIds);
        }

        [HarmonyPatch(typeof(UIBase_CardManager), "SortIDList", MethodType.Normal)]
        [HarmonyPostfix]
        public static void SortIDList_Postfix(List<int> __result)
        {
            IList cardIds = (IList)__result;
            if (cardIds == null)
            {
                return;
            }
            SortCardIdListByCost(cardIds);
        }

        // Deck edit (the card pool with the search box) uses a THIRD path that
        // neither of the two patches above covers:
        //   UIBase_CardManager.SelectAllCardIDInConditionMask  <- only called by CardAllListUI.GetFilteringIDList
        //   UIBase_CardManager.SortIDList                      <- FilterController / DeckData
        // Deck edit instead goes through CardBundleControllerBase.GetFilteringIDList,
        // so its result stayed in raw CardId order. It is declared virtual on the base
        // class and is not overridden by CardBundleController, so patching the base
        // covers every deck-edit card pool.
        [HarmonyPatch(typeof(Wizard.DeckCardEdit.CardBundleControllerBase), nameof(Wizard.DeckCardEdit.CardBundleControllerBase.GetFilteringIDList))]
        [HarmonyPostfix]
        public static void CardBundleControllerBase_GetFilteringIDList_Postfix(List<int> __result)
        {
            IList cardIds = (IList)__result;
            if (cardIds == null)
            {
                return;
            }
            SortCardIdListByCost(cardIds);
        }

        // CardDestruct overrides GetFilteringIDList on
        // CardDestruct_CardBundleController, so the base-class patch above never
        // sees that card pool: an override carries its own method body and the
        // caller binds straight to it, bypassing the patched base implementation.
        // Patching the override too closes the last card-pool path. For any single
        // call only one of these two postfixes ever runs.
        [HarmonyPatch(typeof(Wizard.CardDestruct.CardDestruct_CardBundleController), nameof(Wizard.CardDestruct.CardDestruct_CardBundleController.GetFilteringIDList))]
        [HarmonyPostfix]
        public static void CardDestruct_CardBundleController_GetFilteringIDList_Postfix(List<int> __result)
        {
            IList cardIds = (IList)__result;
            if (cardIds == null)
            {
                return;
            }
            SortCardIdListByCost(cardIds);
        }

        // The game keeps a normal and a foil record for every card, and the foil one
        // is what carries IsFoil plus the NormalCardId/FoilCardId back-references. A
        // patch that only creates the normal record therefore leaves the card with no
        // foil version at all, so one is derived from the same patch: identical stats,
        // skills and text, cloned from the template's own foil card so it also keeps
        // the foil material and artwork.
        // Returns the companion's card id, or 0 when none was created.
        private static int TryAddFoilCompanion(
            CardMaster master,
            IDictionary<int, CardParameter> masterDict,
            ISet<int> requestedNewCardIds,
            CardParameterPatch patch,
            CardParameter normal,
            CardParameter template,
            List<ArtSlotRequest> artSlotRequests)
        {
            if (HasExplicitBoolField(patch, nameof(CardParameter.IsFoil)) &&
                patch.boolFields[nameof(CardParameter.IsFoil)])
            {
                // The patch is itself the foil record, so the pair is managed by hand.
                return 0;
            }

            int foilCardId = normal.CardId + 1;
            if (masterDict.ContainsKey(foilCardId) ||
                (requestedNewCardIds != null && requestedNewCardIds.Contains(foilCardId)))
            {
                Plugin.Logger.LogDebug(
                    $"card {normal.CardId} gets no foil companion: card id {foilCardId} is already claimed");
                return 0;
            }

            int foilTemplateId = template.FoilCardId != 0 && template.FoilCardId != template.CardId
                ? template.FoilCardId
                : template.CardId;
            CardParameter foilTemplate = master.GetCardParameterFromId(foilTemplateId) ?? template;

            CardParameter foil = CardParameterCloner.DeepClone(foilTemplate);
            // Set the id before PatchTemplate so localizationFields are registered under
            // the companion's own id as well.
            foil.CardId = foilCardId;
            patch.PatchTemplate(foil);
            foil.CardId = foilCardId;
            foil.IsFoil = true;
            foil.BaseCardId = normal.BaseCardId != 0 ? normal.BaseCardId : normal.CardId;
            foil.NormalCardId = normal.CardId;
            foil.FoilCardId = foilCardId;
            if (ArtSlotRequest.DeclaresAnySlot(patch))
            {
                // Borrowed faces are keyed by the target resource id, and a foil effect
                // is only accepted while the normal and foil records differ, so the
                // companion always takes a resource id of its own. It has to do so even
                // when the patch pinned ResourceCardId for the normal record, because
                // PatchTemplate would otherwise copy that same value onto the companion
                // and the two would share one resource.
                foil.ResourceCardId = foilCardId;
            }
            else if (!HasExplicitIntField(patch, nameof(CardParameter.ResourceCardId)) &&
                     Utils.HasExternalTexture(normal.ResourceCardId, false))
            {
                // Player artwork in Mods/CardImages is addressed by ResourceCardId,
                // so the foil companion has to share the id of the normal version.
                // Otherwise it would resolve the template's own resource id and
                // show the original card's artwork instead of the custom PNG.
                foil.ResourceCardId = normal.ResourceCardId;
            }

            if (!HasExplicitIntField(patch, nameof(CardParameter.FoilCardId)))
            {
                normal.FoilCardId = foilCardId;
            }

            masterDict.Add(foilCardId, foil);
            if (ArtSlotRequest.DeclaresAnySlot(patch))
            {
                artSlotRequests.Add(new ArtSlotRequest
                {
                    CardId = foilCardId,
                    Patch = patch
                });
            }

            Plugin.Logger.LogDebug(
                $"added foil companion {foilCardId} for new card {normal.CardId} (cloned from {foilTemplateId})");
            return foilCardId;
        }

        private static bool HasExplicitIntField(CardParameterPatch patch, string fieldName)
        {
            return patch.intFields != null && patch.intFields.ContainsKey(fieldName);
        }

        /// <summary>
        /// json 所属的那张卡的文件夹（Mods/CardMaster/&lt;卡文件夹&gt;）。
        /// json 直接放在 CardMaster 根目录时返回 null：这种卡没有自己的文件夹，
        /// 用不了本地卡图与本地语音。
        /// </summary>
        private static string GetCardFolder(FileInfo patchFile, DirectoryInfo cardMasterFolder)
        {
            DirectoryInfo parent = patchFile.Directory;
            if (parent == null)
            {
                return null;
            }

            char[] trailing = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            string parentPath = parent.FullName.TrimEnd(trailing);
            string rootPath = cardMasterFolder.FullName.TrimEnd(trailing);

            return string.Equals(parentPath, rootPath, StringComparison.OrdinalIgnoreCase)
                ? null
                : parent.FullName;
        }

        private static bool HasExplicitBoolField(CardParameterPatch patch, string fieldName)
        {
            return patch.boolFields != null && patch.boolFields.ContainsKey(fieldName);
        }

        [HarmonyPatch(typeof(Wizard.CardMaster), "CreateCardMaster")]
        [HarmonyPostfix]
        public static void CardMaster_CreateCardMaster_post(ref CardMaster __result)
        {
            
            BackupCardMaster(__result);
            ApplyCardMasterPatches(__result);
        }

        public static Material commonCardMaterial;

        [HarmonyPatch(typeof(Cute.ResourcesManager), nameof(Cute.ResourcesManager.LoadAssetGroupAsync))]
        [HarmonyPrefix]
        public static void ResourcesManager_LoadAssetGroupAsync(
            Cute.ResourcesManager __instance,
            ref List<string> rogueAssetList)
        {
            // Local card voices have no ACB behind them, so their cue sheets have to be
            // dropped from the load list before anything else looks at it.
            LocalCardVoicePatches.RemoveLocalVoiceResourcePaths(rogueAssetList);

            if (rogueAssetList == null || rogueAssetList.Count == 0 ||
                (SourceBundleResourcesByTargetBundleResourceId.Count == 0 &&
                 ArtSlotTargetBundleTypes.Count == 0))
            {
                return;
            }

            List<string> expanded = new List<string>(rogueAssetList);
            HashSet<string> loaded = new HashSet<string>(expanded, StringComparer.OrdinalIgnoreCase);

            // Borrowed card faces: the bundle of the card whose face is borrowed has
            // to be resident before its material can be fetched.
            if (ArtSlotTargetBundleTypes.Count != 0)
            {
                foreach (var artPair in ArtSlotTargetBundleTypes)
                {
                    string targetBundle = __instance.GetAssetTypePath(
                        artPair.Key.ToString(),
                        artPair.Value,
                        false);
                    // The borrowed bundle is normally pulled in only alongside the target
                    // card's own bundle. A mod card owns a resource id that no bundle
                    // contains, so that bundle is never requested and this guard alone
                    // would leave the borrowed face unloadable. Inject once regardless,
                    // so the source bundles are resident from the first card-asset load.
                    bool targetRequested =
                        !string.IsNullOrEmpty(targetBundle) && loaded.Contains(targetBundle);
                    if (targetRequested || !ArtSourceBundlesRequestedInGroup)
                    {
                        AddArtSourceBundlesToLoadList(
                            __instance,
                            loaded,
                            expanded,
                            artPair.Key);
                    }
                }

                ArtSourceBundlesRequestedInGroup = true;
            }

            foreach (var pair in SourceBundleResourcesByTargetBundleResourceId)
            {
                ResourcesManager.AssetLoadPathType materialType =
                    TargetBundleMaterialTypes.TryGetValue(
                        pair.Key,
                        out ResourcesManager.AssetLoadPathType mappedType)
                        ? mappedType
                        : ResourcesManager.AssetLoadPathType.UnitCardMaterial;
                string targetBundle = __instance.GetAssetTypePath(
                    pair.Key.ToString(),
                    materialType,
                    false);
                if (!loaded.Contains(targetBundle))
                {
                    continue;
                }

                foreach (int sourceResourceId in pair.Value)
                {
                    string sourceBundle = __instance.GetAssetTypePath(
                        sourceResourceId.ToString(),
                        materialType,
                        false);
                    if (loaded.Add(sourceBundle))
                    {
                        expanded.Add(sourceBundle);
                    }
                }
            }

            rogueAssetList = expanded;
        }

        // A card can only play cues from voice banks that are loaded, and the game
        // itself only ever loads "v/vo_<ownCardId>.acb" (WaitLoadVoiceResourceVfx
        // is handed VoiceDictionaries.VoiceId). These two hooks pull in the banks
        // declared by extraVoiceIds, which is what makes a cross-id voice audible
        // instead of silent. Both are cheap no-ops while no patch declares any
        // extra voice id.
        //
        // InitializeVoiceInfo runs from BattleCardView's constructor, i.e. as soon
        // as the card gets a view, so the bank is normally ready long before the
        // first play/attack/destroy voice.
        [HarmonyPatch(
            typeof(Wizard.Battle.View.BattleCardView),
            nameof(Wizard.Battle.View.BattleCardView.InitializeVoiceInfo))]
        [HarmonyPrefix]
        public static void BattleCardView_InitializeVoiceInfo_Prefix(int cardID)
        {
            EnsureExtraVoicesLoaded(cardID);
        }

        // Safety net for views that never went through InitializeVoiceInfo with
        // the battle CardMaster's id (replay/choice cards and similar), so the
        // banks are still requested at the moment the voice is actually set up.
        [HarmonyPatch(
            typeof(Wizard.Battle.View.Vfx.WaitLoadVoiceResourceVfx),
            MethodType.Constructor,
            new Type[] { typeof(Wizard.Battle.View.IBattleCardView), typeof(string) })]
        [HarmonyPrefix]
        public static void WaitLoadVoiceResourceVfx_Prefix(Wizard.Battle.View.IBattleCardView view)
        {
            if (view == null)
            {
                return;
            }

            var cardInfo = view.CardInfo;
            if (cardInfo == null)
            {
                return;
            }

            var baseParameter = cardInfo.BaseParameter;
            if (baseParameter == null)
            {
                return;
            }

            EnsureExtraVoicesLoaded(baseParameter.CardId);
        }

        [HarmonyPatch(typeof(Cute.ResourcesManager), nameof(Cute.ResourcesManager.FindCardMaterial))]
        [HarmonyPostfix]
        public static void ResourcesManager_FindCardMaterial(
            int cardId,
            ResourcesManager.AssetLoadPathType type,
            bool isEvol,
            bool isMutation,
            CardBasePrm.CharaType originalType,
            bool isChoiceBrave,
            ref Material __result)
        {
            if (IsResolvingFoilEffectTemplate)
            {
                return;
            }

            if (__result != null && commonCardMaterial == null)
            {
                commonCardMaterial = UnityEngine.Object.Instantiate(__result);
            }

            // Borrowed card face: every card face in the UI and in battle is resolved
            // here, so redirecting this one lookup is enough for a card to display
            // another card's artwork - including across follower/spell/amulet kinds,
            // because the source material is fetched from the bundle it really lives in.
            if (TryGetArtSlot(
                    cardId,
                    type,
                    isEvol,
                    out int artResourceCardId,
                    out ResourcesManager.AssetLoadPathType artBundleType,
                    out bool artSourceIsEvol))
            {
                Material artMaterial = GetArtSlotMaterial(
                    artResourceCardId,
                    artBundleType,
                    artSourceIsEvol,
                    isMutation,
                    originalType,
                    isChoiceBrave);
                if (artMaterial != null)
                {
                    __result = artMaterial;
                }
            }

            Material foilEffectTemplate = GetFoilEffectMaterial(
                cardId,
                type,
                isEvol,
                isMutation,
                originalType,
                isChoiceBrave);
            Material customMaterial = CreateCardMaterial(
                cardId,
                isEvol,
                __result,
                foilEffectTemplate);
            if (customMaterial != null)
            {
                __result = customMaterial;
            }

            if (__result == null || __result.mainTexture == null)
            {
                Material rescued = ResolveMissingArtwork(
                    cardId,
                    type,
                    isEvol,
                    isMutation,
                    originalType,
                    isChoiceBrave,
                    __result);
                if (rescued != null)
                {
                    __result = rescued;
                }
            }
        }

        // A card face goes black when the engine hands back no material at all, or a
        // material whose texture is missing - and the deck list asks for a card face once,
        // so a null answer stays blank until something makes the UI ask again. The bundle
        // with the artwork is simply not resident yet at that point, which is why most
        // cards recover on their own once the engine gets round to loading them and the
        // others - the alternate forms a card gains through 先谋 / 结晶 / 奥义 among them -
        // never do.
        //
        // The rescue therefore asks for the bundle itself, exactly the way the borrowed
        // face lookup does, and only falls back to the card's own identity chain (the
        // record the id belongs to, then its base card, then its normal record) when even
        // that yields nothing with a texture.
        private static Material ResolveMissingArtwork(
            int resourceCardId,
            ResourcesManager.AssetLoadPathType type,
            bool isEvolution,
            bool isMutation,
            CardBasePrm.CharaType originalType,
            bool isChoiceBrave,
            Material engineMaterial)
        {
            if (IsResolvingArtworkFallback || Toolbox.ResourcesManager == null || resourceCardId == 0)
            {
                return null;
            }

            CardParameter owner = ResolveCardParameter(resourceCardId);

            List<int> candidates = [];
            AddArtworkCandidate(candidates, resourceCardId, owner?.BaseCardId ?? 0);
            AddArtworkCandidate(candidates, resourceCardId, owner?.NormalCardId ?? 0);
            if (owner != null)
            {
                AddArtworkCandidate(candidates, resourceCardId, owner.ResourceCardId);
            }

            try
            {
                IsResolvingArtworkFallback = true;

                // The artwork may simply not be resident yet, and the caller may also be
                // asking under the wrong kind of card: a spell that borrows a follower's
                // resource id is looked up in the spell bundles, where it cannot be found.
                // Both are worth another try before giving up on this id.
                ResourcesManager.AssetLoadPathType otherType =
                    type == ResourcesManager.AssetLoadPathType.UnitCardMaterial
                        ? ResourcesManager.AssetLoadPathType.SpellCardMaterial
                        : ResourcesManager.AssetLoadPathType.UnitCardMaterial;

                List<int> idsToTry = [resourceCardId];
                idsToTry.AddRange(candidates);

                foreach (int candidate in idsToTry)
                {
                    ResourcesManager.AssetLoadPathType[] typesToTry = [type, otherType];
                    foreach (ResourcesManager.AssetLoadPathType candidateType in typesToTry)
                    {
                        Material material = FindCardMaterialSafe(
                            candidate,
                            candidateType,
                            isEvolution,
                            isMutation,
                            originalType,
                            isChoiceBrave);

                        if (material == null || material.mainTexture == null)
                        {
                            if (TryLoadArtSourceBundleOnDemand(candidate, candidateType))
                            {
                                material = FindCardMaterialSafe(
                                    candidate,
                                    candidateType,
                                    isEvolution,
                                    isMutation,
                                    originalType,
                                    isChoiceBrave);
                            }
                        }

                        if (material == null || material.mainTexture == null)
                        {
                            continue;
                        }

                        // Reaching here is routine rather than a fault: a card face is asked
                        // for once, the engine can answer it under the other kind of card,
                        // and without this the face would simply stay black.
                        return material;
                    }
                }

                return null;
            }
            finally
            {
                IsResolvingArtworkFallback = false;
            }
        }

        private static Material FindCardMaterialSafe(
            int resourceCardId,
            ResourcesManager.AssetLoadPathType type,
            bool isEvolution,
            bool isMutation,
            CardBasePrm.CharaType originalType,
            bool isChoiceBrave)
        {
            try
            {
                return Toolbox.ResourcesManager.FindCardMaterial(
                    resourceCardId,
                    type,
                    isEvolution,
                    isMutation,
                    originalType,
                    isChoiceBrave);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Collects the resource ids worth trying for a card face that came back without
        // artwork, skipping zeroes and anything that is just the id we already tried.
        private static void AddArtworkCandidate(List<int> candidates, int triedResourceId, int cardId)
        {
            if (cardId == 0 || cardId == triedResourceId || candidates.Contains(cardId))
            {
                return;
            }

            CardParameter parameter = ResolveCardParameter(cardId);
            int resourceId = parameter != null ? parameter.ResourceCardId : 0;
            if (resourceId != 0 && resourceId != triedResourceId && !candidates.Contains(resourceId))
            {
                candidates.Add(resourceId);
            }
        }

        [HarmonyPatch(typeof(BattleResourceMgr), nameof(BattleResourceMgr.LoadCardImageMaterial))]
        [HarmonyPrefix]
        public static bool BattleResourceMgr_LoadCardImageMaterial(
            int cardId,
            bool isEvolution,
            ref VfxBase __result)
        {
            int resourceCardId = ResolveResourceCardId(cardId);
            if (!Utils.HasExternalTexture(resourceCardId, isEvolution) &&
                !ArtSlotsByTargetResourceId.ContainsKey(resourceCardId))
            {
                return true;
            }

            // The original loader assumes an AssetBundle material exists and dereferences
            // null for external-only cards. The postfix below supplies the material instead.
            __result = NullVfx.GetInstance();
            return false;
        }

        [HarmonyPatch(typeof(BattleResourceMgr), nameof(BattleResourceMgr.GetCardImageMaterial))]
        [HarmonyPostfix]
        public static void BattleResourceMgr_GetCardImageMaterial(
            int cardId,
            bool isEvolution,
            ref Material __result)
        {
            CardParameter target = ResolveCardParameter(cardId);
            int resourceCardId = target?.ResourceCardId ?? cardId;
            ResourcesManager.AssetLoadPathType type = target != null && UsesUnitCardMaterial(target)
                ? ResourcesManager.AssetLoadPathType.UnitCardMaterial
                : ResourcesManager.AssetLoadPathType.SpellCardMaterial;
            bool isMutation = target != null && CardMaster.IsMutationCardCheck(target.BaseCardId);
            CardBasePrm.CharaType originalType = target?.CharType ?? CardBasePrm.CharaType.NORMAL;

            // Borrowed card face: this path builds the material from a bundle path of
            // its own instead of FindCardMaterial, so the borrowed artwork has to be
            // supplied here as well.
            Material artMaterial = null;
            if (TryGetArtSlot(
                    resourceCardId,
                    type,
                    isEvolution,
                    out int artResourceCardId,
                    out ResourcesManager.AssetLoadPathType artBundleType,
                    out bool artSourceIsEvol))
            {
                artMaterial = GetArtSlotMaterial(
                    artResourceCardId,
                    artBundleType,
                    artSourceIsEvol,
                    isMutation,
                    originalType,
                    false);
            }

            Material foilEffectTemplate = GetFoilEffectMaterial(
                resourceCardId,
                type,
                isEvolution,
                isMutation,
                originalType,
                false);
            Material customMaterial = CreateCardMaterial(
                resourceCardId,
                isEvolution,
                __result ?? artMaterial,
                foilEffectTemplate);
            if (customMaterial != null)
            {
                __result = customMaterial;
                ApplyCardShader(customMaterial, target);
            }
            else if (__result == null && artMaterial != null)
            {
                __result = artMaterial;
                ApplyCardShader(artMaterial, target);
            }
        }

        [HarmonyPatch(typeof(UnitCardCreator), nameof(UnitCardCreator.SetupUnitCardMaterialToCardMesh))]
        [HarmonyPrefix]
        public static void UnitCardCreator_SetupUnitCardMaterialToCardMesh(
            CardParameter cardBasePrm,
            Material normalCardArtMaterial)
        {
            ApplyCardShader(normalCardArtMaterial, cardBasePrm);
        }

        [HarmonyPatch(typeof(FieldCardCreator), nameof(FieldCardCreator.SetupFieldCardMaterialToCardMesh))]
        [HarmonyPrefix]
        public static void FieldCardCreator_SetupFieldCardMaterialToCardMesh(
            CardParameter cardBasePrm,
            Material normalCardArtMaterial)
        {
            ApplyCardShader(normalCardArtMaterial, cardBasePrm);
        }

        private static CardParameter ResolveCardParameter(int cardId)
        {
            return CardMaster.GetInstanceForBattle()?.GetCardParameterFromId(cardId);
        }

        private static int ResolveResourceCardId(int cardId)
        {
            CardParameter parameter = ResolveCardParameter(cardId);
            return parameter?.ResourceCardId ?? cardId;
        }

        private static Material GetFoilEffectMaterial(
            int targetResourceCardId,
            ResourcesManager.AssetLoadPathType type,
            bool isEvolution,
            bool isMutation,
            CardBasePrm.CharaType originalType,
            bool isChoiceBrave)
        {
            if (!FoilEffectsByTargetResourceId.TryGetValue(
                    targetResourceCardId,
                    out FoilEffectBinding binding))
            {
                return null;
            }

            if (Toolbox.ResourcesManager == null)
            {
                return null;
            }

            try
            {
                IsResolvingFoilEffectTemplate = true;
                Material material = Toolbox.ResourcesManager.FindCardMaterial(
                    binding.SourceFoilResourceCardId,
                    type,
                    isEvolution,
                    isMutation,
                    originalType,
                    isChoiceBrave);
                if (material == null)
                {
                    WarnFoilEffectOnce(
                        $"material-unavailable:{binding.SourceFoilCardId}:{isEvolution}",
                        $"foil effect source {binding.SourceFoilCardId} material is unavailable; using the target material instead");
                }
                return material;
            }
            finally
            {
                IsResolvingFoilEffectTemplate = false;
            }
        }

        private static Material CreateCardMaterial(
            int resourceCardId,
            bool isEvolution,
            Material originalMaterial,
            Material foilEffectTemplate)
        {
            Texture2D texture = Utils.GetExternalTexture(resourceCardId, isEvolution);
            if (texture == null && foilEffectTemplate == null)
            {
                return null;
            }

            // Never display the source card's artwork merely because its foil
            // material was selected. If the target has no external image and
            // the target bundle is not loaded, leave the original path intact.
            if (texture == null && foilEffectTemplate != null && originalMaterial == null)
            {
                WarnFoilEffectOnce(
                    $"missing-target-material:{resourceCardId}:{isEvolution}",
                    $"target material {resourceCardId} is unavailable; foil effect override was skipped");
                return null;
            }

            Material materialTemplate = foilEffectTemplate ?? originalMaterial ?? commonCardMaterial;
            if (materialTemplate == null)
            {
                Plugin.Logger.LogWarning(
                    $"Cannot create card material {resourceCardId}: no material template is loaded");
                return null;
            }

            Material material = UnityEngine.Object.Instantiate(materialTemplate);
            GeneratedCardMaterials.Add(material);
            Texture targetTexture = texture ?? originalMaterial?.mainTexture;
            if (targetTexture != null)
            {
                material.mainTexture = targetTexture;
                if (material.HasProperty("_MainTex"))
                {
                    material.SetTexture("_MainTex", targetTexture);
                }
            }

            Plugin.Logger.LogDebug(
                $"Custom {(isEvolution ? "evolved" : "normal")} material for {resourceCardId} loaded");
            return material;
        }

        private static void ApplyCardShader(Material material, CardParameter parameter)
        {
            if (material == null || parameter == null)
            {
                return;
            }

            bool showFoilAnimation = parameter.IsFoil &&
                PlayerPrefsWrapper.GetBool(PlayerPrefsWrapper.SHOW_FOIL_CARD_ANIMATION);
            string shaderName = showFoilAnimation
                ? CardShaderDefine.CARD_SHADER_FOIL
                : CardShaderDefine.CARD_SHADER_DEFAULT;
            Shader shader = Shader.Find(shaderName);
            if (shader == null)
            {
                WarnFoilEffectOnce(
                    $"missing-shader:{shaderName}",
                    $"card shader {shaderName} was not found");
                return;
            }
            material.shader = shader;
        }
    }


}
