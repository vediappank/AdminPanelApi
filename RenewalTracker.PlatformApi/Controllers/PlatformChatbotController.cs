using Microsoft.AspNetCore.Mvc;
using RenewalTracker.Platform.Application.Interfaces;
using RenewalTracker.PlatformApi.Filters;
using RenewalTracker.PlatformApi.Services;

namespace RenewalTracker.PlatformApi.Controllers;

/// <summary>
/// Settings ▸ Chatbot - questions and answers of the Business Panel help chatbot (PlatformChatbotStore).
///   GET    api/platform/chatbot                       settings, topics, questions, pages, open unanswered count
///   PUT    api/platform/chatbot/settings              on/off, bot name, welcome, "no answer" message, suggestions
///   POST   api/platform/chatbot/categories            add topic     PUT .../categories/{id}  DELETE .../categories/{id}
///   POST   api/platform/chatbot/questions             add question  PUT .../questions/{id}   DELETE .../questions/{id}
///   POST   api/platform/chatbot/starter               add the starter questions that are missing
///   POST   api/platform/chatbot/test                  { text } - the answer a Business Panel user would get now (instant test)
///   GET    api/platform/chatbot/unanswered?status=    what users asked without an answer
///   PUT    api/platform/chatbot/unanswered/{id}       { status: Open | Answered | Ignored, questionId? }
///   DELETE api/platform/chatbot/unanswered            clear the answered / ignored ones
/// </summary>
[ApiController]
[Route("api/platform/chatbot")]
[RequirePlatformModule("CHATBOT_SETTINGS", "MESSAGING_SETTINGS")]
public class PlatformChatbotController : ControllerBase
{
    private readonly PlatformChatbotStore _store;
    private readonly ICurrentPlatformUserService _user;
    private readonly ILogger<PlatformChatbotController> _logger;

    public PlatformChatbotController(PlatformChatbotStore store, ICurrentPlatformUserService user, ILogger<PlatformChatbotController> logger)
    {
        _store = store;
        _user = user;
        _logger = logger;
    }

    private string By => string.IsNullOrWhiteSpace(_user.Email) ? $"user#{_user.PlatformUserId}" : _user.Email;

