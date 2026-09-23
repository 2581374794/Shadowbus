using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Wizard;
using Wizard.UI.Dialog;

namespace Shadowbus
{
    /// <summary>
    /// 「其他」页（<see cref="MyPageOtherButtons"/>）的调整：
    ///
    /// · 排行榜、咨询弹窗里的「删除账号」变暗不可点（和商店一样的看门狗，每 0.1 秒重压一次）；
    /// · 「游戏指南」挪到原「入场券兑换券一览」的位置，「入场券兑换券一览」挪到原「道具获得履历」的位置；
    /// · 「道具获得履历」整个删掉（它每帧的入场动画会 SetActive(true)，所以必须从
    ///   <c>_enableOtherButtons</c> 里摘掉，只 SetActive(false) 会被重新点亮）；
    /// · 空出来的原「游戏指南」位置放一个克隆自「设定」的按钮「货币修改」，点开可以改
    ///   金币 / 以太 / 各种入场券（挑战券、杯赛入场券、卡包兑换券…），改完存进 Mods/Profile.json。
    /// </summary>
    internal static class MyPageOtherPatches
    {
        private const string CurrencyButtonName = "ShadowbusCurrencyButton";
        private const string CurrencyDialogPrefab = "UI/layoutParts/MyPage/AccountDeleteConfirmDialog";
        private const int TicketChallenge = 1;      // 挑战券（弹窗里那一行「入场券」）

        private static readonly List<InputField> Fields = new List<InputField>();

        /// <summary>最近一次 Init 过的「其他」页，用来拿咨询弹窗的 prefab。</summary>
        private static MyPageOtherButtons _otherButtons;

        private sealed class InputField
        {
            public char Kind;           // 'g' 金币 / 'e' 以太 / 't' 入场券
            public int ItemId;          // Kind == 't' 时是道具 id
            public UIInput Input;
        }

        // ------------------------------------------------------------------ 其他页

        [HarmonyPatch(typeof(MyPageOtherButtons), nameof(MyPageOtherButtons.Init))]
        [HarmonyPostfix]
        public static void MyPageOtherButtons_Init_Postfix(MyPageOtherButtons __instance)
        {
            _otherButtons = __instance;

            try
            {
                Rearrange(__instance);
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[Other] Could not rearrange the Other page: {exception}");
            }

            HomeMenuPatches.KeepDimmed(__instance, () => Dim(__instance._btnRanking));
        }

        /// <summary>咨询弹窗里的「删除账号」。</summary>
        [HarmonyPatch(typeof(DialogContactMenu), nameof(DialogContactMenu.Start))]
        [HarmonyPostfix]
        public static void DialogContactMenu_Start_Postfix(DialogContactMenu __instance)
        {
            Dim(__instance._deleteAccountButton);
            HomeMenuPatches.KeepDimmed(__instance, () => Dim(__instance._deleteAccountButton));
        }

        /// <summary>个人信息 → 大师级菜单里的「排行榜」入口。</summary>
        [HarmonyPatch(typeof(Wizard.UI.Profile.MasterMenu), "Open")]
        [HarmonyPostfix]
        public static void MasterMenu_Open_Postfix(Wizard.UI.Profile.MasterMenu __instance)
        {
            Dim(__instance._obj_rankingBtn);
            HomeMenuPatches.KeepDimmed(__instance, () => Dim(__instance._obj_rankingBtn));
        }

        // ------------------------------------------------------------------ 货币修改弹窗

