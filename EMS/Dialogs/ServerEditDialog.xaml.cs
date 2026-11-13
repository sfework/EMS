using EMS.Models;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace EMS.Dialogs
{
    public partial class ServerEditDialog : Window
    {
        public Server? ResultServer { get; private set; }
        private readonly string _groupId;
        // 连接类型切换时只联动未手工输入过的端口/账号默认值
        private bool _portEditedByUser;
        private bool _accountEditedByUser;
        private bool _suppressEditTracking;

        public ServerEditDialog(string groupId, Server? server = null)
        {
            InitializeComponent();

            _groupId = groupId;

            TxtPort.TextChanged += (s, e) => { if (!_suppressEditTracking) _portEditedByUser = true; };
            TxtAccount.TextChanged += (s, e) => { if (!_suppressEditTracking) _accountEditedByUser = true; };

            if (server != null)
            {
                Title = "编辑服务器";
                TxtServerName.Text = server.Name;
                TxtIP.Text = server.IP;
                TxtPort.Text = server.Port.ToString();
                TxtAccount.Text = server.Account;
                ChkInheritAccount.IsChecked = server.InheritAccount;
                TxtPassword.Password = server.Password;
                ChkInheritPassword.IsChecked = server.InheritPassword;

                // 已保存的端口/账号视为用户显式输入，连接类型切换不覆盖
                _portEditedByUser = true;
                _accountEditedByUser = true;

                ResultServer = new Server
                {
                    ID = server.ID,
                    GroupID = server.GroupID
                };

                ChkSupportsSsh.IsChecked = server.SupportsSsh;
                TxtSshPort.Text = server.SshPort.ToString();

                CmbConnectionType.SelectedIndex = server.ConnectionType == Enums.ConnectionType.RDP ? 0 : 1;
            }
            else
            {
                Title = "新增服务器";
                ChkInheritAccount.IsChecked = true;
                ChkInheritPassword.IsChecked = true;

                ResultServer = new Server
                {
                    ID = string.Empty,
                    GroupID = _groupId
                };

                // 默认 RDP，SelectionChanged 会带入默认端口/账号
                CmbConnectionType.SelectedIndex = 0;
            }

            UpdateAccountDisplay();
            UpdatePasswordDisplay();
        }

        /// <summary>
        /// 当前下拉框选中的连接类型
        /// </summary>
        private Enums.ConnectionType SelectedConnectionType =>
            CmbConnectionType.SelectedIndex == 0 ? Enums.ConnectionType.RDP : Enums.ConnectionType.SSH;

        private void CmbConnectionType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TxtPort == null) return; // InitializeComponent 阶段可能先触发

            var isRdp = SelectedConnectionType == Enums.ConnectionType.RDP;

            if (!_portEditedByUser)
            {
                _suppressEditTracking = true;
                TxtPort.Text = isRdp ? "3389" : "22";
                _suppressEditTracking = false;
            }

            if (!_accountEditedByUser && ChkInheritAccount.IsChecked != true)
            {
                _suppressEditTracking = true;
                TxtAccount.Text = isRdp ? "Administrator" : "root";
                _suppressEditTracking = false;
            }

            // "支持 SSH" 仅 RDP 场景有意义；非 RDP 时隐藏并取消勾选
            ChkSupportsSsh.Visibility = isRdp ? Visibility.Visible : Visibility.Collapsed;
            if (!isRdp) ChkSupportsSsh.IsChecked = false;
            UpdateSshPortDisplay();
        }

        private void ChkSupportsSsh_Checked(object sender, RoutedEventArgs e)
        {
            UpdateSshPortDisplay();
        }

        private void UpdateSshPortDisplay()
        {
            var show = ChkSupportsSsh.Visibility == Visibility.Visible && ChkSupportsSsh.IsChecked == true;
            LblSshPort.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TxtSshPort.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChkInheritAccount_Checked(object sender, RoutedEventArgs e)
        {
            UpdateAccountDisplay();
        }

        private void ChkInheritPassword_Checked(object sender, RoutedEventArgs e)
        {
            UpdatePasswordDisplay();
        }

        private void UpdateAccountDisplay()
        {
            if (ChkInheritAccount.IsChecked == true)
            {
                TxtAccount.IsEnabled = false;
                TxtAccount.Text = "";
            }
            else
            {
                TxtAccount.IsEnabled = true;
                if (!_accountEditedByUser)
                {
                    _suppressEditTracking = true;
                    TxtAccount.Text = SelectedConnectionType == Enums.ConnectionType.RDP ? "Administrator" : "root";
                    _suppressEditTracking = false;
                }
            }
        }

        private void UpdatePasswordDisplay()
        {
            if (ChkInheritPassword.IsChecked == true)
            {
                TxtPassword.IsEnabled = false;
                TxtPassword.Password = "";
            }
            else
            {
                TxtPassword.IsEnabled = true;
            }
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtServerName.Text))
            {
                MessageBox.Show("请输入服务器名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtServerName.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(TxtIP.Text))
            {
                MessageBox.Show("请输入服务器IP地址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtIP.Focus();
                return;
            }

            if (!int.TryParse(TxtPort.Text, out int port) || port <= 0 || port > 65535)
            {
                MessageBox.Show("请输入有效的端口号（1-65535）", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtPort.Focus();
                return;
            }

            var supportsSsh = ChkSupportsSsh.IsChecked == true;
            int sshPort = 22;
            if (supportsSsh && (!int.TryParse(TxtSshPort.Text, out sshPort) || sshPort <= 0 || sshPort > 65535))
            {
                MessageBox.Show("请输入有效的 SSH 端口号（1-65535）", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtSshPort.Focus();
                return;
            }

            if (ResultServer != null)
            {
                ResultServer.Name = TxtServerName.Text.Trim();
                ResultServer.ConnectionType = SelectedConnectionType;
                ResultServer.IP = TxtIP.Text.Trim();
                ResultServer.Port = port;
                ResultServer.SupportsSsh = supportsSsh;
                ResultServer.SshPort = supportsSsh ? sshPort : 22;
                ResultServer.Account = ChkInheritAccount.IsChecked == true ? "" : TxtAccount.Text.Trim();
                ResultServer.InheritAccount = ChkInheritAccount.IsChecked == true;
                ResultServer.Password = ChkInheritPassword.IsChecked == true ? "" : TxtPassword.Password;
                ResultServer.InheritPassword = ChkInheritPassword.IsChecked == true;
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
