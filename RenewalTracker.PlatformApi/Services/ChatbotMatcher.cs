using System.Text;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Finds the best answer for a question typed in the Business Panel chatbot (no AI - keyword matching).
/// The same class is in the Business Panel API (BusinessPanel.Api.Services.ChatbotMatcher) - keep both copies the same,
/// so Settings ▸ Chatbot ▸ Test gives exactly the answer the users get.
///
/// How it scores: the question is split into words, common words ("how", "do", "I"...) are dropped, words are reduced to a
/// stem ("payments" / "paid" → "pay", "creating" → "add") and every stored question gets points per word found:
/// keywords 3, question text 2, answer text 0.5; one typing mistake in a long word still counts (70 %).
/// </summary>
public static class ChatbotMatcher
{
    public sealed record Item(int Id, string Question, string? Keywords, string Answer);
    public sealed record Scored(int Id, double Score, double Coverage);

    public sealed record Result(Scored? Best, IReadOnlyList<Scored> Related, IReadOnlyList<Scored> DidYouMean)
    {
        public bool Matched => Best is not null;
    }

    /// <summary>Score needed for a confident answer; below it (but at least <see cref="SuggestScore"/>) "did you mean".</summary>
    public const double AnswerScore = 2.5;
    public const double SuggestScore = 1.5;

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "a","an","the","i","me","my","we","our","us","you","your","it","its","is","are","am","was","were","be","been","do","does","did",
        "can","could","would","should","will","shall","may","might","must","to","of","in","on","at","for","from","by","with","about","into",
        "and","or","but","so","if","then","than","that","this","these","those","there","here","what","which","who","whom","how","why","when",
        "where","please","pls","plz","kindly","want","need","like","get","got","let","know","tell","help","any","some","all","just","also","again",
        "have","has","had","not","no","yes","ok","okay","hi","hello","hey","thank","thanks","one","way","option","able","possible","use","using",
    };

    /// <summary>Words that mean the same thing here → one stem.</summary>
    private static readonly Dictionary<string, string> Same = new(StringComparer.Ordinal)
    {
        ["paid"] = "pay", ["payment"] = "pay", ["payments"] = "pay", ["paying"] = "pay", ["receipt"] = "pay", ["collection"] = "pay", ["collect"] = "pay",
        ["quote"] = "quotation", ["quotes"] = "quotation", ["estimate"] = "quotation", ["proposal"] = "quotation", ["quotations"] = "quotation",
        ["bill"] = "invoice", ["bills"] = "invoice", ["billing"] = "invoice", ["invoices"] = "invoice",
        ["client"] = "customer", ["clients"] = "customer", ["customers"] = "customer",
        ["staff"] = "employee", ["employees"] = "employee", ["salesman"] = "employee", ["agent"] = "employee",
        ["pwd"] = "password", ["passcode"] = "password", ["pin"] = "password",
        ["signin"] = "login", ["logon"] = "login", ["log"] = "login", ["sign"] = "login", ["logging"] = "login",
        ["mail"] = "email", ["e-mail"] = "email", ["emails"] = "email", ["gmail"] = "email",
        ["wa"] = "whatsapp", ["whatsap"] = "whatsapp", ["watsapp"] = "whatsapp", ["whatapp"] = "whatsapp",
        ["remove"] = "delete", ["erase"] = "delete", ["cancel"] = "delete",
        ["create"] = "add", ["new"] = "add", ["creating"] = "add", ["created"] = "add", ["register"] = "add", ["insert"] = "add", ["make"] = "add",
        ["change"] = "edit", ["update"] = "edit", ["modify"] = "edit", ["correct"] = "edit", ["amend"] = "edit",
        ["renewal"] = "renew", ["renewals"] = "renew", ["renewing"] = "renew",
        ["remind"] = "reminder", ["reminders"] = "reminder", ["notification"] = "reminder", ["notifications"] = "reminder", ["alert"] = "reminder", ["alerts"] = "reminder",
        ["doc"] = "document", ["docs"] = "document", ["file"] = "document", ["files"] = "document", ["attachment"] = "document",
        ["download"] = "export", ["excel"] = "export", ["xlsx"] = "export",
        ["followup"] = "follow", ["follow-up"] = "follow", ["followups"] = "follow",
        ["expire"] = "expiry", ["expiring"] = "expiry", ["expired"] = "expiry", ["expires"] = "expiry", ["expiration"] = "expiry",
        ["unpaid"] = "outstanding", ["due"] = "outstanding", ["pending"] = "outstanding", ["balance"] = "outstanding", ["receivable"] = "outstanding",
        ["permission"] = "access", ["permissions"] = "access", ["rights"] = "access", ["role"] = "access", ["roles"] = "access",
        ["plan"] = "subscription", ["license"] = "subscription", ["licence"] = "subscription", ["package"] = "subscription",
        ["assigned"] = "assign", ["reassign"] = "assign", ["transfer"] = "assign", ["allocate"] = "assign",
    };

    public static List<string> Words(string? text)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length == 0) return;
            var w = sb.ToString();
            sb.Clear();
            if (Same.TryGetValue(w, out var same)) { list.Add(Stem(same)); return; }
            if (Stop.Contains(w)) return;
            if (w.Length >= 5 && Same.Keys.FirstOrDefault(k => k.Length >= 5 && OneEdit(w, k)) is { } typo) { list.Add(Stem(Same[typo])); return; }   // "pyment" → "payment"
            w = Stem(w);
            if (Same.TryGetValue(w, out same)) w = Stem(same);
            if (w.Length > 1) list.Add(w);
        }
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch == '-' && sb.Length > 0) continue;   // "follow-up" → "followup"
            else Flush();
        }
        Flush();
        return list;
    }

    private static string Stem(string w)
    {
        if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal)) w = w[..^3] + "y";
        else if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) && !w.EndsWith("us", StringComparison.Ordinal)) w = w[..^1];
        if (w.Length > 5 && w.EndsWith("ing", StringComparison.Ordinal)) w = w[..^3];
        else if (w.Length > 4 && w.EndsWith("ed", StringComparison.Ordinal)) w = w[..^2];
        if (w.Length > 3 && w.EndsWith('e')) w = w[..^1];
        return w;
    }

    /// <summary>Hi / thanks / bye → a friendly line (null = a real question).</summary>
    public static string? SmallTalk(string? text, string? userFirstName)
    {
        var t = new string((text ?? string.Empty).ToLowerInvariant().Where(c => char.IsLetter(c) || c == ' ').ToArray()).Trim();
        if (t.Length == 0) return null;
        string[] hello = ["hi", "hello", "hey", "hii", "helo", "good morning", "good afternoon", "good evening", "hi there", "hello there", "salam", "namaste"];
        string[] thanks = ["thanks", "thank you", "thank u", "thx", "ty", "ok thanks", "great thanks", "thanks a lot", "thank you so much", "ok", "okay", "great", "super", "cool", "nice"];
        string[] bye = ["bye", "goodbye", "see you", "bye bye"];
        var name = string.IsNullOrWhiteSpace(userFirstName) ? string.Empty : " " + userFirstName.Trim();
        if (hello.Contains(t)) return $"Hello{name}! Ask me anything about the Business Panel - for example \"how do I record a payment?\" - or pick a question below.";
        if (thanks.Contains(t)) return "You're welcome! Anything else I can help with?";
        if (bye.Contains(t)) return "Goodbye! The help button is always here at the bottom right.";
        return null;
    }

    public static Result Find(string? text, IEnumerable<Item> items)
    {
        var ranked = Rank(text, items);
        var top = ranked.FirstOrDefault();
        // half the words found is enough only with a strong score ("weather today" must not answer with the Dashboard)
        if (top is not null && top.Score >= AnswerScore && (top.Coverage > 0.5 || (top.Coverage >= 0.5 && top.Score >= 5)))
            return new Result(top, ranked.Skip(1).Where(s => s.Score >= SuggestScore).Take(3).ToList(), []);
        return new Result(null, [], ranked.Where(s => s.Score >= SuggestScore).Take(3).ToList());
    }

    /// <summary>Every question that shares a word with the text, best first.</summary>
    public static List<Scored> Rank(string? text, IEnumerable<Item> items)
    {
        var query = Words(text).Distinct().ToList();
        if (query.Count == 0) return [];

        var scored = new List<Scored>();
        foreach (var item in items)
        {
            var kw = Words(item.Keywords).ToHashSet();
            var q = Words(item.Question).ToHashSet();
            var a = Words(item.Answer).ToHashSet();
            double score = 0;
            var found = 0;
            foreach (var w in query)
            {
                var best = Points(w, kw, 3);
                best = Math.Max(best, Points(w, q, 2));
                best = Math.Max(best, Points(w, a, 0.5));
                if (best >= 1) found++;
                score += best;
            }
            if (score <= 0) continue;
            var coverage = (double)found / query.Count;
            // the whole question typed (or nearly) → sure
            if (q.Count > 0 && q.All(query.Contains)) score += 3;
            // long stored questions with many words the user did not type rank a little lower
            score -= 0.1 * q.Count(w => !query.Contains(w));
            scored.Add(new Scored(item.Id, Math.Round(score, 2), Math.Round(coverage, 2)));
        }

        return scored.OrderByDescending(s => s.Score).ThenByDescending(s => s.Coverage).ToList();
    }

    private static double Points(string word, HashSet<string> words, double weight)
    {
        if (words.Contains(word)) return weight;
        if (word.Length >= 5)
            foreach (var w in words)
                if (Math.Abs(w.Length - word.Length) <= 1 && w.Length >= 5 && OneEdit(word, w)) return weight * 0.7;
        return 0;
    }

    /// <summary>True when a and b differ by at most one letter (typed wrong, missing, extra or two letters swapped).</summary>
    private static bool OneEdit(string a, string b)
    {
        if (a == b) return true;
        if (a.Length == b.Length)
        {
            var diff = new List<int>();
            for (var i = 0; i < a.Length && diff.Count <= 2; i++) if (a[i] != b[i]) diff.Add(i);
            if (diff.Count == 1) return true;
            return diff.Count == 2 && diff[1] == diff[0] + 1 && a[diff[0]] == b[diff[1]] && a[diff[1]] == b[diff[0]];
        }
        var (s, l) = a.Length < b.Length ? (a, b) : (b, a);
        int x = 0, y = 0, edits = 0;
        while (x < s.Length && y < l.Length)
        {
            if (s[x] == l[y]) { x++; y++; continue; }
            if (++edits > 1) return false;
            y++;
        }
        return true;
    }
}
