using Newtonsoft.Json;

namespace Shadowbus
{
    /// <summary>
    /// 玩家档案信息（用于联机对战）
    /// </summary>
    public class P2PProfile
    {
        /// <summary>
        /// 玩家 ID
        /// </summary>
        [JsonProperty("viewerId")]
        public int ViewerId { get; set; }

        /// <summary>
        /// 玩家名称
        /// </summary>
        [JsonProperty("userName")]
        public string UserName { get; set; }

        /// <summary>
        /// 段位
        /// </summary>
        [JsonProperty("rank")]
        public int Rank { get; set; }

        /// <summary>
        /// 战斗点数
        /// </summary>
        [JsonProperty("battlePoint")]
        public int BattlePoint { get; set; }

        /// <summary>
        /// 大师点数
        /// </summary>
        [JsonProperty("masterPoint")]
        public int MasterPoint { get; set; }

        /// <summary>
        /// 称号 ID
        /// </summary>
        [JsonProperty("degreeId")]
        public int DegreeId { get; set; }

        /// <summary>
        /// 徽章 ID
        /// </summary>
        [JsonProperty("emblemId")]
        public long EmblemId { get; set; }

        /// <summary>
        /// 国家/地区代码
        /// </summary>
        [JsonProperty("countryCode")]
        public string CountryCode { get; set; }

        /// <summary>
        /// 是否为官方认证
        /// </summary>
        [JsonProperty("isOfficial")]
        public bool IsOfficial { get; set; }
    }
}
