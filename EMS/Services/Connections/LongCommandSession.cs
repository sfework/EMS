using System.Text;

namespace EMS.Services.Connections
{
    /// <summary>
    /// 长时命令输出会话：后台持续缓冲 stdout/stderr，支持全量或增量读取。
    /// 由 SshConnection 创建并推进，读取方（MCP 等）轮询 Snapshot。
    /// </summary>
    public class LongCommandSession
    {
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly object _sync = new();
        private int _stdoutCursor;
        private int _stderrCursor;

        public string Id { get; }

        public bool IsCompleted { get; private set; }
        public int? ExitStatus { get; private set; }
        public string? Failure { get; private set; }

        public LongCommandSession(string id) => Id = id;

        /// <summary>追加一行 stdout（捕获器逐行送达）</summary>
        public void AppendStdout(string? line)
        {
            if (line == null) return;
            lock (_sync)
            {
                _stdout.Append(line).Append('\n');
            }
        }

        public void Complete(int exitStatus)
        {
            lock (_sync)
            {
                IsCompleted = true;
                ExitStatus = exitStatus;
            }
        }

        public void MarkConnectionClosed()
        {
            lock (_sync)
            {
                if (!IsCompleted)
                {
                    IsCompleted = true;
                    Failure = "连接已关闭";
                }
            }
        }

        /// <summary>
        /// 读取输出快照；incremental=true 时仅返回自上次读取以来的新增内容，
        /// 并推进游标（同一会话连续增量读取不重复返回）
        /// </summary>
        public LongCommandSnapshot Snapshot(bool incremental)
        {
            lock (_sync)
            {
                var stdout = incremental
                    ? _stdout.ToString(_stdoutCursor, _stdout.Length - _stdoutCursor)
                    : _stdout.ToString();
                var stderr = incremental
                    ? _stderr.ToString(_stderrCursor, _stderr.Length - _stderrCursor)
                    : _stderr.ToString();
                _stdoutCursor = _stdout.Length;
                _stderrCursor = _stderr.Length;

                return new LongCommandSnapshot(!IsCompleted, ExitStatus, stdout, stderr, Failure);
            }
        }
    }

    /// <summary>
    /// 长时命令某时刻的输出快照
    /// </summary>
    public record LongCommandSnapshot(bool Running, int? ExitStatus, string Stdout, string Stderr, string? Failure);
}