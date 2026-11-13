using SfeWork.EntityFrameworkCore;

namespace EMS.Models
{
    /// <summary>
    /// 配置表，只会生成一条数据
    /// </summary>
    public class AppConfig : DataBase
    {
        /// <summary>
        /// 窗口整体宽度
        /// </summary>
        public int Window_Width { get; set; } = 1024;
        /// <summary>
        /// 窗口整体高度
        /// </summary>
        public int Window_Height { get; set; } = 768;
        /// <summary>
        /// 工作区宽度
        /// </summary>
        public int Width { get; set; }
        /// <summary>
        /// 工作区高度
        /// </summary>
        public int Height { get; set; }
        /// <summary>
        /// 滑动分离器宽度（左侧）
        /// </summary>
        public int SplitterWidth { get; set; } = 200;
        /// <summary>
        /// MCP服务端监听端口，0表示不启动
        /// </summary>
        public int McpPort { get; set; } = 0;
    }
}
