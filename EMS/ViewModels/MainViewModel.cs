using EMS.Common;
using EMS.Data;
using EMS.Models;
using EMS.Services;
using EMS.Services.Connections;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Input;

namespace EMS.ViewModels
{
    /// <summary>
    /// 主窗口 ViewModel：管理服务器树、Tab 生命周期与窗口配置。
    /// 数据访问全部经由仓储，弹窗全部经由服务接口，不直接触碰数据库与 WPF MessageBox。
    /// </summary>
    public class MainViewModel : NotifyPropertyChanged
    {
        private readonly IGroupRepository _groupRepository;
        private readonly IServerRepository _serverRepository;
        private readonly IConfigRepository _configRepository;
        private readonly IDialogService _dialogService;
        private readonly IEditorDialogService _editorDialogService;
        private readonly ConnectionManager _connectionManager;
        private readonly EMS.Services.Mcp.McpServerManager _mcpManager;

        private AppConfig? _config;
        private GroupViewModel? _selectedGroup;
        private ServerViewModel? _selectedServer;
        private TabItemViewModel? _selectedTabItem;
        private System.Threading.Timer? _layoutSaveTimer;

        public MainViewModel(
            IGroupRepository groupRepository,
            IServerRepository serverRepository,
            IConfigRepository configRepository,
            IDialogService dialogService,
            IEditorDialogService editorDialogService,
            ConnectionManager connectionManager,
            EMS.Services.Mcp.McpServerManager mcpManager)
        {
            _groupRepository = groupRepository;
            _serverRepository = serverRepository;
            _configRepository = configRepository;
            _dialogService = dialogService;
            _editorDialogService = editorDialogService;
            _connectionManager = connectionManager;
            _mcpManager = mcpManager;

            NewGroupCommand = new AsyncRelayCommand(NewGroupAsync);
            EditGroupCommand = new AsyncRelayCommand(p => EditGroupAsync(p as GroupViewModel));
            DeleteGroupCommand = new AsyncRelayCommand(p => DeleteGroupAsync(p as GroupViewModel));
            AddServerCommand = new AsyncRelayCommand(p => AddServerAsync(p as GroupViewModel));
            EditServerCommand = new AsyncRelayCommand(p => EditServerAsync(p as ServerViewModel));
            DeleteServerCommand = new AsyncRelayCommand(p => DeleteServerAsync(p as ServerViewModel));
            ConnectCommand = new AsyncRelayCommand(p => ConnectAsync(p as ServerViewModel));
            DisconnectCommand = new RelayCommand(p => DisconnectServer(p as ServerViewModel));
            ConnectOrDisconnectCommand = new AsyncRelayCommand(async p =>
            {
                var server = p as ServerViewModel;
                if (server == null) return;
                if (server.IsConnected)
                {
                    DisconnectServer(server);
                }
                else
                {
                    await ConnectAsync(server);
                }
            });
            CloseTabCommand = new RelayCommand(p => { if (p is TabItemViewModel tab) tab.Close(); });
            McpSettingsCommand = new AsyncRelayCommand(ShowMcpSettingsAsync);
        }

        private async Task ShowMcpSettingsAsync()
        {
            var dialog = new EMS.Dialogs.McpSettingsDialog(_mcpManager)
            {
                Owner = System.Windows.Application.Current.MainWindow,
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                await _mcpManager.ApplyPortAsync(dialog.ResultPort);
                Config.McpPort = dialog.ResultPort;
                await _configRepository.SaveAsync(Config);
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync($"MCP 服务启动失败：{ex.Message}", "错误", MessageKind.Error);
            }
        }

        /// <summary>连接会话注册表（Tab 与 MCP 共用）</summary>
        public ConnectionManager Connections => _connectionManager;

        public ObservableCollection<GroupViewModel> Groups { get; } = [];

        public ObservableCollection<TabItemViewModel> TabItems { get; } = [];

        public TabItemViewModel? SelectedTabItem
        {
            get => _selectedTabItem;
            set
            {
                _selectedTabItem = value;
                OnPropertyChanged(nameof(SelectedTabItem));
            }
        }

        public GroupViewModel? SelectedGroup
        {
            get => _selectedGroup;
            set
            {
                _selectedGroup = value;
                OnPropertyChanged(nameof(SelectedGroup));
                OnPropertyChanged(nameof(StatusBarText1));
                OnPropertyChanged(nameof(StatusBarText2));
            }
        }

        public ServerViewModel? SelectedServer
        {
            get => _selectedServer;
            set
            {
                _selectedServer = value;
                if (value != null)
                {
                    OpenServerTab(value);
                }
                OnPropertyChanged(nameof(SelectedServer));
                OnPropertyChanged(nameof(StatusBarText1));
                OnPropertyChanged(nameof(StatusBarText2));
            }
        }

