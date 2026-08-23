using System.Text.RegularExpressions;
using MizEdit.Services;

using var translator = new OllamaTranslationService();

var cases = new[]
{
    "Proceed to waypoint 3 and contact AWACS on 251.000 MHz.",
    "Pilot {name}, contact AWACS on 251.000 MHz. File: ResKey_Action_1033. Progress: %d%%.",
    "FORD 51: CONTACT SHARJAH TOWER ON 250.2."
};

foreach (var source in cases)
{
    var translated = await translator.TranslateAsync(source);
    if (!Regex.IsMatch(translated, @"\p{IsCyrillic}"))
        throw new InvalidOperationException($"No Russian text in translation: {translated}");

    Console.WriteLine($"SOURCE: {source}");
    Console.WriteLine($"RESULT: {translated}");
}

var protectedResult = await translator.TranslateAsync(cases[1]);
foreach (var token in new[] { "{name}", "251.000", "ResKey_Action_1033", "%d%%" })
{
    if (!protectedResult.Contains(token, StringComparison.Ordinal))
        throw new InvalidOperationException($"Protected token was changed: {token}");
}

const string russian = "Следуйте к точке маршрута 3 и свяжитесь с ДРЛО.";
if (!string.Equals(await translator.TranslateAsync(russian), russian, StringComparison.Ordinal))
    throw new InvalidOperationException("Existing Russian text was modified.");

Console.WriteLine("OLLAMA SMOKE PASS");
