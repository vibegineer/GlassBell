# Real-click test: an observer window sits under the banner and logs the clicks it receives.
# Clicks at points around the card must reach the observer; a click on the card must not (the card takes it).
# The mouse pointer jumps for about two seconds and is put back afterwards.
# (ASCII only: Windows PowerShell 5.1 reads a BOM-less script as ANSI.)
param([string]$Exe)

$tmp = Join-Path $env:LOCALAPPDATA 'GlassBell\test'
New-Item -ItemType Directory -Force $tmp | Out-Null
$log = Join-Path $tmp 'clicks.log'
Remove-Item $log -ErrorAction SilentlyContinue

$observer = @"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
# never activates, so clicking it cannot raise it above the banner and spoil the z-order of the test
Add-Type -ReferencedAssemblies System.Windows.Forms -TypeDefinition @'
public class Obs : System.Windows.Forms.Form {
  protected override bool ShowWithoutActivation { get { return true; } }
  protected override System.Windows.Forms.CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; return cp; } }
}
'@
`$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
`$f = New-Object Obs
`$f.FormBorderStyle = 'None'; `$f.StartPosition = 'Manual'; `$f.TopMost = `$true; `$f.ShowInTaskbar = `$false
`$f.BackColor = [System.Drawing.Color]::DarkOrange
`$f.Location = New-Object System.Drawing.Point((`$b.Width - 520), 0); `$f.Size = New-Object System.Drawing.Size(520, 400)
`$f.Add_MouseDown({ param(`$s, `$e) Add-Content -Path '$log' -Value ('observer got click at ' + (`$e.X + `$f.Left) + ',' + (`$e.Y + `$f.Top)) })
`$t = New-Object System.Windows.Forms.Timer; `$t.Interval = 40000; `$t.Add_Tick({ `$f.Close() }); `$t.Start()
[System.Windows.Forms.Application]::Run(`$f)
"@
$obsFile = Join-Path $tmp 'observer.ps1'
Set-Content $obsFile $observer -Encoding ASCII

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Mouse {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(80); mouse_event(2, 0, 0, 0, UIntPtr.Zero); mouse_event(4, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(120); }
}
'@

Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $obsFile -WindowStyle Hidden
Start-Sleep -Seconds 3
$ti = Join-Path $tmp 't.ini'
Set-Content $ti "glass=1`r`ncolor=auto`r`ntint=30`r`nshadow=60`r`nshadowsoft=60`r`nshadowoffset=40`r`nsound=0" -Encoding ASCII
$env:GLASSBELL_INSTANCE = 'probe'; $env:GLASSBELL_SETTINGS = $ti
Start-Process $Exe -ArgumentList '--daemon' -WindowStyle Hidden; Start-Sleep -Seconds 3
& $Exe --title "Clicks" --body "Click test around the card." --app "Telegram" --seconds 30
Start-Sleep -Milliseconds 1800

$W = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds.Width
$cx = $W - 14 - 180
$old = New-Object Mouse+POINT; [void][Mouse]::GetCursorPos([ref]$old)
$points = @(
  @('20 px below the card', $cx, 135),
  @('60 px below the card', $cx, 175),
  @('40 px left of the card', ($W - 14 - 360 - 40), 60),
  @('above the card', $cx, 3),
  @('inside the card', $cx, 60)
)
foreach ($p in $points) {
  Add-Content -Path $log -Value ("--- click: " + $p[0] + " at " + $p[1] + "," + $p[2])
  [Mouse]::Click([int]$p[1], [int]$p[2])
}
[void][Mouse]::SetCursorPos($old.X, $old.Y)
& $Exe --quit; Start-Sleep -Milliseconds 800
Get-Content $log
