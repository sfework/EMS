using EMS.Models;
using Microsoft.EntityFrameworkCore;
using SfeWork.EntityFrameworkCore;

namespace EMS.Data
{
    // 说明：仓储注册为单例，但每次操作通过工厂创建独立 DbContext。
    // UI 线程与 MCP（Kestrel）线程可能并发访问数据库，DbContext 本身非线程安全，
    // 因此不在仓储中长期持有上下文；实体以分离（detached）方式流转。

    /// <summary>
    /// 分组仓储
    /// </summary>
    public interface IGroupRepository
    {
        /// <summary>
        /// 一次查询加载全部分组及其服务器（消除 N+1）
        /// </summary>
        Task<List<ServerGroup>> GetAllWithServersAsync();

        Task AddAsync(ServerGroup group);

        Task UpdateAsync(ServerGroup group);

        /// <summary>
        /// 删除分组并级联删除组内服务器
        /// </summary>
        Task DeleteAsync(ServerGroup group);
    }

    /// <summary>
    /// 服务器仓储
    /// </summary>
    public interface IServerRepository
    {
        Task AddAsync(Server server);

        Task UpdateAsync(Server server);

        Task DeleteAsync(Server server);
    }

    /// <summary>
    /// 配置仓储：单条配置常驻内存，保存走数据库
    /// </summary>
    public interface IConfigRepository
    {
        /// <summary>
        /// 读取配置（首次读库并缓存，不存在则创建）
        /// </summary>
        Task<AppConfig> GetAsync();

        Task SaveAsync(AppConfig config);
    }

    public class GroupRepository : IGroupRepository
    {
        private readonly IDbContextFactory<EmsDbContext> _factory;

        public GroupRepository(IDbContextFactory<EmsDbContext> factory) => _factory = factory;

        public async Task<List<ServerGroup>> GetAllWithServersAsync()
        {
            using var db = _factory.CreateDbContext();
            return await db.ServerGroups.Include(g => g.Servers).ToListAsync();
        }

        public async Task AddAsync(ServerGroup group)
        {
            using var db = _factory.CreateDbContext();
            if (string.IsNullOrEmpty(group.ID))
            {
                group.ID = NewID.ID;
            }
            db.ServerGroups.Add(group);
            await db.SaveChangesAsync();
        }

        public async Task UpdateAsync(ServerGroup group)
        {
            using var db = _factory.CreateDbContext();
            var tracked = await db.ServerGroups.FirstOrDefaultAsync(g => g.ID == group.ID);
            if (tracked == null) return;

            // 只拷贝标量字段，避免 Update(整个图) 把导航集合重复写库
            tracked.Name = group.Name;
            tracked.DefaultAccount = group.DefaultAccount;
            tracked.DefaultPassword = group.DefaultPassword;
            await db.SaveChangesAsync();
        }

        public async Task DeleteAsync(ServerGroup group)
        {
            using var db = _factory.CreateDbContext();
            // 桩实体只删分组行，组内服务器由外键 ON DELETE CASCADE 在库端级联
            db.ServerGroups.Remove(new ServerGroup { ID = group.ID });
            await db.SaveChangesAsync();
        }
    }

    public class ServerRepository : IServerRepository
    {
        private readonly IDbContextFactory<EmsDbContext> _factory;

        public ServerRepository(IDbContextFactory<EmsDbContext> factory) => _factory = factory;

        public async Task AddAsync(Server server)
        {
            using var db = _factory.CreateDbContext();
            if (string.IsNullOrEmpty(server.ID))
            {
                server.ID = NewID.ID;
            }
            db.Servers.Add(server);
            await db.SaveChangesAsync();
        }

        public async Task UpdateAsync(Server server)
        {
            using var db = _factory.CreateDbContext();
            var tracked = await db.Servers.FirstOrDefaultAsync(s => s.ID == server.ID);
            if (tracked == null) return;

            // 只拷贝标量字段（导航属性保持库中现值）
            tracked.GroupID = server.GroupID;
            tracked.Name = server.Name;
            tracked.ConnectionType = server.ConnectionType;
            tracked.IP = server.IP;
            tracked.Port = server.Port;
            tracked.SupportsSsh = server.SupportsSsh;
            tracked.SshPort = server.SshPort;
            tracked.Account = server.Account;
            tracked.InheritAccount = server.InheritAccount;
            tracked.Password = server.Password;
            tracked.InheritPassword = server.InheritPassword;
            await db.SaveChangesAsync();
        }

        public async Task DeleteAsync(Server server)
        {
            using var db = _factory.CreateDbContext();
            db.Servers.Remove(new Server { ID = server.ID });
            await db.SaveChangesAsync();
        }
    }

    public class ConfigRepository : IConfigRepository
    {
        private readonly IDbContextFactory<EmsDbContext> _factory;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private AppConfig? _cached;

        public ConfigRepository(IDbContextFactory<EmsDbContext> factory) => _factory = factory;

        public async Task<AppConfig> GetAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (_cached != null) return _cached;

                using var db = _factory.CreateDbContext();
                _cached = await db.Configs.FirstOrDefaultAsync();
                if (_cached == null)
                {
                    _cached = new AppConfig { ID = NewID.ID };
                    db.Configs.Add(_cached);
                    await db.SaveChangesAsync();
                }
                return _cached;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task SaveAsync(AppConfig config)
        {
            await _lock.WaitAsync();
            try
            {
                _cached = config;
                using var db = _factory.CreateDbContext();
                db.Configs.Update(config);
                await db.SaveChangesAsync();
            }
            finally
            {
                _lock.Release();
            }
        }
    }
}
