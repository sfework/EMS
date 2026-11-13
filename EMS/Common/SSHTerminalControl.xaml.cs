using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EMS.Dialogs;
using EMS.Services.Connections;
using EMS.Services.Sftp;
using static EMS.Models.Enums;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using ListView = System.Windows.Controls.ListView;
using ListViewItem = System.Windows.Controls.ListViewItem;
using DragEventArgs = System.Windows.DragEventArgs;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using UserControl = System.Windows.Controls.UserControl;

namespace EMS.Common
{
    /// <summary>
    /// SSH 终端 + SFTP 文件管理控件。
    /// 终端输出走批量刷新（DispatcherTimer），文件操作全部委托 SftpBrowserService /
    /// TransferQueueService，控件本身只负责视图与输入。
    /// </summary>
    public partial class SSHTerminalControl : UserControl
    {
        private readonly ISshConnection _ssh;
        private SftpBrowserService? _browser;
        private TransferQueueService? _transferQueue;

        private string _currentRemotePath = "/";

        // 应用内远程文件剪贴板（粘贴支持 远→远 / 远→本地 / 本地→远）
        private List<RemoteFileEntry> _remoteClipboard = [];
        // 系统剪贴板监听：外部复制（资源管理器等）覆盖后，远程剪贴板过期
        private readonly RemoteClipboardMonitor _clipboardMonitor = new();

        // 终端输出批量缓冲：后台线程入队，UI 定时器统一刷入，避免逐条 Invoke
        private readonly ConcurrentQueue<string> _outputQueue = new();
        private readonly DispatcherTimer _flushTimer;

        // VT100/ANSI 终端仿真器：解释转义序列（擦行/清屏/光标移动/\r 覆盖等），
        // cmd 与 Linux 程序的重画行为由此正确渲染，网格尺寸须与远端 pty 一致
        private readonly XTerm.Terminal _emulator;

        // 上次写入控件的文本，避免内容未变时的冗余刷新
        private string _lastRendered = string.Empty;

        public SSHTerminalControl(ISshConnection ssh)
                {
                    InitializeComponent();
                    _ssh = ssh;

                    // 嵌入模式（RDP+SSH 组合视图）：只保留命令终端，且隐藏内层页签条，
                    // 使页签在整个视图内只有外层一级（避免"命令终端页签里又套命令终端页签"）
                    if (ssh.TerminalOnly)
                    {
                        RootTabs.Items.Remove(FileManagerTab);
                        RootTabs.Style = (Style)FindResource("ChromeLessTabControl");
                        // 无页签条模板只渲染 SelectedContent，必须显式选中，否则内容区空白
                        RootTabs.SelectedIndex = 0;
                    }

                    TerminalOutput.SyntaxHighlighting = null;

            // 仿真器网格与远端 pty 保持一致
            _emulator = new XTerm.Terminal(new XTerm.Options.TerminalOptions
            {
                Cols = _ssh.ShellCols,
                Rows = _ssh.ShellRows,
                Scrollback = 5000,
            });

            _ssh.OutputReceived += OnOutputReceived;
            _ssh.StatusChanged += OnStatusChanged;

            _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _flushTimer.Tick += (_, _) => FlushOutput();
            _flushTimer.Start();

            // 网格尺寸随视图大小调整：实测字符像素宽算 cols/rows，
            // 通知远端 pty（top/vim 立即按新宽度重排）+ 同步本地仿真器。防抖避免拖动过程高频重绘
            _resizeDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _resizeDebounce.Tick += (_, _) =>
            {
                _resizeDebounce.Stop();
                ApplyTerminalSize();
            };
            TerminalOutput.SizeChanged += (_, _) =>
            {
                if (TerminalOutput.ActualWidth < 50 || TerminalOutput.ActualHeight < 50) return;
                _resizeDebounce.Stop();
                _resizeDebounce.Start();
            };

            RemoteFileList.ItemsSource = RemoteFiles;
            RemoteIconList.ItemsSource = RemoteFiles;

            // 远程栏启用资源管理器式橡皮筋框选（两个视图都要）
            RubberBandSelector.Attach(RemoteFileList);
            RubberBandSelector.Attach(RemoteIconList);

            RemotePathBox.Text = RemotePathFormat.ToDisplayPath(_currentRemotePath);

            // 传输队列常驻：任务增减/状态变化驱动遮罩显隐
            var queue = TransferQueue;
            queue.Tasks.CollectionChanged += (_, e) =>
            {
                foreach (var t in e.NewItems?.OfType<FileTransferTask>() ?? []) t.PropertyChanged += OnTaskPropertyChanged;
                foreach (var t in e.OldItems?.OfType<FileTransferTask>() ?? []) t.PropertyChanged -= OnTaskPropertyChanged;
                UpdateTransferOverlay();
            };

            LoadRemoteFiles();

            // 挂到窗口后启动剪贴板监听（HwndSource 需要窗口句柄）；
            // 外部复制覆盖系统剪贴板时，使应用内远程剪贴板过期
            Loaded += (_, _) =>
            {
                var win = Window.GetWindow(this);
                if (win != null)
                {
                    _clipboardMonitor.ExternalClipboardUpdated -= OnExternalClipboardUpdated;
                    _clipboardMonitor.ExternalClipboardUpdated += OnExternalClipboardUpdated;
                    _clipboardMonitor.Start(win);
                }
            };
            Unloaded += (_, _) => _clipboardMonitor.Stop();

            // 注意：Tab 切换也会触发 Unloaded（视图被缓存复用），此处若退订输出事件，
            // 切回标签后终端将永久失联。事件订阅与连接同生命周期，由连接 Dispose 兜底；
            // Unloaded 仅停刷新/防抖定时器，Loaded 时恢复
            Unloaded += (_, _) =>
                        {
                _flushTimer.Stop();
                _resizeDebounce.Stop();
                        };
            Loaded += (_, _) =>
            {
                if (!_flushTimer.IsEnabled) _flushTimer.Start();
            };
        }

