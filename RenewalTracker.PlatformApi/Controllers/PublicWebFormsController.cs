using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RenewalTracker.Platform.Application.DTOs.WebLeads;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// The ONLY anonymous write endpoints in this API - every form on
/// blisspointgroup.com posts here (js/bp-leads.js):
///
///   POST api/public/web-forms/enquiry          JSON  - all enquiry / quote / demo / CRM access forms
///   POST api/public/web-forms/job-application  multipart/form-data - careers page (CV upload)
///
/// Protection: "Website" CORS policy (only the website's own origins, see
/// Website:AllowedOrigins), "website-forms" rate limit per IP (Program.cs),
/// a hidden honeypot field (filled = stored as spam, never alerted, visitor
/// still sees "thank you"), server-side validation, and CV type/size/content checks.
///
/// Each saved submission is pushed instantly to the Admin Panel (WebLeadNotifier).
/// </summary>
[ApiController]
[Route("api/public/web-forms")]
[AllowAnonymous]
[EnableCors(WebsiteCorsPolicy)]
[EnableRateLimiting(RateLimitPolicy)]
[WebLeadExceptionFilter]
public partial class PublicWebFormsController : ControllerBase
{
    public const string WebsiteCorsPolicy = "Website";
    public const string RateLimitPolicy = "website-forms";
    public const long MaxCvBytes = 5 * 1024 * 1024;

