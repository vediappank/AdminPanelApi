namespace RenewalTracker.Platform.Application.DTOs.ChatbotQa;

/// <summary>One question and answer for the website chatbot (dbo.WebsiteChatbotQa).</summary>
public class ChatbotQaDto
{
    public int ChatbotQaId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public string Status { get; set; } = "Draft";
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public class ChatbotQaSaveRequest
{
    public string Category { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public string Status { get; set; } = "Draft";
}

/// <summary>What the website receives - published items only, no internal fields.</summary>
public class PublicChatbotQaDto
{
    public string Category { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
}
