using System;
using System.Collections.Generic;
using System.IO;

namespace Shadowbus
{
    /// <summary>
    /// 卡图文件声明。名字随意，路径相对于卡自己的文件夹。
    /// 不写时退回到卡文件夹里的约定名 card.png / card_evo.png。
    /// </summary>
    public class CardImageFilePatch
    {
        /// <summary>进化前卡图。</summary>
        public string normal;
        /// <summary>进化后卡图。不写就沿用进化前那张。</summary>
        public string evolved;
    }

    /// <summary>
    /// 单卡资源目录。一张 mod 卡的全部资源都放在它自己的文件夹里：
    ///
    ///   Mods/CardMaster/diycard1/diycard.json   卡的定义
    ///   Mods/CardMaster/diycard1/卡面.png        卡图（名字随意，在 json 里声明）
    ///   Mods/CardMaster/diycard1/登场.wav        语音（名字随意，在 json 里声明）
    ///
    /// 补丁应用时把「资源卡号 → 卡文件夹 + 声明的文件名」登记在这里，
    /// 卡图与语音在运行时就能凭卡号回到自己的文件夹里取文件。
    ///
    /// 所有资源都只在卡文件夹里找，没有全局目录这回事。
    /// </summary>
    public static class ModCardAssets
    {
        /// <summary>卡文件夹里的卡图约定名，json 没声明 imageFiles 时用。</summary>
        public const string NormalImageName = "card.png";
        public const string EvolutionImageName = "card_evo.png";

        private sealed class CardAssetFolder
        {
            public string Folder;
            public string NormalImage;
            public string EvolutionImage;
        }

        private static readonly Dictionary<int, CardAssetFolder> FoldersByResourceCardId =
            new Dictionary<int, CardAssetFolder>();

        /// <summary>清空登记表。每次重新应用 CardMaster 补丁前调用。</summary>
        public static void Reset()
        {
            FoldersByResourceCardId.Clear();
        }

        /// <summary>
        /// 登记「资源卡号属于哪个卡文件夹」，以及这张卡声明的卡图文件名。
        /// folder 为 null 表示这张卡没有自己的文件夹（json 直接放在 CardMaster 根目录），
        /// 那它就不能使用本地卡图与本地语音。
        /// </summary>
        public static void Register(
            int resourceCardId,
            string folder,
            string normalImage,
            string evolutionImage)
        {
            if (resourceCardId == 0 || string.IsNullOrEmpty(folder))
            {
                return;
            }

            CardAssetFolder existing;
            if (FoldersByResourceCardId.TryGetValue(resourceCardId, out existing) &&
                !string.Equals(existing.Folder, folder, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Logger.LogWarning(
                    $"resource card {resourceCardId} is claimed by two card folders " +
                    $"('{existing.Folder}' and '{folder}'); the latter wins");
            }

            FoldersByResourceCardId[resourceCardId] = new CardAssetFolder
            {
                Folder = folder,
                NormalImage = normalImage,
                EvolutionImage = evolutionImage
            };
        }

        /// <summary>
        /// 找一张卡的自定义卡图，返回第一个存在的候选路径，都没有则返回 null。
        /// 顺序：json 里声明的 imageFiles → 约定名 card.png / card_evo.png
        ///      → &lt;资源卡号&gt;.png / _evo.png。进化图找不到就退回普通图。
        /// </summary>
        public static string ResolveImagePath(int resourceCardId, bool isEvolution)
        {
            CardAssetFolder entry;
            if (!FoldersByResourceCardId.TryGetValue(resourceCardId, out entry) ||
                string.IsNullOrEmpty(entry.Folder))
            {
                return null;
            }

            var candidates = new List<string>(4);
            if (isEvolution)
            {
                if (!string.IsNullOrEmpty(entry.EvolutionImage))
                {
                    candidates.Add(entry.EvolutionImage);
                }

                candidates.Add(EvolutionImageName);
                candidates.Add(resourceCardId + "_evo.png");
            }

            if (!string.IsNullOrEmpty(entry.NormalImage))
            {
                candidates.Add(entry.NormalImage);
            }

            candidates.Add(NormalImageName);
            candidates.Add(resourceCardId + ".png");

            for (int i = 0; i < candidates.Count; i++)
            {
                string path = Path.Combine(entry.Folder, candidates[i]);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        /// <summary>
        /// 把相对路径解析成卡文件夹下的绝对路径。路径不允许是绝对路径，
        /// 也不允许跳出卡文件夹。
        /// </summary>
        public static bool TryResolveRelativePath(
            string relativePath,
            string cardFolder,
            out string fullPath,
            out string error)
        {
            fullPath = null;
            error = null;

            if (string.IsNullOrEmpty(relativePath))
            {
                error = "the path is empty";
                return false;
            }

            if (string.IsNullOrEmpty(cardFolder))
            {
                error = "this card has no folder of its own; put its json in " +
                        "Mods/CardMaster/<folder>/ to use local files";
                return false;
            }

            if (Path.IsPathRooted(relativePath))
            {
                error = "the path must be relative to the card folder " +
                        "(Mods/CardMaster/<folder>/)";
                return false;
            }

            try
            {
                string root = Path.GetFullPath(cardFolder);
                string rootWithSeparator = root.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

                fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
                if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    error = "the path leaves the card folder";
                    fullPath = null;
                    return false;
                }
            }
            catch (Exception exception)
            {
                error = "invalid path (" + exception.Message + ")";
                return false;
            }

            return true;
        }
    }
}