        private static void Rearrange(MyPageOtherButtons buttons)
        {
            if (buttons == null)
            {
                return;
            }

            // 道具获得履历：先隐藏，再从入场动画的名单里摘掉（它每帧会 SetActive(true)）。
            UIButton history = buttons._btnItemsHistory;
            if (history != null)
            {
                history.gameObject.SetActive(false);
            }

            UIButton currency = EnsureCurrencyButton(buttons);

            UIButton[] order =
            {
                buttons._btnSetting,
                buttons._btnReplay,
                buttons._btnHelp,
                buttons._informationButton,
                currency,                       // 原「游戏指南」的位置
                buttons._btnGameGuide,          // 原「入场券兑换券一览」的位置
                buttons._btnTicketCount,        // 原「道具获得履历」的位置
                buttons._btnNotification,
                buttons._btnData,
                buttons._btnContact,
                buttons._btnPortalSite,
                buttons._btnOffcialSite,
                buttons._btnSwitchLanguage,
                buttons._btnCodeInput,
                buttons._btnAdjustSetting,
                buttons._btnReturnTitle
            };

            for (int i = 0; i < order.Length; i++)
            {
                if (order[i] != null)
                {
                    order[i].transform.SetSiblingIndex(i);
                }
            }

            List<UIButton> list = buttons._enableOtherButtons;
            if (list != null)
            {
                list.Clear();
                AddIfNotNull(list, buttons._btnProfile);
                AddIfNotNull(list, buttons._btnFriend);
                AddIfNotNull(list, buttons._btnRanking);

                foreach (UIButton button in order)
                {
                    AddIfNotNull(list, button);
                }
            }

            buttons._girdButtons.Reposition();
            Dim(buttons._btnRanking);
        }

        private static void AddIfNotNull(List<UIButton> list, UIButton button)
        {
            // 只判空：原版 Init 也是无条件加入，随后由入场动画统一 SetActive(true)。
            if (button != null)
            {
                list.Add(button);
            }
        }

        private static UIButton EnsureCurrencyButton(MyPageOtherButtons buttons)
        {
            Transform grid = buttons._girdButtons.transform;
            Transform existing = grid.Find(CurrencyButtonName);
            if (existing != null)
            {
                return existing.GetComponent<UIButton>();
            }

            UIButton source = buttons._btnSetting;
            if (source == null)
            {
                return null;
            }

            GameObject clone = UnityEngine.Object.Instantiate(source.gameObject);
            clone.name = CurrencyButtonName;
            clone.transform.SetParent(grid, false);
            clone.transform.localScale = source.transform.localScale;
            clone.transform.localRotation = source.transform.localRotation;
            clone.transform.localPosition = Vector3.zero;

            UIButton button = clone.GetComponent<UIButton>();
            if (button != null)
            {
                button.onClick.Clear();
                button.onClick.Add(new EventDelegate(OnClickCurrencyEdit));
                button.isEnabled = true;
            }

            HashSet<string> originalTexts = new HashSet<string>();
            foreach (UILabel label in source.GetComponentsInChildren<UILabel>(true))
            {
                originalTexts.Add(label.text);
            }

            foreach (UILabel label in clone.GetComponentsInChildren<UILabel>(true))
            {
                if (originalTexts.Contains(label.text))
                {
                    label.text = "自定义修改";
                }
            }

            Plugin.Logger.LogInfo("[Other] Added the 自定义修改 button (cloned from 设定).");
            return button;
        }
        private static void OnClickCurrencyEdit()
        {
            try
            {
                CreateCurrencyDialog();
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogError($"[Other] Could not open the currency dialog: {exception}");
            }
        }

