# Compares how fast our banner and the native Windows toast show up after a toast is posted.
# Needs GlassBell.exe running. Native toasts must NOT be suppressed (Do Not Disturb off) for the native column.
param([int]$Runs = 5)

$null = [Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime]
$null = [Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom,ContentType=WindowsRuntime]
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class Cap {
  public static byte[] Grab(int x, int y, int w, int h) {
    using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
    using (var g = Graphics.FromImage(bmp)) {
      g.CopyFromScreen(x, y, 0, 0, new Size(w, h));
      var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
      var buf = new byte[d.Stride * h]; Marshal.Copy(d.Scan0, buf, 0, buf.Length); bmp.UnlockBits(d); return buf;
    }
  }
}
'@ -ReferencedAssemblies System.Drawing

function Changed($a, $b) { -not [System.Linq.Enumerable]::SequenceEqual([byte[]]$a, [byte[]]$b) }

$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$ours   = @(($b.Width - 400), 30, 380, 90)
$native = @(($b.Width - 390), ($b.Height - 190), 380, 150)
$notifier = [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe')

foreach ($i in 1..$Runs) {
    Start-Sleep -Seconds 8
    $bo = [Cap]::Grab($ours[0], $ours[1], $ours[2], $ours[3])
    $bn = [Cap]::Grab($native[0], $native[1], $native[2], $native[3])
    $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
    $xml.LoadXml("<toast><visual><binding template=`"ToastGeneric`"><text>Probe $i</text><text>latency probe</text></binding></visual></toast>")
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $notifier.Show((New-Object Windows.UI.Notifications.ToastNotification $xml))
    $to = -1; $tn = -1
    while ($sw.ElapsedMilliseconds -lt 3000 -and ($to -lt 0 -or $tn -lt 0)) {
        if ($to -lt 0 -and (Changed $bo ([Cap]::Grab($ours[0], $ours[1], $ours[2], $ours[3])))) { $to = $sw.ElapsedMilliseconds }
        if ($tn -lt 0 -and (Changed $bn ([Cap]::Grab($native[0], $native[1], $native[2], $native[3])))) { $tn = $sw.ElapsedMilliseconds }
    }
    "run ${i}: ours=$to ms, native=$tn ms"
}
