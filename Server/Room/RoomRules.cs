using Shadowbus;

namespace Shadowbus.Server.Room
{
    /// <summary>
    /// 房间规则配置
    /// </summary>
    public class RoomRules
    {
        public int BattleType { get; set; } = 6;
        // This is the API format value, not the Format enum value.
        // Data.ParseApiFormat(2) is Format.Unlimited; value 1 is Rotation.
        public int DeckFormat { get; set; } = 2;
        public int BattleRule { get; set; } = 1;
        public int TwoPickType { get; set; }
        public bool IsDeckConfirmable { get; set; }

        /// <summary>
        /// 游戏模式
        /// </summary>
        public GameMode Mode { get; set; } = GameMode.Constructed;

        /// <summary>
        /// 对局轮数（BO1、BO3、BO5）
        /// </summary>
        public int BestOf { get; set; } = 1;

        /// <summary>
        /// 初始生命值
        /// </summary>
        public int InitialLife { get; set; } = 20;

        /// <summary>
        /// 回合时间限制（秒）
        /// </summary>
        public int TurnTimeLimit { get; set; } = 90;

        /// <summary>
        /// 卡组规则
        /// </summary>
        public DeckRules DeckRules { get; set; } = new DeckRules();

        /// <summary>
        /// 完整的 Shadowbus 自定义赛制定​​义。房间码会携带这份快照，
        /// Guest 不需要依赖本地同名文件即可使用与 Host 一致的规则。
        /// </summary>
        public CustomFormatDefinition CustomFormat { get; set; } =
            new CustomFormatDefinition
            {
                Id = "unlimited",
                DisplayName = "无限赛制"
            };

        public string CustomFormatId
        {
            get
            {
                if (DeckRules != null &&
                    !string.IsNullOrWhiteSpace(DeckRules.FormatId) &&
                    !string.Equals(DeckRules.FormatId, "unlimited", System.StringComparison.OrdinalIgnoreCase) &&
                    (CustomFormat == null ||
                     string.Equals(CustomFormat.Id, "unlimited", System.StringComparison.OrdinalIgnoreCase)))
                {
                    return DeckRules.FormatId;
                }
                return CustomFormat?.Id ?? DeckRules?.FormatId ?? "unlimited";
            }
            set
            {
                string id = string.IsNullOrWhiteSpace(value) ? "unlimited" : value;
                DeckRules = DeckRules ?? new DeckRules();
                DeckRules.FormatId = id;
                CustomFormat = CustomFormats.Get(id).Clone();
            }
        }

        /// <summary>
        /// 是否允许观战
        /// </summary>
        public bool AllowSpectators { get; set; } = false;

        /// <summary>
        /// 是否显示对手手牌（测试用）
        /// </summary>
        public bool ShowOpponentHand { get; set; } = false;
    }

    /// <summary>
    /// 游戏模式
    /// </summary>
    public enum GameMode
    {
        /// <summary>
        /// 构筑模式
        /// </summary>
        Constructed = 0,

        /// <summary>
        /// Two Pick 模式
        /// </summary>
        TwoPick = 1
    }

    /// <summary>
    /// 卡组规则
    /// </summary>
    public class DeckRules
    {
        /// <summary>
        /// 卡组格式 ID
        /// </summary>
        public string FormatId { get; set; } = "unlimited";

        /// <summary>
        /// 卡组大小限制
        /// </summary>
        public int DeckSize { get; set; } = 40;

        /// <summary>
        /// 单卡数量限制
        /// </summary>
        public int CardLimit { get; set; } = 3;

        /// <summary>
        /// 是否允许跨职业
        /// </summary>
        public bool AllowCrossClass { get; set; } = false;
    }
}
