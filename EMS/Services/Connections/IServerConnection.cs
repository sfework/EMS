using EMS.Models;
using System.Windows;

namespace EMS.Services.Connections
{
    /// <summary>
    /// 服务器连接抽象：纯异步、无 UI 依赖的核心接口 + 视图内容工厂。
    /// RDP/SSH 实现各自负责创建自己的显示控件；后续 MCP 等非 UI 调用方
    /// 只使用连接/状态/命令部分，不触碰 CreateView。
    /// </summary>
    public interface IServerConnection : IDisposable
    {
        string Name { get; }
        string IP { get; }
        Enums.ConnectionType ConnectionType { get; }
        Enums.ServerStatus Status { get; }
        string? ErrorMessage { get; }

        /// <summary>状态变化事件（可能在非 UI 线程触发，订阅方负责编组）</summary>
        event Action<Enums.ServerStatus>? StatusChanged;

        Task ConnectAsync(string userName, string password, int port, int width, int height);

        void Disconnect();

        /// <summary>
        /// 创建（或返回已缓存的）显示内容，仅 UI 场景使用
        /// </summary>
        UIElement CreateView();
    }

    /// <summary>
    /// SSH 连接的扩展能力：交互式终端流 + 非交互式命令执行（供 MCP/自动化使用）
    /// </summary>
    public interface ISshConnection : IServerConnection
    {
        /// <summary>
        /// 终端输出事件：按远端控制台编码解码的原始文本（保留转义序列，
        /// 由终端仿真器处理；可能在非 UI 线程触发）
        /// </summary>
        event Action<string>? OutputReceived;

        /// <summary>交互 Shell 的列/行数（仿真器网格必须与远端 pty 一致）</summary>
        int ShellCols { get; }
        int ShellRows { get; }

        /// <summary>
        /// 终端专用模式：视图只提供命令终端（隐藏文件管理页签）。
        /// RDP+SSH 组合视图内嵌命令终端时置 true，避免页签嵌套。须在首次 CreateView 前设置
        /// </summary>
        bool TerminalOnly { get; set; }

        /// <summary>
        /// 调整交互 Shell 网格尺寸：通知远端 pty（SIGWINCH，top/vim 等立即按新宽度重排）
        /// 并同步更新本地仿真器网格
        /// </summary>
        void ResizeShell(int cols, int rows);

        /// <summary>向交互式 Shell 写入原始字符（不追加换行，控制序列由调用方负责）</summary>
        void SendRaw(string text);

        /// <summary>
        /// 通过交互式 Shell 执行命令并捕获输出（MCP 用）：命令原文直发 pty，
        /// 起止与退出码由 shell integration 注入的 OSC 133 不可见标记界定
        /// （终端观感同手工输入，无包装、无哨兵行）；命令串行执行，前台占用时排队
        /// </summary>
        Task<SshCommandResult> RunInteractiveAsync(string command, int timeoutSeconds = 30);

        /// <summary>
        /// 非交互式执行命令并返回结果（独立 SshCommand，不影响交互终端、不回显）。
        /// 供文件管理等静默场景使用
        /// </summary>
        Task<SshCommandResult> RunCommandAsync(string command, int timeoutSeconds = 30, CancellationToken cancellationToken = default);

        /// <summary>
        /// 启动长时命令：立即返回会话对象，输出在后台持续缓冲，
        /// 由调用方通过会话 Snapshot 增量读取（无超时限制）。
        /// 走交互式 Shell（同 RunInteractiveAsync），终端回显由远端原生产生
        /// </summary>
        LongCommandSession StartLongCommand(string command);

        /// <summary>获取某长时命令会话（不存在返回 null）</summary>
        LongCommandSession? GetLongCommand(string sessionId);

        /// <summary>结束并移除长时命令会话</summary>
        void EndLongCommand(string sessionId);

        /// <summary>底层 SFTP 客户端（未连接为 null），供 SFTP 服务使用</summary>
        Renci.SshNet.SftpClient? Sftp { get; }

        /// <summary>远端是否为 Windows（由服务端标识串探测，决定 shell 命令方言）</summary>
        bool IsWindowsServer { get; }

        /// <summary>通过 chmod 修改远端文件权限（八进制字符串，如 "755"）</summary>
        Task ChangePermissionAsync(string remotePath, string permission);
    }

    /// <summary>
    /// 非交互式命令执行结果
    /// </summary>
    public record SshCommandResult(int ExitStatus, string Output, string Error);
}
