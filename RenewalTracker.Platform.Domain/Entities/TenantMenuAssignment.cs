using System;
using System.ComponentModel.DataAnnotations;

namespace RenewalTracker.Platform.Domain.Entities;

/// <summary>
/// One Business Panel menu (dbo.TenantMenuItems) given to one tenant - chosen on
/// Provision tenant (Modules and menus). Table dbo.TenantMenuAssignments, unique
/// (TenantId, MenuItemId). See DatabaseScripts/TenantMenuAssignments_Schema.sql.
/// </summary>
public class TenantMenuAssignment
{
    [Key]
    public int TenantMenuAssignmentId { get; set; }

    public int TenantId { get; set; }

    public int MenuItemId { get; set; }

    public DateTime CreatedDate { get; set; }
}
