<#
.SYNOPSIS
  给 JiyuBox.exe 做代码签名（自签证书）。UIAccess 版本必须签名，否则 Windows 拒绝启动。

.DESCRIPTION
  首次运行会生成一张自签代码签名证书（CurrentUser\My），并导出 .cer。
  部署脚本会把这张证书装进「受信任的根证书颁发机构」，这是 UIAccess 能生效的前提。
  签名使用 Set-AuthenticodeSignature（不依赖 Windows SDK 的 signtool）。

.EXAMPLE
  .\sign.ps1 -Exe .\JiyuBox.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [string]$Subject = 'CN=JiyuBox Self-Signed Code Signing',
    [string]$CerOut
)

$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
if (-not $CerOut) { $CerOut = Join-Path (Split-Path $Exe -Parent) 'JiyuBox-CodeSign.cer' }

Write-Host "==> 查找/创建签名证书：$Subject" -ForegroundColor Cyan
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    Write-Host "    未找到，正在生成自签证书（有效期 5 年）" -ForegroundColor Yellow
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Subject `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @(
            '2.5.29.37={text}1.3.6.1.5.5.7.3.3',   # EKU: Code Signing
            '2.5.29.19={text}'                      # BasicConstraints: 允许作 CA 根（自签）
        )
}

Export-Certificate -Cert $cert -FilePath $CerOut -Force | Out-Null
Write-Host "    证书已导出：$CerOut" -ForegroundColor Green

Write-Host "==> 签名 $Exe" -ForegroundColor Cyan
$sig = Set-AuthenticodeSignature -FilePath $Exe -Certificate $cert -HashAlgorithm SHA256
if ($sig.Status -ne 'Valid' -and $sig.Status -ne 'UnknownError') {
    Write-Warning "签名状态：$($sig.Status) - $($sig.StatusMessage)"
}

$check = Get-AuthenticodeSignature -FilePath $Exe
Write-Host "    结果：$($check.Status)  ($($check.SignerCertificate.Subject))" -ForegroundColor Green
Write-Host "`n下一步：UIAccess 版请运行 deploy.ps1（需要管理员）把程序和证书装到位。" -ForegroundColor Cyan
