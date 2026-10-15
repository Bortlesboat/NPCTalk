# Record a director take (see npc-mod/Director.cs) in one go: bring GTA to the front, record the screen
# (ffmpeg ddagrab) and everything the PC plays (npcvoice-check record), start the scenes, and keep GTA in
# front and unpaused until they're done. If another window takes focus, fullscreen GTA drops out of its
# display mode and pauses: this brings it back, unpauses it, and starts a new video segment with a sync
# flash. Everything lands in -Out: video<N>.mkv, audio.wav (+ .start), timeline.txt, watch.txt.
#   powershell -ExecutionPolicy Bypass -File tools\shoot.ps1 -Out data\director\take1 [-Script script.txt] [-Reload]
param([Parameter(Mandatory)][string]$Out, [string]$Script = "script.txt", [switch]$Reload, [int]$TimeoutMinutes = 30)

$game = "C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V"
$dir = Join-Path $game "scripts\NpcVoice\director"
$alive = Join-Path $dir "alive.txt"
$timeline = Join-Path $dir "timeline.txt"
$Out = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force $Out | Out-Null
$watch = Join-Path $Out "watch.txt"
$recorder = Get-ChildItem (Join-Path $PSScriptRoot "npcvoice-check\bin\Release") -Recurse -Filter npcvoice-check.exe | Select-Object -First 1

Add-Type -Name Win -Namespace Shoot -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
'@
[Shoot.Win]::SetProcessDPIAware() | Out-Null # screen pixels, like the capture
$gta = Get-Process GTA5 -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $gta) { Write-Error "GTA V isn't running"; exit 1 }
$h = $gta.MainWindowHandle

function Now { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
function Note($what) { "$(Now)`t$what" | Add-Content $watch; Write-Host $what }
function InFront { [Shoot.Win]::GetForegroundWindow() -eq $h }
# Where the game's picture is on the screen (windowed GTA sits on the desktop): the edit crops to it.
function Client {
    $r = New-Object Shoot.Win+RECT
    [Shoot.Win]::GetClientRect($h, [ref]$r) | Out-Null
    $pt = New-Object Shoot.Win+POINT
    [Shoot.Win]::ClientToScreen($h, [ref]$pt) | Out-Null
    "$($pt.X)`t$($pt.Y)`t$($r.Right - $r.Left)`t$($r.Bottom - $r.Top)"
}
function Front { & (Join-Path $PSScriptRoot "gta-front.ps1") 2>$null | Out-Null }
function Press($key) { & (Join-Path $PSScriptRoot "gta-key.ps1") $key | Out-Null }
function AliveAge {
    try { (Now) - [int64](Get-Content $alive -Raw -ErrorAction Stop) } catch { 999999 }
}
# In front and running: unpause (Esc) once the mod's heartbeat has been quiet for 4 s (it beats several
# times a second, teleports included, but stops while the pause menu is open).
function Running([int]$waitSeconds) {
    $deadline = (Get-Date).AddSeconds($waitSeconds)
    $lastEsc = Get-Date 0
    while ((Get-Date) -lt $deadline) {
        if (-not (InFront)) { Note "focus_lost"; Front; $lastEsc = Get-Date; Start-Sleep -Milliseconds 1500; continue }
        if ((AliveAge) -lt 1500) { return $true }
        if ((AliveAge) -gt 4000 -and ((Get-Date) - $lastEsc).TotalSeconds -gt 5) { Note "paused: Esc"; Press Escape; $lastEsc = Get-Date }
        Start-Sleep -Milliseconds 500
    }
    return $false
}
function StartVideo([int]$n) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo "ffmpeg"
    $psi.Arguments = "-hide_banner -loglevel error -y -f lavfi -i ddagrab=output_idx=0:framerate=30:draw_mouse=0,hwdownload,format=bgra " +
        "-c:v h264_amf -quality quality -rc cqp -qp_i 18 -qp_p 20 -pix_fmt nv12 `"$Out\video$n.mkv`""
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($psi)
    Note "video_start`t$n`t$(Client)"
    Start-Sleep -Milliseconds 1500
    Set-Content (Join-Path $dir "flash.txt") "1" # the mod flashes white and beeps: syncs this segment
    return $p
}

Note "take start: script $Script"
Front
Start-Sleep 2
if ($Reload) {
    # The reload key only works while the game runs, and an older build has no heartbeat to tell us
    # whether it's paused: press Insert, and if the new build doesn't start beating, Esc (toggles the
    # pause menu) and try again.
    Remove-Item $alive -ErrorAction SilentlyContinue
    for ($try = 1; $try -le 4 -and (AliveAge) -gt 1500; $try++) {
        if (-not (InFront)) { Note "focus_lost"; Front; Start-Sleep 2 }
        Press Insert
        Note "reload (try $try)"
        for ($i = 0; $i -lt 20 -and (AliveAge) -gt 1500; $i++) { Start-Sleep -Milliseconds 500 }
        if ((AliveAge) -gt 1500) { Press Escape; Note "no heartbeat yet: Esc"; Start-Sleep 2 }
    }
    if ((AliveAge) -gt 1500) { Note "the mod didn't come back after the reload"; exit 1 }
    Start-Sleep 15 # voices upload and warm up
}
if (-not (Running 60)) { Note "GTA isn't running (no heartbeat from the mod)"; exit 1 }

$start = Now
$audio = Start-Process -FilePath $recorder.FullName -ArgumentList "record", "`"$Out\audio.wav`"", ($TimeoutMinutes * 60 + 120) -PassThru -WindowStyle Hidden
Start-Sleep 1
$n = 1
$video = StartVideo $n
Start-Sleep 2
Set-Content (Join-Path $dir "go.txt") $Script
Note "go"

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if (-not (InFront) -or (AliveAge) -gt 2500) { Running 60 | Out-Null }
    if ($video.HasExited) { $n++; $video = StartVideo $n }
    $done = Get-Content $timeline -ErrorAction SilentlyContinue | Where-Object { $_ -match "^(\d+)\tshoot_done" -and [int64]$Matches[1] -gt $start }
    if ($done) { Note "shoot done"; break }
}

Start-Sleep 2
try { $video.StandardInput.Write("q"); $video.StandardInput.Flush() } catch { }
if (-not $video.WaitForExit(10000)) { $video.Kill() }
Set-Content "$Out\audio.wav.stop" "1"
$audio.WaitForExit(15000) | Out-Null
Get-Content $timeline | Where-Object { $_ -match "^(\d+)\t" -and [int64]$Matches[1] -ge $start - 60000 } | Set-Content (Join-Path $Out "timeline.txt")
Note "take end: $n video segment(s)"
