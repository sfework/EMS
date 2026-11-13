using System.Windows;
using EMS.Data;
using EMS.Services;
using EMS.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EMS
{
    /// <summary>
    /// 应用组合根：集中注册服务与视图，窗口由容器创建并注入 DataContext。
    /// 启动路径上不做任何同步数据库操作，建库与数据加载在后台完成。
    /// </summary>
    public partial class App : System.Windows.Application
    {
        public static IServiceProvider Services { get; private set; } = null!;

        /// <summary>
        /// 后台初始化任务（建库 + 加载数据），窗口据此决定何时填充树
        /// </summary>
        public static Task? InitializationTask { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            // 窗口先显示，建库/加载在后台线程执行（启动优化）
            InitializationTask = InitializeBackgroundAsync();

            var window = Services.GetRequiredService<MainWindow>();
            window.Show();

            // RDP ActiveX 首载为进程级一次性开销（约 15MB COM 模块），留到首次连接会在
            // UI 线程同步执行导致冻结——启动后台预热到独立 STA 线程，用户无感
            EMS.Services.Connections.RdpWarmup.Start();
        }

        private static async Task InitializeBackgroundAsync()
        {
            var factory = Services.GetRequiredService<IDbContextFactory<EmsDbContext>>();
            await using (var db = factory.CreateDbContext())
            {
                await EnsureSchemaAsync(db);
            }

            var viewModel = Services.GetRequiredService<MainViewModel>();
            await viewModel.InitializeAsync();

            // 按配置自动拉起 MCP 服务（端口为 0 则跳过）
            if (viewModel.Config.McpPort > 0)
            {
                try
                {
                    var mcp = Services.GetRequiredService<EMS.Services.Mcp.McpServerManager>();
                    await mcp.StartAsync(viewModel.Config.McpPort);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"MCP 自动启动失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 建库。重构后表名随实体改名（ServerGroups/Configs 等），
        /// 检测到旧结构时直接删库重建（不保留旧数据）。
        /// 表结构就绪后，再删除模型已移除但库中残留的列。
        /// </summary>
        private static async Task EnsureSchemaAsync(EmsDbContext db)
        {
            if (await db.Database.CanConnectAsync())
            {
                var exists = await DatabaseTableExistsAsync(db, "ServerGroups");
                if (!exists)
                {
                    await db.Database.EnsureDeletedAsync();
                }
            }
            await db.Database.EnsureCreatedAsync();

            // 模型演进删掉的列不会随 EnsureCreated 移除，启动时一次性清理
            await DropColumnIfPresentAsync(db, "ServerGroups", "ServerType");
            await DropColumnIfPresentAsync(db, "ServerGroups", "DefaultPort");
            await DropColumnIfPresentAsync(db, "Servers", "InheritPort");

            // 服务器类型更名为连接类型：重命名列保留存量数据
            await RenameColumnIfPresentAsync(db, "Servers", "ServerType", "ConnectionType");

            // 新增模型字段不会随 EnsureCreated 补进已有库，启动时一次性补齐
            await AddColumnIfMissingAsync(db, "Servers", "SupportsSsh", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(db, "Servers", "SshPort", "INTEGER NOT NULL DEFAULT 22");
        }

        /// <summary>
        /// 指定表的列不存在则添加（模型新增字段对存量库的补齐迁移）
        /// </summary>
        private static async Task AddColumnIfMissingAsync(EmsDbContext db, string table, string column, string definition)
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using (var check = connection.CreateCommand())
            {
                check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}'";
                var result = await check.ExecuteScalarAsync();
                if (result is not null && Convert.ToInt64(result) > 0) return;
            }
            await using (var add = connection.CreateCommand())
            {
                add.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
                await add.ExecuteNonQueryAsync();
            }
            await connection.CloseAsync();
        }

        /// <summary>
        /// 指定表的旧列存在则重命名（SQLite 3.25+ 支持 RENAME COLUMN）
        /// </summary>
        private static async Task RenameColumnIfPresentAsync(EmsDbContext db, string table, string oldColumn, string newColumn)
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using (var check = connection.CreateCommand())
            {
                check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{oldColumn}'";
                var result = await check.ExecuteScalarAsync();
                if (result is null || Convert.ToInt64(result) == 0) return;
            }
            await using (var rename = connection.CreateCommand())
            {
                rename.CommandText = $"ALTER TABLE {table} RENAME COLUMN {oldColumn} TO {newColumn}";
                await rename.ExecuteNonQueryAsync();
            }
            await connection.CloseAsync();
        }

        /// <summary>
        /// 指定表的列存在则删除（SQLite 3.35+ 支持 DROP COLUMN）
        /// </summary>
        private static async Task DropColumnIfPresentAsync(EmsDbContext db, string table, string column)
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using (var check = connection.CreateCommand())
            {
                check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}'";
                var result = await check.ExecuteScalarAsync();
                if (result is null || Convert.ToInt64(result) == 0) return;
            }
            await using (var drop = connection.CreateCommand())
            {
                drop.CommandText = $"ALTER TABLE {table} DROP COLUMN {column}";
                await drop.ExecuteNonQueryAsync();
            }
            await connection.CloseAsync();
        }

        private static async Task<bool> DatabaseTableExistsAsync(EmsDbContext db, string table)
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'";
            var result = await command.ExecuteScalarAsync();
            await connection.CloseAsync();
            return result is not null && Convert.ToInt64(result) > 0;
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            // 数据层：连接串集中配置。仓储经工厂取短生命周期 DbContext，
            // 避免 UI 与 MCP 线程并发共享同一上下文
            services.AddDbContextFactory<EmsDbContext>(
                options => options.UseSqlite("Data Source=data.db"));

            services.AddSingleton<IGroupRepository, GroupRepository>();
            services.AddSingleton<IServerRepository, ServerRepository>();
            services.AddSingleton<IConfigRepository, ConfigRepository>();

            // 平台服务
            services.AddSingleton<IDialogService, DialogService>();
            services.AddSingleton<IEditorDialogService, EditorDialogService>();
            services.AddSingleton<EMS.Services.Connections.ConnectionManager>();
            services.AddSingleton<EMS.Services.Mcp.McpServerManager>();

            // ViewModel 与视图
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
        }
    }
}
