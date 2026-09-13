using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Sales;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Sales ▸ Enquiries / Requirements / Follow-ups - Bliss Point Group's own
/// sales pipeline for onboarding new tenant businesses. Platform-owned,
/// hand-written against PlatformDbContext. Talks to the real
/// dbo.SalesEnquiries / dbo.SalesFollowUps tables (see Enquiry.cs /
/// FollowUp.cs) plus the new dbo.EnquiryRequirementProducts table (see
/// EnquiryRequirementProduct.cs) - see SalesEnquiriesComponent/
/// SalesFollowUpsComponent for the Angular side.
///
/// Requirements are a plain multi-select of Products (dbo.Applications) a
/// customer is interested in - never the Service Catalog (dbo.Services),
/// which is not used by Requirements at all - see GetRequirements()/
/// SaveRequirements(). There is no pricing/service breakdown at this stage
/// (that's a Quotation's job - see PlatformQuotationsController); the
/// enquiry's own CategoryId/SubCategoryId/EstimatedAmount fields are no
/// longer set from this UI at all and are left untouched by every endpoint
/// here.
///
/// CategoryId/SubCategoryId/AssignedToEmployeeId/EmployeeId are plain
/// columns on those tables, not enforced foreign keys - the table's own
/// convention is to snapshot the display name alongside the id
/// (CategoryNameSnapshot, AssignedToEmployeeNameSnapshot, ...) so history
/// reads correctly even if the category/employee is later renamed or
/// removed. This controller resolves those snapshot names itself on
/// create/update, from the current ServiceCategories/ServiceSubCategories/
/// PlatformUsers tables.
/// </summary>
[ApiController]
[Route("api/platform/sales")]
[RequirePlatformAuth]
public class PlatformSalesController : ControllerBase
{
    private readonly PlatformDbContext _db;

    public PlatformSalesController(PlatformDbContext db)
    {
        _db = db;
    }

    // ----- Enquiries -----

    [HttpGet("enquiries")]
    public async Task<ActionResult<List<EnquiryDto>>> GetEnquiries()
    {
        // Tracked (not AsNoTracking) - the self-heal pass below may need to
        // save corrections back onto these same entities.
        var rows = await _db.Enquiries.OrderBy(e => e.CustomerNameSnapshot).ToListAsync();
        // The list grid's "Products" column shows the picked Application names for each enquiry.
        var requirementRows = await _db.EnquiryRequirementProducts.AsNoTracking()
            .OrderBy(p => p.EnquiryRequirementProductId)
            .ToListAsync();
        var productsByEnquiry = requirementRows
            .GroupBy(p => p.SalesEnquiryId)
            .ToDictionary(g => g.Key, g => g.Select(p => p.ApplicationNameSnapshot).ToList());

        await SelfHealEnquiryStatusesAsync(rows);

        return Ok(rows.Select(e => ToDto(e, productsByEnquiry.GetValueOrDefault(e.SalesEnquiryId) ?? new List<string>())).ToList());
    }

    /// <summary>
    /// Brings every enquiry's Status in line with whichever of its
    /// follow-ups is latest, same rule as
    /// SyncEnquiryStatusFromLatestFollowUpAsync - but as a batch pass over
    /// everything GetEnquiries is about to return, so follow-ups that
    /// existed before that sync was added (or any other way Status and
    /// follow-ups could have drifted apart) get corrected the next time the
    /// Sales Enquiries screen loads, without waiting for someone to open
    /// and re-save each one's follow-ups by hand.
    /// </summary>
    private async Task SelfHealEnquiryStatusesAsync(List<Enquiry> enquiries)
    {
        var allFollowUps = await _db.FollowUps.AsNoTracking().ToListAsync();
        var latestByEnquiry = allFollowUps
            .GroupBy(f => f.SalesEnquiryId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(f => f.NextFollowUpDate ?? f.FollowUpDate).ThenByDescending(f => f.SalesFollowUpId).First());

        var changed = false;
        foreach (var enquiry in enquiries)
        {
            if (latestByEnquiry.TryGetValue(enquiry.SalesEnquiryId, out var latest) && enquiry.Status != latest.Status)
            {
                enquiry.Status = latest.Status;
                enquiry.ModifiedDate = DateTime.UtcNow;
                changed = true;
            }
        }
        if (changed) await _db.SaveChangesAsync();
    }

