namespace EMS.Common
{
    /// <summary>
    /// 字节大小与速率的显示格式化，替代原先散落在多个文件中的重复实现
    /// </summary>
    public static class ByteFormat
    {
        /// <summary>
        /// 格式化为 B/KB/MB/GB/TB
        /// </summary>
        public static string Size(long bytes)
        {
            if (bytes < 0) return "0 B";
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
        }

        /// <summary>
        /// 格式化为每秒传输速率，如 "1.2 MB/s"
        /// </summary>
        public static string Speed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return "0 B/s";
            return $"{Size((long)bytesPerSecond)}/s";
        }
    }
}
