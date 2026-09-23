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
        // Optional. Borrow another card's EFFECTS (VFX + SE), the same way
        // normalArtCardId borrows artwork and extraVoiceIds borrows voice banks.
        // Everything the game needs to *show* an ability - the effect prefab name,
        // its SE cue, how it moves, how long it plays - is read straight off the
        // source card, so a mod card can look and sound like an official one
        // without anyone having to type effect names by hand.
        //
        //   effectBorrowCardId  - everything below at once (summon/evolve/attack/skill)
        //   summonEffectCardId  - summon + destroy effect/SE, SummonEffectType/MoveType, SummonTime
        //   evolveEffectCardId  - evolve effect/SE, EvoEffectType, EvolTime
        //   attackEffectCardId  - the whole AtkEffectParameter (effect/SE/move/engine/time)
        //   skillEffectCardId   - every Skill*/EvoSkill* effect+SE array and SkillVoice
        //
        // The per-slot fields are applied after effectBorrowCardId, and the patch's own
        // string/array fields are applied after all of them, so the most specific
        // declaration always wins. Borrowed effect bundles (effect_<name>.unity3d) and
        // SE banks (s/<se>.acb) are loaded automatically - without that the names would
        // point at assets nothing ever loaded and the ability would play silently.
        public int? effectBorrowCardId;
        public int? summonEffectCardId;
        public int? evolveEffectCardId;
        public int? attackEffectCardId;
        public int? skillEffectCardId;
        public int? destroyEffectCardId;
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

        // ------------------------------------------------------------ 借来的特效（VFX + SE）
        //
        // 和借卡图（normalArtCardId）、借语音库（extraVoiceIds）一个思路：在 json 里写一个
        // 来源卡号，插件把引擎真正要用的那套字段整组抄过来（特效名、SE 名、移动方式、
        // 引擎类型、时长），再把来源卡的 effect_<名字>.unity3d 挂进预载清单 ——
        // 不挂的话那些名字指向的包谁都没加载过，技能就是「静默无特效」
        // （引擎只是取不到 GameObject，不报错、不崩）。
        //
        //   effectBorrowCardId  → 下面全部
        //   summonEffectCardId  → SummonEffectPath / SummonSePath / SummonMoveType /
        //                         SummonEffectType / SummonTime
        //   destroyEffectCardId → DestroyEffectPath（破坏演出没有 SE 字段）
        //   evolveEffectCardId  → EvolEffectPath / EvolSePath / EvoEffectType / EvolTime
        //   attackEffectCardId  → AtkEffectParameter（_effectPath/_se/_moveType/
        //                         _effectEnginType/_time，普通 + 进化两套）
        //   skillEffectCardId   → SkillEffectPath / SkillSe / SkillMoveType /
        //                         SkillEffectEnginType / SkillEffectTime /
        //                         SkillEffectTargetType 以及对应的 EvoSkill* 六项
        //
        // 只管「演出」。技能本身（Skill / SkillTiming / SkillCondition / SkillTarget /
        // SkillOption / SkillPreprocess）不在这里借 —— 那是效果逻辑。
        private static readonly string[] SummonEffectFieldNames =
        {
            nameof(CardParameter.SummonEffectPath),
            nameof(CardParameter.SummonSePath),
            nameof(CardParameter.SummonMoveType),
            nameof(CardParameter.SummonEffectType),
            nameof(CardParameter.SummonTime)
        };

        private static readonly string[] DestroyEffectFieldNames =
        {
            nameof(CardParameter.DestroyEffectPath)
        };

        private static readonly string[] EvolveEffectFieldNames =
        {
            nameof(CardParameter.EvolEffectPath),
            nameof(CardParameter.EvolSePath),
            nameof(CardParameter.EvoEffectType),
            nameof(CardParameter.EvolTime)
        };

        private static readonly string[] AttackEffectFieldNames =
        {
            nameof(CardParameter.AtkEffectParameter)
        };

        private static readonly string[] SkillEffectFieldNames =
        {
            nameof(CardParameter.SkillEffectPath),
            nameof(CardParameter.SkillSe),
            nameof(CardParameter.SkillMoveType),
            nameof(CardParameter.SkillEffectEnginType),
            nameof(CardParameter.SkillEffectTime),
            nameof(CardParameter.SkillEffectTargetType),
            nameof(CardParameter.EvoSkillEffectPath),
            nameof(CardParameter.EvoSkillSe),
            nameof(CardParameter.EvoSkillMoveType),
            nameof(CardParameter.EvoSkillEffectEnginType),
            nameof(CardParameter.EvoSkillEffectTime),
            nameof(CardParameter.EvoSkillEffectTargetType)
        };

        /// <summary>借用登记：目标卡号 → 要加载的特效包 / SE 库。</summary>
        private sealed class BorrowedEffectResources
        {
            public readonly HashSet<string> EffectBundles =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public readonly HashSet<string> SeBanks =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private static readonly Dictionary<int, BorrowedEffectResources> BorrowedEffectsByCardId =
            new Dictionary<int, BorrowedEffectResources>();

        private static readonly HashSet<string> BorrowedEffectWarnings = [];

        private static bool BorrowedEffectBundlesRequestedInGroup;

        /// <summary>
        /// 按 <paramref name="patch"/> 的声明，把来源卡的特效/音效字段抄到 <paramref name="target"/>。
        /// 必须在 <c>PatchTemplate</c> **之前**调用：抄完再由 json 自己写的字段覆盖，
        /// 「明确写的」永远赢过「借来的」。
        /// </summary>
        internal static void ApplyBorrowedEffects(
            CardParameterPatch patch,
            CardParameter target,
            CardMaster master)
        {
            if (patch == null || target == null || master == null)
            {
                return;
            }

            // 先整套（effectBorrowCardId），再逐槽位；越具体的越晚抄。
            BorrowEffectFields(patch, target, master, patch.effectBorrowCardId, "all",
                SummonEffectFieldNames, DestroyEffectFieldNames, EvolveEffectFieldNames,
                AttackEffectFieldNames, SkillEffectFieldNames);
            BorrowEffectFields(patch, target, master, patch.summonEffectCardId, "summon",
                SummonEffectFieldNames);
            BorrowEffectFields(patch, target, master, patch.destroyEffectCardId, "destroy",
                DestroyEffectFieldNames);
            BorrowEffectFields(patch, target, master, patch.evolveEffectCardId, "evolve",
                EvolveEffectFieldNames);
            BorrowEffectFields(patch, target, master, patch.attackEffectCardId, "attack",
                AttackEffectFieldNames);
            BorrowEffectFields(patch, target, master, patch.skillEffectCardId, "skill",
                SkillEffectFieldNames);
        }

        private static void BorrowEffectFields(
            CardParameterPatch patch,
            CardParameter target,
            CardMaster master,
            int? sourceCardId,
            string label,
            params string[][] fieldGroups)
        {
            if (!sourceCardId.HasValue || sourceCardId.Value <= 0)
            {
                return;
            }

            if (sourceCardId.Value == target.CardId)
            {
                WarnBorrowedEffectOnce(
                    $"self:{target.CardId}:{label}",
                    $"card {target.CardId} cannot borrow its own effects ({label}); ignored");
                return;
            }

            CardParameter source;
            try
            {
                source = master.GetCardParameterFromId(sourceCardId.Value);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    $"[CardEffect] card {target.CardId} could not read effect source {sourceCardId.Value}: {exception.Message}");
                return;
            }

            if (source == null)
            {
                WarnBorrowedEffectOnce(
                    $"missing:{target.CardId}:{label}:{sourceCardId.Value}",
                    $"card {target.CardId} borrows effects from {sourceCardId.Value}, which does not exist");
                return;
            }

            // 深拷贝一份当「捐赠者」：里面的数组/嵌套对象都是新副本，抄给目标卡之后
            // 不会和来源卡共享可变状态（来源多半是原版卡，被改坏就麻烦了）。
            CardParameter donor;
            try
            {
                donor = CardParameterCloner.DeepClone(source);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    $"[CardEffect] card {target.CardId} could not clone effect source {sourceCardId.Value}: {exception.Message}");
                return;
            }

            int copied = 0;
            foreach (string[] group in fieldGroups)
            {
                foreach (string fieldName in group)
                {
                    if (CopyEffectField(donor, target, fieldName))
                    {
                        copied++;
                    }
                }
            }

            if (copied == 0)
            {
                return;
            }

            CollectBorrowedEffectResources(donor, target.CardId);
            Plugin.Logger.LogInfo(
                $"[CardEffect] card {target.CardId} borrows {label} effects from {sourceCardId.Value} " +
                $"({copied} field(s)).");
        }

        private static bool CopyEffectField(CardParameter donor, CardParameter target, string fieldName)
        {
            PropertyInfo property = typeof(CardParameter).GetProperty(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public);
            if (property == null || !property.CanRead || !property.CanWrite)
            {
                WarnBorrowedEffectOnce(
                    $"field:{fieldName}",
                    $"CardParameter has no writable property '{fieldName}'; that effect field was skipped");
                return false;
            }

            try
            {
                property.SetValue(target, property.GetValue(donor));
                return true;
            }
            catch (Exception exception)
            {
                WarnBorrowedEffectOnce(
                    $"copy:{fieldName}",
                    $"could not copy effect field '{fieldName}': {exception.Message}");
                return false;
            }
        }

        /// <summary>把抄过来的特效名 / SE 名登记成「要加载的东西」。</summary>
        private static void CollectBorrowedEffectResources(CardParameter donor, int targetCardId)
        {
            if (donor == null || targetCardId <= 0)
            {
                return;
            }

            if (!BorrowedEffectsByCardId.TryGetValue(targetCardId, out BorrowedEffectResources resources))
            {
                resources = new BorrowedEffectResources();
                BorrowedEffectsByCardId[targetCardId] = resources;
            }

            AddEffectBundle(resources, donor.SummonEffectPath);
            AddEffectBundle(resources, donor.DestroyEffectPath);
            AddEffectBundles(resources, donor.EvolEffectPath);
            AddEffectBundles(resources, donor.SkillEffectPath);
            AddEffectBundles(resources, donor.EvoSkillEffectPath);

            AddSeBank(resources, donor.SummonSePath);
            AddSeBanks(resources, donor.EvolSePath);
            AddSeBanks(resources, donor.SkillSe);
            AddSeBanks(resources, donor.EvoSkillSe);

            CardParameter.AttackEffectParameter attack = donor.AtkEffectParameter;
            if (attack != null)
            {
                AddEffectBundles(resources, attack._effectPath);
                AddSeBanks(resources, attack._se);
            }
        }

        private static void AddEffectBundles(BorrowedEffectResources resources, IEnumerable<string> names)
        {
            if (names == null)
            {
                return;
            }

            foreach (string name in names)
            {
                AddEffectBundle(resources, name);
            }
        }

        private static void AddEffectBundle(BorrowedEffectResources resources, string effectName)
        {
            // 引擎就是这么拼的：GetAssetTypePath(name, Effect2D /*37*/, isfetch:false)
            // → "effect_" + name.ToLower() + ".unity3d"（对象路径是 Effect/Effects/<name>）。
            if (resources == null || string.IsNullOrEmpty(effectName))
            {
                return;
            }

            resources.EffectBundles.Add("effect_" + effectName.Trim().ToLowerInvariant() + ".unity3d");
        }

        private static void AddSeBanks(BorrowedEffectResources resources, IEnumerable<string> names)
        {
            if (names == null)
            {
                return;
            }

            foreach (string name in names)
            {
                AddSeBank(resources, name);
            }
        }

        private static void AddSeBank(BorrowedEffectResources resources, string seName)
        {
            // 战斗特效的 SE 和特效分开打包：BattleResourceMgr.LoadEffectBattle 会把
            // "s/<se>.acb" 一起塞进加载清单（随从的进化 SE 同理）。
            if (resources == null || string.IsNullOrEmpty(seName))
            {
                return;
            }

            resources.SeBanks.Add("s/" + seName.Trim() + ".acb");
        }

        private static void WarnBorrowedEffectOnce(string key, string message)
        {
            if (BorrowedEffectWarnings.Add(key))
            {
                Plugin.Logger.LogWarning($"[CardEffect] {message}");
            }
        }
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
        // 同一张卡（同一个变体、同一个模板、同一张贴图）的材质只生成一次。以前每次取
        // 卡面都 Instantiate 一个新材质，在卡组编辑里反复重画就会一路堆下去，把内存和
        // 贴图都吊住。键里带模板和贴图的实例号，所以换了模板/贴图不会拿到旧的。
        private static readonly Dictionary<string, Material> CardMaterialCache =
            new Dictionary<string, Material>(StringComparer.Ordinal);

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

        /// <summary>
        /// 材质号 → **实测**取到它的那个 AssetLoadPathType。用来纠正包内的对象路径。
        ///
        /// 剧情抉择卡（技巧/秘术）在原版数据里自相矛盾：卡是法术，卡表却让它借用一张
        /// **随从/护符**的图。战斗里 <c>CardTemplate.DynamicSetupSpellObjMaterials</c> 于是按
        /// <c>SpellCardMaterial(35)</c> 去拼 <c>Card/Spell/Materials/&lt;材质号&gt;_M</c>，
        /// 而那个材质物理上躺在 <c>Card/Field/Materials/</c> 下 —— 取不到 → 手牌透明。
        ///
        /// 靠 charType 猜目录是不行的（实测：龙蛋 104412010 charType=FIELD，材质却在 spell 目录），
        /// 所以这里只记「哪次查询真的拿到了材质」，再把对象路径按它纠正。
        ///
        /// **记录源必须是实测结果**（<c>AssetManager.LoadObject</c> 里真正命中的那条路径），
        /// 不能是调用方声明的 <c>type</c>：`FindCardMaterial` 对 ≥10 位的材质号**无视传进来的 type**，
        /// 一律按 <c>Card/Field/Materials/</c> 拼路径，所以「按声明记」会把实际躺在 field 目录的
        /// 材质记成 spell。记错的后果不是"没纠正"，而是**把本来正确的请求改坏**：
        /// 实测 <c>1214410311_M</c> / <c>1215410311_M</c>（剧情「肃清」系列抉择卡的卡面）
        /// 两张手牌因此变紫红（<c>Assets</c> 里它们的包只有 <c>card/field/materials/</c>，
        /// 被改去 spell 目录后 <c>LoadObject</c> 找不到 → 没有材质 = 品红）。
        /// </summary>
        private static readonly Dictionary<string, ResourcesManager.AssetLoadPathType> MaterialKindByMaterialId =
            new Dictionary<string, ResourcesManager.AssetLoadPathType>(StringComparer.Ordinal);

        /// <summary>位置相关的诊断只打一次。</summary>
        private static readonly HashSet<string> MaterialPathFixWarnings = [];

        /// <summary>卡面包里放卡面材质的两种目录（field = 随从/护符，spell = 法术）。</summary>
        private const string CardFieldMaterialFolder = "card/field/materials/";

        private const string CardSpellMaterialFolder = "card/spell/materials/";

        /// <summary>正在用「另一个目录」重试同一次卡面查找，防止重试再触发重试（无限递归）。</summary>
        [ThreadStatic]
        private static bool IsRetryingCardMaterialObject;

        /// <summary>「按另一个目录找回卡面」的诊断只打一次。</summary>
        private static readonly HashSet<string> MaterialFolderFallbackLog = [];

        /// <summary>
        /// 抉择卡（技巧/秘术/奥义）的卡面材质：材质号 → 材质。只记引擎自己按
        /// <c>choiceBrave=true</c> 取过图的那批 —— 这正是"这批卡"最可靠的判据，
        /// 不用卡号段去猜（猜错会把对手手牌/背面卡也掀开）。
        /// </summary>
        private static readonly Dictionary<string, Material> ChoiceFaceMaterials =
            new Dictionary<string, Material>(StringComparer.Ordinal);

        /// <summary>已经修过（补材质/网格/图层）的「时机 + 材质号」。</summary>
        private static readonly HashSet<string> AttachedChoiceFaceLog = [];

        private static readonly HashSet<int> DisabledArtSlotResourceIds = [];
        // Bundles pulled in by the on-demand fallback in GetArtSlotMaterial. Kept
        // apart from RequestedArtSourceBundles so a bundle the preload path already
        // asked for can still be retried if it turns out not to be resident.
        //
        // Requested and Loaded are deliberately two sets: starting the load is
        // asynchronous, so the lookup that follows it can only succeed once the
        // bundle is really in memory. Marking a bundle "done" the moment the request
        // went out is what left a card blank until the UI happened to ask again.
        private static readonly HashSet<string> ArtSourceBundlesRequestedOnDemand =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            ArtSourceBundlesRequestedOnDemand.Clear();
            ArtSourceBundlesLoadedOnDemand.Clear();
            BorrowedEffectsByCardId.Clear();
            BorrowedEffectWarnings.Clear();
            BorrowedEffectBundlesRequestedInGroup = false;
            CardMaterialCache.Clear();
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

        // ---------------------------------------------------------------------------
        // 官方卡的「卡图包名」重定向
        //
        // 客户端给一张卡取卡面，先要算出装着这张卡图的包：
        //   GetAssetTypePath(<号>, UnitCardMaterial | SpellCardMaterial, isfetch: false)
        // 拿到包名后把它加载进来，最后在**所有已加载的包**里按路径搜材质。可这个包名是按
        // 传进来的那个号拼的（"card_<号>0.unity3d"），而客户端里一大批卡的卡图根本不在那个包里：
        //   · 闪卡版（…011）的材质放在普通版（…010）的包里；
        //   · 剧情/特殊卡（蛟的秘术 930444080 → 资源卡号 102424030）的材质放在**别的卡**的包里；
        //   · 各调用点传进来的号也不统一 —— 卡组编辑/卡牌详情传卡号，UICardList、CardDetailUI、
        //     UnitBattleCardView 这些传的是资源卡号。
        // 于是「按号拼出来的包」磁盘上根本没有 → 包永远不会被加载 → 材质搜不到 → 卡面全黑/透明。
        //
        // 规律只跟传进来的那个号有关（本地 11895 张卡逐张核对过）：
        //     材质号 M10 = (号 >= 10 位 ? 号 : 号 * 10)
        //     真正的包   = "card_" + (M10 - M10 % 100) + ".unity3d"
        // 只在「按号拼的包磁盘上没有、算出来的这个在」时才换。
        // ---------------------------------------------------------------------------
        private static readonly Dictionary<string, string> ArtBundleRedirects =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// 从卡表补一批「传卡号」的重定向：有些调用点（卡组编辑的 CardBundle、部分战斗卡牌视图）
        /// 传的是**卡号**，光按号算规律是算不出来的 —— 卡号 820144030（贯穿肃清的意志）的图
        /// 其实在资源卡号 1211410301 那一组里。这里用卡表的 ResourceCardId 把它们也登记进去。
        /// </summary>
        private static void BuildArtBundleRedirects(CardMaster master)
        {
            ArtBundleRedirects.Clear();
            double startedAt = PerfTrace.Now;
            if (master == null)
            {
                return;
            }

            IEnumerable<CardParameter> cards = master.GetAllParameters();
            if (cards == null)
            {
                return;
            }

            int registered = 0;
            foreach (CardParameter card in cards)
            {
                if (card == null || card.CardId <= 0 || card.ResourceCardId <= 0)
                {
                    continue;
                }

                string ownBundle = "card_" + card.CardId + "0.unity3d";
                if (BundleFileExists(ownBundle))
                {
                    continue;
                }

                long materialId = card.ResourceCardId >= 1000000000
                    ? card.ResourceCardId
                    : (long)card.ResourceCardId * 10;
                string groupBundle = "card_" + (materialId - (materialId % 100)) + ".unity3d";

                if (!string.Equals(groupBundle, ownBundle, StringComparison.OrdinalIgnoreCase) &&
                    BundleFileExists(groupBundle))
                {
                    ArtBundleRedirects[card.CardId.ToString()] = groupBundle;
                    registered++;
                }
            }

            if (registered != 0)
            {
                Plugin.Logger.LogInfo(
                    $"[CardArt] {registered} card id(s) load their card face from another bundle " +
                    "of the same card group.");
            }

            PerfTrace.Note("BuildArtBundleRedirects", (long)((PerfTrace.Now - startedAt) * 1000.0));
        }

        /// <summary>这个号要卡图时该加载哪个包；不需要改就返回 null。</summary>
        private static string RedirectedArtBundle(string idText)
        {
            string cached;
            if (ArtBundleRedirects.TryGetValue(idText, out cached))
            {
                return string.IsNullOrEmpty(cached) ? null : cached;
            }

            // 传进来的是资源卡号时（UICardList / CardDetailUI / UnitBattleCardView 这些），
            // 规律只跟这个号本身有关，不需要卡表。
            string redirect = null;
            long id;
            if (long.TryParse(idText, out id) && id > 0)
            {
                long materialId = id >= 1000000000 ? id : id * 10;
                string groupBundle = "card_" + (materialId - (materialId % 100)) + ".unity3d";
                string ownBundle = "card_" + idText + "0.unity3d";

                if (!string.Equals(groupBundle, ownBundle, StringComparison.OrdinalIgnoreCase) &&
                    !BundleFileExists(ownBundle) &&
                    BundleFileExists(groupBundle))
                {
                    redirect = groupBundle;
                }
            }

            ArtBundleRedirects[idText] = redirect;
            return redirect;
        }

        /// <summary>
        /// 这个包在额外资源目录里存在吗。没有额外资源目录时一律返回 true ——
        /// 那种情况下不该替游戏下判断，交给它自己按原路径处理。
        /// </summary>
        private static bool BundleFileExists(string bundle)
        {
            string root = ResourceRootPatches.ResourceRoot;
            if (string.IsNullOrEmpty(root))
            {
                return true;
            }

            try
            {
                return System.IO.File.Exists(System.IO.Path.Combine(root, "a", bundle));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 卡面材质的两条路都要纠正：
        /// <list type="bullet">
        ///   <item><c>isfetch=false</c>：包名（纯数字输入，换成真正装着材质的包）；</item>
        ///   <item><c>isfetch=true</c>：包内的对象路径（<c>Card/Spell/Materials/&lt;材质号&gt;_M</c>
        ///         与 <c>Card/Field/Materials/…</c> 之间的目录纠正）。</item>
        /// </list>
        /// </summary>
        [HarmonyPatch(typeof(ResourcesManager), nameof(ResourcesManager.GetAssetTypePath))]
        [HarmonyPostfix]
        private static void ResourcesManager_GetAssetTypePath_CardArt(
            string path,
            ResourcesManager.AssetLoadPathType type,
            bool isfetch,
            ref string __result)
        {
            if (type != ResourcesManager.AssetLoadPathType.UnitCardMaterial &&
                type != ResourcesManager.AssetLoadPathType.SpellCardMaterial)
            {
                return;
            }

            if (isfetch)
            {
                // 对象路径：目录段可能和实际不符，按实测学到的目录纠正。
                string corrected = CorrectMaterialObjectPath(path, type, __result);
                if (corrected != null)
                {
                    __result = corrected;
                }

                return;
            }

            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(__result) || path.Length > 10 ||
                !__result.StartsWith("card_", StringComparison.Ordinal) ||
                !__result.EndsWith(".unity3d", StringComparison.Ordinal))
            {
                return;
            }

            for (int i = 0; i < path.Length; i++)
            {
                if (path[i] < '0' || path[i] > '9')
                {
                    return;
                }
            }

            string redirect = RedirectedArtBundle(path);
            if (redirect != null)
            {
                __result = redirect;
            }
        }

        /// <summary>
        /// 把 <c>Card/Spell/Materials/&lt;材质号&gt;_M</c> 这类对象路径换成实测取到材质的那个目录。
        /// 只有「这个材质号之前确实在另一种目录下拿到了带贴图的材质」时才改，否则返回 null 不动它。
        /// </summary>
        private static string CorrectMaterialObjectPath(
            string name,
            ResourcesManager.AssetLoadPathType requested,
            string result)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(result) || name.Length < 3)
            {
                return null;
            }

            // 调用方给的可能是材质名（<材质号>_M），也可能只是材质号（路径由引擎自己补 _M）。
            string materialId = name.EndsWith("_M", StringComparison.Ordinal)
                ? name.Substring(0, name.Length - 2)
                : name;
            for (int i = 0; i < materialId.Length; i++)
            {
                if (materialId[i] < '0' || materialId[i] > '9')
                {
                    return null;
                }
            }

            ResourcesManager.AssetLoadPathType working;
            if (!MaterialKindByMaterialId.TryGetValue(materialId, out working) || working == requested)
            {
                return null;
            }

            string expected = working == ResourcesManager.AssetLoadPathType.UnitCardMaterial
                ? "/Field/Materials/"
                : "/Spell/Materials/";
            string wrong = expected == "/Field/Materials/" ? "/Spell/Materials/" : "/Field/Materials/";
            if (result.IndexOf(wrong, StringComparison.Ordinal) < 0)
            {
                return null;
            }

            string corrected = result.Replace(wrong, expected);
            if (MaterialPathFixWarnings.Add(materialId))
            {
                Plugin.Logger.LogInfo(
                    $"[CardArt] {materialId}_M is really stored under {expected}; corrected the engine's " +
                    $"object path '{result}' -> '{corrected}' (this card's kind and the artwork it borrows " +
                    "disagree in the stock data).");
            }

            return corrected;
        }

        private static void BuildArtSlotRegistry(
            CardMaster master,
            IEnumerable<ArtSlotRequest> requests)
        {            foreach (ArtSlotRequest request in requests)
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
            else
            {
                // 借来的卡面同样要保活，否则战斗中途会被 UnloadUnusedAssets 收走（卡面变紫红）。
                KeepArtAssetsAlive(material);
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
        /// <summary>
        /// 卡图真正所在的那个包名。
        ///
        /// 常规情况就是「按资源卡号算出来的包名」（<c>GetAssetTypePath(id, type, false)</c>）。
        /// 但客户端里有一大批卡（实测 2742 张）的 <c>ResourceCardId</c> 指向**别的卡**：
        /// 按自己的资源号算出来的包名磁盘上根本没有，真正装着材质的是
        /// <c>card_&lt;材质号去掉后两位&gt;.unity3d</c>（一张卡面材质
        /// <c>&lt;10位材质号&gt;_M</c> 与同组的另外三个变体一起放在这个包里）。
        /// 这条规律在本地 2742 张上逐张核对过，0 例外。
        /// </summary>
        private static string ResolveArtBundleName(
            ResourcesManager resourcesManager,
            int resourceCardId,
            ResourcesManager.AssetLoadPathType bundleType)
        {
            string byResource = resourcesManager.GetAssetTypePath(
                resourceCardId.ToString(),
                bundleType,
                false);

            if (string.IsNullOrEmpty(byResource) || BundleFileExists(byResource))
            {
                return byResource;
            }

            string materialId = resourceCardId >= 1000000000
                ? resourceCardId.ToString()
                : resourceCardId + "0";
            long value;
            if (!long.TryParse(materialId, out value))
            {
                return byResource;
            }

            string groupBundle = "card_" + (value - (value % 100)) + ".unity3d";
            return BundleFileExists(groupBundle) ? groupBundle : byResource;
        }

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
                bundle = ResolveArtBundleName(resourcesManager, resourceCardId, bundleType);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning(
                    $"could not resolve the bundle of artwork source {resourceCardId}: {e.Message}");
                return false;
            }

            if (string.IsNullOrEmpty(bundle))
            {
                return false;
            }

            // 这个按号算出来的包磁盘上就没有：请求它只会白跑一趟，而且协程回调还会把它
            // 记成「按需已加载」，之后再也不重试。直接当作「没有」处理。
            if (!BundleFileExists(bundle))
            {
                return false;
            }

            // 包已经在内存里，或者上一次请求还在飞:前者不用再拉，后者让调用方直接重查
            // （协程跑完之前查不到，但下一次调用就会命中）。两种都不再记账成"试过了"。
            if (ArtSourceBundlesLoadedOnDemand.Contains(bundle))
            {
                return false;
            }

            if (ArtSourceBundlesRequestedOnDemand.Contains(bundle))
            {
                return true;
            }

            try
            {
                if (resourcesManager.IsLoadedAssetBundle(bundle))
                {
                    ArtSourceBundlesLoadedOnDemand.Add(bundle);
                    return false;
                }
            }
            catch (Exception)
            {
                // The probe is only an optimisation; fall through and try to load.
            }

            ArtSourceBundlesRequestedOnDemand.Add(bundle);

            try
            {
                using (PerfTrace.Enter("loadArtBundle(" + bundle + ")"))
                {
                    IsLoadingArtSourceBundle = true;

                    // 必须是异步那一支（StartCoroutine_LoadAssetGroupSync 会传
                    // preferSynchronousLoad=true，AssetHandle 立刻走阻塞的 AssetBundle.LoadFromFile）。
                    // 这个调用点在 FindCardMaterial 里面 —— 也就是**游戏自己正在加载资源的时候**，
                    // 同步再去拉一个包属于「在包加载过程中重入包加载」，实测会把主线程彻底挂住
                    // （日志最后一行停在加载中途，之后再没有任何输出）。
                    // 异步只把包排进队列，几帧后回调；调用方下次查卡面时就已经命中了。
                    resourcesManager.StartCoroutine_LoadAssetGroupAsync(
                        new List<string> { bundle },
                        delegate
                        {
                            ArtSourceBundlesLoadedOnDemand.Add(bundle);
                            Plugin.Logger.LogInfo($"[ArtBundle] Borrowed artwork bundle {bundle} is loaded.");
                        },
                        true);
                }

                Plugin.Logger.LogInfo($"[ArtBundle] Requested borrowed artwork bundle {bundle} on demand.");
                return true;
            }
            catch (Exception e)
            {
                ArtSourceBundlesRequestedOnDemand.Remove(bundle);
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

            // 这里传进来的常常是**10 位材质号**（借来的卡面就是按材质号登记的），按
            // `<id>0.unity3d` 算出来的包名磁盘上根本不存在 —— 真正装着它的是同组的包
            // （见 ResolveArtBundleName）。把一个不存在的包名塞进加载列表只会让引擎白跑一趟、
            // 日志里多一条找不到包；这里按实测规律纠正，确实没有就不加（按需加载那条路还会再试）。
            string bundle = ResolveArtBundleName(resourcesManager, resourceCardId, bundleType);
            if (string.IsNullOrEmpty(bundle) || !loaded.Add(bundle))
            {
                return;
            }

            if (!BundleFileExists(bundle))
            {
                Plugin.Logger.LogDebug(
                    $"borrowed artwork bundle {bundle} is not on disk; skipping the preload of resource {resourceCardId}.");
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

                            // 借来的特效要先抄：抄完 PatchTemplate 里 json 明确写的字段再覆盖，
                            // 「明确写的」赢过「借来的」。
                            ApplyBorrowedEffects(patch, variant, master);
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
                            ApplyBorrowedEffects(patch, newCard, master);
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
            BuildArtBundleRedirects(master);
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
            RequestBorrowedArtFor(__result);
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

        /// <summary>
        /// 引擎这个方法本身没有任何空判断：
        ///
        ///   return (from id in idList
        ///           let card = CardMaster.GetInstance(cardMasterId).GetCardParameterFromId(id)
        ///           orderby new ComparableCard(card.CardId, cardMasterId)   // card 为 null 就 NRE
        ///           select id).ToList();
        ///
        /// 只要传入的 id 列表里有一个卡号在当前卡表里查不到（自制卡被删、旧牌组/牌组码/导入
        /// 数据里存着已经不存在的卡号），这里就空引用，调用方整段流程断掉 —— 实测：
        /// 本地牌组列表整份注入失败（DeckInfoTask 报 NullReferenceException），牌组列表打不开。
        ///
        /// 所以在原版跑之前把「查不到」的卡号剔掉，其余照旧；只记一次日志。原版与上面那个
        /// 排序 postfix 都不受影响。
        /// </summary>
        [HarmonyPatch(typeof(UIBase_CardManager), "SortIDList", MethodType.Normal)]
        [HarmonyPrefix]
        public static bool SortIDList_Prefix(
            ref IList<int> idList,
            CardMaster.CardMasterId cardMasterId,
            ref List<int> __result)
        {
            if (idList == null || idList.Count == 0)
            {
                return true;
            }

            CardMaster master;
            try
            {
                master = CardMaster.GetInstance(cardMasterId);
            }
            catch (Exception)
            {
                master = null;
            }

            if (master == null)
            {
                // 连这张卡表都没拿到：原版那句 `CardMaster.GetInstance(cardMasterId).GetCardParameterFromId(id)`
                // 自己就会空引用。原样把列表交回去（顺序交给下面的排序 postfix），不抛异常。
                __result = new List<int>(idList);
                Plugin.Logger.LogWarning(
                    $"[Cards] card master '{cardMasterId}' is not available; a card list was passed through unsorted " +
                    "instead of letting the game throw.");
                return false;
            }

            List<int> unknown = null;
            for (int i = 0; i < idList.Count; i++)
            {
                int cardId = idList[i];
                CardParameter card;
                try
                {
                    card = master.GetCardParameterFromId(cardId);
                }
                catch (Exception)
                {
                    card = null;
                }

                if (card == null)
                {
                    (unknown ??= []).Add(cardId);
                }
            }

            if (unknown == null)
            {
                return true;   // 一个都不缺：原样交给原版
            }

            List<int> kept = new List<int>(idList.Count - unknown.Count);
            for (int i = 0; i < idList.Count; i++)
            {
                if (!unknown.Contains(idList[i]))
                {
                    kept.Add(idList[i]);
                }
            }

            idList = kept;
            unknown.Sort();
            string key = string.Join(",", unknown.Distinct());
            if (MissingCardIdWarnings.Add(key))
            {
                Plugin.Logger.LogWarning(
                    $"[Cards] a card list references {unknown.Count} card id(s) that are not in the card master " +
                    $"({cardMasterId}): {string.Join(", ", unknown.Distinct())} — they were skipped so the list could " +
                    "be built (their card files are probably gone; put them back or fix the deck).");
            }

            return true;
        }

        private static readonly HashSet<string> MissingCardIdWarnings = [];

        /// <summary>
        /// 卡池（卡组编辑的卡表、图鉴、分解界面…）刚算出「要显示哪些卡号」时，先把这些卡
        /// **借来的卡面所在包**排进加载队列。
        ///
        /// 为什么需要这一步：借来的图不在卡自己的包里，只能按需异步补拉；而 UI 是拿到卡号后
        /// 立刻就去取材质的 —— 包还没到就取，只能拿到空材质（表现为卡图丢失/黑卡），要等玩家
        /// 滚动或刷新列表才会重新取一次。在"列表刚建好、卡面还没渲染"这个时刻提前请求，
        /// 大部分情况下包就已经在内存里了。
        /// </summary>
        private static void RequestBorrowedArtFor(IEnumerable<int> cardIds)
        {
            if (cardIds == null || ArtSlotsByTargetResourceId.Count == 0)
            {
                return;
            }

            int requested = 0;
            foreach (int cardId in cardIds)
            {
                CardParameter card = ResolveCardParameterForPool(cardId);
                if (card == null ||
                    !ArtSlotsByTargetResourceId.TryGetValue(
                        card.ResourceCardId,
                        out ArtSlotBinding binding))
                {
                    continue;
                }

                if (RequestArtSourceBundle(binding.NormalResourceCardId, binding.NormalBundleType))
                {
                    requested++;
                }

                if (RequestArtSourceBundle(binding.EvolutionResourceCardId, binding.EvolutionBundleType))
                {
                    requested++;
                }

                if (RequestArtSourceBundle(binding.SpellResourceCardId, binding.SpellBundleType))
                {
                    requested++;
                }
            }

            if (requested != 0)
            {
                Plugin.Logger.LogInfo(
                    $"[CardArt] pre-requested {requested} borrowed artwork bundle(s) for the card list " +
                    "that is about to be shown.");
            }
        }

        private static bool RequestArtSourceBundle(
            int resourceCardId,
            ResourcesManager.AssetLoadPathType bundleType)
        {
            if (resourceCardId == 0)
            {
                return false;
            }

            try
            {
                return TryLoadArtSourceBundleOnDemand(resourceCardId, bundleType);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    $"[CardArt] could not pre-request the artwork bundle of {resourceCardId}: {exception.Message}");
                return false;
            }
        }

        private static CardParameter ResolveCardParameterForPool(int cardId)
        {
            if (cardId <= 0)
            {
                return null;
            }

            try
            {
                CardMaster master = CardMaster.GetInstanceForBattle();
                CardParameter card = master != null ? master.GetCardParameterFromId(cardId) : null;
                if (card != null)
                {
                    return card;
                }
            }
            catch (Exception)
            {
            }

            try
            {
                CardMaster master = CardMaster.GetInstance(CardMaster.CardMasterId.Default);
                return master != null ? master.GetCardParameterFromId(cardId) : null;
            }
            catch (Exception)
            {
                return null;
            }
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
            RequestBorrowedArtFor(__result);
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
            RequestBorrowedArtFor(__result);
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
            ApplyBorrowedEffects(patch, foil, master);
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
                // A player PNG in the card's own folder is addressed by ResourceCardId,
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

        /// <summary>
        /// 引擎自己算出来的卡面包名，对「重印卡 / 异画卡」这类 <c>ResourceCardId</c> 指向别的卡的记录，
        /// 在磁盘上**并不存在**：真正装着它的是同组的包（<see cref="RedirectedArtBundle"/>）。
        /// 取对象那一步我们会把名字改对，但**资源组加载清单里还是那个不存在的名字** ——
        /// 于是包从来没被请求过，第一次取卡面就取不到（卡图丢失/黑卡），要靠按需补拉、
        /// 玩家滚动或刷新列表才恢复。实测日志：
        ///   [ArtBundle] Requested borrowed artwork bundle card_7203140200.unity3d on demand.   ← 补拉
        /// 这里在清单里就把不存在的卡面包名换成真正存在的那个（存在的名字一个都不动）。
        /// </summary>
        private static List<string> RedirectMissingCardBundles(List<string> bundles, HashSet<string> loaded)
        {
            if (bundles == null || bundles.Count == 0 ||
                string.IsNullOrEmpty(ResourceRootPatches.ResourceRoot))
            {
                return bundles;   // 没有额外资源目录时 BundleFileExists 恒真，不用做任何事
            }

            List<string> result = null;
            for (int i = 0; i < bundles.Count; i++)
            {
                string bundle = bundles[i];
                if (string.IsNullOrEmpty(bundle) || !bundle.StartsWith("card_", StringComparison.OrdinalIgnoreCase) ||
                    !bundle.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // card_<资源卡号>0.unity3d —— 把末尾那个 0 去掉就是资源卡号。
                string idText = bundle.Substring(
                    "card_".Length,
                    bundle.Length - "card_".Length - ".unity3d".Length);
                if (idText.Length < 2 || idText[idText.Length - 1] != '0')
                {
                    continue;
                }

                idText = idText.Substring(0, idText.Length - 1);
                if (BundleFileExists(bundle))
                {
                    continue;
                }

                string redirect = RedirectedArtBundle(idText);
                if (string.IsNullOrEmpty(redirect) || !loaded.Add(redirect))
                {
                    continue;
                }

                (result ??= new List<string>(bundles)).Add(redirect);
            }

            return result ?? bundles;
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
                 ArtSlotTargetBundleTypes.Count == 0 &&
                 BorrowedEffectsByCardId.Count == 0))
            {
                return;
            }

            List<string> expanded = new List<string>(rogueAssetList);
            HashSet<string> loaded = new HashSet<string>(expanded, StringComparer.OrdinalIgnoreCase);

            // 引擎清单里那些"名字根本不存在"的卡面包，换成真正装着它的同组包。
            expanded = RedirectMissingCardBundles(expanded, loaded);

            // Borrowed card faces: the bundle of the card whose face is borrowed has
            // to be resident before its material can be fetched.
            //
            // 每一次资源组加载都要补（不再是"只补第一批"）：卡组编辑、图鉴这些界面各自加载
            // 自己的资源组，切换界面时上一批会被卸掉（日志里的 Unloading N unused Assets），
            // 借来的卡面于是又不在内存里 —— 第一次取图就会拿到空材质（黑卡/丢图），
            // 直到玩家滚动或刷新列表才恢复。已经加载过的包被 QuickLoadAssetGroup 直接跳过，
            // 所以重复补是安全的（不会重复加载）。
            if (ArtSlotTargetBundleTypes.Count != 0)
            {
                foreach (var artPair in ArtSlotTargetBundleTypes)
                {
                    AddArtSourceBundlesToLoadList(
                        __instance,
                        loaded,
                        expanded,
                        artPair.Key);
                }
            }

            // Borrowed effects: effect_<name>.unity3d has to be resident before the
            // engine can fetch the effect prefab (a missing bundle is not an error, the
            // ability just plays nothing). 同样每次资源组都补：战斗资源组之间也会互相卸载。
            if (BorrowedEffectsByCardId.Count != 0)
            {
                int injected = 0;
                foreach (var pair in BorrowedEffectsByCardId)
                {
                    foreach (string bundle in pair.Value.EffectBundles)
                    {
                        if (!BundleFileExists(bundle))
                        {
                            continue;
                        }

                        if (loaded.Add(bundle))
                        {
                            expanded.Add(bundle);
                            injected++;
                        }
                    }
                }

                if (injected != 0)
                {
                    Plugin.Logger.LogInfo(
                        $"[CardEffect] added {injected} borrowed effect bundle(s) to the load list.");
                }
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

        // 抉择卡（技巧/秘术/奥义）的手牌视图**不走** Setup*MaterialToCardMesh，所以卡面那一层
        // （NormalField / EvolField）永远是 null 材质 —— 手牌上只剩卡框和卡背，看起来就是透明。
        // 这里把引擎自己按 choiceBrave=true 取到的那份卡面材质挂到空着的卡面层上。
        //
        // 挂两个时机：InitHandParameter 时这批卡的卡面网格刚建出来（普通卡那时早已就绪），
        // LoadResource 之后是更晚的一次机会。只有在材质是 null 时才动手，已经有材质就完全不管；
        // 也只对"引擎按 choiceBrave 取过图"的资源号生效，免得把对手手牌/背面卡掀开。
        [HarmonyPatch(typeof(Wizard.Battle.View.BattleCardView), "InitHandParameter")]
        [HarmonyPostfix]
        public static void BattleCardView_InitHandParameter_ChoiceFace(Wizard.Battle.View.BattleCardView __instance)
        {
            AttachChoiceFace(__instance, "InitHandParameter");
        }

        [HarmonyPatch(typeof(Wizard.Battle.View.BattleCardView), nameof(Wizard.Battle.View.BattleCardView.LoadResource))]
        [HarmonyPostfix]
        public static void BattleCardView_LoadResource_ChoiceFace(Wizard.Battle.View.BattleCardView __instance)
        {
            AttachChoiceFace(__instance, "LoadResource");
        }

        /// <summary>普通手牌卡上量到的图层/排序/网格（作为抉择卡的校准基准）。</summary>
        private static int ReferenceCardLayer = -1;
        private static int ReferenceSortingLayerId;
        private static int ReferenceSortingOrder;
        private static bool ReferenceSortingCaptured;
        private static readonly Dictionary<string, Mesh> ReferenceMeshes = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        private static readonly HashSet<string> SortingAlignLog = [];

        /// <summary>
        /// 让抉择卡跟普通手牌一致：图层 / 排序 / **网格**。
        ///
        /// 引擎在 <c>CardTemplate</c> 的抉择卡分支里会：
        ///   · 把卡面网格换成 `md_card_heroskill`（`filters[i].sharedMesh = GetChoiceBraveCardMesh(...)`）；
        ///   · 用 `CardFrame_HS_*` 当卡框；材质数组是 [卡框, 卡面, 职业图标]。
        ///
        /// 本地客户端加载不到 `md_card_heroskill`（`GetChoiceBraveCardMesh` 返回 null），
        /// 于是网格被**清空**：材质数组明明是好的，但没有网格 → 整张卡画不出来，看着就是透明。
        /// 同时"英雄技卡"那一套还被放在比游戏 UI 更低的层（换牌背景能盖住它）。
        ///
        /// 这里不猜：先见到普通手牌就把它的 layer / sorting / 各 renderer 的网格记下来，
        /// 抉择卡出现时照抄，并把空掉的网格补回同名网格 —— 于是它就和普通卡一样画得出来。
        /// </summary>
        private static void AlignChoiceCardSorting(
            Wizard.Battle.View.BattleCardView view,
            bool isChoiceCard,
            long materialId,
            string stage)
        {
            try
            {
                Transform root = view != null ? view.Transform : null;
                if (root == null)
                {
                    return;
                }

                Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);

                // 判据**不看材质表、也不看调用顺序**：普通手牌的卡框网格来自 prefab，永远不为空；
                // 被抉择卡分支清空网格的那张卡（"英雄技卡"那套）才会是空的。所以
                // "卡框网格为空" 就是"这张卡需要修"的确切判据。
                bool emptyFrameMesh = false;
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null || string.IsNullOrEmpty(renderer.name) ||
                        !renderer.name.StartsWith("md_Card_Spell", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    MeshFilter probe = renderer.GetComponent<MeshFilter>();
                    if (probe != null && probe.sharedMesh == null)
                    {
                        emptyFrameMesh = true;
                        break;
                    }
                }

                bool needsRepair = isChoiceCard || emptyFrameMesh;

                if (!needsRepair)
                {
                    if (ReferenceSortingCaptured)
                    {
                        return;
                    }

                    if (renderers.Length == 0 || renderers[0] == null)
                    {
                        return;
                    }

                    ReferenceCardLayer = renderers[0].gameObject.layer;
                    ReferenceSortingLayerId = renderers[0].sortingLayerID;
                    ReferenceSortingOrder = renderers[0].sortingOrder;
                    ReferenceSortingCaptured = true;

                    int meshes = 0;
                    for (int i = 0; i < renderers.Length; i++)
                    {
                        MeshFilter filter = renderers[i] != null ? renderers[i].GetComponent<MeshFilter>() : null;
                        if (filter != null && filter.sharedMesh != null && !ReferenceMeshes.ContainsKey(renderers[i].name))
                        {
                            ReferenceMeshes[renderers[i].name] = filter.sharedMesh;
                            meshes++;
                        }
                    }

                    Plugin.Logger.LogInfo(
                        $"[CardArt] reference hand card: layer={ReferenceCardLayer}, " +
                        $"sortingLayerID={ReferenceSortingLayerId}, sortingOrder={ReferenceSortingOrder}, " +
                        $"{meshes} mesh(es) recorded for lookup.");
                    return;
                }

                // 注意：**不能**因为没有见过普通卡就跳过 —— 全抉择卡的牌组里根本没有参照卡，
                // 而网格修复不依赖参照（直接取引擎无条件预载的普通卡网格）。图层对齐才是
                // "有参照就抄、没有就算了"。上几版正是卡在这里，日志里什么都没留下。
                int restored = 0;
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer == null || string.IsNullOrEmpty(renderer.name) ||
                        !renderer.name.StartsWith("md_Card_Spell", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (ReferenceSortingCaptured)
                    {
                        renderer.gameObject.layer = ReferenceCardLayer;
                        renderer.sortingLayerID = ReferenceSortingLayerId;
                        renderer.sortingOrder = ReferenceSortingOrder;
                    }

                    // 网格为空（抉择卡分支换成 md_card_heroskill 但那个没预载、拿到 null），
                    // 或者已经被换成了 heroskill 网格 —— 都换回普通卡那套无条件预载的网格。
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null)
                    {
                        continue;
                    }

                    bool emptyMesh = filter.sharedMesh == null;
                    bool heroSkillMesh = !emptyMesh && filter.sharedMesh.name != null &&
                        filter.sharedMesh.name.IndexOf("heroskill", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!emptyMesh && !heroSkillMesh)
                    {
                        continue;
                    }

                    Mesh mesh = GetCardMesh(renderer.name.IndexOf("_Low", StringComparison.Ordinal) >= 0
                        ? "md_card_spell_low"
                        : "md_card_spell");
                    if (mesh != null)
                    {
                        filter.sharedMesh = mesh;
                        restored++;
                    }
                }

                if (SortingAlignLog.Add(stage + ":" + materialId))
                {
                    Plugin.Logger.LogInfo(
                        $"[CardArt] choice card {materialId} (emptyFrameMesh={emptyFrameMesh}): restored " +
                        $"{restored} card mesh(es) out of {renderers.Length} renderer(s)" +
                        (ReferenceSortingCaptured
                            ? $"; sorting aligned to the normal hand card (layer={ReferenceCardLayer})."
                            : "."));
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[CardArt] could not align the choice-card view: {exception.Message}");
            }
        }

        /// <summary>引擎自己的卡面网格缓存（md_card_spell / md_card_spell_low）。</summary>
        private static readonly Dictionary<string, Mesh> CardMeshesByName = new Dictionary<string, Mesh>(StringComparer.Ordinal);
        private static readonly HashSet<string> ChoiceMeshLog = [];

        /// <summary>
        /// **从源头**把抉择卡要用的网格换成普通卡那套。
        ///
        /// `BattleResourceMgr.GetChoiceBraveCardMesh(low)` 只返回 `_choiceBraveCardMesh` /
        /// `_choiceBraveCardLowMesh`，而这两个字段只在 <c>Wizard.Data.CurrentFormat == 39</c>
        /// 时随预载清单加载（见 SBattleLoad 的 cardPrefabPathList）—— 离线剧情/练习战不是 39，
        /// 于是恒为 null，引擎把卡面 MeshFilter 设成 null，卡就画不出来。
        ///
        /// 这里直接返回**无条件预载**的普通卡网格（`md_card_spell` / `md_card_spell_low`），
        /// 于是从第一帧（换牌界面、抽牌动画、手牌）起就是对的，不用等到后补。
        /// 两个实现类都拦：BattleResourceMgr 与 NullBattleResourceMgr。
        /// </summary>
        [HarmonyPatch(typeof(Wizard.Battle.Resource.BattleResourceMgr), "GetChoiceBraveCardMesh")]
        [HarmonyPrefix]
        public static bool BattleResourceMgr_GetChoiceBraveCardMesh_Prefix(bool __0, ref Mesh __result)
        {
            return ReplaceChoiceBraveCardMesh(__0, ref __result);
        }

        [HarmonyPatch(typeof(Wizard.Battle.Resource.NullBattleResourceMgr), "GetChoiceBraveCardMesh")]
        [HarmonyPrefix]
        public static bool NullBattleResourceMgr_GetChoiceBraveCardMesh_Prefix(bool __0, ref Mesh __result)
        {
            return ReplaceChoiceBraveCardMesh(__0, ref __result);
        }

        private static bool ReplaceChoiceBraveCardMesh(bool lowMesh, ref Mesh __result)
        {
            try
            {
                string name = lowMesh ? "md_card_spell_low" : "md_card_spell";
                Mesh mesh = GetCardMesh(name);
                if (mesh == null)
                {
                    return true;      // 拿不到就交回原版，不比原版更差
                }

                __result = mesh;
                if (ChoiceMeshLog.Add(name))
                {
                    Plugin.Logger.LogInfo(
                        $"[CardArt] choice card mesh '{name}': handing out the normal card mesh instead of the " +
                        "not-preloaded md_card_heroskill (that set only exists in format 39).");
                }

                return false;
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[CardArt] could not substitute the choice-card mesh: {exception.Message}");
                return true;
            }
        }

        /// <summary>
        /// 取引擎自己的卡面网格。
        ///
        /// 证据（SBattleLoad 的预载清单）：<c>md_card_spell</c> / <c>md_card_spell_low</c> /
        /// <c>md_card_spell_n</c> 是**无条件**预载的；而 <c>md_card_heroskill</c> /
        /// <c>md_card_heroskill_low</c> 只在 <c>Wizard.Data.CurrentFormat == 39</c> 时才预载。
        /// 离线剧情/练习战不是 39 —— 所以抉择卡分支把网格换成 heroskill 时拿到 null，
        /// 卡面 MeshFilter 被清空，材质数组再好也没东西可画（观感就是"透明"）。
        ///
        /// 这里按引擎自己的取法（AssetLoadPathType 28、isfetch=true）把普通卡网格取回来，
        /// 两者子网格布局一致（卡框 / 卡面 / 职业图标三个槽位），换上去就能正常显示。
        /// </summary>
        private static Mesh GetCardMesh(string name)
        {
            Mesh cached;
            if (CardMeshesByName.TryGetValue(name, out cached) && cached != null)
            {
                return cached;
            }

            try
            {
                ResourcesManager manager = Toolbox.ResourcesManager;
                if (manager == null)
                {
                    return null;
                }

                string path = manager.GetAssetTypePath(name, (ResourcesManager.AssetLoadPathType)28, true);
                if (string.IsNullOrEmpty(path))
                {
                    return null;
                }

                Mesh mesh = manager.LoadObject<Mesh>(path, true, false);
                if (mesh != null)
                {
                    CardMeshesByName[name] = mesh;
                }
                else if (CardMeshesByName.Count < 8)
                {
                    Plugin.Logger.LogWarning($"[CardArt] card mesh '{name}' could not be loaded from '{path}'.");
                }

                return mesh;
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[CardArt] could not load card mesh '{name}': {exception.Message}");
                return null;
            }
        }

        private static void AttachChoiceFace(Wizard.Battle.View.BattleCardView view, string stage)
        {            try
            {
                if (view == null)
                {
                    return;
                }

                Wizard.Battle.IReadOnlyBattleCardInfo info = view.CardInfo;
                CardParameter parameter = info != null ? info.BaseParameter : null;
                if (parameter == null || parameter.ResourceCardId <= 0)
                {
                    return;
                }

                long materialId = parameter.ResourceCardId >= 1000000000
                    ? parameter.ResourceCardId
                    : (long)parameter.ResourceCardId * 10;
                Material material;
                bool isChoiceCard = ChoiceFaceMaterials.TryGetValue(materialId.ToString(), out material) &&
                    material != null;

                // 抉择卡会被引擎放在**比游戏 UI 还低的一层**（换牌界面的半透明黑底盖住它、
                // 玩家血量等 UI 也在它上面），看起来就是"透明"。这里用普通手牌的图层与排序
                // 自动校准：先见到普通卡就把它的值记下来，之后每张抉择卡照抄。
                AlignChoiceCardSorting(view, isChoiceCard, materialId, stage);

                if (!isChoiceCard)
                {
                    return;
                }

                Transform root = view.Transform;
                if (root == null)
                {
                    return;
                }

                MeshRenderer[] renderers = root.GetComponentsInChildren<MeshRenderer>(true);
                bool attached = false;
                bool activated = false;
                for (int i = 0; i < renderers.Length; i++)
                {
                    MeshRenderer renderer = renderers[i];
                    if (renderer == null || renderer.sharedMaterial != null || string.IsNullOrEmpty(renderer.name))
                    {
                        continue;
                    }

                    // 只补卡面那一层：卡框（md_Card_*）和卡背（CardBase）都已有材质，不会命中。
                    if (renderer.name.StartsWith("NormalField", StringComparison.Ordinal) ||
                        renderer.name.StartsWith("EvolField", StringComparison.Ordinal))
                    {
                        // 只挂材质，**不激活**这一层：实测激活它只会多出一层"巨大且透明的特效"
                        // 并渲染成紫红（比原来的透明更糟）。真正的原因看下面的说明：
                        // 这批卡根本不在普通手牌那一层渲染（换牌界面里它们在半透明黑底**后面**，
                        // 普通卡在前面），所以要动的是"它们由谁、在哪一层渲染"，不是这里的材质。
                        renderer.sharedMaterial = material;
                        attached = true;
                    }
                }

                if (attached && AttachedChoiceFaceLog.Add(stage + ":" + materialId))
                {
                    Plugin.Logger.LogInfo(
                        $"[CardArt] attached the choice-card face '{material.name}' " +
                        $"(shader '{material.shader?.name}') to the face layer of card {parameter.CardId} " +
                        $"(resource {parameter.ResourceCardId}) at {stage}" +
                        (activated ? " and activated that layer." : "."));
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[CardArt] could not attach a choice-card face: {exception.Message}");
            }
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
            // 这是 UI 和战斗里取卡面的公共出口，卡死时最需要知道当时是不是它、是哪张卡。
            using (PerfTrace.Enter("FindCardMaterial(" + cardId + ")"))
            {
                ResolveCardMaterial(cardId, type, isEvol, isMutation, originalType, isChoiceBrave, ref __result);
            }

            if (__result != null)
            {
                // 这是 UI 与战斗取卡面的公共出口：从这里交给引擎的每一份材质（以及它的贴图）
                // 都要一直有人引用着，否则会被 UnloadUnusedAssets 收走（见 KeepArtAssetsAlive）。
                KeepArtAssetsAlive(__result);
            }
        }

        /// <summary>
        /// 卡面材质真正的目录由**这次查找有没有真的命中**决定（不再由调用方声明的 type 决定）。
        /// 只要确实取到了，就把它属于 field 还是 spell 记下来，供 <see cref="CorrectMaterialObjectPath"/> 用；
        /// 覆盖写：实测结果永远赢过之前记的那条（记错一次不会一直错下去）。
        /// </summary>
        private static void RememberMaterialKindFromObjectPath(string objectPath)
        {
            try
            {
                int start = IndexOfCardFolder(objectPath, out int folderIndex, out bool isSpell);
                // 只记材质那对目录：CorrectMaterialObjectPath 用的就是材质号。
                if (start < 0 || folderIndex != CardMaterialFolderPair)
                {
                    return;
                }

                string materialId = objectPath.Substring(start);
                int dot = materialId.IndexOf('.');
                if (dot >= 0)
                {
                    materialId = materialId.Substring(0, dot);
                }

                if (materialId.EndsWith("_M", StringComparison.OrdinalIgnoreCase))
                {
                    materialId = materialId.Substring(0, materialId.Length - 2);
                }

                if (materialId.Length == 0)
                {
                    return;
                }

                for (int i = 0; i < materialId.Length; i++)
                {
                    if (materialId[i] < '0' || materialId[i] > '9')
                    {
                        return;
                    }
                }

                MaterialKindByMaterialId[materialId] =
                    isSpell
                        ? ResourcesManager.AssetLoadPathType.SpellCardMaterial
                        : ResourcesManager.AssetLoadPathType.UnitCardMaterial;
            }
            catch (Exception)
            {
                // 只是记账，出错就算了。
            }
        }

        /// <summary>
        /// 卡面包里成对的两种目录：<c>[0]</c> = field（随从/护符），<c>[1]</c> = spell（法术）。
        /// 卡面材质、卡面贴图、卡名横幅（header）三套都会出现「卡型和实际存放目录不一致」。
        /// </summary>
        private static readonly string[][] CardFolderPairs =
        {
            new[] { CardFieldMaterialFolder, CardSpellMaterialFolder },
            new[] { "card/field/textures/", "card/spell/textures/" },
            new[] { "card/field/header/", "card/spell/header/" }
        };

        private const int CardMaterialFolderPair = 0;

        /// <summary>
        /// <paramref name="objectPath"/> 里命中的那对卡面目录；返回目录之后（对象名开头）的位置，
        /// <paramref name="folderIndex"/> 是 <see cref="CardFolderPairs"/> 的下标，
        /// <paramref name="isSpell"/> 说明命中的是 spell 那一侧。不是卡面对象就返回 -1。
        /// </summary>
        private static int IndexOfCardFolder(string objectPath, out int folderIndex, out bool isSpell)
        {
            folderIndex = -1;
            isSpell = false;
            if (string.IsNullOrEmpty(objectPath))
            {
                return -1;
            }

            for (int i = 0; i < CardFolderPairs.Length; i++)
            {
                int index = objectPath.IndexOf(CardFolderPairs[i][0], StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    folderIndex = i;
                    return index + CardFolderPairs[i][0].Length;
                }

                index = objectPath.IndexOf(CardFolderPairs[i][1], StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    folderIndex = i;
                    isSpell = true;
                    return index + CardFolderPairs[i][1].Length;
                }
            }

            return -1;
        }

        /// <summary>同一个对象换成同一对的另一个卡面目录（field ↔ spell）；不是卡面对象就返回 null。</summary>
        private static string FlipCardFolder(string objectPath)
        {
            int start = IndexOfCardFolder(objectPath, out int folderIndex, out bool isSpell);
            if (start < 0)
            {
                return null;
            }

            string[] pair = CardFolderPairs[folderIndex];
            string source = isSpell ? pair[1] : pair[0];
            string target = isSpell ? pair[0] : pair[1];
            return objectPath.Substring(0, start - source.Length) + target + objectPath.Substring(start);
        }

        /// <summary>
        /// 卡面对象的**兜底取法**：这次按 caller 给的目录没找到时，换另一个目录再找一次。
        ///
        /// 原版卡面数据的「卡型（法术/随从）」和「材质真实所在目录」经常不一致
        /// （剧情抉择卡的卡面几乎全是借用随从/护符的图；实测 8 张「肃清」系列全部躺在
        /// <c>card/field/materials/</c>，而 <c>FindCardMaterial</c> 对 10 位材质号一律按 field 拼路径、
        /// 对法术卡又按 spell 拼）。上游靠"记下哪个目录能用"来纠正，但记错一次就会**把本来正确的
        /// 请求改坏**（手牌变紫红）。这里加一道不依赖任何猜测的兜底：两个目录只有两种可能，
        /// 第一个没找到就用另一个，找到为止。命中之后按实测记下真正的目录。
        /// </summary>
        private static void ResolveCardMaterialObjectPath(string objectName, Type type, ref UnityEngine.Object __result)
        {
            int start = IndexOfCardFolder(objectName, out _, out bool isSpell);
            if (start < 0)
            {
                return;
            }

            if (__result != null)
            {
                RememberMaterialKindFromObjectPath(objectName);
                return;
            }

            if (IsRetryingCardMaterialObject)
            {
                return;
            }

            string alternate = FlipCardFolder(objectName);
            if (alternate == null || Toolbox.AssetManager == null)
            {
                return;
            }

            try
            {
                IsRetryingCardMaterialObject = true;
                // 用调用方要的类型重查（原版卡面就是 UnityEngine.Object，比 Material 更宽）。
                UnityEngine.Object retried = Toolbox.AssetManager.LoadObject(
                    alternate,
                    type ?? typeof(UnityEngine.Object),
                    false);
                if (retried == null)
                {
                    return;
                }

                __result = retried;
                RememberMaterialKindFromObjectPath(alternate);
                if (MaterialFolderFallbackLog.Add(objectName))
                {
                    Plugin.Logger.LogInfo(
                        $"[CardArt] '{objectName}' is in the other folder; loaded '{alternate}' instead " +
                        $"(the card's kind and the folder its artwork lives in disagree in the stock data" +
                        (isSpell ? ", asked for spell)." : ", asked for field)."));
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[CardArt] could not retry '{objectName}' in the other folder: {exception.Message}");
            }
            finally
            {
                IsRetryingCardMaterialObject = false;
            }
        }

        [HarmonyPatch(typeof(AssetManager), nameof(AssetManager.LoadObject),
            new Type[] { typeof(string), typeof(Type), typeof(bool) })]
        [HarmonyPostfix]
        private static void AssetManager_LoadObject_CardArt(string objectName, Type type, ref UnityEngine.Object __result)
        {
            ResolveCardMaterialObjectPath(objectName, type, ref __result);
        }

        [HarmonyPatch(typeof(AssetManager), nameof(AssetManager.LoadObject),
            new Type[] { typeof(string), typeof(string), typeof(Type) })]
        [HarmonyPostfix]
        private static void AssetManager_LoadObjectByName_CardArt(string objectName, Type type, ref UnityEngine.Object __result)
        {
            ResolveCardMaterialObjectPath(objectName, type, ref __result);
        }

        /// <summary>
        /// 卡面素材的「保活」。游戏在战斗进出界面时会调 <c>Resources.UnloadUnusedAssets</c>
        /// （日志里那些 `Unloading N unused Assets`）：**没有任何托管引用的材质/贴图会被直接销毁**。
        /// 插件把「借来的卡面」（别的包的素材、按需异步拉进来的包）交回引擎之后就没人引用了，
        /// 于是战斗中途卡面会消失 —— 材质被销毁的表现就是**紫红**（Unity 缺失材质＝品红），
        /// 只有贴图被销毁则是黑卡（卡组编辑里「刷新一下又好」的另一半原因就在这）。
        /// 静态字段引用算「在用」，把材质和贴图记在这里就不会被回收。
        /// </summary>
        private static readonly List<UnityEngine.Object> ArtAssetsToKeepAlive = new List<UnityEngine.Object>();

        internal static void KeepArtAssetsAlive(Material material)
        {
            if (material == null)
            {
                return;
            }

            if (!ArtAssetsToKeepAlive.Contains(material))
            {
                ArtAssetsToKeepAlive.Add(material);
            }

            Texture texture = material.mainTexture;
            if (texture != null && !ArtAssetsToKeepAlive.Contains(texture))
            {
                ArtAssetsToKeepAlive.Add(texture);
            }
        }

        private static void ResolveCardMaterial(
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

            // 抉择卡（技巧/秘术/奥义）在战斗里是按 choiceBrave=true 取图的，拿到的常常是
            // **闪卡变体材质**（`Wizard/VariantCardShader`）—— 材质和贴图都是好的，但普通卡
            // 的卡面在别处会被 `CardShaderDefine.ReplaceShader` 归一化，而这条路没有那一步，
            // 变体 shader 在手牌视图里就渲染成透明。这里补上引擎自己那一步。
            if (isChoiceBrave && __result != null)
            {
                try
                {
                    // 记下来：这条路的材质就是这批卡该有的卡面（下面挂到卡面层用）。
                    long choiceMaterialId = cardId >= 1000000000 ? cardId : (long)cardId * 10;
                    ChoiceFaceMaterials[choiceMaterialId.ToString()] = __result;
                }
                catch (Exception exception)
                {
                    Plugin.Logger.LogWarning($"[CardArt] could not remember a choice-card material: {exception.Message}");
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

        private static readonly HashSet<string> ChoiceFrameLog = [];

        // 抉择卡（技巧/秘术/奥义）在战斗里是**刻意**画成"英雄技卡"样式的：
        // BattleResourceMgr.GetRerityMaterial 在 isChoiceBraveCard 为真时返回
        // _choiceBraveCardFrameMaterials[rarity]（CardFrame_HS_*，shader 是 Front0
        // —— 不带正面贴图），网格也是另一套 md_card_heroskill。所以手牌上只剩卡框、
        // 中间是空的，看起来就是"透明"。取图、材质、shader 全都是好的，问题在这个选择上。
        //
        // 这里把这一支改成"抉择卡也用普通法术/随从卡框"（CardFrame_S_* / BTL_*，Front1
        // 带正面贴图），它就会像普通卡一样把卡面画出来。只影响抉择卡，且只换卡框材质。
        [HarmonyPatch(typeof(Wizard.Battle.Resource.BattleResourceMgr), "GetRerityMaterial")]
        [HarmonyPrefix]
        public static bool BattleResourceMgr_GetRerityMaterial_ChoiceFace(
            Wizard.Battle.Resource.BattleResourceMgr __instance,
            bool __0,
            bool __1,
            int __2,
            bool __3,
            ref Material __result)
        {
            if (!__3 || __instance == null)
            {
                return true;      // 不是抉择卡：原样走原版
            }

            // 抉择卡（技巧/秘术/奥义）在 CardTemplate 里会走一段**专用装配**：
            // 把网格换成 md_card_heroskill，再取 GetRerityMaterial(..., isChoiceBraveCard: true)
            // 当卡框。而原版这个分支取的是 _choiceBraveCardFrameMaterials —— 那个数组
            // **只在 Data.CurrentFormat == 39 时才加载**；普通剧情/练习战不是 39，
            // 于是这里空引用或返回 null，装配中断，卡面（MaterialArrayNormal[1]）永远挂不上
            // → 手牌上那张卡就是透明的（而且原版抛异常时 postfix 不会执行，所以之前
            // 打在这个方法上的 postfix 一行日志都没有）。
            //
            // 这里直接给它**普通手牌卡框**（带正面贴图那支）并跳过原版，让它像普通卡一样画出来。
            try
            {
                string fieldName = __1
                    ? "_handSpellCardFrameMaterials"
                    : (__0 ? "_handStandardCardFrameMaterials" : "_inPlayStandardCardFrameMaterials");
                System.Reflection.FieldInfo field = AccessTools.Field(__instance.GetType(), fieldName);
                Material[] frames = field != null ? field.GetValue(__instance) as Material[] : null;
                if (frames == null || __2 < 0 || __2 >= frames.Length || frames[__2] == null)
                {
                    return true;      // 拿不到就交回原版，绝不比原版更差
                }

                __result = frames[__2];
                if (ChoiceFrameLog.Add(fieldName + ":" + __2))
                {
                    Plugin.Logger.LogInfo(
                        $"[CardArt] choice card frame: using the normal '{frames[__2].name}' instead of the " +
                        "not-loaded CardFrame_HS_* set (those only exist in format 39).");
                }

                return false;
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[CardArt] could not swap the choice-card frame: {exception.Message}");
                return true;
            }
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

            // 这条路径自己拼包名，不走 FindCardMaterial，所以「借图卡的包还没加载」
            // 这个坑要在这里单独兜一次：先按需把装着材质的包加载进来再查一遍。
            if ((__result == null || __result.mainTexture == null) && target != null)
            {
                Material rescued = ResolveMissingArtwork(
                    resourceCardId,
                    type,
                    isEvolution,
                    isMutation,
                    originalType,
                    false,
                    __result);
                if (rescued != null)
                {
                    __result = rescued;
                    ApplyCardShader(rescued, target);
                }
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

            Texture targetTexture = texture ?? originalMaterial?.mainTexture;
            string cacheKey = resourceCardId + ":" + (isEvolution ? "1" : "0") + ":" +
                materialTemplate.GetInstanceID() + ":" +
                (targetTexture != null ? targetTexture.GetInstanceID() : 0);
            if (CardMaterialCache.TryGetValue(cacheKey, out Material cached))
            {
                // 贴图会随包卸载被 Unity 销毁，缓存里那条于是变成一张**黑卡**，而且会一直黑到
                // 下次重建卡表（那时才清缓存）—— 这就是「刷新一下就好了」的另一半原因。
                // 已经没贴图的条目直接丢掉重建，不还给调用方。
                if (cached != null && cached.mainTexture != null)
                {
                    return cached;
                }

                CardMaterialCache.Remove(cacheKey);
            }

            Material material = UnityEngine.Object.Instantiate(materialTemplate);
            GeneratedCardMaterials.Add(material);
            if (targetTexture != null)
            {
                material.mainTexture = targetTexture;
                if (material.HasProperty("_MainTex"))
                {
                    material.SetTexture("_MainTex", targetTexture);
                }

                // 只缓存带贴图的那一份：没有贴图的材质一旦进了缓存，之后每次查询
                // 都会拿到同一张黑卡，直到缓存被清掉。
                CardMaterialCache[cacheKey] = material;
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

            // 只换**我们自己生成**的材质（CreateCardMaterial 里 Instantiate 出来的那些）。
            //
            // 游戏自己的材质，shader 是它按那个包里贴图的布局挑好的：素材包里 foil / variant
            // 材质的贴图挂在 variant shader 才认的属性上，这里硬按 IsFoil 把它换成普通
            // shader（或反过来），材质就会采样到空的贴图属性 —— 战斗里手牌/场上那张卡
            // 直接变透明。2D 卡面（卡组列表、卡牌图鉴）不走这两个入口，所以那边一直是好的。
            if (!GeneratedCardMaterials.Contains(material))
            {
                WarnFoilEffectOnce(
                    "keep-engine-shader:" + material.shader?.name,
                    $"kept the bundle shader '{material.shader?.name}' of an engine card material " +
                    "(swapping it is what makes battle hand/field cards transparent)");
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
