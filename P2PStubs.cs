using System;

namespace Shadowbus
{
    /// <summary>
    /// P2P 相关的占位符类（用于保持旧代码编译通过）
    /// 这些类在新的联机系统中已被删除，这里仅作兼容
    /// </summary>

    [Obsolete("P2P system has been removed. Use Server.OnlineRuntime instead.")]
    public static class P2PTaskRouter
    {
        public static void Initialize()
        {
            // 空实现
        }

        public static void Shutdown()
        {
            // 空实现
        }

        public static bool CanHandle(object task)
        {
            return false;
        }

        public static object Process(object instance, object task)
        {
            // 返回 null，表示不处理
            return null;
        }
    }

    [Obsolete("P2P system has been removed.")]
    public static class P2PIdentity
    {
        public static string LocalPlayerId => "local_player";
        public static int ViewerId => 0;
    }

    [Obsolete("P2P system has been removed.")]
    public static class P2PBattleProtocol
    {
        public const string ActionManifestKey = "p2p_action_manifest";
        public const string FusionMetamorphoseOriginalsKey = "p2p_fusion_originals";
    }
}
