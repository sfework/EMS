using EMS.Services.Sftp;

namespace EMS.Common
{
    /// <summary>
    /// 远端路径与界面显示路径的双向转换（纯逻辑，便于单元测试）。
    /// 约定：Windows 服务器的盘符目录在 SFTP 路径中形如 /D:/Projects，
    /// 界面显示为 D:\Projects；Linux 路径（/home/...）原样显示。
    /// </summary>
    public static class RemotePathFormat
    {
        /// <summary>远端路径 → 显示路径</summary>
        public static string ToDisplayPath(string remote)
        {
            if (SftpBrowserService.IsDriveRoot(remote)) return remote.Trim('/')[0] + @":\";
            if (remote.Length > 2 && remote[0] == '/' && remote[2] == ':' && char.IsLetter(remote[1]))
            {
                // /D:/Projects -> D:\Projects
                var rest = remote.Substring(3).Replace('/', '\\');
                return remote.Substring(1, 2) + @"\" + rest.TrimStart('\\');
            }
            return remote;
        }

        /// <summary>显示路径 → 远端路径（兼容 D:\、D:/、/D: 三种写法）</summary>
        public static string ToRemotePath(string display)
        {
            var path = display.Replace('\\', '/');
            if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            {
                if (path.Length == 2) path += "/";
                else if (path[2] != '/') path = path.Insert(2, "/");
                return "/" + path;
            }
            return path;
        }
    }
}
