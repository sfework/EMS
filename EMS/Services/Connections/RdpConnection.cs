using AxMSTSCLib;
using EMS.Models;
using System.Windows;
using System.Windows.Forms.Integration;

namespace EMS.Services.Connections
{
    /// <summary>
    /// RDP 连接：内部持有 ActiveX 控件（组合而非继承），服务层不再"是"一个控件。
    /// ActiveX 要求 STA/UI 线程操作，Connect 内部自行编组。
    /// </summary>
    public class RdpConnection : IServerConnection
    {
        private readonly AxMsRdpClient9NotSafeForScripting _client;
        private readonly WindowsFormsHost _host;
        private readonly System.Windows.Forms.Label _statusLabel;
        private readonly System.Windows.Forms.Panel _panel;

        private Enums.ServerStatus _status = Enums.ServerStatus.未连接;
        private string? _errorMessage;

        public string Name { get; }
        public string IP { get; }
        public Enums.ConnectionType ConnectionType => Enums.ConnectionType.RDP;

        public Enums.ServerStatus Status
        {
            get => _status;
            private set
            {
                _status = value;
                UpdateStatusLabel();
                StatusChanged?.Invoke(value);
            }
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                _errorMessage = value;
                UpdateStatusLabel();
            }
        }

        public event Action<Enums.ServerStatus>? StatusChanged;

        public RdpConnection(string name, string ip)
        {
            Name = name;
            IP = ip;

            // 初始化 ActiveX 控件
            _client = new AxMsRdpClient9NotSafeForScripting();
            ((System.ComponentModel.ISupportInitialize)_client).BeginInit();
            _client.Enabled = false;
            _client.Dock = System.Windows.Forms.DockStyle.Fill;
            _client.Location = new System.Drawing.Point(0, 0);

            _client.OnConnecting += Client_OnConnecting;
            _client.OnLoginComplete += Client_OnLoginComplete;
            _client.OnDisconnected += Client_OnDisconnected;
            _client.OnAuthenticationWarningDisplayed += Client_OnAuthenticationWarning;
            ((System.ComponentModel.ISupportInitialize)_client).EndInit();

            _statusLabel = new System.Windows.Forms.Label
            {
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter,
                Dock = System.Windows.Forms.DockStyle.Fill,
                Font = new System.Drawing.Font("Microsoft YaHei", 9),
                ForeColor = System.Drawing.Color.Gray,
            };

            _panel = new System.Windows.Forms.Panel();
            _panel.Controls.Add(_statusLabel);
            _panel.Controls.Add(_client);

            _host = new WindowsFormsHost { Child = _panel };

            // 必须在 _host 就绪后再刷新标签（UpdateStatusLabel 依赖其 Dispatcher）
            UpdateStatusLabel();
        }

        private void UpdateStatusLabel()
        {
            // 构造尚未完成（_host 未就绪）时跳过，构造函数末尾会补一次刷新
            if (_host == null || _statusLabel == null) return;

            // WinForms 控件只能在 UI 线程更新
            if (!_host.Dispatcher.CheckAccess())
            {
                _host.Dispatcher.Invoke(UpdateStatusLabel);
                return;
            }

            _statusLabel.Text = Status switch
            {
                Enums.ServerStatus.未连接 => $"未连接\n{Name}\n({IP})",
                Enums.ServerStatus.连接中 => $"连接中...\n{Name}\n({IP})",
                Enums.ServerStatus.已连接 => $"已连接 - {Name}",
                Enums.ServerStatus.异常 => $"连接失败\n{_errorMessage ?? "未知错误"}\n{Name}\n({IP})",
                _ => $"{Name}\n({IP})",
            };
            _statusLabel.ForeColor = Status switch
            {
                Enums.ServerStatus.已连接 => System.Drawing.Color.Green,
                Enums.ServerStatus.异常 => System.Drawing.Color.Red,
                Enums.ServerStatus.连接中 => System.Drawing.Color.Orange,
                _ => System.Drawing.Color.Gray,
            };
        }

        public Task ConnectAsync(string userName, string password, int port, int width, int height)
        {
            // ActiveX 控件必须在 UI 线程连接
            if (!_host.Dispatcher.CheckAccess())
            {
                return _host.Dispatcher.InvokeAsync(() => ConnectCore(userName, password, port, width, height)).Task;
            }
            ConnectCore(userName, password, port, width, height);
            return Task.CompletedTask;
        }

