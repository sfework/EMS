using System.Text;

namespace EMS.Services.Connections
{
    /// <summary>
    /// 远端输出字节流的文本解码器：优先按 UTF-8 严格解码（增量处理跨块多字节字符），
    /// 遇到非法 UTF-8 序列（典型如 Windows cmd 的 GBK 输出）自动切换 GBK 并持续保持。
    /// 仅用于交互 Shell 数据流，非线程安全，由连接层单线程调用。
    /// </summary>
    public class RemoteTextDecoder
    {
        private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        private static readonly object ProviderLock = new();
        private static bool _providerRegistered;

        private readonly Decoder _utf8 = StrictUtf8.GetDecoder();
        private readonly List<byte> _gbkPending = [];
        private Encoding? _gbk;
        private bool _isGbk;

        public RemoteTextDecoder()
        {
            // CodePages 提供程序只需注册一次（.NET 内置编码表不含 GBK 936）
            lock (ProviderLock)
            {
                if (!_providerRegistered)
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    _providerRegistered = true;
                }
            }
        }

        public string Decode(byte[] data, int offset, int count)
        {
            if (count == 0) return string.Empty;

            if (_isGbk)
            {
                return DecodeGbk(data, offset, count);
            }

            try
            {
                var charCount = _utf8.GetCharCount(data, offset, count);
                var chars = new char[charCount];
                _utf8.GetChars(data, offset, count, chars, 0);
                return new string(chars);
            }
            catch (DecoderFallbackException)
            {
                // 非法 UTF-8：判定为 GBK 会话，本块整体按 GBK 解码
                _isGbk = true;
                _utf8.Reset();
                _gbkPending.Clear();
                return DecodeGbk(data, offset, count);
            }
        }

        private string DecodeGbk(byte[] data, int offset, int count)
        {
            _gbk ??= Encoding.GetEncoding(936);

            // 拼接上一轮残留的半个多字节字符
            var bytes = new byte[_gbkPending.Count + count];
            _gbkPending.CopyTo(bytes);
            Array.Copy(data, offset, bytes, _gbkPending.Count, count);
            _gbkPending.Clear();

            // 末尾若是 GBK 首字节（0x81-0xFE），留待下一块配对，避免切碎汉字
            if (bytes.Length > 0 && bytes[^1] >= 0x81 && bytes[^1] <= 0xFE)
            {
                _gbkPending.Add(bytes[^1]);
                var usable = bytes.Length - 1;
                return usable > 0 ? _gbk.GetString(bytes, 0, usable) : string.Empty;
            }

            return _gbk.GetString(bytes);
        }

        /// <summary>连接重置时恢复初始状态（重连后服务器可能换编码）</summary>
        public void Reset()
        {
            _utf8.Reset();
            _gbkPending.Clear();
            _isGbk = false;
        }
    }
}
