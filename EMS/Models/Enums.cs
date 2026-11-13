namespace EMS.Models
{
    public class Enums
    {
        /// <summary>
        /// 连接类型
        /// </summary>
        public enum ConnectionType
        {
            /// <summary>
            /// Windows远程桌面
            /// </summary>
            RDP = 1,
            /// <summary>
            /// Linux SSH
            /// </summary>
            SSH
        }
        /// <summary>
        /// 服务器状态
        /// </summary>
        public enum ServerStatus
        {
            未连接 = 1,
            连接中,
            已连接,
            异常
        }
    }
}