        private readonly DispatcherTimer _resizeDebounce;

        private void OnExternalClipboardUpdated()
        {
            _remoteClipboard = [];
        }

        /// <summary>
        /// 按视图实际尺寸与字符像素度量算网格：cols/rows 变化时同步远端 pty 与本地仿真器，
        /// 并立即重绘（仿真器 resize 会重排缓冲，须刷新显示）
        /// </summary>
        private void ApplyTerminalSize()
        {
            // 实测等宽字符像素：'0' 的渲染宽 / 行高（与字体、DPI、字号联动）
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = new Typeface(TerminalOutput.FontFamily, TerminalOutput.FontStyle,
                                         TerminalOutput.FontWeight, TerminalOutput.FontStretch);
            var probe = new FormattedText("0", System.Globalization.CultureInfo.CurrentCulture,
                                          System.Windows.FlowDirection.LeftToRight, typeface,
                                          TerminalOutput.FontSize, System.Windows.Media.Brushes.Gray, dpi);
            var colWidth = Math.Max(1.0, probe.WidthIncludingTrailingWhitespace);
            var rowHeight = Math.Max(1.0, probe.Height);

            // 扣除 Padding=7（两侧共 14px）
            var cols = Math.Max(20, (int)((TerminalOutput.ActualWidth - 14) / colWidth));
            var rows = Math.Max(6, (int)((TerminalOutput.ActualHeight - 14) / rowHeight));

            if (cols == _ssh.ShellCols && rows == _ssh.ShellRows) return;

            // 先同步远端 pty（内部有同尺寸短路），再重排本地仿真器
            _ssh.ResizeShell(cols, rows);
            _emulator.Resize(cols, rows);

            // 重排后网格内容已变：清缓存并立即重绘（不能走 FlushOutput——队列空它会直接返回）
            _lastRendered = string.Empty;
            _lastCaret = -1;
            var text = RenderBuffer();
            var caret = ComputeCaretOffset(text);
            _lastRendered = text;
            _lastCaret = caret;
            TerminalOutput.Text = text;
            TerminalOutput.CaretOffset = caret;
            TerminalOutput.ScrollToEnd();
        }

        private ObservableCollection<RemoteFileEntry> RemoteFiles { get; } = [];

        /// <summary>SFTP 服务（延迟创建，SftpClient 未就绪时为 null）</summary>
        private SftpBrowserService? Browser =>
            _browser ??= _ssh.Sftp != null ? new SftpBrowserService(_ssh.Sftp) : null;

        private TransferQueueService TransferQueue =>
            _transferQueue ??= new TransferQueueService(_ssh.Sftp!, Browser!);

        #region 终端

