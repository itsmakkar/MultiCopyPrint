using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MultiCopyPrint.Services;

public class ParseResult
{
    public Dictionary<int, int> PageCopies { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public bool UsedAi { get; set; }
}

public static class InstructionParserService
{
    private static readonly HttpClient Http = new();

    // Detects Hinglish or complex relative phrases that need AI instead of simple regex
    private static readonly Regex NeedsAiRegex = new(
        @"\b(sab|sara|saare|wala|waala|chhod|chor|except|skip|front|back|starting|ending|odd|even|baki|baaki)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<ParseResult> ParseAsync(string rawText, int totalPages, string? apiKey = null)
    {
        var result = new ParseResult();
        if (string.IsNullOrWhiteSpace(rawText) || totalPages <= 0)
            return result;

        string sanitized = SanitizeRawInput(rawText);

        // Layer 1: Try Offline Regex if no complex/Hinglish keywords are present
        if (!NeedsAiRegex.IsMatch(sanitized) && TryParseWithRegex(sanitized, totalPages, result))
            return result;

        // Layer 2: Claude API Fallback
        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            result.Warnings.Add("Complex/Hinglish text detected, but ANTHROPIC_API_KEY is not set.");
            return result;
        }

        await ParseWithClaudeAsync(StripPii(sanitized), totalPages, apiKey, result);
        return result;
    }

    public static string SanitizeRawInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // 1. Strip WhatsApp bracketed headers: e.g. [10:30 AM, 15/11/2026] or [15/11/2026, 10:30:15]
        var cleaned = Regex.Replace(input, @"\[\s*\d{1,2}[:\/]\d{1,2}.*?\]", " ");

        // 2. Strip standard WhatsApp exported chat headers: e.g. "15/11/2026, 10:30 - Customer:" or "15/11/26, 10:30 am - "
        cleaned = Regex.Replace(cleaned, @"\b\d{1,2}[\/\-\.]\d{1,2}[\/\-\.]\d{2,4}(?:,?\s+\d{1,2}:\d{2}(?::\d{2})?(?:\s*[ap]m)?)?(?:\s*[-–:]\s*[^:\n]+:)?", " ", RegexOptions.IgnoreCase);

        // 3. Strip standalone timestamps with AM/PM or seconds: e.g. "10:30 AM" or "10:30:45"
        cleaned = Regex.Replace(cleaned, @"\b\d{1,2}:\d{2}(?::\d{2})?\s*[ap]m\b", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\b\d{1,2}:\d{2}:\d{2}\b", " ");

        // 4. Strip phone numbers: e.g. +91 98765 43210, +91-9876543210, 9876543210
        cleaned = Regex.Replace(cleaned, @"(?:\+91[\-\s]?)?[6-9]\d{4}[\-\s]?\d{5}\b", " ");

        return cleaned;
    }

