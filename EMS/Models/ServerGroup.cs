using SfeWork.EntityFrameworkCore;

namespace EMS.Models
{
    /// <summary>
    /// 服务器分组表
    /// </summary>
    public class ServerGroup : DataBase
    {
        /// <summary>
        /// 群组名称
        /// </summary>
        public string Name { get; set; } = null!;
        /// <summary>
        /// 组内服务器默认登陆账号
        /// </summary>
        public string DefaultAccount { get; set; } = "";
        /// <summary>
        /// 组内服务器默认登陆密码
        /// </summary>
        public string DefaultPassword { get; set; } = "";
        /// <summary>
        /// 组内服务器数据
        /// </summary>
        public ICollection<Server> Servers { get; set; } = [];
    }
}
