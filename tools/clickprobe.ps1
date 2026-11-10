# Shows a banner in an isolated test instance and reports which window the system hit-tests at points around it.
# A point that answers "GlassBell" outside the card means the shadow / transparent area swallows clicks.
# (ASCII only: Windows PowerShell 5.1 reads a BOM-less script as ANSI.)
param([string]$Exe)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Hit {
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PT p);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
  public static string At(int x, int y, out uint pid, out int ex) {
    IntPtr h = WindowFromPoint(new PT { X = x, Y = y });
    pid = 0; ex = 0;
    if (h == IntPtr.Zero) return "none";
    GetWindowThreadProcessId(h, out pid);
    ex = GetWindowLong(h, -20);
    return h.ToString();
  }
}
'@
$ti = Join-Path $env:LOCALAPPDATA 'GlassBell\test\t.ini'
New-Item -ItemType Directory -Force (Split-Path $ti) | Out-Null
Set-Content $ti "glass=1`r`ncolor=auto`r`ntint=30`r`nshadow=60`r`nshadowsoft=60`r`nshadowoffset=40`r`nsound=0" -Encoding ASCII
$env:GLASSBELL_INSTANCE = 'probe'; $env:GLASSBELL_SETTINGS = $ti
Start-Process $Exe -ArgumentList '--daemon' -WindowStyle Hidden; Start-Sleep -Seconds 3
& $Exe --title "Clicks" --body "Probe of the area around the card." --app "Telegram" --seconds 12
Start-Sleep -Milliseconds 1600

$W = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
$me = @(Get-Process GlassBell | ForEach-Object { $_.Id })
function Probe($label, $x, $y) {
  $pid2 = [uint32]0; $ex = 0
  $null = [Hit]::At($x, $y, [ref]$pid2, [ref]$ex)
  $name = (Get-Process -Id $pid2 -ErrorAction SilentlyContinue).ProcessName
  $flag = ''
  if ($me -contains [int]$pid2) { $flag = '   <== GlassBell takes the click' }
  "{0,-34} -> {1} (ex-style 0x{2:X}){3}" -f $label, $name, $ex, $flag
}
$cx = $W - 14 - 180
Probe 'inside the card (expect GlassBell)' $cx 60
Probe '20 px below the card'               $cx 135
Probe '60 px below the card'               $cx 175
Probe '40 px left of the card'             ($W - 14 - 360 - 40) 60
Probe 'top edge of the screen'             $cx 2
& $Exe --quit; Start-Sleep -Milliseconds 800
