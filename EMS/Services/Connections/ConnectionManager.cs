using EMS.Models;
using System.Collections.Concurrent;

namespace EMS.Services.Connections
{
    /// <summary>
    /// 已打开连接的会话注册表：UI Tab 打开/关闭连接时登记，
    /// MCP 等非 UI 调用方据此查找"当前已连接的 SSH 服务器"并执行命令。
    /// </summary>
    public class ConnectionManager
    {
        private readonly ConcurrentDictionary<string, IServerConnection> _sessions = new();

        // RDP+SSH 双连接共存：SSH 会话以副键注册，避免覆盖主键上的 RDP 连接
        public const string SshSuffix = ":ssh";

        /// <summary>
        /// 注册（或替换）某服务器的活动连接；带后缀键用于双连接场景
        /// </summary>
        public void Register(string serverId, IServerConnection connection)
        {
            _sessions[serverId] = connection;
        }

        public void Unregister(string key)
        {
            _sessions.TryRemove(key, out _);
        }

        public IServerConnection? Find(string serverId)
            => _sessions.TryGetValue(serverId, out var conn) ? conn : null;

        /// <summary>
        /// 查找已连接（状态=已连接）的 SSH 会话：主键（纯 SSH 服务器）与副键（RDP+SSH）都查
        /// </summary>
        public ISshConnection? FindConnectedSsh(string serverId)
        {
            if (Find(serverId) is ISshConnection primary && primary.Status == Enums.ServerStatus.已连接)
                return primary;
            if (Find(serverId + SshSuffix) is ISshConnection secondary && secondary.Status == Enums.ServerStatus.已连接)
                return secondary;
            return null;
        }

        /// <summary>
        /// 当前所有活动连接的 (服务器ID, 连接) 快照
        /// </summary>
        public IReadOnlyList<KeyValuePair<string, IServerConnection>> SnapshotWithIds()
            => _sessions.ToList();
    }
}
