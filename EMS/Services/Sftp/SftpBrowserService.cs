using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace EMS.Services.Sftp
{
    /// <summary>
    /// SFTP 目录浏览/属性/删除/权限操作，无 UI 依赖，可被终端控件、
    /// 文件管理窗口以及后续 MCP 工具复用。
    /// </summary>
    public class SftpBrowserService
    {
        private readonly SftpClient _client;

        public SftpBrowserService(SftpClient client)
        {
            _client = client;
        }

        /// <summary>
        /// 列出目录内容（过滤 . 与 ..，目录在前按名称排序）
        /// </summary>
        public List<RemoteFileEntry> ListDirectory(string path)
        {
            var result = new List<RemoteFileEntry>();
            foreach (var item in _client.ListDirectory(path))
            {
                if (item.Name == "." || item.Name == "..") continue;

                result.Add(new RemoteFileEntry
                {
                    Name = item.Name,
                    IsDirectory = item.IsDirectory,
                    FullPath = item.FullName,
                    LastWriteTime = item.LastWriteTime,
                    Size = item.Length,
                    Permissions = (item.IsDirectory ? "d" : "-") + FormatPermissions(item.Attributes),
                });
            }

            return result.OrderByDescending(f => f.IsDirectory)
                         .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                         .ToList();
        }

        /// <summary>
                /// 读取八进制权限（如 "755"），供属性对话框回显
                /// </summary>
        public string GetOctalPermissions(string path)
        {
            var a = _client.GetAttributes(path);
            static int Value(bool r, bool w, bool x) => (r ? 4 : 0) + (w ? 2 : 0) + (x ? 1 : 0);
            return $"{Value(a.OwnerCanRead, a.OwnerCanWrite, a.OwnerCanExecute)}" +
                   $"{Value(a.GroupCanRead, a.GroupCanWrite, a.GroupCanExecute)}" +
                   $"{Value(a.OthersCanRead, a.OthersCanWrite, a.OthersCanExecute)}";
        }

        public void Delete(string path)
        {
            var entry = _client.Get(path);
            if (entry.IsDirectory)
            {
                _client.DeleteDirectory(path);
            }
            else
            {
                _client.DeleteFile(path);
            }
        }

        /// <summary>
        /// 递归删除（文件/空目录/非空目录均可）：纯 SFTP 协议自底向上，
        /// 中文路径安全，不依赖远端 shell
        /// </summary>
        public void DeleteRecursive(string path)
        {
            var entry = _client.Get(path);
            if (!entry.IsDirectory)
            {
                _client.DeleteFile(path);
                return;
            }

            foreach (var child in _client.ListDirectory(path))
            {
                if (child.Name == "." || child.Name == "..") continue;
                DeleteRecursive(child.FullName);
            }
            _client.DeleteDirectory(path);
        }

        public bool Exists(string path) => _client.Exists(path);

        /// <summary>
        /// 重命名/同服务器移动（SFTP rename，对目录同样有效）
        /// </summary>
        public void Rename(string oldPath, string newPath) => _client.RenameFile(oldPath, newPath);

        public void CreateDirectory(string path)
        {
            if (!_client.Exists(path))
            {
                _client.CreateDirectory(path);
            }
        }

        /// <summary>
        /// 逐级创建远端目录
        /// </summary>
        public void CreateDirectoryRecursive(string path)
        {
            if (_client.Exists(path)) return;

            var parent = GetParent(path);
            if (!string.IsNullOrEmpty(parent) && parent != "/" && !_client.Exists(parent))
            {
                CreateDirectoryRecursive(parent);
            }

            try
            {
                _client.CreateDirectory(path);
            }
            catch (Renci.SshNet.Common.SshException)
            {
                // 目录可能已存在，忽略
            }
        }

        /// <summary>
        /// 取上级目录；Windows 盘符根（/D:/）回退到盘符列表根 "/"
        /// </summary>
        public static string? GetParent(string path)
        {
            // Windows 盘符根（/D:/）回退到盘符列表根 "/"
            if (IsDriveRoot(path)) return "/";

            var parts = path.Split(['/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1) return null;
            return "/" + string.Join("/", parts.Take(parts.Length - 1));
        }

        /// <summary>
        /// 是否为 Windows 盘符根路径（如 /D:/ 或 /D:）
        /// </summary>
        public static bool IsDriveRoot(string path)
        {
            var trimmed = path.Trim('/');
            return trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':';
        }

        /// <summary>
        /// 递归收集目录下全部文件的全路径
        /// </summary>
        public List<string> GetFilesRecursive(string remotePath)
        {
            var files = new List<string>();
            foreach (var item in _client.ListDirectory(remotePath))
            {
                if (item.Name == "." || item.Name == "..") continue;

                if (item.IsDirectory)
                {
                    files.AddRange(GetFilesRecursive(item.FullName));
                }
                else
                {
                    files.Add(item.FullName);
                }
            }
            return files;
        }

        private static string FormatPermissions(SftpFileAttributes attributes)
        {
            static string Tri(bool allowed, char letter) => allowed ? letter.ToString() : "-";
            return Tri(attributes.OwnerCanRead, 'r') + Tri(attributes.OwnerCanWrite, 'w') + Tri(attributes.OwnerCanExecute, 'x')
                 + Tri(attributes.GroupCanRead, 'r') + Tri(attributes.GroupCanWrite, 'w') + Tri(attributes.GroupCanExecute, 'x')
                 + Tri(attributes.OthersCanRead, 'r') + Tri(attributes.OthersCanWrite, 'w') + Tri(attributes.OthersCanExecute, 'x');
        }
    }
}
