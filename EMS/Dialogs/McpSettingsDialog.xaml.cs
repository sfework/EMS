using System.Windows;
using EMS.Services.Mcp;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace EMS.Dialogs
{
    /// <summary>
    /// MCP 服务端口设置：0 表示停用，修改后立即生效（重启监听）
    /// </summary>
    public partial class McpSettingsDialog : Window
    {
        private readonly McpServerManager _mcpManager;

        /// <summary>确认后的端口（0 表示停用）</summary>
        public int ResultPort { get; private set; }

        public McpSettingsDialog(McpServerManager mcpManager)
        {
            InitializeComponent();
            _mcpManager = mcpManager;

            TxtPort.Text = (_mcpManager.Port ?? 0).ToString();
            StatusText.Text = _mcpManager.IsRunning
                ? $"当前状态：运行中（http://localhost:{_mcpManager.Port}/mcp）"
                : "当前状态：未启动";
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtPort.Text.Trim(), out int port) || port < 0 || port > 65535)
            {
                MessageBox.Show("请输入有效端口（0 表示停用，或 1-65535）", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultPort = port;
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
