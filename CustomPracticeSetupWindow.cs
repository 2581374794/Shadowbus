using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cute;
using Shadowbus.LLMAI;
using UnityEngine;
using Wizard;
using Wizard.Dialog.Setting;

namespace Shadowbus
{
    internal sealed class CustomPracticeSetupWindow : MonoBehaviour
    {
        private const int LeadersPerPage = 10;
        private const float ColumnScale = 0.78f;

        private sealed class AIPresetChoice
        {
            public PracticeAISettingData Setting;
            public string Label;
        }

        private sealed class CsvChoice
        {
            public string Label;
            public string Path;
        }

        /// <summary>
        /// 一条官方剧情敌方 AI：master 的 story_ai_setting 一行，加上从资源目录导出的
        /// 那三份 CSV 的本地路径（找不到就走 master 里的 AI 资源）。
        /// Setting 为 null 的那条是固定的「无」，用来取消剧情 AI 模式。
        /// </summary>
        private sealed class StoryAiChoice
        {
            public StoryAISettingData Setting;
            public string DeckName;
            public string DeckCsvPath;
            public string StyleCsvPath;
            public string EmoteCsvPath;
            /// <summary>敌方主战者。从表情表的 voice_id 前缀推出来（500211_003_100 -> 500211）。</summary>
            public int EnemyCharaId;
            /// <summary>敌方职业 = 敌方主战者的职业，必须与主战者一致，否则会构筑出空牌组。</summary>
            public int EnemyClassId;
            /// <summary>官方牌组展开后的卡表（40 张），用来真正接管敌方卡组。</summary>
            public List<int> DeckCardIds;

            public string Label => Setting == null
                ? "无（自定义对手AI数据）"
                : string.Format(
                    "AI {0} · {1} · 逻辑 {2}",
                    Setting.EnemyAiId,
                    string.IsNullOrEmpty(DeckName) ? "未知牌组" : DeckName,
                    Setting.LogicLevel);
        }

        private DialogBase _dialog;
        private ClassSelectionPage _page;
        private List<AIManager.CustomPracticeDeckChoice> _decks;
        private List<AIManager.CustomPracticeDeckChoice> _playerDecks;
        private int _deckIndex;
        private int _playerDeckIndex;
        private int _classId;
        private int _playerClassId;
        private List<ClassCharacterMasterData> _leaders;
        private int _leaderIndex;
        private int _leaderPageIndex;
        private List<AIPresetChoice> _presets;
        private int _presetIndex;
        private List<CsvChoice> _deckCsvChoices;
        private List<CsvChoice> _styleCsvChoices;
        private List<CsvChoice> _emoteCsvChoices;
        private int _deckCsvIndex;
        private int _styleCsvIndex;
        private int _emoteCsvIndex;
        private int _logicLevel;
        private int _maxLife;
        private bool _enableLLMAI;
        /// <summary>「斗蛐蛐」模式：我方也交给 AI 打（AI 对 AI）。</summary>
        private bool _dualAi;
        private bool _isStarting;
        private bool _updatingLifeSlider;
        private bool _isDestroyed;
        private int _leaderBuildVersion;

        // Initialize 会先跑 SelectDeck/SelectPlayerDeck 再跑 BuildNativeUi，所以在那之前
        // 任何刷新 UI 的调用都必须是无副作用的。踩过一次坑：早期版本在这里抛 NRE，
        // 整个对话框构建失败、按钮点了没反应。
        private bool _uiReady;

        // 「剧情 AI」：直接用官方那一套剧情敌方 AI（Resources/story_ai 里导出的 CSV）。
        // 与自定义 AI（卡组 / 原作预设 / 三份 CSV）以及 AI 逻辑互斥：选了剧情 AI，
        // 那两组都变暗；列表第一项是固定的「无」，选它就能取消。
        private List<StoryAiChoice> _storyAiChoices;
        private int _storyAiIndex;

        private GameObject _contentRoot;
        private GameObject _leaderStripRoot;
        private UILabel _leaderNameLabel;
        private UILabel _validationLabel;
        private UILabel _leaderPageLabel;
        private ItemSlider _lifeSlider;
        private UIButton _leaderPreviousButton;
        private UIButton _leaderNextButton;
        private UIButton _deckButton;
        private UIButton _playerDeckButton;
        private UIButton _presetButton;
        private UIButton _deckCsvButton;
        private UIButton _styleCsvButton;
        private UIButton _emoteCsvButton;
        private UIButton _llmAIButton;
        private UIButton _storyAiButton;
        private readonly List<UIButton> _classButtons = new List<UIButton>();
        private readonly List<UIButton> _logicButtons = new List<UIButton>();
        private readonly List<SelectRandomSkinButton> _leaderButtons = new List<SelectRandomSkinButton>();
        private List<string> _loadedLeaderPaths = new List<string>();
        private SettingBase _settingTemplate;
        private SelectRandomSkinDialog _skinDialogTemplate;

        public void Initialize(
            DialogBase dialog,
            ClassSelectionPage page,
            List<AIManager.CustomPracticeDeckChoice> decks,
            bool dualAi = false)
        {
            _dialog = dialog;
            _page = page;
            _decks = decks;
            _dualAi = dualAi;
            _playerDecks = BuildPlayerDeckChoices(decks);
            _logicLevel = 2;
            _maxLife = 20;
            _enableLLMAI = LLMAITurnController.DefaultEnabled;

            try
            {
                RefreshCsvChoices();
                SelectDeck(0, false);
                SelectPlayerDeck(0, false);
                BuildNativeUi();
                RefreshAllControls();
                BeginRebuildLeaderButtons();
            }
            catch
            {
                _dialog.CloseSoon();
                throw;
            }
        }

        private static List<AIManager.CustomPracticeDeckChoice> BuildPlayerDeckChoices(
            List<AIManager.CustomPracticeDeckChoice> availableDecks)
        {
            var choices = new List<AIManager.CustomPracticeDeckChoice>();
            try
            {
                DataMgr dataMgr = GameMgr.GetIns().GetDataMgr();
                List<int> currentCardIds = dataMgr.GetCurrentDeckData()?.ToList();
                if (currentCardIds != null && currentCardIds.Count > 0)
                {
                    int classId = dataMgr.GetPlayerClassId();
                    var currentDeck = new DeckData(Format.Unlimited, DeckAttributeType.CustomDeck);
                    currentDeck.SetDeckID(-200000000);
                    currentDeck.SetDeckName("当前玩家卡组");
                    currentDeck.SetDeckClassID(classId);
                    currentDeck.SetDeckSubClassID(10);
                    currentDeck.SetDeckSleeveID(3000011L);
                    currentDeck.SetDeckIsComplete(true);
                    currentDeck.SetCardIdList(currentCardIds);
                    choices.Add(new AIManager.CustomPracticeDeckChoice
                    {
                        Deck = currentDeck,
                        EnemyClassId = classId
                    });
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    $"[AIManager] Failed to prepare the current player deck choice: {exception.Message}");
            }

            choices.AddRange(availableDecks ?? new List<AIManager.CustomPracticeDeckChoice>());
            return choices;
        }

