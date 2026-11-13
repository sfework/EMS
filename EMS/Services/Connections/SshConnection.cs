using EMS.Common;
using EMS.Models;
using Renci.SshNet;
using System.Text;
using System.Windows;

namespace EMS.Services.Connections
{
    /// <summary>
    /// SSH 连接：持有 SshClient/SftpClient/ShellStream 三种通道。
    /// 全链路异步（不再有 .Wait() 阻塞），事件在后台线程触发、由订阅方编组。
    /// </summary>
    public class SshConnection : ISshConnection
    {
        private SshClient? _sshClient;
        private SftpClient? _sftpClient;
        private ShellStream? _shellStream;
        private UIElement? _cachedView;

        // 输出解码：UTF-8 优先、非法序列自动回退 GBK（Windows cmd 场景）
        private readonly RemoteTextDecoder _decoder = new();
        private readonly object _outputLock = new();

        // shell integration（OSC 133）就绪状态：连接后注入钩子脚本，
        // 观测到真实 ESC]133;A/C 字节即确认生效（命令回显里的字面量不含 ESC 字节，不会误判）
        private TaskCompletionSource<bool> _integrationTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _integrationReady;
        // 注入彻底失败（SFTP 写文件失败或 source 后未见标记）：置位后命令不再逐条等待探测，
        // 直接走退化模式（回显过滤 + 静默收尾兜底），避免每条命令白等 6 秒
        private volatile bool _integrationGaveUp;
        private readonly StringBuilder _integrationTail = new();

        // 交互 Shell 网格尺寸（CreateShellStream 时确定，仿真器需与之一致）
        public int ShellCols { get; private set; } = 80;
        public int ShellRows { get; private set; } = 24;

        // 长时命令会话表（后台执行、增量读取）
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LongCommandSession> _longSessions = new();
        private long _longSessionCounter;

        private Enums.ServerStatus _status = Enums.ServerStatus.未连接;
        private string? _errorMessage;

        public string Name { get; }
        public string IP { get; }
        public Enums.ConnectionType ConnectionType => Enums.ConnectionType.SSH;

        // 远端是否为 Windows（由 SSH 服务端标识串探测，决定 shell 命令方言）
        public bool IsWindowsServer { get; private set; }

        public Enums.ServerStatus Status
        {
            get => _status;
            private set
            {
                if (_status == value) return;
                _status = value;
                StatusChanged?.Invoke(value);
            }
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            private set => _errorMessage = value;
        }

        public event Action<Enums.ServerStatus>? StatusChanged;

        // 终端输出缓冲（backlog）：Linux 服务器登录后秒出 MOTD+提示符，而终端视图要等
        // Status=已连接 触发绑定后才创建并订阅，首屏输出会在"无订阅者"时广播而被丢弃。
        // 因此无订阅者时先暂存，首个订阅者挂上时按序回放（Windows OpenSSH 首包慢故此前未暴露）
        private readonly object _backlogLock = new();
        private readonly LinkedList<string> _outputBacklog = new();
        private int _outputBacklogChars;
        private const int OutputBacklogLimit = 200_000;
        private Action<string>? _outputReceived;

        public event Action<string>? OutputReceived
        {
            add
            {
                List<string>? pending = null;
                lock (_backlogLock)
                {
                    _outputReceived += value;
                    if (_outputBacklog.Count > 0)
                    {
                        // 新订阅者补收暂存输出（仅一次，避免重复回放）
                        pending = [.. _outputBacklog];
                        ClearBacklog();
                    }
                }
                if (pending != null)
                {
                    foreach (var chunk in pending) value!.Invoke(chunk);
                }
            }
            remove
            {
                lock (_backlogLock)
                {
                    _outputReceived -= value;
                }
            }
        }

        /// <summary>
        /// 发布一段终端输出：有订阅者直接广播，无订阅者则暂存待回放
        /// </summary>
        private void PublishOutput(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            Action<string>? handler;
            lock (_backlogLock)
            {
                handler = _outputReceived;
                if (handler == null)
                {
                    _outputBacklog.AddLast(text);
                    _outputBacklogChars += text.Length;
                    while (_outputBacklogChars > OutputBacklogLimit && _outputBacklog.Count > 1)
                    {
                        _outputBacklogChars -= _outputBacklog.First!.Value.Length;
                        _outputBacklog.RemoveFirst();
                    }
                    return;
                }
            }
            handler?.Invoke(text);
        }