        private static void CreateCurrencyDialog()
        {
            if (_otherButtons == null)
            {
                return;
            }

            // 输入框用「删除账号」那个 prefab 里的：它的 Start() 会拿 _dialog 做校验，
            // 激活必炸，所以实例化后立刻 SetActive(false)，只当模板克隆它的输入框。
            GameObject prefab = Resources.Load(CurrencyDialogPrefab) as GameObject;
            if (prefab == null)
            {
                Plugin.Logger.LogWarning($"[Other] '{CurrencyDialogPrefab}' was not found.");
                return;
            }

            GameObject template = UnityEngine.Object.Instantiate(prefab);
            template.SetActive(false);
            AccountDeleteConfirmDialog templateDialog = template.GetComponent<AccountDeleteConfirmDialog>();
            UIInput inputTemplate = templateDialog != null ? templateDialog._inputConfirmCode : null;
            if (inputTemplate == null)
            {
                Plugin.Logger.LogWarning("[Other] The delete-account prefab has no input field to clone.");
                UnityEngine.Object.Destroy(template);
                return;
            }

            // 弹窗内容整体照搬「咨询 → 咨询」那个二级弹窗（UIManager.SupportDialogPrefab）：
            // 它是一张 UITable，每行之间带下划线，正好拿来当三行的骨架。
            GameObject contactPrefab = UIManager.GetInstance().SupportDialogPrefab;
            if (contactPrefab == null)
            {
                Plugin.Logger.LogWarning("[Other] The support dialog prefab was not found.");
                UnityEngine.Object.Destroy(template);
                return;
            }

            DialogBase dialog = UIManager.GetInstance().CreateDialogClose();
            dialog.SetSize(DialogBase.Size.M);
            dialog.SetTitleLabel("自定义修改");
            dialog.SetButtonLayout(DialogBase.ButtonLayout.BlueBtn_CancelBtn);
            dialog.SetButtonText("保存");

            GameObject content = UnityEngine.Object.Instantiate(contactPrefab);

            // 原组件 Start() 会把每行文字刷成客服那几句，先把它拿掉再摆自己的行。
            DialogSupport support = content.GetComponent<DialogSupport>();
            if (support != null)
            {
                UnityEngine.Object.Destroy(support);
            }

            // 咨询弹窗的 UITable 正好 4 行（用户ID / 版本 / 系统 / 机型），正好够我放
            // 「ID / 金币 / 以太 / 入场券」四项，一项都不用藏。
            List<Transform> allRows = FindRows(content);
            List<Transform> rows = new List<Transform>(allRows);

            if (rows.Count == 0)
            {
                Plugin.Logger.LogWarning("[Other] The support dialog has no rows to reuse.");
                UnityEngine.Object.Destroy(template);
                UnityEngine.Object.Destroy(content);
                return;
            }

            // 行文字留着，其余所有 UILabel（顶部两行说明、最后那句提问、prefab 里的
            //「New Label」占位）统统收起来 —— 它们不在 UITable 里，只能按标签找。
            List<UILabel> rowLabels = new List<UILabel>();
            foreach (Transform row in rows)
            {
                rowLabels.Add(row.GetComponentInChildren<UILabel>(true));
            }

            foreach (UILabel label in content.GetComponentsInChildren<UILabel>(true))
            {
                if (label != null && !rowLabels.Contains(label))
                {
                    label.gameObject.SetActive(false);
                }
            }

            float contentWidth = GetContentWidth(content);

            // 以「半宽居中」为基准先算出当前这一版的实际框（日志里 input W wide centred at X），
            // 再按你的要求相对它调整：左端 −30% 当前宽度、右端 −45% 当前宽度
            // ⇒ 宽度 ×0.85、中心左移 0.375×当前宽度。越出面板时只整体右移，不再改宽度。
            float baseWidth = contentWidth * 0.5f;
            float currentWidth = baseWidth * 1.5f;
            float currentCenter = -baseWidth * 0.05f;

            float inputWidth = currentWidth * 0.85f;
            float inputCenter = -130f;     // 固定中心 −130
            // 不再夹在面板里：之前夹到面板左边缘，看起来就像「怎么调都不动」。

            Fields.Clear();

            string[] names = { "玩家ID", "金币", "以太", "入场券" };
            string[] values =
            {
                ProfileOfflineData.GetViewerId().ToString(),
                PlayerStaticData.UserRupyCount.ToString(),
                PlayerStaticData.UserRedEtherCount.ToString(),
                PlayerStaticData.GetItemNum(TicketChallenge).ToString()
            };
            char[] kinds = { 'v', 'g', 'e', 't' };
            int[] itemIds = { 0, 0, 0, TicketChallenge };

            for (int i = 0; i < names.Length && i < rows.Count; i++)
            {
                // 勾选行 = [左边文字][右边勾选框]。勾选框收掉，文字改成资源名，
                // 输入框挂在同一个父节点上、按文字的 y 对齐（上一版就是这里错位了）。
                Transform row = rows[i];
                HideCheckbox(row);

                UILabel rowLabel = row.GetComponentInChildren<UILabel>(true);
                if (rowLabel != null)
                {
                    rowLabel.text = names[i];
                }

                GameObject inputObject = UnityEngine.Object.Instantiate(inputTemplate.gameObject);
                inputObject.transform.SetParent(content.transform, false);
                inputObject.SetActive(true);
                ResizeWidget(inputObject, inputWidth);

                // 竖直方向：与行文字的**视觉中心**对齐（UILabel 的 transform 常挂在文字顶部，
                // 直接用 position.y 会整体高出一行，上一版就是这么偏的）。
                Vector3[] corners = rowLabel != null ? rowLabel.worldCorners : null;
                float labelCenterY = corners != null
                    ? (corners[0].y + corners[2].y) * 0.5f
                    : row.transform.position.y;
                float labelLeftX = corners != null
                    ? Mathf.Min(corners[0].x, corners[2].x)
                    : row.transform.position.x;

                Vector3 origin = content.transform.position;

                // 上一版按「行里最宽的精灵」对齐右端，结果那个精灵比可见下划线宽得多，
                // 框被推到弹窗外面去了 —— 改回固定中心（当前 −10），要挪直接给数字。
                float centerX = origin.x + inputCenter;

                inputObject.transform.position = new Vector3(
                    centerX,
                    labelCenterY,
                    origin.z);

                if (rowLabel != null)
                {
                    // 行文字摆到输入框左边（用它的实际宽度，不要压住输入框）。
                    float labelWidth = corners != null ? Mathf.Abs(corners[2].x - corners[0].x) : rowLabel.width;
                    float labelX = centerX - inputWidth * 0.5f - contentWidth * 0.02f - labelWidth * 0.5f;
                    rowLabel.transform.position = new Vector3(
                        labelX,
                        labelCenterY,
                        rowLabel.transform.position.z);
                }
                else
                {
                    labelLeftX = origin.x;
                }

                UIInput input = inputObject.GetComponent<UIInput>();
                if (input != null)
                {
                    // 输入长度：ID / 金币 / 以太最多 9 位，入场券最多 3 位，且只收数字，
                    // 到顶就再也输不进去了（NGUI 的 characterLimit + Integer 校验）。
                    input.characterLimit = kinds[i] == 't' ? 3 : 9;
                    try
                    {
                        input.validation = UIInput.Validation.Integer;
                    }
                    catch (Exception)
                    {
                        // 老版本 NGUI 没有这个枚举就当没设。
                    }

                    input.value = values[i];
                    if (input.label != null)
                    {
                        input.label.text = values[i];
                    }
                }

                Fields.Add(new InputField { Kind = kinds[i], ItemId = itemIds[i], Input = input });
            }

            // 最下面那条下划线：用到的最后一行里的线状子节点收起来。
            if (rows.Count > 0)
            {
                HideUnderline(rows[Mathf.Min(names.Length, rows.Count) - 1]);
            }

            dialog.SetObj(content);
            dialog.onPushButton1 = SaveCurrencyDialog;
            dialog.OnClose = delegate
            {
                if (template != null)
                {
                    UnityEngine.Object.Destroy(template);
                }
            };

            Plugin.Logger.LogInfo(
                $"[Other] 自定义修改 dialog: {rows.Count} tick row(s), content width {contentWidth}, " +
                $"input {inputWidth:F0} wide centred at {inputCenter:F0} " +
                $"(spans {inputCenter - inputWidth / 2f:F0}..{inputCenter + inputWidth / 2f:F0}).");
        }

