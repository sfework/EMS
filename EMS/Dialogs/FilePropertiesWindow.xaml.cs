using System.Windows;
using CheckBox = System.Windows.Controls.CheckBox;

namespace EMS.Dialogs
{
    public partial class FilePropertiesWindow : Window
    {
        private readonly string _remotePath;
        private readonly bool _isDirectory;

        public string NewPermission { get; private set; } = string.Empty;

        public FilePropertiesWindow(string remotePath, bool isDirectory, string currentPermission)
        {
            InitializeComponent();
            _remotePath = remotePath;
            _isDirectory = isDirectory;

            FileNameText.Text = System.IO.Path.GetFileName(remotePath);
            FilePathText.Text = remotePath;
            FileTypeText.Text = isDirectory ? "类型：目录" : "类型：文件";

            // 解析八进制权限并设置复选框
            ParseAndSetPermissions(currentPermission);

            // 监听复选框变化，实时更新八进制显示
            SetupCheckBoxEvents();
            
            // 初始更新八进制显示
            UpdateOctalDisplay();
        }

        private void ParseAndSetPermissions(string permission)
        {
            if (permission.Length >= 3)
            {
                int owner = int.Parse(permission[permission.Length - 3].ToString());
                int group = int.Parse(permission[permission.Length - 2].ToString());
                int others = int.Parse(permission[permission.Length - 1].ToString());

                OwnerReadCheck.IsChecked = (owner & 4) != 0;
                OwnerWriteCheck.IsChecked = (owner & 2) != 0;
                OwnerExecuteCheck.IsChecked = (owner & 1) != 0;

                GroupReadCheck.IsChecked = (group & 4) != 0;
                GroupWriteCheck.IsChecked = (group & 2) != 0;
                GroupExecuteCheck.IsChecked = (group & 1) != 0;

                OthersReadCheck.IsChecked = (others & 4) != 0;
                OthersWriteCheck.IsChecked = (others & 2) != 0;
                OthersExecuteCheck.IsChecked = (others & 1) != 0;
            }
        }

        private void SetupCheckBoxEvents()
        {
            var allChecks = new CheckBox[] {
                OwnerReadCheck, OwnerWriteCheck, OwnerExecuteCheck,
                GroupReadCheck, GroupWriteCheck, GroupExecuteCheck,
                OthersReadCheck, OthersWriteCheck, OthersExecuteCheck
            };

            foreach (var check in allChecks)
            {
                check.Checked += (s, e) => UpdateOctalDisplay();
                check.Unchecked += (s, e) => UpdateOctalDisplay();
            }
        }

        private void UpdateOctalDisplay()
        {
            int owner = GetPermissionValue(OwnerReadCheck, OwnerWriteCheck, OwnerExecuteCheck);
            int group = GetPermissionValue(GroupReadCheck, GroupWriteCheck, GroupExecuteCheck);
            int others = GetPermissionValue(OthersReadCheck, OthersWriteCheck, OthersExecuteCheck);

            OctalText.Text = $"{owner}{group}{others}";
        }

        private int GetPermissionValue(CheckBox read, CheckBox write, CheckBox execute)
        {
            int value = 0;
            if (read.IsChecked == true) value += 4;
            if (write.IsChecked == true) value += 2;
            if (execute.IsChecked == true) value += 1;
            return value;
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            int owner = GetPermissionValue(OwnerReadCheck, OwnerWriteCheck, OwnerExecuteCheck);
            int group = GetPermissionValue(GroupReadCheck, GroupWriteCheck, GroupExecuteCheck);
            int others = GetPermissionValue(OthersReadCheck, OthersWriteCheck, OthersExecuteCheck);

            NewPermission = $"{owner}{group}{others}";
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}