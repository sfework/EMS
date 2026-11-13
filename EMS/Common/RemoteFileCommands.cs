namespace EMS.Common
{
    /// <summary>
    /// 远程→远程 复制命令的构造（纯逻辑，便于单元测试）。
    /// Linux 走 POSIX（cp -a）；Windows OpenSSH 默认 shell 为 PowerShell，走 Copy-Item。
    /// 删除/重命名/新建走 SFTP 协议（SftpBrowserService），剪切已取消，故此处只需复制。
    /// </summary>
    public static class RemoteFileCommands
    {
        /// <summary>
        /// 远端统一路径（/C:/Users/x 或 /home/user）→ 当前 OS 的原生路径写法
        /// </summary>
        public static string ToNativePath(string remotePath, bool isWindows)
        {
            if (!isWindows) return remotePath;
            // /C:/Users/x -> C:\Users\x
            var p = remotePath.Replace('/', '\\').TrimStart('\\');
            return p.Length == 0 ? "\\" : p;
        }

        /// <summary>
        /// 构造复制命令。targetExists 仅影响目录（目标已存在 = 合并内容，资源管理器语义）。
        /// </summary>
        public static string BuildCopy(bool isDirectory, bool targetExists,
            string srcRemote, string dstRemote, bool isWindows)
        {
            var src = ToNativePath(srcRemote, isWindows);
            var dst = ToNativePath(dstRemote, isWindows);

            if (isWindows)
            {
                var s = PsQuote(src);
                var d = PsQuote(dst);

                if (isDirectory)
                {
                    if (!targetExists)
                    {
                        // 目标不存在：整体复制为目录本身
                        return $"Copy-Item -LiteralPath {s} -Destination {d} -Recurse -Force";
                    }

                    // 目标已存在 = 合并子项（PowerShell 直接 Copy 目录会嵌套一层，须用 \* 展开）
                    return $"Copy-Item -Path {PsQuote(src.TrimEnd('\\') + "\\*")} -Destination {d} -Recurse -Force";
                }

                return $"Copy-Item -LiteralPath {s} -Destination {d} -Force";
            }
            else
            {
                var s = PosixQuote(src);
                var d = PosixQuote(dst);

                if (isDirectory && targetExists)
                {
                    // 目录冲突替换 = 合并内容
                    return $"cp -a -- {s}/. {d}/";
                }

                return $"cp -a -- {s} {d}";
            }
        }

        /// <summary>
        /// 目标路径是否为源路径自身或其子孙（禁止把目录粘贴到自身内部，资源管理器同样拒绝）。
        /// 统一按远端正斜杠路径比较，Windows 忽略大小写。
        /// </summary>
        public static bool IsSelfOrDescendant(string srcRemote, string dstRemote, bool isWindows)
        {
            var comparer = isWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var src = srcRemote.Replace('\\', '/').TrimEnd('/');
            if (src.Length == 0) src = "/"; // 根：任何粘贴目标都在其内
            var dst = dstRemote.Replace('\\', '/').TrimEnd('/');
            if (dst.Length == 0) dst = "/";
            if (src == "/") return true;
            return string.Equals(src, dst, comparer)
                || dst.StartsWith(src + "/", comparer);
        }

        /// <summary>PowerShell 单引号字符串转义（' 双写）</summary>
        private static string PsQuote(string path) => $"'{path.Replace("'", "''")}'";

        /// <summary>POSIX 单引号转义</summary>
        private static string PosixQuote(string path) => $"'{path.Replace("'", "'\\''")}'";
    }
}
