using EMS.Models;
using Microsoft.EntityFrameworkCore;

namespace EMS.Data
{
    /// <summary>
    /// 应用数据库上下文：连接串集中在此配置，业务方法一律放在仓储层
    /// </summary>
    public class EmsDbContext : DbContext
    {
        public EmsDbContext(DbContextOptions<EmsDbContext> options) : base(options)
        {
        }

        public DbSet<ServerGroup> ServerGroups { get; set; }
        public DbSet<Server> Servers { get; set; }
        public DbSet<AppConfig> Configs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // 分组删除时级联删除组内服务器
            modelBuilder.Entity<Server>()
                .HasOne(s => s.Group)
                .WithMany(g => g.Servers)
                .HasForeignKey(s => s.GroupID)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
