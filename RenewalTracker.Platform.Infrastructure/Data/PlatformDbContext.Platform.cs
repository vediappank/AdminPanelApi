using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Domain.Entities;

namespace RenewalTracker.Platform.Infrastructure.Data;

/// <summary>
/// Hand-written half of PlatformDbContext, in its own partial file so it
/// survives regeneration of the other half (see that file's
/// `partial void OnModelCreatingPartial` hook).
///
/// PlatformUsers is not a mirror table (it has no tenant-side FK pointing
/// at it, unlike Tenants/Modules/Permissions/Menus), so it lives only in
/// the Platform database. Deliberately a separate identity system from the
/// per-tenant Users table (RenewalTracker.Api) - not tenant-owned, so no
/// TenantId column, no query filter (there is nothing to scope it by), and
/// no FK to Tenants/Users/Roles at all. See PlatformUser and
/// PlatformAuthService.
/// </summary>
public partial class PlatformDbContext
{
    public DbSet<PlatformUser> PlatformUsers { get; set; } = null!;

    // ----- Sales / Service Catalog / Billing / Applications / Tenant
    // Modules - none of these mirror to the Business database (they are
    // platform-owned, like PlatformUsers), so they live in this
    // hand-written half rather than the generated one. Added to back the
    // Platform Panel screens that previously ran on front-end-only demo
    // data (MockDataService in the Angular app) - see each screen's
    // matching controller for the full story.
    public DbSet<ServiceCategory> ServiceCategories { get; set; } = null!;
    public DbSet<ServiceSubCategory> ServiceSubCategories { get; set; } = null!;
    public DbSet<ServiceItem> ServiceItems { get; set; } = null!;
    public DbSet<Enquiry> Enquiries { get; set; } = null!;
    public DbSet<EnquiryRequirementProduct> EnquiryRequirementProducts { get; set; } = null!;
    public DbSet<FollowUp> FollowUps { get; set; } = null!;
    public DbSet<Lead> Leads { get; set; } = null!;
    public DbSet<Quotation> Quotations { get; set; } = null!;
    public DbSet<QuotationProduct> QuotationProducts { get; set; } = null!;
    public DbSet<QuotationServiceLine> QuotationServiceLines { get; set; } = null!;
    public DbSet<SalesPicklistValue> SalesPicklists { get; set; } = null!;
    public DbSet<Payment> Payments { get; set; } = null!;
    public DbSet<AppDefinition> Applications { get; set; } = null!;
    public DbSet<AppConfig> AppConfigs { get; set; } = null!;
    public DbSet<License> Licenses { get; set; } = null!;
    public DbSet<TenantModule> TenantModules { get; set; } = null!;
    public DbSet<TenantModuleCatalog> TenantModuleCatalogs { get; set; } = null!;
    public DbSet<PlatformRole> PlatformRoles { get; set; } = null!;
    public DbSet<PlatformRoleModule> PlatformRoleModules { get; set; } = null!;

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformUser>(b =>
        {
            b.ToTable("PlatformUsers");
            b.HasIndex(x => x.Email).IsUnique();
            b.Property(x => x.Status).HasDefaultValueSql("'Active'");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("SYSUTCDATETIME()");
            b.HasOne(x => x.PlatformRole).WithMany().HasForeignKey(x => x.PlatformRoleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PlatformRole>(b =>
        {
            b.ToTable("PlatformRoles");
            b.HasIndex(x => x.RoleName).IsUnique();
            b.Property(x => x.IsActive).HasDefaultValueSql("1");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("SYSUTCDATETIME()");
        });

        modelBuilder.Entity<PlatformRoleModule>(b =>
        {
            b.ToTable("PlatformRoleModules");
            b.HasIndex(x => new { x.PlatformRoleId, x.ModuleId }).IsUnique();
            b.HasOne(x => x.PlatformRole).WithMany().HasForeignKey(x => x.PlatformRoleId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Module).WithMany().HasForeignKey(x => x.ModuleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ServiceCategory>(b =>
        {
            b.ToTable("ServiceCategories");
            b.HasIndex(x => x.CategoryName).IsUnique();
            b.Property(x => x.IsActive).HasDefaultValueSql("1");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("SYSUTCDATETIME()");
        });

        modelBuilder.Entity<ServiceSubCategory>(b =>
        {
            b.ToTable("ServiceSubCategories");
            b.HasIndex(x => new { x.CategoryId, x.SubCategoryName }).IsUnique();
            b.Property(x => x.IsActive).HasDefaultValueSql("1");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("SYSUTCDATETIME()");
            b.HasOne(x => x.ServiceCategory).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        // Real table is "Services" (confirmed against the actual column list
        // this time - see ServiceItem.cs's class summary).
        modelBuilder.Entity<ServiceItem>(b =>
        {
            b.ToTable("Services");
            b.Property(x => x.IsActive).HasDefaultValueSql("1");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("SYSUTCDATETIME()");
            b.HasOne(x => x.ServiceCategory).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.ServiceSubCategory).WithMany().HasForeignKey(x => x.SubCategoryId).OnDelete(DeleteBehavior.Restrict);
        });

        // Enquiry/FollowUp map to the real dbo.SalesEnquiries/dbo.SalesFollowUps
        // tables (see the CREATE TABLE script the user supplied). CategoryId/
        // SubCategoryId/CustomerId/AssignedToEmployeeId/EmployeeId are plain
        // columns there, not enforced FKs - that table's own convention is an
        // "id + *Snapshot name" pair, so no HasOne()/navigation config for
        // those. The only real FK - SalesFollowUps -> SalesEnquiries - is
        // configured below.
        modelBuilder.Entity<Enquiry>(b =>
        {
            b.ToTable("SalesEnquiries");
            b.Property(x => x.EnquiryDate).HasColumnType("date");
            b.Property(x => x.NextFollowUpDate).HasColumnType("date");
            b.Property(x => x.CustomerType).HasDefaultValueSql("'Individual'");
            b.Property(x => x.EstimatedAmount).HasDefaultValueSql("(0)");
            b.Property(x => x.Priority).HasDefaultValueSql("'Normal'");
            b.Property(x => x.Status).HasDefaultValueSql("'New'");
            b.Property(x => x.IsActive).HasDefaultValueSql("(1)");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("sysutcdatetime()");
        });

        // EnquiryRequirementProduct - see EnquiryRequirementProduct.cs.
        // Requirements are a plain multi-select of Products (Applications) -
        // no nested service lines/pricing at this stage anymore (that used
        // to be EnquiryRequirementService, now removed - see the entity's
        // doc comment). New table - see
        // create-sales-enquiry-requirement-products.sql, which also drops
        // the now-unused dbo.SalesEnquiryRequirements and
        // dbo.EnquiryRequirementServices tables. Deleting the parent Enquiry
        // removes its requirement products (cascade, same as SalesFollowUps/
        // Quotations below); the FK to Applications is Restrict, same
        // convention as every other master-data reference in this context.
        modelBuilder.Entity<EnquiryRequirementProduct>(b =>
        {
            b.ToTable("EnquiryRequirementProducts");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("sysutcdatetime()");
            b.HasOne(x => x.Enquiry).WithMany().HasForeignKey(x => x.SalesEnquiryId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Application).WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FollowUp>(b =>
        {
            b.ToTable("SalesFollowUps");
            b.Property(x => x.FollowUpDate).HasColumnType("date");
            b.Property(x => x.NextFollowUpDate).HasColumnType("date");
            b.Property(x => x.Status).HasDefaultValueSql("'Pending'");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("sysutcdatetime()");
            // Deleting an enquiry removes its follow-ups too (real ON DELETE
            // CASCADE on FK_SalesFollowUps_Enquiry in the actual table).
            b.HasOne(x => x.Enquiry).WithMany().HasForeignKey(x => x.SalesEnquiryId).OnDelete(DeleteBehavior.Cascade);
        });

        // Lead - see Lead.cs. New table, not part of the original schema
        // dump - see create-sales-leads.sql. Deleting the parent Enquiry
        // removes its leads too (cascade, same convention as FollowUps/
        // Quotations). The FollowUp relationship is deliberately NoAction,
        // NOT SetNull/Cascade - SQL Server refuses a second cascading path
        // into the same table once Enquiry -> SalesLeads already cascades
        // (error 1785, hit for real setting this up), so the API clears
        // SalesFollowUpId itself (PlatformSalesController.DeleteFollowUp())
        // before deleting a Follow-up that has a converted Lead, rather
        // than leaning on the database to do it.
        modelBuilder.Entity<Lead>(b =>
        {
            b.ToTable("SalesLeads");
            b.Property(x => x.Status).HasDefaultValueSql("'New'");
            b.Property(x => x.ConvertedDate).HasDefaultValueSql("sysutcdatetime()");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("sysutcdatetime()");
            b.HasOne(x => x.Enquiry).WithMany().HasForeignKey(x => x.SalesEnquiryId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.FollowUp).WithMany().HasForeignKey(x => x.SalesFollowUpId).OnDelete(DeleteBehavior.NoAction);
        });

        // Quotation / QuotationProduct / QuotationServiceLine - see
        // Quotation.cs. New tables, not part of the original schema dump -
        // see create-quotations.sql. Deleting the parent Enquiry removes its
        // quotations too (cascade, same as EnquiryRequirements/FollowUps
        // above); deleting a Quotation removes its Products, and deleting a
        // Product removes its Service lines - the whole tree cascades from
        // the Enquiry down. The FK to Applications is Restrict, same
        // convention as every other master-data reference in this context.
        modelBuilder.Entity<Quotation>(b =>
        {
            b.ToTable("Quotations");
            b.Property(x => x.Status).HasDefaultValueSql("'Draft'");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("sysutcdatetime()");
            b.HasOne(x => x.Enquiry).WithMany().HasForeignKey(x => x.SalesEnquiryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<QuotationProduct>(b =>
        {
            b.ToTable("QuotationProducts");
            b.HasOne(x => x.Quotation).WithMany().HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Application).WithMany().HasForeignKey(x => x.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<QuotationServiceLine>(b =>
        {
            b.ToTable("QuotationServiceLines");
            b.Property(x => x.Qty).HasDefaultValueSql("(1)");
            b.Property(x => x.VatPercent).HasDefaultValueSql("(0)");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("sysutcdatetime()");
            b.HasOne(x => x.QuotationProduct).WithMany().HasForeignKey(x => x.QuotationProductId).OnDelete(DeleteBehavior.Cascade);
        });

        // Sales picklists (Enquiry Status, Enquiry Priority, ...) - replaces
        // hardcoded TypeScript arrays. Not an enforced FK from anywhere
        // (Enquiry.Status/Priority are plain columns) - see
        // SalesPicklistValue.cs for the CHECK-constraint caveat.
        modelBuilder.Entity<SalesPicklistValue>(b =>
        {
            b.ToTable("SalesPicklists");
            b.HasIndex(x => new { x.ListName, x.Code }).IsUnique();
            b.Property(x => x.IsActive).HasDefaultValueSql("1");
            b.Property(x => x.DisplayOrder).HasDefaultValueSql("0");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("sysutcdatetime()");
        });

        // Payment - see Payment.cs for the exactly-one-of QuotationId/
        // LicenseId rule. All three FKs are Restrict (not Cascade) - a
        // Payment is a financial record, it should never silently
        // disappear because someone deleted the Tenant/Quotation/License it
        // was recorded against; the delete has to be blocked instead. This
        // also means these FKs never contribute to a SQL Server "multiple
        // cascade paths" conflict (only Cascade FKs do - see the Lead/
        // FollowUp NoAction workaround above), so no special-casing needed
        // here despite Quotation already being on a Cascade path from
        // Enquiry.
        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("Payments", t => t.HasCheckConstraint(
                "CK_Payments_ExactlyOneTarget",
                "([QuotationId] IS NOT NULL AND [LicenseId] IS NULL) OR ([QuotationId] IS NULL AND [LicenseId] IS NOT NULL)"));
            b.Property(x => x.Status).HasDefaultValueSql("'Completed'");
            // The real dbo.Payments table (it pre-dates this project's SQL-script
            // convention, so there's no CREATE script for it) names this column
            // PaymentDate, not the plain Date the entity assumes by convention -
            // same class of mismatch as AppDefinition's ApplicationCode/
            // ApplicationName below.
            b.Property(x => x.Date).HasColumnName("PaymentDate");
            // The real column for a payment's method is PaymentMethodId (a
            // plain app-owned int, no FK to any lookup table - confirmed
            // against the real CREATE TABLE script), not the free-text Method
            // the entity used to assume. PaymentMethodId is mapped by
            // convention (property name matches the column exactly, no
            // override needed here) and is what actually persists; Method
            // itself is [NotMapped] on the entity now - a thin pass-through
            // over PaymentMethodId via PaymentMethodCodes - so no explicit
            // Ignore() is needed for it either. See Payment.cs.
            b.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.Quotation).WithMany().HasForeignKey(x => x.QuotationId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.License).WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AppDefinition>(b =>
        {
            b.ToTable("Applications");
            // The real dbo.Applications table names these columns ApplicationCode/
            // ApplicationName, not the plain Code/Name the entity uses in C# - see
            // AppDefinition.cs's doc comment for why (the earlier "Name"/"Description"
            // assumption didn't match the live table at all and broke every read).
            b.Property(x => x.Code).HasColumnName("ApplicationCode");
            b.Property(x => x.Name).HasColumnName("ApplicationName");
            b.Property(x => x.IsActive).HasDefaultValueSql("1");
        });

        modelBuilder.Entity<AppConfig>(b =>
        {
            b.ToTable("AppConfigs");
            b.HasIndex(x => x.TenantId).IsUnique();
            b.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<License>(b =>
        {
            b.ToTable("Licenses");
            b.HasIndex(x => x.LicenseKey).IsUnique();
            b.Property(x => x.IsActive).HasDefaultValueSql("1");
            b.Property(x => x.RenewalAmount).HasDefaultValueSql("(0)");
            b.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        // IsEnabled/DisabledDate/ModifiedDate are real dbo.TenantModules
        // columns that pre-date this fix - the old TenantModule.cs simply
        // didn't map them, and used row-existence (insert/delete) as a
        // stand-in for "enabled". Toggling now flips IsEnabled on the row
        // instead - see PlatformTenantModulesController. TenantModuleCatalogId
        // replaced the old ModuleId (-> dbo.Modules) FK - see
        // TenantModule.cs/TenantModuleCatalog.cs and
        // tenant-module-catalog-split.sql for why. The unique index is
        // filtered (WHERE TenantModuleCatalogId IS NOT NULL) because SQL
        // Server treats multiple NULLs as duplicates in a plain unique
        // index, which would otherwise cap each tenant at exactly one
        // never-assigned row.
        modelBuilder.Entity<TenantModule>(b =>
        {
            b.ToTable("TenantModules");
            b.HasIndex(x => new { x.TenantId, x.TenantModuleCatalogId }).IsUnique().HasFilter("[TenantModuleCatalogId] IS NOT NULL");
            b.Property(x => x.IsEnabled).HasDefaultValueSql("1");
            b.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.TenantModuleCatalog).WithMany().HasForeignKey(x => x.TenantModuleCatalogId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantModuleCatalog>(b =>
        {
            b.ToTable("TenantModuleCatalog");
            b.HasIndex(x => x.ModuleCode).IsUnique();
            b.Property(x => x.IsActive).HasDefaultValueSql("1");
            b.Property(x => x.DisplayOrder).HasDefaultValueSql("0");
            b.Property(x => x.IsCore).HasDefaultValueSql("0");
            b.Property(x => x.CreatedDate).HasDefaultValueSql("SYSUTCDATETIME()");
            b.HasOne(x => x.ParentTenantModuleCatalog).WithMany().HasForeignKey(x => x.ParentTenantModuleCatalogId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
