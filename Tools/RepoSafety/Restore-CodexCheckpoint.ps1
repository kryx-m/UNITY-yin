[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^checkpoint/')]
    [string]$Checkpoint
)

. (Join-Path $PSScriptRoot "Common.ps1")

$config = Get-RepoSafetyConfig
$suffix = ($Checkpoint.Substring("checkpoint/".Length).ToLowerInvariant() -replace '[^a-z0-9-]+', '-').Trim("-")
if ([string]::IsNullOrWhiteSpace($suffix)) {
    throw "Checkpoint name cannot be converted to a safe recovery branch."
}

$branch = "recovery/$suffix"
$destination = Join-Path $config.worktreeRoot ("recovery-" + (Get-Date -Format "yyyyMMdd-HHmmss") + "-" + $suffix)
$repositories = @(
    [pscustomobject]@{
        Name = "Unity"
        Repository = $config.unityRepo
        Worktree = (Join-Path $destination "unity")
    },
    [pscustomobject]@{
        Name = "Python"
        Repository = $config.pythonRepo
        Worktree = (Join-Path $destination "python")
    }
)

if (Test-Path -LiteralPath $destination) {
    throw "Recovery destination already exists: $destination"
}

foreach ($entry in $repositories) {
    Assert-CleanRepository $entry.Repository
    [void](Invoke-Git -Repository $entry.Repository -Arguments @("fetch", "--tags", "origin"))
    [void](Invoke-Git -Repository $entry.Repository -Arguments @(
        "rev-parse", "--verify", "refs/tags/$Checkpoint"
    ))
    & git -C $entry.Repository show-ref --verify --quiet "refs/heads/$branch"
    if ($LASTEXITCODE -eq 0) {
        throw "Recovery branch already exists in $($entry.Name): $branch"
    }
}

New-Item -ItemType Directory -Force -Path $destination | Out-Null
foreach ($entry in $repositories) {
    [void](Invoke-Git -Repository $entry.Repository -Arguments @(
        "worktree", "add", "-b", $branch, $entry.Worktree, "refs/tags/$Checkpoint"
    ))
    [void](Invoke-Git -Repository $entry.Worktree -Arguments @(
        "push", "--set-upstream", "origin", $branch
    ))
}

Write-Host "Paired recovery worktrees created without modifying the failed branches."
Write-Host "Recovery root: $destination"
Write-Host "Unity: $($repositories[0].Worktree)"
Write-Host "Python: $($repositories[1].Worktree)"
Write-Host "Branch: $branch"
