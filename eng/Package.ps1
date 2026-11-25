[CmdletBinding()]
param(
    [switch] $SkipSmokeTest,
    # The release version, such as 0.1.0 from the tag v0.1.0. Defaults to VersionPrefix in Directory.Build.props.
    [string] $Version
)

# Publishes the self-contained single-file Windows executable, runs its built-in screenshot and
# recording self-tests against the real desktop, then writes the release archive and checksums.
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    [xml] $props = Get-Content 'Directory.Build.props'
    $version = if ($Version) { $Version } else { $props.Project.PropertyGroup.VersionPrefix }
    if ($version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "'$version' is not a release version such as 0.1.0." }
    $numeric = ($version -split '-')[0]
    $publish = Join-Path $repoRoot 'artifacts/publish/win-x64'
    $release = Join-Path $repoRoot 'artifacts/release'
    Remove-Item -Recurse -Force $publish, $release -ErrorAction SilentlyContinue

    dotnet publish src/Kino.Windows/Kino.Windows.csproj -c Release -o $publish `
        "-p:Version=$version" "-p:AssemblyVersion=$numeric.0" "-p:FileVersion=$numeric.0"
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    $exe = Join-Path $publish 'Kino.exe'
    $unexpected = Get-ChildItem $publish | Where-Object { $_.Name -ne 'Kino.exe' }
    if ($unexpected) { throw "The publish folder holds more than Kino.exe: $($unexpected.Name -join ', ')" }

    if (-not $SkipSmokeTest) {
        $evidence = Join-Path $repoRoot 'artifacts/test-results'
        New-Item -ItemType Directory -Force $evidence | Out-Null
        $capture = Start-Process $exe -ArgumentList @('--selftest-capture', (Join-Path $evidence 'selftest-capture.txt')) -Wait -PassThru
        Get-Content (Join-Path $evidence 'selftest-capture.txt') -Encoding UTF8
        if ($capture.ExitCode -ne 0) { throw 'The published screenshot self-test failed.' }

        if (Get-Command ffmpeg -ErrorAction SilentlyContinue) {
            $record = Start-Process $exe -ArgumentList @('--selftest-record', (Join-Path $evidence 'selftest-record.txt'), '--seconds', '4') -Wait -PassThru
            Get-Content (Join-Path $evidence 'selftest-record.txt') -Encoding UTF8
            if ($record.ExitCode -ne 0) { throw 'The published recording self-test failed.' }
        }
        else {
            Write-Warning 'ffmpeg is not on PATH; the recording self-test was skipped.'
        }
    }

    New-Item -ItemType Directory -Force $release | Out-Null
    $binary = Join-Path $release "kino-v$version-win-x64.exe"
    Copy-Item $exe $binary
    $archive = Join-Path $release "kino-v$version-win-x64.zip"
    Compress-Archive -Path $exe, 'LICENSE', 'THIRD-PARTY-NOTICES.md' -DestinationPath $archive
    foreach ($file in @($binary, $archive)) {
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -Path "$file.sha256" -Value "$hash  $([IO.Path]::GetFileName($file))" -Encoding ascii
    }

    Get-ChildItem $release | ForEach-Object { '{0,12:N0}  {1}' -f $_.Length, $_.Name }
}
finally {
    Pop-Location
}
