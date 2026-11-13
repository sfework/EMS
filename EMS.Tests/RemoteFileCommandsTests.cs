using EMS.Common;

namespace EMS.Tests
{
    /// <summary>
    /// 远程→远程 复制命令构造测试：Windows 必须走 PowerShell 方言（此前误用 cp 导致
    /// "cp 不是内部或外部命令"），Linux 走 POSIX
    /// </summary>
    public class RemoteFileCommandsTests
    {
        [Theory]
        [InlineData("/C:/Users/admin/app", @"C:\Users\admin\app")]
        [InlineData("/C:/", @"C:\")]
        public void ToNativePath_Windows转换盘符路径(string remote, string expected)
            => Assert.Equal(expected, RemoteFileCommands.ToNativePath(remote, isWindows: true));

        [Fact]
        public void ToNativePath_Linux保持原样()
            => Assert.Equal("/home/user", RemoteFileCommands.ToNativePath("/home/user", isWindows: false));

        [Fact]
        public void Windows_文件复制用CopyItem_不含cp()
        {
            var cmd = RemoteFileCommands.BuildCopy(
                isDirectory: false, targetExists: false,
                "/C:/a.txt", "/C:/b.txt", isWindows: true);

            Assert.Contains("Copy-Item", cmd);
            Assert.DoesNotContain("cp ", cmd);
            Assert.Contains("'C:\\a.txt'", cmd);
            Assert.Contains("'C:\\b.txt'", cmd);
        }

        [Fact]
        public void Windows_目录新建目标整体复制()
        {
            var cmd = RemoteFileCommands.BuildCopy(
                isDirectory: true, targetExists: false,
                "/C:/src", "/C:/dst", isWindows: true);

            Assert.Contains("Copy-Item -LiteralPath", cmd);
            Assert.Contains("-Recurse", cmd);
        }

        [Fact]
        public void Windows_目录合并复制展开子项()
        {
            var cmd = RemoteFileCommands.BuildCopy(
                isDirectory: true, targetExists: true,
                "/C:/src", "/C:/dst", isWindows: true);

            // PowerShell 直接 Copy 目录会嵌套一层，合并必须用 \* 展开
            Assert.Contains(@"'C:\src\*'", cmd);
            Assert.Contains("-Recurse", cmd);
        }

        [Fact]
        public void Windows_单引号路径转义为双单引号()
        {
            var cmd = RemoteFileCommands.BuildCopy(
                isDirectory: false, targetExists: false,
                "/C:/it's.txt", "/C:/x.txt", isWindows: true);

            Assert.Contains(@"'C:\it''s.txt'", cmd);
        }

        [Fact]
        public void Linux_保持POSIX命令()
        {
            var copy = RemoteFileCommands.BuildCopy(
                isDirectory: false, targetExists: false,
                "/home/a.txt", "/home/b.txt", isWindows: false);
            Assert.StartsWith("cp -a", copy);

            var merge = RemoteFileCommands.BuildCopy(
                isDirectory: true, targetExists: true,
                "/home/src", "/home/dst", isWindows: false);
            Assert.Contains("cp -a -- '/home/src'/. '/home/dst'/", merge);
        }

        [Theory]
        // 自身与子孙：拒绝
        [InlineData("/C:/src", "/C:/src", true, true)]
        [InlineData("/C:/src", "/C:/src/sub", true, true)]
        [InlineData("/c:/SRC", "/C:/src/x", true, true)]   // Windows 忽略大小写
        [InlineData("/home/src", "/home/src/x", false, true)]
        [InlineData("/", "/etc", false, true)]             // 根包含一切
        // 非子孙：放行
        [InlineData("/C:/src", "/C:/dst", true, false)]
        [InlineData("/C:/src", "/C:/src2/x", true, false)] // 前缀相似但非路径段
        [InlineData("/home/src", "/home/dst", false, false)]
        public void IsSelfOrDescendant_目录粘贴环检测(string src, string dst, bool isWindows, bool expected)
            => Assert.Equal(expected, RemoteFileCommands.IsSelfOrDescendant(src, dst, isWindows));
    }
}
