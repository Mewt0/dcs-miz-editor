param(
    [ValidateSet("export", "import", "gemini")]
    [string]$Mode = "export",

    [string]$TrainingRoot = $env:DCS_F4E_TRAINING_ROOT,
    [string]$WorkRoot = "",
    [int]$ChunkSize = 80,
    [switch]$ReplaceOriginals
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($TrainingRoot)) {
    throw "Pass -TrainingRoot or set DCS_F4E_TRAINING_ROOT."
}
if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    $WorkRoot = Join-Path $repoRoot ".codex-build\f4e-training-agent-translation"
}
$translatorProject = Join-Path $repoRoot "tests\F4E.Translation\F4E.Translation.csproj"
$validatorProject = Join-Path $repoRoot "tests\GreenLine.Integration\GreenLine.Integration.csproj"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$runRoot = Join-Path $WorkRoot ("run-" + $stamp)
$outputRoot = Join-Path $runRoot "translated-miz"
$backupRoot = Join-Path $runRoot "backup-originals"
$reportPath = Join-Path $runRoot "F4E-Training-Translation-Report.md"

$missions = @(
    "F-4E_TR_Caucasus_01_PILOT_STARTUP.miz",
    "F-4E_TR_Caucasus_01_WSO_STARTUP.miz",
    "F-4E_TR_Caucasus_02_PILOT_TAXI.miz",
    "F-4E_TR_Caucasus_03_PILOT_TAKEOFF.miz",
    "F-4E_TR_Caucasus_04_PILOT_VISUAL_LANDING.miz",
    "F-4E_TR_Caucasus_05_WSO_Radar Basics.miz",
    "F-4E_TR_Caucasus_06_Pilot_CAA.miz",
    "F-4E_TR_Caucasus_07_PILOT_Dive Toss.miz",
    "F-4E_TR_Caucasus_08_PILOT_AGM-65 Maverick.miz"
)

function Invoke-Checked {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
    }
}

function Get-MissionPath {
    param([string]$MissionName)
    $path = Join-Path $TrainingRoot $MissionName
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Mission not found: $path"
    }
    return $path
}

New-Item -ItemType Directory -Force -Path $runRoot, $outputRoot, $backupRoot | Out-Null

$report = New-Object System.Collections.Generic.List[string]
$report.Add("# F-4E Training Translation")
$report.Add("")
$report.Add("- Mode: $Mode")
$report.Add("- Training root: $TrainingRoot")
$report.Add("- Work root: $WorkRoot")
$report.Add("- Run root: $runRoot")
$report.Add("- Replace originals: $ReplaceOriginals")
$report.Add("")

Invoke-Checked "dotnet" @("build", $translatorProject, "-c", "Release", "-v", "minimal")

if ($Mode -eq "gemini" -and [string]::IsNullOrWhiteSpace($env:GEMINI_API_KEY)) {
    throw "GEMINI_API_KEY is required for gemini mode."
}

foreach ($missionName in $missions) {
    $missionPath = Get-MissionPath $missionName
    $missionStem = [System.IO.Path]::GetFileNameWithoutExtension($missionName)
    $missionExportRoot = Join-Path $WorkRoot $missionStem
    $translatedPath = Join-Path $outputRoot $missionName

    if ($Mode -eq "export") {
        New-Item -ItemType Directory -Force -Path $missionExportRoot | Out-Null
        Invoke-Checked "dotnet" @("run", "--project", $translatorProject, "-c", "Release", "--", $missionPath, $missionExportRoot, "--agent-export", "$ChunkSize")
        $report.Add("- Exported: $missionName -> $missionExportRoot")
        continue
    }

    if ($Mode -eq "import") {
        $translatedJsonlRoot = Join-Path $missionExportRoot "translated"
        if (-not (Test-Path -LiteralPath $translatedJsonlRoot)) {
            throw "Translated JSONL folder not found: $translatedJsonlRoot"
        }

        Invoke-Checked "dotnet" @("run", "--project", $translatorProject, "-c", "Release", "--", $missionPath, $translatedPath, "--agent-import", $translatedJsonlRoot)
    }
    elseif ($Mode -eq "gemini") {
        Invoke-Checked "dotnet" @("run", "--project", $translatorProject, "-c", "Release", "--", $missionPath, $translatedPath, "--gemini")
    }

    Invoke-Checked "dotnet" @("run", "--project", $validatorProject, "-c", "Release", "--no-restore", "--", $translatedPath)

    if ($ReplaceOriginals) {
        $backupPath = Join-Path $backupRoot $missionName
        Copy-Item -LiteralPath $missionPath -Destination $backupPath -Force
        Copy-Item -LiteralPath $translatedPath -Destination $missionPath -Force
        Invoke-Checked "dotnet" @("run", "--project", $validatorProject, "-c", "Release", "--no-restore", "--", $missionPath)
        $report.Add("- Replaced: $missionName; backup: $backupPath")
    }
    else {
        $report.Add("- Built and validated: $missionName -> $translatedPath")
    }
}

$report.Add("")
$report.Add("## Notes")
$report.Add("")
$report.Add("- export mode creates JSONL chunks for agents and does not change DCS files.")
$report.Add("- import mode expects each mission folder to contain translated\\chunk-*.jsonl.")
$report.Add("- gemini mode uses GEMINI_API_KEY and writes translated .miz files before optional replacement.")
$report.Add("- original files are copied to backup-originals before any replacement.")

Set-Content -LiteralPath $reportPath -Value $report -Encoding UTF8
Write-Output "REPORT $reportPath"
