using Newtonsoft.Json;
using System.Collections.Generic;

namespace Shadowbus
{
    /// <summary>
    /// 自定义赛制定义
    /// </summary>
    public class CustomFormatDefinition
    {
        /// <summary>
        /// 赛制 ID（唯一标识符）
        /// </summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>
        /// 显示名称
        /// </summary>
        [JsonProperty("displayName")]
        public string DisplayName { get; set; }

        /// <summary>
        /// 卡组大小限制（null 表示无限制）
        /// </summary>
        [JsonProperty("deckSizeLimit")]
        public int? DeckSizeLimit { get; set; }

        /// <summary>
        /// 同名卡牌数量限制（null 表示无限制）
        /// </summary>
        [JsonProperty("sameCardLimit")]
        public int? SameCardLimit { get; set; }

        /// <summary>
        /// Token 卡牌总数限制（null 表示无限制）
        /// </summary>
        [JsonProperty("tokenCardTotalLimit")]
        public int? TokenCardTotalLimit { get; set; }

        /// <summary>
        /// 同名 Token 卡牌数量限制（null 表示无限制）
        /// </summary>
        [JsonProperty("tokenSameCardLimit")]
        public int? TokenSameCardLimit { get; set; }

        /// <summary>
        /// 特定卡牌的数量限制
        /// 键：卡牌 ID，值：该卡牌的数量限制
        /// </summary>
        [JsonProperty("cardLimits")]
        public Dictionary<int, int> CardLimits { get; set; }

        /// <summary>
        /// 克隆当前定义
        /// </summary>
        public CustomFormatDefinition Clone()
        {
            return new CustomFormatDefinition
            {
                Id = this.Id,
                DisplayName = this.DisplayName,
                DeckSizeLimit = this.DeckSizeLimit,
                SameCardLimit = this.SameCardLimit,
                TokenCardTotalLimit = this.TokenCardTotalLimit,
                TokenSameCardLimit = this.TokenSameCardLimit,
                CardLimits = this.CardLimits != null ? new Dictionary<int, int>(this.CardLimits) : null
            };
        }
    }
}