        private void BuildNativeUi()
        {
            _dialog.SetSize(DialogBase.Size.XL);
            _dialog.SetTitleLabel("自定义练习");
            _dialog.SetButtonLayout(DialogBase.ButtonLayout.BlueBtn_CancelBtn);
            _dialog.SetButtonText("开始对战", "取消");
            _dialog.SetButtonDelegate(StartBattle);
            _dialog.isNotCloseWindowButton1 = true;
            _dialog.DetailMsg.gameObject.SetActive(false);

            _contentRoot = new GameObject("CustomPracticeNativeContent");
            _contentRoot.layer = _dialog.gameObject.layer;
            // The native XL dialog leaves less vertical room at lower resolutions than the
            // original game UI. Keep the whole custom page inside the dialog while retaining
            // readable text and hit targets.
            _contentRoot.transform.localScale = new Vector3(0.80f, 0.80f, 1f);

            _settingTemplate = UIManager.GetInstance().OptionSettingPrefab;
            if (_settingTemplate == null)
            {
                throw new InvalidOperationException("OptionSettingPrefab is unavailable.");
            }

            GameObject skinDialogPrefab = Resources.Load<GameObject>("UI/layoutParts/Dialog/SelectRandomSkinDialog");
            if (skinDialogPrefab == null)
            {
                throw new InvalidOperationException("SelectRandomSkinDialog resource is unavailable.");
            }

            _skinDialogTemplate = skinDialogPrefab.GetComponent<SelectRandomSkinDialog>();
            if (_skinDialogTemplate == null)
            {
                throw new InvalidOperationException("SelectRandomSkinDialog component is unavailable.");
            }

            // 整页分两栏：
            //   左 = 「我方设置」→ 我方卡组 ；「对手设置」→ 选择剧情AI / 生命上限 / 对手皮肤职业 + 职业按钮
            //   右 = LLM AI ；「自定义对手 AI 数据」→ 对手卡组 / 选择官方预设AI / AI 逻辑 /
            //        牌组 / 风格 / 表情 CSV ，最下面 刷新 CSV。
            CreateSectionHeader("我方设置", new Vector3(-500f, 190f, 0f));
            CreateLabel("我方卡组", new Vector3(-500f, 142f, 0f), 95, 34, 18, NGUIText.Alignment.Left);
            _playerDeckButton = CreateNativeButton(
                string.Empty,
                new Vector3(-240f, 142f, 0f),
                300,
                38,
                OpenPlayerDeckDialog);

            CreateSectionHeader("对手设置", new Vector3(-500f, 96f, 0f));

            // 「选择剧情AI」排在「对手设置」下面第一栏。
            // 标签宽度必须 <= 110：按钮左沿在 -390，标签框再宽就会压到按钮下面被盖住。
            // 再往左挪 1.5 个字号（27）让文字和按钮之间留出空隙。
            CreateLabel("选择剧情AI", new Vector3(-527f, 48f, 0f), 110, 34, 18, NGUIText.Alignment.Left);
            _storyAiButton = CreateNativeButton(
                string.Empty,
                new Vector3(-240f, 48f, 0f),
                300,
                38,
                OpenStoryAiDialog);

            // 「对手卡组」已移到右栏（与左栏「我方卡组」同一水平线），这里放「生命上限」，
            // 「对手皮肤职业」+ 8 个职业按钮整体下移到滑块下面。
            _lifeSlider = NGUITools.AddChild(_contentRoot, _settingTemplate.m_itemSlider).GetComponent<ItemSlider>();
            _lifeSlider.name = "MaxLifeSlider";
            _lifeSlider.transform.localPosition = new Vector3(-270f, 2f, 0f);
            _lifeSlider.transform.localScale = new Vector3(ColumnScale, ColumnScale, 1f);
            _lifeSlider.SetTitleLabel("生命上限");
            _lifeSlider.SetActive_SeparatorLine(false);
            _lifeSlider.m_slider.numberOfSteps = 100;
            _lifeSlider.AddChangeCallback(OnLifeSliderChanged);

            // 「对手皮肤职业」比原来的「职业」长两个字，标签框左移并加宽，避免被职业按钮挡住
            // （第一个职业按钮覆盖 -398 起，文字必须止步于 -400 之前）。
            CreateLabel("对手皮肤职业", new Vector3(-515f, -50f, 0f), 115, 32, 18, NGUIText.Alignment.Left);
            CreateClassButtons();

            // 右栏第一、二行：LLM AI 与大标题整体上移，让大标题和左栏「我方设置」同一水平线。
            _llmAIButton = CreateNativeButton(
                string.Empty,
                new Vector3(390f, 234f, 0f),
                190,
                34,
                ToggleLLMAI);

            CreateSectionHeader("自定义对手 AI 数据", new Vector3(20f, 190f, 0f), 480);

            // 从左侧移过来的「对手卡组」，与「我方卡组」同高（142）。
            CreateLabel("对手卡组", new Vector3(20f, 142f, 0f), 95, 34, 18, NGUIText.Alignment.Left);
            _deckButton = CreateNativeButton(
                string.Empty,
                new Vector3(335f, 142f, 0f),
                300,
                38,
                OpenDeckDialog);

            // 「选择官方预设AI」与我方卡组按钮同尺寸（300x38），长预设名才不会被迫缩小字号。
            // 按你的要求左移的是**标签框**（不是按钮）：标签 x 20 -> 2，按钮仍在 335。
            CreateLabel("选择官方预设AI", new Vector3(2f, 96f, 0f), 160, 34, 18, NGUIText.Alignment.Left);
            _presetButton = CreateNativeButton(
                string.Empty,
                new Vector3(335f, 96f, 0f),
                300,
                38,
                OpenPresetDialog);

            // AI 逻辑：弱 / 中 / 强 直接放在这一行右边，不另起一行，且三个按钮的整体
            // 横向范围与上面那些按钮一致（215..405）。
            CreateLabel("AI 逻辑", new Vector3(20f, 52f, 0f), 130, 32, 17, NGUIText.Alignment.Left);
            CreateLogicButtons();

            CreateLabel("牌组 CSV", new Vector3(20f, 8f, 0f), 130, 32, 17, NGUIText.Alignment.Left);
            _deckCsvButton = CreateNativeButton(
                string.Empty,
                new Vector3(310f, 8f, 0f),
                190,
                38,
                () => OpenCsvDialog("选择牌组 CSV", _deckCsvChoices, _deckCsvIndex, index => _deckCsvIndex = index));

            CreateLabel("风格 CSV", new Vector3(20f, -36f, 0f), 130, 32, 17, NGUIText.Alignment.Left);
            _styleCsvButton = CreateNativeButton(
                string.Empty,
                new Vector3(310f, -36f, 0f),
                190,
                38,
                () => OpenCsvDialog("选择风格 CSV", _styleCsvChoices, _styleCsvIndex, index => _styleCsvIndex = index));

            CreateLabel("表情 CSV", new Vector3(20f, -80f, 0f), 130, 32, 17, NGUIText.Alignment.Left);
            _emoteCsvButton = CreateNativeButton(
                string.Empty,
                new Vector3(310f, -80f, 0f),
                190,
                38,
                () => OpenCsvDialog("选择表情 CSV", _emoteCsvChoices, _emoteCsvIndex, index => _emoteCsvIndex = index));

            CreateNativeButton("刷新 CSV", new Vector3(425f, -124f, 0f), 120, 32, RefreshCsvChoicesAndControls);

            // 主战者标题往上收，标题与下方名字之间拉开距离。
            CreateSectionHeader("对手皮肤", new Vector3(-500f, -174f, 0f), 1000);
            _leaderStripRoot = new GameObject("LeaderStrip");
            _leaderStripRoot.transform.parent = _contentRoot.transform;
            _leaderStripRoot.transform.localPosition = new Vector3(0f, -222f, 0f);
            _leaderStripRoot.transform.localScale = Vector3.one;
            _leaderStripRoot.layer = _contentRoot.layer;

            CreateLeaderPageButtons();
            // 名字往下让开主战者列表，页码跟着标题一起上移。
            _leaderNameLabel = CreateLabel(string.Empty, new Vector3(0f, -268f, 0f), 520, 26, 15, NGUIText.Alignment.Center);
            _leaderPageLabel = CreateLabel(string.Empty, new Vector3(445f, -174f, 0f), 100, 26, 14, NGUIText.Alignment.Right);
            // 校验/LLM 报错放页面最上方：放底部会压住主战者名字。
            _validationLabel = CreateLabel(string.Empty, new Vector3(0f, 260f, 0f), 900, 26, 14, NGUIText.Alignment.Center);
            _validationLabel.color = new Color(1f, 0.72f, 0.72f, 1f);

            _dialog.SetObj(_contentRoot, Vector3.zero);
            _contentRoot.transform.localScale = new Vector3(0.80f, 0.80f, 1f);
            _uiReady = true;

            LoadStoryAiChoices();
            DeduplicateTitleLabel();
        }

