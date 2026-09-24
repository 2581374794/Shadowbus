# PatchCheck —— 补丁目标校验器

Harmony 是**按参数名**把原方法的参数注入到补丁方法里的（`__instance`、`__result`、`___字段`、`__0..__n`，
其余按名字与目标方法的形参名匹配）。名字写错时 Harmony **不会报错**，整条补丁会静默挂不上 ——
编译期完全发现不了。这个工具就是发布前把这件事查一遍。

（它取代了早期放在 `_reverse/PatchCheck.exe` 的那个版本；`_reverse/` 是开发产物目录，
随个人数据清理一起被移走了，所以这里留一份源码，随时能重建。）

## 用法

```powershell
# 先构建插件本体（工具只读 DLL，不参与构建）
dotnet build Shadowbus.csproj -c Release

# 构建并运行校验器
dotnet build _tools/PatchCheck/PatchCheck.csproj -c Release
_tools/PatchCheck/bin/Release/net48/PatchCheck.exe `
    "D:\Games\Shadowbus\Shadowverse\BepInEx\plugins\Shadowbus.dll" `
    "D:\Games\Shadowbus\Shadowverse\Shadowverse_Data\Managed\Assembly-CSharp.dll"
```

输出形如：

```
patch definition(s): 268, patched target method(s): 269, mismatch(es): 0
```

退出码 `0` = 全部匹配，`1` = 有不匹配（问题逐条打印在下面），`2` = 参数/加载错误。

排查时可用 `$env:PATCHCHECK_DEBUG=1` 打开附加信息（加载到的类型数、`[HarmonyPatch]` 属性数、
识别到的 Harmony 属性类型）。

## 它检查什么

1. **目标存在**：`[HarmonyPatch(typeof(X), "M")]` / `nameof` / `MethodType.Constructor|Getter|Setter` /
   带 `Type[]` 参数类型 都能解析；解析不到就报出来。
2. **注入名对得上**：
   - `__instance` 用在静态方法上 → 报错；`__result` 用在返回 void 的方法上 → 报错；
   - `___字段` 在目标类型（含基类）里不存在 → 报错；
   - `__0..__n` 超出目标形参个数 → 报错；
   - 其余名字必须在目标方法的形参名里找得到（**这是当年真实拦下 `isPlayer`/`isSelf`、
     `giftButton`/`_giftButton` 两类错误的那条规则**）；
   - Transpiler 的第一个 `IEnumerable<CodeInstruction>` 参数按约定跳过（Harmony 不看它的名字）。
3. **只把真正的补丁算进来**：类上带 `[HarmonyPatch]` 时，只有带 `HarmonyPrefix/Postfix/Transpiler/
   Finalizer` 属性或按约定命名（`Prefix`/`Postfix`/…）的方法才算补丁，同名的辅助方法不会误报。

## 说明

- 目标框架 `net48`、`PlatformTarget=x86`：与游戏（32 位 Unity Mono）保持一致，避免加载
  `Assembly-CSharp.dll` 时出现 `BadImageFormatException`。
- 程序集解析顺序：插件所在目录 → `BepInEx/` → `BepInEx/core` → `Shadowverse_Data/Managed` →
  游戏根目录下的 `BepInEx/core`、`BepInEx/plugins`；只做反射读取，不执行游戏代码。
- 统计口径变化：旧版只报一个"补丁方法数"（2.5.6 时为 266），现在分别报**补丁定义数**（每个
  `[HarmonyPatch]` 定义算一个，与旧口径接近）与**展开后的目标方法数**（一个定义可能匹配多个重载）。
