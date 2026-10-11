namespace RenewalTracker.Platform.Application.DTOs.Sales;

/// <summary>Everything the quotation "Send" dialog needs - see PlatformQuotationSendController.</summary>
public class QuotationSendDetailsDto
{
    public QuotationDto Quotation { get; set; } = new();
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public string CustomerMobile { get; set; } = string.Empty;
    public string DefaultSubject { get; set; } = string.Empty;
    public string DefaultBody { get; set; } = string.Empty;
    public string DefaultWhatsAppMessage { get; set; } = string.Empty;
    /// <summary>False when the Smtp section in appsettings.json is not filled in.</summary>
    public bool EmailConfigured { get; set; }
}

public class QuotationEmailRequest
{
    public string To { get; set; } = string.Empty;
    public List<string>? Cc { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}

public class QuotationWhatsAppRequest
{
    public string Mobile { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class QuotationSendResultDto
{
    public string Message { get; set; } = string.Empty;
    /// <summary>WhatsApp only: the wa.me link the browser opens.</summary>
    public string? WhatsAppUrl { get; set; }
    public QuotationDto Quotation { get; set; } = new();
}