        /// <summary>
        /// 顶部标题只保留一个。这个对话框的标题在打开时（AIManager）已经设过一次，
        /// 若 prefab 里还带一份自己的标题文本，页面上就会出现两个「自定义练习」。
        /// 这里把除 DialogBase.titleLabel 以外、文字与标题相同的标签清空。
        /// </summary>
        private void DeduplicateTitleLabel()
        {
            try
            {
                string title = _dialog.GetTitleLabelStr();
                if (string.IsNullOrEmpty(title))
                {
                    return;
                }

                UILabel keep = _dialog.titleLabel;
                int cleared = 0;
                foreach (UILabel label in _dialog.GetComponentsInChildren<UILabel>(true))
                {
                    if (label == null || label == keep)
                    {
                        continue;
                    }

                    if (string.Equals(label.text, title, StringComparison.Ordinal))
                    {
                        label.text = string.Empty;
                        cleared++;
                    }
                }

                // 也可能有第二份在职业选择页那一层（对话框换 Size 会重建自己的标题）。
                if (_page != null)
                {
                    foreach (UILabel label in _page.GetComponentsInChildren<UILabel>(true))
                    {
                        if (label == null ||
                            string.Equals(label.text, title, StringComparison.Ordinal) == false)
                        {
                            continue;
                        }

                        label.text = string.Empty;
                        cleared++;
                    }
                }

                if (cleared > 0)
                {
                    Plugin.Logger.LogInfo(
                        $"[AIManager] Cleared {cleared} duplicated title label(s) reading '{title}'.");
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[AIManager] Could not deduplicate the dialog title: {exception.Message}");
            }
        }

        private void CreateSectionHeader(string text, Vector3 position, int width = 470)
        {
            UILabel label = CreateLabel(text, position, width, 34, 22, NGUIText.Alignment.Left);
            label.fontStyle = FontStyle.Bold;

            GameObject lineObject = NGUITools.AddChild(_contentRoot, _dialog.titleLine.gameObject);
            lineObject.name = text + "Line";
            UISprite line = lineObject.GetComponent<UISprite>();
            line.ResetAnchors();
            line.SetDimensions(width, Mathf.Max(2, line.height));
            line.pivot = UIWidget.Pivot.Left;
            lineObject.transform.localPosition = position + new Vector3(0f, -23f, 0f);
            lineObject.transform.localScale = Vector3.one;
            lineObject.SetActive(true);
        }

        private UILabel CreateLabel(
            string text,
            Vector3 position,
            int width,
            int height,
            int fontSize,
            NGUIText.Alignment alignment)
        {
            GameObject labelObject = NGUITools.AddChild(_contentRoot, _dialog.DetailMsg.gameObject);
            labelObject.name = "Label_" + text;
            UILabel label = labelObject.GetComponent<UILabel>();
            label.ResetAnchors();
            label.pivot = alignment == NGUIText.Alignment.Center
                ? UIWidget.Pivot.Center
                : alignment == NGUIText.Alignment.Right
                    ? UIWidget.Pivot.Right
                    : UIWidget.Pivot.Left;
            label.alignment = alignment;
            label.overflowMethod = UILabel.Overflow.ShrinkContent;
            label.SetDimensions(width, height);
            label.fontSize = fontSize;
            label.text = text;
            labelObject.transform.localPosition = position;
            labelObject.transform.localScale = Vector3.one;
            labelObject.SetActive(true);
            return label;
        }

        private void CreateClassButtons()
        {
            string[] names = Enumerable.Range(1, 8)
                .Select(id => GameMgr.GetIns().GetDataMgr().GetClanNameByKey(id))
                .ToArray();

            for (int i = 0; i < names.Length; i++)
            {
                int classId = i + 1;
                float x = -350f + i % 4 * 100f;
                float y = -50f - i / 4 * 42f;
                UIButton button = CreateNativeButton(
                    names[i],
                    new Vector3(x, y, 0f),
                    96,
                    36,
                    () => SelectClass(classId));
                _classButtons.Add(button);
            }
        }

        private void CreateLogicButtons()
        {
            string[] labels = { "弱", "中", "强" };
            for (int i = 0; i < labels.Length; i++)
            {
                int logicLevel = i;
                UIButton button = CreateNativeButton(
                    labels[i],
                    new Vector3(245f + i * 65f, 52f, 0f),
                    60,
                    36,
                    () =>
                    {
                        _logicLevel = logicLevel;
                        UpdateLogicButtons();
                    });
                _logicButtons.Add(button);
            }
        }

        private UIButton CreateNativeButton(
            string text,
            Vector3 position,
            int width,
            int height,
            Action onClick)
        {
            GameObject itemObject = NGUITools.AddChild(_contentRoot, _settingTemplate.m_itemButton);
            itemObject.name = "Button_" + text;
            itemObject.transform.localPosition = position;
            itemObject.transform.localScale = Vector3.one;
            itemObject.SetActive(true);

            ItemButton item = itemObject.GetComponent<ItemButton>();
            item.SetActive_SeparatorLine(false);
            item.SetActive_SpriteOnButton(false);
            item._subLabel.gameObject.SetActive(false);
            item._sprite.ResetAnchors();
            item._sprite.pivot = UIWidget.Pivot.Center;
            item._sprite.transform.localPosition = Vector3.zero;
            item._sprite.SetDimensions(width, height);
            item._label.ResetAnchors();
            item._label.pivot = UIWidget.Pivot.Center;
            item._label.alignment = NGUIText.Alignment.Center;
            item._label.overflowMethod = UILabel.Overflow.ShrinkContent;
            item._label.SetDimensions(width - 12, height - 4);
            item._label.transform.localPosition = Vector3.zero;
            item.SetValue(text);
            item._collider.size = new Vector3(width, height, item._collider.size.z);

            UIButton button = item._button;
            button.onClick.Clear();
            button.onClick.Add(new EventDelegate(delegate
            {
                GameMgr.GetIns().GetSoundMgr().PlaySe(Se.TYPE.SYS_COMMON_BUTTON, false);
                onClick?.Invoke();
            }));

            SetButtonSelected(button, false);
            return button;
        }

        private static void SetButtonSelected(UIButton button, bool selected)
        {
            if (button == null)
            {
                return;
            }

            string normalSprite = selected ? "btn_common_02_m_off" : "btn_common_01_m_off";
            string pressedSprite = selected ? "btn_common_02_m_on" : "btn_common_01_m_on";
            button.normalSprite = normalSprite;
            button.hoverSprite = normalSprite;
            button.pressedSprite = pressedSprite;
            UISprite sprite = button.GetComponent<UISprite>() ?? button.GetComponentInChildren<UISprite>(true);
            if (sprite != null)
            {
                sprite.spriteName = normalSprite;
            }
        }

        private void CreateLeaderPageButtons()
        {
            _leaderPreviousButton = CloneLeaderPageButton(
                _skinDialogTemplate._btnNextPage,
                new Vector3(-485f, -232f, 0f),
                true,
                ShowPreviousLeaderPage);
            _leaderNextButton = CloneLeaderPageButton(
                _skinDialogTemplate._btnNextPage,
                new Vector3(485f, -232f, 0f),
                false,
                ShowNextLeaderPage);
        }

        private UIButton CloneLeaderPageButton(
            UIButton template,
            Vector3 position,
            bool mirrorHorizontally,
            Action onClick)
        {
            GameObject buttonObject = NGUITools.AddChild(_contentRoot, template.gameObject);
            buttonObject.transform.localPosition = position;
            buttonObject.transform.localScale = new Vector3(mirrorHorizontally ? -0.78f : 0.78f, 0.78f, 1f);
            buttonObject.SetActive(true);

            UIButton button = buttonObject.GetComponent<UIButton>();
            button.onClick.Clear();
            button.onClick.Add(new EventDelegate(delegate
            {
                GameMgr.GetIns().GetSoundMgr().PlaySe(Se.TYPE.SYS_SLIDE_BTN, false);
                onClick();
            }));
            return button;
        }

        private void SelectDeck(int index, bool refreshControls = true)
        {
            _deckIndex = Mathf.Clamp(index, 0, _decks.Count - 1);
            ClearStoryAiSelection();
            AIManager.CustomPracticeDeckChoice choice = _decks[_deckIndex];
            SelectClass(choice.EnemyClassId, refreshControls);
            if (choice.OriginalAIPreset != null)
            {
                int presetIndex = FindPresetIndex(_presets, choice.OriginalAIPreset);
                if (presetIndex >= 0)
                {
                    _presetIndex = presetIndex;
                    ApplyPresetDefaults(_presets[presetIndex].Setting);
                }
            }
            UpdateDeckButton();
        }

        private void SelectPlayerDeck(int index, bool refreshControls = true)
        {
            if (_playerDecks == null || _playerDecks.Count == 0)
            {
                _playerClassId = Mathf.Clamp(GameMgr.GetIns().GetDataMgr().GetPlayerClassId(), 1, 8);
                return;
            }

            _playerDeckIndex = Mathf.Clamp(index, 0, _playerDecks.Count - 1);
            AIManager.CustomPracticeDeckChoice choice = _playerDecks[_playerDeckIndex];
            _playerClassId = Mathf.Clamp(choice.EnemyClassId, 1, 8);

            if (refreshControls && _contentRoot != null)
            {
                UpdatePlayerDeckButton();
            }
        }

        private void OpenDeckDialog()
        {
            var deckGroup = new DeckGroup(
                _decks.Select(choice => choice.Deck).ToList(),
                Format.Unlimited,
                DeckAttributeType.CustomDeck);
            DeckSelectUIDialog deckSelector = DeckSelectUIDialog.Create(
                "选择 AI 卡组",
                new DeckGroupListData(deckGroup),
                Format.Unlimited,
                DeckSelectUIDialog.eFormatChangeUIType.SingleFormat,
                false,
                OnDeckSelected,
                new DeckSelectUI.InitOptions
                {
                    PrimaryFirstDisplayDeck = _decks[_deckIndex].Deck,
                    // Original practice decks contain cards the local profile may not own,
                    // but they are valid training inputs and must remain selectable.
                    CanUseNonPossessionCard = true
                });
            RaiseDialogAbove(deckSelector.Dialog, _dialog);
        }

        private void OnDeckSelected(DialogBase deckDialog, DeckData deck)
        {
            int index = _decks.FindIndex(choice =>
                ReferenceEquals(choice.Deck, deck) ||
                choice.Deck.GetDeckID() == deck.GetDeckID());
            if (index < 0)
            {
                return;
            }

            deckDialog.CloseSoon();
            SelectDeck(index);
            ClearValidation();
        }

        private void OpenPlayerDeckDialog()
        {
            if (_playerDecks == null || _playerDecks.Count == 0)
            {
                return;
            }

            var deckGroup = new DeckGroup(
                _playerDecks.Select(choice => choice.Deck).ToList(),
                Format.Unlimited,
                DeckAttributeType.CustomDeck);
            DeckSelectUIDialog deckSelector = DeckSelectUIDialog.Create(
                "选择我方卡组",
                new DeckGroupListData(deckGroup),
                Format.Unlimited,
                DeckSelectUIDialog.eFormatChangeUIType.SingleFormat,
                false,
                OnPlayerDeckSelected,
                new DeckSelectUI.InitOptions
                {
                    PrimaryFirstDisplayDeck = _playerDecks[_playerDeckIndex].Deck,
                    CanUseNonPossessionCard = true
                });
            RaiseDialogAbove(deckSelector.Dialog, _dialog);
        }

        private void OnPlayerDeckSelected(DialogBase deckDialog, DeckData deck)
        {
            int index = _playerDecks.FindIndex(choice =>
                ReferenceEquals(choice.Deck, deck) ||
                choice.Deck.GetDeckID() == deck.GetDeckID());
            if (index < 0)
            {
                return;
            }

            deckDialog.CloseSoon();
            SelectPlayerDeck(index);
            ClearValidation();
        }

        private void SelectClass(int classId, bool refreshControls = true)
        {
            _classId = Mathf.Clamp(classId, 1, 8);
            RebuildLeaders();
            RebuildPresets();
            ClearValidation();

            if (refreshControls && _contentRoot != null)
            {
                RefreshClassDependentControls();
                BeginRebuildLeaderButtons();
            }
        }

        private void RebuildLeaders()
        {
            DataMgr dataMgr = GameMgr.GetIns().GetDataMgr();
            _leaders = Data.Master.ClassCharacterList
                .Where(leader => leader.is_usable && leader.IsAcquired && leader.class_id == _classId)
                .OrderBy(leader => leader.skin_id)
                .ToList();

            ClassCharacterMasterData currentLeader = dataMgr.GetCharaPrmByClassId(_classId, true);
            if (_leaders.Count == 0 && currentLeader != null)
            {
                _leaders.Add(currentLeader);
            }

            int currentLeaderIndex = currentLeader == null
                ? -1
                : _leaders.FindIndex(leader => leader.chara_id == currentLeader.chara_id);
            if (currentLeaderIndex > 0)
            {
                ClassCharacterMasterData selectedLeader = _leaders[currentLeaderIndex];
                _leaders.RemoveAt(currentLeaderIndex);
                _leaders.Insert(0, selectedLeader);
            }

            _leaderIndex = 0;
            _leaderPageIndex = 0;
        }

        private void RebuildPresets()
        {
            List<PracticeAISettingData> settings = Data.Master.PracticeAISettingList?
                .GetSettingDataTable()?
                .Where(setting => setting.ClassId == _classId)
                .OrderBy(setting => setting.Difficulty)
                .ToList() ?? new List<PracticeAISettingData>();

            _presets = settings.Select((setting, index) => new AIPresetChoice
            {
                Setting = setting,
                Label = $"官方预设 {index + 1}  [{GetDeckFileName(setting)}]"
            }).ToList();

            _presetIndex = settings.FindIndex(setting => setting.Difficulty == 1);
            if (_presetIndex < 0)
            {
                _presetIndex = 0;
            }

            if (_presets.Count > 0)
            {
                ApplyPresetDefaults(_presets[_presetIndex].Setting);
            }
        }

        private static int FindPresetIndex(List<AIPresetChoice> presets, PracticeAISettingData setting)
        {
            if (presets == null || setting == null)
            {
                return -1;
            }

            return presets.FindIndex(choice => choice.Setting != null &&
                choice.Setting.ClassId == setting.ClassId &&
                choice.Setting.Difficulty == setting.Difficulty &&
                choice.Setting.DeckId == setting.DeckId &&
                choice.Setting.StyleId == setting.StyleId &&
                choice.Setting.EmoteId == setting.EmoteId);
        }

        private void SelectPreset(int index)
        {
            if (_presets == null || _presets.Count == 0)
            {
                return;
            }

            _presetIndex = Mathf.Clamp(index, 0, _presets.Count - 1);
            ClearStoryAiSelection();
            ApplyPresetDefaults(_presets[_presetIndex].Setting);
            UpdateLogicButtons();
            UpdateLifeSlider();
            UpdateAIPresetButton();
            ClearValidation();
        }

        private void OpenPresetDialog()
        {
            if (_presets == null || _presets.Count == 0)
            {
                return;
            }

            OpenListDialog(
                "选择官方 AI 预设",
                _presets.Select(choice => choice.Label).ToList(),
                _presetIndex,
                SelectPreset);
        }

        private void OpenCsvDialog(
            string title,
            List<CsvChoice> choices,
            int selectedIndex,
            Action<int> onSelect)
        {
            if (choices == null || choices.Count == 0)
            {
                return;
            }

            OpenListDialog(
                title,
                choices.Select(choice => choice.Label).ToList(),
                selectedIndex,
                index =>
                {
                    onSelect(index);
                    ClearStoryAiSelection(false);
                    RefreshCsvControls();
                    ClearValidation();
                });
        }

        private void OpenListDialog(
            string title,
            List<string> choices,
            int selectedIndex,
            Action<int> onSelect)
        {
            DialogBase choiceDialog = DrumrollDialog.Create(
                choices,
                Mathf.Clamp(selectedIndex, 0, choices.Count - 1),
                null,
                null,
                onSelect,
                title);
            RaiseDialogAbove(choiceDialog, _dialog);
        }

        private static void RaiseDialogAbove(DialogBase dialog, DialogBase parentDialog)
        {
            UIPanel[] parentPanels = parentDialog.GetComponentsInChildren<UIPanel>(true);
            UIPanel[] dialogPanels = dialog.GetComponentsInChildren<UIPanel>(true);
            int parentMaxDepth = parentPanels.Length == 0
                ? parentDialog.GetPanelDepth()
                : parentPanels.Max(panel => panel.depth);
            int parentMaxSortingOrder = parentPanels.Length == 0
                ? 0
                : parentPanels.Max(panel => panel.sortingOrder);

            if (dialogPanels.Length > 0)
            {
                int dialogMinDepth = dialogPanels.Min(panel => panel.depth);
                int depthOffset = parentMaxDepth + 2 - dialogMinDepth;
                foreach (UIPanel panel in dialogPanels)
                {
                    panel.depth += depthOffset;
                    panel.sortingOrder = parentMaxSortingOrder + 1;
                }
            }

            UIPanel backPanel = dialog.backView?.GetComponent<UIPanel>();
            if (backPanel != null)
            {
                backPanel.depth = parentMaxDepth + 1;
                backPanel.sortingOrder = parentMaxSortingOrder + 1;
            }
        }

        private void ApplyPresetDefaults(PracticeAISettingData preset)
        {
            _logicLevel = Mathf.Clamp(preset.LogicLevel, 0, 2);
            _maxLife = Mathf.Clamp(preset.MaxLife > 0 ? preset.MaxLife : 20, 1, 100);
        }

        private void RefreshCsvChoices()
        {
            string selectedDeckPath = GetSelectedPath(_deckCsvChoices, _deckCsvIndex);
            string selectedStylePath = GetSelectedPath(_styleCsvChoices, _styleCsvIndex);
            string selectedEmotePath = GetSelectedPath(_emoteCsvChoices, _emoteCsvIndex);

            _deckCsvChoices = BuildCsvChoices(PathHelper.AIDeckPath);
            _styleCsvChoices = BuildCsvChoices(PathHelper.AIStylePath);
            _emoteCsvChoices = BuildCsvChoices(PathHelper.AIEmotePath);

            _deckCsvIndex = FindPathIndex(_deckCsvChoices, selectedDeckPath);
            _styleCsvIndex = FindPathIndex(_styleCsvChoices, selectedStylePath);
            _emoteCsvIndex = FindPathIndex(_emoteCsvChoices, selectedEmotePath);
        }

        private void RefreshCsvChoicesAndControls()
        {
            RefreshCsvChoices();
            RefreshCsvControls();
            ClearValidation();
        }

        private static List<CsvChoice> BuildCsvChoices(string directory)
        {
            Directory.CreateDirectory(directory);
            var choices = new List<CsvChoice>
            {
                new CsvChoice { Label = "使用官方预设", Path = null }
            };
            choices.AddRange(Directory.EnumerateFiles(directory, "*.csv", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .Select(path => new CsvChoice
                {
                    Label = Path.GetFileName(path),
                    Path = path
                }));
            return choices;
        }

        private static string GetSelectedPath(List<CsvChoice> choices, int index)
        {
            return choices != null && index >= 0 && index < choices.Count ? choices[index].Path : null;
        }

        private static int FindPathIndex(List<CsvChoice> choices, string path)
        {
            int index = choices.FindIndex(choice => string.Equals(choice.Path, path, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? 0 : index;
        }

        private void RefreshAllControls()
        {
            UpdateDeckButton();
            UpdatePlayerDeckButton();
            RefreshClassDependentControls();
            RefreshCsvControls();
            UpdateLLMAIControl();
        }

        private void UpdateDeckButton()
        {
            if (!_uiReady)
            {
                return;
            }

            string label = _decks != null && _decks.Count > 0
                ? GetDeckChoiceLabel(_decks[Mathf.Clamp(_deckIndex, 0, _decks.Count - 1)])
                : "无可用卡组";
            SetNativeButtonText(_deckButton, label);
        }

        private void RefreshClassDependentControls()
        {
            UpdateClassButtons();
            UpdateLogicButtons();
            UpdateLifeSlider();
            UpdateAIPresetButton();
        }

        private void UpdatePlayerDeckButton()
        {
            if (!_uiReady)
            {
                return;
            }

            string label = _playerDecks != null && _playerDecks.Count > 0
                ? GetDeckChoiceLabel(_playerDecks[Mathf.Clamp(_playerDeckIndex, 0, _playerDecks.Count - 1)])
                : "无可用卡组";
            SetNativeButtonText(_playerDeckButton, label);
        }

        private void RefreshCsvControls()
        {
            SetNativeButtonText(_deckCsvButton, GetChoiceLabel(_deckCsvChoices, _deckCsvIndex));
            SetNativeButtonText(_styleCsvButton, GetChoiceLabel(_styleCsvChoices, _styleCsvIndex));
            SetNativeButtonText(_emoteCsvButton, GetChoiceLabel(_emoteCsvChoices, _emoteCsvIndex));
            UpdateStoryAiControls();
        }

        private void UpdateAIPresetButton()
        {
            string label = _presets != null && _presets.Count > 0
                ? _presets[Mathf.Clamp(_presetIndex, 0, _presets.Count - 1)].Label
                : "无可用预设";
            SetNativeButtonText(_presetButton, label);
        }

        private void ToggleLLMAI()
        {
            if (!LLMAITurnController.IsAvailable(out string reason))
            {
                _enableLLMAI = false;
                UpdateLLMAIControl();
                ShowValidation("LLM AI unavailable: " + reason + ". Configure [LLMAI] and restart the game.");
                return;
            }

            _enableLLMAI = !_enableLLMAI;
            UpdateLLMAIControl();
            ClearValidation();
        }

        private void UpdateLLMAIControl()
        {
            bool available = LLMAITurnController.IsAvailable(out _);
            string label = !available ? "LLM AI: UNAVAILABLE" : (_enableLLMAI ? "LLM AI: ON" : "LLM AI: OFF");
            SetNativeButtonText(_llmAIButton, label);
            SetButtonSelected(_llmAIButton, available && _enableLLMAI);
        }

        /// <summary>
        /// 载入官方剧情敌方 AI 列表（master 的 story_ai_setting + story_ai 目录里的 CSV）。
        /// </summary>
        private void LoadStoryAiChoices()
        {
            _storyAiChoices = new List<StoryAiChoice>
            {
                // 固定的「无」：选中它即可取消剧情 AI，把变暗的自定义按钮恢复。
                new StoryAiChoice()
            };
            try
            {
                List<StoryAISettingData> settings = Data.Master?.StoryAISettingList?.GetSettingDataTable()?.ToList();
                if (settings == null)
                {
                    return;
                }

                string officialRoot = PathHelper.OfficialAIDataPath;
                foreach (StoryAISettingData setting in settings.Where(s => s != null).OrderBy(s => s.EnemyAiId))
                {
                    string deckName = GetStoryDeckFileName(setting.DeckId);
                    string styleName = GetAIStyleFileName(setting.StyleId);
                    string emoteName = GetAIEmoteFileName(setting.EmoteId);
                    string emotePath = FindOfficialCsv(officialRoot, "emote", emoteName);
                    string deckPath = FindOfficialCsv(officialRoot, "deck", deckName);
                    int enemyCharaId = ResolveStoryEnemyChara(emotePath);
                    _storyAiChoices.Add(new StoryAiChoice
                    {
                        Setting = setting,
                        DeckName = deckName,
                        DeckCsvPath = deckPath,
                        StyleCsvPath = FindOfficialCsv(officialRoot, "style", styleName),
                        EmoteCsvPath = emotePath,
                        EnemyCharaId = enemyCharaId,
                        EnemyClassId = ResolveCharaClassId(enemyCharaId),
                        DeckCardIds = ReadDeckCardIds(deckPath)
                    });
                }

                Plugin.Logger.LogInfo(
                    $"[AIManager] Story AI choices available: {_storyAiChoices.Count} " +
                    $"(story data root '{officialRoot ?? "<none>"}').");
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning($"[AIManager] Could not list the story AI settings: {exception.Message}");
            }
        }

        /// <summary>
        /// 剧情 AI 的敌方主战者：story_ai_setting 里没有这一列，但它的表情表每条都有
        /// <c>voice_id</c>（如 <c>500211_003_100</c>），前缀就是主战者 chara id。取第一个能在
        /// 本地角色表里查到的前缀即可（20 篇第 8 章 -> 500211，与章节里的 enemy_chara_id 一致）。
        /// </summary>
        private static int ResolveStoryEnemyChara(string emoteCsvPath)
        {
            if (string.IsNullOrEmpty(emoteCsvPath) || !File.Exists(emoteCsvPath))
            {
                return 0;
            }

            try
            {
                foreach (string line in File.ReadLines(emoteCsvPath).Skip(1))
                {
                    string[] cells = line.Split(',');
                    if (cells.Length <= 4)
                    {
                        continue;
                    }

                    string voiceId = cells[4].Trim().Trim('"');
                    int separator = voiceId.IndexOf('_');
                    string prefix = separator > 0 ? voiceId.Substring(0, separator) : string.Empty;
                    if (prefix.Length == 0 || !prefix.All(char.IsDigit) ||
                        !int.TryParse(prefix, out int charaId) || charaId <= 0)
                    {
                        continue;
                    }

                    if (GameMgr.GetIns().GetDataMgr().GetCharaPrmByCharaId(charaId) != null)
                    {
                        return charaId;
                    }
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    $"[AIManager] Could not read the story AI's enemy chara from '{emoteCsvPath}': {exception.Message}");
            }

            return 0;
        }

        private static int ResolveCharaClassId(int charaId)
        {
            if (charaId <= 0)
            {
                return 0;
            }

            try
            {
                ClassCharacterMasterData chara = GameMgr.GetIns().GetDataMgr().GetCharaPrmByCharaId(charaId);
                return chara != null && chara.class_id >= 1 && chara.class_id <= 8 ? chara.class_id : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// 把官方牌组 CSV 展开成卡表（card_id × card_num）。超过 9 位的卡 id 后两位是版本号，
        /// 取基础卡 id（和客户端 AICardDataAssetSet 的处理一致）。
        /// </summary>
        private static List<int> ReadDeckCardIds(string deckCsvPath)
        {
            var cardIds = new List<int>();
            if (string.IsNullOrEmpty(deckCsvPath) || !File.Exists(deckCsvPath))
            {
                return cardIds;
            }

            try
            {
                foreach (string line in File.ReadLines(deckCsvPath).Skip(1))
                {
                    string[] cells = line.Split(',');
                    if (cells.Length < 4)
                    {
                        continue;
                    }

                    string text = cells[0].Trim().Trim('"');
                    string countText = cells[3].Trim().Trim('"');
                    if (!text.All(char.IsDigit) || !int.TryParse(countText, out int count) || count <= 0)
                    {
                        continue;
                    }

                    if (text.Length > 9)
                    {
                        text = text.Substring(0, text.Length - 2);
                    }

                    if (!int.TryParse(text, out int cardId) || cardId <= 0)
                    {
                        continue;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        cardIds.Add(cardId);
                    }
                }
            }
            catch (Exception exception)
            {
                Plugin.Logger.LogWarning(
                    $"[AIManager] Could not read the story AI deck '{deckCsvPath}': {exception.Message}");
            }

            return cardIds;
        }

        private static string FindOfficialCsv(string officialRoot, string kind, string fileName)        {
            if (string.IsNullOrEmpty(officialRoot) || string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            string path = Path.Combine(Path.Combine(officialRoot, kind), fileName + ".csv");
            if (File.Exists(path))
            {
                return path;
            }

            // 官方资源没导出时，仍然可以退回 master 里的 AI 资源（由 AIManager 自己加载）。
            return null;
        }

        private static string GetStoryDeckFileName(int deckId)
        {
            try
            {
                return Data.Master?.AIDeckFileNameList?.GetFileName(deckId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string GetAIStyleFileName(int styleId)        {
            try
            {
                return Data.Master?.AIStyleFileNameList?.GetFileName(styleId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string GetAIEmoteFileName(int emoteId)
        {
            try
            {
                return Data.Master?.AIEmoteFileNameList?.GetFileName(emoteId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void OpenStoryAiDialog()
        {
            if (_storyAiChoices == null || _storyAiChoices.Count == 0)
            {
                ShowValidation("没有可用的剧情 AI 数据（master 的 story_ai_setting 为空）。");
                return;
            }

            OpenListDialog(
                "选择剧情AI预设",
                _storyAiChoices.Select(choice => choice.Label).ToList(),
                _storyAiIndex < 0 ? 0 : _storyAiIndex,
                SelectStoryAi);
        }

        private void SelectStoryAi(int index)
        {
            if (_storyAiChoices == null || _storyAiChoices.Count == 0)
            {
                return;
            }

            _storyAiIndex = Mathf.Clamp(index, 0, _storyAiChoices.Count - 1);
            // 剧情对战的官方生命上限就是 20；选剧情 AI 时把它拉回默认值
            // （之前会残留上一个职业预设的生命上限，比如 30/40）。
            if (StoryAiSelected && _maxLife != 20)
            {
                _maxLife = 20;
                UpdateLifeSlider();
            }
            UpdateStoryAiControls();
            ClearValidation();
        }

        /// <summary>是否真的启用了剧情 AI（列表第 0 项是固定的「无」）。</summary>
        private bool StoryAiSelected =>
            _storyAiIndex > 0 && _storyAiChoices != null && _storyAiIndex < _storyAiChoices.Count;

        private StoryAiChoice SelectedStoryAi => StoryAiSelected ? _storyAiChoices[_storyAiIndex] : null;

        /// <summary>
        /// 动了自定义的任意一项就退回「无」——剧情 AI 与自定义 AI 互斥。
        /// </summary>
        private void ClearStoryAiSelection(bool updateControls = true)
        {
            _storyAiIndex = 0;
            if (updateControls)
            {
                UpdateStoryAiControls();
            }
        }

        private void UpdateStoryAiControls()
        {
            // Initialize 里 SelectDeck 会在 BuildNativeUi 之前跑，那时按钮还不存在。
            if (!_uiReady)
            {
                return;
            }

            StoryAiChoice choice = _storyAiChoices != null &&
                                   _storyAiIndex >= 0 &&
                                   _storyAiIndex < _storyAiChoices.Count
                ? _storyAiChoices[_storyAiIndex]
                : null;
            bool story = StoryAiSelected;

            if (_storyAiButton != null)
            {
                SetNativeButtonText(_storyAiButton, choice == null ? "无（自定义对手AI数据）" : choice.Label);
                SetButtonSelected(_storyAiButton, story);
            }

            // 剧情 AI 接管了敌方主战者 / 职业 / 卡组 / AI 数据 / 逻辑等级，所以这些自定义项
            // 全部变暗；LLM AI 也会去抢对手的出牌决策，同样算冲突。
            bool customEnabled = !story;
            foreach (UIButton button in new[]
                     {
                         _deckButton, _presetButton,
                         _deckCsvButton, _styleCsvButton, _emoteCsvButton,
                         _llmAIButton
                     })
            {
                SetButtonEnabled(button, customEnabled);
            }

            foreach (UIButton button in _classButtons)
            {
                SetButtonEnabled(button, customEnabled);
            }

            foreach (UIButton button in _logicButtons)
            {
                SetButtonEnabled(button, customEnabled);
            }

            // 主战者栏（含左右翻页）也一并变暗。
            if (_leaderStripRoot != null)
            {
                UIManager.SetObjectToGrey(_leaderStripRoot, !customEnabled);
            }

            // 左右翻页按钮不在 _leaderStripRoot 里（是单独克隆出来的），所以
            // 光靠上面那行变暗盖不到它们，得单独灰掉。
            SetLeaderPageButtonEnabled(_leaderPreviousButton, customEnabled);
            SetLeaderPageButtonEnabled(_leaderNextButton, customEnabled);
        }

        /// <summary>
        /// 主战者左右翻页按钮没有走 ItemButton 那套，<c>isEnabled</c> 只拦点击不改变外观，
        /// 这里额外把它整块变灰。
        /// </summary>
        private static void SetLeaderPageButtonEnabled(UIButton button, bool enabled)
        {
            if (button == null)
            {
                return;
            }

            button.isEnabled = enabled;
            UIManager.SetObjectToGrey(button.gameObject, !enabled);
        }

        private static void SetButtonEnabled(UIButton button, bool enabled)
        {
            if (button == null)
            {
                return;
            }

            button.isEnabled = enabled;
            UIManager.SetObjectToGrey(button.gameObject, !enabled);
        }

        private static string GetChoiceLabel(List<CsvChoice> choices, int index)
        {
            return choices != null && choices.Count > 0
                ? choices[Mathf.Clamp(index, 0, choices.Count - 1)].Label
                : "无可用选项";
        }

        private static void SetNativeButtonText(UIButton button, string text)
        {
            ItemButton item = button == null ? null : button.GetComponentInParent<ItemButton>();
            if (item != null)
            {
                item.SetValue(text);
            }
        }

        private string GetDeckChoiceLabel(AIManager.CustomPracticeDeckChoice choice)
        {
            DeckData deck = choice.Deck;
            string name = string.IsNullOrEmpty(deck.GetDeckName()) ? "未命名卡组" : deck.GetDeckName();
            string className = GameMgr.GetIns().GetDataMgr().GetClanNameByKey(choice.EnemyClassId);
            return $"{name}  [{className} / {deck.GetCardIdList().Count}张]";
        }

        private void UpdateClassButtons()
        {
            for (int i = 0; i < _classButtons.Count; i++)
            {
                SetButtonSelected(_classButtons[i], i + 1 == _classId);
            }
        }

        private void UpdateLogicButtons()
        {
            for (int i = 0; i < _logicButtons.Count; i++)
            {
                SetButtonSelected(_logicButtons[i], i == _logicLevel);
            }
        }

        private void OnLifeSliderChanged()
        {
            if (_updatingLifeSlider)
            {
                return;
            }

            _maxLife = Mathf.Clamp(Mathf.RoundToInt(1f + _lifeSlider.GetValue() * 99f), 1, 100);
            UpdateLifeValueLabel();
        }

        private void UpdateLifeSlider()
        {
            if (_lifeSlider == null)
            {
                return;
            }

            _updatingLifeSlider = true;
            _lifeSlider.SetValue((_maxLife - 1f) / 99f);
            _updatingLifeSlider = false;
            UpdateLifeValueLabel();
        }

        private void UpdateLifeValueLabel()
        {
            if (_lifeSlider != null && _lifeSlider.m_valueLabel != null)
            {
                _lifeSlider.m_valueLabel.text = _maxLife.ToString();
            }
        }

        private void BeginRebuildLeaderButtons()
        {
            int version = ++_leaderBuildVersion;
            DestroyLeaderButtons();
            ReleaseLeaderResources();
            UpdateLeaderPage();

            if (_leaders == null || _leaders.Count == 0)
            {
                return;
            }

            List<string> paths = _leaders
                .Select(leader => Toolbox.ResourcesManager.GetAssetTypePath(
                    leader.skin_id.ToString(),
                    ResourcesManager.AssetLoadPathType.ClassCharaButton,
                    false))
                .Distinct()
                .ToList();
            StartCoroutine(LoadLeaderButtons(paths, version));
        }

        private IEnumerator LoadLeaderButtons(List<string> paths, int version)
        {
            yield return StartCoroutine(Toolbox.ResourcesManager.LoadAssetGroupAsync(paths, null, true));

            if (_isDestroyed || version != _leaderBuildVersion)
            {
                Toolbox.ResourcesManager.RemoveAssetGroup(paths);
                yield break;
            }

            _loadedLeaderPaths = paths;
            for (int i = 0; i < _leaders.Count; i++)
            {
                ClassCharacterMasterData leader = _leaders[i];
                GameObject buttonObject = NGUITools.AddChild(
                    _leaderStripRoot,
                    _skinDialogTemplate._skinButtonItemOriginal);
                buttonObject.name = "Leader_" + leader.skin_id;
                buttonObject.transform.localScale = new Vector3(0.52f, 0.52f, 1f);
                int leaderIndex = i;
                SelectRandomSkinButton button = buttonObject.GetComponent<SelectRandomSkinButton>();
                button.Initialize(
                    leader.skin_id,
                    i == _leaderIndex,
                    (skinId, status) => SelectLeader(leaderIndex),
                    obj => { },
                    (obj, direction) => { });
                _leaderButtons.Add(button);
            }

            UpdateLeaderPage();
        }

        private void SelectLeader(int index)
        {
            _leaderIndex = Mathf.Clamp(index, 0, _leaders.Count - 1);
            _leaderPageIndex = _leaderIndex / LeadersPerPage;
            UpdateLeaderPage();
            ClearValidation();
        }

        private void ShowPreviousLeaderPage()
        {
            _leaderPageIndex = Mathf.Max(0, _leaderPageIndex - 1);
            UpdateLeaderPage();
        }

        private void ShowNextLeaderPage()
        {
            int pageCount = GetLeaderPageCount();
            _leaderPageIndex = Mathf.Min(pageCount - 1, _leaderPageIndex + 1);
            UpdateLeaderPage();
        }

        private void UpdateLeaderPage()
        {
            int pageCount = GetLeaderPageCount();
            _leaderPageIndex = Mathf.Clamp(_leaderPageIndex, 0, Mathf.Max(0, pageCount - 1));
            int firstIndex = _leaderPageIndex * LeadersPerPage;
            int lastIndex = Mathf.Min(firstIndex + LeadersPerPage, _leaderButtons.Count);
            int visibleCount = lastIndex - firstIndex;

            for (int i = 0; i < _leaderButtons.Count; i++)
            {
                bool visible = i >= firstIndex && i < lastIndex;
                SelectRandomSkinButton button = _leaderButtons[i];
                button.gameObject.SetActive(visible);
                button.SetSelectStatus(i == _leaderIndex);
                if (visible)
                {
                    int visibleIndex = i - firstIndex;
                    float x = (visibleIndex - (visibleCount - 1) * 0.5f) * 86f;
                    button.transform.localPosition = new Vector3(x, 0f, 0f);
                }
            }

            bool hasMultiplePages = pageCount > 1;
            if (_leaderPreviousButton != null)
            {
                _leaderPreviousButton.gameObject.SetActive(hasMultiplePages && _leaderPageIndex > 0);
            }
            if (_leaderNextButton != null)
            {
                _leaderNextButton.gameObject.SetActive(hasMultiplePages && _leaderPageIndex < pageCount - 1);
            }
            if (_leaderPageLabel != null)
            {
                _leaderPageLabel.text = hasMultiplePages ? $"{_leaderPageIndex + 1} / {pageCount}" : string.Empty;
            }
            if (_leaderNameLabel != null)
            {
                _leaderNameLabel.text = _leaders != null && _leaders.Count > 0
                    ? _leaders[Mathf.Clamp(_leaderIndex, 0, _leaders.Count - 1)].chara_name
                    : "没有可用主战者";
            }
        }

        private int GetLeaderPageCount()
        {
            return Mathf.Max(1, Mathf.CeilToInt((_leaders?.Count ?? 0) / (float)LeadersPerPage));
        }

        private void DestroyLeaderButtons()
        {
            foreach (SelectRandomSkinButton button in _leaderButtons)
            {
                if (button != null)
                {
                    button.gameObject.SetActive(false);
                    Destroy(button.gameObject);
                }
            }
            _leaderButtons.Clear();
        }

        private void ReleaseLeaderResources()
        {
            if (_loadedLeaderPaths.Count == 0)
            {
                return;
            }

            Toolbox.ResourcesManager.RemoveAssetGroup(_loadedLeaderPaths);
            _loadedLeaderPaths.Clear();
        }

        private void StartBattle()
        {
            if (_isStarting)
            {
                return;
            }
            if (_leaders == null || _leaders.Count == 0)
            {
                ShowValidation("所选职业没有可用主战者。");
                return;
            }
            if (_presets == null || _presets.Count == 0)
            {
                ShowValidation("所选职业没有可用的官方 AI 预设。");
                return;
            }
            if (_enableLLMAI && !LLMAITurnController.IsAvailable(out string llmReason))
            {
                ShowValidation("LLM AI unavailable: " + llmReason + ". Configure [LLMAI] and restart the game.");
                return;
            }

            _isStarting = true;
            _dialog.IsButton1Enabled = false;
            StoryAiChoice storyAi = SelectedStoryAi;

            var settings = new AIManager.CustomPracticeSettings
            {
                Deck = _decks[_deckIndex].Deck,
                PlayerDeck = _playerDecks != null && _playerDecks.Count > 0
                    ? _playerDecks[Mathf.Clamp(_playerDeckIndex, 0, _playerDecks.Count - 1)].Deck
                    : null,
                EnemyClassId = storyAi != null && storyAi.EnemyClassId > 0 ? storyAi.EnemyClassId : _classId,
                PlayerClassId = _playerClassId,
                Leader = _leaders[_leaderIndex],
                // 剧情 AI 连敌方主战者一起接管。
                EnemyCharaId = storyAi?.EnemyCharaId ?? 0,
                EnemyDeckCardIds = storyAi?.DeckCardIds,
                StoryAiId = storyAi?.Setting?.EnemyAiId ?? 0,
                AIPreset = _presets[_presetIndex].Setting,
                // 我方 AI 已删除，这项恒为 null（AIManager 里只有 EnablePlayerAI 为真时才用它）。
                PlayerAIPreset = null,
                // 剧情 AI 优先：直接用官方那一套 CSV 和逻辑等级接管对手 AI。
                LocalDeckCsvPath = storyAi?.DeckCsvPath ?? GetSelectedPath(_deckCsvChoices, _deckCsvIndex),
                LocalStyleCsvPath = storyAi?.StyleCsvPath ?? GetSelectedPath(_styleCsvChoices, _styleCsvIndex),
                LocalEmoteCsvPath = storyAi?.EmoteCsvPath ?? GetSelectedPath(_emoteCsvChoices, _emoteCsvIndex),
                LogicLevel = storyAi != null ? storyAi.Setting.LogicLevel : _logicLevel,
                MaxLife = _maxLife,
                EnableLLMAI = _enableLLMAI,
                // 「斗蛐蛐」：我方也交给 AI（用我方职业对应的官方预设 / 本地 CSV）。
                // 普通「自定义对手」仍然只配置对手 AI。
                EnablePlayerAI = _dualAi,
                PlayerAIUseLocalCsv = false,
                LocalPlayerDeckCsvPath = null,
                LocalPlayerStyleCsvPath = null,
                LocalPlayerEmoteCsvPath = null
            };

            if (_dualAi)
            {
                Plugin.Logger.LogInfo(
                    "[AIManager] 斗蛐蛐（AI 对 AI）：我方也会由 AI 接管，敌方配置照旧生效。");
            }

            if (storyAi != null)
            {
                Plugin.Logger.LogInfo(
                    $"[AIManager] Custom practice uses the story AI {storyAi.Setting.EnemyAiId} " +
                    $"({storyAi.DeckName}), deckCsv='{storyAi.DeckCsvPath ?? "<master>"}', " +
                    $"logic={storyAi.Setting.LogicLevel}.");
            }

            _dialog.CloseSoon();
            AIManager.StartCustomPractice(_page, settings);
        }

        private void ShowValidation(string message)
        {
            if (_validationLabel != null)
            {
                _validationLabel.text = message;
            }
            // 报错栏已经移到页面最上方，不再需要清空主战者名字来给它让位。
        }

        private void ClearValidation()
        {
            if (_validationLabel != null)
            {
                _validationLabel.text = string.Empty;
            }
            UpdateLeaderPage();
        }

        private static string GetDeckFileName(PracticeAISettingData setting)
        {
            try
            {
                return Data.Master.AIDeckFileNameList.GetFileName(setting.DeckId);
            }
            catch
            {
                return $"Deck ID {setting.DeckId}";
            }
        }

        private void OnDestroy()
        {
            _isDestroyed = true;
            _leaderBuildVersion++;
            DestroyLeaderButtons();
            ReleaseLeaderResources();
        }
    }
}
