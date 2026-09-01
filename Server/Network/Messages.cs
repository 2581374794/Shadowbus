using System.Collections.Generic;
using Shadowbus.Server.Room;

namespace Shadowbus.Server.Network
{
    /// <summary>
    /// 网络消息数据类定义
    /// </summary>

    public class CreateRoomRequest
    {
        public RoomRules Rules { get; set; }
        public P2PProfile Profile { get; set; }
    }

    public class RoomCreatedResponse
    {
        public string RoomId { get; set; }
        public string RoomCode { get; set; }
        public string HostPlayerId { get; set; }
        public string BattleId { get; set; }
        public RoomInfoMessage Room { get; set; }
    }

    public class JoinRoomRequest
    {
        public string RoomCode { get; set; }
        public P2PProfile Profile { get; set; }
    }

    public class JoinSuccessResponse
    {
        public string RoomId { get; set; }
        public string RoomCode { get; set; }
        public string BattleId { get; set; }
        public string HostPlayerId { get; set; }
        public RoomRules Rules { get; set; }
        public List<PlayerInfo> Players { get; set; }
    }

    public class PlayerReadyRequest
    {
        public bool IsReady { get; set; }
    }

    public class RoomInfoMessage
    {
        public string RoomId { get; set; }
        public string RoomCode { get; set; }
        public string BattleId { get; set; }
        public string HostPlayerId { get; set; }
        public string State { get; set; }
        public RoomRules Rules { get; set; }
        public List<PlayerInfo> Players { get; set; }
    }

    public class PlayerListMessage
    {
        public List<PlayerInfo> Players { get; set; }
    }

    public class PlayerInfo
    {
        public string PlayerId { get; set; }
        public string Name { get; set; }
        public int ViewerId { get; set; }
        public int Rank { get; set; }
        public int BattlePoint { get; set; }
        public int MasterPoint { get; set; }
        public int DegreeId { get; set; }
        public long EmblemId { get; set; }
        public string CountryCode { get; set; }
        public bool IsOfficial { get; set; }
        public bool IsHost { get; set; }
        public bool IsReady { get; set; }
    }

    public class MatchStartMessage
    {
        public string RoomId { get; set; }
    }

    public class ErrorMessage
    {
        public string ErrorCode { get; set; }
        public string Message { get; set; }
    }
}
