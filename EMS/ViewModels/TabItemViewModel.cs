using EMS.Common;
using EMS.Models;
using EMS.Services.Connections;
using System.ComponentModel;
using System.Windows;
using Application = System.Windows.Application;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace EMS.ViewModels
{
    /// <summary>
    /// 单个服务器 Tab：持有该服务器的连接实例，负责状态同步与视图提供。
    /// 连接事件在后台线程触发，统一在本类一处编组到 UI 线程。
    /// </summary>
    public class TabItemViewModel : NotifyPropertyChanged, IDataErrorInfo
    {
        private readonly MainViewModel _mainViewModel;
        private IServerConnection? _connection;
        private UIElement? _cachedView;
        private Enums.ServerStatus _status;
        private string? _errorMessage;

                // RDP 连接前先把视图挂到内容区，等布局完成后才能实测控件客户区尺寸
                private bool _viewMounted;

        public TabItemViewModel(Server server, MainViewModel mainViewModel)
        {
            Server = server;
            _mainViewModel = mainViewModel;
            _status = Enums.ServerStatus.未连接;
        }

        public Server Server { get; }

        public string Header => Server.Name;

        public Enums.ServerStatus Status
        {
            get => _status;
            set
            {
                if (_status == value) return;

                _status = value;

                                // 连接成功即挂载视图（SSH 无预挂载需求，在此统一置位）
                                if (value == Enums.ServerStatus.已连接)
                                {
                                    _viewMounted = true;
                                }

                                // 断开（回到未连接）时彻底销毁视图结构：页签、控件缓存、SSH 副连接一并回收，
                                // 使界面与"从未连接过"的服务器完全一致（不残留已构建的页签）
                                if (value == Enums.ServerStatus.未连接)
                                {
                                    DestroyView();
                                }

                                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(DisplayContent));
                OnPropertyChanged(nameof(StatusMessage));

                // 同步树节点状态
                _mainViewModel.GetServerViewModelById(Server.ID)?.Status = value;
            }
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            set
            {
                if (_errorMessage == value) return;

                _errorMessage = value;
                OnPropertyChanged(nameof(ErrorMessage));
                OnPropertyChanged(nameof(DisplayContent));
                OnPropertyChanged(nameof(StatusMessage));

                _mainViewModel.GetServerViewModelById(Server.ID)?.ErrorMessage = value;
            }
        }

        public string StatusText => Status.ToString();

        /// <summary>
                /// 返回 Tab 内容视图，否则 null（内容区据此切换显示为状态文字）。
        /// 所有连接类型统一为一级页签：RDP 有"远程桌面"（支持 SSH 时加"命令终端"），
                /// SSH 有"命令终端""文件管理"，不存在页签嵌套。
                /// 注意：RDP 在连接前就要挂载视图（先布局、再按真实客户区尺寸连接），
                /// 因此仅在"连接中/已连接"且已挂载时才返回视图
                /// </summary>
                public UIElement? DisplayContent
                {
                    get
                    {
                        if (_connection == null) return null;
                        if (Status == Enums.ServerStatus.未连接 || Status == Enums.ServerStatus.异常) return null;
                        if (!_viewMounted) return null;

                        _cachedView ??= BuildTabs();
                        return _cachedView;
                    }
                }

                // RDP 尺寸回退估算用（正常情况下由控件实测取代，仅布局未就绪时兜底）
                private const int RdpTabChromeWidth = 4;
                private const int RdpTabChromeHeight = 30;

        // SSH 侧（懒连接）：RDP+SSH 组合时的副连接
                private SshConnection? _sshSecondary;
                private System.Windows.Controls.TabControl? _tabs;
                private System.Windows.Controls.TabItem? _terminalTab;

                /// <summary>
                /// 销毁视图结构，回到"从未连接"的干净状态：
                /// 回收 SSH 副连接、断开页签事件、清空页签容器与视图缓存。
                /// 只释放视图与副连接，主连接实例保留以便下次重连复用
                /// </summary>
                private void DestroyView()
                {
                    if (_terminalTab != null)
                    {
                        _terminalTab.GotFocus -= OnTerminalTabActivated;
                        _terminalTab.PreviewMouseLeftButtonDown -= OnTerminalTabClicked;
                        _terminalTab = null;
                    }

                    if (_sshSecondary != null)
                    {
                        _mainViewModel.Connections.Unregister(Server.ID + ConnectionManager.SshSuffix);
                        try { _sshSecondary.Disconnect(); } catch { /* 关闭尽力而为 */ }
                        _sshSecondary.Dispose();
                        _sshSecondary = null;
                    }

                    // 清空页签项：避免已构建的 TabItem 随视图缓存残留
                    _tabs?.Items.Clear();
                    _tabs = null;

                    _cachedView = null;
                    _viewMounted = false;
                }

        /// <summary>
        /// 组装视图：只有 RDP 需要外层页签容器（远程桌面 + 可选命令终端）；
        /// SSH 视图自带"命令终端""文件管理"两个页签，直接返回即可（同为一级页签）
        /// </summary>
        private UIElement BuildTabs()
        {
            if (Server.ConnectionType != Enums.ConnectionType.RDP)
            {
                return _connection!.CreateView();
            }

            _tabs = new System.Windows.Controls.TabControl();
            _tabs.Items.Add(new System.Windows.Controls.TabItem
            {
                Header = "🖥 远程桌面",
                Content = _connection!.CreateView(),
            });

            if (Server.SupportsSsh)
            {
                // 命令终端页签：初始占位，激活时才连 SSH（懒连接）
                _terminalTab = new System.Windows.Controls.TabItem
                {
                    Header = "📟 命令终端",
                    Content = new System.Windows.Controls.TextBlock
                    {
                        Text = "点击此处连接命令终端...",
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                _terminalTab.GotFocus += OnTerminalTabActivated;
                // 连接失败后页签已处于选中态，GotFocus 不再触发：补鼠标点击作为重试入口
                                _terminalTab.PreviewMouseLeftButtonDown += OnTerminalTabClicked;
                _tabs.Items.Add(_terminalTab);
            }
            return _tabs;
        }

                        private async void OnTerminalTabActivated(object sender, RoutedEventArgs e)
                            => await EnsureSshSecondaryConnectedAsync();

                        private async void OnTerminalTabClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
                            => await EnsureSshSecondaryConnectedAsync();

                    /// <summary>
                    /// RDP+SSH 服务器的 SSH 副连接：首次进入命令终端页签时连接，账号密码复用主凭据（含分组继承）。
        /// 以 "serverId:ssh" 副键注册供 MCP 查找；断开不影响 RDP 主连接
        /// </summary>
        private async Task EnsureSshSecondaryConnectedAsync()
        {
            if (_sshSecondary != null) return;

            var groupVm = _mainViewModel.GetGroupViewModelById(Server.GroupID);
            string account = Server.InheritAccount && groupVm != null ? groupVm.DefaultAccount : Server.Account;
            string password = Server.InheritPassword && groupVm != null ? groupVm.DefaultPassword : Server.Password;

            var config = _mainViewModel.Config;
            int width = config.Width > 0 ? config.Width : 1024;
            int height = config.Height > 0 ? config.Height : 768;

            var ssh = new SshConnection($"{Server.Name}-SSH", Server.IP)
                        {
                            // 嵌入外层页签：只留命令终端面板，且隐藏其内部页签条，避免页签嵌套
                            TerminalOnly = true,
                        };
                        _sshSecondary = ssh;

            if (_terminalTab != null)
            {
                _terminalTab.Content = new System.Windows.Controls.TextBlock
                {
                    Text = $"正在连接 SSH {Server.IP}:{Server.SshPort} ...",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }

            try
            {
                await ssh.ConnectAsync(account, password, Server.SshPort, width, height);

                // ConnectAsync 内部吞异常并置状态，须按 Status 判定成败
                var connected = ssh.Status == Enums.ServerStatus.已连接;
                if (!connected) _sshSecondary = null;

                if (_terminalTab != null)
                {
                    _terminalTab.Content = connected
                        ? ssh.CreateView()
                        : BuildSshErrorView(ssh.ErrorMessage ?? "未知错误");
                }

                if (connected)
                {
                    _mainViewModel.Connections.Register(Server.ID + ConnectionManager.SshSuffix, ssh);
                }
            }
            catch (Exception ex)
            {
                _sshSecondary = null;
                if (_terminalTab != null) _terminalTab.Content = BuildSshErrorView(ex.Message);
            }
        }

        /// <summary>SSH 副连接失败提示（可重复点击页签重试）</summary>
        private System.Windows.Controls.TextBlock BuildSshErrorView(string reason) => new()
        {
            Text = $"SSH 连接失败: {reason}\n（请确认服务器已开启 OpenSSH 服务、端口 {Server.SshPort} 可访问）\n重新点击本标签可重试",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = System.Windows.Media.Brushes.Red,
            TextAlignment = TextAlignment.Center,
        };

                /// <summary>
                /// 未连接/连接中/异常时的状态提示
                /// </summary>
                public string StatusMessage
        {
            get
            {
                if (Status == Enums.ServerStatus.异常 && !string.IsNullOrEmpty(ErrorMessage))
                {
                    return $"连接失败\n\n{ErrorMessage}\n\n服务器: {Server.Name}\nIP: {Server.IP}";
                }

                return Status switch
                {
                    Enums.ServerStatus.未连接 => $"服务器: {Server.Name}\nIP: {Server.IP}:{Server.Port}\n\n状态: 未连接",
                    Enums.ServerStatus.连接中 => $"正在连接到 {Server.Name}...\nIP: {Server.IP}:{Server.Port}",
                    Enums.ServerStatus.已连接 => $"已连接到 {Server.Name}",
                    _ => $"服务器: {Server.Name}\nIP: {Server.IP}",
                };
            }
        }

        public void Connect()
        {
            if (Status == Enums.ServerStatus.已连接 || Status == Enums.ServerStatus.连接中)
                return;

            // 解析继承项：账号/密码优先取所属分组的默认值（端口始终存在服务器上）
            var groupVm = _mainViewModel.GetGroupViewModelById(Server.GroupID);
            string actualAccount = Server.InheritAccount && groupVm != null ? groupVm.DefaultAccount : Server.Account;
            string actualPassword = Server.InheritPassword && groupVm != null ? groupVm.DefaultPassword : Server.Password;

            if (Server.ConnectionType == Enums.ConnectionType.RDP)
            {
                // RDP 分辨率必须等于控件真实客户区（物理像素），否则画面与可视区不匹配出滚动条。
                // 先挂载视图让布局跑完，再由 RdpConnection 实测 ClientSize 作为分辨率。
                // 这里传的只是布局未就绪时的兜底估算值
                var config = _mainViewModel.Config;
                int estimatedW = (config.Width > 0 ? config.Width : 1024) - RdpTabChromeWidth;
                int estimatedH = (config.Height > 0 ? config.Height : 768) - RdpTabChromeHeight;

                _viewMounted = true;
                OnPropertyChanged(nameof(DisplayContent));

                // 等一帧完成布局再连接（否则 ClientSize 仍是 0）
                _ = Application.Current.Dispatcher.InvokeAsync(
                    () => _ = ConnectInternalAsync(actualAccount, actualPassword, Server.Port, estimatedW, estimatedH),
                    System.Windows.Threading.DispatcherPriority.Loaded);
                return;
            }

                        // SSH：视图在连接成功后按 Status=已连接 挂载
                        var sshConfig = _mainViewModel.Config;
                        int width = sshConfig.Width > 0 ? sshConfig.Width : 1024;
                        int height = sshConfig.Height > 0 ? sshConfig.Height : 768;
                        _ = ConnectInternalAsync(actualAccount, actualPassword, Server.Port, width, height);
                    }
        private async Task ConnectInternalAsync(string account, string password, int port, int width, int height)
        {
            try
            {
                // 复用已有连接实例（断开后重连场景）
                _connection ??= Server.ConnectionType == Enums.ConnectionType.RDP
                    ? new RdpConnection(Server.Name, Server.IP)
                    : new SshConnection(Server.Name, Server.IP);

                // 先退订再订阅：同一连接实例可能被重连复用，避免重复订阅叠加
                _connection.StatusChanged -= OnConnectionStatusChanged;
                _connection.StatusChanged += OnConnectionStatusChanged;

                                // ConnectAsync 内部吞异常并置状态，须按 Status 判定成败
                                Status = Enums.ServerStatus.连接中;
                                ErrorMessage = null;

                                await _connection.ConnectAsync(account, password, port, width, height);

                                // 只有明确异常才算失败：RDP 的 ConnectAsync 发起异步连接后即返回，
                                // 此刻 Status 仍是"连接中"（成功由 OnLoginComplete 事件置位）
                                if (_connection.Status == Enums.ServerStatus.异常)
                                {
                                    ErrorMessage = _connection.ErrorMessage ?? "连接失败";
                                    return;
                                }

                                // 注册会话供 MCP 等使用
                                _mainViewModel.Connections.Register(Server.ID, _connection);
            }
            catch (Exception ex)
            {
                Status = Enums.ServerStatus.异常;
                ErrorMessage = $"连接失败: {ex.Message}";
            }
        }

        /// <summary>
        /// 连接状态回调（可能来自后台线程），统一在此编组到 UI 线程
        /// </summary>
        private void OnConnectionStatusChanged(Enums.ServerStatus status)
        {
            void Apply()
            {
                string? error = _connection?.ErrorMessage;
                bool changed = _status != status || _errorMessage != error;
                if (!changed) return;

                ErrorMessage = error;
                Status = status;
            }

            if (Application.Current?.Dispatcher.CheckAccess() == true)
            {
                Apply();
            }
            else
            {
                Application.Current?.Dispatcher.BeginInvoke(Apply);
            }
        }

        public void Disconnect()
        {
            if (Status != Enums.ServerStatus.已连接 && Status != Enums.ServerStatus.连接中)
                return;

            try
            {
                _connection?.Disconnect();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"断开失败: {ex.Message}";
                Status = Enums.ServerStatus.异常;
            }
        }

        public void Close()
                {
                    Disconnect();

                    if (_connection != null)
                    {
                        _connection.StatusChanged -= OnConnectionStatusChanged;
                        _mainViewModel.Connections.Unregister(Server.ID);
                    }

                    DestroyView();

                    _connection?.Dispose();
                    _connection = null;

                    _mainViewModel.CloseTab(this);
                }

        public string Error => string.Empty;

        public string this[string columnName] => string.Empty;
    }
}