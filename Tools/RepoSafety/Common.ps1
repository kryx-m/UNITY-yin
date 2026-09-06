Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Git {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Repository,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $output = & git -C $Repository @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $detail = ($output | Out-String).Trim()
        throw "git $($Arguments -join ' ') failed in $Repository$([Environment]::NewLine)$detail"
    }
    return @($output)
}

function Get-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath(
        [Environment]::ExpandEnvironmentVariables($Path)
    )
}

function Get-RepoSafetyConfig {
    $unityRoot = Get-NormalizedPath (Join-Path $PSScriptRoot "..\..")
    $configPath = Join-Path $unityRoot ".codex\repo-safety.local.json"
    if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
        throw "Missing local configuration: $configPath. Copy .codex/repo-safety.example.json and fill in the local paths."
    }

    $config = Get-Content -Raw -LiteralPath $configPath | ConvertFrom-Json
    foreach ($name in @("unityRepo", "pythonRepo", "worktreeRoot", "unityExe", "pythonExe")) {
        if (-not $config.PSObject.Properties[$name] -or [string]::IsNullOrWhiteSpace([string]$config.$name)) {
            throw "Configuration property '$name' is required in $configPath."
        }
        $config.$name = Get-NormalizedPath ([string]$config.$name)
    }

    foreach ($repo in @($config.unityRepo, $config.pythonRepo)) {
        if (-not (Test-Path -LiteralPath $repo -PathType Container)) {
            throw "Repository directory does not exist: $repo"
        }
        [void](Invoke-Git -Repository $repo -Arguments @("rev-parse", "--show-toplevel"))
    }
    if (-not (Test-Path -LiteralPath $config.unityExe -PathType Leaf)) {
        throw "Unity executable does not exist: $($config.unityExe)"
    }
    if (-not (Test-Path -LiteralPath $config.pythonExe -PathType Leaf)) {
        throw "Python executable does not exist: $($config.pythonExe)"
    }
    return $config
}

function Assert-CleanRepository {
    param([Parameter(Mandatory = $true)][string]$Repository)
    $changes = Invoke-Git -Repository $Repository -Arguments @("status", "--porcelain")
    if ($changes.Count -gt 0) {
        $detail = $changes -join [Environment]::NewLine
        throw "Repository has uncommitted changes and will not be touched: $Repository$([Environment]::NewLine)$detail"
    }
}

function Assert-OriginMain {
    param([Parameter(Mandatory = $true)][string]$Repository)
    [void](Invoke-Git -Repository $Repository -Arguments @("remote", "get-url", "origin"))
    [void](Invoke-Git -Repository $Repository -Arguments @("fetch", "--prune", "origin", "main"))
    [void](Invoke-Git -Repository $Repository -Arguments @("rev-parse", "--verify", "refs/remotes/origin/main"))
}

function Assert-SafeRepositoryFiles {
    param([Parameter(Mandatory = $true)][string]$Repository)

    $maxGitBytes = 100MB
    $lfsThresholdBytes = 50MB
    $textScanLimitBytes = 5MB
    $textExtensions = @(
        ".asset", ".bat", ".cfg", ".cs", ".env", ".ini", ".json", ".md",
        ".ps1", ".py", ".sh", ".txt", ".xml", ".yaml", ".yml"
    )
    $sensitiveValuePattern = '(?im)\b(api[_-]?key|secret|password|passwd|private[_-]?key|token)\b[ \t]*[:=][ \t]*["'']?[A-Za-z0-9_./+=-]{12,}'
    $privateKeyPattern = '-----BEGIN [A-Z ]*PRIVATE KEY-----'
    $sensitiveNamePattern = '(^|[\\/])(\.env|id_rsa|id_ed25519|.*\.pem)$'

    $relativeFiles = Invoke-Git -Repository $Repository -Arguments @(
        "ls-files", "--cached", "--others", "--exclude-standard"
    )
    foreach ($relative in $relativeFiles) {
        if ([string]::IsNullOrWhiteSpace([string]$relative)) { continue }
        if ($relative -match $sensitiveNamePattern -and $relative -notmatch '\.example$') {
            throw "Sensitive file name is not allowed: $relative"
        }

        $fullPath = Join-Path $Repository ([string]$relative)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
        $file = Get-Item -LiteralPath $fullPath
        if ($file.Length -gt $maxGitBytes) {
            throw "File exceeds GitHub's 100 MiB ordinary Git limit: $relative ($($file.Length) bytes)"
        }
        if ($file.Length -gt $lfsThresholdBytes) {
            $attribute = Invoke-Git -Repository $Repository -Arguments @(
                "check-attr", "filter", "--", [string]$relative
            )
            if (($attribute -join [Environment]::NewLine) -notmatch 'filter:\s+lfs') {
                throw "File larger than 50 MiB must use Git LFS: $relative ($($file.Length) bytes)"
            }
        }

        if ($file.Length -le $textScanLimitBytes -and $textExtensions -contains $file.Extension.ToLowerInvariant()) {
            try {
                $content = [System.IO.File]::ReadAllText($file.FullName)
            }
            catch {
                continue
            }
            if ($content -match $privateKeyPattern -or $content -match $sensitiveValuePattern) {
                throw "Possible credential detected in: $relative"
            }
        }
    }
}

function Get-SafeSlug {
    param([Parameter(Mandatory = $true)][string]$Value)
    $slug = $Value.Trim().ToLowerInvariant() -replace '[^a-z0-9-]+', '-'
    $slug = $slug.Trim("-")
    if ([string]::IsNullOrWhiteSpace($slug)) {
        throw "Task name must contain at least one ASCII letter or digit."
    }
    return $slug
}

function Get-TaskStatePath {
    param([Parameter(Mandatory = $true)][string]$TaskDirectory)
    return Join-Path (Get-NormalizedPath $TaskDirectory) ".repo-safety-task.json"
}

function Read-TaskState {
    param([Parameter(Mandatory = $true)][string]$TaskDirectory)
    $statePath = Get-TaskStatePath $TaskDirectory
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        throw "Task state not found: $statePath"
    }
    return Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
}

function Write-TaskState {
    param(
        [Parameter(Mandatory = $true)][string]$TaskDirectory,
        [Parameter(Mandatory = $true)]$State
    )
    $statePath = Get-TaskStatePath $TaskDirectory
    $State | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $statePath -Encoding utf8
}
