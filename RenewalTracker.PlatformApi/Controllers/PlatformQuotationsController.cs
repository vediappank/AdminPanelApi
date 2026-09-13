using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RenewalTracker.Platform.Application.DTOs.Sales;
using RenewalTracker.Platform.Domain.Entities;
using RenewalTracker.Platform.Infrastructure.Data;
using RenewalTracker.PlatformApi.Filters;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Sales ▸ Quotations - priced proposals generated from a Sales Enquiry.
/// Platform-owned, hand-written against PlatformDbContext. Talks to the new
/// dbo.Quotations / dbo.QuotationProducts / dbo.QuotationServiceLines
/// tables (see Quotation.cs and friends) - see QuotationsComponent for the
/// Angular side.
///
/// Deliberately separate from PlatformSalesController's Enquiry
/// Requirements: a Quotation is built from one or more Products (dbo.
/// Applications - the apps Bliss Point Group ships), each carrying its own
/// manually-typed service lines. Neither is a Service Catalog row - that
/// table (dbo.Services, see EnquiryRequirement.cs) serves Enquiry
/// Requirements only.
///
/// GenerateQuotations() is the only way a Quotation gets created: picking
/// more than one Product offers Mode "Combined" (a single Quotation with
/// one QuotationProduct per picked Product) or "Independent" (a separate,
/// fully independent Quotation per Product - its own QuoteNo, totals and
/// Status). Everything else here is read/status-update/delete against
/// whatever GenerateQuotations() produced.
/// </summary>
[ApiController]
[Route("api/platform/quotations")]
[RequirePlatformAuth]
public class PlatformQuotationsController : ControllerBase
{
    private static readonly string[] ValidStatuses = { "Draft", "Sent", "Accepted", "Rejected" };

    private readonly PlatformDbContext _db;

