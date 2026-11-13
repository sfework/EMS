using EMS.Common;
using EMS.Models;
using EMS.Services.Connections;
using EMS.Services.Sftp;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;

namespace EMS.Services.Mcp
{
    /// <summary>
    /// EMS MCP 工具集：只读 + 命令执行 + SFTP 文件操作，且仅面向已连接的 SSH 服务器。
    /// 服务器/分组的增删改与连接/断开均由用户在 UI 手工操作，MCP 仅协助诊断。
    /// </summary>
    [McpServerToolType]
    public class EmsMcpTools
    {
        private readonly ConnectionManager _connections;

        // SftpClient 非线程安全，串行化 MCP 侧文件操作（与 UI 并发冲突概率低，可接受）
        private static readonly SemaphoreSlim _sftpLock = new(1, 1);

        public EmsMcpTools(ConnectionManager connections)
        {
            _connections = connections;
        }

        [McpServerTool(Name = "list_connected_servers")]
        [Description("列出当前已连接的服务器（名称/ID/协助能力/文件管理能力）。" +
                    "仅包含用户已在 EMS 界面中手工连接的服务器，未连接的不显示。" +
                    "注意：只有连接类型为 SSH 的服务器可通过 run_command 提供命令协助；" +
                    "文件上传/下载/改权限走 SSH 连接的 SFTP 通道，文件管理能力为\"不可用\"时这些工具均不可用。")]
        public string ListConnectedServers()
        {
            // 按基础服务器 ID 归并（RDP+SSH 双连接会占用主键与 ":ssh" 副键），
            // 同一服务器优先展示可协助的 SSH 连接，ID 去掉副键后缀
            var byServer = _connections.SnapshotWithIds()
                .Where(p => p.Value.Status == Enums.ServerStatus.已连接 ||
                            p.Value.Status == Enums.ServerStatus.连接中)
                .GroupBy(p => p.Key.EndsWith(ConnectionManager.SshSuffix)
                    ? p.Key[..^ConnectionManager.SshSuffix.Length] : p.Key)
                .ToList();

            if (byServer.Count == 0) return "当前没有已连接的服务器";

            var lines = new List<string>();
            foreach (var group in byServer)
            {
                // SSH 连接优先（可协助）；纯 RDP 时取 RDP
                var sshPair = group.FirstOrDefault(p => p.Value is ISshConnection);
                var conn = sshPair.Value ?? group.First().Value;
                var error = string.IsNullOrEmpty(conn.ErrorMessage) ? "" : $" | 错误: {conn.ErrorMessage}";
                var assist = conn.ConnectionType == Enums.ConnectionType.SSH ? "可协助" : "仅查看(非SSH)";
                // 文件能力 = SSH 连接且 SFTP 通道在线（上传/下载/改权限均走该通道）
                var files = (conn as ISshConnection)?.Sftp is { IsConnected: true } ? "可传输" : "不可用";
                lines.Add($"- {conn.Name} | ID: {group.Key} | {assist} | 文件: {files}{error}");
            }
            return string.Join("\n", lines);
        }

        [McpServerTool(Name = "run_command")]
        [Description("在已连接的 SSH 服务器上执行【短时】命令并同步返回输出（退出码/stdout/stderr），" +
                    "默认 30 秒超时。适合 ls/ps/cd 组合查询等快速命令；" +
                    "长时间运行的命令（编译、tail -f、监控脚本等）请改用 start_long_command + read_command_output。" +
                            "命令走交互式 Shell（用户终端可见回显），前台被占用时会排队等待。仅支持已连接的 SSH 服务器。")]
        public async Task<string> RunCommandAsync(
            [Description("服务器 ID（list_connected_servers 返回的 ID）")] string serverId,
            [Description("要执行的 Shell 命令")] string command,
            [Description("超时秒数，默认 30")] int timeoutSeconds = 30)
        {
            var ssh = _connections.FindConnectedSsh(serverId);
            if (ssh == null)
                return $"错误: 服务器 {serverId} 没有已连接的 SSH 会话。请先在 EMS 界面中连接该服务器。";

            if (string.IsNullOrWhiteSpace(command))
                return "错误: 命令不能为空";

            // 走交互 Shell：命令回显/提示符由远端原生产生，观感与手工输入一致。
            // Shell 通道异常（如远端 shell 已退出）转友好文本，不向上抛裸异常
            SshCommandResult result;
            try
            {
                result = await ssh.RunInteractiveAsync(command, timeoutSeconds);
            }
            catch (Exception ex)
            {
                return $"错误: 命令无法执行 - {ex.Message}（服务器可能已断开或交互 Shell 已退出，请在 EMS 界面检查连接状态）";
            }

            var output = $"exit: {result.ExitStatus}";
            if (!string.IsNullOrEmpty(result.Output))
                output += $"\n--- stdout ---\n{result.Output.TrimEnd()}";
            if (!string.IsNullOrEmpty(result.Error))
                output += $"\n--- stderr ---\n{result.Error.TrimEnd()}";
            return output;
        }

