<#
.SYNOPSIS
  部署 UIAccess 版到 C:\Program Files\JiyuBox，并安装自签证书（UIAccess 的必要条件）。

.DESCRIPTION
  Windows 对 UIAccess 的硬性要求：
    1) exe 必须有有效签名；
    2) 签名证书必须被本机信任；
    3) exe 必须位于受保护目录（默认 C:\Program Files）。
  少一条，进程会“启动后立刻消失”且没有报错。本脚本一次把三条都办掉。

.EXAMPLE
  # 管理员 PowerShell
  .\deploy.ps1
  .\deploy.ps1 -Source .\  -Target 'C:\Program Files\JiyuBox'
#>
[CmdletBinding()]
param(
    [string]$Source = $PSScriptRoot,
    [string]$Target = 'C:\Program Files\JiyuBox'
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw '请用「以管理员身份运行」启动 PowerShell 后再执行本脚本。'
}

$exe = Join-Path $Source 'JiyuBox.exe'
$cer = Join-Path $Source 'JiyuBox-CodeSign.cer'
if (-not (Test-Path $exe)) { throw "找不到 JiyuBox.exe：$exe" }
if (-not (Test-Path $cer)) { throw "找不到证书 $cer，请先运行 sign.ps1" }

$sig = Get-AuthenticodeSignature -FilePath $exe
Write-Host "签名状态：$($sig.Status)" -ForegroundColor Cyan
if ($sig.Status -ne 'Valid' -and $sig.Status -ne 'UnknownError') {
    throw "exe 未正确签名（$($sig.Status)），UIAccess 不会生效。请先运行 sign.ps1。"
}

Write-Host "==> 安装证书到「受信任的根证书颁发机构」和「受信任的发布者」" -ForegroundColor Cyan
Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPublisher' | Out-Null

Write-Host "==> 复制到 $Target" -ForegroundColor Cyan
if (-not (Test-Path $Target)) { New-Item -ItemType Directory -Path $Target -Force | Out-Null }

# 先结束正在运行的实例，避免文件占用
Get-Process JiyuBox -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

Copy-Item (Join-Path $Source '*') $Target -Recurse -Force

$targetExe = Join-Path $Target 'JiyuBox.exe'
Write-Host "==> 校验 UIAccess 清单" -ForegroundColor Cyan
$manifest = & mt.exe -inputresource:"$targetExe;#1" -out:- 2>$null
if ($LASTEXITCODE -ne 0 -or -not $manifest) {
    Write-Host "    （本机没有 mt.exe，跳过清单回读；以启动结果为准）" -ForegroundColor Yellow
}
elseif ($manifest -match 'uiAccess="true"') {
    Write-Host "    清单确认 uiAccess=true" -ForegroundColor Green
}
else {
    Write-Warning '清单里没有 uiAccess="true"，说明打包时没用 -UiAccess，请重新构建。'
}

Write-Host "==> 启动" -ForegroundColor Cyan
Start-Process -FilePath $targetExe -WorkingDirectory $Target
Write-Host "完成。如果窗口没出现，先看 $Target\logs 或事件查看器里的应用程序错误。" -ForegroundColor Green
