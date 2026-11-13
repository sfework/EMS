using System.Windows.Threading;

namespace EMS.Services.Connections
{
    /// <summary>
    /// RDP ActiveX（mstscax.dll）启动预热：进程内首次实例化需冷加载约 15MB 的
    /// COM 模块并完成控件激活，该开销为进程级一次性成本（OS 模块加载器全局共享），
    /// 但必须在任一 STA 线程完成——若留到用户首次连接 RDP，将在 UI 线程同步执行导致界面冻结数秒。
    /// 本类在应用启动后于独立 STA 线程（隐藏窗体 + 消息泵）提前完成加载，异常静默降级：
    /// 预热失败不影响功能，仅回退为"首次连接卡一次"的原行为。
    /// </summary>
    public static class RdpWarmup
    {
        private static int _started;

        /// <summary>
        /// 启动预热（幂等）。在 UI 线程调用，仅做延迟调度，无任何阻塞。
        /// </summary>
        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;

            // 等主窗口完成首帧渲染（Loaded 优先级）后再开始，避免与启动关键路径争抢磁盘/CPU
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded, new Action(() =>
                {
                    var thread = new Thread(RunSta)
                    {
                        IsBackground = true,
                        Name = "RdpWarmup",
                    };
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                }));
        }

        /// <summary>
        /// STA 线程主体：隐藏窗体内实例化并创建 RDP 控件句柄（触发 DLL 加载与激活），
        /// 窗体 Shown（首次绘制周期完成）即退出消息循环并销毁线程。
        /// </summary>
        private static void RunSta()
        {
            try
            {
                using var form = new WarmupForm();
                // 消息泵驱动窗体显示/销毁；WarmupForm 在 Shown 中 ExitThread 停泵后返回
                System.Windows.Forms.Application.Run(form);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RDP 预热异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 预热专用窗体：不可见（无任务栏、不显示），控件句柄创建后即请求退出线程
        /// </summary>
        private sealed class WarmupForm : System.Windows.Forms.Form
        {
            public WarmupForm()
            {
                ShowInTaskbar = false;
                StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                Location = new System.Drawing.Point(-2000, -2000);
                Size = new System.Drawing.Size(1, 1);
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;

                try
                {
                    var client = new AxMSTSCLib.AxMsRdpClient9NotSafeForScripting();
                    ((System.ComponentModel.ISupportInitialize)client).BeginInit();
                    client.Enabled = false;
                    Controls.Add(client);
                    ((System.ComponentModel.ISupportInitialize)client).EndInit();
                    // 强制创建句柄：ActiveX 的真正激活与 mstscax.dll 加载发生在这里
                    client.CreateControl();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"RDP 预热控件初始化失败: {ex.Message}");
                }

                // Shown 在窗体首次显示周期结束时触发，此时控件已完成初始化
                Shown += (_, _) => System.Windows.Forms.Application.ExitThread();
            }
        }
    }
}
