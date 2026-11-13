using EMS.Common;
using EMS.Services.Sftp;

namespace EMS.Tests
{
    /// <summary>
    /// 文件管理页路径逻辑单元测试：显示路径与远端路径双向转换、上级目录回退
    /// </summary>
    public class RemotePathFormatTests
    {
        [Theory]
        // Windows 盘符路径
        [InlineData("/D:/Projects", @"D:\Projects")]
        [InlineData("/D:/", @"D:\")]
        [InlineData("/D:", @"D:\")]
        // Linux 路径原样
        [InlineData("/home/user/.bashrc", "/home/user/.bashrc")]
        [InlineData("/", "/")]
        public void ToDisplayPath_按平台转换(string remote, string expected)
            => Assert.Equal(expected, RemotePathFormat.ToDisplayPath(remote));

        [Theory]
        // 三种 Windows 写法都归一为 /D:/Projects
        [InlineData(@"D:\Projects", "/D:/Projects")]
        [InlineData("D:/Projects", "/D:/Projects")]
        [InlineData("/D:/Projects", "/D:/Projects")]
        [InlineData(@"D:\", "/D:/")]
        [InlineData("D:", "/D:/")]
        // Linux 路径原样
        [InlineData("/home/user", "/home/user")]
        [InlineData("/", "/")]
        public void ToRemotePath_兼容多种写法(string display, string expected)
            => Assert.Equal(expected, RemotePathFormat.ToRemotePath(display));

        [Fact]
        public void 往返转换保持稳定()
        {
            foreach (var remote in new[] { "/D:/Projects", "/home/user", "/" })
            {
                var display = RemotePathFormat.ToDisplayPath(remote);
                var back = RemotePathFormat.ToRemotePath(display);
                Assert.Equal(remote.TrimEnd('/'), back.TrimEnd('/'));
            }
        }

        [Theory]
        [InlineData("/D:", true)]
        [InlineData("/D:/", true)]
        [InlineData("/c:/", true)]
        [InlineData("/D:/Projects", false)]
        [InlineData("/home/user", false)]
        [InlineData("/", false)]
        public void IsDriveRoot_识别盘符根(string path, bool expected)
            => Assert.Equal(expected, SftpBrowserService.IsDriveRoot(path));

        [Theory]
        [InlineData("/D:/Projects/src", "/D:/Projects")]
        [InlineData("/D:/Projects", "/D:")]
        // 盘符根回退到盘符列表根
        [InlineData("/D:", "/")]
        [InlineData("/D:/", "/")]
        // Linux：/home/user -> /home；/home -> null（根不再上）
        [InlineData("/home/user", "/home")]
        [InlineData("/home", null)]
        [InlineData("/", null)]
        public void GetParent_上级目录(string path, string? expected)
            => Assert.Equal(expected, SftpBrowserService.GetParent(path));
    }
}