        private void OnStatusChanged(ServerStatus status)
        {
            if (status == ServerStatus.未连接 || status == ServerStatus.异常)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    while (_outputQueue.TryDequeue(out _)) { }
                    _emulator.Clear();
                    _lastRendered = string.Empty;
                    TerminalOutput.Clear();
                });
            }
        }

        private void OnOutputReceived(string output)
        {
            // 后台线程回调，仅入队；清屏标记也入队以保持顺序
            _outputQueue.Enqueue(output);
        }

        private void FlushOutput()
        {
            if (_outputQueue.IsEmpty) return;

            // 先记录用户是否在底部：翻阅历史时来输出不应把视图拽回（标准终端行为）
            bool atBottom = IsViewAtBottom();

            while (_outputQueue.TryDequeue(out var chunk))
            {
                _emulator.Write(chunk);
            }

            // 从仿真器缓冲提取可见文本（滚动缓冲 + 视口），覆盖式刷新
            var text = RenderBuffer();
            var caret = ComputeCaretOffset(text);
            if (text != _lastRendered || caret != _lastCaret)
            {
                _lastRendered = text;
                _lastCaret = caret;
                TerminalOutput.Text = text;
                // 本地插入符跟随仿真器光标：远端光标在哪（提示符后、行内编辑处），
                // 本地光标就在哪，而不是恒钉在文本末尾
                TerminalOutput.CaretOffset = caret;
                if (atBottom)
                {
                    TerminalOutput.ScrollToEnd();
                }
            }
        }

        private int _lastCaret;

        /// <summary>
        /// 仿真器光标（视口行/列）映射为导出文本中的字符偏移。
        /// 行尾空白已被裁剪，列钳制到行尾；含双宽字符的行可能有 1~2 格误差
        /// </summary>
        private int ComputeCaretOffset(string text)
        {
            var lineCount = _emulator.Buffer.Lines.GetItems().Count();
            var lines = text.Split('\n');

            // 视口是网格的最后 ShellRows 行；buffer.Y 为视口内 0 基行号
            var line = Math.Max(0, lineCount - _ssh.ShellRows) + _emulator.Buffer.Y;
            if (line >= lines.Length) line = lines.Length - 1;
            if (line < 0) return 0;

            var offset = 0;
            for (var i = 0; i < line; i++)
            {
                offset += lines[i].Length + 1;
            }
            return offset + Math.Min(_emulator.Buffer.X, lines[line].Length);
        }

        /// <summary>视口是否停在文档底部（用户没有向上翻阅历史）</summary>
        private bool IsViewAtBottom()
        {
            var view = TerminalOutput.TextArea.TextView;
            return view.ScrollOffset.Y + TerminalOutput.ActualHeight >= view.DocumentHeight - 2;
        }

        /// <summary>
        /// 把仿真器网格（含 scrollback）导出为纯文本，逐行去尾部空格；
        /// 统一用 \n 分隔，保证与 CaretOffset 偏移计算一致
        /// </summary>
        private string RenderBuffer()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var line in _emulator.Buffer.Lines.GetItems())
            {
                var row = new System.Text.StringBuilder();
                for (var i = 0; i < line.Length; i++)
                {
                    row.Append(line[i].Content);
                }
                sb.Append(row.ToString().TrimEnd()).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>
        /// 终端直输（控制键）：Enter/退格/Tab/方向键等映射为对应控制序列发给
        /// 远端 pty；可打印字符统一走 PreviewTextInput，天然兼容 IME 与 Shift 符号，
        /// 且不会与键盘事件重复发送
        /// </summary>
        private void TerminalOutput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;

            // Ctrl+V = 把剪贴板文本发到远端（终端惯例），不插入本地文档
            if (mods.HasFlag(ModifierKeys.Control) && e.Key == Key.V)
            {
                if (Clipboard.ContainsText())
                {
                    _ssh.SendRaw(Clipboard.GetText());
                }
                e.Handled = true;
                return;
            }

            // Ctrl+C：有选中 → 交给本地复制（编辑惯例）；无选中 → 发 SIGINT 给远端
            // （xterm 同款语义。此前 Ctrl+C 从不透传，前台程序（tail -f 等）无法用键盘中断）
            if (mods.HasFlag(ModifierKeys.Control) && e.Key == Key.C)
            {
                if (!TerminalOutput.TextArea.Selection.IsEmpty) return; // 有选区：本地复制
                _ssh.SendRaw("\x03");
                e.Handled = true;
                return;
            }

            // 其余 Ctrl/Win 组合留给本地编辑（Ctrl+A 全选等）
            if (mods.HasFlag(ModifierKeys.Control) || mods.HasFlag(ModifierKeys.Windows)) return;

            string? payload = e.Key switch
            {
                Key.Enter or Key.Return => "\r",
                Key.Back => "\b",
                Key.Tab => "\t",
                Key.Escape => "\x1b",
                Key.Up => "\x1b[A",
                Key.Down => "\x1b[B",
                Key.Right => "\x1b[C",
                Key.Left => "\x1b[D",
                Key.Home => "\x1b[H",
                Key.End => "\x1b[F",
                Key.Delete => "\x1b[3~",
                _ => null,
            };

            if (payload != null)
            {
                _ssh.SendRaw(payload);
                e.Handled = true;
            }
        }

        /// <summary>
        /// 可打印文本（含 IME 组合、Shift 符号）原样透传给远端 shell。
        /// 用 PreviewTextInput 而非 TextInput：AvalonEdit 的 TextArea 会吞掉冒泡的
        /// TextInput（只读时字符输入完全失效），隧道事件在到达 TextArea 前即可截获；
        /// 置 Handled 同时阻止文本插入本地文档
        /// </summary>
        private void TerminalOutput_PreviewTextInput(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Text))
            {
                _ssh.SendRaw(e.Text);
                e.Handled = true;
            }
        }

        /// <summary>
        /// 点击输出区聚焦终端。本地插入符位置跟随远端光标：
        /// 在最后一行点击 → 按点击列定位（行内编辑体验）；
        /// 点击历史区域 → 光标回到底部（历史只读，键入始终接在提示符后）
        /// </summary>
        private void TerminalOutput_Focus(object sender, MouseButtonEventArgs e)
        {
            TerminalOutput.Focus();

            // 标准终端语义：点击只聚焦，本地插入符不跟鼠标走，
            // 恒镜像远端光标位置（行内编辑时随远端 \r/退格移动）
            if (!string.IsNullOrEmpty(_lastRendered))
            {
                TerminalOutput.CaretOffset = ComputeCaretOffset(_lastRendered);
            }
        }

        #endregion

        #region 远程文件

        /// <summary>当前活动的文件视图（详情/图标）</summary>
        private ListView ActiveFileView =>
            RemoteFileList.Visibility == Visibility.Visible ? RemoteFileList : RemoteIconList;

        // 视图切换图标：列表视图时显示"平铺"图标（点击切到图标），反之显示"详情"图标
        private const string ListGlyph = "\uE8FD";   // Clear
        private const string TileGlyph = "\uE8A9";   // ViewAll

        private bool _isLoadingFiles;

        private void LoadRemoteFiles()
        {
            var browser = Browser;
            if (browser == null || _isLoadingFiles) return;

            _isLoadingFiles = true;
            var path = _currentRemotePath;
            RemoteRefreshButton.IsEnabled = false;
            RefreshGlyph.Visibility = Visibility.Collapsed;
            RefreshSpinner.Visibility = Visibility.Visible;

            // 目录列表走网络，后台线程执行避免卡住 UI
            _ = Task.Run(() =>
            {
                List<RemoteFileEntry>? entries = null;
                string? error = null;
                try
                {
                    entries = browser.ListDirectory(path);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                Dispatcher.BeginInvoke(() =>
                {
                    _isLoadingFiles = false;
                    RemoteRefreshButton.IsEnabled = true;
                    RefreshSpinner.Visibility = Visibility.Collapsed;
                    RefreshGlyph.Visibility = Visibility.Visible;

                    if (error != null)
                    {
                        MessageBox.Show($"加载远程文件失败: {error}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    RemoteFiles.Clear();
                    foreach (var entry in entries!)
                    {
                        RemoteFiles.Add(entry);
                    }
                });
            });
        }

        private void RemoteUp_Click(object sender, RoutedEventArgs e)
        {
            var parent = SftpBrowserService.GetParent(_currentRemotePath);
            if (parent != null)
            {
                _currentRemotePath = parent;
            }
            else
            {
                _currentRemotePath = "/";
            }
            RemotePathBox.Text = RemotePathFormat.ToDisplayPath(_currentRemotePath);
            LoadRemoteFiles();
        }

        private void RemoteRefresh_Click(object sender, RoutedEventArgs e)
        {
            LoadRemoteFiles();
        }

        private void ViewToggle_Click(object sender, RoutedEventArgs e)
        {
            bool toIcon = RemoteFileList.Visibility == Visibility.Visible;
            RemoteFileList.Visibility = toIcon ? Visibility.Collapsed : Visibility.Visible;
            RemoteIconList.Visibility = toIcon ? Visibility.Visible : Visibility.Collapsed;
            ViewToggleGlyph.Text = toIcon ? ListGlyph : TileGlyph;
            ActiveFileView.Focus();
        }

        private void RemotePathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && RemotePathBox.IsKeyboardFocused)
            {
                var raw = RemotePathBox.Text.Trim();
                if (string.IsNullOrEmpty(raw)) return;

                var path = RemotePathFormat.ToRemotePath(raw);
                try
                {
                    Browser?.ListDirectory(path);
                    _currentRemotePath = path;
                    RemotePathBox.Text = RemotePathFormat.ToDisplayPath(path);
                    LoadRemoteFiles();
                }
                catch
                {
                    MessageBox.Show($"远程目录不存在或无权限访问: {path}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void RemoteFileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (((ListView)sender).SelectedItem is not RemoteFileEntry item) return;

            if (item.IsDirectory)
            {
                _currentRemotePath = item.FullPath;
                RemotePathBox.Text = RemotePathFormat.ToDisplayPath(_currentRemotePath);
                LoadRemoteFiles();
            }
            else
            {
                // 双击文件可拖出到本地（如资源管理器）
                var dragData = new System.Windows.DataObject();
                dragData.SetData("RemoteFile", item);
                DragDrop.DoDragDrop((ListView)sender, dragData, DragDropEffects.Copy);
            }
        }

        private void RemoteFileList_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
        }

        private void RemoteFileList_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
                e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                foreach (var path in paths)
                {
                    var name = Path.GetFileName(path.TrimEnd('\\', '/'));
                    if (string.IsNullOrEmpty(name)) continue;
                    TransferQueue.EnqueueUpload(path, $"{_currentRemotePath.TrimEnd('/')}/{name}");
                }
            }
        }

        /// <summary>当前选中且可操作的远程条目（两个视图共用活动视图的选中集）</summary>
        private List<RemoteFileEntry> SelectedRemoteEntries() =>
            ActiveFileView.SelectedItems.OfType<RemoteFileEntry>().ToList();

        private void RemoteFileList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var items = SelectedRemoteEntries();
            bool hasSelection = items.Count > 0;
            // 下载/编辑仅对纯文件选中集开放
            bool allFiles = hasSelection && items.All(f => !f.IsDirectory);
            bool canPaste = HasPasteSource();

            // 详情/图标两套菜单同步状态
            RemoteEditMenu.IsEnabled = IconEditMenu.IsEnabled = items.Count == 1 && allFiles;
            RemoteCopyMenu.IsEnabled = IconCopyMenu.IsEnabled = hasSelection;
            RemotePasteMenu.IsEnabled = IconPasteMenu.IsEnabled = canPaste;
            RemoteRenameMenu.IsEnabled = IconRenameMenu.IsEnabled = items.Count == 1;
            RemoteDeleteMenu.IsEnabled = IconDeleteMenu.IsEnabled = hasSelection;
            RemotePropertiesMenu.IsEnabled = IconPropertiesMenu.IsEnabled = items.Count == 1;
        }

        private void RemoteQuickEdit_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveFileView.SelectedItem is RemoteFileEntry item && !item.IsDirectory)
            {
                QuickEditFile(item);
            }
        }

        private void QuickEditFile(RemoteFileEntry remoteFile)
        {
            var browser = Browser;
            if (browser == null) return;

            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), $"ssh_edit_{Guid.NewGuid():N}_{remoteFile.Name}");

                // 快速编辑不显示传输记录，直接下载到临时文件
                using (var fs = File.Create(tempPath))
                {
                    _ssh.Sftp!.DownloadFile(remoteFile.FullPath, fs);
                }

                var editor = new TextEditorWindow(tempPath)
                {
                    // 有 Owner 时 CenterOwner 生效，弹窗居中于主窗口
                    Owner = Window.GetWindow(this),
                };
                if (editor.ShowDialog() == true)
                {
                    TransferQueue.EnqueueUpload(tempPath, remoteFile.FullPath);
                }
                else
                {
                    try { File.Delete(tempPath); } catch { }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"编辑失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RemoteProperties_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveFileView.SelectedItem is not RemoteFileEntry item) return;
            var browser = Browser;
            if (browser == null) return;

            try
            {
                var current = browser.GetOctalPermissions(item.FullPath);
                var dialog = new FilePropertiesWindow(item.FullPath, item.IsDirectory, current)
                {
                    Owner = Window.GetWindow(this),
                };
                if (dialog.ShowDialog() == true && dialog.NewPermission != current)
                {
                    await _ssh.ChangePermissionAsync(item.FullPath, dialog.NewPermission);
                    LoadRemoteFiles();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"操作失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RemoteDelete_Click(object sender, RoutedEventArgs e)
        {
            var items = SelectedRemoteEntries();
            if (items.Count == 0) return;
            var browser = Browser;
            if (browser == null) return;

            var desc = items.Count == 1
                ? $"{(items[0].IsDirectory ? "目录" : "文件")} \"{items[0].Name}\""
                : $"选中的 {items.Count} 个项目";
            var result = MessageBox.Show($"确定要删除{desc}吗？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                // 纯 SFTP 递归删除：协议通道 UTF-8，中文路径安全；后台线程执行避免大目录卡 UI
                await Task.Run(() =>
                {
                    foreach (var item in items)
                    {
                        browser.DeleteRecursive(item.FullPath);
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"删除失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                LoadRemoteFiles();
            }
        }

        private void RemoteFileList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 资源管理器行为：右击未选中项则改选该项；右击已选中项保持多选集
            var view = (ListView)sender;
            if (e.OriginalSource is not DependencyObject src) return;
            if (ItemsControl.ContainerFromElement(view, src) is not ListViewItem container) return;
            if (container.IsSelected) return;
            if (container.Content is RemoteFileEntry entry)
            {
                view.SelectedItem = entry;
            }
        }

        private void RemoteFileList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

            if (ctrl && e.Key == Key.C) { RemoteCopy_Click(sender, e); e.Handled = true; }
            else if (ctrl && e.Key == Key.V) { RemotePaste_Click(sender, e); e.Handled = true; }
            else if (ctrl && e.Key == Key.A) { ((ListView)sender).SelectAll(); e.Handled = true; }
            else if (e.Key == Key.F2) { RemoteRename_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Delete) { RemoteDelete_Click(sender, e); e.Handled = true; }
        }

        #endregion

        #region 剪贴板与粘贴

        /// <summary>应用内远程剪贴板未过期，或系统剪贴板里有本地文件（资源管理器复制）时可粘贴</summary>
        private bool HasPasteSource() =>
            RemoteClipboardActive || SystemClipboard.GetFileDrop().Length > 0;

        private void RemoteCopy_Click(object sender, RoutedEventArgs e) => SetRemoteClipboard();

        private void SetRemoteClipboard()
        {
            var items = SelectedRemoteEntries();
            if (items.Count == 0) return;
            _remoteClipboard = items;

            // 复制到本地：立即下载到临时目录并写入系统剪贴板（复制即传输）。
            // 曾尝试 CF_HDROP 延迟渲染实现"粘贴时传输"，但日志证实 Windows 剪贴板历史/
            // 外壳组件会在复制后立即主动渲染 CF_HDROP，下载被迫提前且无法拦截，
            // 故回退为复制即物化——粘贴必定成功（RDP 大文件剪贴板行为同源）
            _ = MaterializeRemoteCopyAsync(items);
        }

        private string? _clipboardTempRoot;

        /// <summary>复制远程条目到本地：下载到临时目录，完成后以 FileDrop 写入系统剪贴板</summary>
        private async Task MaterializeRemoteCopyAsync(List<RemoteFileEntry> items)
        {
            var root = Path.Combine(Path.GetTempPath(), "EMS", "RemoteClipboard");
            try
            {
                if (_clipboardTempRoot != null && Directory.Exists(_clipboardTempRoot))
                {
                    Directory.Delete(_clipboardTempRoot, true);
                }
            }
            catch { /* 被占用则跳过清理 */ }

            _clipboardTempRoot = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_clipboardTempRoot);

            // 走传输队列：目录递归、进度接入遮罩
            var tempRoot = _clipboardTempRoot;
            var tasks = items.Select(item => TransferQueue.EnqueueDownload(item, tempRoot)).ToList();
            while (tasks.Any(t => t.IsRunning)) await Task.Delay(300);

            var paths = items
                .Select(item => Path.Combine(tempRoot, item.Name))
                .Where(p => File.Exists(p) || Directory.Exists(p))
                .ToArray();

            if (paths.Length == 0)
            {
                MessageBox.Show("复制到本地失败：文件下载未完成", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                // flush=true：内容立即物化进系统剪贴板，本进程退出后仍可粘贴
                Clipboard.SetDataObject(new DataObject(DataFormats.FileDrop, paths), true);
            }
            catch
            {
                // 剪贴板被占用：本地粘贴降级，应用内 远→远 剪贴板不受影响
            }
        }

        /// <summary>
        /// 远程剪贴板是否有效：有内容即有效（外部复制的过期判定由 WM_CLIPBOARDUPDATE
        /// 监听完成，不依赖系统剪贴板自定义格式往返——Win10/11 剪贴板历史会丢失
        /// 自定义格式，曾导致"第二次复制后无法粘贴"）
        /// </summary>
        private bool RemoteClipboardActive => _remoteClipboard.Count > 0;

        /// <summary>粘贴一批冲突询问的策略：首次询问后按"应用到全部"记住选择</summary>
        private sealed class ConflictPolicy
        {
            private ConflictDialog.Choice? _held;
            public bool Cancelled { get; private set; }

            /// <summary>true=继续（替换），false=跳过该项</summary>
            public bool Decide(Window? owner, string name, bool exists)
            {
                if (!exists) return true;
                if (_held.HasValue) return _held == ConflictDialog.Choice.Replace;

                var dialog = new ConflictDialog($"目标位置已存在名为 \"{name}\" 的项目。\n是否替换它？")
                {
                    Owner = owner,
                };
                if (dialog.ShowDialog() != true)
                {
                    Cancelled = true;
                    return false;
                }
                if (dialog.ApplyToAll) _held = dialog.Result;
                return dialog.Result == ConflictDialog.Choice.Replace;
            }
        }

        private async void RemotePaste_Click(object sender, RoutedEventArgs e)
        {
            var browser = Browser;
            if (browser == null) return;

            var targetDir = NormalizeRemote(_currentRemotePath);
            var policy = new ConflictPolicy();

            // 1) 应用内剪贴板未过期：远程→远程（PowerShell / POSIX 按 OS 方言）
            if (RemoteClipboardActive)
            {
                foreach (var item in _remoteClipboard)
                {
                    var target = CombineRemote(targetDir, item.Name);

                    // 目录不允许粘贴进自身/子孙（资源管理器同样拒绝）
                    if (item.IsDirectory && RemoteFileCommands.IsSelfOrDescendant(item.FullPath, targetDir, _ssh.IsWindowsServer))
                    {
                        MessageBox.Show($"不能将目录 \"{item.Name}\" 移动到其自身内部", "提示",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        continue;
                    }
                    if (NormalizeRemote(item.FullPath) == NormalizeRemote(target)) continue; // 原地粘贴

                    bool exists = browser.Exists(target);
                    if (!policy.Decide(Window.GetWindow(this), item.Name, exists))
                    {
                        if (policy.Cancelled) break;
                        continue;
                    }

                    // 失败即中断本批：保留剪贴板可重试
                    if (!await RunRemoteTransferAsync(item, target, exists)) break;
                }

                LoadRemoteFiles();
                return;
            }

            // 2) 系统剪贴板：本地→远程（资源管理器里复制的文件，走上传）。
            // 直接读 Win32 CF_HDROP，绕开 WPF 剪贴板缓存读到旧数据的问题
            var localPaths = SystemClipboard.GetFileDrop();
            if (localPaths.Length > 0)
            {
                foreach (var path in localPaths)
                {
                    var name = Path.GetFileName(path.TrimEnd('\\', '/'));
                    if (string.IsNullOrEmpty(name)) continue;

                    var remoteTarget = targetDir == "/" ? "/" + name : $"{targetDir}/{name}";
                    if (!policy.Decide(Window.GetWindow(this), name, browser.Exists(remoteTarget)))
                    {
                        if (policy.Cancelled) break;
                        continue;
                    }

                    TransferQueue.EnqueueUpload(path, remoteTarget);
                }
            }
        }

        /// <summary>
        /// 服务器端执行 远程→远程 复制：命令方言按远端 OS 选择
        /// （Windows PowerShell / Linux POSIX），由 RemoteFileCommands 构造。
        /// 创建传输任务接入遮罩：单文件轮询目标长度出百分比，目录显示不确定进度。
        /// 返回是否成功（失败已弹框提示）
        /// </summary>
        private async Task<bool> RunRemoteTransferAsync(RemoteFileEntry item, string target, bool targetExists)
        {
            var command = RemoteFileCommands.BuildCopy(
                item.IsDirectory, targetExists,
                item.FullPath, target, _ssh.IsWindowsServer);

            var total = item.IsDirectory ? 0 : item.Size;
            var title = $"复制 {item.Name}";
            var queue = TransferQueue;
            var task = queue.EnqueueRemoteOperation(title, total, target);

            var ok = await RunRemoteCommandAsync(command, $"粘贴 \"{item.Name}\" 失败");
            queue.FinishRemoteOperation(task, ok);
            return ok;
        }

        /// <summary>执行远端 shell 命令，非零退出码弹框并返回 false</summary>
        private async Task<bool> RunRemoteCommandAsync(string command, string failureTitle)
        {
            var result = await _ssh.RunCommandAsync(command, timeoutSeconds: 600);
            if (result.ExitStatus == 0) return true;

            var error = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            MessageBox.Show($"{failureTitle}: {error}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        private static string NormalizeRemote(string path) => path.TrimEnd('/').Length == 0 ? "/" : path.TrimEnd('/');

        /// <summary>目录与子项名拼接，根目录不产生双斜杠</summary>
        private static string CombineRemote(string dir, string name)
        {
            var d = NormalizeRemote(dir);
            return d == "/" ? "/" + name : $"{d}/{name}";
        }

        #endregion

        #region 重命名与新建

        private void RemoteRename_Click(object sender, RoutedEventArgs e)
        {
            var items = SelectedRemoteEntries();
            if (items.Count != 1) return;
            var item = items[0];
            var browser = Browser;
            if (browser == null) return;

            var dialog = new NameInputDialog("重命名", $"将 \"{item.Name}\" 重命名为：", item.Name, selectAll: false)
            {
                Owner = Window.GetWindow(this),
            };
            if (dialog.ShowDialog() != true) return;

            var newName = dialog.InputName;
            if (newName == item.Name) return;
            if (newName.Contains('/') || newName.Contains('\\'))
            {
                MessageBox.Show("名称不能包含路径分隔符", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var target = CombineRemote(_currentRemotePath, newName);
            try
            {
                if (browser.Exists(target))
                {
                    MessageBox.Show($"已存在名为 \"{newName}\" 的项目", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                browser.Rename(item.FullPath, target);
                LoadRemoteFiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"重命名失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RemoteNewFolder_Click(object sender, RoutedEventArgs e)
        {
            var browser = Browser;
            if (browser == null) return;

            var dialog = new NameInputDialog("新建文件夹", "新文件夹名称：", "新建文件夹", selectAll: true)
            {
                Owner = Window.GetWindow(this),
            };
            if (dialog.ShowDialog() != true) return;

            var name = dialog.InputName;
            if (name.Contains('/') || name.Contains('\\'))
            {
                MessageBox.Show("名称不能包含路径分隔符", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var target = CombineRemote(_currentRemotePath, name);
            try
            {
                if (browser.Exists(target))
                {
                    MessageBox.Show($"已存在名为 \"{name}\" 的项目", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                browser.CreateDirectory(target);
                LoadRemoteFiles();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"新建文件夹失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region 传输遮罩

        private bool _overlayActive;

        /// <summary>
        /// 有运行中的任务时显示遮罩并禁用文件管理区；
        /// 一批任务全部结束时收起遮罩并刷新远程列表（上传/远端操作完成后新文件立即可见）
        /// </summary>
        private void UpdateTransferOverlay()
        {
            var running = TransferQueue.Tasks.Any(t => t.IsRunning);
            TransferItems.ItemsSource = null;
            TransferItems.ItemsSource = TransferQueue.Tasks.Where(t => t.IsRunning).ToList();
            TransferOverlay.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            FileManagerRoot.IsEnabled = !running;

            // 只在 有→无 的边沿刷新一次，避免逐任务完成反复列目录
            if (_overlayActive && !running)
            {
                LoadRemoteFiles();
                // FileManagerRoot 禁用期间焦点已落到窗口，恢复焦点否则后续 Ctrl+C/V 快捷键失效
                // （曾致"第一次粘贴后，再次复制无法粘贴"——键盘事件根本没有到达列表）
                _ = Dispatcher.BeginInvoke(() => ActiveFileView.Focus(), DispatcherPriority.Input);
            }
            _overlayActive = running;
        }

        private void TransferCancel_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button { DataContext: FileTransferTask task })
            {
                task.Cancel();
            }
        }

        private void OnTaskPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FileTransferTask.IsRunning))
            {
                UpdateTransferOverlay();
            }
        }

        #endregion
    }
}