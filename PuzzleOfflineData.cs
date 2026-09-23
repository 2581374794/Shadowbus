using System;
using System.Collections.Generic;
using System.IO;
using Cute;
using LitJson;
using Newtonsoft.Json;

namespace Shadowbus
{
    /// <summary>
    /// 解谜（国服接口 <c>basic_puzzle/*</c>）的离线数据。
    ///
    /// 谜题本身（棋盘、手牌、PP、胜利条件）一直都在客户端自己的 master 里
    /// （<c>master_puzzle_data</c> → <c>master_puzzle_battle_data</c>，113 题）；
    /// 服务端只负责**分组**和**通关状态**。所以这里只需要一份分组表：
    /// <c>&lt;资源根&gt;/puzzle/puzzle_info.json</c>（官方 <c>basic_puzzle/info</c> 的原样数据，
    /// 由 <c>_tools/build_puzzle_data.py</c> 生成），并且所有条目都标成已通关 / 可玩，
    /// 与离线剧情把 <c>is_released</c> 置真同一个思路。
    ///
    /// 用任务**类型名**而不是类型本身来判断：这样不需要在编译期引用客户端的解谜类型，
    /// 客户端版本变化也不会把插件编译带崩。
    /// </summary>
    internal static class PuzzleOfflineData
    {
        private const string InfoTaskName = "PracticePuzzleInfoTask";
        private const string ListTaskName = "PracticePuzzleListTask";
        private const string MissionTaskName = "PracticePuzzleMissionListTask";
        private const string BattleStartTaskName = "PracticePuzzleBattleStartTask";
        private const string BattleFinishTaskName = "PracticePuzzleBattleFinish";

        private static List<Dictionary<string, object>> _groups;
        private static List<Dictionary<string, object>> _missions;
        private static bool _loaded;
        private static bool _loggedMissing;

        internal static bool CanHandle(string taskName)
        {
            return taskName == InfoTaskName ||
                taskName == ListTaskName ||
                taskName == MissionTaskName ||
                taskName == BattleStartTaskName ||
                taskName == BattleFinishTaskName;
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
                object data;
                switch (taskName)
                {
                    case InfoTaskName:
                        data = LoadGroups();
                        break;

                    case ListTaskName:
                        data = CreateDialogData(ReadPuzzleMasterId(task));
                        break;

                    case MissionTaskName:
                        data = LoadMissions();
                        break;

                    case BattleFinishTaskName:
                        data = new Dictionary<string, object>
                        {
                            ["get_class_experience"] = 0,
                            ["class_experience"] = 0,
                            ["class_level"] = 0,
                            ["achieved_info"] = EmptyAchievedInfo(),
                            ["reward_list"] = new List<object>()
                        };
                        break;

                    default:                        // PracticePuzzleBattleStartTask：Parse 不读任何字段
                        data = new Dictionary<string, object>();
                        break;
                }

                // 外壳必须和别的离线任务一致：FakeConnect 会往 data_headers 里写 servertime，
                // 少了这一层就是 KeyNotFoundException（"Error processing local data for ..."）。
                response = JsonMapper.ToObject(JsonConvert.SerializeObject(CreateResponseEnvelope(data)));
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning(
                    $"[Puzzle] Could not build local data for {taskName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>把某一组的分组信息摊成 <c>open_puzzle_dialog</c> 的响应。</summary>
        private static Dictionary<string, object> CreateDialogData(int puzzleMasterId)
        {
            Dictionary<string, object> group = null;
            foreach (Dictionary<string, object> candidate in LoadGroups())
            {
                if (ReadInt(candidate, "puzzle_master_id") == puzzleMasterId)
                {
                    group = candidate;
                    break;
                }
            }

            if (group == null)
            {
                Plugin.Logger.LogWarning(
                    $"[Puzzle] No local group for puzzle_master_id={puzzleMasterId}; returning an empty dialog.");
            }

            return new Dictionary<string, object>
            {
                ["puzzle_quest"] = group != null && group.TryGetValue("puzzle_data", out object list)
                    ? list
                    : new List<object>(),
                ["puzzle_quest_chara_id"] = group != null ? ReadString(group, "puzzle_chara_id") : string.Empty,
                ["puzzle_difficulty_name_list"] = group != null &&
                    group.TryGetValue("puzzle_difficulty_name_list", out object names)
                        ? names
                        : new Dictionary<string, object>(),
                ["is_display_badge"] = false,
                ["is_display_puzzle_new"] = false
            };
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

        private static Dictionary<string, object> EmptyAchievedInfo()
        {
            return new Dictionary<string, object>
            {
                ["achieved_mission_list"] = new List<object>(),
                ["achieved_mission_reward_list"] = new List<object>(),
                ["mission_start_data"] = new List<object>()
            };
        }

        private static List<Dictionary<string, object>> LoadGroups()
        {
            EnsureLoaded();
            return _groups ?? new List<Dictionary<string, object>>();
        }

        private static List<Dictionary<string, object>> LoadMissions()
        {
            EnsureLoaded();
            return _missions ?? new List<Dictionary<string, object>>();
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            string root = ResourceRootPatches.ResourceRoot;
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            string dir = Path.Combine(root, "puzzle");
            _groups = ReadArray(Path.Combine(dir, "puzzle_info.json"));
            _missions = ReadArray(Path.Combine(dir, "puzzle_mission.json"));

            if (_groups.Count == 0 && _missions.Count == 0)
            {
                if (!_loggedMissing)
                {
                    _loggedMissing = true;
                    Plugin.Logger.LogWarning(
                        $"[Puzzle] No local puzzle data in '{dir}'; the puzzle screen will stay empty. " +
                        "Run _tools/build_puzzle_data.py to create it.");
                }

                return;
            }

            int puzzles = 0;
            foreach (Dictionary<string, object> group in _groups)
            {
                if (group.TryGetValue("puzzle_data", out object list) && list is List<object> entries)
                {
                    puzzles += entries.Count;
                }
            }

            Plugin.Logger.LogInfo(
                $"[Puzzle] Loaded {_groups.Count} puzzle group(s) / {puzzles} puzzle(s) / " +
                $"{_missions.Count} mission(s) from '{dir}' (all marked cleared).");
        }

        private static List<Dictionary<string, object>> ReadArray(string path)
        {
            var result = new List<Dictionary<string, object>>();
            if (!File.Exists(path))
            {
                return result;
            }

            try
            {
                List<Dictionary<string, object>> parsed =
                    JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(File.ReadAllText(path));
                if (parsed != null)
                {
                    result.AddRange(parsed);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[Puzzle] Could not read '{Path.GetFileName(path)}': {ex.Message}");
            }

            return result;
        }

        /// <summary>从请求参数里取 puzzle_master_id（PracticePuzzleListTaskParam.puzzle_master_id）。</summary>
        private static int ReadPuzzleMasterId(NetworkTask task)
        {
            try
            {
                object parameters = task.Params;
                if (parameters == null)
                {
                    return 0;
                }

                System.Reflection.FieldInfo field = parameters.GetType().GetField("puzzle_master_id");
                if (field == null)
                {
                    return 0;
                }

                object value = field.GetValue(parameters);
                return value == null ? 0 : Convert.ToInt32(value);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static int ReadInt(Dictionary<string, object> data, string key)
        {
            if (data == null || !data.TryGetValue(key, out object value) || value == null)
            {
                return 0;
            }

            try
            {
                return Convert.ToInt32(value.ToString());
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static string ReadString(Dictionary<string, object> data, string key)
        {
            return data != null && data.TryGetValue(key, out object value) && value != null
                ? value.ToString()
                : string.Empty;
        }
    }
}
