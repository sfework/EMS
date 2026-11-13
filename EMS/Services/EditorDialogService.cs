using EMS.Dialogs;
using EMS.Models;

namespace EMS.Services
{
    /// <summary>
    /// 编辑对话框调度：ViewModel 通过它弹出组/服务器编辑窗口，不直接接触 Window 类型
    /// </summary>
    public interface IEditorDialogService
    {
        /// <summary>
        /// 编辑/新建群组。editing 为 null 表示新建；确认返回实体，取消返回 null
        /// </summary>
        ServerGroup? EditGroup(ServerGroup? editing);

        /// <summary>
        /// 编辑/新建服务器。editing 为 null 表示新建；确认返回分离的实体副本，取消返回 null
        /// </summary>
        Server? EditServer(string groupId, Server? editing);
    }

    /// <summary>
    /// 基于现有对话框窗口的实现（组合层服务）
    /// </summary>
    public class EditorDialogService : IEditorDialogService
    {
        public ServerGroup? EditGroup(ServerGroup? editing)
        {
            // 编辑传入被跟踪实体（对话框内直接改该实体）
            var dialog = new GroupEditDialog(editing)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            if (dialog.ShowDialog() == true)
            {
                return dialog.ResultGroupVm?.GetModel();
            }
            return null;
        }

        public Server? EditServer(string groupId, Server? editing)
        {
            var dialog = new ServerEditDialog(groupId, editing)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            if (dialog.ShowDialog() == true)
            {
                return dialog.ResultServer;
            }
            return null;
        }
    }
}
