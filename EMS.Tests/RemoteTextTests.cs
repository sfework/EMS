using System.Text;
using EMS.Services.Connections;

namespace EMS.Tests
{
    /// <summary>
    /// 命令传输编码测试：非 ASCII 路径必须以 UTF-8 字节到达远端
    /// （此前按 Latin1 直传，中文变 '?' 导致 cp/Remove-Item 对中文目录失败）
    /// </summary>
    public class RemoteTextTests
    {
        [Fact]
        public void ASCII命令保持不变()
        {
            const string cmd = "cp -a -- '/home/a.txt' '/home/b.txt'";
            Assert.Equal(cmd, RemoteText.EncodeForTransfer(cmd));
        }

        [Fact]
        public void 中文路径编码为UTF8字节载体()
        {
            var encoded = RemoteText.EncodeForTransfer("Remove-Item '壁纸'");

            // Latin1 载体还原出的字节必须是合法 UTF-8 且能解回原文
            var bytes = Encoding.Latin1.GetBytes(encoded);
            Assert.Equal("Remove-Item '壁纸'", Encoding.UTF8.GetString(bytes));
        }

        [Fact]
        public void 空与null安全()
        {
            Assert.Equal("", RemoteText.EncodeForTransfer(""));
            Assert.Null(RemoteText.EncodeForTransfer(null!));
        }
    }
}