        #region 命令

        public ICommand NewGroupCommand { get; }
        public ICommand EditGroupCommand { get; }
        public ICommand DeleteGroupCommand { get; }
        public ICommand AddServerCommand { get; }
        public ICommand EditServerCommand { get; }
        public ICommand DeleteServerCommand { get; }
        /// <summary>参数为 ServerViewModel；为空则作用于当前选中服务器</summary>
        public ICommand ConnectCommand { get; }
        public ICommand DisconnectCommand { get; }
        /// <summary>参数为 ServerViewModel，已连接则断开、否则连接（树右键菜单用）</summary>
        public ICommand ConnectOrDisconnectCommand { get; }
        public ICommand CloseTabCommand { get; }
        /// <summary>打开 MCP 端口设置对话框</summary>
        public ICommand McpSettingsCommand { get; }

        #endregion

        #region 状态栏

        /// <summary>
        /// 状态栏左侧文本
        /// </summary>
        public string StatusBarText1
        {
            get
            {
                if (SelectedServer != null)
                {
                    return $"{SelectedServer.Port}";
                }
                else if (SelectedGroup != null)
                {
                    return $" {SelectedGroup.Servers.Count}台服务器";
                }

                var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
                return $" v{version}";
            }
        }

        /// <summary>
        /// 状态栏右侧文本
        /// </summary>
        public string StatusBarText2
        {
            get
            {
                if (SelectedServer != null)
                {
                    return SelectedServer.IP;
                }
                else if (SelectedGroup != null)
                {
                    return SelectedGroup.Name;
                }

                var productName = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "EMS";
                return productName;
            }
        }

        #endregion

        #region 初始化与配置

        /// <summary>
        /// 启动时异步加载配置与服务器树（一次 Include 查询，无 N+1）
        /// </summary>
        public async Task InitializeAsync()
        {
            _config = await _configRepository.GetAsync();
            var groups = await _groupRepository.GetAllWithServersAsync();

            Groups.Clear();
            foreach (var group in groups)
            {
                Groups.Add(new GroupViewModel(group));
            }
        }

        /// <summary>
        /// 窗口配置（InitializeAsync 完成后可用，未就绪时返回默认值）
        /// </summary>
        public AppConfig Config => _config ?? new AppConfig();

