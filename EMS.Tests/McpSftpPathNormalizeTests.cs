using EMS.Services.Mcp;

namespace EMS.Tests
{
    /// <summary>
    /// MCP 文件工具的 SFTP 路径规范化单元测试：
    /// Windows 盘符路径必须转为 /C:/x 形式（否则被服务端当相对路径拼到主目录后），
    /// POSIX 路径与已规范路径必须原样保留
    /// </summary>
    public class McpSftpPathNormalizeTests
    {
        [Theory]
        // Windows 盘符三种写法 → 统一 /C:/ 形式
        [InlineData(@"C:\Users\a\fttest", "/C:/Users/a/fttest")]
        [InlineData("C:/Users/a", "/C:/Users/a")]
        [InlineData("C:\\", "/C:/")]
        [InlineData("D:/Projects", "/D:/Projects")]
        // 已规范的 SFTP 路径原样
        [InlineData("/C:/Users/a", "/C:/Users/a")]
        // POSIX 路径原样（含反斜杠也不得被改写——文件名可能合法含 \）
        [InlineData("/tmp/fttest/up", "/tmp/fttest/up")]
        [InlineData("/", "/")]
        // 非盘符的相对/主目录形式原样
        [InlineData("ems-integ.ps1", "ems-integ.ps1")]
        [InlineData("~/.ssh", "~/.ssh")]
        // 前后空白剥离
        [InlineData("  /tmp/x  ", "/tmp/x")]
        public void NormalizeSftpPath_盘符转SFTP绝对路径(string input, string expected)
            => Assert.Equal(expected, EmsMcpTools.NormalizeSftpPath(input));

        [Fact]
        public void 盘符根仅字母冒号补斜杠()
        {
            // "C:" 裸盘符（长度2）也识别为盘符形式
            Assert.Equal("/C:/", EmsMcpTools.NormalizeSftpPath("C:"));
        }

        [Fact]
        public void 类Unix路径含冒号不误判盘符()
        {
            // 第二字符非 ':' 或第三字符非分隔符的都不算盘符
            Assert.Equal("/a:b/c", EmsMcpTools.NormalizeSftpPath("/a:b/c"));
            Assert.Equal("C:temp", EmsMcpTools.NormalizeSftpPath("C:temp"));
        }
    }
}
