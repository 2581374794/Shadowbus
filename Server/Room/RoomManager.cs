using System;
using System.Collections.Generic;

namespace Shadowbus.Server.Room
{
    /// <summary>
    /// 房间管理器，负责管理所有房间
    /// </summary>
    public class RoomManager
    {
        private readonly Dictionary<string, GameRoom> _rooms;
        private readonly object _roomLock = new object();

        public RoomManager()
        {
            _rooms = new Dictionary<string, GameRoom>();
        }

        /// <summary>
        /// 创建房间
        /// </summary>
        public GameRoom CreateRoom(string roomId, string hostPlayerId)
        {
            if (string.IsNullOrEmpty(roomId))
                throw new ArgumentException("Room ID cannot be empty", nameof(roomId));
            if (string.IsNullOrEmpty(hostPlayerId))
                throw new ArgumentException("Host player ID cannot be empty", nameof(hostPlayerId));

            lock (_roomLock)
            {
                if (_rooms.ContainsKey(roomId))
                {
                    throw new InvalidOperationException($"Room {roomId} already exists");
                }

                var room = new GameRoom(roomId, hostPlayerId);
                _rooms[roomId] = room;

                Plugin.Logger.LogInfo($"[RoomManager] Room {roomId} created by {hostPlayerId}");

                return room;
            }
        }

        /// <summary>
        /// 获取房间
        /// </summary>
        public GameRoom GetRoom(string roomId)
        {
            lock (_roomLock)
            {
                return _rooms.TryGetValue(roomId, out var room) ? room : null;
            }
        }

        /// <summary>
        /// 移除房间
        /// </summary>
        public bool RemoveRoom(string roomId)
        {
            lock (_roomLock)
            {
                if (_rooms.Remove(roomId))
                {
                    Plugin.Logger.LogInfo($"[RoomManager] Room {roomId} removed");
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 获取所有房间
        /// </summary>
        public List<GameRoom> GetAllRooms()
        {
            lock (_roomLock)
            {
                return new List<GameRoom>(_rooms.Values);
            }
        }

        /// <summary>
        /// 获取房间数量
        /// </summary>
        public int GetRoomCount()
        {
            lock (_roomLock)
            {
                return _rooms.Count;
            }
        }
    }
}