        private void ClearBacklog()
        {
            _outputBacklog.Clear();
            _outputBacklogChars = 0;
        }

        public SftpClient? Sftp => _sftpClient;

        public SshConnection(string name, string ip)
        {
            Name = name;
            IP = ip;
        }

        // 自动重连已移除：断开即回到未连接/异常，由用户决定是否重新连接

                public async Task ConnectAsync(string userName, string password, int port, int width, int height)
                {
                    try
                    {
                        Status = Enums.ServerStatus.连接中;
                        ErrorMessage = null;

                        // SSH 通道按 Latin1 传输（字节↔字符 1:1 无损），输出经 RemoteText
                // 二次解码（UTF-8 优先、GBK 回退），解决 cmd 服务器中文乱码
                var sshInfo = new ConnectionInfo(IP, port, userName,
                    new PasswordAuthenticationMethod(userName, password ?? ""))
                {
                    Timeout = TimeSpan.FromSeconds(30),
                    Encoding = Encoding.Latin1,
                };

                _sshClient = new SshClient(sshInfo)
                {
                    KeepAliveInterval = TimeSpan.FromSeconds(3),
                };
                await Task.Run(() => _sshClient.Connect());

                // 服务端标识串探测 OS（OpenSSH for Windows banner 含 "Windows"），
                // 决定文件管理粘贴走 PowerShell 还是 POSIX 命令
                IsWindowsServer = sshInfo.ServerVersion?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true;

                // SFTP 文件名按协议为 UTF-8，独立连接信息保持 UTF-8
                var sftpInfo = new ConnectionInfo(IP, port, userName,
                    new PasswordAuthenticationMethod(userName, password ?? ""))
                {
                    Timeout = TimeSpan.FromSeconds(30),
                    Encoding = Encoding.UTF8,
                };
                _sftpClient = new SftpClient(sftpInfo);
                await Task.Run(() => _sftpClient.Connect());

                // 交互式 Shell（列/行按 8x16 像素估算终端字符尺寸）
                ShellCols = Math.Max(20, width / 8);
                ShellRows = Math.Max(8, height / 16);
                _shellStream = await Task.Run(() => _sshClient.CreateShellStream(
                    "xterm-256color",
                    (uint)ShellCols,
                    (uint)ShellRows,
                    (uint)width,
                    (uint)height,
                    1024 * 1024));

                _shellStream.DataReceived += ShellStream_DataReceived;
                _shellStream.ErrorOccurred += ShellStream_ErrorOccurred;
                _shellStream.Closed += ShellStream_Closed;

                // 初始输出（登录 MOTD 等）由 DataReceived 事件送达并经 backlog 回放给视图；
                // 不可再 Read() 缓冲区——事件不消费内部缓冲，重复读取会二次投递（Last login 显示两遍）

                Status = Enums.ServerStatus.已连接;

                // shell integration：注入 OSC 133 标记钩子（仅当前会话内存生效，不写配置文件）。
                // 后台执行不阻塞连接完成；注入结果经 _integrationOk 反馈给交互命令路径
                _ = Task.Run(InjectShellIntegrationAsync);
            }
            catch (Exception ex)
            {
                ErrorMessage = $"连接失败: {ex.Message}";
                Status = Enums.ServerStatus.异常;
                Cleanup();
            }
        }

        public void Disconnect()
                {
                    Cleanup();
                    Status = Enums.ServerStatus.未连接;
                    ErrorMessage = null;
                }

        public UIElement CreateView()
        {
            // 终端视图与连接一一绑定，创建一次后缓存
            _cachedView ??= new SSHTerminalControl(this);
            return _cachedView;
        }

        /// <summary>终端专用模式：视图隐藏文件管理页签（须在首次 CreateView 前设置）</summary>
        public bool TerminalOnly { get; set; }

        /// <summary>
                /// 调整交互 Shell 网格：通知远端 pty（SIGWINCH），top/vim 等前台程序立即按新宽度重排
                /// </summary>
                public void ResizeShell(int cols, int rows)
                {
                    if (_shellStream is not { CanWrite: true }) return;
                    if (cols == ShellCols && rows == ShellRows) return;

                    ShellCols = cols;
                    ShellRows = rows;

                    try
                    {
                        // 像素尺寸按 8x16 字符估算（仅窗口消息提示用，ptx 以 cols/rows 为准）
                        _shellStream.ChangeWindowSize((uint)cols, (uint)rows, (uint)(cols * 8), (uint)(rows * 16));
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Shell resize 失败: {ex.Message}");
                    }
                }

