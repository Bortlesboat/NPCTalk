# Bring the GTA V window to the front (restoring it if it's minimized) so the game runs; GTA pauses in
# the background. Prints whether it worked.
#   powershell -ExecutionPolicy Bypass -File tools\gta-front.ps1
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class GtaFront {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
"@

$gta = Get-Process GTA5 -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $gta -or $gta.MainWindowHandle -eq [IntPtr]::Zero) { Write-Error "GTA V isn't running"; exit 1 }
$h = $gta.MainWindowHandle
for ($i = 0; $i -lt 5 -and [GtaFront]::GetForegroundWindow() -ne $h; $i++) {
    if ([GtaFront]::IsIconic($h)) { [GtaFront]::ShowWindow($h, 9) | Out-Null } # SW_RESTORE
    # Windows only lets the foreground app hand over focus; a key event counts as user input for that.
    [GtaFront]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero) # Alt down
    [GtaFront]::SetForegroundWindow($h) | Out-Null
    [GtaFront]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero) # Alt up
    Start-Sleep -Milliseconds 1500
}
if ([GtaFront]::GetForegroundWindow() -eq $h) { "GTA V is in front" } else { Write-Error "couldn't bring GTA V to the front"; exit 1 }
