# Press a key in GTA V the way a keyboard does (SendInput with scan codes, which the game's raw input
# sees; posted window messages don't reach its menus). GTA must be the foreground window.
#   powershell -ExecutionPolicy Bypass -File tools\gta-key.ps1 Escape [-HoldMs 80]
param([Parameter(Mandatory)][string]$Key, [int]$HoldMs = 80)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class GtaKey {
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit, Size = 40)] public struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
  [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
  public static uint Send(ushort scan, bool extended, bool up) {
    var i = new INPUT { type = 1 };
    i.ki.wScan = scan;
    i.ki.dwFlags = 0x0008u | (extended ? 0x0001u : 0u) | (up ? 0x0002u : 0u); // SCANCODE | EXTENDEDKEY | KEYUP
    return SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
  }
}
"@

# Set-1 scan codes; extended keys have an E0 prefix.
$codes = @{ Escape = @(0x01, $false); Enter = @(0x1C, $false); Insert = @(0x52, $true); Backspace = @(0x0E, $false); Space = @(0x39, $false) }
if (-not $codes.ContainsKey($Key)) { Write-Error "unknown key $Key (known: $($codes.Keys -join ', '))"; exit 1 }
$scan, $ext = $codes[$Key]
if ([GtaKey]::Send($scan, $ext, $false) -ne 1) { Write-Error "SendInput failed"; exit 1 }
Start-Sleep -Milliseconds $HoldMs
[GtaKey]::Send($scan, $ext, $true) | Out-Null
"pressed $Key"
