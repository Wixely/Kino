[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $SkipRestore
)

# Restores with a vulnerability audit, checks formatting, builds and runs the tests. The ffmpeg
# integration tests return early when ffmpeg is not on PATH, so install it for a complete run.
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    if (-not $SkipRestore) {
        dotnet restore Kino.slnx -p:NuGetAudit=true -p:NuGetAuditMode=all
        if ($LASTEXITCODE -ne 0) { throw 'Restore or NuGet audit failed.' }
    }

    dotnet format Kino.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Formatting verification failed.' }

    dotnet build Kino.slnx -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
        Write-Warning 'ffmpeg is not on PATH; the rendering integration tests will not exercise ffmpeg.'
    }

    $results = Join-Path $repoRoot 'artifacts/test-results'
    New-Item -ItemType Directory -Force $results | Out-Null
    dotnet test tests/Kino.Tests/Kino.Tests.csproj -c $Configuration --no-build --logger 'trx;LogFileName=Kino.Tests.trx' --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally {
    Pop-Location
}
