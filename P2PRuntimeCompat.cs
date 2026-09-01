using System;
using Wizard;

namespace Shadowbus
{
    /// <summary>
    /// P2P 运行时兼容层（临时，用于保持旧代码编译通过）
    /// </summary>
    [Obsolete("This is a compatibility shim. Use Server.OnlineRuntime instead.")]
    public static class P2PRuntime
    {
        /// <summary>
        /// 是否处于活动状态
        /// </summary>
        public static bool IsActive => Server.OnlineRuntime.IsEnabled;

        /// <summary>
        /// 当前 Socket.IO 房间规则。
        /// </summary>
        public static P2PRoomRules Rules
        {
            get
            {
                var rules = Server.OnlineRuntime.LastJoinSuccess?.Rules ??
                    Server.OnlineRuntime.CurrentRoomInfo?.Rules;
                if (rules == null)
                    return null;
                return new P2PRoomRules
                {
                    CustomFormatId = rules.CustomFormatId,
                    DeckFormat = rules.DeckFormat,
                    Definition = rules.CustomFormat
                };
            }
        }

        /// <summary>
        /// 检查卡组是否允许（临时实现）
        /// </summary>
        public static bool IsDeckAllowed(DeckData deck, out CustomFormatDefinition definition, out CustomFormatViolation violation)
        {
            definition = Rules?.Definition ??
                CustomFormats.Get(Rules?.CustomFormatId ?? CustomFormats.UnlimitedId);
            if (!Server.OnlineRuntime.IsEnabled || deck == null || deck.IsNoCard())
            {
                violation = null;
                return true;
            }

            return CustomFormats.IsDeckCompliant(
                deck.GetCardIdList(),
                definition,
                CardMaster.GetInstanceForBattle(),
                out violation);
        }
    }

    /// <summary>
    /// P2P 房间规则（临时兼容类）
    /// </summary>
    [Obsolete("This is a compatibility shim.")]
    public class P2PRoomRules
    {
        public string CustomFormatId { get; set; } = "unlimited";
        public int DeckFormat { get; set; } = Data.FormatConvertApi(Format.Unlimited);
        public CustomFormatDefinition Definition { get; set; }
    }
}
