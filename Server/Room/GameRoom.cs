using System;
using System.Collections.Generic;
using System.Linq;

namespace Shadowbus.Server.Room
{
    /// <summary>
    /// 游戏房间
    /// </summary>
    public class GameRoom
    {
        private readonly object _playerLock = new object();
        private readonly List<Player> _players;

        public string RoomId { get; }
        public string HostPlayerId { get; }
        public RoomState State { get; private set; }
        public DateTime CreatedTime { get; }
        public RoomRules Rules { get; set; }

        public event Action<Player> OnPlayerJoined;
        public event Action<Player> OnPlayerLeft;
        public event Action<Player> OnPlayerReady;
        public event Action OnAllPlayersReady;
        public event Action<RoomState, RoomState> OnStateChanged;

        public GameRoom(string roomId, string hostPlayerId)
        {
            RoomId = roomId ?? throw new ArgumentNullException(nameof(roomId));
            HostPlayerId = hostPlayerId ?? throw new ArgumentNullException(nameof(hostPlayerId));

            _players = new List<Player>();
            State = RoomState.Waiting;
            CreatedTime = DateTime.UtcNow;
            Rules = new RoomRules();

            // 添加房主
            var host = new Player
            {
                PlayerId = hostPlayerId,
                IsHost = true,
                JoinedTime = DateTime.UtcNow
            };
            _players.Add(host);
        }

        /// <summary>
        /// 玩家加入房间
        /// </summary>
        public bool AddPlayer(string playerId)
        {
            if (string.IsNullOrEmpty(playerId))
                throw new ArgumentException("Player ID cannot be empty", nameof(playerId));

            lock (_playerLock)
            {
                // 检查房间状态
                if (State != RoomState.Waiting)
                {
                    Plugin.Logger.LogWarning($"[GameRoom] Cannot join room {RoomId}: room is not waiting");
                    return false;
                }

                // 检查房间是否已满
                if (_players.Count >= 2)
                {
                    Plugin.Logger.LogWarning($"[GameRoom] Cannot join room {RoomId}: room is full");
                    return false;
                }

                // 检查玩家是否已在房间中
                if (_players.Any(p => p.PlayerId == playerId))
                {
                    Plugin.Logger.LogWarning($"[GameRoom] Player {playerId} is already in room {RoomId}");
                    return false;
                }

                // 添加玩家
                var player = new Player
                {
                    PlayerId = playerId,
                    IsHost = false,
                    JoinedTime = DateTime.UtcNow
                };
                _players.Add(player);

                Plugin.Logger.LogInfo($"[GameRoom] Player {playerId} joined room {RoomId}");
                OnPlayerJoined?.Invoke(player);

                return true;
            }
        }

        /// <summary>
        /// 玩家离开房间
        /// </summary>
        public bool RemovePlayer(string playerId)
        {
            lock (_playerLock)
            {
                var player = _players.FirstOrDefault(p => p.PlayerId == playerId);
                if (player == null)
                    return false;

                _players.Remove(player);

                Plugin.Logger.LogInfo($"[GameRoom] Player {playerId} left room {RoomId}");
                OnPlayerLeft?.Invoke(player);

                return true;
            }
        }

        /// <summary>
        /// Marks a socket as disconnected while retaining its logical player
        /// slot and profile for a subsequent native Socket.IO reconnect.
        /// Unlike RemovePlayer this does not raise OnPlayerLeft.
        /// </summary>
        public bool MarkPlayerDisconnected(string playerId)
        {
            lock (_playerLock)
            {
                var player = _players.FirstOrDefault(p => p.PlayerId == playerId);
                if (player == null)
                    return false;

                player.IsReady = false;
                Plugin.Logger.LogInfo($"[GameRoom] Player {playerId} disconnected from room {RoomId}; slot retained");
                return true;
            }
        }

        /// <summary>
        /// 设置玩家准备状态
        /// </summary>
        public bool SetPlayerReady(string playerId, bool isReady)
        {
            lock (_playerLock)
            {
                var player = _players.FirstOrDefault(p => p.PlayerId == playerId);
                if (player == null)
                    return false;

                player.IsReady = isReady;

                Plugin.Logger.LogInfo($"[GameRoom] Player {playerId} ready state: {isReady}");
                OnPlayerReady?.Invoke(player);

                // 检查是否所有玩家都准备好了
                if (_players.Count == 2 && _players.All(p => p.IsReady))
                {
                    Plugin.Logger.LogInfo($"[GameRoom] All players ready in room {RoomId}");
                    OnAllPlayersReady?.Invoke();
                }

                return true;
            }
        }

        /// <summary>
        /// 获取房间内的所有玩家
        /// </summary>
        public List<Player> GetPlayers()
        {
            lock (_playerLock)
            {
                return new List<Player>(_players);
            }
        }

        /// <summary>
        /// 获取玩家
        /// </summary>
        public Player GetPlayer(string playerId)
        {
            lock (_playerLock)
            {
                return _players.FirstOrDefault(p => p.PlayerId == playerId);
            }
        }

        /// <summary>
        /// 获取玩家数量
        /// </summary>
        public int GetPlayerCount()
        {
            lock (_playerLock)
            {
                return _players.Count;
            }
        }

        /// <summary>
        /// 改变房间状态
        /// </summary>
        public void ChangeState(RoomState newState)
        {
            var oldState = State;
            State = newState;

            Plugin.Logger.LogInfo($"[GameRoom] Room {RoomId} state changed: {oldState} -> {newState}");
            OnStateChanged?.Invoke(oldState, newState);
        }

        /// <summary>
        /// 检查房间是否已满
        /// </summary>
        public bool IsFull()
        {
            lock (_playerLock)
            {
                return _players.Count >= 2;
            }
        }

        /// <summary>
        /// 检查所有玩家是否都准备好了
        /// </summary>
        public bool AreAllPlayersReady()
        {
            lock (_playerLock)
            {
                return _players.Count == 2 && _players.All(p => p.IsReady);
            }
        }
    }

    /// <summary>
    /// 房间状态
    /// </summary>
    public enum RoomState
    {
        /// <summary>
        /// 等待玩家加入
        /// </summary>
        Waiting,

        /// <summary>
        /// 准备中
        /// </summary>
        Preparing,

        /// <summary>
        /// 游戏中
        /// </summary>
        InGame,

        /// <summary>
        /// 已结束
        /// </summary>
        Finished
    }
}