        [McpServerTool(Name = "start_long_command")]
        [Description("在已连接的 SSH 服务器上启动【长时】命令，立即返回会话ID（不等待执行完成）。" +
                    "命令在后台持续执行、输出被缓冲（stderr 已合并进 stdout），" +
                    "用 read_command_output 按会话ID读取增量输出。" +
                    "适合编译部署、tail -f、watch、长时间脚本等场景。仅支持已连接的 SSH 服务器。")]
        public string StartLongCommand(
            [Description("服务器 ID（list_connected_servers 返回的 ID）")] string serverId,
            [Description("要执行的 Shell 命令（可长时间运行）")] string command)
        {
            var ssh = _connections.FindConnectedSsh(serverId);
            if (ssh == null)
                return $"错误: 服务器 {serverId} 没有已连接的 SSH 会话。请先在 EMS 界面中连接该服务器。";

            if (string.IsNullOrWhiteSpace(command))
                return "错误: 命令不能为空";

            try
            {
                // 长时命令同样走交互 Shell（远端原生回显）
                var session = ssh.StartLongCommand(command);
                return $"已启动长时命令，会话ID: {session.Id}\n请用 read_command_output(serverId=\"{serverId}\", sessionId=\"{session.Id}\") 读取输出。";
            }
            catch (Exception ex)
            {
                return $"错误: 启动失败 - {ex.Message}";
            }
        }

        [McpServerTool(Name = "read_command_output")]
        [Description("读取 start_long_command 启动的长时命令的当前输出。" +
                    "默认增量读取（只返回上次读取之后的新内容），running=true 表示命令仍在执行；" +
                    "命令结束后返回退出码。也可用 full=true 一次读取全部累计输出。")]
        public string ReadCommandOutput(
            [Description("服务器 ID（start_long_command 使用的 serverId）")] string serverId,
            [Description("会话 ID（start_long_command 返回的会话ID）")] string sessionId,
            [Description("true=返回全部累计输出；false(默认)=仅返回上次读取后的新增内容")] bool full = false)
        {
            var ssh = _connections.FindConnectedSsh(serverId);
            if (ssh == null)
                return $"错误: 服务器 {serverId} 没有已连接的 SSH 会话（会话可能已断开，长时命令记录已失效）。";

            var session = ssh.GetLongCommand(sessionId);
            if (session == null)
                return $"错误: 未找到会话 {sessionId}。请确认 ID 来自 start_long_command 的返回值，且服务器未断开重连。";

            var snap = session.Snapshot(!full);

            var lines = new List<string>
            {
                snap.Running ? "状态: 运行中" : $"状态: 已结束 (exit: {(snap.ExitStatus ?? -1)})" + (snap.Failure != null ? $" | 异常: {snap.Failure}" : ""),
            };
            if (!string.IsNullOrEmpty(snap.Stdout))
                lines.Add($"--- stdout{(full ? "" : "(增量)")} ---\n{snap.Stdout.TrimEnd()}");
            if (!string.IsNullOrEmpty(snap.Stderr))
                lines.Add($"--- stderr ---\n{snap.Stderr.TrimEnd()}");
            if (string.IsNullOrEmpty(snap.Stdout) && string.IsNullOrEmpty(snap.Stderr))
                lines.Add(snap.Running ? "(暂无新输出)" : "(无输出)");

            // 命令已结束则回收会话，避免缓冲无限增长
            if (!snap.Running && !full)
            {
                ssh.EndLongCommand(sessionId);
                lines.Add($"(会话 {sessionId} 已结束并回收)");
            }

            return string.Join("\n", lines);
        }

