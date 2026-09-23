using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Shadowbus
{
    public static class PathHelper
    {
        public static readonly string ModPath = Path.Combine(Paths.GameRootPath, "Mods");
        public static readonly string UnlimitedDeckPath = Path.Combine(ModPath, "UnlimitedDecks");
        public static readonly string FormatPath = Path.Combine(ModPath, "Format");
        public static readonly string TwoPickPath = Path.Combine(ModPath, "TwoPick");
        public static readonly string LegacyCustomFormatPath =
            Path.Combine(ModPath, "CustomFormats");
        public static readonly string CardMasterPath = Path.Combine(ModPath, "CardMaster");
        public static readonly string CardMasterReferencePath = Path.Combine(CardMasterPath, "Reference");
        public static readonly string AIDataPath = Path.Combine(ModPath, "AIData");
        public static readonly string AIDeckPath = Path.Combine(AIDataPath, "deck");
        public static readonly string AIStylePath = Path.Combine(AIDataPath, "style");
        public static readonly string AIEmotePath = Path.Combine(AIDataPath, "emote");

        /// <summary>
        /// 官方 AI 数据（从游戏本体资源包导出成 CSV 的那一份）所在的本地目录。
        ///
        /// 位置在**资源目录**里（`&lt;资源根&gt;/story_ai/{deck,style,emote}`），不在 Mods 下：
        /// `Mods/AIData` 是给玩家自己写模组用的，官方数据不能和它混在一起。资源根还没
        /// 解析出来（或没放资源文件夹）时返回 null。
        /// </summary>
        public static string OfficialAIDataPath
        {
            get
            {
                string root = ResourceRootPatches.ResourceRoot;
                return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "story_ai");
            }
        }
        public static readonly string MyPageBackgroundSettingsPath = Path.Combine(ModPath, "MyPageBackground.json");
        public static readonly string ProfileSettingsPath = Path.Combine(ModPath, "Profile.json");
        public static readonly string P2PIdentityPath = Path.Combine(ModPath, "P2PIdentity.json");
        public static readonly string BossRushPath = Path.Combine(ModPath, "BossRush");
        public static readonly string BossRushStatePath = Path.Combine(BossRushPath, "State");
        public static readonly string BossRushReferencePath = Path.Combine(BossRushPath, "Reference");

        static PathHelper()
        {
            Directory.CreateDirectory(ModPath);
            Directory.CreateDirectory(UnlimitedDeckPath);
            Directory.CreateDirectory(FormatPath);
            Directory.CreateDirectory(TwoPickPath);
            Directory.CreateDirectory(CardMasterPath);
            Directory.CreateDirectory(AIDataPath);
            Directory.CreateDirectory(AIDeckPath);
            Directory.CreateDirectory(AIStylePath);
            Directory.CreateDirectory(AIEmotePath);
            Directory.CreateDirectory(BossRushPath);
            Directory.CreateDirectory(BossRushStatePath);
            Directory.CreateDirectory(BossRushReferencePath);
        }
    }
}
