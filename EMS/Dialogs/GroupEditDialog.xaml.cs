using EMS.Models;
using EMS.ViewModels;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace EMS.Dialogs
{
    /// <summary>
    /// GroupEditDialog.xaml 的交互逻辑
    /// </summary>
    public partial class GroupEditDialog : Window
    {
        public GroupViewModel? ResultGroupVm { get; private set; }

        /// <summary>
        /// group 为 null 表示新建，否则编辑该实体（对话框直接修改此实体）
        /// </summary>
        public GroupEditDialog(ServerGroup? group = null)
        {
            InitializeComponent();

            if (group != null)
            {
                Title = "编辑群组";
                TxtGroupName.Text = group.Name;
                TxtDefaultAccount.Text = group.DefaultAccount;
                TxtDefaultPassword.Password = group.DefaultPassword;

                ResultGroupVm = new GroupViewModel(group);
            }
            else
            {
                Title = "新建群组";

                var newGroup = new ServerGroup { ID = string.Empty };
                ResultGroupVm = new GroupViewModel(newGroup);
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtGroupName.Text))
            {
                MessageBox.Show("请输入群组名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtGroupName.Focus();
                return;
            }

            if (ResultGroupVm != null)
            {
                ResultGroupVm.Name = TxtGroupName.Text.Trim();
                ResultGroupVm.DefaultAccount = TxtDefaultAccount.Text.Trim();
                ResultGroupVm.DefaultPassword = TxtDefaultPassword.Password;
            }

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