        [McpServerTool(Name = "send_interrupt")]
        [Description("向已连接 SSH 服务器的交互终端发送 Ctrl+C（SIGINT），" +
                    "用于中断卡住的前台程序（tail -f、死循环、挂起命令等）。" +
                    "前台程序退出后，此前排队的 MCP 命令会被 shell 继续执行。" +
                    "仅支持已连接的 SSH 服务器。")]
        public string SendInterrupt(
            [Description("服务器 ID（list_connected_servers 返回的 ID）")] string serverId)
        {
            var ssh = _connections.FindConnectedSsh(serverId);
            if (ssh == null)
                return $"错误: 服务器 {serverId} 没有已连接的 SSH 会话。请先在 EMS 界面中连接该服务器。";

            try
            {
                ssh.SendRaw("\x03");
                return $"已向 {serverId} 的交互终端发送 Ctrl+C (SIGINT)。";
            }
            catch (Exception ex)
            {
                return $"错误: 发送中断失败 - {ex.Message}";
            }
        }

        [McpServerTool(Name = "upload_file")]
        [Description("上传文件/文件夹到已连接的 SSH 服务器（走 SFTP 通道）。" +
                    "localPaths 是本机路径数组，每项可为文件或文件夹；remoteDir 是远端目标目录。" +
                    "文件传到 remoteDir/<文件名>；文件夹递归复刻整棵树（含空目录）到 remoteDir/<目录名>/。" +
                    "同名覆盖，父目录自动创建。大文件/大目录可能耗时较长，全部完成才返回。仅支持已连接的 SSH 服务器。")]
        public async Task<string> UploadFileAsync(
            [Description("服务器 ID（list_connected_servers 返回的 ID）")] string serverId,
            [Description("本机路径数组（每项为文件或文件夹完整路径）")] string[] localPaths,
            [Description("远端目标目录（如 /tmp/upload）")] string remoteDir)
        {
            var ssh = _connections.FindConnectedSsh(serverId);
            if (ssh == null)
                return $"错误: 服务器 {serverId} 没有已连接的 SSH 会话。请先在 EMS 界面中连接该服务器。";

            var sftp = ssh.Sftp;
            if (sftp is not { IsConnected: true })
                return "错误: 该会话的 SFTP 通道不可用。";

            if (localPaths is null || localPaths.Length == 0)
                return "错误: localPaths 不能为空";
            if (string.IsNullOrWhiteSpace(remoteDir))
                return "错误: remoteDir 不能为空";

            var targetDir = NormalizeSftpPath(remoteDir).TrimEnd('/', '\\');

            try
            {
                await _sftpLock.WaitAsync();
                try
                {
                    var browser = new SftpBrowserService(sftp);
                    var (lines, fileCount, totalBytes) = await Task.Run(() =>
                    {
                        browser.CreateDirectoryRecursive(targetDir);
                        var list = new List<string>();
                        var count = 0;
                        long bytes = 0;

                        foreach (var src in localPaths)
                        {
                            if (string.IsNullOrWhiteSpace(src)) continue;

                            if (File.Exists(src))
                            {
                                var name = Path.GetFileName(src);
                                var remote = targetDir + "/" + name;
                                using var fs = File.OpenRead(src);
                                sftp.UploadFile(fs, remote, true);
                                var size = new FileInfo(src).Length;
                                bytes += size;
                                count++;
                                list.Add($"  文件 {name}（{ByteFormat.Size(size)}）→ {remote}");
                            }
                            else if (Directory.Exists(src))
                            {
                                var dirName = new DirectoryInfo(src).Name;
                                var remoteBase = targetDir + "/" + dirName;
                                var (f, b) = UploadDirectoryRecursive(sftp, browser, src, remoteBase);
                                bytes += b;
                                count += f;
                                list.Add($"  文件夹 {dirName}/（{f} 个文件，{ByteFormat.Size(b)}）→ {remoteBase}/");
                            }
                            else
                            {
                                list.Add($"  跳过（本机不存在）: {src}");
                            }
                        }
                        return (list, count, bytes);
                    });

                    var head = $"已上传 {fileCount} 个文件（共 {ByteFormat.Size(totalBytes)}）→ {ssh.Name}({ssh.IP}):{targetDir}/";
                    return lines.Count > 0 ? head + "\n" + string.Join("\n", lines) : head;
                }
                finally
                {
                    _sftpLock.Release();
                }
            }
            catch (Exception ex)
            {
                return $"错误: 上传失败 - {ex.Message}";
            }
        }

