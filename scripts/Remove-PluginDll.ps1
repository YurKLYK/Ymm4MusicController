[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$PluginName = "BrowserMusicController",
    [string]$YMM4DirPath = "C:\YukkuriMovieMaker_v4\",
    [string]$WorkspaceRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$KillYmmProcess,
    [switch]$IncludeBuildOutputs = $true
)

$ErrorActionPreference = "Stop"

function Remove-TargetFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (!(Test-Path -LiteralPath $Path)) {
        return "Missing"
    }

    if ($WhatIfPreference) {
        $null = $PSCmdlet.ShouldProcess($Path, "Remove file")
        return "WhatIf"
    }

    if ($PSCmdlet.ShouldProcess($Path, "Remove file")) {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
        return "Removed"
    }

    return "Skipped"
}

$removed = New-Object System.Collections.Generic.List[string]
$whatIfTargets = New-Object System.Collections.Generic.List[string]
$failed = New-Object System.Collections.Generic.List[string]

if ($KillYmmProcess) {
    Get-Process -Name "YukkuriMovieMaker*" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

$candidateFiles = @(
    (Join-Path $YMM4DirPath ("user\\plugin\\{0}.dll" -f $PluginName)),
    (Join-Path $YMM4DirPath ("user\\plugin\\{0}\\{0}.dll" -f $PluginName))
)

if ($IncludeBuildOutputs) {
    $workspaceMatches = Get-ChildItem -LiteralPath $WorkspaceRoot -Recurse -File -Filter ("{0}.dll" -f $PluginName) -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName

    if ($workspaceMatches) {
        $candidateFiles += $workspaceMatches
    }
}

$candidateFiles = $candidateFiles | Sort-Object -Unique

foreach ($file in $candidateFiles) {
    try {
        $result = Remove-TargetFile -Path $file
        if ($result -eq "Removed") {
            $removed.Add($file)
        }
        elseif ($result -eq "WhatIf") {
            $whatIfTargets.Add($file)
        }
    }
    catch {
        $failed.Add(("{0} :: {1}" -f $file, $_.Exception.Message))
    }
}

Write-Host "PluginName      : $PluginName"
Write-Host "WorkspaceRoot   : $WorkspaceRoot"
Write-Host "YMM4DirPath     : $YMM4DirPath"
Write-Host "KillYmmProcess  : $KillYmmProcess"
Write-Host "IncludeBuildOut : $IncludeBuildOutputs"
Write-Host ""

if ($removed.Count -gt 0) {
    Write-Host "Removed files:" -ForegroundColor Green
    $removed | ForEach-Object { Write-Host " - $_" }
}
else {
    Write-Host "Removed files: none"
}

if ($whatIfTargets.Count -gt 0) {
    Write-Host ""
    Write-Host "WhatIf targets:" -ForegroundColor Cyan
    $whatIfTargets | ForEach-Object { Write-Host " - $_" }
}

if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host "Failed files:" -ForegroundColor Yellow
    $failed | ForEach-Object { Write-Host " - $_" }
    exit 1
}

exit 0
