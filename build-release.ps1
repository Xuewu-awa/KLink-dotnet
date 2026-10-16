#requires -Version 7
[CmdletBinding()]
param(
    [string]$OutDir = '',
    [string]$BotDataRoot = '',
    [switch]$SkipAdminUi
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$fyRoot = Join-Path $repoRoot 'vendor\fyserver'
$fyProject = Join-Path $fyRoot 'fyserver.csproj'
$appProject = Join-Path $repoRoot 'KLink.App\KLink.App.csproj'
$fyPublish = Join-Path $fyRoot 'publish\win-x64'

if (-not (Test-Path -LiteralPath $fyProject)) {
    throw "找不到 vendored fyserver 项目：$fyProject"
}

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path (Split-Path -Parent $repoRoot) 'rel'
}

if ([string]::IsNullOrWhiteSpace($BotDataRoot)) {
    $BotDataRoot = Join-Path (Split-Path -Parent $repoRoot) 'klink bot\klink bot\docs'
}

$requiredBotFiles = @(
    'cards.live.json',
    'card-effects.json',
    'card-ir.json',
    'card-vectors.json',
    'deck_code_ids.live.json'
)
foreach ($name in $requiredBotFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $BotDataRoot $name))) {
        throw "BotData 缺少 $name：$BotDataRoot。请用 -BotDataRoot 指定完整数据目录。"
    }
}

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet 失败（exit=$LASTEXITCODE）：$($Arguments -join ' ')"
    }
}

Write-Host "BotData: $BotDataRoot" -ForegroundColor DarkGray

if (-not $SkipAdminUi) {
    Write-Host '[1/4] 构建 fyserver AdminUi' -ForegroundColor Cyan
    Push-Location (Join-Path $fyRoot 'AdminUi')
    try {
        if (-not (Test-Path -LiteralPath 'node_modules\vite\package.json')) {
            & npm ci --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) { throw "npm ci 失败（exit=$LASTEXITCODE）" }
        }
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw "AdminUi 构建失败（exit=$LASTEXITCODE）" }
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Host '[1/4] 跳过 AdminUi 构建' -ForegroundColor Yellow
}

Write-Host '[2/4] 构建 fyserver' -ForegroundColor Cyan
Invoke-Dotnet @(
    'build', $fyProject, '-c', 'Release', '--nologo',
    ('-p:BotDataRoot=' + $BotDataRoot),
    '-p:EnableAot=false',
    '-p:SkipAdminUiBuild=true',
    '-p:NuGetAudit=false'
)

Write-Host '[3/4] 发布自包含 fyserver' -ForegroundColor Cyan
if (Test-Path -LiteralPath $fyPublish) {
    Remove-Item -LiteralPath $fyPublish -Recurse -Force
}
Invoke-Dotnet @(
    'publish', $fyProject, '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', '--nologo',
    ('-p:BotDataRoot=' + $BotDataRoot),
    '-p:EnableAot=false',
    '-p:SkipAdminUiBuild=true',
    '-p:DebugType=none',
    '-p:NuGetAudit=false',
    '-o', $fyPublish
)

Write-Host '[4/4] 发布 KLink.App 并装配 fyserver' -ForegroundColor Cyan
if (Test-Path -LiteralPath $OutDir) {
    Remove-Item -LiteralPath $OutDir -Recurse -Force
}
Invoke-Dotnet @(
    'publish', $appProject, '-c', 'Release', '-r', 'win-x64',
    '--self-contained', 'true', '--nologo',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none',
    '-p:NuGetAudit=false',
    ('-p:FyServerSource=' + $fyPublish),
    '-o', $OutDir
)

$runtimeJunk = @(
    (Join-Path $OutDir 'config.json'),
    (Join-Path $OutDir 'data\fyserver\setting.json'),
    (Join-Path $OutDir 'data\fyserver\data'),
    (Join-Path $OutDir 'data\fyserver\faster-log'),
    (Join-Path $OutDir 'data\fyserver\bot-log')
)
foreach ($path in $runtimeJunk) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

$checks = @(
    (Join-Path $OutDir 'KLink.App.exe'),
    (Join-Path $OutDir 'data\fyserver\fyserver.exe'),
    (Join-Path $OutDir 'data\fyserver\BotData\card-vectors.json'),
    (Join-Path $OutDir 'data\fyserver\library\deckCodeIDsTable2.json'),
    (Join-Path $OutDir 'data\fyserver\config\serverOptions.schema.json'),
    (Join-Path $OutDir 'data\fyserver\wwwroot\admin-ui\index.html')
)
foreach ($path in $checks) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "发布包缺少必需文件：$path"
    }
}

$files = @(Get-ChildItem -LiteralPath $OutDir -Recurse -File)
$bytes = ($files | Measure-Object -Property Length -Sum).Sum
Write-Host "发布完成：$OutDir" -ForegroundColor Green
Write-Host ("{0} 个文件，{1:N1} MB" -f $files.Count, ($bytes / 1MB))
