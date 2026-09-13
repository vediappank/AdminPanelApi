using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Domain.Entities;

namespace RenewalTracker.Platform.Infrastructure.Data;

// The Platform database (RenewalTrackerPlatform) - Tenants, Modules and
// Permissions authored here, then mirrored read-only into the Business
// database (V1, via RenewalTracker.Api's own AppDbContext) by that
// project's CatalogSyncService so every tenant-owned row keeps a real,
// local, EF-enforced FK - see the architecture split plan. Unlike
// AppDbContext, this context has no tenant concept at all: no
// CurrentTenantId, no query filters - every row here IS shared, global,
// platform-owned data.
//
// Menus was merged into Module (dbo.Menus dropped - it held no real
// data): a Module row now doubles as a left-nav entry via its own
// ParentModuleId/MenuUrl/Icon - see Module.cs. Modules has no per-tenant
// relation (TenantId was removed - every row is a standard, global entry).
//
// This class is `partial` so a hand-written half
// (PlatformDbContext.Platform.cs) can add DbSets/model config that don't
// fit this generator's assumptions - e.g. PlatformUsers, which has no FK
// to any of these tables at all - without that code being wiped out the
// next time this file is regenerated.
public partial class PlatformDbContext : DbContext
{
    public PlatformDbContext(DbContextOptions<PlatformDbContext> options) : base(options)
    {
    }

    public DbSet<Module> Modules { get; set; } = null!;
    public DbSet<Permission> Permissions { get; set; } = null!;
    public DbSet<Tenant> Tenants { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Module>(b =>
        {
            b.ToTable("Modules");
            b.HasIndex(x => new { x.ModuleCode }).IsUnique();
            b.Property(x => x.IsActive).HasDefaultValueSql("1");
            b.Property(x => x.IsCore).HasDefaultValueSql("0");
            b.Property(x => x.DisplayOrder).HasDefaultValueSql("0");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("SYSUTCDATETIME()");
            b.HasOne(x => x.ParentModule).WithMany().HasForeignKey(x => x.ParentModuleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Permission>(b =>
        {
            b.ToTable("Permissions");
            b.HasIndex(x => new { x.PermissionCode }).IsUnique();
            b.HasOne(x => x.Module).WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Tenant>(b =>
        {
            b.ToTable("Tenants");
            b.HasIndex(x => new { x.TenantCode }).IsUnique();
            b.Property(x => x.IsActive).HasDefaultValueSql("1");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("SYSUTCDATETIME()");
        });

        OnModelCreatingPartial(modelBuilder);
    }

    /// <summary>Implemented in PlatformDbContext.Platform.cs (hand-written, not regenerated).</summary>
    partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
}