        /// <summary>
        /// 收起勾选框：勾选图标就是行里那把 <c>btn_check_on/off</c> 的精灵
        /// （<c>DialogSupport</c> 用 <c>UIButton.normalSprite</c> 切换它），
        /// 直接把它的透明度压成 0，行本身的下划线和布局都不动。
        /// </summary>
        private static void HideCheckbox(Transform row)
        {
            if (row == null)
            {
                return;
            }

            // 先关按钮（UIButton 会把精灵重新上色），再动手隐藏图标。
            foreach (UIButton button in row.GetComponentsInChildren<UIButton>(true))
            {
                button.isEnabled = false;
            }

            foreach (Collider collider in row.GetComponentsInChildren<Collider>(true))
            {
                collider.enabled = false;
            }

            foreach (UISprite sprite in row.GetComponentsInChildren<UISprite>(true))
            {
                string name = sprite.spriteName;
                if (name != null && name.ToLowerInvariant().Contains("check"))
                {
                    Color color = sprite.color;
                    color.a = 0f;
                    sprite.color = color;
                    sprite.enabled = false;      // 光改颜色会被 UIButton 的状态机刷回来
                }
            }
        }

        /// <summary>收起一行的下划线：优先按名字找（line / under），找不到就找又宽又扁的 UISprite。</summary>
        private static void HideUnderline(Transform row)
        {
            if (row == null)
            {
                return;
            }

            foreach (Transform child in row)
            {
                string name = child.name.ToLowerInvariant();
                if (name.Contains("line") || name.Contains("under"))
                {
                    child.gameObject.SetActive(false);
                    return;
                }
            }

            UISprite widest = null;
            foreach (UISprite sprite in row.GetComponentsInChildren<UISprite>(true))
            {
                if (sprite.height <= 6 && (widest == null || sprite.width > widest.width))
                {
                    widest = sprite;
                }
            }

            if (widest != null)
            {
                widest.gameObject.SetActive(false);
            }
        }