        private void ConnectCore(string userName, string password, int port, int width, int height)
        {
            try
            {
                Status = Enums.ServerStatus.连接中;

                if (!_client.IsHandleCreated)
                {
                    _client.CreateControl();
                    if (!_client.IsHandleCreated)
                    {
                        throw new InvalidOperationException("RDP 控件初始化失败，无法创建句柄");
                    }
                }

                _client.Server = IP;
                _client.UserName = userName;
                _client.AdvancedSettings9.ClearTextPassword = password ?? "";
                _client.AdvancedSettings9.RDPPort = port;
                _client.AdvancedSettings9.EnableCredSspSupport = true;
                _client.AdvancedSettings9.AuthenticationLevel = 2;

                // 以控件真实客户区为准（已扣除页签条/边框，且为物理像素）：
                // 估算值哪怕差几像素都会导致远端桌面与可视区不匹配而出现滚动条
                var (actualW, actualH) = MeasureSurface();
                _client.DesktopWidth = actualW > 0 ? actualW : width;
                _client.DesktopHeight = actualH > 0 ? actualH : height;
                _client.ColorDepth = 32;

                // 不启用 SmartSizing：它把远端画面整体缩放以适配控件区域，分辨率不匹配时必然模糊
                _client.AdvancedSettings9.SmartSizing = false;

                _client.Connect();
            }
            catch (Exception ex)
            {
                Status = Enums.ServerStatus.异常;
                ErrorMessage = $"连接失败: {ex.Message}";
            }
        }

        private void Client_OnConnecting(object? sender, EventArgs e)
        {
            Status = Enums.ServerStatus.连接中;
            _statusLabel.Visible = true;
            _client.Visible = false;
        }

        /// <summary>
        /// 测量控件真实可用客户区的物理像素尺寸。
        /// 返回 (0,0) 表示尚未布局（调用方回退到估算值）。
        /// WinForms 控件的 ClientSize 已是物理像素，无需再做 DPI 换算
        /// </summary>
        private (int width, int height) MeasureSurface()
        {
            if (!_host.Dispatcher.CheckAccess())
            {
                return _host.Dispatcher.Invoke(MeasureSurface);
            }

            // 尝试把当前布局同步到 WinForms 侧，保证 ClientSize 反映真实可视区
            _host.UpdateLayout();

            var size = _panel.ClientSize;
            // 布局尚未完成时尺寸不可信（太小），交由调用方回退
            if (size.Width < 100 || size.Height < 100) return (0, 0);
            return (size.Width, size.Height);
        }

        private void Client_OnLoginComplete(object? sender, EventArgs e)
        {
            Status = Enums.ServerStatus.已连接;
            ErrorMessage = null;
            _statusLabel.Visible = false;
            _client.Visible = true;
        }

        private void Client_OnDisconnected(object sender, IMsTscAxEvents_OnDisconnectedEvent e)
        {
            // discReason: 0=正常断开, 1=本地主动断开, 2=远程断开, 其他=错误。
            // 不做自动重连：断开即回到未连接/异常，由用户决定是否重新连接
            if (e.discReason == 0 || e.discReason == 1)
            {
                ErrorMessage = null;
                Status = Enums.ServerStatus.未连接;
            }
            else
            {
                ErrorMessage = GetDisconnectReason(e.discReason);
                Status = Enums.ServerStatus.异常;
            }

            _statusLabel.Visible = true;
            _client.Visible = false;
        }

        private void Client_OnAuthenticationWarning(object? sender, EventArgs e)
        {
            // 关键：先设置 ErrorMessage，再设置 Status
            ErrorMessage = "身份验证警告";
            Status = Enums.ServerStatus.异常;
        }

        private static string GetDisconnectReason(int reason)
        {
            return reason switch
            {
                1 => "本地断开",
                2 => "远程断开",
                3 => "连接超时",
                4 => "网络错误",
                5 => "身份验证失败",
                6 => "许可证错误",
                7 => "安全层错误",
                _ => $"未知错误 (代码: {reason})",
            };
        }

        public void Disconnect()
        {
            if (!_host.Dispatcher.CheckAccess())
            {
                _host.Dispatcher.Invoke(Disconnect);
                return;
            }

            try
            {
                if (_status == Enums.ServerStatus.已连接)
                {
                    _client.RequestClose();
                }
                else
                {
                    Status = Enums.ServerStatus.未连接;
                    ErrorMessage = null;
                }
            }
            catch (Exception ex)
            {
                Status = Enums.ServerStatus.异常;
                ErrorMessage = $"断开失败: {ex.Message}";
            }
        }

        public UIElement CreateView() => _host;

        public void Dispose()
        {
            try
            {
                if (_status == Enums.ServerStatus.已连接)
                {
                    _client.RequestClose();
                }
                _client.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RDP 释放失败: {ex.Message}");
            }
        }
    }
}
