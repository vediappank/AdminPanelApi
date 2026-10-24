namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Sample quotation / invoice / receipt in a chosen template with a company's name and logo - lets the
/// operator see a template before choosing it (Control Center ▸ Business Panel branding, Settings ▸ Branding).
/// Sample data only; nothing is read from or written to any database.
/// </summary>
public static class TemplatePreview
{
    public static byte[] Build(string template, string? document, string companyName, string? logoDataUrl, string currency = "INR")
    {
        var kind = (document ?? "quotation").Trim().ToLowerInvariant();
        var today = DateTime.Today;
        if (kind == "receipt")
        {
            return PaymentReceiptPdf.Build(new PaymentReceiptData(
                companyName, logoDataUrl, template, "PAY-000123 (sample)", today, "Sample Customer",
                new[] { "Customer Code: CUST-0001", "+971 50 000 0000" }, "Bank Transfer", "TXN 000123",
                "Invoice INV-0001", "Payment towards invoice INV-0001 (quotation QT-0001)", 2000m, currency, 6300m, 4000m));
        }

        var isInvoice = kind == "invoice";
        var items = new[]
        {
            new QuotationPdfItem("Visa renewal - 2 years", "Visa", 1, 3000m, 150m, 3150m),
            new QuotationPdfItem("Emirates ID renewal", "Emirates ID", 1, 1500m, 75m, 1575m),
            new QuotationPdfItem("Document typing and attestation", "Typing", 3, 500m, 25m, 525m),
        };
        return QuotationPdfBuilder.Build(new QuotationPdfData(
            CompanyName: companyName,
            QuotationNumber: isInvoice ? "INV-0001 (sample)" : "QT-0001 (sample)",
            CustomerName: "Sample Customer",
            EnquiryNumber: isInvoice ? "QT-0001" : "ENQ-0001",
            ContactLine: "+971 50 000 0000 / customer@example.com",
            ContactPerson: "Sample Contact",
            CreatedDate: today,
            ValidTill: today.AddDays(isInvoice ? 7 : 30),
            Items: items,
            Net: 6000m,
            Vat: 300m,
            GrandTotal: 6300m,
            Currency: currency,
            DocumentTitle: isInvoice ? "INVOICE" : "QUOTATION",
            NumberLabel: isInvoice ? "Invoice No" : "Quotation No",
            ValidLabel: isInvoice ? "Due Date" : "Valid Till",
            ReferenceLabel: isInvoice ? "Quotation No" : "Enquiry No",
            Template: template,
            LogoDataUrl: logoDataUrl));
    }
}
