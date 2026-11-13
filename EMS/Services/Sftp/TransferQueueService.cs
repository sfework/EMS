using Renci.SshNet;
using System.IO;

namespace EMS.Services.Sftp
{
    /// <summary>
    /// 文件传输队列：上传下载（含目录递归）、进度与取消，无 UI 依赖。
    /// 传输任务集合暴露给 UI 绑定（FileTransferTask）。
    /// </summary>
    public class TransferQueueService
    {
        private readonly SftpClient _client;
        private readonly SftpBrowserService _browser;

        public System.Collections.ObjectModel.ObservableCollection<FileTransferTask> Tasks { get; } = [];

        public TransferQueueService(SftpClient client, SftpBrowserService browser)
        {
            _client = client;
            _browser = browser;
        }

        /// <summary>
        /// 上传本地文件或目录（目录自动递归）。
        /// </summary>
        public FileTransferTask EnqueueUpload(string localPath, string remotePath)
        {
            if (Directory.Exists(localPath))
            {
                var task = CreateTask($"上传目录 {Path.GetFileName(localPath)}", GetDirectorySize(localPath), remotePath);
                Start(task, ct => UploadDirectoryCore(localPath, remotePath, ct));
                return task;
            }
            var fileTask = CreateTask($"上传 {Path.GetFileName(localPath)}", new FileInfo(localPath).Length, remotePath);
            Start(fileTask, ct => UploadFileCore(localPath, remotePath, fileTask, ct));
            return fileTask;
        }

        /// <summary>
        /// 下载远端文件或目录到本地目录
        /// </summary>
        public FileTransferTask EnqueueDownload(RemoteFileEntry remoteFile, string localDirectory)
        {
            if (remoteFile.IsDirectory)
            {
                var task = CreateTask($"下载目录 {remoteFile.Name}", 0, remoteFile.FullPath);
                Start(task, ct => DownloadDirectoryCore(remoteFile.FullPath, localDirectory, ct));
                return task;
            }
            var localPath = Path.Combine(localDirectory, remoteFile.Name);
            var fileTask = CreateTask($"下载 {remoteFile.Name}", remoteFile.Size, remoteFile.FullPath);
            Start(fileTask, ct => DownloadFileCore(remoteFile.FullPath, localPath, fileTask, ct));
            return fileTask;
        }

        private FileTransferTask CreateTask(string title, long totalBytes, string targetPath)
        {
            var task = new FileTransferTask(title, totalBytes, targetPath);
            Tasks.Add(task);
            return task;
        }

        /// <summary>
        /// 服务器端 shell 操作任务（远→远 复制/移动/删除）：无字节流回调。
        /// totalBytes=0 约定为不确定进度（UI 滚动条），由调用方在命令结束时调用 FinishRemoteOperation
        /// </summary>
        public FileTransferTask EnqueueRemoteOperation(string title, long totalBytes, string targetPath)
            => CreateTask(title, totalBytes, targetPath);

        /// <summary>结束服务器端操作任务（成功/失败由命令退出码决定）</summary>
        public void FinishRemoteOperation(FileTransferTask task, bool success)
        {
            if (success) task.MarkCompleted();
            else task.MarkFailed("命令执行失败");
        }

        private void Start(FileTransferTask task, Func<CancellationToken, Task> work)
        {
            var cts = new CancellationTokenSource();
            task.AttachCancellation(cts);

            _ = Task.Run(async () =>
            {
                try
                {
                    await work(cts.Token);
                    task.MarkCompleted();
                }
                catch (OperationCanceledException)
                {
                    task.MarkCancelled();
                }
                catch (Exception ex)
                {
                    task.MarkFailed(ex.Message);
                }
                finally
                {
                    cts.Dispose();
                }
            });
        }

        private Task UploadFileCore(string localPath, string remotePath, FileTransferTask? task, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var fileStream = File.OpenRead(localPath);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool aborted = false;

                try
                {
                    // SSH.NET 同步 API + 进度回调；回调中检测取消并关闭流以中断传输
                    _client.UploadFile(fileStream, remotePath, true, bytes =>
                    {
                        if (ct.IsCancellationRequested)
                        {
                            aborted = true;
                            try { fileStream.Close(); } catch { }
                            return;
                        }
                        var speed = sw.Elapsed.TotalSeconds > 0 ? bytes / sw.Elapsed.TotalSeconds : 0;
                        task?.UpdateProgress((long)bytes, speed);
                    });
                }
                catch when (aborted)
                {
                    // 取消导致的中断异常按取消处理
                }

                if (aborted || ct.IsCancellationRequested)
                {
                    TryDeleteRemote(remotePath);
                    ct.ThrowIfCancellationRequested();
                }
            }, ct);
        }

        private Task DownloadFileCore(string remotePath, string localPath, FileTransferTask? task, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var fileStream = File.Create(localPath);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool aborted = false;

                try
                {
                    _client.DownloadFile(remotePath, fileStream, bytes =>
                    {
                        if (ct.IsCancellationRequested)
                        {
                            aborted = true;
                            try { fileStream.Close(); } catch { }
                            return;
                        }
                        var speed = sw.Elapsed.TotalSeconds > 0 ? bytes / sw.Elapsed.TotalSeconds : 0;
                        task?.UpdateProgress((long)bytes, speed);
                    });
                }
                catch when (aborted)
                {
                }

                if (aborted || ct.IsCancellationRequested)
                {
                    TryDeleteLocal(localPath);
                    ct.ThrowIfCancellationRequested();
                }
            }, ct);
        }

        private async Task UploadDirectoryCore(string localPath, string remoteBase, CancellationToken ct)
        {
            // remoteBase 即目标目录全路径（与文件上传语义一致：调用方已含目录名），不再二次拼接
            var targetDir = remoteBase.TrimEnd('/');
            _browser.CreateDirectoryRecursive(targetDir);

            foreach (var file in Directory.GetFiles(localPath, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                var relative = Path.GetRelativePath(localPath, file).Replace('\\', '/');
                var remoteFile = $"{targetDir}/{relative}";
                var remoteDir = SftpBrowserService.GetParent(remoteFile);
                if (!string.IsNullOrEmpty(remoteDir))
                {
                    _browser.CreateDirectoryRecursive(remoteDir);
                }

                // 目录传输中的单个文件不挂任务进度，由父任务统一展示
                await UploadFileCore(file, remoteFile, null, ct);
            }
        }

        private async Task DownloadDirectoryCore(string remotePath, string localBase, CancellationToken ct)
        {
            var dirName = new DirectoryInfo(remotePath.TrimEnd('/')).Name;
            var targetDir = Path.Combine(localBase, dirName);
            Directory.CreateDirectory(targetDir);

            foreach (var remoteFile in _browser.GetFilesRecursive(remotePath))
            {
                ct.ThrowIfCancellationRequested();

                var relative = remoteFile.Substring(remotePath.Length).TrimStart('/');
                var localFile = Path.Combine(targetDir, relative.Replace('/', Path.DirectorySeparatorChar));
                var localDir = Path.GetDirectoryName(localFile);
                if (!string.IsNullOrEmpty(localDir))
                {
                    Directory.CreateDirectory(localDir);
                }

                await DownloadFileCore(remoteFile, localFile, null, ct);
            }
        }

        private void TryDeleteRemote(string path)
        {
            try { if (_client.Exists(path)) _client.DeleteFile(path); } catch { }
        }

        private static void TryDeleteLocal(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        public static long GetDirectorySize(string path)
        {
            try
            {
                long total = 0;
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    total += new FileInfo(file).Length;
                }
                return total;
            }
            catch
            {
                return 0;
            }
        }
    }
}
