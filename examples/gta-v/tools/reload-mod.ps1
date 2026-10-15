# Reload ScriptHookVDotNet scripts in the running game without touching the keyboard: posts the
# reload key (ScriptHookVDotNet.ini ReloadKeyBinding, Insert by default here) to the GTA window.
# GTA pauses while it's minimized or in the background, so by default this waits until the game
# window is in front, sends the key, and checks ScriptHookVDotNet.log for the reload.
#   powershell -ExecutionPolicy Bypass -File tools\reload-mod.ps1 [-NoWait] [-TimeoutMinutes 60]
param([int]$VirtualKey = 0x2D, [switch]$NoWait, [int]$TimeoutMinutes = 60) # VK_INSERT

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ReloadKeys {
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
"@

$gta = Get-Process GTA5 -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $gta -or $gta.MainWindowHandle -eq [IntPtr]::Zero) { Write-Error "GTA V isn't running"; exit 1 }
$log = Join-Path (Split-Path $gta.Path) "ScriptHookVDotNet.log"

if (-not $NoWait) {
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    while ([ReloadKeys]::GetForegroundWindow() -ne $gta.MainWindowHandle) {
        if ((Get-Date) -gt $deadline -or $gta.HasExited) { Write-Error "GTA V never came to the front"; exit 1 }
        Start-Sleep -Seconds 1
    }
    Start-Sleep -Seconds 2 # let the game resume
}

$before = if (Test-Path $log) { (Get-Item $log).LastWriteTime } else { Get-Date 0 }
$scan = [ReloadKeys]::MapVirtualKey($VirtualKey, 0)
$down = [IntPtr](1 -bor ($scan -shl 16) -bor (1 -shl 24))
$up = [IntPtr]([int64](1 -bor ($scan -shl 16) -bor (1 -shl 24) -bor (1 -shl 30)) -bor [int64]0x80000000)
[ReloadKeys]::PostMessage($gta.MainWindowHandle, 0x100, [IntPtr]$VirtualKey, $down) | Out-Null # WM_KEYDOWN
Start-Sleep -Milliseconds 80
[ReloadKeys]::PostMessage($gta.MainWindowHandle, 0x101, [IntPtr]$VirtualKey, $up) | Out-Null   # WM_KEYUP

for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    if ((Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $before -and (Select-String -Path $log -Pattern "Started script" -Quiet)) {
        Write-Output "Scripts reloaded."
        exit 0
    }
}
Write-Output "Sent the reload key, but ScriptHookVDotNet.log didn't change; press Insert in game."
exit 2
