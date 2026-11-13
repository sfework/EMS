using System.Text;

namespace EMS.Services.Connections
{
    /// <summary>
    /// 远端文本修复：SSH.NET 按 Latin1 无损传输字节后，
    /// 把字符串还原为原始字节再解码——UTF-8 优先，非法序列回退 GBK。
    /// 解决 Windows cmd（GBK）服务器上 run_command 输出中文乱码问题。
    /// </summary>
    public static class RemoteText
    {
        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private static Encoding? _gbk;

        static RemoteText()
        {
            // .NET 内置编码表不含 GBK 936，需注册 CodePages 提供程序
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        /// <summary>
        /// 命令输入方向：把文本编码为 UTF-8 字节后以 Latin1 载体字符串传出
        /// （SSH.NET 按 Latin1 转字节是 1:1 无损的，远端即收到 UTF-8 字节），
        /// 修复中文路径在 cp/Remove-Item 等命令中变成 '?' 导致执行失败
        /// </summary>
        public static string EncodeForTransfer(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return Encoding.Latin1.GetString(StrictUtf8.GetBytes(text));
        }

        public static string Fix(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;

            var bytes = Encoding.Latin1.GetBytes(raw);
            try
            {
                return StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                _gbk ??= Encoding.GetEncoding(936);
                return _gbk.GetString(bytes);
            }
        }
    }
}
