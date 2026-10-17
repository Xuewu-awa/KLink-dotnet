[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $BotSourceRoot,
    [string] $BotDataRoot
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$sourceRoot = (Resolve-Path -LiteralPath $BotSourceRoot).Path
if (-not $BotDataRoot) { $BotDataRoot = Join-Path $sourceRoot 'klink bot/docs' }
$dataRoot = (Resolve-Path -LiteralPath $BotDataRoot).Path
$testRoot = Join-Path $PSScriptRoot 'KLink.Bot.UpstreamTests'
$pin = Get-Content -LiteralPath (Join-Path $testRoot 'upstream.lock.json') -Raw | ConvertFrom-Json
$fixtures = Get-Content -LiteralPath (Join-Path $testRoot 'veteran-fixtures.json') -Raw | ConvertFrom-Json
foreach ($entry in $pin.data.PSObject.Properties) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $dataRoot $entry.Name) -Algorithm SHA256).Hash
    if ($actual -ne $entry.Value) { throw "Pinned data mismatch: $($entry.Name); expected $($entry.Value), actual $actual" }
}
& git -C $sourceRoot cat-file -e "$($pin.commit)^{commit}"
if ($LASTEXITCODE -ne 0) { throw "The Bot checkout must contain commit $($pin.commit)." }
$stage = Join-Path $testRoot "obj/pinned/$($pin.commit)"
[void][System.IO.Directory]::CreateDirectory($stage)
$files = @($pin.sources | ForEach-Object { [pscustomobject]@{ Source = "tools/BotSim/$_"; Target = $_ } })
$files += @($pin.replayFixtures | ForEach-Object { [pscustomobject]@{ Source = $_; Target = $_ } })
foreach ($file in $files) {
    $name = $file.Target
    $info = [System.Diagnostics.ProcessStartInfo]::new('git')
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false, $true)
    $info.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false, $true)
    foreach ($arg in @('-C', $sourceRoot, 'show', "$($pin.commit):$($file.Source)")) {
        [void]$info.ArgumentList.Add($arg)
    }
    $process = [System.Diagnostics.Process]::Start($info)
    $text = $process.StandardOutput.ReadToEnd().Replace("`r`n", "`n")
    $failure = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Cannot stage ${name}: $failure" }
    $process.Dispose()
    if ($name -eq 'SelfTest.cs') {
        foreach ($fixture in $fixtures) {
            $matches = [regex]::Matches($text, [regex]::Escape($fixture.old)).Count
            if ($matches -ne 1) { throw "Expected one fixture anchor, found ${matches}: $($fixture.old)" }
            $text = $text.Replace($fixture.old, $fixture.new)
        }
    }
    $destination = Join-Path $stage $name
    [void][System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination))
    [System.IO.File]::WriteAllText($destination, $text, [System.Text.UTF8Encoding]::new($false))
}
"Pinned upstream: $($pin.commit); six data SHA-256 checks passed; five veteran fixtures applied."
$regression = Join-Path $PSScriptRoot 'KLink.Bot.RegressionTests/KLink.Bot.RegressionTests.csproj'
& dotnet run --project $regression -c Release -p:NuGetAudit=false "-p:BotDataRoot=$dataRoot" -- $dataRoot (Join-Path $stage 'klink bot/docs/live-replays')
if ($LASTEXITCODE -ne 0) { throw 'Local regression suite failed.' }
$upstream = Join-Path $testRoot 'KLink.Bot.UpstreamTests.csproj'
Push-Location -LiteralPath $stage
try {
    & dotnet run --project $upstream -c Release -p:NuGetAudit=false "-p:BotDataRoot=$dataRoot" "-p:UpstreamSourceRoot=$stage" -- selftest
    if ($LASTEXITCODE -ne 0) { throw 'Pinned upstream selftest failed.' }
}
finally { Pop-Location }
