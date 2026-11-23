using RenewalTracker.Platform.Application.DTOs.ChatbotQa;

namespace RenewalTracker.Platform.Application.Interfaces;

/// <summary>Website chatbot questions and answers (dbo.WebsiteChatbotQa).</summary>
public interface IChatbotQaRepository
{
    Task<List<ChatbotQaDto>> ListAsync(CancellationToken ct = default);
    Task<ChatbotQaDto?> GetAsync(int id, CancellationToken ct = default);
    /// <summary>Returns null when the id does not exist.</summary>
    Task<ChatbotQaDto?> SaveAsync(int? id, ChatbotQaSaveRequest r, string changedBy, CancellationToken ct = default);
    /// <summary>Returns false when the id does not exist.</summary>
    Task<bool> DeleteAsync(int id, string changedBy, CancellationToken ct = default);
    Task<List<PublicChatbotQaDto>> ListPublishedAsync(CancellationToken ct = default);
}
