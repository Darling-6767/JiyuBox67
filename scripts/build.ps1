<#
.SYNOPSIS
  一键构建 JiyuBox：按极域 2016 / 2021 两个预设，各出「便携版」和「UIAccess 版」。

.DESCRIPTION
  · 便携版：普通置顶，任意目录可运行，适合先试用。
  · UIAccess 版：uiAccess=true 清单 + 自签证书 + 安装到 Program Files，才能压过极域的全屏窗口。
  · 两种都用 self-contained 单文件，目标机不需要装 .NET。

.EXAMPLE
  .\build.ps1              # 全量四个包
  .\build.ps1 -Preset 2016 -PortableOnly
#>
[CmdletBinding()]
param(
    [ValidateSet('2016', '2021', 'all')]
    [string]$Preset = 'all',
    [switch]$PortableOnly,
    [switch]$UiAccessOnly
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
else { $scriptDir = $PSScriptRoot }

$root = Split-Path -Parent $scriptDir
$repo = Split-Path -Parent $root
$dotnet = Join-Path $repo '.toolchain\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$proj = Join-Path $root 'src\JiyuBox.App\JiyuBox.App.csproj'
$dist = Join-Path $root 'dist'
$staging = Join-Path $root '.build\publish'

function Publish-Flavor {
    param([bool]$UiAccess)

    $flavor = if ($UiAccess) { 'uiaccess' } else { 'portable' }
    $out = Join-Path $staging $flavor
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    Write-Host "==> 发布 $flavor" -ForegroundColor Cyan
    $dotnetArgs = @(
        'publish', $proj,
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-o', $out,
        "-p:UiAccess=$($UiAccess.ToString().ToLower())",
        '-p:PublishSingleFile=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=none',
        '--nologo', '-v', 'q'
    )
    & $dotnet @dotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败：$flavor" }
    return $out
}

function Make-Package {
    param([string]$PresetName, [string]$Flavor, [string]$Src)

    $name = "JiyuBox-$PresetName-$Flavor"
    $dst = Join-Path $dist $name
    if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
    New-Item -ItemType Directory -Path $dst -Force | Out-Null

    Copy-Item (Join-Path $Src '*') $dst -Recurse -Force

    $config = [ordered]@{
        Preset             = $PresetName
        WindowScale        = 0.7
        AutoWatch          = $true
        StartMinimized     = $false
        ProtectFromCapture = $true
        KeepTopmost        = $true
    }
    $config | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $dst 'config.json') -Encoding UTF8

    Copy-Item (Join-Path $scriptDir 'sign.ps1') $dst -Force
    if ($Flavor -eq 'UIAccess') { Copy-Item (Join-Path $scriptDir 'deploy.ps1') $dst -Force }
    Copy-Item (Join-Path $root 'README.md') $dst -Force

    $size = [math]::Round((Get-ChildItem $dst -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    Write-Host "    $name  ($size MB)" -ForegroundColor Green
    return $dst
}

$presets = if ($Preset -eq 'all') { @('2016', '2021') } else { @($Preset) }

if (-not $UiAccessOnly) {
    $src = Publish-Flavor $false
    foreach ($p in $presets) { Make-Package $p 'Portable' $src | Out-Null }
}

if (-not $PortableOnly) {
    $srcUi = Publish-Flavor $true
    $exe = Join-Path $srcUi 'JiyuBox.exe'

    Write-Host "==> 签名（UIAccess 必须）" -ForegroundColor Cyan
    & (Join-Path $scriptDir 'sign.ps1') -Exe $exe -CerOut (Join-Path $srcUi 'JiyuBox-CodeSign.cer')

    foreach ($p in $presets) { Make-Package $p 'UIAccess' $srcUi | Out-Null }
}

Write-Host "`n完成。产物：" -ForegroundColor Cyan
Get-ChildItem $dist -Directory | ForEach-Object {
    $mb = [math]::Round((Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
    "  {0,-28} {1,6} MB" -f $_.Name, $mb
}