    public sealed class ChatbotSettingsDto
    {
        public bool Enabled { get; set; } = true;
        public string? BotName { get; set; }
        public string? Welcome { get; set; }
        public string? Fallback { get; set; }
        public bool ShowSuggestions { get; set; } = true;
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }
    }

    public sealed class ChatbotCategoryDto
    {
        public int CategoryId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Icon { get; set; }
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public int QuestionCount { get; set; }
    }

    public sealed class ChatbotQuestionDto
    {
        public int QuestionId { get; set; }
        public int CategoryId { get; set; }
        public string Question { get; set; } = string.Empty;
        public string? Keywords { get; set; }
        public string Answer { get; set; } = string.Empty;
        public string? LinkLabel { get; set; }
        public string? LinkRoute { get; set; }
        public bool IsSuggested { get; set; }
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public int AskCount { get; set; }
        public int HelpfulCount { get; set; }
        public int NotHelpfulCount { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }
        /// <summary>Only when saving: mark this unanswered question as answered by it.</summary>
        public int? FromUnansweredId { get; set; }
    }

    public sealed record ChatbotPageDto(string Name, string Route);
    public sealed record ChatbotTestRequest(string? Text);
    public sealed record ChatbotUnansweredUpdate(string Status, int? QuestionId);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var s = await _store.GetSettingsAsync(ct);
        var categories = await _store.GetCategoriesAsync(ct);
        var questions = await _store.GetQuestionsAsync(ct);
        var pages = await _store.GetPagesAsync(ct);
        return Ok(new
        {
            settings = ToDto(s),
            defaults = new { botName = PlatformChatbotStore.DefaultBotName, welcome = PlatformChatbotStore.DefaultWelcome, fallback = PlatformChatbotStore.DefaultFallback },
            categories = categories.Select(ToDto),
            questions = questions.Select(ToDto),
            pages = pages.Select(p => new ChatbotPageDto(p.Name, p.Route)),
            openUnanswered = await _store.CountOpenUnansweredAsync(ct),
        });
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings(ChatbotSettingsDto dto, CancellationToken ct)
    {
        if ((dto.BotName?.Trim().Length ?? 0) > 60) return BadRequest(new { message = "Bot name: up to 60 characters." });
        if ((dto.Welcome?.Trim().Length ?? 0) > 400 || (dto.Fallback?.Trim().Length ?? 0) > 400)
            return BadRequest(new { message = "Messages: up to 400 characters." });
        await _store.SaveSettingsAsync(new PlatformChatbotStore.Settings
        {
            Enabled = dto.Enabled, BotName = dto.BotName, Welcome = dto.Welcome, Fallback = dto.Fallback, ShowSuggestions = dto.ShowSuggestions,
        }, By, ct);
        _logger.LogInformation("Chatbot settings saved by {By} (enabled {Enabled})", By, dto.Enabled);
        return Ok(new { message = "Chatbot settings saved. The Business Panel shows them within 30 seconds." });
    }

    // ---------------------------------------------------------------- topics

    [HttpPost("categories")]
    public Task<IActionResult> AddCategory(ChatbotCategoryDto dto, CancellationToken ct) => SaveCategory(0, dto, ct);

    [HttpPut("categories/{id:int}")]
    public async Task<IActionResult> SaveCategory(int id, ChatbotCategoryDto dto, CancellationToken ct)
    {
        var name = (dto.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 100) return BadRequest(new { message = "Enter the topic name (up to 100 characters)." });
        var existing = await _store.GetCategoriesAsync(ct);
        if (existing.Any(c => c.CategoryId != id && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new { message = $"There is already a topic \"{name}\"." });
        var saved = await _store.SaveCategoryAsync(new PlatformChatbotStore.Category
        {
            CategoryId = id, Name = name, Icon = dto.Icon, IsActive = dto.IsActive,
            DisplayOrder = id == 0 && dto.DisplayOrder == 0 ? (existing.Count == 0 ? 10 : existing.Max(c => c.DisplayOrder) + 10) : dto.DisplayOrder,
        }, ct);
        if (saved == 0) return NotFound(new { message = "Topic not found." });
        return Ok(new { categoryId = saved, message = id == 0 ? "Topic added." : "Topic saved." });
    }

    [HttpDelete("categories/{id:int}")]
    public async Task<IActionResult> DeleteCategory(int id, CancellationToken ct)
    {
        if (!await _store.DeleteCategoryAsync(id, ct))
            return BadRequest(new { message = "This topic still has questions - move them to another topic or delete them first." });
        return Ok(new { message = "Topic deleted." });
    }

    // ---------------------------------------------------------------- questions

    [HttpPost("questions")]
    public Task<IActionResult> AddQuestion(ChatbotQuestionDto dto, CancellationToken ct) => SaveQuestion(0, dto, ct);

    [HttpPut("questions/{id:int}")]
    public async Task<IActionResult> SaveQuestion(int id, ChatbotQuestionDto dto, CancellationToken ct)
    {
        var question = (dto.Question ?? string.Empty).Trim();
        var answer = (dto.Answer ?? string.Empty).Trim();
        if (question.Length is < 3 or > 300) return BadRequest(new { message = "Enter the question (3 to 300 characters)." });
        if (answer.Length is < 2 or > 4000) return BadRequest(new { message = "Enter the answer (up to 4000 characters)." });
        if ((dto.Keywords?.Length ?? 0) > 1000) return BadRequest(new { message = "Keywords: up to 1000 characters." });
        var route = string.IsNullOrWhiteSpace(dto.LinkRoute) ? null : dto.LinkRoute.Trim();
        if (route is not null)
        {
            if (!route.StartsWith('/')) route = "/" + route;
            if (route.Length > 200 || route.Contains("://") || route.StartsWith("//") || route.Any(char.IsWhiteSpace))
                return BadRequest(new { message = "Page link: a Business Panel page like /trackers." });
        }
        var label = string.IsNullOrWhiteSpace(dto.LinkLabel) ? null : dto.LinkLabel.Trim();
        if (label?.Length > 60) return BadRequest(new { message = "Button text: up to 60 characters." });
        if (route is not null && label is null) label = "Open page";

        var categories = await _store.GetCategoriesAsync(ct);
        if (categories.All(c => c.CategoryId != dto.CategoryId)) return BadRequest(new { message = "Choose the topic." });

        var questions = await _store.GetQuestionsAsync(ct);
        if (questions.Any(q => q.QuestionId != id && string.Equals(q.QuestionText.Trim(), question, StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new { message = "This question is already in the list." });
        var order = dto.DisplayOrder;
        if (id == 0 && order == 0)
            order = questions.Where(q => q.CategoryId == dto.CategoryId).Select(q => q.DisplayOrder).DefaultIfEmpty(0).Max() + 10;

        var saved = await _store.SaveQuestionAsync(new PlatformChatbotStore.Question
        {
            QuestionId = id, CategoryId = dto.CategoryId, QuestionText = question, Keywords = dto.Keywords, Answer = answer,
            LinkLabel = route is null ? null : label, LinkRoute = route, IsSuggested = dto.IsSuggested, DisplayOrder = order, IsActive = dto.IsActive,
        }, By, ct);
        if (saved == 0) return NotFound(new { message = "Question not found." });
        if (dto.FromUnansweredId is int un) await _store.SetUnansweredAsync(un, "Answered", saved, By, ct);
        return Ok(new { questionId = saved, message = id == 0 ? "Question added." : "Question saved." });
    }

    [HttpDelete("questions/{id:int}")]
    public async Task<IActionResult> DeleteQuestion(int id, CancellationToken ct)
    {
        if (!await _store.DeleteQuestionAsync(id, ct)) return NotFound(new { message = "Question not found." });
        return Ok(new { message = "Question deleted." });
    }

    [HttpPost("starter")]
    public async Task<IActionResult> Starter(CancellationToken ct)
    {
        var added = await _store.AddStarterAsync(By, ct);
        return Ok(new { added, message = added == 0 ? "All starter questions are already in the list." : $"{added} starter question(s) added." });
    }

    /// <summary>Instant test - the same matching the Business Panel uses, over the active questions of active topics.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test(ChatbotTestRequest req, CancellationToken ct)
    {
        var text = (req.Text ?? string.Empty).Trim();
        if (text.Length == 0) return BadRequest(new { message = "Type a question to test." });
        if (text.Length > 500) text = text[..500];
        var s = await _store.GetSettingsAsync(ct);
        var activeCats = (await _store.GetCategoriesAsync(ct)).Where(c => c.IsActive).Select(c => c.CategoryId).ToHashSet();
        var questions = (await _store.GetQuestionsAsync(ct)).Where(q => q.IsActive && activeCats.Contains(q.CategoryId)).ToList();
        var byId = questions.ToDictionary(q => q.QuestionId);

        var small = ChatbotMatcher.SmallTalk(text, "there");
        if (small is not null) return Ok(new { type = "smalltalk", reply = small, scores = Array.Empty<object>() });

        var items = questions.Select(q => new ChatbotMatcher.Item(q.QuestionId, q.QuestionText, q.Keywords, q.Answer)).ToList();
        var result = ChatbotMatcher.Find(text, items);
        // the top scores, so you can see why this answer was chosen (and add keywords when it was not)
        var scores = ChatbotMatcher.Rank(text, items).Take(5).Select(x => new { questionId = x.Id, question = byId[x.Id].QuestionText, score = x.Score, coverage = x.Coverage });
        object? Q(int id) => byId.TryGetValue(id, out var q) ? new { q.QuestionId, question = q.QuestionText, q.Answer, q.LinkLabel, q.LinkRoute } : null;

        if (result.Best is not null)
            return Ok(new { type = "answer", words = ChatbotMatcher.Words(text), answer = Q(result.Best.Id), related = result.Related.Select(r => Q(r.Id)), scores });
        return Ok(new
        {
            type = result.DidYouMean.Count > 0 ? "suggest" : "fallback",
            words = ChatbotMatcher.Words(text),
            reply = result.DidYouMean.Count > 0 ? "I'm not sure I understood. Did you mean:" : (string.IsNullOrWhiteSpace(s.Fallback) ? PlatformChatbotStore.DefaultFallback : s.Fallback),
            suggestions = result.DidYouMean.Select(r => Q(r.Id)),
            scores,
        });
    }

    // ---------------------------------------------------------------- unanswered

    [HttpGet("unanswered")]
    public async Task<IActionResult> Unanswered([FromQuery] string? status, CancellationToken ct)
    {
        status = status is "Open" or "Answered" or "Ignored" ? status : null;
        return Ok(await _store.GetUnansweredAsync(status, ct));
    }

    [HttpPut("unanswered/{id:int}")]
    public async Task<IActionResult> SetUnanswered(int id, ChatbotUnansweredUpdate dto, CancellationToken ct)
    {
        if (dto.Status is not ("Open" or "Answered" or "Ignored")) return BadRequest(new { message = "Status: Open, Answered or Ignored." });
        if (!await _store.SetUnansweredAsync(id, dto.Status, dto.QuestionId, By, ct)) return NotFound(new { message = "Not found." });
        return Ok(new { message = "Saved." });
    }

    [HttpDelete("unanswered")]
    public async Task<IActionResult> ClearUnanswered(CancellationToken ct)
    {
        var n = await _store.DeleteClosedUnansweredAsync(ct);
        return Ok(new { message = $"{n} answered / ignored question(s) cleared." });
    }

    private static ChatbotSettingsDto ToDto(PlatformChatbotStore.Settings s) => new()
    {
        Enabled = s.Enabled, BotName = s.BotName, Welcome = s.Welcome, Fallback = s.Fallback, ShowSuggestions = s.ShowSuggestions,
        ModifiedDate = s.ModifiedDate, ModifiedBy = s.ModifiedBy,
    };

    private static ChatbotCategoryDto ToDto(PlatformChatbotStore.Category c) => new()
    {
        CategoryId = c.CategoryId, Name = c.Name, Icon = c.Icon, DisplayOrder = c.DisplayOrder, IsActive = c.IsActive, QuestionCount = c.QuestionCount,
    };

    private static ChatbotQuestionDto ToDto(PlatformChatbotStore.Question q) => new()
    {
        QuestionId = q.QuestionId, CategoryId = q.CategoryId, Question = q.QuestionText, Keywords = q.Keywords, Answer = q.Answer,
        LinkLabel = q.LinkLabel, LinkRoute = q.LinkRoute, IsSuggested = q.IsSuggested, DisplayOrder = q.DisplayOrder, IsActive = q.IsActive,
        AskCount = q.AskCount, HelpfulCount = q.HelpfulCount, NotHelpfulCount = q.NotHelpfulCount, ModifiedDate = q.ModifiedDate, ModifiedBy = q.ModifiedBy,
    };
}