        /// <summary>一行的分割线（最宽的精灵）在世界坐标里的右边缘；找不到就返回 null。</summary>
        private static float? GetUnderlineRight(Transform row)
        {
            if (row == null)
            {
                return null;
            }

            UISprite widest = null;
            foreach (UISprite sprite in row.GetComponentsInChildren<UISprite>(true))
            {
                if (!sprite.enabled)
                {
                    continue;
                }

                string name = sprite.spriteName;
                if (name != null && name.ToLowerInvariant().Contains("check"))
                {
                    continue;
                }

                if (widest == null || sprite.width > widest.width)
                {
                    widest = sprite;
                }
            }

            if (widest == null)
            {
                return null;
            }

            Vector3[] corners = widest.worldCorners;
            return Mathf.Max(corners[0].x, corners[2].x);
        }

        /// <summary>
        /// 支持弹窗是「UITable + 若干行」，行可能不是按钮（客服那几行是纯文字）。
        /// 优先用 UITable 的直接子节点，其次退回 UITable 里最上层的那批子节点。
        /// </summary>
        private static List<Transform> FindRows(GameObject content)
        {
            List<Transform> rows = new List<Transform>();

            UITable table = content.GetComponentInChildren<UITable>(true);
            Transform root = table != null ? table.transform : content.transform;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.gameObject.activeSelf)
                {
                    rows.Add(child);
                }
            }

            if (rows.Count == 0)
            {
                for (int i = 0; i < content.transform.childCount; i++)
                {
                    rows.Add(content.transform.GetChild(i));
                }
            }

