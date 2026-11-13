using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EMS.ViewModels;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace EMS
{
    //https://github.com/mRemoteNG/mRemoteNG
    /// <summary>
    /// 主窗口：code-behind 只保留无法绑定的纯视图行为
    /// （选中项路由、双击命中测试、布局读取），业务操作全部走 ViewModel 命令
    /// </summary>
    public partial class MainWindow : Window
    {
        private bool _isLoading = true;
        private bool _layoutApplied;
        private bool _applyingLayout;

        public MainWindow(MainViewModel viewModel)
        {
            DataContext = viewModel;
            InitializeComponent();

            // 后台初始化（建库+加载）完成后应用保存的窗口布局
            if (App.InitializationTask != null)
            {
                _ = App.InitializationTask.ContinueWith(task =>
                {
                    if (task.IsFaulted)
                    {
                        Dispatcher.Invoke(() => MessageBox.Show(
                            $"数据初始化失败：{task.Exception?.GetBaseException().Message}",
                            "错误", MessageBoxButton.OK, MessageBoxImage.Error));
                        return;
                    }
                    Dispatcher.BeginInvoke(ApplySavedLayout);
                });
            }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // 手动计算并设置窗口居中（替代 WindowStartupLocation="CenterScreen"）
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            this.Left = (screenWidth - this.Width) / 2;
            this.Top = (screenHeight - this.Height) / 2;
        }

        /// <summary>
        /// 数据就绪后应用一次保存的窗口尺寸与分隔条位置
        /// </summary>
        private void ApplySavedLayout()
        {
            if (_layoutApplied || DataContext is not MainViewModel viewModel) return;
            _layoutApplied = true;

            var config = viewModel.Config;
            // 先快照再赋值：赋值 Width 会同步触发 SizeChanged → ScheduleSaveLayout，
            // 而它写回的正是同一个 AppConfig 实例；不快照的话 Window_Height 会在
            // 赋值前被"当前实际高度（还是 XAML 默认值）"覆盖，高度永远恢复不了
            int savedW = config.Window_Width, savedH = config.Window_Height, savedSplit = config.SplitterWidth;

            // 程序化恢复布局期间屏蔽保存（否则中间态会被防抖落库）
            _applyingLayout = true;
            try
            {
                if (savedW > 0) this.Width = savedW;
                if (savedH > 0) this.Height = savedH;
                if (savedSplit > 0) LeftColumn.Width = new GridLength(savedSplit);
            }
            finally
            {
                _applyingLayout = false;
            }

            // 尺寸变化后重新居中
            this.Left = (SystemParameters.PrimaryScreenWidth - this.Width) / 2;
            this.Top = (SystemParameters.PrimaryScreenHeight - this.Height) / 2;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _isLoading = false;
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 关闭时兜底：先把当前尺寸刷进内存配置（覆盖"拖动后 500ms 防抖未触发"的窗口期），
            // 再同步等待落盘——异步 fire-and-forget 会在进程退出时丢掉最后一次布局；
            // SQLite 单行写为毫秒级，同步等待不影响关闭体感
            if (DataContext is MainViewModel viewModel)
            {
                SaveWorkspaceConfig();
                viewModel.SaveLayoutNowAsync().GetAwaiter().GetResult();
            }
        }

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isLoading || _applyingLayout) return;
            SaveWorkspaceConfig();
        }

        private void WorkspaceGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_isLoading || _applyingLayout) return;
            SaveWorkspaceConfig();
        }

        /// <summary>
        /// 布局变化防抖保存，不再每次拖动都写库
        /// </summary>
        private void SaveWorkspaceConfig()
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.ScheduleSaveLayout(
                    (int)ActualWidth, (int)ActualHeight,
                    (int)WorkspaceGrid.ActualWidth, (int)WorkspaceGrid.ActualHeight,
                    (int)LeftColumn.ActualWidth);
            }
        }

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is MainViewModel viewModel)
            {
                if (e.NewValue is ServerViewModel server)
                {
                    viewModel.SelectedGroup = null;
                    viewModel.SelectedServer = server;
                }
                else if (e.NewValue is GroupViewModel group)
                {
                    viewModel.SelectedServer = null;
                    viewModel.SelectedGroup = group;
                }
            }
        }

        /// <summary>
        /// 双击服务器节点连接（命中测试无法绑定，转发给命令）
        /// </summary>
        private void ServerTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.TreeView treeView) return;
            if (DataContext is not MainViewModel viewModel) return;

            var hitTest = VisualTreeHelper.HitTest(treeView, e.GetPosition(treeView));
            if (hitTest == null) return;

            var treeViewItem = FindAncestor<System.Windows.Controls.TreeViewItem>(hitTest.VisualHit);
            if (treeViewItem?.DataContext is ServerViewModel server)
            {
                viewModel.ConnectCommand.Execute(server);
                e.Handled = true;
            }
        }

        /// <summary>
        /// 右键先选中命中节点，菜单本身由 XAML 声明式绑定
        /// </summary>
        private void TreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.TreeView treeView) return;

            var hitTest = VisualTreeHelper.HitTest(treeView, e.GetPosition(treeView));
            if (hitTest == null) return;

            var treeViewItem = FindAncestor<System.Windows.Controls.TreeViewItem>(hitTest.VisualHit);
            if (treeViewItem != null)
            {
                treeViewItem.IsSelected = true;
            }
        }

        private void MenuItem_Exit_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Application.Current.Shutdown();
        }

        private T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T ancestor)
                {
                    return ancestor;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
