using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace EMS.Dialogs
{
    /// <summary>
    /// 单行名称输入对话框（重命名/新建文件夹共用）
    /// </summary>
    public partial class NameInputDialog : Window
    {
        public string InputName => TxtName.Text.Trim();

        public NameInputDialog(string title, string prompt, string initialName, bool selectAll)
        {
            Title = title;
            InitializeComponent();

            TxtPrompt.Text = prompt;
            TxtName.Text = initialName;

            TxtName.Focus();
            if (selectAll)
            {
                TxtName.SelectAll();
            }
            else
            {
                // 默认只选中主名部分（不含扩展名），符合资源管理器重命名习惯
                var dot = initialName.LastIndexOf('.');
                TxtName.SelectionStart = 0;
                TxtName.SelectionLength = dot > 0 ? dot : initialName.Length;
            }
        }

        private void TxtName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) BtnOk_Click(sender, e);
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtName.Text))
            {
                MessageBox.Show("名称不能为空", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtName.Focus();
                return;
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
