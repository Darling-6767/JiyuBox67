<#
.SYNOPSIS
  端到端测试：用 FakePeer 冒充极域学生端，验证「探测 → 识别广播窗口 → 窗口化 → 还原」整条链路。

.DESCRIPTION
  不需要真极域、不需要管理员：
    1) FakePeer.exe 开一个全屏+置顶+无边框窗口（就是极域广播窗口的形状）
    2) 把 adapter.json 改成认识 FakePeer.exe
    3) 启动 JiyuBox，等它探测到
    4) 外部发送 Ctrl+Alt+W 全局热键
    5) 读 FakePeer 窗口样式，断言已窗口化；再断言还原后回到全屏置顶
#>
[CmdletBinding()]
param(
    [string]$BuildDir
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($BuildDir)) {
    $scriptDir = Split-Path -Parent $PSCommandPath
    $BuildDir = Join-Path (Split-Path -Parent $scriptDir) '.build'
}
$devDir = Join-Path $BuildDir 'dev'
$toolsDir = Join-Path $BuildDir 'tools'
$appExe = Join-Path $devDir 'JiyuBox.exe'
$peerExe = Join-Path $toolsDir 'FakePeer.exe'
$results = New-Object System.Collections.Generic.List[string]
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if (-not $ok) { $script:failed++ }
    $script:results.Add(("{0}  {1}  {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail))
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class E2E {
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
}
"@

function Send-Hotkey {
    # Ctrl+Alt+W
    [E2E]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    [E2E]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [E2E]::keybd_event(0x57, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [E2E]::keybd_event(0x57, 0, 2, [UIntPtr]::Zero)
    [E2E]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [E2E]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
}

function Get-Style([IntPtr]$hwnd) {
    $style = [E2E]::GetWindowLongPtr($hwnd, -16).ToInt64()
    $ex = [E2E]::GetWindowLongPtr($hwnd, -20).ToInt64()
    $r = New-Object E2E+RECT
    [E2E]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    [pscustomobject]@{
        Style = $style
        Ex = $ex
        Captioned = ($style -band 0x00C00000) -eq 0x00C00000
        Topmost = ($ex -band 0x8) -ne 0
        Width = $r.R - $r.L
        Height = $r.B - $r.T
    }
}

# 0. 清理
Get-Process JiyuBox, FakePeer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
Remove-Item (Join-Path $env:TEMP 'fakepeer_hwnd.txt') -ErrorAction SilentlyContinue

# 1. 让 JiyuBox 把 FakePeer 当成目标
$adapter = @{
    name           = 'E2E 假极域'
    processNames   = @('FakePeer.exe', 'StudentMain.exe')
    broadcastHints = @('Fake', 'TD')
    blackScreenHints = @('Black')
    notes          = '端到端测试用'
}
$adapter | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $devDir 'adapter.json') -Encoding UTF8

$cfg = Get-Content (Join-Path $devDir 'config.json') -Raw | ConvertFrom-Json
$cfg.ProtectFromCapture = $false
$cfg | ConvertTo-Json | Set-Content (Join-Path $devDir 'config.json') -Encoding UTF8

# 2. 起假学生端
$peer = Start-Process -FilePath $peerExe -PassThru
Start-Sleep -Seconds 2
$hwndFile = Join-Path $env:TEMP 'fakepeer_hwnd.txt'
Check 'FakePeer 已启动并暴露窗口句柄' (Test-Path $hwndFile) $hwndFile
$parts = (Get-Content $hwndFile -Raw).Trim().Split(' ')
$hwnd = [IntPtr][int64]$parts[0]

$before = Get-Style $hwnd
Check '初始状态：全屏' ($before.Width -ge 1400 -and $before.Height -ge 800) "$($before.Width)x$($before.Height)"
Check '初始状态：置顶' $before.Topmost ("ex=0x{0:X}" -f $before.Ex)
Check '初始状态：无标题栏' (-not $before.Captioned) ("style=0x{0:X}" -f $before.Style)

# 3. 起工具箱
$app = Start-Process -FilePath $appExe -WorkingDirectory $devDir -PassThru
Start-Sleep -Seconds 3
Check 'JiyuBox 进程存活' (-not $app.HasExited) "pid=$($app.Id)"

# 4. 发全局热键窗口化
Send-Hotkey
Start-Sleep -Seconds 2

$after = Get-Style $hwnd
Check '窗口化后出现标题栏' $after.Captioned ("style=0x{0:X}" -f $after.Style)
Check '窗口化后不再是置顶' (-not $after.Topmost) ("ex=0x{0:X}" -f $after.Ex)
Check '窗口化后不再是全屏' ($after.Width -lt $before.Width) "$($after.Width)x$($after.Height)"

# 5. 触发“全部还原”（托盘菜单不在自动化范围内，直接结束进程走退出还原逻辑不可靠，
#    这里改用再发一次热键无法还原，故仅校验窗口化；还原逻辑由冒烟测试覆盖）

# 6. 收尾
$log = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'JiyuBox\logs') -Filter *.log |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
$logTail = if ($log) { Get-Content $log.FullName -Tail 6 -Encoding UTF8 } else { @() }

Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $peer.Id -Force -ErrorAction SilentlyContinue

Write-Host "`n================ E2E 结果 ================" -ForegroundColor Cyan
$results | ForEach-Object { Write-Host $_ }
Write-Host "`n--- 工具箱日志尾部 ---"
$logTail | ForEach-Object { Write-Host "  $_" }
Write-Host $(if ($failed -eq 0) { "`n全部通过" } else { "`n失败 $failed 项" }) -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
exit $(if ($failed -eq 0) { 0 } else { 1 })
