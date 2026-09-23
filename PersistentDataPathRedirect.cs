using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Shadowbus
{
    /// <summary>
    /// 把游戏里**所有** <c>Application.persistentDataPath</c> 的取值换成额外资源目录。
    ///
    /// 这个属性在 Unity 里是 extern（InternalCall，没有 IL 方法体），Harmony 没法直接给它打补丁，
    /// 所以反过来做：扫出游戏程序集里所有调用它的方法，把这些调用指令换成 <see cref="Current"/>。
    /// 这样一处都不漏 —— 卡图/语音/manifest/影片、首页特殊称号的 BGM 检查、回放目录
    /// （<c>NewReplay</c> / RecordDirectory）、HTTP 下载缓存、统计日志（<c>accumulate_log</c> 等）、
    /// <c>NGUITools</c> 存档、游戏自带的 CardMaster 导出目录，全都跟着走。
    ///
    /// 额外资源目录不存在时什么都不做，游戏完全保持原样。
    /// </summary>
    internal static class PersistentDataPathRedirect
    {
        private const string TargetTypeName = "UnityEngine.Application";
        private const string TargetMethodName = "get_persistentDataPath";

        private static string _original;
        private static bool _applied;
        private static int _patchedCount;
        private static Harmony _harmony;
        private static bool _applyRequested;

        /// <summary>
        /// cctor 里拿 persistentDataPath 拼过路径的类型。这类静态字段可能在插件加载前
        /// 就已经初始化成旧路径了，光换调用指令不够，得把字段本身也改掉。
        /// </summary>
        private static readonly List<Type> PathInitializedTypes = new List<Type>();

        /// <summary>游戏实际会用的 persistentDataPath（已重定向后）。</summary>
        public static string Current
        {
            get
            {
                if (string.IsNullOrEmpty(_original))
                {
                    // 还没打过补丁（或取原值失败），直接问 Unity。
                    return Application.persistentDataPath;
                }

                if (!ResourceRootPatches.UseResourceRoot)
                {
                    return _original;
                }

                // 分隔符要跟 Unity 原生一致：Unity 在 Windows 上给的 persistentDataPath 也是
                // 正斜杠（C:/Users/.../LocalLow/...）。游戏代码里凡是拼 "<persistentDataPath>/xxx"
                // 再和 Directory.GetDirectories 的结果比字符串的地方，都默认两边都是正斜杠 ——
                // 回放就是这么判定的（ReplayDialogContent.GoReplay / ReplayDataHandler.NewStockDataPlayer：
                // `GetDirectories(...).Select(d => d.Replace("\\","/")).FirstOrDefault(f => f == path + "/" + id)`）。
                // 我们原来把资源目录（反斜杠）直接交出去，这个比较永远不成立 → 本地回放被当成
                // 「服务器回放」播 → 卡在换牌界面什么都不放。这里统一成正斜杠。
                return ResourceRootPatches.ResourceRoot?.Replace('\\', '/');
            }
        }

        /// <summary>重定向了多少个方法，给日志用。</summary>
        public static int PatchedCount => _patchedCount;

        /// <summary>插件启动时把 Harmony 实例记下来，方便之后补打补丁。</summary>
        public static void SetHarmony(Harmony harmony)
        {
            _harmony = harmony;
        }

        /// <summary>
        /// 资源目录是后来才出现的：请求在下一帧（Plugin.Update）补打重定向补丁。
        /// </summary>
        public static void RequestApply()
        {
            if (!_applied)
            {
                _applyRequested = true;
            }
        }

        /// <summary>Plugin.Update 每帧调用：把待处理的补打做掉。</summary>
        public static void Tick()
        {
            if (!_applyRequested || _applied)
            {
                _applyRequested = false;
                return;
            }

            _applyRequested = false;

            try
            {
                if (_harmony == null)
                {
                    _harmony = new Harmony("GeorgesZebit.Shadowbus.resources");
                }

                Apply(_harmony);
            }
            catch (Exception exception)
            {
                LogError($"[Resources] Deferred persistentDataPath redirect failed: {exception}");
            }
        }

        /// <summary>
        /// 必须在游戏读任何资源之前调用（插件的 Awake 里）。
        /// 资源目录还不存在时不会把自己锁死，之后 RequestApply/Tick 还能补上。
        /// </summary>
        public static void Apply(Harmony harmony)
        {
            _harmony = harmony;

            if (!ResourceRootPatches.UseResourceRoot)
            {
                LogInfo(
                    "[Resources] No extra resource folder yet; Application.persistentDataPath " +
                    $"stays at '{Application.persistentDataPath}' until one appears.");
                return;
            }

            if (_applied)
            {
                return;
            }

            _applied = true;

            try
            {
                // 先取原值：万一后面哪一步失败，回退时还是原来的路径。
                try
                {
                    _original = Application.persistentDataPath;
                }
                catch (Exception exception)
                {
                    LogWarning($"[Resources] Could not read the original persistentDataPath: {exception.Message}");
                }

                ApplyRedirect(harmony);
                RewritePreInitializedPaths();
            }
            catch (Exception exception)
            {
                LogError($"[Resources] Failed to redirect Application.persistentDataPath: {exception}");
            }
        }

        /// <summary>扫描并打补丁（不含取原值那一步，方便单独验证）。</summary>
        internal static int ApplyRedirect(Harmony harmony)
        {
            MethodInfo transpiler = AccessTools.Method(
                typeof(PersistentDataPathRedirect),
                nameof(ReplacePersistentDataPath));
            if (transpiler == null)
            {
                LogError("[Resources] Internal error: transpiler was not found.");
                return 0;
            }

            int patched = 0;
            int failed = 0;
            foreach (Assembly assembly in GameAssemblies())
            {
                try
                {
                    patched += RedirectAssembly(harmony, assembly, transpiler, ref failed);
                }
                catch (Exception exception)
                {
                    LogWarning($"[Resources] Could not scan '{assembly.GetName().Name}': {exception.Message}");
                }
            }

            _patchedCount = patched;
            LogInfo(
                $"[Resources] Application.persistentDataPath -> {Current} " +
                $"(redirected {patched} method(s)" +
                (failed > 0 ? $", {failed} failed)" : ")"));
            return patched;
        }

        /// <summary>
        /// 有些路径是游戏在启动最早期（插件加载之前）就在静态构造函数里拼好存进静态字段的
        /// （比如 <c>Wizard.LocalLog</c> 的那几个统计日志路径）。这些字段已经在用旧目录了，
        /// 光改调用指令没用，这里把字段值本身的前缀换成额外资源目录。
        /// 如果那个 cctor 还没跑过，读字段会触发它 —— 那时重定向已经生效，拿到的就是新目录。
        /// </summary>
        private static int RewritePreInitializedPaths()
        {
            string root = ResourceRootPatches.ResourceRoot;
            if (string.IsNullOrEmpty(_original) || string.IsNullOrEmpty(root) ||
                PathInitializedTypes.Count == 0)
            {
                return 0;
            }

            string oldRoot = _original.Replace('\\', '/').TrimEnd('/');
            int rewritten = 0;

            foreach (Type type in PathInitializedTypes)
            {
                FieldInfo[] fields;
                try
                {
                    fields = type.GetFields(
                        BindingFlags.Static | BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (FieldInfo field in fields)
                {
                    if (field.FieldType != typeof(string))
                    {
                        continue;
                    }

                    string value;
                    try
                    {
                        value = field.GetValue(null) as string;
                    }
                    catch (Exception)
                    {
                        // cctor 抛异常之类，跳过。
                        continue;
                    }

                    if (string.IsNullOrEmpty(value))
                    {
                        continue;
                    }

                    string normalized = value.Replace('\\', '/');
                    if (!normalized.StartsWith(oldRoot + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string relative = normalized.Substring(oldRoot.Length).TrimStart('/');
                    string replacement = Path.Combine(
                        root,
                        relative.Replace('/', Path.DirectorySeparatorChar));

                    try
                    {
                        field.SetValue(null, replacement);
                        rewritten++;
                        LogInfo(
                            $"[Resources] {type.FullName}.{field.Name} -> {replacement}");
                    }
                    catch (Exception exception)
                    {
                        LogWarning(
                            $"[Resources] Could not fix {type.FullName}.{field.Name}: {exception.Message}");
                    }
                }
            }

            if (rewritten > 0)
            {
                LogInfo($"[Resources] Rewrote {rewritten} pre-initialized path field(s).");
            }

            return rewritten;
        }

        private static void LogInfo(string message)
        {
            try
            {
                Plugin.Logger?.LogInfo(message);
            }
            catch (Exception)
            {
                // 日志本身不能影响补丁。
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

                if (name != null &&
                    (name == "Assembly-CSharp" || name == "Assembly-CSharp-firstpass"))
                {
                    yield return assembly;
                }
            }
        }

        internal static int RedirectAssembly(
            Harmony harmony,
            Assembly assembly,
            MethodInfo transpiler,
            ref int failed)
        {
            List<MethodBase> bodies = new List<MethodBase>();
            List<byte[]> instructions = new List<byte[]>();

            foreach (Type type in SafeGetTypes(assembly))
            {
                foreach (MethodBase method in MethodsOf(type))
                {
                    byte[] il = ReadIl(method);
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

            // 先把"call/callvirt 后面跟的所有 token"收集起来，再逐个解析：
            // 同一个 token 在整份程序集里会出现成千上万次，解析一次就够。
            HashSet<int> callTokens = new HashSet<int>();
            foreach (byte[] il in instructions)
            {
                CollectCallTokens(il, callTokens);
            }

            HashSet<int> targetTokens = new HashSet<int>();
            Module module = bodies[0].Module;
            foreach (int token in callTokens)
            {
                try
                {
                    if (IsPersistentDataPathGetter(module.ResolveMethod(token, null, null)))
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
                if (!ContainsAnyToken(instructions[i], targetTokens))
                {
                    continue;
                }

                try
                {
                    harmony.Patch(bodies[i], transpiler: new HarmonyMethod(transpiler));
                    patched++;

                    // cctor 里的路径可能已经存进静态字段了，记下来待会儿一起修。
                    if (bodies[i] is ConstructorInfo constructor && constructor.IsStatic)
                    {
                        Type declaringType = constructor.DeclaringType;
                        if (declaringType != null && !PathInitializedTypes.Contains(declaringType))
                        {
                            PathInitializedTypes.Add(declaringType);
                        }
                    }
                }
                catch (Exception exception)
                {
                    failed++;
                    LogWarning(
                        $"[Resources] Could not redirect " +
                        $"{bodies[i].DeclaringType?.FullName}.{bodies[i].Name}: {exception.Message}");
                }
            }

            return patched;
        }

        internal static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                List<Type> loaded = new List<Type>();
                foreach (Type type in exception.Types)
                {
                    if (type != null)
                    {
                        loaded.Add(type);
                    }
                }

                return loaded;
            }
            catch (Exception)
            {
                return new Type[0];
            }
        }

        private const BindingFlags DeclaredMembers =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
            BindingFlags.Instance | BindingFlags.DeclaredOnly;

        internal static IEnumerable<MethodBase> MethodsOf(Type type)
        {
            MethodInfo[] methods;
            try
            {
                methods = type.GetMethods(DeclaredMembers);
            }
            catch (Exception)
            {
                methods = new MethodInfo[0];
            }

            foreach (MethodInfo method in methods)
            {
                yield return method;
            }

            ConstructorInfo[] constructors;
            try
            {
                constructors = type.GetConstructors(DeclaredMembers);
            }
            catch (Exception)
            {
                constructors = new ConstructorInfo[0];
            }

            foreach (ConstructorInfo constructor in constructors)
            {
                yield return constructor;
            }

            ConstructorInfo typeInitializer = null;
            try
            {
                typeInitializer = type.TypeInitializer;
            }
            catch (Exception)
            {
                // 忽略
            }

            if (typeInitializer != null)
            {
                yield return typeInitializer;
            }
        }

        internal static byte[] ReadIl(MethodBase method)
        {
            try
            {
                MethodBody body = method.GetMethodBody();
                return body?.GetILAsByteArray();
            }
            catch (Exception)
            {
                // 抽象方法/外部方法没有方法体。
                return null;
            }
        }

        internal static void CollectCallTokens(byte[] il, HashSet<int> tokens)
        {
            for (int i = 0; i + 4 < il.Length; i++)
            {
                byte opcode = il[i];
                if (opcode != 0x28 && opcode != 0x39)
                {
                    // 0x28 = call，0x39 = callvirt
                    continue;
                }

                tokens.Add(BitConverter.ToInt32(il, i + 1));
            }
        }

        internal static bool ContainsAnyToken(byte[] il, HashSet<int> tokens)
        {
            for (int i = 0; i + 4 < il.Length; i++)
            {
                byte opcode = il[i];
                if (opcode != 0x28 && opcode != 0x39)
                {
                    continue;
                }

                if (tokens.Contains(BitConverter.ToInt32(il, i + 1)))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPersistentDataPathGetter(MethodBase method)
        {
            return method != null &&
                   method.Name == TargetMethodName &&
                   method.DeclaringType != null &&
                   method.DeclaringType.FullName == TargetTypeName;
        }

        /// <summary>把 <c>Application.persistentDataPath</c> 的取值指令换成 <see cref="Current"/>。</summary>
        private static IEnumerable<CodeInstruction> ReplacePersistentDataPath(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo replacement = AccessTools.Method(
                typeof(PersistentDataPathRedirect),
                nameof(RedirectedOnStack));

            foreach (CodeInstruction instruction in instructions)
            {
                bool isGetterCall =
                    (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                    IsPersistentDataPathGetter(instruction.operand as MethodBase);

                if (!isGetterCall || replacement == null)
                {
                    yield return instruction;
                    continue;
                }

                // 原地替换，标签和异常块要跟着搬过去，否则跳转目标会丢。
                CodeInstruction replaced = new CodeInstruction(OpCodes.Call, replacement);
                replaced.labels.AddRange(instruction.labels);
                replaced.blocks.AddRange(instruction.blocks);
                yield return replaced;
            }
        }

        /// <summary>
        /// 给 IL 用的取值入口。故意不碰 Application.persistentDataPath 的"打补丁后"路径，
        /// 免得自己递归自己（本程序集不在被扫描的范围里，这里只是保险）。
        /// </summary>
        public static string RedirectedOnStack()
        {
            return Current;
        }
    }
}
