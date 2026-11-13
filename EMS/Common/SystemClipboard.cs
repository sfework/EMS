using System.Runtime.InteropServices;
using System.Text;

namespace EMS.Common
{
    /// <summary>
    /// 直接经 Win32 读取系统剪贴板的文件列表（CF_HDROP）。
    /// 绕开 WPF Clipboard.GetDataObject 的进程内缓存——外部进程（资源管理器）
    /// 延迟渲染时 WPF 会读到过期数据，导致"第二次复制后粘贴的仍是第一次的文件"。
    /// OpenClipboard 会强制所有者实时渲染，拿到的永远是最新内容。
    /// </summary>
    public static class SystemClipboard
    {
        private const uint CF_HDROP = 15;

        /// <summary>当前剪贴板中的文件路径列表；无文件或剪贴板被占用时返回空数组</summary>
        public static string[] GetFileDrop()
        {
            if (!OpenClipboard(IntPtr.Zero)) return [];
            try
            {
                if (!IsClipboardFormatAvailable(CF_HDROP)) return [];

                // 句柄归剪贴板所有，禁止释放
                var hDrop = GetClipboardData(CF_HDROP);
                if (hDrop == IntPtr.Zero) return [];

                var count = DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
                var files = new string[count];
                for (uint i = 0; i < count; i++)
                {
                    var len = DragQueryFileW(hDrop, i, null, 0);
                    var sb = new StringBuilder((int)len + 1);
                    DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity);
                    files[i] = sb.ToString();
                }
                return files;
            }
            finally
            {
                CloseClipboard();
            }
        }

        [DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll")]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);
    }
}
