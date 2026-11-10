# Measures what banners cost, in an isolated test instance (own pipe, mutex and settings; the real daemon is not touched).
#   single: one banner at a time (show, slide in, 1 s on screen, slide out)
#   stack:  three banners in quick succession, so the older ones slide down and get their glass re-baked
#   idle:   the daemon with nothing on screen
# CPU is the exact cycle count of the process (UI + render threads), GPU is the 3D engine busy time of the process.
# The in-app timings come from the trace line Manager.Show writes while trace.flag exists.
# (ASCII only: Windows PowerShell 5.1 reads a BOM-less script as ANSI.)
param([string]$Exe, [string]$Quality = '', [switch]$Cpu, [switch]$Flat, [switch]$LiveBg, [int]$Runs = 6, [int]$Stacks = 3, [switch]$NoGpu)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System; using System.Diagnostics; using System.Runtime.InteropServices;
public static class Cyc {
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int id);
  [DllImport("kernel32.dll")] static extern bool QueryProcessCycleTime(IntPtr h, out ulong c);
  [DllImport("kernel32.dll")] static extern bool QueryThreadCycleTime(IntPtr h, out ulong c);
  [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  public static ulong Proc(int id) { IntPtr h = OpenProcess(0x1000, false, id); ulong c = 0; if (h != IntPtr.Zero) { QueryProcessCycleTime(h, out c); CloseHandle(h); } return c; }
  // cycles per millisecond of this CPU (the cycle counter runs at a fixed rate)
  public static double PerMs() {
    ulong a, b; var sw = Stopwatch.StartNew(); QueryThreadCycleTime(GetCurrentThread(), out a);
    while (sw.ElapsedMilliseconds < 300) { }
    QueryThreadCycleTime(GetCurrentThread(), out b); return (b - a) / sw.Elapsed.TotalMilliseconds;
  }
}
'@

$dir = Join-Path $env:LOCALAPPDATA 'GlassBell'
$tmp = Join-Path $dir 'test'
New-Item -ItemType Directory -Force $tmp | Out-Null
$ini = Join-Path $tmp 'bench.ini'
$g = if ($Flat) { '0' } else { '1' }
$q = if ($Quality.Length -gt 0) { "`r`nquality=$Quality" } else { '' }
if ($LiveBg) { $q += "`r`nlivebg=1" }
Set-Content $ini ("glass=$g`r`ncolor=auto`r`nintensity=60`r`ntint=60`r`nblur=35`r`nshadow=40`r`nshadowsoft=60`r`nshadowoffset=40`r`nsound=0" + $q) -Encoding ASCII
$env:GLASSBELL_INSTANCE = 'bench'; $env:GLASSBELL_SETTINGS = $ini
if ($Cpu) { $env:GLASSBELL_CPU = '1' } else { Remove-Item Env:GLASSBELL_CPU -ErrorAction SilentlyContinue }
$flag = Join-Path $dir 'trace.flag'
$log = Join-Path $dir 'log.txt'

$perMs = [Cyc]::PerMs()
function Ms($c) { [math]::Round($c / $perMs, 1) }
function Gpu3D($id) {
  if ($NoGpu) { return 0 }
  $t = 0
  Get-CimInstance Win32_PerfRawData_GPUPerformanceCounters_GPUEngine | Where-Object { $_.Name -like "pid_$($id)_*engtype_3D" } | ForEach-Object { $t += [double]$_.RunningTime }
  return $t / 10000.0   # 100 ns units -> ms
}

$d = Start-Process $Exe -ArgumentList '--daemon' -WindowStyle Hidden -PassThru
Start-Sleep -Seconds 2
$id = $d.Id
New-Item -ItemType File -Force $flag | Out-Null
$logStart = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }

# cold first banner
$c0 = [Cyc]::Proc($id)
& $Exe --title "Bench" --body "First banner after start, measured cold." --app "Telegram" --seconds 1
Start-Sleep -Milliseconds 2800
$cold = Ms ([Cyc]::Proc($id) - $c0)

# single banners
$g0 = Gpu3D $id
$c0 = [Cyc]::Proc($id)
for ($i = 1; $i -le $Runs; $i++) {
  & $Exe --title "Bench $i" --body "One banner at a time: show, slide in, stay a second, slide out." --app "Telegram" --seconds 1
  Start-Sleep -Milliseconds 2600
}
$single = Ms (([Cyc]::Proc($id) - $c0) / $Runs)
$g1 = Gpu3D $id
$gSingle = [math]::Round(($g1 - $g0) / $Runs, 1)

# stacks of three
$c0 = [Cyc]::Proc($id)
for ($s = 1; $s -le $Stacks; $s++) {
  foreach ($k in 1..3) {
    & $Exe --title "Stack $s.$k" --body "Banners pile up and the older ones slide down." --app "Telegram" --seconds 2.5
    Start-Sleep -Milliseconds 450
  }
  Start-Sleep -Milliseconds 4200
}
$stack = Ms (([Cyc]::Proc($id) - $c0) / $Stacks)
$g2 = Gpu3D $id
$gStack = [math]::Round(($g2 - $g1) / $Stacks, 1)

# idle
$c0 = [Cyc]::Proc($id)
Start-Sleep -Seconds 5
$idle = Ms (([Cyc]::Proc($id) - $c0) / 5)
$p = Get-Process -Id $id
$mem = "{0:N0} MB private, {1:N0} MB working set" -f ($p.PrivateMemorySize64 / 1MB), ($p.WorkingSet64 / 1MB)

& $Exe --quit
Start-Sleep -Milliseconds 600
Remove-Item $flag -ErrorAction SilentlyContinue
$lines = @()
if (Test-Path $log) {
  $fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
  $fs.Position = [math]::Min($logStart, $fs.Length)
  $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
  $lines = $sr.ReadToEnd() -split "`r`n" | Where-Object { $_ -match 'show:' -and ($_ -match 'inst=bench' -or $_ -notmatch 'inst=') }
  $sr.Dispose()
}
$label = "quality=" + $(if ($Quality) { $Quality } else { '(none)' }) + $(if ($Cpu) { ' CPU' } else { ' GPU' }) + $(if ($Flat) { ' flat' } else { '' }) + $(if ($LiveBg) { ' live' } else { '' })
"== $label   (cycles/ms = $([int]$perMs))"
"cold first banner: $cold ms CPU"
"single banner:     $single ms CPU, $gSingle ms GPU 3D   (per banner, average of $Runs)"
"stack of three:    $stack ms CPU, $gStack ms GPU 3D   (per stack, average of $Stacks)"
"idle:              $idle ms CPU per second"
"memory at the end: $mem"
"trace (first = cold):"
$lines | ForEach-Object { '  ' + ($_ -replace '^\S+ ', '') }
