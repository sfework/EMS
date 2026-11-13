using SfeWork.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;

namespace EMS.Models
{
    /// <summary>
    /// 服务器表
    /// </summary>
    public class Server : DataBase
    {
        /// <summary>
        /// 所属服务器分组ID
        /// </summary>
        public string GroupID { get; set; } = null!;
        /// <summary>
        /// 服务器名称
        /// </summary>
        public string Name { get; set; } = null!;
        /// <summary>
        /// 连接类型（RDP/SSH），决定新增时的默认端口（RDP=3389，SSH=22）
        /// </summary>
        public Enums.ConnectionType ConnectionType { get; set; }
        /// <summary>
        /// 服务器IP
        /// </summary>
        public string IP { get; set; } = null!;
        /// <summary>
        /// 服务器端口
        /// </summary>
        public int Port { get; set; } = 3389;
        /// <summary>
        /// 服务登陆账号
        /// </summary>
        public string Account { get; set; } = "";
        /// <summary>
        /// 是否继承关联的服务器分组设置的默认登陆账号，为true时Account的值从关联分组中读取DefaultAccount
        /// </summary>
        public bool InheritAccount { get; set; } = true;
        /// <summary>
        /// 服务器登录密码
        /// </summary>
        public string Password { get; set; } = "";
        /// <summary>
        /// RDP 服务器是否同时开启了 SSH 服务（Windows 开启 OpenSSH Server 场景）。
        /// 为 true 时右侧页签增加"命令终端"，可用 SshPort 建立 SSH 连接；账号密码复用主凭据
        /// </summary>
        public bool SupportsSsh { get; set; }
        /// <summary>
        /// SSH 端口（仅 SupportsSsh=true 有效），默认 22
        /// </summary>
        public int SshPort { get; set; } = 22;
        /// <summary>
        /// 是否继承关联的服务器分组设置的默认登陆密码，为true时Password的值从关联分组中读取DefaultPassword
        /// </summary>
        public bool InheritPassword { get; set; } = true;

        /// <summary>
        /// 关联的服务器分组
        /// </summary>
        [ForeignKey(nameof(GroupID))]
        public ServerGroup Group { get; set; } = null!;
    }
}