    public PlatformQuotationsController(PlatformDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<List<QuotationDto>>> GetQuotations()
    {
        var quotations = await _db.Quotations.AsNoTracking().OrderByDescending(q => q.QuotationId).ToListAsync();
        var enquiryNames = await _db.Enquiries.AsNoTracking().ToDictionaryAsync(e => e.SalesEnquiryId, e => e.CustomerNameSnapshot);
        var products = await _db.QuotationProducts.AsNoTracking().ToListAsync();
        var services = await _db.QuotationServiceLines.AsNoTracking().ToListAsync();
        return Ok(quotations.Select(q => ToDto(q, enquiryNames.GetValueOrDefault(q.SalesEnquiryId), products, services)).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<QuotationDto>> GetQuotation(int id)
    {
        var q = await _db.Quotations.AsNoTracking().FirstOrDefaultAsync(x => x.QuotationId == id);
        if (q == null) return NotFound();

        var customer = (await _db.Enquiries.AsNoTracking().FirstOrDefaultAsync(e => e.SalesEnquiryId == q.SalesEnquiryId))?.CustomerNameSnapshot;
        var products = await _db.QuotationProducts.AsNoTracking().Where(p => p.QuotationId == id).ToListAsync();
        var productIds = products.Select(p => p.QuotationProductId).ToList();
        var services = await _db.QuotationServiceLines.AsNoTracking().Where(s => productIds.Contains(s.QuotationProductId)).ToListAsync();
        return Ok(ToDto(q, customer, products, services));
    }

    /// <summary>
    /// Generates one or more Quotations for an Enquiry from a picked set of
    /// Products (Applications) and, for each, a manually-entered list of
    /// service lines - typed here directly, not looked up from the Service
    /// Catalog. See the class doc comment for what Mode does.
    /// </summary>
    [HttpPost("generate")]
    public async Task<ActionResult<List<QuotationDto>>> GenerateQuotations(GenerateQuotationDto dto)
    {
        if (!await _db.Enquiries.AnyAsync(e => e.SalesEnquiryId == dto.EnquiryId))
        {
            return BadRequest(new { message = "The selected enquiry does not exist." });
        }
        if (dto.Products.Count == 0)
        {
            return BadRequest(new { message = "Pick at least one product." });
        }

        var appIds = dto.Products.Select(p => p.ApplicationId).Distinct().ToList();
        var apps = await _db.Applications.AsNoTracking().Where(a => appIds.Contains(a.ApplicationId)).ToDictionaryAsync(a => a.ApplicationId);
        if (dto.Products.Any(p => !apps.ContainsKey(p.ApplicationId)))
        {
            return BadRequest(new { message = "One of the selected products no longer exists." });
        }

        // Each product can only ever be quoted once against a given
        // enquiry - the Angular side already hides/disables an
        // already-quoted product in the picker (see QuotationsComponent.
        // loadProductsForEnquiry), but that's a snapshot taken when the
        // enquiry was picked, so re-check here as the real guarantee
        // against a stale picker or two people generating at once.
        var alreadyQuotedAppIds = await (
            from qp in _db.QuotationProducts.AsNoTracking()
            join q in _db.Quotations.AsNoTracking() on qp.QuotationId equals q.QuotationId
            where q.SalesEnquiryId == dto.EnquiryId
            select qp.ApplicationId
        ).Distinct().ToListAsync();
        var duplicateAppIds = appIds.Intersect(alreadyQuotedAppIds).ToList();
        if (duplicateAppIds.Count > 0)
        {
            var duplicateNames = string.Join(", ", duplicateAppIds.Select(id => apps[id].Name));
            return BadRequest(new { message = $"Already quoted for this enquiry, pick a different product: {duplicateNames}." });
        }

        // Combined = one Quotation with every picked Product as its own
        // QuotationProduct; Independent = one Quotation per Product, each
        // generated on its own.
        var groups = dto.Mode == "Independent"
            ? dto.Products.Select(p => new List<GenerateQuotationProductDto> { p }).ToList()
            : new List<List<GenerateQuotationProductDto>> { dto.Products };

        var created = new List<Quotation>();
        foreach (var group in groups)
        {
            var quotation = new Quotation
            {
                SalesEnquiryId = dto.EnquiryId,
                QuoteNo = await NextQuoteNoAsync(),
                QuoteDate = DateTime.UtcNow.Date,
                Status = "Draft",
                CreatedDate = DateTime.UtcNow,
            };
            _db.Quotations.Add(quotation);
            await _db.SaveChangesAsync(); // need QuotationId before adding its Products

            decimal subtotal = 0, vatAmount = 0;
            foreach (var p in group)
            {
                var app = apps[p.ApplicationId];
                var product = new QuotationProduct
                {
                    QuotationId = quotation.QuotationId,
                    ApplicationId = p.ApplicationId,
                    ApplicationNameSnapshot = app.Name,
                };
                _db.QuotationProducts.Add(product);
                await _db.SaveChangesAsync(); // need QuotationProductId before adding its Services

                foreach (var s in p.Services)
                {
                    var qty = s.Qty <= 0 ? 1 : s.Qty;
                    _db.QuotationServiceLines.Add(new QuotationServiceLine
                    {
                        QuotationProductId = product.QuotationProductId,
                        ServiceName = s.ServiceName,
                        Qty = qty,
                        Amount = s.Amount,
                        VatPercent = s.VatPercent,
                        CreatedDate = DateTime.UtcNow,
                    });
                    var lineSubtotal = qty * s.Amount;
                    subtotal += lineSubtotal;
                    vatAmount += lineSubtotal * s.VatPercent / 100m;
                }
            }
            await _db.SaveChangesAsync();

            quotation.Subtotal = subtotal;
            quotation.VatAmount = vatAmount;
            quotation.GrandTotal = subtotal + vatAmount;
            await _db.SaveChangesAsync();

            created.Add(quotation);
        }

        var customer = (await _db.Enquiries.AsNoTracking().FirstOrDefaultAsync(e => e.SalesEnquiryId == dto.EnquiryId))?.CustomerNameSnapshot;
        var createdIds = created.Select(c => c.QuotationId).ToList();
        var allProducts = await _db.QuotationProducts.AsNoTracking().Where(p => createdIds.Contains(p.QuotationId)).ToListAsync();
        var allProductIds = allProducts.Select(p => p.QuotationProductId).ToList();
        var allServices = await _db.QuotationServiceLines.AsNoTracking().Where(s => allProductIds.Contains(s.QuotationProductId)).ToListAsync();

        return Ok(created.Select(q => ToDto(q, customer, allProducts, allServices)).ToList());
    }

    [HttpPut("{id:int}/status")]
    public async Task<ActionResult<QuotationDto>> UpdateStatus(int id, UpdateQuotationStatusDto dto)
    {
        if (!ValidStatuses.Contains(dto.Status))
        {
            return BadRequest(new { message = "Invalid status." });
        }
        var q = await _db.Quotations.FirstOrDefaultAsync(x => x.QuotationId == id);
        if (q == null) return NotFound();

        q.Status = dto.Status;
        q.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var customer = (await _db.Enquiries.AsNoTracking().FirstOrDefaultAsync(e => e.SalesEnquiryId == q.SalesEnquiryId))?.CustomerNameSnapshot;
        var products = await _db.QuotationProducts.AsNoTracking().Where(p => p.QuotationId == id).ToListAsync();
        var productIds = products.Select(p => p.QuotationProductId).ToList();
        var services = await _db.QuotationServiceLines.AsNoTracking().Where(s => productIds.Contains(s.QuotationProductId)).ToListAsync();
        return Ok(ToDto(q, customer, products, services));
    }

    /// <summary>
    /// Edits an already-generated quotation's pricing - see UpdateQuotationDto.
    /// Which Products are on the quotation never changes here, only each
    /// Product's own service lines: every existing line is replaced
    /// wholesale by whatever the person submitted (same simplest-thing-that-
    /// works approach as GenerateQuotations - lets them freely add/remove
    /// lines without diffing ids), and Subtotal/VatAmount/GrandTotal are
    /// recomputed from the new lines, same formula as Generate.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<QuotationDto>> UpdateQuotation(int id, UpdateQuotationDto dto)
    {
        var q = await _db.Quotations.FirstOrDefaultAsync(x => x.QuotationId == id);
        if (q == null) return NotFound();

        var products = await _db.QuotationProducts.AsNoTracking().Where(p => p.QuotationId == id).ToListAsync();
        var productIds = products.Select(p => p.QuotationProductId).ToList();

        if (dto.Products.Any(p => !productIds.Contains(p.Id)))
        {
            return BadRequest(new { message = "One of the products no longer belongs to this quotation." });
        }
        if (dto.Products.Any(p => p.Services.Count == 0 || p.Services.Any(s => string.IsNullOrWhiteSpace(s.ServiceName))))
        {
            return BadRequest(new { message = "Every product needs at least one named service line." });
        }

        var existingServices = await _db.QuotationServiceLines.Where(s => productIds.Contains(s.QuotationProductId)).ToListAsync();
        _db.QuotationServiceLines.RemoveRange(existingServices);
        await _db.SaveChangesAsync();

        decimal subtotal = 0, vatAmount = 0;
        foreach (var p in dto.Products)
        {
            foreach (var s in p.Services)
            {
                var qty = s.Qty <= 0 ? 1 : s.Qty;
                _db.QuotationServiceLines.Add(new QuotationServiceLine
                {
                    QuotationProductId = p.Id,
                    ServiceName = s.ServiceName,
                    Qty = qty,
                    Amount = s.Amount,
                    VatPercent = s.VatPercent,
                    CreatedDate = DateTime.UtcNow,
                });
                var lineSubtotal = qty * s.Amount;
                subtotal += lineSubtotal;
                vatAmount += lineSubtotal * s.VatPercent / 100m;
            }
        }

        q.ValidTill = dto.ValidTill;
        q.Notes = dto.Notes;
        q.Subtotal = subtotal;
        q.VatAmount = vatAmount;
        q.GrandTotal = subtotal + vatAmount;
        q.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var customer = (await _db.Enquiries.AsNoTracking().FirstOrDefaultAsync(e => e.SalesEnquiryId == q.SalesEnquiryId))?.CustomerNameSnapshot;
        var allServices = await _db.QuotationServiceLines.AsNoTracking().Where(s => productIds.Contains(s.QuotationProductId)).ToListAsync();
        return Ok(ToDto(q, customer, products, allServices));
    }

    /// <summary>Also removes this quotation's products and their service lines (FK cascade - see PlatformDbContext.Platform.cs).</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteQuotation(int id)
    {
        var q = await _db.Quotations.FirstOrDefaultAsync(x => x.QuotationId == id);
        if (q == null) return NotFound();

        _db.Quotations.Remove(q);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>QT-000001, QT-000002, ... - a simple incrementing number; nothing in this schema implied a per-year reset.</summary>
    private async Task<string> NextQuoteNoAsync()
    {
        var maxId = await _db.Quotations.MaxAsync(q => (int?)q.QuotationId) ?? 0;
        return $"QT-{maxId + 1:D6}";
    }

    private static QuotationDto ToDto(Quotation q, string? customer, List<QuotationProduct> allProducts, List<QuotationServiceLine> allServices) => new()
    {
        Id = q.QuotationId,
        EnquiryId = q.SalesEnquiryId,
        Customer = customer,
        QuoteNo = q.QuoteNo,
        QuoteDate = q.QuoteDate,
        ValidTill = q.ValidTill,
        Status = q.Status,
        Subtotal = q.Subtotal,
        VatAmount = q.VatAmount,
        GrandTotal = q.GrandTotal,
        Notes = q.Notes,
        CreatedDate = q.CreatedDate,
        Products = allProducts
            .Where(p => p.QuotationId == q.QuotationId)
            .Select(p => new QuotationProductDto
            {
                Id = p.QuotationProductId,
                ApplicationId = p.ApplicationId,
                ApplicationName = p.ApplicationNameSnapshot,
                Services = allServices
                    .Where(s => s.QuotationProductId == p.QuotationProductId)
                    .Select(s => new QuotationServiceLineDto
                    {
                        Id = s.QuotationServiceLineId,
                        ServiceName = s.ServiceName,
                        Qty = s.Qty,
                        Amount = s.Amount,
                        VatPercent = s.VatPercent,
                        LineTotal = s.Qty * s.Amount,
                    })
                    .ToList(),
            })
            .ToList(),
    };
}