    [HttpPost("enquiries")]
    public async Task<ActionResult<EnquiryDto>> CreateEnquiry(EnquiryDto dto)
    {
        var entity = new Enquiry
        {
            EnquiryDate = DateTime.UtcNow.Date,
            CreatedDate = DateTime.UtcNow,
        };
        await ApplyAsync(entity, dto);
        _db.Enquiries.Add(entity);
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity, new List<string>()));
    }

    [HttpPut("enquiries/{id:int}")]
    public async Task<ActionResult<EnquiryDto>> UpdateEnquiry(int id, EnquiryDto dto)
    {
        var entity = await _db.Enquiries.FirstOrDefaultAsync(e => e.SalesEnquiryId == id);
        if (entity == null) return NotFound();

        await ApplyAsync(entity, dto);
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var products = await _db.EnquiryRequirementProducts.AsNoTracking()
            .Where(p => p.SalesEnquiryId == id)
            .OrderBy(p => p.EnquiryRequirementProductId)
            .Select(p => p.ApplicationNameSnapshot)
            .ToListAsync();
        return Ok(ToDto(entity, products));
    }

    /// <summary>Also removes this enquiry's follow-ups and requirement products (FK cascade - see PlatformDbContext.Platform.cs).</summary>
    [HttpDelete("enquiries/{id:int}")]
    public async Task<IActionResult> DeleteEnquiry(int id)
    {
        var entity = await _db.Enquiries.FirstOrDefaultAsync(e => e.SalesEnquiryId == id);
        if (entity == null) return NotFound();

        _db.Enquiries.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // ----- Requirements (plain multi-select of Products - no pricing at this stage) -----

    [HttpGet("enquiries/{enquiryId:int}/requirements")]
    public async Task<ActionResult<List<EnquiryRequirementProductDto>>> GetRequirements(int enquiryId)
    {
        if (!await _db.Enquiries.AnyAsync(e => e.SalesEnquiryId == enquiryId)) return NotFound();

        var products = await _db.EnquiryRequirementProducts.AsNoTracking()
            .Where(p => p.SalesEnquiryId == enquiryId)
            .OrderBy(p => p.EnquiryRequirementProductId)
            .ToListAsync();
        return Ok(products.Select(ToDto).ToList());
    }

    /// <summary>
    /// Replaces the enquiry's whole set of picked Products in one call - a
    /// plain multi-select, so the only thing to diff is which ApplicationIds
    /// are posted: anything already linked but missing from the posted list
    /// is unlinked, and anything posted that isn't already linked is added.
    /// ApplicationName is always re-resolved from the current Applications
    /// catalog. Deliberately does not touch the Enquiry's EstimatedAmount -
    /// there's no pricing at this stage (see the class doc comment).
    /// </summary>
    [HttpPut("enquiries/{enquiryId:int}/requirements")]
    public async Task<ActionResult<List<EnquiryRequirementProductDto>>> SaveRequirements(int enquiryId, List<int> applicationIds)
    {
        if (!await _db.Enquiries.AnyAsync(e => e.SalesEnquiryId == enquiryId)) return NotFound();

        var distinctIds = applicationIds.Distinct().ToList();
        var apps = await _db.Applications.AsNoTracking()
            .Where(a => distinctIds.Contains(a.ApplicationId))
            .ToDictionaryAsync(a => a.ApplicationId);
        if (distinctIds.Any(id => !apps.ContainsKey(id)))
        {
            return BadRequest(new { message = "One of the selected products no longer exists." });
        }

        var existing = await _db.EnquiryRequirementProducts.Where(p => p.SalesEnquiryId == enquiryId).ToListAsync();

        foreach (var stale in existing.Where(p => !distinctIds.Contains(p.ApplicationId)))
        {
            _db.EnquiryRequirementProducts.Remove(stale);
        }

        foreach (var id in distinctIds.Where(id => !existing.Any(p => p.ApplicationId == id)))
        {
            _db.EnquiryRequirementProducts.Add(new EnquiryRequirementProduct
            {
                SalesEnquiryId = enquiryId,
                ApplicationId = id,
                ApplicationNameSnapshot = apps[id].Name,
                CreatedDate = DateTime.UtcNow,
            });
        }

        await _db.SaveChangesAsync();

        var saved = await _db.EnquiryRequirementProducts.AsNoTracking()
            .Where(p => p.SalesEnquiryId == enquiryId)
            .OrderBy(p => p.EnquiryRequirementProductId)
            .ToListAsync();
        return Ok(saved.Select(ToDto).ToList());
    }

    // ----- Follow-ups -----

    [HttpGet("follow-ups")]
    public async Task<ActionResult<List<FollowUpDto>>> GetFollowUps()
    {
        var rows = await _db.FollowUps.AsNoTracking().OrderBy(f => f.NextFollowUpDate).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    [HttpPost("follow-ups")]
    public async Task<ActionResult<FollowUpDto>> CreateFollowUp(FollowUpDto dto)
    {
        if (!await _db.Enquiries.AnyAsync(e => e.SalesEnquiryId == dto.EnquiryId))
        {
            return BadRequest(new { message = "The selected enquiry does not exist." });
        }

        var entity = new FollowUp
        {
            SalesEnquiryId = dto.EnquiryId,
            FollowUpDate = DateTime.UtcNow.Date,
            CreatedDate = DateTime.UtcNow,
        };
        await ApplyAsync(entity, dto);
        _db.FollowUps.Add(entity);
        await _db.SaveChangesAsync();
        await SyncEnquiryStatusFromLatestFollowUpAsync(entity.SalesEnquiryId);
        return Ok(ToDto(entity));
    }

    [HttpPut("follow-ups/{id:int}")]
    public async Task<ActionResult<FollowUpDto>> UpdateFollowUp(int id, FollowUpDto dto)
    {
        var entity = await _db.FollowUps.FirstOrDefaultAsync(f => f.SalesFollowUpId == id);
        if (entity == null) return NotFound();

        if (!await _db.Enquiries.AnyAsync(e => e.SalesEnquiryId == dto.EnquiryId))
        {
            return BadRequest(new { message = "The selected enquiry does not exist." });
        }

        var previousEnquiryId = entity.SalesEnquiryId;
        entity.SalesEnquiryId = dto.EnquiryId;
        await ApplyAsync(entity, dto);
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Re-sync whichever enquiry(s) this follow-up affects - almost
        // always just its own enquiry, but the standalone Follow-ups
        // screen's Enquiry field is a free-text autocomplete that can move a
        // follow-up onto a different enquiry entirely, in which case the
        // enquiry it just LEFT also needs its status recomputed from
        // whatever follow-up is now its latest.
        await SyncEnquiryStatusFromLatestFollowUpAsync(entity.SalesEnquiryId);
        if (previousEnquiryId != entity.SalesEnquiryId)
        {
            await SyncEnquiryStatusFromLatestFollowUpAsync(previousEnquiryId);
        }
        return Ok(ToDto(entity));
    }

    /// <summary>
    /// Deleting a Follow-up that already has a converted Lead (see
    /// ConvertFollowUpToLead()) must first clear that Lead's
    /// SalesFollowUpId - FK_SalesLeads_FollowUp is NO ACTION (SQL Server
    /// disallows a second cascade/SET NULL path alongside the Enquiry ->
    /// SalesLeads cascade), so nothing does this for us automatically. The
    /// Lead itself is untouched otherwise - it's a snapshot, not a live
    /// pointer back to this Follow-up.
    /// </summary>
    [HttpDelete("follow-ups/{id:int}")]
    public async Task<IActionResult> DeleteFollowUp(int id)
    {
        var entity = await _db.FollowUps.FirstOrDefaultAsync(f => f.SalesFollowUpId == id);
        if (entity == null) return NotFound();

        var linkedLeads = await _db.Leads.Where(l => l.SalesFollowUpId == id).ToListAsync();
        foreach (var lead in linkedLeads) lead.SalesFollowUpId = null;

        var enquiryId = entity.SalesEnquiryId;
        _db.FollowUps.Remove(entity);
        await _db.SaveChangesAsync();
        // Whichever follow-up is now the latest for this enquiry (if any
        // are left) becomes the enquiry's status again - see
        // SyncEnquiryStatusFromLatestFollowUpAsync.
        await SyncEnquiryStatusFromLatestFollowUpAsync(enquiryId);
        return NoContent();
    }

    /// <summary>
    /// Converts one Follow-up into a Lead - the only way a Lead is created
    /// (see LeadDto's doc comment). Idempotent: re-converting a Follow-up
    /// that already has a Lead just returns that same Lead rather than
    /// making a duplicate (UQ_SalesLeads_SalesFollowUpId backs this too).
    /// Customer/Mobile/Email are copied from the parent Enquiry, Assigned to
    /// from the Follow-up itself, as a point-in-time snapshot - editing the
    /// Enquiry or Follow-up afterwards does not change an already-converted
    /// Lead (see LeadDto's PUT doc comment).
    /// </summary>
    [HttpPost("follow-ups/{id:int}/convert-to-lead")]
    public async Task<ActionResult<LeadDto>> ConvertFollowUpToLead(int id)
    {
        var followUp = await _db.FollowUps.FirstOrDefaultAsync(f => f.SalesFollowUpId == id);
        if (followUp == null) return NotFound();

        var existing = await _db.Leads.FirstOrDefaultAsync(l => l.SalesFollowUpId == id);
        if (existing != null) return Ok(ToDto(existing));

        var enquiry = await _db.Enquiries.FirstOrDefaultAsync(e => e.SalesEnquiryId == followUp.SalesEnquiryId);
        if (enquiry == null) return BadRequest(new { message = "This follow-up's enquiry no longer exists." });

        var lead = new Lead
        {
            SalesEnquiryId = enquiry.SalesEnquiryId,
            SalesFollowUpId = followUp.SalesFollowUpId,
            LeadNo = await NextLeadNoAsync(),
            CustomerNameSnapshot = enquiry.CustomerNameSnapshot,
            Mobile = enquiry.Mobile,
            Email = enquiry.Email,
            EmployeeId = followUp.EmployeeId,
            EmployeeNameSnapshot = followUp.EmployeeNameSnapshot,
            Status = "New",
            Notes = followUp.Notes,
            ConvertedDate = DateTime.UtcNow,
            CreatedDate = DateTime.UtcNow,
        };
        _db.Leads.Add(lead);
        await _db.SaveChangesAsync();

        // Log the conversion itself as a real Follow-up entry (not just a
        // display-only badge) - see alter-enquirystatus-add-conversion-codes.sql
        // for the new "Converted to Lead" EnquiryStatus code this writes.
        // Flowing it through SyncEnquiryStatusFromLatestFollowUpAsync means
        // the parent Enquiry's own Status genuinely becomes "Converted to
        // Lead" (same rule as every other follow-up-driven status change),
        // and it shows up in that enquiry's Follow-ups history/timeline.
        _db.FollowUps.Add(new FollowUp
        {
            SalesEnquiryId = enquiry.SalesEnquiryId,
            FollowUpDate = DateTime.UtcNow.Date,
            FollowUpType = followUp.FollowUpType,
            EmployeeId = followUp.EmployeeId,
            EmployeeNameSnapshot = followUp.EmployeeNameSnapshot,
            Status = "Converted to Lead",
            Notes = $"Converted to Lead {lead.LeadNo}.",
            NextFollowUpDate = DateTime.UtcNow.Date,
            CreatedDate = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();
        await SyncEnquiryStatusFromLatestFollowUpAsync(enquiry.SalesEnquiryId);

        return Ok(ToDto(lead));
    }

    // ----- Leads -----

    [HttpGet("leads")]
    public async Task<ActionResult<List<LeadDto>>> GetLeads()
    {
        var rows = await _db.Leads.AsNoTracking().OrderByDescending(l => l.ConvertedDate).ToListAsync();
        return Ok(rows.Select(ToDto).ToList());
    }

    /// <summary>Only ever changes Assigned to/Status/Notes - see LeadDto's doc comment.</summary>
    [HttpPut("leads/{id:int}")]
    public async Task<ActionResult<LeadDto>> UpdateLead(int id, LeadDto dto)
    {
        var entity = await _db.Leads.FirstOrDefaultAsync(l => l.LeadId == id);
        if (entity == null) return NotFound();

        entity.Status = dto.Status;
        entity.Notes = dto.Notes;
        entity.EmployeeId = dto.EmployeeId;
        entity.EmployeeNameSnapshot = dto.EmployeeId is int employeeId
            ? EmployeeName(await _db.PlatformUsers.AsNoTracking().FirstOrDefaultAsync(u => u.PlatformUserId == employeeId))
            : dto.AssignedTo;
        entity.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(entity));
    }

    [HttpDelete("leads/{id:int}")]
    public async Task<IActionResult> DeleteLead(int id)
    {
        var entity = await _db.Leads.FirstOrDefaultAsync(l => l.LeadId == id);
        if (entity == null) return NotFound();

        _db.Leads.Remove(entity);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>LD-000001, LD-000002, ... - same simple incrementing scheme as Quotation's QT- numbers.</summary>
    private async Task<string> NextLeadNoAsync()
    {
        var maxId = await _db.Leads.MaxAsync(l => (int?)l.LeadId) ?? 0;
        return $"LD-{maxId + 1:D6}";
    }

    /// <summary>
    /// Keeps Enquiry.Status mirroring whichever Follow-up logged against it
    /// has the latest NextFollowUpDate (falling back to FollowUpDate when
    /// that's not set, same fallback ToDto(FollowUp) already uses for the
    /// Angular side's "nextDate" - so "latest" here matches whatever the UI
    /// itself shows as each follow-up's date) - ties broken by the higher
    /// SalesFollowUpId, i.e. whichever was logged more recently. Called
    /// after every Follow-up Create/Update/Delete, from every entry point
    /// (Sales Enquiries' own popup+nested table, and the standalone
    /// Sales ▸ Follow-ups screen alike) so the two screens can never drift
    /// out of sync with each other. Leaves Enquiry.Status untouched when
    /// the enquiry has no follow-ups left at all (e.g. its only follow-up
    /// was just deleted) - there's no "reset to New" rule requested, and
    /// silently reverting to New would be a bigger surprise than just
    /// leaving the last known status in place.
    /// </summary>
    private async Task SyncEnquiryStatusFromLatestFollowUpAsync(int enquiryId)
    {
        var latest = await _db.FollowUps
            .Where(f => f.SalesEnquiryId == enquiryId)
            .OrderByDescending(f => f.NextFollowUpDate ?? f.FollowUpDate)
            .ThenByDescending(f => f.SalesFollowUpId)
            .FirstOrDefaultAsync();
        if (latest == null) return;

        var enquiry = await _db.Enquiries.FirstOrDefaultAsync(e => e.SalesEnquiryId == enquiryId);
        if (enquiry == null || enquiry.Status == latest.Status) return;

        enquiry.Status = latest.Status;
        enquiry.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ----- DTO <-> entity mapping -----

    /// <summary>
    /// Copies the editable fields from the DTO onto the entity, resolving
    /// CategoryNameSnapshot/SubCategoryNameSnapshot/AssignedToEmployeeNameSnapshot
    /// against the current ServiceCategories/ServiceSubCategories/PlatformUsers
    /// tables. Falls back to whatever text the Angular autocomplete sent in
    /// dto.AssignedTo when the employee wasn't resolved to an id (a typed
    /// name that doesn't match any employee), so "Assigned to" never blocks a save.
    ///
    /// Deliberately does NOT touch entity.EstimatedAmount - Requirements no
    /// longer carry any pricing (see the class doc comment), so Amount is
    /// left exactly as it already is in the database, whatever dto.Amount
    /// says (the Angular form no longer collects it and always sends 0).
    /// </summary>
    private async Task ApplyAsync(Enquiry entity, EnquiryDto dto)
    {
        entity.EnquiryNo = dto.EnquiryNo;
        entity.CustomerId = dto.CustomerId;
        entity.CustomerNameSnapshot = dto.Customer;
        entity.ContactPerson = dto.Contact;
        entity.Mobile = dto.Mobile;
        entity.WhatsApp = dto.WhatsApp;
        entity.Email = dto.Email;
        entity.CustomerType = string.IsNullOrWhiteSpace(dto.CustomerType) ? "Individual" : dto.CustomerType;
        entity.Source = dto.Source;
        entity.ServiceIdsCsv = dto.ServiceIdsCsv;
        entity.ServiceNamesSnapshot = dto.ServiceNamesSnapshot;
        entity.ServiceDetailsJson = dto.ServiceDetailsJson;
        entity.Requirement = dto.Requirement;
        entity.Priority = dto.Priority;
        entity.Status = dto.Status;
        entity.NextFollowUpDate = dto.NextFollowUpDate;
        entity.Notes = dto.Notes;
        entity.IsActive = dto.IsActive;

        entity.CategoryId = dto.CategoryId;
        entity.CategoryNameSnapshot = dto.CategoryId is int categoryId
            ? (await _db.ServiceCategories.AsNoTracking().FirstOrDefaultAsync(c => c.CategoryId == categoryId))?.CategoryName
            : null;

        entity.SubCategoryId = dto.SubCategoryId;
        entity.SubCategoryNameSnapshot = dto.SubCategoryId is int subCategoryId
            ? (await _db.ServiceSubCategories.AsNoTracking().FirstOrDefaultAsync(s => s.SubCategoryId == subCategoryId))?.SubCategoryName
            : null;

        entity.AssignedToEmployeeId = dto.AssignedToEmployeeId;
        entity.AssignedToEmployeeNameSnapshot = dto.AssignedToEmployeeId is int employeeId
            ? EmployeeName(await _db.PlatformUsers.AsNoTracking().FirstOrDefaultAsync(u => u.PlatformUserId == employeeId))
            : dto.AssignedTo;
    }

    private async Task ApplyAsync(FollowUp entity, FollowUpDto dto)
    {
        entity.FollowUpType = dto.Type;
        entity.Status = dto.Status;
        entity.NextFollowUpDate = dto.NextDate;
        entity.Notes = dto.Notes;

        entity.EmployeeId = dto.EmployeeId;
        entity.EmployeeNameSnapshot = dto.EmployeeId is int employeeId
            ? EmployeeName(await _db.PlatformUsers.AsNoTracking().FirstOrDefaultAsync(u => u.PlatformUserId == employeeId))
            : dto.AssignedTo;
    }

    private static string? EmployeeName(PlatformUser? u) =>
        u == null ? null : string.Join(" ", new[] { u.FirstName, u.LastName }.Where(n => !string.IsNullOrWhiteSpace(n)));

    private static EnquiryDto ToDto(Enquiry e, List<string> products) => new()
    {
        Id = e.SalesEnquiryId,
        EnquiryNo = e.EnquiryNo,
        EnquiryDate = e.EnquiryDate,
        CustomerId = e.CustomerId,
        Customer = e.CustomerNameSnapshot,
        Contact = e.ContactPerson,
        Mobile = e.Mobile,
        WhatsApp = e.WhatsApp,
        Email = e.Email,
        CustomerType = e.CustomerType,
        Source = e.Source,
        CategoryId = e.CategoryId,
        SubCategoryId = e.SubCategoryId,
        ServiceIdsCsv = e.ServiceIdsCsv,
        ServiceNamesSnapshot = e.ServiceNamesSnapshot,
        ServiceDetailsJson = e.ServiceDetailsJson,
        Requirement = e.Requirement,
        Amount = e.EstimatedAmount,
        RequirementCount = products.Count,
        Products = products,
        AssignedToEmployeeId = e.AssignedToEmployeeId,
        AssignedTo = e.AssignedToEmployeeNameSnapshot,
        Priority = e.Priority,
        Status = e.Status,
        NextFollowUpDate = e.NextFollowUpDate,
        Notes = e.Notes,
        IsActive = e.IsActive,
        CreatedDate = e.CreatedDate,
    };

    private static EnquiryRequirementProductDto ToDto(EnquiryRequirementProduct p) => new()
    {
        Id = p.EnquiryRequirementProductId,
        ApplicationId = p.ApplicationId,
        ApplicationName = p.ApplicationNameSnapshot,
    };

    private static FollowUpDto ToDto(FollowUp f) => new()
    {
        Id = f.SalesFollowUpId,
        EnquiryId = f.SalesEnquiryId,
        Type = f.FollowUpType,
        EmployeeId = f.EmployeeId,
        AssignedTo = f.EmployeeNameSnapshot,
        Status = f.Status,
        NextDate = f.NextFollowUpDate ?? f.FollowUpDate,
        Notes = f.Notes,
        CreatedDate = f.CreatedDate,
    };

    private static LeadDto ToDto(Lead l) => new()
    {
        Id = l.LeadId,
        EnquiryId = l.SalesEnquiryId,
        FollowUpId = l.SalesFollowUpId,
        LeadNo = l.LeadNo,
        Customer = l.CustomerNameSnapshot,
        Mobile = l.Mobile,
        Email = l.Email,
        EmployeeId = l.EmployeeId,
        AssignedTo = l.EmployeeNameSnapshot,
        Status = l.Status,
        Notes = l.Notes,
        ConvertedDate = l.ConvertedDate,
        CreatedDate = l.CreatedDate,
    };
}
