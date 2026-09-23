using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Shadowbus
{
    /// <summary>
    /// 把 <c>UnityEngine.PlayerPrefs</c>（Windows 上就是注册表
    /// <c>HKCU\Software\Cygames\Shadowverse</c>）搬到额外资源目录里的 <c>PlayerPrefs.txt</c>，
    /// 让整个包只有游戏目录一处数据，换电脑/换盘直接搬走。
    ///
    /// PlayerPrefs 只有 <c>SetInt/SetFloat/SetString</c> 和 1 参数的 <c>GetX</c> 有托管方法体；
    /// 2 参数的 <c>GetX</c>、<c>HasKey</c>、<c>DeleteKey</c>、<c>DeleteAll</c>、<c>Save</c> 都是
    /// extern（InternalCall），Harmony 没法直接打补丁 —— 所以和 <see cref="PersistentDataPathRedirect"/>
    /// 用同一招：扫出游戏程序集里所有调用 PlayerPrefs 的方法，把这些调用指令换成这里同名的静态方法。
    ///
    /// 注册表里的旧值不会丢：某个键在本地文件里找不到时，先回落到原版 PlayerPrefs 读一次，
    /// 顺手记进本地文件（惰性搬家）。重定向生效之后**只读不写**，不会再往注册表写东西。
    /// 例外是 Unity 播放器原生层自己维护的 <c>Screenmanager *</c>（分辨率/全屏）那几个键，
    /// 那是 C++ 层直接写注册表的，托管代码拦不住。
    ///
    /// 额外资源目录不存在时什么都不做，游戏完全保持原样。
    /// </summary>
    internal static class PlayerPrefsRedirect
    {
        private const string TargetTypeName = "UnityEngine.PlayerPrefs";
        private const string WrapperTypeName = "Wizard.PlayerPrefsWrapper";
        private const string FileName = "PlayerPrefs.txt";
        private const char Deleted = 'd';
        private const float FlushInterval = 1f;

        private sealed class Value
        {
            public char Kind;      // 'i' int / 'f' float / 's' string / 'd' 已删除
            public int Int;
            public float Float;
            public string Str;
        }

        private static readonly Dictionary<string, Value> Entries =
            new Dictionary<string, Value>(StringComparer.Ordinal);

        private static readonly Dictionary<string, MethodInfo> Replacements =
            new Dictionary<string, MethodInfo>(StringComparer.Ordinal);

        private static readonly List<string> _patchedNames = new List<string>();

        /// <summary>
        /// 要被换掉的入口名。前半是 <c>UnityEngine.PlayerPrefs</c> 的，后半是游戏自己的
        /// <c>Wizard.PlayerPrefsWrapper</c> —— 游戏里几乎全部设置都走后者，而后者太短，
        /// Mono 会把它的方法体**内联**进调用方，内联出来的是没打补丁的那份 IL，
        /// 所以光给 wrapper 自己打补丁是拦不住的（实测一条都没走到）。
        /// 必须连调用它的地方一起换掉。
        /// </summary>
        private static readonly string[] Names =
        {
            "GetInt", "GetFloat", "GetString",
            "SetInt", "SetFloat", "SetString",
            "HasKey", "DeleteKey", "DeleteAll", "Save",
            "GetBool", "GetValue", "SetBool", "SetValue"
        };

        private static Harmony _harmony;
        private static bool _applied;
        private static bool _applyRequested;
        private static bool _loaded;
        private static bool _wiped;      // DeleteAll 之后不再回落到注册表
        private static bool _dirty;
        private static int _patchedCount;
        private static float _lastFlush;
        private static bool _reportedLoadFailure;
        private static bool _reportedWriteFailure;
        private static int _traceCount;
        private static bool _flushedLogged;

        public static int PatchedCount => _patchedCount;

        public static bool Active => _applied && ResourceRootPatches.UseResourceRoot;

        private static string FilePath
        {
            get
            {
                string root = ResourceRootPatches.ResourceRoot;
                return string.IsNullOrEmpty(root) ? null : Path.Combine(root, FileName);
            }
        }

        public static void SetHarmony(Harmony harmony)
        {
            _harmony = harmony;
        }

        /// <summary>资源目录是后来才出现的：请求在下一帧（Plugin.Update）补打重定向补丁。</summary>
        public static void RequestApply()
        {
            if (!_applied)
            {
                _applyRequested = true;
            }
        }

        /// <summary>Plugin.Update 每帧调用：补打补丁 + 把改动落盘。补丁没生效时不动任何东西。</summary>
        public static void Tick()
        {
            if (_applyRequested && !_applied)
            {
                _applyRequested = false;
                try
                {
                    Apply(_harmony ?? new Harmony("GeorgesZebit.Shadowbus.playerprefs"));
                }
                catch (Exception exception)
                {
                    LogError($"[Prefs] Deferred PlayerPrefs redirect failed: {exception}");
                }
            }

            if (!_dirty || !Active)
            {
                return;
            }

            if (Time.unscaledTime - _lastFlush >= FlushInterval)
            {
                Flush();
            }
        }

        /// <summary>插件 Awake 里调用（越早越好，晚于 persistentDataPath 的重定向）。</summary>
        public static void Apply(Harmony harmony)
        {
            _harmony = harmony;

            if (!ResourceRootPatches.UseResourceRoot)
            {
                LogInfo("[Prefs] No extra resource folder yet; PlayerPrefs stays in the registry.");
                return;
            }

            if (_applied)
            {
                return;
            }

            _applied = true;

            try
            {
                Load();

                MethodInfo transpiler = AccessTools.Method(
                    typeof(PlayerPrefsRedirect),
                    nameof(ReplacePlayerPrefs));
                if (transpiler == null)
                {
                    LogError("[Prefs] Internal error: transpiler was not found.");
                    return;
                }

                _patchedCount = ApplyRedirect(harmony, transpiler);
                LogInfo(
                    $"[Prefs] PlayerPrefs -> {FilePath} " +
                    $"(redirected {_patchedCount} method(s), {Entries.Count} key(s) already stored)");
                LogInfo($"[Prefs] Patched: {string.Join(", ", _patchedNames)}");
            }
            catch (Exception exception)
            {
                LogError($"[Prefs] Failed to redirect PlayerPrefs: {exception}");
            }
        }

        private static int ApplyRedirect(Harmony harmony, MethodInfo transpiler)
        {
            int patched = 0;
            foreach (Assembly assembly in GameAssemblies())
            {
                try
                {
                    patched += RedirectAssembly(harmony, assembly, transpiler);
                }
                catch (Exception exception)
                {
                    LogWarning($"[Prefs] Could not scan '{assembly.GetName().Name}': {exception.Message}");
                }
            }

            return patched;
        }

        private static IEnumerable<Assembly> GameAssemblies()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name;
                try
                {
                    name = assembly.GetName().Name;
                }
                catch (Exception)
                {
                    continue;
                }

                if (name == "Assembly-CSharp" || name == "Assembly-CSharp-firstpass")
                {
                    yield return assembly;
                }
            }
        }

        private static int RedirectAssembly(Harmony harmony, Assembly assembly, MethodInfo transpiler)
        {
            List<MethodBase> bodies = new List<MethodBase>();
            List<byte[]> instructions = new List<byte[]>();

            foreach (Type type in PersistentDataPathRedirect.SafeGetTypes(assembly))
            {
                foreach (MethodBase method in PersistentDataPathRedirect.MethodsOf(type))
                {
                    byte[] il = PersistentDataPathRedirect.ReadIl(method);
                    if (il == null || il.Length == 0)
                    {
                        continue;
                    }

                    bodies.Add(method);
                    instructions.Add(il);
                }
            }

            if (bodies.Count == 0)
            {
                return 0;
            }

            HashSet<int> callTokens = new HashSet<int>();
            foreach (byte[] il in instructions)
            {
                PersistentDataPathRedirect.CollectCallTokens(il, callTokens);
            }

            HashSet<int> targetTokens = new HashSet<int>();
            Module module = bodies[0].Module;
            foreach (int token in callTokens)
            {
                try
                {
                    if (ReplacementFor(module.ResolveMethod(token, null, null)) != null)
                    {
                        targetTokens.Add(token);
                    }
                }
                catch (Exception)
                {
                    // 不是方法 token（字段/类型），或者需要泛型上下文，忽略。
                }
            }

            if (targetTokens.Count == 0)
            {
                return 0;
            }

            int patched = 0;
            for (int i = 0; i < bodies.Count; i++)
            {
                if (!PersistentDataPathRedirect.ContainsAnyToken(instructions[i], targetTokens))
                {
                    continue;
                }

                try
                {
                    harmony.Patch(bodies[i], transpiler: new HarmonyMethod(transpiler));
                    patched++;

                    if (_patchedNames.Count < 30)
                    {
                        _patchedNames.Add($"{bodies[i].DeclaringType?.Name}.{bodies[i].Name}");
                    }
                }
                catch (Exception exception)
                {
                    LogWarning(
                        $"[Prefs] Could not redirect " +
                        $"{bodies[i].DeclaringType?.FullName}.{bodies[i].Name}: {exception.Message}");
                }
            }

            return patched;
        }

        private static void BuildReplacementTable()
        {
            if (Replacements.Count > 0)
            {
                return;
            }

            foreach (MethodInfo method in typeof(PlayerPrefsRedirect).GetMethods(
                         BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (Array.IndexOf(Names, method.Name) < 0)
                {
                    continue;
                }

                Replacements[Key(method.Name, method.GetParameters())] = method;
            }
        }

        private static MethodInfo ReplacementFor(MethodBase method)
        {
            if (method == null || method.DeclaringType == null)
            {
                return null;
            }

            string declaringType = method.DeclaringType.FullName;
            if (declaringType != TargetTypeName && declaringType != WrapperTypeName)
            {
                return null;
            }

            BuildReplacementTable();
            Replacements.TryGetValue(
                Key(method.Name, method.GetParameters()), out MethodInfo replacement);
            return replacement;
        }

        /// <summary>
        /// 「方法名 + 参数类型」做键，用来把游戏里的调用和这边的方法一一对上。
        /// 泛型参数要展开成稳定写法（<c>KeyValuePair&lt;string,int&gt;</c>），
        /// 直接用 FullName 会把 mscorlib 的版本号也带进来。
        /// </summary>
        private static string Key(string name, ParameterInfo[] parameters)
        {
            StringBuilder builder = new StringBuilder(name).Append('(');
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(TypeKey(parameters[i].ParameterType));
            }

            return builder.Append(')').ToString();
        }

        private static string TypeKey(Type type)
        {
            if (type.IsByRef)
            {
                return TypeKey(type.GetElementType()) + "&";
            }

            if (!type.IsGenericType)
            {
                return type.FullName ?? type.Name;
            }

            StringBuilder builder = new StringBuilder(
                (type.GetGenericTypeDefinition().FullName ?? type.Name).Split('`')[0]).Append('<');
            Type[] arguments = type.GetGenericArguments();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(TypeKey(arguments[i]));
            }

            return builder.Append('>').ToString();
        }

        /// <summary>把游戏里对 PlayerPrefs 的调用换成这里的方法（签名一一对应，栈不会变）。</summary>
        private static IEnumerable<CodeInstruction> ReplacePlayerPrefs(
            IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                {
                    MethodInfo replacement = ReplacementFor(instruction.operand as MethodBase);
                    if (replacement != null)
                    {
                        instruction.operand = replacement;
                        instruction.opcode = OpCodes.Call;
                    }
                }

                yield return instruction;
            }
        }

        // ---------------------------------------------------------------- 读写入口

        public static int GetInt(string key, int defaultValue)
        {
            if (TryGet(key, out Value value))
            {
                switch (value.Kind)
                {
                    case Deleted:
                        return defaultValue;
                    case 'i':
                        return value.Int;
                    case 'f':
                        return (int)value.Float;
                    case 's':
                        return int.TryParse(value.Str, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                            ? parsed
                            : defaultValue;
                }

                return defaultValue;
            }

            // 本地文件里没这个键：先回落到原版注册表读一次（旧数据搬家），顺手记下来。
            if (!Active)
            {
                return PlayerPrefs.GetInt(key, defaultValue);
            }

            if (_wiped)
            {
                // DeleteAll 之后不该再被注册表里的旧值复活。
                return defaultValue;
            }

            int stored = PlayerPrefs.GetInt(key, defaultValue);
            SetInt(key, stored);
            return stored;
        }

        public static int GetInt(string key)
        {
            return GetInt(key, 0);
        }

        public static float GetFloat(string key, float defaultValue)
        {
            if (TryGet(key, out Value value))
            {
                switch (value.Kind)
                {
                    case Deleted:
                        return defaultValue;
                    case 'f':
                        return value.Float;
                    case 'i':
                        return value.Int;
                    case 's':
                        return float.TryParse(value.Str, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                            ? parsed
                            : defaultValue;
                }

                return defaultValue;
            }

            if (!Active)
            {
                return PlayerPrefs.GetFloat(key, defaultValue);
            }

            if (_wiped)
            {
                return defaultValue;
            }

            float stored = PlayerPrefs.GetFloat(key, defaultValue);
            SetFloat(key, stored);
            return stored;
        }

        public static float GetFloat(string key)
        {
            return GetFloat(key, 0f);
        }

        public static string GetString(string key, string defaultValue)
        {
            if (TryGet(key, out Value value))
            {
                switch (value.Kind)
                {
                    case Deleted:
                        return defaultValue;
                    case 's':
                        return value.Str;
                    case 'i':
                        return value.Int.ToString(CultureInfo.InvariantCulture);
                    case 'f':
                        return value.Float.ToString(CultureInfo.InvariantCulture);
                }

                return defaultValue;
            }

            if (!Active)
            {
                return PlayerPrefs.GetString(key, defaultValue);
            }

            if (_wiped)
            {
                return defaultValue;
            }

            string stored = PlayerPrefs.GetString(key, defaultValue);
            SetString(key, stored);
            return stored;
        }

        public static string GetString(string key)
        {
            return GetString(key, string.Empty);
        }

        public static void SetInt(string key, int value)
        {
            if (!Active)
            {
                PlayerPrefs.SetInt(key, value);
                return;
            }

            Store(key, new Value { Kind = 'i', Int = value });
        }

        public static void SetFloat(string key, float value)
        {
            if (!Active)
            {
                PlayerPrefs.SetFloat(key, value);
                return;
            }

            Store(key, new Value { Kind = 'f', Float = value });
        }

        public static void SetString(string key, string value)
        {
            if (!Active)
            {
                PlayerPrefs.SetString(key, value);
                return;
            }

            Store(key, new Value { Kind = 's', Str = value ?? string.Empty });
        }

        public static bool HasKey(string key)
        {
            Trace("HasKey", key);

            if (TryGet(key, out Value value))
            {
                return value.Kind != Deleted;
            }

            if (!Active || _wiped)
            {
                return false;
            }

            return PlayerPrefs.HasKey(key);
        }

        // ------------------------------------------------- Wizard.PlayerPrefsWrapper
        //
        // 游戏自己的包装层。这些方法体太短，Mono 会把它们内联进调用方，所以除了它们内部
        // 对 PlayerPrefs 的调用，调用它们的地方也要一起换（见 Names 的注释）。
        // 语义与 PlayerPrefsWrapper 完全一致：TRUE=1、FALSE=0。

        public static bool GetBool(KeyValuePair<string, int> id)
        {
            return GetInt(id.Key, id.Value) == 1;
        }

        public static bool GetValue(KeyValuePair<string, bool> id)
        {
            return GetInt(id.Key, id.Value ? 1 : 0) == 1;
        }

        public static int GetValue(KeyValuePair<string, int> id)
        {
            return GetInt(id.Key, id.Value);
        }

        public static float GetValue(KeyValuePair<string, float> id)
        {
            return GetFloat(id.Key, id.Value);
        }

        public static string GetValue(KeyValuePair<string, string> id)
        {
            return GetString(id.Key, id.Value);
        }

        public static void SetBool(KeyValuePair<string, int> id, bool flag)
        {
            SetInt(id.Key, flag ? 1 : 0);
        }

        public static void SetValue(KeyValuePair<string, bool> id, bool flag)
        {
            SetInt(id.Key, flag ? 1 : 0);
        }

        public static void SetValue(KeyValuePair<string, int> id, int value)
        {
            SetInt(id.Key, value);
        }

        public static void SetValue(KeyValuePair<string, float> id, float value)
        {
            SetFloat(id.Key, value);
        }

        public static void SetValue(KeyValuePair<string, string> id, string value)
        {
            SetString(id.Key, value);
        }

        public static void DeleteKey(string key)
        {
            Store(key, new Value { Kind = Deleted });
        }

        public static void DeleteAll()
        {
            Entries.Clear();
            _wiped = true;
            _dirty = true;
        }

        public static void Save()
        {
            Flush();
        }

        // ---------------------------------------------------------------- 存储

        private static bool TryGet(string key, out Value value)
        {
            Trace("read", key);

            value = null;
            if (string.IsNullOrEmpty(key))
            {
                return false;
            }

            if (!_loaded)
            {
                Load();
            }

            // 命中「已删除」也算命中：这样 GetX 走 switch 里的 Deleted 分支返回默认值，
            // 不会又回落到注册表把旧值读回来。
            return Entries.TryGetValue(key, out value);
        }

        private static void Store(string key, Value value)
        {
            Trace("write", key);

            if (key == null)
            {
                return;
            }

            if (!_loaded)
            {
                Load();
            }

            Entries[key] = value;
            _dirty = true;
        }

        private static void Load()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;

            string path = FilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            try
            {
                foreach (string rawLine in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = rawLine.TrimEnd('\r', '\n');
                    if (line.Length == 0 || line[0] == '#')
                    {
                        continue;
                    }

                    if (line == "wiped=1")
                    {
                        _wiped = true;
                        continue;
                    }

                    string[] parts = line.Split('\t');
                    if (parts.Length < 2 || parts[0].Length != 1)
                    {
                        continue;
                    }

                    string key = Decode(parts[1]);
                    if (key == null)
                    {
                        continue;
                    }

                    if (parts[0][0] == Deleted)
                    {
                        Entries[key] = new Value { Kind = Deleted };
                        continue;
                    }

                    if (parts.Length < 3)
                    {
                        continue;
                    }

                    string text = Decode(parts[2]);
                    if (text == null)
                    {
                        continue;
                    }

                    switch (parts[0][0])
                    {
                        case 'i':
                            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
                            {
                                Entries[key] = new Value { Kind = 'i', Int = intValue };
                            }

                            break;

                        case 'f':
                            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue))
                            {
                                Entries[key] = new Value { Kind = 'f', Float = floatValue };
                            }

                            break;

                        case 's':
                            Entries[key] = new Value { Kind = 's', Str = text };
                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                if (!_reportedLoadFailure)
                {
                    _reportedLoadFailure = true;
                    LogWarning($"[Prefs] Could not read '{path}': {exception.Message}");
                }
            }
        }

        private static void Flush()
        {
            _lastFlush = Time.unscaledTime;

            string path = FilePath;
            if (string.IsNullOrEmpty(path))
            {
                _dirty = false;
                return;
            }

            try
            {
                StringBuilder builder = new StringBuilder();
                builder.Append("# Shadowbus PlayerPrefs store. 本文件由插件维护，删掉等于恢复出厂设置。\n");
                if (_wiped)
                {
                    builder.Append("wiped=1\n");
                }

                foreach (KeyValuePair<string, Value> pair in Entries)
                {
                    Value value = pair.Value;
                    string key = Encode(pair.Key);
                    switch (value.Kind)
                    {
                        case 'i':
                            builder.Append("i\t").Append(key).Append('\t')
                                .Append(Encode(value.Int.ToString(CultureInfo.InvariantCulture))).Append('\n');
                            break;
                        case 'f':
                            builder.Append("f\t").Append(key).Append('\t')
                                .Append(Encode(value.Float.ToString("R", CultureInfo.InvariantCulture))).Append('\n');
                            break;
                        case 's':
                            builder.Append("s\t").Append(key).Append('\t')
                                .Append(Encode(value.Str ?? string.Empty)).Append('\n');
                            break;
                        case Deleted:
                            builder.Append("d\t").Append(key).Append('\n');
                            break;
                    }
                }

                File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
                _dirty = false;

                if (!_flushedLogged)
                {
                    _flushedLogged = true;
                    LogInfo($"[Prefs] Stored {Entries.Count} key(s) in {path}");
                }
            }
            catch (Exception exception)
            {
                if (!_reportedWriteFailure)
                {
                    _reportedWriteFailure = true;
                    LogWarning($"[Prefs] Could not write '{path}': {exception.Message}");
                }
            }
        }

        private static string Encode(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? string.Empty));
        }

        /// <summary>头 20 次读写记一条日志，用来确认重定向真的接上了（尤其是内联那条坑）。</summary>
        private static void Trace(string kind, string key)
        {
            if (_traceCount >= 20)
            {
                return;
            }

            _traceCount++;
            LogInfo($"[Prefs] {kind} '{key}' -> local store");
        }

        private static string Decode(string text)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(text));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void LogInfo(string message)
        {
            try
            {
                Plugin.Logger?.LogInfo(message);
            }
            catch (Exception)
            {
            }
        }

        private static void LogWarning(string message)
        {
            try
            {
                Plugin.Logger?.LogWarning(message);
            }
            catch (Exception)
            {
            }
        }

        private static void LogError(string message)
        {
            try
            {
                Plugin.Logger?.LogError(message);
            }
            catch (Exception)
            {
            }
        }
    }
}
