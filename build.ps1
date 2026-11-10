# Builds bin\GlassBell.exe and, from it, dist\GlassBell-Setup.exe with the C# compiler that ships with Windows
# (.NET Framework 4.x): no SDK, no NuGet. Run: powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw   = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wpf  = Join-Path $fw 'WPF'
$wm   = 'C:\Windows\System32\WinMetadata'
$bin  = Join-Path $here 'bin'
$dist = Join-Path $here 'dist'
$ico  = Join-Path $here 'assets\GlassBell.ico'
New-Item -ItemType Directory -Force $bin, $dist | Out-Null

$refs = @(
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll",
    "$wpf\UIAutomationClient.dll", "$wpf\UIAutomationTypes.dll",
    "$fw\System.Xaml.dll", "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll",
    "$fw\System.Runtime.WindowsRuntime.dll", "$fw\System.Runtime.InteropServices.WindowsRuntime.dll", "$fw\System.Runtime.dll", "$fw\System.Threading.Tasks.dll",
    "$wm\Windows.Foundation.winmd", "$wm\Windows.UI.winmd",
    "$wm\Windows.ApplicationModel.winmd", "$wm\Windows.Storage.winmd"
) | Where-Object { Test-Path $_ } | ForEach-Object { "/r:$_" }

# every translation is embedded in both exes as lang.<code>.txt
$langs = Get-ChildItem "$here\lang\*.txt" | ForEach-Object { "/resource:$($_.FullName),lang.$($_.BaseName).txt" }

function Compile([string]$out, [string[]]$extra, [string[]]$sources) {
    & "$fw\csc.exe" /nologo /target:winexe /platform:x64 /optimize+ "/out:$out" @refs @extra @sources
    if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE) for $out" }
    "built: $out"
}

# the program: the icon is the exe's own icon and is also embedded, so the tray icon and the settings window can load it
$app = Join-Path $bin 'GlassBell.exe'
Compile $app (@("/win32icon:$ico", "/resource:$ico,GlassBell.ico") + $langs) @(
    "$here\src\GlassBell.cs", "$here\src\Chime.cs", "$here\src\SettingsUi.cs",
    "$here\src\Lang.cs", "$here\src\Sandbox.cs", "$here\src\AssemblyInfo.cs")

# the installer carries the program, the icon, the license and the translations inside it
Compile (Join-Path $dist 'GlassBell-Setup.exe') (@(
    "/win32icon:$ico", "/win32manifest:$here\setup\setup.manifest",
    "/resource:$app,GlassBell.exe", "/resource:$ico,GlassBell.ico", "/resource:$here\LICENSE,LICENSE.txt") + $langs) @(
    "$here\setup\Setup.cs", "$here\src\Lang.cs", "$here\src\Sandbox.cs", "$here\src\AssemblyInfo.cs")