            return rows;
        }

        /// <summary>内容区的宽度：先看 UIPanel，再看里面最宽的 UISprite。</summary>
        private static float GetContentWidth(GameObject content)
        {
            UIPanel panel = content.GetComponent<UIPanel>();
            if (panel == null)
            {
                panel = content.GetComponentInChildren<UIPanel>(true);
            }

            if (panel != null && panel.width > 1f)
            {
                return panel.width;
            }

            float width = 0f;
            foreach (UISprite sprite in content.GetComponentsInChildren<UISprite>(true))
            {
                if (sprite.width > width)
                {
                    width = sprite.width;
                }
            }

            return width > 1f ? width : 500f;
        }

        /// <summary>把一个控件（含它里面的 sprite / label）的宽度改成指定值，位置不动。</summary>
        private static void ResizeWidget(GameObject go, float width)
        {
            foreach (UISprite sprite in go.GetComponentsInChildren<UISprite>(true))
            {
                sprite.width = Mathf.RoundToInt(width);
            }

            foreach (UILabel label in go.GetComponentsInChildren<UILabel>(true))
            {
                label.width = Mathf.RoundToInt(width);
            }
        }

        private static void SaveCurrencyDialog()
        {
            try
            {
                int gold = 0;
                int ether = 0;
                int viewerId = 0;
                Dictionary<int, int> tickets = new Dictionary<int, int>();

                foreach (InputField field in Fields)
                {
                    int value = ParseInput(field.Input, field.Kind);
                    switch (field.Kind)
                    {
                        case 'v':
                            viewerId = value;
                            break;                        case 'g':
                            gold = value;
                            break;
                        case 'e':
                            ether = value;
                            break;
                        case 't':
                            tickets[field.ItemId] = value;
                            break;
                    }
                }

                if (viewerId > 0 && viewerId != ProfileOfflineData.GetViewerId())
                {
                    ProfileOfflineData.SaveViewerId(viewerId);

                    // ID 是客户端身份，改完要重启才彻底生效 —— 按要求保存 0.2 秒后重启整个游戏。
                    if (Plugin.Instance != null)
                    {
                        Plugin.Instance.StartCoroutine(RestartAfterSave());
                    }
                }

                ProfileOfflineData.SaveCurrencies(gold, ether, tickets);

                // 顶栏的金币/水晶数字是缓存过的，改完要让它们立刻重画（UIManager 不在时别硬调，
                // 否则会刷 Coroutine couldn't be started 的报错）。
                UIManager uiManager = UIManager.GetInstance();
                if (uiManager != null && uiManager.gameObject.activeInHierarchy)
                {
                    try
                    {
                        uiManager.UpDateCrystalNum();
                        uiManager.UpDateRupyNum();
                    }
                    catch (Exception)
                    {
                    }

                    try
                    {
                        if (MyPageMenu.Instance != null)
                        {
                            MyPageMenu.Instance.UpdateCrystalCount();
                            MyPageMenu.Instance.UpdateRupyCount();
                        }
                    }
                    catch (Exception)
                    {
                    }
                }

                Plugin.Logger.LogInfo(
                    $"[Other] Saved currencies: 金币={gold}, 以太={ether}, " +
                    $"入场券={string.Join("/", tickets)}.");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogError($"[Other] Could not save the currencies: {exception}");
            }
        }

        /// <summary>改完 ID 之后按游戏原生方式重启（SoftwareReset）；它偶尔会刷 UIManager/CRIWARE 的报错，不致命，不管。</summary>
        private static IEnumerator RestartAfterSave()
        {
            yield return new WaitForSeconds(0.2f);

            // SoftwareReset.exec() 会在 UIManager 上起协程，UIManager 不激活时会报
            // "Coroutine couldn't be started … is inactive"（日志里那两条错误就是它）。
            // 所以先等 UIManager 回来，最多等 3 秒。
            float waited = 0f;
            while (waited < 3f)
            {
                UIManager uiManager = UIManager.GetInstance();
                if (uiManager != null && uiManager.gameObject.activeInHierarchy)
                {
                    break;
                }

                waited += 0.1f;
                yield return new WaitForSeconds(0.1f);
            }

            Plugin.Logger.LogInfo("[Other] Restarting the game so the new viewer id takes effect.");
            SoftwareReset.exec();
        }

        private static int ParseInput(UIInput input, char kind)
        {
            if (input == null)
            {
                return 0;
            }

            string text = input.value;
            if (string.IsNullOrEmpty(text) && input.label != null)
            {
                text = input.label.text;
            }

            if (!int.TryParse(text, out int value))
            {
                return 0;
            }

            // 上限统一按 int 上限（10 位，已经是游戏里能存的最大值）；
            // 入场券同样是 int，不再另外压到 9999。
            return Math.Max(0, Math.Min(int.MaxValue, value));
        }

        // ------------------------------------------------------------------ 变暗

        private static void Dim(Component control)
        {
            if (control == null)
            {
                return;
            }

            UIButton button = control as UIButton;
            if (button == null)
            {
                button = control.GetComponent<UIButton>();
            }

            if (button != null)
            {
                button.isEnabled = false;
            }

            UIManager.SetObjectToGrey(control.gameObject, true);
        }

        /// <summary>排行榜那个入口是 GameObject，单独一个重载。</summary>
        private static void Dim(GameObject control)
        {
            if (control == null)
            {
                return;
            }

            UIButton button = control.GetComponent<UIButton>();
            if (button == null)
            {
                button = control.GetComponentInChildren<UIButton>(true);
            }

            if (button != null)
            {
                button.isEnabled = false;
            }

            UIManager.SetObjectToGrey(control, true);
        }
    }
}