    public static bool TryParseWithRegex(string text, int totalPages, ParseResult result)
    {
        text = SanitizeRawInput(text);
        bool matchedAny = false;

        // 1. Check "all pages = X" or "all 50"
        var allMatch = Regex.Match(text, @"\b(?:all(?:\s+pages)?)\s*[:=\-x×]?\s*(\d+)", RegexOptions.IgnoreCase);
        if (allMatch.Success && int.TryParse(allMatch.Groups[1].Value, out int allCopies))
        {
            for (int p = 1; p <= totalPages; p++)
                result.PageCopies[p] = ClampCopies(allCopies);
            matchedAny = true;
        }

        // 2. Check "last page 100"
        var lastMatch = Regex.Match(text, @"\blast(?:\s+page|\s+pg)?\s*[:=\-x×]?\s*(\d+)", RegexOptions.IgnoreCase);
        if (lastMatch.Success && int.TryParse(lastMatch.Groups[1].Value, out int lastCopies))
        {
            ApplyValidated(totalPages, lastCopies, totalPages, result);
            matchedAny = true;
        }

        // 3. Unified pattern:
        // Group 1..3: Range e.g. "pg 2-4 - 5" or "1-3: 10"
        // Group 4..5: Prefixed single e.g. "pg 1 - 10" or "p2: 5"
        // Group 6..7: Bare number single with operator e.g. "1 - 10", "2: 15", "3 x 20", "1-3" (page 1, 3 copies)
        var unifiedPattern = @"(?:\b(?:p(?:age|g)?s?\s*)?(\d+)\s*(?:to|\-|\.\.)\s*(\d+)\s*(?:[:=\-x×]|\s+)\s*(\d+))|(?:\b(?:p(?:age|g)?s?\s*)(\d+)\s*(?:[:=\-x×]|\s+)\s*(\d+))|(?:\b(\d+)\s*[:=\-x×]\s*(\d+))";

        foreach (Match m in Regex.Matches(text, unifiedPattern, RegexOptions.IgnoreCase))
        {
            if (m.Groups[1].Success) // Range match
            {
                int start = int.Parse(m.Groups[1].Value);
                int end = int.Parse(m.Groups[2].Value);
                int copies = int.Parse(m.Groups[3].Value);
                for (int p = Math.Min(start, end); p <= Math.Max(start, end); p++)
                    ApplyValidated(p, copies, totalPages, result);
                matchedAny = true;
            }
            else if (m.Groups[4].Success) // Prefixed single page match
            {
                int page = int.Parse(m.Groups[4].Value);
                int copies = int.Parse(m.Groups[5].Value);
                ApplyValidated(page, copies, totalPages, result);
                matchedAny = true;
            }
            else if (m.Groups[6].Success) // Bare single page with operator
            {
                int page = int.Parse(m.Groups[6].Value);
                int copies = int.Parse(m.Groups[7].Value);
                ApplyValidated(page, copies, totalPages, result);
                matchedAny = true;
            }
        }

        return matchedAny;
    }

    private static async Task ParseWithClaudeAsync(string sanitizedText, int totalPages, string apiKey, ParseResult result)
    {
        result.UsedAi = true;
        var systemPrompt = $"You convert print shop instructions (English/Hinglish) into JSON for a PDF with exactly {totalPages} pages. " +
                           "Return ONLY a valid JSON array of objects: [{\"page\": 1, \"copies\": 10}]. No markdown, no explanation.";

        var payload = JsonSerializer.Serialize(new
        {
            model = "claude-3-5-haiku-latest",
            max_tokens = 512,
            temperature = 0,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = sanitizedText } }
        });

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var res = await Http.SendAsync(req);
            res.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var rawJson = doc.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "[]";

            // Strip markdown fences in case model returns ```json ... ```
            rawJson = CleanJsonFences(rawJson);

            var items = JsonSerializer.Deserialize<List<PageCopyDto>>(rawJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (items == null) return;

            foreach (var item in items)
                ApplyValidated(item.Page, item.Copies, totalPages, result);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"AI fallback unavailable ({ex.Message}).");
        }
    }

    public static string CleanJsonFences(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "[]";
        var cleaned = Regex.Replace(input, @"^```(?:json)?\s*|\s*```$", "", RegexOptions.Multiline | RegexOptions.IgnoreCase).Trim();
        return cleaned;
    }

    private static void ApplyValidated(int page, int copies, int totalPages, ParseResult result)
    {
        if (page < 1 || page > totalPages)
        {
            result.Warnings.Add($"Ignored Page {page} (PDF only has {totalPages} pages).");
            return;
        }
        result.PageCopies[page] = ClampCopies(copies);
    }

    private static int ClampCopies(int copies) => Math.Clamp(copies, 0, 10000);

    private static string StripPii(string input)
    {
        var noPhones = Regex.Replace(input, @"(\+91[\-\s]?)?[6-9]\d{9}\b", "[PHONE]");
        return Regex.Replace(noPhones, @"[\w\.-]+@[\w\.-]+\.\w+", "[EMAIL]");
    }

    private record PageCopyDto(int Page, int Copies);
}