    private static readonly string[] RequestTypes = { "Enquiry", "Quote", "Demo", "Access" };
    private static readonly Dictionary<string, string> CvTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
    };

    private readonly IWebLeadRepository _repo;
    private readonly WebLeadNotifier _notifier;
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PublicWebFormsController> _logger;

    public PublicWebFormsController(IWebLeadRepository repo, WebLeadNotifier notifier, IConfiguration config,
        IWebHostEnvironment env, ILogger<PublicWebFormsController> logger)
    {
        _repo = repo;
        _notifier = notifier;
        _config = config;
        _env = env;
        _logger = logger;
    }

    // -----------------------------------------------------------------
    // Enquiry / quote / demo / CRM access
    // -----------------------------------------------------------------

    [HttpPost("enquiry")]
    [RequestSizeLimit(64 * 1024)]
    public async Task<ActionResult<PublicSubmitResponse>> SubmitEnquiry([FromBody] PublicEnquiryRequest request, CancellationToken ct)
    {
        var isSpam = !string.IsNullOrWhiteSpace(request.Website);

        request.FormCode = (request.FormCode ?? string.Empty).Trim().ToLowerInvariant();
        request.RequestType = RequestTypes.FirstOrDefault(t => t.Equals(request.RequestType?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Enquiry";
        request.FullName = (request.FullName ?? string.Empty).Trim();
        request.Email = (request.Email ?? string.Empty).Trim();
        request.Phone = (request.Phone ?? string.Empty).Trim();

        var errors = new List<string>();
        if (!FormCodeRegex().IsMatch(request.FormCode)) errors.Add("Form code is missing.");
        if (request.FullName.Length < 2) errors.Add("Please enter your name.");
        if (!IsEmail(request.Email)) errors.Add("Please enter a valid email address.");
        if (!IsPhone(request.Phone)) errors.Add("Please enter a valid phone number.");
        if (request.Travellers is < 0 or > 500 || request.Pilgrims is < 0 or > 500) errors.Add("Number of travellers looks wrong.");
        if (errors.Count > 0 && !isSpam)
            return BadRequest(new { message = string.Join(" ", errors), errors });

        if (isSpam && errors.Count > 0)
        {
            // A bot that also failed validation - don't store it, just look successful.
            return Ok(new PublicSubmitResponse { ReferenceNo = "WE-0000-000000", Message = ThankYou(request.RequestType) });
        }

        var result = await _repo.InsertEnquiryAsync(new WebEnquiryInsert
        {
            Request = request,
            SourcePage = request.PagePath ?? Request.Headers.Referer.ToString(),
            IpAddress = ClientIp(),
            UserAgent = Request.Headers.UserAgent.ToString(),
            IsSpam = isSpam,
        }, ct);

        _logger.LogInformation("Website enquiry {Reference} from form {FormCode} (duplicate={Duplicate}, spam={Spam})",
            result.ReferenceNo, request.FormCode, result.IsDuplicate, result.IsSpam);

        await _notifier.EnquiryReceivedAsync(result, request);

        return Ok(new PublicSubmitResponse { ReferenceNo = result.ReferenceNo, Message = ThankYou(result.RequestType) });
    }

    // -----------------------------------------------------------------
    // Careers - CV upload
    // -----------------------------------------------------------------

    [HttpPost("job-application")]
    [RequestSizeLimit(MaxCvBytes + 256 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxCvBytes + 256 * 1024)]
    public async Task<ActionResult<PublicSubmitResponse>> SubmitJobApplication(
        [FromForm] string? name, [FromForm] string? email, [FromForm] string? phone, [FromForm] string? role,
        [FromForm] string? experience, [FromForm] string? message, [FromForm] string? website,
        [FromForm] string? pagePath, IFormFile? cv, CancellationToken ct)
    {
        var isSpam = !string.IsNullOrWhiteSpace(website);
        name = name?.Trim() ?? string.Empty;
        email = email?.Trim() ?? string.Empty;
        phone = phone?.Trim() ?? string.Empty;
        role = role?.Trim() ?? string.Empty;

        var errors = new List<string>();
        if (name.Length < 2) errors.Add("Please enter your name.");
        if (!IsEmail(email)) errors.Add("Please enter a valid email address.");
        if (!IsPhone(phone)) errors.Add("Please enter a valid phone number.");
        if (role.Length < 2) errors.Add("Please choose the role you are applying for.");

        var extension = Path.GetExtension(cv?.FileName ?? string.Empty);
        if (cv == null || cv.Length == 0) errors.Add("Please attach your CV.");
        else if (!CvTypes.ContainsKey(extension)) errors.Add("CV must be a PDF or Word document (.pdf, .doc, .docx).");
        else if (cv.Length > MaxCvBytes) errors.Add("CV must be 5 MB or smaller.");
        else if (!await HasExpectedSignatureAsync(cv, extension, ct)) errors.Add("The CV file appears to be damaged or is not a real PDF/Word document.");

        if (isSpam)
            return Ok(new PublicSubmitResponse { ReferenceNo = "JA-0000-000000", Message = "Thank you - your application has been received." });
        if (errors.Count > 0)
            return BadRequest(new { message = string.Join(" ", errors), errors });

        // Stored outside wwwroot under a random name - only the Admin Panel can download it.
        var folder = CvFolder();
        Directory.CreateDirectory(folder);
        var storedName = $"{DateTime.UtcNow:yyyyMM}_{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var fullPath = Path.Combine(folder, storedName);
        await using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await cv!.CopyToAsync(stream, ct);
        }

        WebJobApplicationInsertResult result;
        try
        {
            result = await _repo.InsertJobApplicationAsync(new WebJobApplicationInsert
            {
                FullName = name,
                Email = email,
                Phone = phone,
                RoleApplied = role,
                Experience = experience,
                CoverNote = message,
                CvFileName = SafeDisplayName(cv.FileName),
                CvFilePath = storedName,
                CvContentType = CvTypes[extension],
                CvSizeBytes = (int)cv.Length,
                SourcePage = pagePath ?? Request.Headers.Referer.ToString(),
                IpAddress = ClientIp(),
                UserAgent = Request.Headers.UserAgent.ToString(),
                IsSpam = false,
            }, ct);
        }
        catch
        {
            TryDelete(fullPath);
            throw;
        }

        _logger.LogInformation("Website job application {Reference} ({Role})", result.ReferenceNo, result.RoleApplied);
        await _notifier.JobApplicationReceivedAsync(result);

        return Ok(new PublicSubmitResponse
        {
            ReferenceNo = result.ReferenceNo,
            Message = "Thank you - your application has been received. Our HR team will contact you if your profile matches.",
        });
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    internal static string CvFolder(IConfiguration config, IWebHostEnvironment env)
    {
        var configured = config["Website:CvStoragePath"];
        if (string.IsNullOrWhiteSpace(configured)) return Path.Combine(env.ContentRootPath, "App_Data", "cv");
        return Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
    }

    private string CvFolder() => CvFolder(_config, _env);

    private static string ThankYou(string requestType) => requestType switch
    {
        "Demo" => "Thank you - our team will contact you shortly to schedule your demo.",
        "Access" => "Thank you - our team will set up your access and contact you shortly.",
        "Quote" => "Thank you - we will send your quotation shortly.",
        _ => "Thank you - our team will contact you shortly.",
    };

    private string? ClientIp()
    {
        // Behind IIS/Plesk the real visitor address may arrive in X-Forwarded-For.
        var forwarded = Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded)) return forwarded.Split(',')[0].Trim();
        return HttpContext.Connection.RemoteIpAddress?.ToString();
    }

    private static bool IsEmail(string value)
    {
        if (value.Length is < 5 or > 200) return false;
        try
        {
            return new MailAddress(value).Address.Equals(value, StringComparison.OrdinalIgnoreCase) && value.Contains('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsPhone(string value)
    {
        if (value.Length is < 6 or > 30 || !PhoneRegex().IsMatch(value)) return false;
        return value.Count(char.IsDigit) is >= 6 and <= 15;
    }

    private static string SafeDisplayName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 200 ? name[^200..] : name;
    }

    /// <summary>Checks the first bytes really are PDF / OLE (.doc) / ZIP (.docx) - not just a renamed file.</summary>
    private static async Task<bool> HasExpectedSignatureAsync(IFormFile file, string extension, CancellationToken ct)
    {
        var header = new byte[8];
        await using var s = file.OpenReadStream();
        var read = await s.ReadAsync(header.AsMemory(0, header.Length), ct);
        if (read < 4) return false;

        return extension.ToLowerInvariant() switch
        {
            ".pdf" => header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46,             // %PDF
            ".doc" => read >= 8 && header[0] == 0xD0 && header[1] == 0xCF && header[2] == 0x11 && header[3] == 0xE0, // OLE2
            ".docx" => header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04,            // PK..
            _ => false,
        };
    }

    private static void TryDelete(string path)
    {
        try { System.IO.File.Delete(path); } catch { /* best effort */ }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,59}$")]
    private static partial Regex FormCodeRegex();

    [GeneratedRegex(@"^[0-9+()\-\s./]+$")]
    private static partial Regex PhoneRegex();
}