        public void SendRaw(string text)
        {
            if (_shellStream is not { CanWrite: true }) return;

            try
            {
                // 原样写入（不追加换行）：终端直输场景逐字符/控制序列透传，
                // 回显由远端 pty 完成
                var data = Encoding.UTF8.GetBytes(text);
                _shellStream.Write(data, 0, data.Length);
                _shellStream.Flush();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"发送命令失败: {ex.Message}";
            }
        }

        public async Task<SshCommandResult> RunCommandAsync(string command, int timeoutSeconds = 30, CancellationToken cancellationToken = default)
        {
            if (_sshClient == null)
                throw new InvalidOperationException("SSH 未连接");

            // 命令按 UTF-8 字节传输（Latin1 载体无损），中文路径不再变 '?'
            using var cmd = _sshClient.CreateCommand(RemoteText.EncodeForTransfer(command));
            cmd.CommandTimeout = TimeSpan.FromSeconds(timeoutSeconds);
            try
            {
                var output = await Task.Run(() => cmd.Execute(), cancellationToken);
                // 非交互通道同样经 UTF-8→GBK 回退解码（Latin1 无损传输）
                return new SshCommandResult(cmd.ExitStatus ?? -1, RemoteText.Fix(output ?? ""), RemoteText.Fix(cmd.Error));
            }
            catch (Exception ex)
            {
                return new SshCommandResult(-1, "", ex.Message);
            }
        }

        // 活跃交互捕获（OSC 133 集成后按 C/D 标记分段）。pty 物理上串行，
        // 同一时刻仅一个捕获占槽；长时命令占槽直到结束，期间短命令排队等待
        private readonly object _captureGate = new();
        private volatile ShellCommandCapture? _activeCapture;

        private bool TryAttachCapture(ShellCommandCapture capture)
        {
            lock (_captureGate)
            {
                if (_activeCapture != null) return false;
                _activeCapture = capture;
                return true;
            }
        }

        private void DetachCapture(ShellCommandCapture capture)
        {
            lock (_captureGate)
            {
                if (ReferenceEquals(_activeCapture, capture)) _activeCapture = null;
            }
        }

        /// <summary>等待捕获槽释放（长时命令/前台程序占用期间轮询，超时放弃）</summary>
        private async Task<bool> WaitCaptureSlotAsync(ShellCommandCapture capture, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                if (TryAttachCapture(capture)) return true;
                if (DateTime.UtcNow >= deadline) return false;
                await Task.Delay(200);
            }
        }

        /// <summary>
        /// shell integration 生效探测：在输出流里找真实 ESC]133;A / ESC]133;C 序列（跨块用滚动尾窗）。
        /// 只认 A/C（钩子专属标记），不认 D——退化模式下内联标记也发 D，若认 D 会把
        /// "仅内联兜底"误判为"钩子已生效"，导致后续命令失去标记而等满超时。
        /// 一旦命中永久置位，后续不再扫描
        /// </summary>
        private void DetectIntegration(string text)
        {
            if (_integrationReady) return;

            string tail;
            lock (_outputLock)
            {
                _integrationTail.Append(text);
                // 保留足够覆盖 "ESC]133;A" 被拆分的尾部
                const int keep = 16;
                if (_integrationTail.Length > keep + text.Length)
                {
                    _integrationTail.Remove(0, _integrationTail.Length - keep);
                }
                tail = _integrationTail.ToString();
            }

            if (tail.Contains("\x1b]133;A", StringComparison.Ordinal) ||
                tail.Contains("\x1b]133;C", StringComparison.Ordinal))
            {
                _integrationReady = true;
                _integrationTcs.TrySetResult(true);
            }
        }

