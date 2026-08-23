using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MizEdit.Core;

namespace MizEdit.Services;

public sealed class OllamaTranslationService : ITranslationProvider, IDisposable
{
    public const string DefaultModel = "mizedit-translator";

    private const string SystemPrompt =
        "You are a strict DCS mission translator. Translate English mission text to natural Russian. " +
        "Return only the translated text, with no quotes, Markdown, comments, or explanations. " +
        "Translate cockpit checklist labels, menu items, commands, radio subtitles, and UI/action text even when they look short or technical. " +
        "For lines like '- R Throttle Lever: IDLE', translate the human-readable label and side abbreviation: '- Рычаг газа правого двигателя: IDLE'. " +
        "Translate L/R as левый/правый when they denote aircraft side. Translate words before and after ':' unless the part is a fixed switch state/code. " +
        "Do not translate script/resource status lines such as 'mist_4_5_107.lua loaded.'; return them unchanged. " +
        "If a line looks like Lua logic or code — if/elseif/else/then/end/return/local/function/for/while, comparisons > < == ~= <= >=, and/or/not, assignments =, counters like count = count + 1, or calls like runCommand(...), trigger.action..., Unit.getByName(...) — return the entire line unchanged. " +
        "Do not Markdown-escape punctuation: output ***ENG, HDG_end and Batch IDs with underscores, without backslashes. " +
        "Preserve callsigns, file paths, Lua code, resource keys, placeholders, coordinates, frequencies, numbers, and every __MIZEDIT_TOKEN_####__ marker exactly. " +
        "Keep standard cockpit states and radio/avionics abbreviations such as ON, OFF, IDLE, ARM, SAFE, EXT, EXTEND, RETRACT, NORM, F10, AWACS, CAP, UHF, VHF, TACAN, ILS, QNH, QNE, HÖJD, SPAK, ATT, EBK, FIR unchanged when they are switch positions, modes, or system codes. " +
        "Do not reorder coded airspace names: ANKARA FIR stays ANKARA FIR, not FIR ANKARA.";

    private static readonly Regex CyrillicRegex = new(@"\p{IsCyrillic}", RegexOptions.Compiled);
    private static readonly Regex ProtectedFragmentRegex = new(
        @"https?://\S+|(?:[A-Za-z]:\\|\\\\)[^\s""']+|\b[\w.-]+\.(?:ogg|wav|mp3|png|jpe?g|bmp|lua|miz|zip)\b|<[^>\r\n]+>|\\[nrt""'\\]|%(?:[-+#0 ]*\d*(?:\.\d+)?[A-Za-z]|%)|\{[^{}\r\n]+\}|\$[A-Za-z_]\w*|\b(?:ResKey|DictKey|CLSID)[A-Za-z0-9_.:-]*\b|\b[A-Za-z][A-Za-z0-9]*_[A-Za-z0-9_.:-]+\b|(?<![\p{L}\p{N}_])[-+]?\d+(?:[.,:/-]\d+)*(?![\p{L}\p{N}_])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ProtectedMarkerRegex = new(
        @"__MIZEDIT_TOKEN_\d{4}__",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DcsLuaApiRegex = new(
        @"\b(?:Unit|Group|StaticObject|Object|Weapon|Airbase|Controller|coalition|country|trigger|timer|world|coord|AI|atmosphere|land|env|missionCommands|mist|ctld)\s*[.:]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LuaControlFlowRegex = new(
        @"\b(?:if|else|elseif|then|end|return|local|function|for|while|repeat|until|do|break)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LuaLogicLineRegex = new(
        @"^\s*(?:(?:if|elseif|while)\s+.+\s+then|else\s*$|end\s*$|return\b.+|local\s+\w+|for\s+\w+\s*=.+\s+do|(?:\w+\.)*\w+\s*=\s*.+|(?:\w+\.)*\w+\s*(?:\+=|-=)\s*.+|(?:\w+\.)*\w+\s*[+\-*/%]=?\s*\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LuaComparisonOrBooleanRegex = new(
        @"(?:==|~=|<=|>=|<|>|\band\b|\bor\b|\bnot\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex GenericCodeCallRegex = new(
        @"^\s*[A-Za-z_]\w*(?:[.:][A-Za-z_]\w*)*\s*\([^)]*(?:[A-Za-z_]\w*|['""]|[-+]?\d)[^)]*\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ResourceStatusRegex = new(
        @"^\s*[\w./\\-]+\.(?:lua|miz|ogg|wav|mp3|png|jpe?g|bmp)\s+(?:loaded|loading|started|initialized|failed|error|missing|not\s+found)\.?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _requestTimeout;

    public OllamaTranslationService(
        string baseAddress = "http://127.0.0.1:11434/",
        TimeSpan? requestTimeout = null)
    {
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(60);
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<string> TranslateAsync(string source, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source) ||
            CyrillicRegex.IsMatch(source) ||
            LooksLikeLuaScript(source))
            return source;

        var leadingWhitespace = Regex.Match(source, @"^\s*").Value;
        var trailingWhitespace = Regex.Match(source, @"\s*$").Value;
        var coreLength = source.Length - leadingWhitespace.Length - trailingWhitespace.Length;
        if (coreLength <= 0)
            return source;

        var core = source.Substring(leadingWhitespace.Length, coreLength);
        var protectedSource = ProtectFragments(core, out var fragments);
        var request = new
        {
            model = DefaultModel,
            stream = false,
            think = false,
            keep_alive = "10m",
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = "Translate this DCS mission line to Russian:\n" + protectedSource }
            },
            options = new { temperature = 0, num_predict = 2048 }
        };

