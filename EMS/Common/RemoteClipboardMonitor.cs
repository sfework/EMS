using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EMS.Common
{
    /// <summary>
    /// 系统剪贴板变更监听（WM_CLIPBOARDUPDATE）：用于判定"本应用最后复制的远程内容
    /// 是否已被外部复制（如资源管理器复制本地文件）覆盖"。
    /// 不依赖剪贴板自定义格式往返——Win10/11 剪贴板历史会丢弃自定义格式，
    /// ContainsData 轮询曾导致远程复制第二次起无法粘贴。
    /// 本应用自身写剪贴板（WPF 延迟渲染）会连续触发多条消息，按 GetClipboardOwner 归属过滤。
    /// </summary>
    public sealed class RemoteClipboardMonitor
    {
        /// <summary>剪贴板内容被外部（非本应用）更新</summary>
        public event Action? ExternalClipboardUpdated;

        private HwndSource? _source;
        private IntPtr _hwnd;

        public void Start(Window owner)
        {
            if (_source != null) return;
            var helper = new WindowInteropHelper(owner);
            _hwnd = helper.EnsureHandle();
            _source = HwndSource.FromHwnd(_hwnd);
            _source.AddHook(WndProc);
            // WM_CLIPBOARDUPDATE 只投递给已注册窗口，漏注册则监听永不生效
            AddClipboardFormatListener(_hwnd);
        }

        public void Stop()
        {
            if (_hwnd != IntPtr.Zero) RemoveClipboardFormatListener(_hwnd);
            _source?.RemoveHook(WndProc);
            _source = null;
            _hwnd = IntPtr.Zero;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CLIPBOARDUPDATE && IsExternalOwner())
            {
                ExternalClipboardUpdated?.Invoke();
            }
            return IntPtr.Zero;
        }

        /// <summary>当前剪贴板所有者是否不是本进程的窗口（拿不到所有者时保守视为外部）</summary>
        private static bool IsExternalOwner()
        {
            var owner = GetClipboardOwner();
            if (owner == IntPtr.Zero) return true;
            GetWindowThreadProcessId(owner, out uint pid);
            return pid != (uint)Environment.ProcessId;
        }

        private const int WM_CLIPBOARDUPDATE = 0x031D;

        [DllImport("user32.dll")]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardOwner();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }
}
