using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RenewalTracker.Platform.Application.Interfaces;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// The website AI assistant, run inside this API (replaces the n8n workflow).
/// One visitor message in -> the model may call tools (FAQ, live price list, lead actions) -> one reply out.
/// Memory: last messages of the chat session from SQL Server (ChatMemoryStore). Providers: LlmClient (failover).
/// </summary>
public sealed class ChatAgentService
{
    private const int MaxSteps = 6;
    private const int HistoryMessages = 16;

    private readonly LlmClient _llm;
    private readonly ChatMemoryStore _memory;
    private readonly ChatLeadStore _leads;
    private readonly SubscriberStore _subscribers;
    private readonly SubscriberMessenger _messenger;
    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ChatAgentService> _log;
    private readonly string _selfApi, _businessApi;

    public ChatAgentService(LlmClient llm, ChatMemoryStore memory, ChatLeadStore leads, SubscriberStore subscribers, SubscriberMessenger messenger,
        IServiceScopeFactory scopes, IHttpClientFactory http, IConfiguration config, ILogger<ChatAgentService> log)
    {
        _llm = llm; _memory = memory; _leads = leads; _subscribers = subscribers; _messenger = messenger; _scopes = scopes; _http = http; _log = log;
        _selfApi = (config["ChatAgent:SelfApiBaseUrl"] ?? "https://adminpanelapi.blisspointworld.com/api").TrimEnd('/');
        _businessApi = (config["ChatAgent:BusinessApiBaseUrl"] ?? "https://businesspanelapi.blisspointworld.com/api").TrimEnd('/');
    }

    public bool Ready => _llm.Ready;

    // ------------------------------------------------------------------ prompt

    private const string Core = """
You are the Bliss Point Group website assistant (blisspointworld.com), a Dubai-based UAE government and business services company. You are not a lawyer, tax adviser or government authority, and you do not process applications yourself.

HOW YOU WORK
- Company facts, contact routes, typical prices and timelines come ONLY from the tool get_company_info (call it with the topic, e.g. "business setup mainland", "accounting VAT", "typing attestation", "contact"). Approved Q&A wording comes from get_faq. UAE government updates come from get_latest_updates.
- For Visa & Immigration Services (family visa, employment visa and their sub-services) get prices from get_service_prices, never from memory. Ask what is needed (how many people, inside or outside the UAE, medical, Emirates ID), pick only the lines that apply (never both Inside Visa and Outside Visa for the same person), then call get_price_estimate with those serviceIds and the number of people. Show the lines and totals exactly as returned, in AED, say it is an estimate from the current price list and the team confirms the final quotation. Never add or multiply numbers yourself.
- Never invent prices, fees, timelines, requirements or legal advice. If you do not have it, say so and offer a team follow-up.
- If the visitor asks who you are or what you can do: you are the Bliss Point Group website assistant. You help with company setup, PRO services, visas, attestation, legal translation and document clearing, then offer one next step. Answer identity questions yourself; never use an admin-panel FAQ for them.

CONVERSATION FLOW - answer first, then convert
A. ANSWER FIRST. Every question gets a real, useful answer straight away. Never hold an answer back until the visitor signs in or gives contact details. If the exact figure is not published, give what is known (timeline, process, documents, what affects the cost) and say a quotation gives the final price.
B. THEN ONE STEP FORWARD. End each reply with one short, specific next step matched to what they asked, e.g. "Shall I prepare a draft price list for the 4 family and 2 employment visas?" Ask only one question at a time.
C. BUYING SIGNALS - cost, timeline, documents, how to start, quantities, "I need / I want": treat it as a lead and move to the lead steps on your next reply.
D. LEAD STEPS
 1. Signed-in visitor (email and WhatsApp verified): use their saved details, do not ask again. Call log_question for each question (matched true if you answered it, false if not).
 2. Quote journey: ask which one service matters most, call set_service_priority, show the prices and timelines for that need, then call save_draft_price_list with a short summary (never a total you invented). Ask: "Does this look right to you?" Accepted: call issue_quotation, say the team sends the formal quotation within 24 hours, and ask them to approve or reject it when received; then call record_decision with the answer. Not accepted or unsure: ask what would help (price, timing, scope), then call create_followup_task with type "offer" (hours 24, or 48 when approvals are needed) and say a better offer follows in that time.
 3. Guest (not signed in): after a buying signal offer two choices in one short message: (a) sign in or create an account right here in the chat with the Sign in button (name, email, mobile, confirmed with codes on email and WhatsApp; never ask for codes or passwords in the chat) to get a draft price list and quotation - when you offer this, end that reply with the exact marker [[LOGIN]]; or (b) leave name, phone and email and a team member will contact them - then call submit_enquiry. If they decline both, keep answering and do not repeat the offer more than once. When a tool answers needsLogin, ask them to sign in the same way and end with [[LOGIN]].
 4. A question you cannot answer: say so honestly, call create_followup_task with type "question" if signed in (a team member replies within 24 hours), or offer submit_enquiry if a guest.
E. OBJECTIONS. "Too expensive": explain what is included, say the team can tailor the scope, offer a follow-up offer. "I will think about it": agree warmly, offer to save a draft price list or have the team send details by WhatsApp, and stop pushing. "Is it guaranteed?": be honest, government approval and timing are decided by the authorities.
F. CONTACT FALLBACK. If they prefer a person: WhatsApp +971 58 997 0732 (wa.me/971589970732), Business Setup and IT +971 50 789 2097, General and Document Clearing +971 4 454 0091, info@blisspointworld.com.
G. Reply in the language the visitor writes in. Keep replies short (2-5 sentences or a short list), warm and professional, no pressure and no fake urgency. Plain text only, no markdown tables.

RULES
""";