        /// <summary>
        /// 规范化 SFTP 远端路径：SFTP 协议根为 "/"，Windows 服务器绝对路径必须写成 /C:/x 形式，
        /// 盘符开头的 C:\x 或 C:/x 会被服务端当作相对路径拼到用户主目录后（路径重复）。
        /// 复用文件管理页的转换规则；POSIX 路径（/ 开头）原样返回，不做反斜杠替换。
        /// </summary>
        public static string NormalizeSftpPath(string path)
        {
            var p = path.Trim();
            // 仅盘符形式（C:\、C:/、C:）需要转换，其余（含 POSIX 路径）保持原样
            if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':' &&
                (p.Length == 2 || p[2] == '\\' || p[2] == '/'))
                return EMS.Common.RemotePathFormat.ToRemotePath(p);
            return p;
        }

        /// <summary>递归上传本地目录到远端：先建目录（含空目录），再传文件，返回文件数与总字节</summary>
        private static (int files, long bytes) UploadDirectoryRecursive(
            Renci.SshNet.SftpClient sftp, SftpBrowserService browser, string localDir, string remoteBase)
        {
            browser.CreateDirectoryRecursive(remoteBase);

            var files = 0;
            long bytes = 0;

            foreach (var sub in Directory.GetDirectories(localDir))
            {
                var (f, b) = UploadDirectoryRecursive(sftp, browser, sub, remoteBase + "/" + Path.GetFileName(sub));
                files += f;
                bytes += b;
            }

            foreach (var file in Directory.GetFiles(localDir))
            {
                var remote = remoteBase + "/" + Path.GetFileName(file);
                using var fs = File.OpenRead(file);
                sftp.UploadFile(fs, remote, true);
                files++;
                bytes += new FileInfo(file).Length;
            }

            return (files, bytes);
        }

        [McpServerTool(Name = "download_file")]
        [Description("从已连接的 SSH 服务器下载文件/文件夹（走 SFTP 通道）。" +
                    "remotePaths 是远端路径数组，每项可为文件或目录；localDir 是本机目标目录（不存在自动创建）。" +
                    "文件存到 localDir/<文件名>；目录递归下载整棵树（含空目录）到 localDir/<目录名>/。" +
                    "同名覆盖。大文件/大目录可能耗时较长，全部完成才返回。仅支持已连接的 SSH 服务器。")]
        public async Task<string> DownloadFileAsync(
            [Description("服务器 ID（list_connected_servers 返回的 ID）")] string serverId,
            [Description("远端路径数组（每项为文件或目录完整路径）")] string[] remotePaths,
            [Description("本机目标目录")] string localDir)
        {
            var ssh = _connections.FindConnectedSsh(serverId);
            if (ssh == null)
                return $"错误: 服务器 {serverId} 没有已连接的 SSH 会话。请先在 EMS 界面中连接该服务器。";

            var sftp = ssh.Sftp;
            if (sftp is not { IsConnected: true })
                return "错误: 该会话的 SFTP 通道不可用。";

            if (remotePaths is null || remotePaths.Length == 0)
                return "错误: remotePaths 不能为空";
            if (string.IsNullOrWhiteSpace(localDir))
                return "错误: localDir 不能为空";

            try
            {
                await _sftpLock.WaitAsync();
                try
                {
                    var (lines, fileCount, totalBytes) = await Task.Run(() =>
                    {
                        Directory.CreateDirectory(localDir);
                        var list = new List<string>();
                        var count = 0;
                        long bytes = 0;

                        foreach (var raw in remotePaths)
                        {
                            if (string.IsNullOrWhiteSpace(raw)) continue;
                            var src = NormalizeSftpPath(raw);

                            Renci.SshNet.Sftp.ISftpFile entry;
                            try { entry = sftp.Get(src); }
                            catch { list.Add($"  跳过（远端不存在）: {src}"); continue; }

                            var name = entry.IsDirectory
                                ? new string(src.TrimEnd('/', '\\').Split('/')[^1])
                                : Path.GetFileName(src.TrimEnd('/', '\\'));

                            if (entry.IsDirectory)
                            {
                                var localBase = Path.Combine(localDir, name);
                                var (f, b) = DownloadDirectoryRecursive(sftp, src.TrimEnd('/', '\\'), localBase);
                                bytes += b;
                                count += f;
                                list.Add($"  目录 {name}/（{f} 个文件，{ByteFormat.Size(b)}）→ {localBase}");
                            }
                            else
                            {
                                var localPath = Path.Combine(localDir, name);
                                using (var fs = File.Create(localPath))
                                {
                                    sftp.DownloadFile(src, fs);
                                }
                                // 关流后再取长度（写缓冲落盘），否则统计为 0
                                var size = new FileInfo(localPath).Length;
                                bytes += size;
                                count++;
                                list.Add($"  文件 {name}（{ByteFormat.Size(size)}）→ {localPath}");
                            }
                        }
                        return (list, count, bytes);
                    });

                    var head = $"已下载 {fileCount} 个文件（共 {ByteFormat.Size(totalBytes)}）自 {ssh.Name}({ssh.IP}) → {localDir}";
                    return lines.Count > 0 ? head + "\n" + string.Join("\n", lines) : head;
                }
                finally
                {
                    _sftpLock.Release();
                }
            }
            catch (Exception ex)
            {
                return $"错误: 下载失败 - {ex.Message}";
            }
        }

