using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace PatchCheck
{
    /// <summary>
    /// 补丁校验器：加载插件 DLL 与游戏的 Assembly-CSharp.dll，逐个解析 [HarmonyPatch]
    /// 目标方法，并检查注入参数名能不能对上。
    ///
    /// 为什么需要它：Harmony 是**按参数名**把原方法的参数注入到补丁方法里的
    /// （`__instance`、`__result`、`___字段`、`__0..__n`，其余按名字匹配）。名字写错时
    /// Harmony 不会报错，整条补丁**静默挂不上** —— 编译期完全发现不了。
    /// 这里就把这件事查出来：目标方法是否存在、注入名与目标参数名/字段是否匹配。
    ///
    /// 用法：
    ///   PatchCheck.exe &lt;插件.dll&gt; &lt;Assembly-CSharp.dll&gt;
    /// 退出码 0 = 全部匹配；1 = 有不匹配（详情打印到 stdout）。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("usage: PatchCheck <plugin.dll> <Assembly-CSharp.dll>");
                return 2;
            }

            string pluginPath = Path.GetFullPath(args[0]);
            string gamePath = Path.GetFullPath(args[1]);
            if (!File.Exists(pluginPath) || !File.Exists(gamePath))
            {
                Console.WriteLine("input dll not found");
                return 2;
            }

            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) => Resolve(eventArgs.Name, pluginPath, gamePath);

            Assembly game;
            Assembly plugin;
            try
            {
                game = Assembly.LoadFrom(gamePath);
                plugin = Assembly.LoadFrom(pluginPath);
            }
            catch (Exception exception)
            {
                Console.WriteLine("failed to load assemblies: " + exception.Message);
                return 2;
            }

            var checker = new Checker(game);
            int patchedMethods = 0;
            int patchedDefinitions = 0;
            var mismatches = new List<string>();
            int typeCount = 0;
            int attributeCount = 0;

            foreach (Type type in SafeGetTypes(plugin))
            {
                typeCount++;
                List<CustomAttributeData> typePatches = GetPatchAttributes(type);
                attributeCount += typePatches.Count;
                foreach (MethodInfo patchMethod in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                                  BindingFlags.Static | BindingFlags.Instance |
                                                                  BindingFlags.DeclaredOnly))
                {
                    List<CustomAttributeData> methodPatches = GetPatchAttributes(patchMethod);
                    attributeCount += methodPatches.Count;
                    if (typePatches.Count == 0 && methodPatches.Count == 0)
                        continue;

                    // 类上带 [HarmonyPatch] 时，只有"带 Harmony* 方法属性"或"按约定命名"的方法
                    // 才是补丁；同名的普通辅助方法（例如 AddIfMissing）Harmony 不会去挂。
                    if (typePatches.Count > 0 && methodPatches.Count == 0 && !IsConventionalPatchName(patchMethod.Name))
                        continue;

                    // Harmony 的合并规则：方法上的属性覆盖类上的，同类属性按出现顺序累加。
                    PatchSpec spec = new PatchSpec().Merge(typePatches);
                    spec = spec.Merge(methodPatches);

                    List<MethodBase> targets = checker.FindTargets(spec, out string targetError);
                    if (targets.Count == 0)
                    {
                        mismatches.Add($"{type.Name}.{patchMethod.Name}: {targetError ?? "no patch target found"}");
                        continue;
                    }

                    patchedDefinitions++;
                    patchedMethods += targets.Count;
                    bool isTranspiler = HasAttribute(patchMethod, "HarmonyLib.HarmonyTranspiler");
                    foreach (MethodBase target in targets)
                    {
                        string problem = checker.CheckInjections(patchMethod, target, isTranspiler);
                        if (problem != null)
                            mismatches.Add($"{type.Name}.{patchMethod.Name} -> {Describe(target)}: {problem}");
                    }
                }
            }

            Console.WriteLine(
                $"patch definition(s): {patchedDefinitions}, patched target method(s): {patchedMethods}, " +
                $"mismatch(es): {mismatches.Count}");
            if (Environment.GetEnvironmentVariable("PATCHCHECK_DEBUG") == "1")
            {
                Console.WriteLine($"(debug: types={typeCount}, harmonyPatch attributes={attributeCount})");
                foreach (string attributeName in Program.AttributeNames.Where(n => n.IndexOf("Harmony", StringComparison.OrdinalIgnoreCase) >= 0).OrderBy(n => n))
                    Console.WriteLine("(debug) attribute type seen: " + attributeName);
            }
            foreach (string mismatch in mismatches)
                Console.WriteLine("  [!] " + mismatch);

            return mismatches.Count == 0 ? 0 : 1;
        }

        private static string Describe(MethodBase method)
        {
            string parameters = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
            return $"{method.DeclaringType?.FullName}.{method.Name}({parameters})";
        }

        private static bool IsConventionalPatchName(string methodName)
        {
            return methodName == "Prefix" || methodName == "Postfix" || methodName == "Transpiler" ||
                   methodName == "Finalizer" || methodName == "ReversePatch" ||
                   methodName == "Prepare" || methodName == "Cleanup";
        }

        private static bool HasAttribute(MemberInfo member, string attributeTypeName)
        {
            try
            {
                return member.GetCustomAttributesData().Any(a => a.AttributeType.FullName == attributeTypeName);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static List<CustomAttributeData> GetPatchAttributes(MemberInfo member)
        {
            var result = new List<CustomAttributeData>();
            try
            {
                foreach (CustomAttributeData attribute in member.GetCustomAttributesData())
                {
                    string name = attribute.AttributeType.FullName;
                    _attributeNames.Add(name ?? "<null>");
                    if (name == "HarmonyLib.HarmonyPatch" || name == "HarmonyLib.HarmonyPatchAttribute" ||
                        (name != null && name.EndsWith(".HarmonyPatch", StringComparison.Ordinal)))
                    {
                        result.Add(attribute);
                    }
                }
            }
            catch (Exception exception)
            {
                if (_debugPrinted < 25)
                {
                    _debugPrinted++;
                    Console.WriteLine($"(debug) attribute read failed on {member.DeclaringType?.Name}.{member.Name}: {exception.GetType().Name}: {exception.Message}");
                }
            }

            return result;
        }

        private static int _debugPrinted;

        private static readonly HashSet<string> _attributeNames = new HashSet<string>(StringComparer.Ordinal);

        internal static IEnumerable<string> AttributeNames => _attributeNames;

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null);
            }
        }

        private static Assembly Resolve(string name, string pluginPath, string gamePath)
        {
            string simpleName = new AssemblyName(name).Name + ".dll";
            var directories = new List<string>();
            AddDirectory(directories, Path.GetDirectoryName(pluginPath));                 // BepInEx/plugins
            string bepInEx = Path.GetDirectoryName(Path.GetDirectoryName(pluginPath));     // BepInEx
            AddDirectory(directories, bepInEx);
            AddDirectory(directories, Combine(bepInEx, "core"));
            string managed = Path.GetDirectoryName(gamePath);                              // Shadowverse_Data/Managed
            AddDirectory(directories, managed);
            string dataFolder = Path.GetDirectoryName(managed);                            // Shadowverse_Data
            string gameRoot = Path.GetDirectoryName(dataFolder);                            // 游戏根目录
            AddDirectory(directories, Combine(gameRoot, "BepInEx", "core"));
            AddDirectory(directories, Combine(gameRoot, "BepInEx", "plugins"));
            AddDirectory(directories, gameRoot);

            foreach (string directory in directories)
            {
                string candidate = Path.Combine(directory, simpleName);
                if (!File.Exists(candidate))
                    continue;
                try
                {
                    return Assembly.LoadFrom(candidate);
                }
                catch (Exception)
                {
                }
            }

            return null;
        }

        private static void AddDirectory(List<string> directories, string directory)
        {
            if (!string.IsNullOrEmpty(directory) && !directories.Contains(directory))
                directories.Add(directory);
        }

        private static string Combine(string left, params string[] parts)
        {
            if (string.IsNullOrEmpty(left))
                return null;
            string result = left;
            foreach (string part in parts)
                result = Path.Combine(result, part);
            return result;
        }
    }

    /// <summary>[HarmonyPatch] 里的信息（目标类型 / 方法名 / 参数类型 / 方法种类）。</summary>
    internal sealed class PatchSpec
    {
        public Type DeclaringType;
        public string MethodName;
        public Type[] ArgumentTypes;
        public string MethodType; // null / "Constructor" / "Getter" / "Setter" / "StaticConstructor"

        public PatchSpec Merge(IEnumerable<CustomAttributeData> attributes)
        {
            var result = new PatchSpec
            {
                DeclaringType = DeclaringType,
                MethodName = MethodName,
                ArgumentTypes = ArgumentTypes,
                MethodType = MethodType
            };

            foreach (CustomAttributeData attribute in attributes)
            {
                foreach (CustomAttributeTypedArgument argument in attribute.ConstructorArguments)
                {
                    Apply(result, argument.ArgumentType, argument.Value);
                }

                foreach (CustomAttributeNamedArgument argument in attribute.NamedArguments)
                {
                    Apply(result, argument.TypedValue.ArgumentType, argument.TypedValue.Value, argument.MemberName);
                }
            }

            return result;
        }

        private static void Apply(PatchSpec spec, Type type, object value, string memberName = null)
        {
            if (type == typeof(Type) && value is Type declaredType)
            {
                spec.DeclaringType = declaredType;
                return;
            }

            if (type == typeof(string) && value is string text)
            {
                if (memberName == null || memberName.Equals("methodName", StringComparison.OrdinalIgnoreCase))
                    spec.MethodName = text;
                return;
            }

            if (type == typeof(Type[]))
            {
                var values = value as IEnumerable<CustomAttributeTypedArgument>;
                if (values != null)
                {
                    spec.ArgumentTypes = values.Select(v => v.Value as Type).Where(t => t != null).ToArray();
                    return;
                }

                if (value is Type[] types)
                {
                    spec.ArgumentTypes = types;
                    return;
                }

                if (value is System.Collections.IEnumerable enumerable)
                {
                    var list = new List<Type>();
                    foreach (object item in enumerable)
                    {
                        if (item is Type itemType)
                            list.Add(itemType);
                        else if (item is CustomAttributeTypedArgument typed && typed.Value is Type nested)
                            list.Add(nested);
                    }

                    spec.ArgumentTypes = list.ToArray();
                    return;
                }

                return;
            }

            if (type.IsEnum && value != null)
            {
                // CustomAttributeData 给的是枚举的底层数值，要先映射回名字。
                string name = value is string text2 ? text2 : Enum.GetName(type, value);
                if (name == null && type.IsEnum)
                {
                    try
                    {
                        name = Enum.ToObject(type, value).ToString();
                    }
                    catch (Exception)
                    {
                    }
                }

                if (!string.IsNullOrEmpty(name) && name != "Normal")
                    spec.MethodType = name;
            }
        }
    }

    internal sealed class Checker
    {
        private const BindingFlags AnyMember = BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Static | BindingFlags.Instance;

        private readonly Assembly _game;

        public Checker(Assembly game)
        {
            _game = game;
        }

        public List<MethodBase> FindTargets(PatchSpec spec, out string error)
        {
            error = null;
            var result = new List<MethodBase>();
            Type declaringType = spec.DeclaringType;
            if (declaringType == null)
            {
                error = "no target type given";
                return result;
            }

            if (string.Equals(spec.MethodType, "Constructor", StringComparison.Ordinal) ||
                string.Equals(spec.MethodType, "StaticConstructor", StringComparison.Ordinal))
            {
                foreach (ConstructorInfo constructor in declaringType.GetConstructors(AnyMember))
                {
                    if (spec.ArgumentTypes == null || Matches(constructor.GetParameters(), spec.ArgumentTypes))
                        result.Add(constructor);
                }

                if (result.Count == 0)
                    error = $"no constructor on {declaringType.FullName} matches the given argument types";
                return result;
            }

            if (string.Equals(spec.MethodType, "Getter", StringComparison.Ordinal) ||
                string.Equals(spec.MethodType, "Setter", StringComparison.Ordinal))
            {
                string prefix = spec.MethodType == "Getter" ? "get_" : "set_";
                foreach (MethodInfo accessor in declaringType.GetMethods(AnyMember))
                {
                    if (accessor.Name == prefix + spec.MethodName || (spec.MethodName == null && accessor.Name.StartsWith(prefix, StringComparison.Ordinal)))
                        result.Add(accessor);
                }

                if (result.Count == 0)
                    error = $"no {spec.MethodType} named '{spec.MethodName}' on {declaringType.FullName}";
                return result;
            }

            MethodInfo[] methods = declaringType.GetMethods(AnyMember);
            foreach (MethodInfo method in methods)
            {
                if (spec.MethodName != null && !string.Equals(method.Name, spec.MethodName, StringComparison.Ordinal))
                    continue;
                if (spec.ArgumentTypes != null && !Matches(method.GetParameters(), spec.ArgumentTypes))
                    continue;
                result.Add(method);
            }

            if (result.Count == 0)
            {
                error = spec.MethodName == null
                    ? $"no method on {declaringType.FullName} matches the given parameter types"
                    : $"method '{spec.MethodName}' not found on {declaringType.FullName}";
            }

            return result;
        }

        private static bool Matches(ParameterInfo[] parameters, Type[] wanted)
        {
            if (parameters.Length != wanted.Length)
                return false;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].ParameterType != wanted[i])
                    return false;
            }

            return true;
        }

        /// <summary>检查补丁方法的每个参数能不能被 Harmony 注入。返回 null 表示没问题。</summary>
        public string CheckInjections(MethodInfo patchMethod, MethodBase target, bool isTranspiler)
        {
            ParameterInfo[] targetParameters = target.GetParameters();
            bool targetIsStatic = target.IsStatic;
            bool targetReturnsValue = target is MethodInfo info && info.ReturnType != typeof(void);

            foreach (ParameterInfo patchParameter in patchMethod.GetParameters())
            {
                string name = patchParameter.Name;
                if (string.IsNullOrEmpty(name))
                    continue;

                // Transpiler 的第一个参数按约定是原始 IL 流，Harmony 不看名字。
                if (isTranspiler && patchParameter.Position == 0 && IsCodeInstructionStream(patchParameter.ParameterType))
                {
                    continue;
                }

                if (name == "__instance")
                {
                    if (targetIsStatic)
                        return "__instance used but the target method is static";
                    continue;
                }

                if (name == "__result")
                {
                    if (!targetReturnsValue)
                        return "__result used but the target returns void";
                    continue;
                }

                if (name == "__state" || name == "__originalMethod" || name == "__runOriginal" ||
                    name == "__args" || name == "__resultRef" || name == "__exception" ||
                    name == "__marker" || name == "__method")
                {
                    continue;
                }

                if (name.StartsWith("___", StringComparison.Ordinal))
                {
                    string fieldName = name.Substring(3);
                    if (!FieldExists(target.DeclaringType, fieldName))
                        return $"injected field '{fieldName}' does not exist on {target.DeclaringType?.FullName} (or its base types)";
                    continue;
                }

                if (name.StartsWith("__", StringComparison.Ordinal) && name.Length > 2 && char.IsDigit(name[2]))
                {
                    if (!int.TryParse(name.Substring(2), out int index) || index >= targetParameters.Length)
                        return $"injected argument index '{name}' is out of range (target has {targetParameters.Length} parameter(s))";
                    continue;
                }

                if (name.StartsWith("__", StringComparison.Ordinal))
                {
                    // 未知的特殊名：Harmony 会当成普通名字去匹配参数名，基本一定挂不上。
                    return $"unknown injected name '{name}'";
                }

                if (!targetParameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal)))
                {
                    string available = string.Join(", ", targetParameters.Select(p => p.Name));
                    return $"parameter '{name}' does not match any target parameter ({available})";
                }
            }

            return null;
        }

        /// <summary>是不是 Harmony 的 IL 流参数（`IEnumerable&lt;CodeInstruction&gt;` / `CodeInstruction[]` 等）。</summary>
        private static bool IsCodeInstructionStream(Type type)
        {
            if (type == null)
                return false;
            if (type.Name.IndexOf("CodeInstruction", StringComparison.Ordinal) >= 0)
                return true;
            if (!type.IsGenericType)
                return false;

            return type.GetGenericArguments().Any(argument =>
                argument != null && argument.Name.IndexOf("CodeInstruction", StringComparison.Ordinal) >= 0);
        }

        private static bool FieldExists(Type type, string fieldName)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                if (current.GetField(fieldName, AnyMember) != null)
                    return true;
            }

            return false;
        }
    }
}