        /// <summary>
        /// 布局保存防抖：尺寸变化停止 500ms 后才落盘，避免拖动过程持续写库
        /// </summary>
        public void ScheduleSaveLayout(int windowWidth, int windowHeight, int workspaceWidth, int workspaceHeight, int splitterWidth)
        {
            if (_config == null) return;
            _config.Window_Width = windowWidth;
            _config.Window_Height = windowHeight;
            _config.Width = workspaceWidth;
            _config.Height = workspaceHeight;
            _config.SplitterWidth = splitterWidth;

            if (_layoutSaveTimer == null)
            {
                _layoutSaveTimer = new System.Threading.Timer(_ =>
                {
                    try { _configRepository.SaveAsync(_config).GetAwaiter().GetResult(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"保存布局失败: {ex.Message}"); }
                }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }
            _layoutSaveTimer.Change(500, System.Threading.Timeout.Infinite);
        }

        /// <summary>
        /// 窗口关闭时立即落盘布局
        /// </summary>
        public async Task SaveLayoutNowAsync()
        {
            _layoutSaveTimer?.Dispose();
            _layoutSaveTimer = null;
            if (_config != null)
            {
                await _configRepository.SaveAsync(_config);
            }
        }

        #endregion

        #region 群组管理

        private async Task NewGroupAsync()
        {
            var result = _editorDialogService.EditGroup(null);
            if (result == null) return;

            try
            {
                await _groupRepository.AddAsync(result);
                Groups.Add(new GroupViewModel(result));
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync($"保存失败：{ex.Message}", "错误", MessageKind.Error);
            }
        }

        private async Task EditGroupAsync(GroupViewModel? target)
        {
            target ??= SelectedGroup;
            if (target == null) return;

            var entity = target.GetModel();
            var result = _editorDialogService.EditGroup(entity);
            if (result == null) return;

            try
            {
                await _groupRepository.UpdateAsync(entity);
                target.RefreshFromModel();
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync($"保存失败：{ex.Message}", "错误", MessageKind.Error);
            }
        }

        private async Task DeleteGroupAsync(GroupViewModel? target)
        {
            target ??= SelectedGroup;
            if (target == null) return;

            var confirmed = await _dialogService.ConfirmAsync(
                $"群组 \"{target.Name}\" 中包含 {target.Servers.Count} 个服务器。\n\n删除群组将同时删除所有服务器，此操作不可恢复！\n\n确定要删除吗？",
                "确认删除");
            if (!confirmed) return;

            try
            {
                // 先关闭组内服务器的 Tab
                foreach (var serverVm in target.Servers.ToList())
                {
                    CloseTabsForServer(serverVm.ID);
                }

                await _groupRepository.DeleteAsync(target.GetModel());
                Groups.Remove(target);
                if (SelectedGroup == target)
                {
                    SelectedGroup = null;
                }
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync($"删除失败：{ex.Message}", "错误", MessageKind.Error);
            }
        }

        #endregion

        #region 服务器管理

        private async Task AddServerAsync(GroupViewModel? target)
        {
            target ??= SelectedGroup;
            if (target == null) return;

            var result = _editorDialogService.EditServer(target.ID, null);
            if (result == null) return;

            try
            {
                await _serverRepository.AddAsync(result);
                target.AddServer(new ServerViewModel(result));
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync($"保存失败：{ex.Message}", "错误", MessageKind.Error);
            }
        }

        private async Task EditServerAsync(ServerViewModel? target)
        {
            target ??= SelectedServer;
            if (target == null) return;

            var tracked = target.GetModel();
            var result = _editorDialogService.EditServer(target.GroupID, tracked);
            if (result == null) return;

            // 对话框返回的是副本，把值写回实体（连接类型与 SSH 副连接字段同样必须回写）
            tracked.Name = result.Name;
            tracked.ConnectionType = result.ConnectionType;
            tracked.IP = result.IP;
            tracked.Port = result.Port;
            tracked.SupportsSsh = result.SupportsSsh;
            tracked.SshPort = result.SshPort;
            tracked.Account = result.Account;
            tracked.InheritAccount = result.InheritAccount;
            tracked.Password = result.Password;
            tracked.InheritPassword = result.InheritPassword;

            try
            {
                await _serverRepository.UpdateAsync(tracked);
                target.RefreshFromModel();
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync($"保存失败：{ex.Message}", "错误", MessageKind.Error);
            }
        }

        private async Task DeleteServerAsync(ServerViewModel? target)
        {
            target ??= SelectedServer;
            if (target == null) return;

            var confirmed = await _dialogService.ConfirmAsync(
                $"确定要删除服务器 \"{target.Name}\" 吗？", "确认删除");
            if (!confirmed) return;

            try
            {
                CloseTabsForServer(target.ID);
                await _serverRepository.DeleteAsync(target.GetModel());

                var groupVm = GetGroupViewModelById(target.GroupID);
                groupVm?.RemoveServer(target);
                if (SelectedServer == target)
                {
                    SelectedServer = null;
                }
            }
            catch (Exception ex)
            {
                await _dialogService.ShowMessageAsync($"删除失败：{ex.Message}", "错误", MessageKind.Error);
            }
        }

        #endregion

        #region Tab 与连接

        private async Task ConnectAsync(ServerViewModel? target)
        {
            target ??= SelectedServer;
            if (target == null)
            {
                await _dialogService.ShowMessageAsync("请先选择一个服务器", "提示", MessageKind.Warning);
                return;
            }

            OpenServerTab(target);
            var tab = TabItems.FirstOrDefault(t => t.Server.ID == target.ID);
            tab?.Connect();
        }

        private void DisconnectServer(ServerViewModel? target)
        {
            target ??= SelectedServer;
            if (target == null) return;
            var tab = TabItems.FirstOrDefault(t => t.Server.ID == target.ID);
            tab?.Disconnect();
        }

        /// <summary>
        /// 打开（或聚焦）服务器对应 Tab
        /// </summary>
        public void OpenServerTab(ServerViewModel server)
        {
            var existingTab = TabItems.FirstOrDefault(t => t.Server.ID == server.ID);
            if (existingTab != null)
            {
                SelectedTabItem = existingTab;
                return;
            }

            var newTab = new TabItemViewModel(server.GetModel(), this);
            TabItems.Add(newTab);
            SelectedTabItem = newTab;
        }

        public void CloseTab(TabItemViewModel tab)
        {
            TabItems.Remove(tab);
        }

        private void CloseTabsForServer(string serverId)
        {
            var tabs = TabItems.Where(t => t.Server.ID == serverId).ToList();
            foreach (var tab in tabs)
            {
                if (SelectedTabItem == tab)
                {
                    SelectedTabItem = null;
                }
                tab.Disconnect();
                TabItems.Remove(tab);
            }
        }

        public ServerViewModel? GetServerViewModelById(string serverId)
        {
            foreach (var group in Groups)
            {
                var server = group.Servers.FirstOrDefault(s => s.ID == serverId);
                if (server != null)
                {
                    return server;
                }
            }
            return null;
        }

        public GroupViewModel? GetGroupViewModelById(string groupId)
        {
            return Groups.FirstOrDefault(g => g.ID == groupId);
        }

        #endregion
    }
}