        using var body = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(_requestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync("api/chat", body, requestCancellation.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(UserMessages.Get("OllamaTimeout", _requestTimeout.TotalSeconds.ToString("0")), ex);
        }

        using (response)
        {
            string responseJson;
            try
            {
                responseJson = await response.Content.ReadAsStringAsync(requestCancellation.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(UserMessages.Get("OllamaCompletionTimeout", _requestTimeout.TotalSeconds.ToString("0")), ex);
            }

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(UserMessages.Get("OllamaHttpError", (int)response.StatusCode, responseJson));

            using var document = JsonDocument.Parse(responseJson);
            var translated = document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString()?.Trim() ?? string.Empty;
            if (translated.Length == 0)
                throw new InvalidOperationException(UserMessages.Get("OllamaEmptyResult"));

            translated = RestoreProtectedFragments(translated, fragments);
            return leadingWhitespace + translated + trailingWhitespace;
        }
    }

    internal static string RestoreProtectedFragments(
        string translated,
        IReadOnlyList<string> fragments)
    {
        var returnedMarkers = ProtectedMarkerRegex.Matches(translated)
            .Select(match => match.Value)
            .ToArray();
        if (returnedMarkers.Length != fragments.Count)
        {
            var token = fragments.Count > 0 ? fragments[0] : returnedMarkers.FirstOrDefault() ?? "unknown";
            throw new ProtectedTokenException(
                token,
                UserMessages.Get("ProtectedMarkerCount", fragments.Count, returnedMarkers.Length));
        }

        for (var index = 0; index < fragments.Count; index++)
        {
            var expected = Marker(index);
            if (!string.Equals(returnedMarkers[index], expected, StringComparison.Ordinal))
            {
                throw new ProtectedTokenException(
                    fragments[index],
                    UserMessages.Get("ProtectedMarkerPosition", expected, index + 1, returnedMarkers[index]));
            }
        }

        for (var index = 0; index < fragments.Count; index++)
            translated = translated.Replace(Marker(index), fragments[index], StringComparison.Ordinal);

        return translated;
    }

    internal static bool LooksLikeLuaScript(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return false;

        var hasCallSyntax = source.Contains('(') && source.Contains(')');
        if (hasCallSyntax && DcsLuaApiRegex.IsMatch(source))
            return true;
        if (ResourceStatusRegex.IsMatch(source))
            return true;
        if (LooksLikeLuaLogic(source))
            return true;

        var hasBlockStructure = source.Contains('\n') || source.Contains('\r');
        var hasAssignment = source.Contains('=');
        return hasBlockStructure && hasAssignment && LuaControlFlowRegex.IsMatch(source);
    }

    private static bool LooksLikeLuaLogic(string source)
    {
        var trimmed = source.Trim();
        if (LuaLogicLineRegex.IsMatch(trimmed))
            return true;
        if ((trimmed.StartsWith("if ", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("elseif ", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("while ", StringComparison.OrdinalIgnoreCase)) &&
            LuaComparisonOrBooleanRegex.IsMatch(trimmed))
        {
            return true;
        }

        if (GenericCodeCallRegex.IsMatch(trimmed) &&
            (trimmed.Contains('.', StringComparison.Ordinal) ||
             trimmed.Contains(':', StringComparison.Ordinal) ||
             trimmed.Contains('=', StringComparison.Ordinal) ||
             trimmed.Contains('"', StringComparison.Ordinal) ||
             trimmed.Contains('\'', StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    internal static string ProtectFragments(string source, out List<string> fragments)
    {
        var collected = new List<string>();
        var protectedSource = ProtectedFragmentRegex.Replace(source, match =>
        {
            var index = collected.Count;
            collected.Add(match.Value);
            return Marker(index);
        });
        fragments = collected;
        return protectedSource;
    }

    internal static string Marker(int index) => $"__MIZEDIT_TOKEN_{index:D4}__";

    public void Dispose() => _httpClient.Dispose();
}
