[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$files = Get-ChildItem -LiteralPath $PSScriptRoot -Filter "*.ps1" -File
$failed = $false
foreach ($file in $files) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $file.FullName,
        [ref]$tokens,
        [ref]$errors
    )
    if ($errors.Count -gt 0) {
        $failed = $true
        Write-Error "$($file.Name): $($errors -join '; ')"
    }
}
if ($failed) {
    exit 1
}
Write-Host "All repository safety PowerShell scripts parsed successfully."
