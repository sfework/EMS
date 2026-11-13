using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using EMS.Services.Connections;

namespace EMS.Services.Mcp
{
    /// <summary>
    /// 进程内 MCP 服务端：localhost Streamable HTTP，仅监听 127.0.0.1。
    /// 端口来自 AppConfig.McpPort（0 = 不启动），配置变更后可 Restart。
    /// </summary>
    public class McpServerManager : IAsyncDisposable
    {
        private readonly ConnectionManager _connections;
        private WebApplication? _app;

        public bool IsRunning => _app != null;
        public int? Port { get; private set; }

        public McpServerManager(ConnectionManager connections)
        {
            _connections = connections;
        }

        public async Task StartAsync(int port)
        {
            await StopAsync();

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options =>
            {
                // 同时监听 IPv4/IPv6 回环，确保客户端用 http://localhost 访问时
                // 无论解析到 127.0.0.1 还是 ::1 都能连上；仍仅限本机
                options.Listen(System.Net.IPAddress.Loopback, port);
                options.Listen(System.Net.IPAddress.IPv6Loopback, port);
            });
            builder.Logging.ClearProviders();

            // 桥接 WPF 侧单例到 MCP 宿主容器
            builder.Services.AddSingleton(_connections);

            builder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithTools<EmsMcpTools>();

            var app = builder.Build();
            // 显式映射到 /mcp：MapMcp() 默认挂在根路径 "/"，
            // 客户端配置 http://localhost:port/mcp 会得到 404
            app.MapMcp("/mcp");

            await app.StartAsync();
            _app = app;
            Port = port;
            System.Diagnostics.Debug.WriteLine($"MCP 服务端已启动: http://localhost:{port}/mcp");
        }

        public async Task StopAsync()
        {
            if (_app == null) return;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _app.StopAsync(cts.Token);
                await _app.DisposeAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MCP 服务端停止失败: {ex.Message}");
            }
            _app = null;
            Port = null;
        }

        /// <summary>
        /// 按端口重启（0 表示停止）
        /// </summary>
        public async Task ApplyPortAsync(int port)
        {
            if (port <= 0)
            {
                await StopAsync();
            }
            else if (Port != port)
            {
                await StartAsync(port);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
        }
    }
}
