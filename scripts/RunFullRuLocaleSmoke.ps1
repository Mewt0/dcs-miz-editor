param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot = Split-Path -Parent $PSScriptRoot
$assemblyPath = Join-Path $repoRoot "bin\$Configuration\net10.0-windows\mizedit.dll"
[Reflection.Assembly]::LoadFrom($assemblyPath) | Out-Null

$smokeRoot = Join-Path $repoRoot ".codex-build\full-ru-locale-smoke"
$sourceRoot = Join-Path $smokeRoot "source"
$sourceMiz = Join-Path $smokeRoot "source.miz"
$outputMiz = Join-Path $smokeRoot "output.miz"

if (Test-Path -LiteralPath $smokeRoot) {
    Rename-Item -LiteralPath $smokeRoot -NewName ("full-ru-locale-smoke.old-" + [Guid]::NewGuid().ToString("N"))
}

New-Item -ItemType Directory -Path (Join-Path $sourceRoot "l10n\DEFAULT") -Force | Out-Null
Set-Content -LiteralPath (Join-Path $sourceRoot "mission") -Encoding UTF8 -Value 'mission = { name = "DictKey_Name" }'
Set-Content -LiteralPath (Join-Path $sourceRoot "l10n\DEFAULT\dictionary") -Encoding UTF8 -Value 'dictionary = { DictKey_Name = "Default name", DictKey_Line = "Default line", DictKey_Empty = "" }'
Set-Content -LiteralPath (Join-Path $sourceRoot "l10n\DEFAULT\mapResource") -Encoding UTF8 -Value 'mapResource = { ResKey_Snd_Default = "default.ogg" }'
Set-Content -LiteralPath (Join-Path $sourceRoot "l10n\DEFAULT\default.ogg") -Encoding UTF8 -Value 'audio'
[IO.Compression.ZipFile]::CreateFromDirectory($sourceRoot, $sourceMiz)

function Get-ZipEntryHash([string]$Path, [string]$Name) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entry = $zip.GetEntry($Name)
        if ($null -eq $entry) {
            return "<missing>"
        }

        $stream = $entry.Open()
        try {
            return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $zip.Dispose()
    }
}

$missionHashBefore = Get-ZipEntryHash $sourceMiz "mission"
$defaultDictionaryHashBefore = Get-ZipEntryHash $sourceMiz "l10n/DEFAULT/dictionary"
$defaultMapHashBefore = Get-ZipEntryHash $sourceMiz "l10n/DEFAULT/mapResource"

$service = [MizEdit.Services.MissionService]::new()
$session = $service.LoadMission($sourceMiz)
try {
    $session.Localization.AddLocale("RU")
    $changes = [System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]]::new()
    $changes.Add([System.Collections.Generic.KeyValuePair[string,string]]::new("DictKey_Line", "Русская строка"))
    $session.Localization.UpdateDictionaryEntriesWithDefaultFallback("RU", $changes)
    $service.SaveAsMiz($session, $outputMiz)
}
finally {
    $session.Dispose()
}

$missionHashAfter = Get-ZipEntryHash $outputMiz "mission"
$defaultDictionaryHashAfter = Get-ZipEntryHash $outputMiz "l10n/DEFAULT/dictionary"
$defaultMapHashAfter = Get-ZipEntryHash $outputMiz "l10n/DEFAULT/mapResource"

$reopened = $service.LoadMission($outputMiz)
try {
    $defaultDictionary = $reopened.Localization.GetDictionaryEntries("DEFAULT")
    $ruDictionary = $reopened.Localization.GetDictionaryEntries("RU")
    $ruMap = $reopened.Localization.LoadMapResource("RU")

    $result = [pscustomobject]@{
        MissionUnchanged = $missionHashBefore -eq $missionHashAfter
        DefaultDictionaryUnchanged = $defaultDictionaryHashBefore -eq $defaultDictionaryHashAfter
        DefaultMapUnchanged = $defaultMapHashBefore -eq $defaultMapHashAfter
        DefaultKeys = $defaultDictionary.Count
        RuKeys = $ruDictionary.Count
        RuTranslated = $ruDictionary["DictKey_Line"]
        RuFallback = $ruDictionary["DictKey_Name"]
        RuEmptyMapKeys = $ruMap.Count
        HasRuDictionary = (Get-ZipEntryHash $outputMiz "l10n/RU/dictionary") -ne "<missing>"
        HasRuMapResource = (Get-ZipEntryHash $outputMiz "l10n/RU/mapResource") -ne "<missing>"
    }
    $result | Format-List

    if (-not $result.MissionUnchanged) { throw "mission changed" }
    if (-not $result.DefaultDictionaryUnchanged) { throw "DEFAULT dictionary changed" }
    if (-not $result.DefaultMapUnchanged) { throw "DEFAULT mapResource changed" }
    if ($result.RuKeys -ne $result.DefaultKeys) { throw "RU dictionary is incomplete" }
    if ($result.RuTranslated -ne "Русская строка") { throw "RU translated value mismatch" }
    if ($result.RuFallback -ne "Default name") { throw "RU fallback value mismatch" }
    if ($result.RuEmptyMapKeys -ne 0) { throw "RU mapResource is not empty" }
    if (-not $result.HasRuDictionary) { throw "RU dictionary was not saved" }
    if (-not $result.HasRuMapResource) { throw "RU mapResource was not saved" }
}
finally {
    $reopened.Dispose()
}

Write-Host "PASS full RU localization smoke"
