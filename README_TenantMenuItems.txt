TenantMenuItems refactor
========================

The Platform API now exposes CRUD at:
  /api/platform/tenant-menu-items

The EF entity maps to SuperAdminPanel.dbo.TenantMenuItems and uses MenuItemId
as the identity key. The SQL script in DatabaseScripts creates the requested
15-column table when absent, adds a unique MenuKey index, and optionally
imports missing menu keys from BusinessPanel.dbo.MenuItems while remapping
parent links by MenuKey.

The appsettings connection strings were changed from RenewalTrackerPlatform1
to SuperAdminPanel to match the requested database. Verify your SQL Server
instance and deployment-specific connection strings before running.

Build verification note: .NET SDK was not available in the execution
environment, so this source package has not been dotnet-built here.
