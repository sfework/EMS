using System.Windows;

namespace EMS.Dialogs
{
    /// <summary>
    /// 传输目标冲突询问（模仿资源管理器）：替换 / 跳过 / 取消，可应用到全部后续冲突
    /// </summary>
    public partial class ConflictDialog : Window
    {
        /// <summary>用户选择的冲突处理方式</summary>
        public enum Choice { Replace, Skip, Cancel }

        public string Message { get; }
        public Choice Result { get; private set; } = Choice.Cancel;
        public bool ApplyToAll => ChkApplyAll.IsChecked == true;

        public ConflictDialog(string message)
        {
            Message = message;
            InitializeComponent();
        }

        private void BtnReplace_Click(object sender, RoutedEventArgs e)
        {
            Result = Choice.Replace;
            DialogResult = true;
            Close();
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            Result = Choice.Skip;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Result = Choice.Cancel;
            DialogResult = false;
            Close();
        }
    }
}