        /// <summary>递归下载远端目录到本机：先建目录（含空目录），再下文件，返回文件数与总字节</summary>
        private static (int files, long bytes) DownloadDirectoryRecursive(
            Renci.SshNet.SftpClient sftp, string remoteDir, string localBase)
        {
            Directory.CreateDirectory(localBase);

            var files = 0;
            long bytes = 0;

            foreach (var item in sftp.ListDirectory(remoteDir))
            {
                if (item.Name is "." or "..") continue;

                if (item.IsDirectory)
                {
                    var (f, b) = DownloadDirectoryRecursive(sftp, item.FullName, Path.Combine(localBase, item.Name));
                    files += f;
                    bytes += b;
                }
                else
                {
                    var localPath = Path.Combine(localBase, item.Name);
                    using (var fs = File.Create(localPath))
                    {
                        sftp.DownloadFile(item.FullName, fs);
                    }
                    // 关流后再取长度（写缓冲落盘），否则统计为 0
                    files++;
                    bytes += new FileInfo(localPath).Length;
                }
            }

            return (files, bytes);
        }

        [McpServerTool(Name = "set_file_permission")]
        [Description("设置已连接 SSH 服务器上远端文件/目录的权限（chmod）。" +
                    "permission 为八进制字符串，如 \"755\"（rwxr-xr-x）、\"644\"（rw-r--r--）。" +
                    "通过非交互命令执行，不影响用户正在使用的交互终端。仅支持已连接的 SSH 服务器。")]
        public async Task<string> SetFilePermissionAsync(
            [Description("服务器 ID（list_connected_servers 返回的 ID）")] string serverId,
            [Description("远端文件或目录完整路径")] string remotePath,
            [Description("八进制权限，如 755、644")] string permission)
        {
            var ssh = _connections.FindConnectedSsh(serverId);
            if (ssh == null)
                return $"错误: 服务器 {serverId} 没有已连接的 SSH 会话。请先在 EMS 界面中连接该服务器。";

            if (string.IsNullOrWhiteSpace(remotePath))
                return "错误: remotePath 不能为空";

            if (string.IsNullOrWhiteSpace(permission) || !Regex.IsMatch(permission.Trim(), @"^[0-7]{3,4}$"))
                return $"错误: 权限格式无效: {permission}（应为 3-4 位八进制，如 755）";

            try
            {
                await ssh.ChangePermissionAsync(remotePath, permission.Trim());
                return $"已设置权限: {remotePath} → {permission.Trim()}";
            }
            catch (Exception ex)
            {
                return $"错误: 设置权限失败 - {ex.Message}";
            }
        }
    }
}
