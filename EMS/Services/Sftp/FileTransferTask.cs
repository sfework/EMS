using EMS.Common;

namespace EMS.Services.Sftp
{
    /// <summary>
    /// 单个传输任务（UI 绑定用），由 TransferQueueService 创建与推进
    /// </summary>
    public class FileTransferTask : NotifyPropertyChanged
    {
        private long _transferredBytes;
        private double _speed;
        private TransferStatus _status = TransferStatus.Transferring;
        private CancellationTokenSource? _cts;

        public FileTransferTask(string title, long fileSize, string targetPath)
        {
            Title = title;
            FileSize = fileSize;
            TargetPath = targetPath;
        }

        /// <summary>任务显示名，如 "上传 foo.txt"</summary>
        public string Title { get; }

        public long FileSize { get; }

        /// <summary>远端或本地目标路径</summary>
        public string TargetPath { get; }

        public long TransferredBytes
        {
            get => _transferredBytes;
            private set
            {
                _transferredBytes = value;
                OnPropertyChanged(nameof(TransferredBytes));
                OnPropertyChanged(nameof(TransferredText));
                OnPropertyChanged(nameof(ProgressPercentage));
            }
        }

        public string TransferredText => ByteFormat.Size(_transferredBytes);
        public string SizeText => FileSize > 0 ? ByteFormat.Size(FileSize) : "";

        public double Speed
        {
            get => _speed;
            private set
            {
                _speed = value;
                OnPropertyChanged(nameof(Speed));
                OnPropertyChanged(nameof(SpeedText));
            }
        }

        public string SpeedText => ByteFormat.Speed(_speed);

        public double ProgressPercentage => FileSize > 0 ? (double)TransferredBytes / FileSize : 0;

        /// <summary>
        /// 服务器端 shell 操作（远→远 复制/移动/删除）无字节流回调：
        /// FileSize=0 约定为不确定进度，UI 进度条跑滚动动画
        /// </summary>
        public bool IsIndeterminate => FileSize == 0 && _status == TransferStatus.Transferring;

        public TransferStatus Status
        {
            get => _status;
            private set
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsIndeterminate));
            }
        }

        public string StatusText => _status switch
        {
            TransferStatus.Transferring => "传输中",
            TransferStatus.Completed => "已完成",
            TransferStatus.Failed => "失败",
            TransferStatus.Cancelled => "已取消",
            _ => "未知",
        };

        public bool IsRunning => _status == TransferStatus.Transferring;

        public string? Error { get; private set; }

        /// <summary>
        /// 取消传输（仅运行中有效）
        /// </summary>
        public void Cancel()
        {
            if (!IsRunning) return;
            try { _cts?.Cancel(); } catch { }
        }

        internal void AttachCancellation(CancellationTokenSource cts) => _cts = cts;

        /// <summary>
        /// 进度更新（后台线程调用，编组到 UI 线程通知）
        /// </summary>
        internal void UpdateProgress(long transferred, double speed)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                TransferredBytes = transferred;
                Speed = speed;
                return;
            }

            // 高频回调下用 BeginInvoke 排队，值取最新即可
            dispatcher.BeginInvoke(new Action(() =>
            {
                TransferredBytes = transferred;
                Speed = speed;
            }));
        }

        internal void MarkCompleted() => SetOnUi(() => Status = TransferStatus.Completed);

        internal void MarkCancelled() => SetOnUi(() => Status = TransferStatus.Cancelled);

        internal void MarkFailed(string message) => SetOnUi(() =>
        {
            Error = message;
            Status = TransferStatus.Failed;
        });

        private void SetOnUi(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                action();
                return;
            }
            if (dispatcher.CheckAccess()) action();
            else dispatcher.Invoke(action);
        }
    }

    public enum TransferStatus
    {
        Transferring,
        Completed,
        Failed,
        Cancelled,
    }
}