    // The rules text was written for a single long prompt; point its references at the tools instead.
    private static readonly string Rules = "(Where the rules below say \"this message\" or \"the knowledge base\", they mean the information returned by get_company_info and get_faq.)\n" +
        ChatCompanyInfo.Behavior
            .Replace("the right contact from section 3", "the right contact (get_company_info topic \"contact\")")
            .Replace("capture a lead (section 7)", "capture a lead (submit_enquiry, or the sign-in flow)")
            .Replace("section 3", "the contact routes").Replace("section 7", "the lead steps above");

    private static string SystemPrompt(SubscriberStore.Subscriber? me, bool verified)
    {
        var visitor = me != null && verified
            ? $"VISITOR: signed-in customer (email and WhatsApp verified). Name: {me.Name}. Email: {me.Email}. Phone: +{me.WhatsApp}."
            : "VISITOR: guest (not signed in).";
        return Core + "\n" + Rules + "\n\n" + visitor;
    }

    // ------------------------------------------------------------------ tools

    private static JsonObject Tool(string name, string description, JsonObject? props = null, params string[] required)
    {
        var parameters = new JsonObject { ["type"] = "object", ["properties"] = props ?? new JsonObject() };
        if (required.Length > 0) { var r = new JsonArray(); foreach (var x in required) r.Add(x); parameters["required"] = r; }
        return new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["description"] = description, ["parameters"] = parameters } };
    }
    private static JsonObject P(string type, string description) => new() { ["type"] = type, ["description"] = description };

    private static readonly JsonArray Tools = new()
    {
        Tool("get_company_info", "Company facts, contact routes, typical prices, timelines, documents and steps for each service area. Call it for any question about services, costs or timelines.",
            new JsonObject { ["topic"] = P("string", "What the visitor asks about, e.g. 'mainland company setup', 'VAT registration', 'attestation', 'CRM', 'contact'") }, "topic"),
        Tool("get_faq", "Approved Q&A answers published on the website. Optional keywords narrow the list.",
            new JsonObject { ["query"] = P("string", "Keywords from the visitor's question") }),
        Tool("get_latest_updates", "Latest UAE government update posts from the Bliss Point blog."),
        Tool("get_service_prices", "The live price list for Visa & Immigration Services: categories, sub-categories and services with serviceId, amount (AED before VAT), VAT percent, count and pricingUnit (PerPerson, PerFamily, Fixed)."),
        Tool("get_price_estimate", "Calculates the price for the chosen services from the live price list. Never calculate yourself.",
            new JsonObject { ["serviceIds"] = P("string", "Comma-separated serviceId values picked from get_service_prices, only lines that apply"), ["people"] = P("integer", "Number of people") }, "serviceIds", "people"),
        Tool("submit_enquiry", "Saves a lead so the team contacts the visitor. Only when the visitor asked for contact and gave name, email and phone (or is signed in).",
            new JsonObject { ["fullName"] = P("string", "Name"), ["email"] = P("string", "Email"), ["phone"] = P("string", "Phone or WhatsApp with country code"), ["serviceInterest"] = P("string", "Service"), ["message"] = P("string", "Short summary of the need") },
            "fullName", "email", "phone"),
        Tool("log_question", "Signed-in visitors only: logs one visitor question on their lead.",
            new JsonObject { ["question"] = P("string", "The question"), ["matched"] = P("boolean", "True if you answered it from company information or Q&A") }, "question", "matched"),
        Tool("create_followup_task", "Signed-in visitors only: books a team follow-up. type question = reply within 24 hours; type offer = better offer in 24 or 48 hours.",
            new JsonObject { ["type"] = P("string", "question or offer"), ["hours"] = P("integer", "24 or 48"), ["note"] = P("string", "What the team should do") }, "type", "hours"),
        Tool("set_service_priority", "Signed-in visitors only: saves the one service the visitor cares about most.",
            new JsonObject { ["service"] = P("string", "Service name") }, "service"),
        Tool("save_draft_price_list", "Signed-in visitors only: saves the draft price list shown to the visitor on their lead.",
            new JsonObject { ["service"] = P("string", "Service name"), ["summary"] = P("string", "Lines with quantity and totals, then the grand total, as shown to the visitor") }, "service", "summary"),
        Tool("issue_quotation", "Signed-in visitors only: requests the formal quotation for the accepted draft price list."),
        Tool("record_decision", "Signed-in visitors only: saves the decision on the quotation. approved creates the sales record, rejected closes the lead.",
            new JsonObject { ["decision"] = P("string", "approved or rejected") }, "decision"),
    };

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static string Json(object o) => JsonSerializer.Serialize(o, Web);
    private static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    // Common words that match almost any FAQ text and must not decide which FAQ is used.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase) { "who", "what", "when", "where", "which", "how", "why", "are", "you", "the", "and", "for", "can", "does", "your", "this", "that", "with", "have", "has", "was", "will", "would", "should", "could", "about", "tell", "please", "there", "their", "them", "they", "its", "into", "from", "much", "many", "need", "want", "get" };
    // Whole words only, so 'who' does not match inside 'whole'.
    private static HashSet<string> Tokens(string s) => new(System.Text.RegularExpressions.Regex.Split(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(t => t.Length > 0), StringComparer.OrdinalIgnoreCase);

    private string Needs(string what) => Json(new { ok = false, needsLogin = true, message = "The visitor is not signed in (email and WhatsApp verified). Ask them to sign in or create an account in the chat and end your reply with [[LOGIN]]. " + what });

    private async Task<string> GetUrlAsync(string url, int max, CancellationToken ct)
    {
        try
        {
            var c = _http.CreateClient(LlmClient.HttpClientName);
            using var res = await c.GetAsync(url, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            return res.IsSuccessStatusCode ? Cut(text, max) : Json(new { ok = false, message = "The service could not answer right now." });
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Chat tool GET {Url} failed", url); return Json(new { ok = false, message = "The service could not answer right now." }); }
    }

    private async Task<string> RunToolAsync(string name, JsonNode? a, SubscriberStore.Subscriber? me, bool verified, CancellationToken ct)
    {
        string S(string k) => (a?[k]?.ToString() ?? string.Empty).Trim();
        int I(string k, int d) => int.TryParse(a?[k]?.ToString(), out var v) ? v : d;
        bool B(string k) => bool.TryParse(a?[k]?.ToString(), out var v) && v;

        switch (name)
        {
            case "get_company_info": return ChatCompanyInfo.Lookup(S("topic"));
            case "get_faq":
            {
                using var scope = _scopes.CreateScope();
                var rows = await scope.ServiceProvider.GetRequiredService<IChatbotQaRepository>().ListPublishedAsync(ct);
                var list = JsonNode.Parse(Json(rows))?.AsArray() ?? new JsonArray();
                var words = S("query").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2 && !StopWords.Contains(w)).ToList();
                var ranked = list.Select(n => (Row: n!, Score: words.Count(w => Tokens(n!["question"] + " " + n["answer"] + " " + n["category"]).Contains(w))))
                    .OrderByDescending(x => x.Score).Take(words.Count == 0 ? 0 : 6).Where(x => x.Score >= Math.Min(2, words.Count)).ToList();
                var sb = new StringBuilder();
                if (ranked.Count == 0) sb.Append("No close match. Published topics: ").Append(string.Join(", ", list.Select(n => n!["category"]?.ToString()).Distinct()));
                else foreach (var r in ranked) sb.Append("Q: ").Append(r.Row["question"]).Append("\nA: ").Append(r.Row["answer"]).Append("\n\n");
                return Cut(sb.ToString(), 5000);
            }
            case "get_latest_updates": return await GetUrlAsync(_selfApi + "/public/blog/posts", 4000, ct);
            case "get_service_prices": return await GetUrlAsync(_businessApi + "/public/service-catalog", 14000, ct);
            case "get_price_estimate":
                return await GetUrlAsync($"{_businessApi}/public/service-catalog/estimate?serviceIds={Uri.EscapeDataString(S("serviceIds"))}&people={Math.Clamp(I("people", 1), 1, 50)}", 6000, ct);
            case "submit_enquiry":
            {
                var fullName = S("fullName"); var email = S("email"); var phone = S("phone");
                if (me != null && verified) { fullName = me.Name; email = me.Email; phone = "+" + me.WhatsApp; }
                if (fullName.Length < 2 || !System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^\s@]+@[^\s@]+\.[^\s@]+$") || phone.Count(char.IsDigit) < 7)
                    return Json(new { ok = false, message = "Name, a valid email and a phone number are needed. Ask the visitor for the missing detail." });
                var reference = await _memory.InsertGuestEnquiryAsync(fullName, email, phone, S("serviceInterest"), S("message"), ct);
                return Json(new { ok = true, reference, message = "Saved. The team replies within one working day." });
            }
        }

        // The remaining tools work on the signed-in visitor's own lead.
        if (me == null || !verified) return Needs(string.Empty);
        var lead = await _leads.GetOrCreateLeadAsync(me, ct);
        switch (name)
        {
            case "log_question":
                if (S("question").Length == 0) return Json(new { ok = false, message = "Question is empty." });
                return await _leads.LogQuestionAsync(lead, S("question"), B("matched"), ct) ? Json(new { ok = true }) : Json(new { ok = false, message = "Too many questions logged. Carry on without logging." });
            case "create_followup_task":
            {
                var type = S("type").ToLowerInvariant();
                if (type is not ("question" or "offer")) return Json(new { ok = false, message = "type must be question or offer." });
                var hours = type == "question" ? 24 : (I("hours", 24) == 48 ? 48 : 24);
                var due = await _leads.CreateTaskAsync(lead, type, hours, S("note"), ct);
                return due == null ? Json(new { ok = false, message = "A follow-up is already booked. Tell the visitor the team will reply soon." }) : Json(new { ok = true, hours, message = $"Follow-up booked. The team replies within {hours} hours." });
            }
            case "set_service_priority":
                if (S("service").Length == 0) return Json(new { ok = false, message = "Service is empty." });
                await _leads.SetPriorityAsync(lead, S("service"), ct); return Json(new { ok = true });
            case "save_draft_price_list":
                if (S("service").Length == 0 || S("summary").Length == 0) return Json(new { ok = false, message = "Service and summary are required." });
                await _leads.SaveDraftAsync(lead, S("service"), S("summary"), ct); return Json(new { ok = true, message = "Draft price list saved on the lead." });
            case "issue_quotation":
                if (lead.DraftAt == null) return Json(new { ok = false, message = "Save a draft price list first, then ask for the quotation." });
                if (lead.QuotationRequestedAt != null) return Json(new { ok = true, message = "The quotation was already requested. The team sends it shortly." });
                await _leads.MarkQuotationAsync(lead, ct);
                await _leads.CreateTaskAsync(lead, "quotation", 24, "Prepare and send the quotation for the accepted draft price list.", ct);
                return Json(new { ok = true, message = "Quotation requested. The team sends the formal quotation within 24 hours. Ask the visitor to approve or reject it when received." });
            case "record_decision":
            {
                var d = S("decision").ToLowerInvariant();
                if (d is not ("approved" or "rejected")) return Json(new { ok = false, message = "decision must be approved or rejected." });
                if (lead.QuotationRequestedAt == null) return Json(new { ok = false, message = "There is no quotation yet. Request the quotation first." });
                await _leads.RecordDecisionAsync(lead, d == "approved", ct);
                return Json(new { ok = true, message = d == "approved" ? "Approved. A sales record was created and the team will start the work." : "Rejected. The lead is closed as rejected." });
            }
        }
        return Json(new { ok = false, message = "Unknown tool." });
    }

    // ------------------------------------------------------------------ one chat turn

    public sealed record Reply(string? Output, string? Error);

    public async Task<Reply> AskAsync(string sessionId, string text, string? sessionToken, CancellationToken ct)
    {
        var me = string.IsNullOrWhiteSpace(sessionToken) ? null : await _subscribers.FromSessionAsync(sessionToken.Trim(), ct);
        var verified = me != null && me.EmailVerifiedAt != null && !string.IsNullOrEmpty(me.WhatsApp) && (!_messenger.WhatsAppReady || me.WhatsAppVerifiedAt != null);

        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt(me, verified) } };
        foreach (var m in await _memory.RecentAsync(sessionId, HistoryMessages, ct))
            if (m.Role is "user" or "assistant") messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
        await _memory.AddAsync(sessionId, "user", text, null, verified ? me!.SubscriberId : null, ct);

        string? final = null, provider = null;
        for (var step = 0; step < MaxSteps; step++)
        {
            var r = await _llm.CompleteAsync(messages, step == MaxSteps - 1 ? new JsonArray() : Tools, ct);
            if (r == null) return new Reply(null, "All AI providers failed.");
            provider = r.Provider;
            var calls = r.Message["tool_calls"] as JsonArray;
            if (calls == null || calls.Count == 0) { final = (r.Message["content"]?.ToString() ?? string.Empty).Trim(); break; }

            messages.Add(JsonNode.Parse(r.Message.ToJsonString())!);
            foreach (var call in calls)
            {
                var fn = call?["function"]; var name = fn?["name"]?.ToString() ?? string.Empty;
                JsonNode? args = null;
                try { var raw = fn?["arguments"]?.ToString(); args = string.IsNullOrWhiteSpace(raw) ? new JsonObject() : JsonNode.Parse(raw); } catch { args = new JsonObject(); }
                string result;
                try { result = await RunToolAsync(name, args, me, verified, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _log.LogError(ex, "Chat tool {Tool} failed", name); result = Json(new { ok = false, message = "That action could not be completed right now." }); }
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call?["id"]?.ToString() ?? name, ["content"] = result });
            }
        }

        if (string.IsNullOrWhiteSpace(final)) return new Reply(null, "The AI returned no answer.");
        await _memory.AddAsync(sessionId, "assistant", final, provider, verified ? me!.SubscriberId : null, ct);
        return new Reply(final, null);
    }
}