        /// <summary>
        /// 注入 shell integration 钩子（仅当前会话，不写配置文件）：
        /// bash 用 PROMPT_COMMAND 发 133;D;码 + 133;A，PS0 发 133;C；
        /// PowerShell 重写 prompt 函数发 133;D;码 + 133;A（5.1 无 PS0，C 缺失由回显过滤兜底）。
        /// 脚本本体经 SFTP 通道写入远端用户主目录（方言无关、可靠；exec 通道在 Windows
        /// OpenSSH 上跑的是 cmd，PowerShell 写文件命令会静默失败），交互 pty 只发一条短 source 命令。
        /// source 后等待标记探测确认生效；彻底失败则置 _integrationGaveUp，
        /// 后续命令不再白等探测，直接走退化模式（回显过滤 + 静默收尾）
        /// </summary>
        private async Task InjectShellIntegrationAsync()
        {
            // 等待连接稳定（登录 MOTD 期间不注入，避免被吞）
            await Task.Delay(1200);
            if (_shellStream is not { CanWrite: true }) return;

            // bash：PROMPT_COMMAND 发 D;码 + A（保留原有钩子），PS0 发 C（命令执行前）；
            // PowerShell：重写全局 prompt 函数发 D;码 + A（5.1 无 PS0，C 缺失由回显过滤兜底）。
            // 脚本幂等（重跑仅覆盖同名函数/变量），固定短文件名放用户主目录，
            // 保证 pty 里的 source 行 ≤30 字符——窄终端（实测约 58 列）不折行。
            // 脚本末尾输出"上移一行+清行+清屏下"：擦除 source 命令自身的回显行，
            // 新提示符恰好打印在擦除处——连接后终端看不到任何"自动输入"痕迹
            const string bashScript = "__ems_d(){ local __e=$?; printf '\\033]133;D;%s\\007\\033]133;A\\007' \"$__e\"; }; PROMPT_COMMAND=\"__ems_d${PROMPT_COMMAND:+;$PROMPT_COMMAND}\"; PS0=$'\\e]133;C\\a'; printf '\\e[1A\\e[2K\\e[J'";
            const string psScript = "function global:prompt { $e = if ($?) { 0 } else { 1 }; [Console]::Write([char]27 + \"]133;D;$e\" + [char]7 + [char]27 + \"]133;A\" + [char]7); \"PS $($PWD.Path)> \" }; [Console]::Write([char]27 + \"[1A\" + [char]27 + \"[2K\" + [char]27 + \"[J\")";

            var script = IsWindowsServer ? psScript : bashScript;
            var remoteName = IsWindowsServer ? "ems-integ.ps1" : ".ems-integ.sh";
            // Windows：不能用 ". $HOME\..." 点源——ExecutionPolicy=Restricted 会拦截 .ps1 文件执行
            // （实测报 UnauthorizedAccess）；iex 执行读入的字符串不受策略约束，且 ≤30 字符不折行
            var sourceCmd = IsWindowsServer ? "iex(gc $HOME\\ems-integ.ps1)" : ". $HOME/.ems-integ.sh";

            try
            {
                // SFTP 相对路径落在用户主目录（服务端登录初始目录）
                var sftp = _sftpClient;
                if (sftp is not { IsConnected: true }) { _integrationGaveUp = true; return; }
                using var ms = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(script));
                await Task.Run(() => sftp.UploadFile(ms, remoteName, true));

                SendRaw(sourceCmd + "\r");

                // 确认钩子真的产出标记（source 报错/被吞则不认账）；失败置终态不再重试
                if (!await WaitForIntegrationAsync(TimeSpan.FromSeconds(5)))
                {
                    _integrationGaveUp = true;
                }
            }
            catch
            {
                // 注入失败不影响连接：命令走退化模式（回显过滤 + 静默收尾兜底）
                _integrationGaveUp = true;
            }
        }

        /// <summary>
        /// 等待 shell integration 就绪（带超时）。就绪则命令可精确按 C/D 分段；
        /// 超时返回 false，命令仍可执行但退化为"从挂载即收集"（可能含少量回显噪声）
        /// </summary>
        private async Task<bool> WaitForIntegrationAsync(TimeSpan timeout)
        {
            if (_integrationReady) return true;
            var finished = await Task.WhenAny(_integrationTcs.Task, Task.Delay(timeout)) == _integrationTcs.Task;
            return _integrationReady;
        }

        /// <summary>
        /// 通过交互 Shell 执行命令并捕获输出（MCP 用）：命令原文直发 pty，
        /// 起止与退出码由 shell integration 注入的 OSC 133 不可见标记界定——
        /// 终端观感与手工输入完全一致（无包装、无哨兵行、无折行碎片）。
        /// 集成未就绪（注入失败或首条命令撞窗口）时退化为"回显过滤 + 静默收尾"：
        /// 以 IsIdleCompleted 轮询判结束，不再死等到超时
        /// </summary>
        public async Task<SshCommandResult> RunInteractiveAsync(string command, int timeoutSeconds = 30)
        {
            if (_shellStream is not { CanWrite: true })
                throw new InvalidOperationException("交互 Shell 未就绪");

            // 首条命令可能撞上注入未完成窗口：短暂等待集成就绪；注入已彻底失败则跳过白等
            if (!_integrationGaveUp)
                await WaitForIntegrationAsync(TimeSpan.FromSeconds(6));

            // 集成未就绪时无法靠 C/D 分段，退化为全程收集（含回显噪声，Windows 同款过滤）
            var degraded = !_integrationReady;
            var capture = new ShellCommandCapture(IsWindowsServer || degraded);
            capture.SetCommand(command);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.CompletedEvent += _ => done.TrySetResult(true);

            if (!await WaitCaptureSlotAsync(capture, TimeSpan.FromSeconds(timeoutSeconds)))
            {
                return new SshCommandResult(-1, "", "Shell 被运行中的命令占用：等待其结束，或用 send_interrupt 中断");
            }

            try
            {
                SendRaw(command.TrimEnd('\r', '\n') + "\r");

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
                bool finished = false;
                bool byIdle = false;
                // 就绪模式：done 由 D 标记触发，一次等待到超时即可；
                // 退化模式：命令结束不会有 D 标记，改以"回显已见 + 输出静默 + 提示符复现"轮询判收尾
                while (!finished && DateTime.UtcNow < deadline)
                {
                    var remain = deadline - DateTime.UtcNow;
                    if (!degraded)
                    {
                        finished = await Task.WhenAny(done.Task, Task.Delay(remain)) == done.Task;
                        break;
                    }
                    if (await Task.WhenAny(done.Task, Task.Delay(200)) == done.Task) { finished = true; break; }
                    if (capture.IsIdleCompleted() &&
                        await Task.WhenAny(done.Task, Task.Delay(300)) != done.Task)
                    {
                        // 静默窗口成立且宽限期内 D 标记仍未到：按静默收尾（退出码不可知，记 -1）
                        capture.Abort();
                        finished = true;
                        byIdle = true;
                        break;
                    }
                }

                if (!finished)
                {
                    capture.Abort();
                    try { SendRaw("\x03"); } catch { /* 尽力解锁，失败忽略 */ }
                }

                var output = string.Join("\n", capture.Output);
                var note = finished ? (byIdle ? "（按输出静默+提示符判完成，退出码不可靠）" : "") : "命令执行超时";
                return new SshCommandResult(capture.ExitCode ?? -1, output, note);
            }
            finally
            {
                DetachCapture(capture);
            }
        }

        public LongCommandSession StartLongCommand(string command)
        {
            if (_shellStream is not { CanWrite: true })
                throw new InvalidOperationException("交互 Shell 未就绪");

            // 集成未就绪时无法靠 C 标记界定起点，退化为全程收集（含回显，Windows 同款过滤）
            var capture = new ShellCommandCapture(IsWindowsServer || !_integrationReady);
            capture.SetCommand(command);
            if (!TryAttachCapture(capture))
            {
                throw new InvalidOperationException("Shell 被运行中的命令占用：请等待结束或用 send_interrupt 中断");
            }

            var session = new LongCommandSession($"{Name}-{Interlocked.Increment(ref _longSessionCounter)}");
            _longSessions[session.Id] = session;

            // 输出逐行流入会话（无超时，直到 133;D 命中或中断）
            capture.OnLine = session.AppendStdout;
            capture.CompletedEvent += c =>
            {
                session.Complete(c.ExitCode ?? -1);
                DetachCapture(c);
            };

            // 退化模式（集成未就绪）下命令结束不会有 D 标记，捕获器将永久占槽：
            // 挂静默看门狗，以"回显已见 + 输出静默 + 提示符复现"判收尾并释放槽位
            if (!_integrationReady)
            {
                _ = WatchIdleCompletionAsync(capture);
            }

            SendRaw(command.TrimEnd('\r', '\n') + "\r");
            return session;
        }

        /// <summary>
        /// 退化模式长时命令的静默看门狗：捕获器判静默收尾后终止捕获（触发 CompletedEvent
        /// 完成会话并释放槽位）；捕获器因 D 标记/中断先结束则自然退出
        /// </summary>
        private static async Task WatchIdleCompletionAsync(ShellCommandCapture capture)
        {
            while (!capture.Completed)
            {
                await Task.Delay(1000);
                if (capture.Completed) return;
                if (capture.IsIdleCompleted(idleMs: 3000))
                {
                    capture.Abort();
                    return;
                }
            }
        }

        public LongCommandSession? GetLongCommand(string sessionId)
            => _longSessions.TryGetValue(sessionId, out var session) ? session : null;

        public void EndLongCommand(string sessionId)
        {
            _longSessions.TryRemove(sessionId, out _);
        }

        /// <summary>
        /// 通过 chmod 修改远端权限（复用主连接的非交互命令通道）
        /// </summary>
        public async Task ChangePermissionAsync(string remotePath, string permission)
        {
            var result = await RunCommandAsync($"chmod {permission} \"{remotePath.Replace("\"", "\\\"")}\"");
            if (result.ExitStatus != 0)
            {
                throw new InvalidOperationException($"修改权限失败: {result.Error}");
            }
        }

        private void ShellStream_DataReceived(object? sender, Renci.SshNet.Common.ShellDataEventArgs e)
        {
            try
            {
                // 自适应解码（UTF-8/GBK，增量处理跨块字符），
                // 转义序列原样透传，由 UI 端 XTerm.NET 仿真器解释
                string text;
                lock (_outputLock)
                {
                    text = _decoder.Decode(e.Data, 0, e.Data.Length);
                }
                if (!string.IsNullOrEmpty(text))
                {
                    // shell integration 生效探测：观测到真实 ESC]133; 字节（回显里的字面量
                    // 不含 ESC 字节，不会误判）。跨块可能拆分，用滚动尾窗匹配
                    DetectIntegration(text);

                    // 交互命令捕获：解码后的共享流喂给活跃捕获器（OSC 133 标记分段，串行单槽）
                    _activeCapture?.Feed(text);
                    PublishOutput(text);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"终端输出解析失败: {ex.Message}");
            }
        }

        private void ShellStream_ErrorOccurred(object? sender, Renci.SshNet.Common.ExceptionEventArgs e)
                {
                    // 不做自动重连：通道异常即置异常状态，由用户决定是否重新连接
                    ErrorMessage = $"SSH 错误: {e.Exception.Message}";
                    if (Status == Enums.ServerStatus.已连接) Status = Enums.ServerStatus.异常;
                }

        private void ShellStream_Closed(object? sender, EventArgs e)
        {
            // 远端 shell 进程退出（如手工 exit）：通道关闭但 TCP/SFTP 可能仍好。
            // 主动断开清理（Status 已非"已连接"）时不重复处理
            if (Status != Enums.ServerStatus.已连接) return;
            ErrorMessage = "交互 Shell 已退出（远端 shell 进程结束），请重新连接";
            Status = Enums.ServerStatus.异常;
            // 终止活跃捕获：RunInteractive 立即返回已截获部分，不再死等到超时
            _activeCapture?.Abort();
        }

        private void Cleanup()
        {
            if (_shellStream != null)
            {
                _shellStream.DataReceived -= ShellStream_DataReceived;
                _shellStream.ErrorOccurred -= ShellStream_ErrorOccurred;
                _shellStream.Closed -= ShellStream_Closed;
            }

            try { _shellStream?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            try { _sftpClient?.Disconnect(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            try { _sftpClient?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            try { _sshClient?.Disconnect(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
            try { _sshClient?.Dispose(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }

            _shellStream = null;
            _sftpClient = null;
            _sshClient = null;

            // 复位解码状态，重连后按新会话重新探测编码
            lock (_outputLock)
            {
                _decoder.Reset();
            }

            // 丢弃未回放的暂存输出，避免重连时回放旧会话内容
            lock (_backlogLock)
            {
                ClearBacklog();
            }

            // 通知所有未完成的长时会话：连接已关闭
            foreach (var session in _longSessions.Values)
            {
                session.MarkConnectionClosed();
            }
            _longSessions.Clear();

            // 终止活跃捕获：RunInteractive 立即返回已截获部分（同时释放超时 Delay）
            _activeCapture?.Abort();
            lock (_captureGate)
            {
                _activeCapture = null;
            }

            // 复位 shell integration 状态，重连后重新注入并探测
            lock (_outputLock)
            {
                _integrationReady = false;
                _integrationGaveUp = false;
                _integrationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _integrationTail.Clear();
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
