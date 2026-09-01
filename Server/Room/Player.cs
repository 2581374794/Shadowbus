using System;

namespace Shadowbus.Server.Room
{
    /// <summary>
    /// 玩家信息
    /// </summary>
    public class Player
    {
        /// <summary>
        /// 玩家 ID
        /// </summary>
        public string PlayerId { get; set; }

        /// <summary>
        /// 玩家名称
        /// </summary>
        public string Name { get; set; }

        public int ViewerId { get; set; }
        public int Rank { get; set; }
        public int BattlePoint { get; set; }
        public int MasterPoint { get; set; }
        public int DegreeId { get; set; }
        public long EmblemId { get; set; }
        public string CountryCode { get; set; }
        public bool IsOfficial { get; set; }

        /// <summary>
        /// 是否是房主
        /// </summary>
        public bool IsHost { get; set; }

        /// <summary>
        /// 是否准备好了
        /// </summary>
        public bool IsReady { get; set; }

        /// <summary>
        /// 加入时间
        /// </summary>
        public DateTime JoinedTime { get; set; }

        /// <summary>
        /// 卡组信息
        /// </summary>
        public PlayerDeck Deck { get; set; }

        /// <summary>
        /// Whether this logical slot has ever been bound to a Socket.IO
        /// connection. The slot must survive a transport reconnect, while the
        /// first bind still needs to be distinguishable from a re-enter.
        /// </summary>
        public bool HasConnected { get; set; }
    }

    /// <summary>
    /// 玩家卡组信息
    /// </summary>
    public class PlayerDeck
    {
        /// <summary>
        /// 职业 ID
        /// </summary>
        public int ClanId { get; set; }

        /// <summary>
        /// Secondary class used by crossover decks. Native clients accept the
        /// field even when the current format does not use it.
        /// </summary>
        public int SubclassId { get; set; }

        /// <summary>
        /// Character master id selected by the deck's leader skin.
        /// </summary>
        public int CharaId { get; set; }

        /// <summary>
        /// My Rotation identifier, when the deck carries one.
        /// </summary>
        public string RotationId { get; set; }

        /// <summary>
        /// 卡牌 ID 列表
        /// </summary>
        public int[] CardIds { get; set; }

        /// <summary>
        /// 主战者皮肤 ID
        /// </summary>
        public int SkinId { get; set; }

        /// <summary>
        /// 卡背 ID
        /// </summary>
        public int SleeveId { get; set; }

        public PlayerDeck Clone()
        {
            return new PlayerDeck
            {
                ClanId = ClanId,
                SubclassId = SubclassId,
                CharaId = CharaId,
                RotationId = RotationId,
                CardIds = CardIds == null ? null : (int[])CardIds.Clone(),
                SkinId = SkinId,
                SleeveId = SleeveId
            };
        }
    }
}
