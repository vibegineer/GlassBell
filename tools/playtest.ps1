# Simulates someone playing with the workshop sliders and measures what the program costs while doing it.
# Runs an isolated test instance (own pipe, mutex and settings file), so the real daemon is never touched.
# -Cpu measures the CPU renderer instead of the GPU one; -Quality sets the quality level of the test settings.
# Each slider is moved in turn at about 30 changes per second, like a mouse drag.
# (ASCII only: Windows PowerShell 5.1 reads a BOM-less script as ANSI.)
param([string]$Exe, [int]$Steps = 30, [string]$Quality = '', [switch]$Cpu, [int]$Scene = 0)

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System; using System.Diagnostics; using System.Runtime.InteropServices;
public static class Cyc2 {
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int id);
  [DllImport("kernel32.dll")] static extern bool QueryProcessCycleTime(IntPtr h, out ulong c);
  [DllImport("kernel32.dll")] static extern bool QueryThreadCycleTime(IntPtr h, out ulong c);
  [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  public static ulong Proc(int id) { IntPtr h = OpenProcess(0x1000, false, id); ulong c = 0; if (h != IntPtr.Zero) { QueryProcessCycleTime(h, out c); CloseHandle(h); } return c; }
  public static double PerMs() {
    ulong a, b; var sw = Stopwatch.StartNew(); QueryThreadCycleTime(GetCurrentThread(), out a);
    while (sw.ElapsedMilliseconds < 300) { }
    QueryThreadCycleTime(GetCurrentThread(), out b); return (b - a) / sw.Elapsed.TotalMilliseconds;
  }
}
'@
$perMs = [Cyc2]::PerMs()

$tmp = Join-Path $env:LOCALAPPDATA 'GlassBell\test'
New-Item -ItemType Directory -Force $tmp | Out-Null
$ini = Join-Path $tmp 'play.ini'
$q = if ($Quality.Length -gt 0) { "`r`nquality=$Quality" } else { '' }
Set-Content $ini ("glass=1`r`ncolor=auto`r`nintensity=60`r`ntint=60`r`nblur=35`r`nshadow=40`r`nshadowsoft=60`r`nshadowoffset=40`r`nsound=0`r`nscene=$Scene" + $q) -Encoding ASCII
$env:GLASSBELL_INSTANCE = 'play'; $env:GLASSBELL_SETTINGS = $ini
if ($Cpu) { $env:GLASSBELL_CPU = '1' } else { Remove-Item Env:GLASSBELL_CPU -ErrorAction SilentlyContinue }

$d = Start-Process $Exe -ArgumentList '--daemon' -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 2
& $Exe --settings
Start-Sleep -Seconds 3

$win = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst('Children',
  (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $d.Id)))
$sliders = $win.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Slider)))
$rv = @(); foreach ($s in $sliders) { $rv += $s.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern) }
$names = @('tint', 'glass', 'blur', 'shadow', 'softness', 'offset')
$label = "quality=" + $(if ($Quality) { $Quality } else { '(none)' }) + $(if ($Cpu) { ' CPU' } else { ' GPU' })
"== $label, sliders found: $($rv.Count)"

# floor: the same value again changes nothing, so this is only the cost of the automation calls themselves
$c0 = [Cyc2]::Proc($d.Id)
for ($i = 0; $i -lt $Steps; $i++) { $rv[0].SetValue(60); Start-Sleep -Milliseconds 30 }
Start-Sleep -Milliseconds 300
"{0,-9} {1,6:N1} ms CPU per call (automation overhead, nothing changes)" -f 'noop', ((([Cyc2]::Proc($d.Id) - $c0) / $perMs) / $Steps)

function Gpu3D($id) {
  $t = 0
  Get-CimInstance Win32_PerfRawData_GPUPerformanceCounters_GPUEngine | Where-Object { $_.Name -like "pid_$($id)_*engtype_3D" } | ForEach-Object { $t += [double]$_.RunningTime }
  return $t / 10000.0   # 100 ns units -> ms
}
$g0 = Gpu3D $d.Id
$total = 0
for ($k = 0; $k -lt [math]::Min(6, $rv.Count); $k++) {
  $c0 = [Cyc2]::Proc($d.Id)
  $sw = [Diagnostics.Stopwatch]::StartNew()
  for ($i = 0; $i -lt $Steps; $i++) {
    $rv[$k].SetValue(20 + ($i * 7) % 60)
    Start-Sleep -Milliseconds 30
  }
  Start-Sleep -Milliseconds 300
  $c = ([Cyc2]::Proc($d.Id) - $c0) / $perMs
  $total += $c
  $rv[$k].SetValue(@(60, 60, 35, 40, 60, 40)[$k])
  "{0,-9} {1,6:N1} ms CPU per change   ({2} changes in {3} ms)" -f $names[$k], ($c / $Steps), $Steps, [int]$sw.ElapsedMilliseconds
}
"all       {0,6:N1} ms CPU per change" -f ($total / ($Steps * [math]::Min(6, $rv.Count)))
"GPU 3D    {0,6:N2} ms per change (the 3D engine time of the process, slider moves back to defaults included)" -f (((Gpu3D $d.Id) - $g0) / ($Steps * [math]::Min(6, $rv.Count)))
$texts = $win.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition(
  [System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
foreach ($t in $texts) { if ($t.Current.AutomationId -eq 'timing') { "readout: " + $t.Current.Name } }
$win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
Start-Sleep -Milliseconds 400
& $Exe --quit
Start-Sleep -Milliseconds 600
