using Microsoft.Win32;
// 项目同时启用 WinForms，显式别名到 WPF 版本避免二义性
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace EMS.Services
{
    /// <summary>
    /// 消息框类型
    /// </summary>
    public enum MessageKind { Info, Warning, Error }

    /// <summary>
    /// 对话框服务：ViewModel 通过它弹消息/确认/文件选择，不直接依赖 WPF MessageBox
    /// </summary>
    public interface IDialogService
    {
        Task ShowMessageAsync(string message, string title = "提示", MessageKind kind = MessageKind.Info);

        /// <summary>
        /// 确认框，返回 true 表示用户点击"是"
        /// </summary>
        Task<bool> ConfirmAsync(string message, string title = "确认");

        /// <summary>
        /// 选择本地文件，取消返回 null
        /// </summary>
        string? PickFile(string? filter = null, string? title = null);

        /// <summary>
        /// 选择保存路径，取消返回 null
        /// </summary>
        string? SaveFile(string? fileName = null, string? filter = null, string? title = null);

        /// <summary>
        /// 选择本地文件夹，取消返回 null
        /// </summary>
        string? PickFolder(string? title = null);
    }

    /// <summary>
    /// 基于 WPF 的实现，注册为单例
    /// </summary>
    public class DialogService : IDialogService
    {
        public Task ShowMessageAsync(string message, string title = "提示", MessageKind kind = MessageKind.Info)
        {
            var image = kind switch
            {
                MessageKind.Error => MessageBoxImage.Error,
                MessageKind.Warning => MessageBoxImage.Warning,
                _ => MessageBoxImage.Information,
            };
            System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, image);
            return Task.CompletedTask;
        }

        public Task<bool> ConfirmAsync(string message, string title = "确认")
        {
            var result = System.Windows.MessageBox.Show(message, title,
                System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Question);
            return Task.FromResult(result == System.Windows.MessageBoxResult.Yes);
        }

        public string? PickFile(string? filter = null, string? title = null)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = filter ?? "所有文件 (*.*)|*.*",
                Title = title ?? "选择文件",
                Multiselect = false,
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public string? SaveFile(string? fileName = null, string? filter = null, string? title = null)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                FileName = fileName ?? string.Empty,
                Filter = filter ?? "所有文件 (*.*)|*.*",
                Title = title ?? "保存文件",
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        public string? PickFolder(string? title = null)
        {
            // .NET 8+ 提供的文件夹选择对话框
            var dialog = new OpenFolderDialog
            {
                Title = title ?? "选择文件夹",
            };
            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }
    }
}
