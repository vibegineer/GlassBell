// Sandbox: is this process writing into the private copy of a packaged (MSIX) app instead of the real profile?
// Shared by GlassBell.exe and GlassBell-Setup.exe.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace GlassBell
{
    static class Sandbox
    {
        [DllImport("kernel32.dll")]
        static extern int GetCurrentPackageFullName(ref int length, StringBuilder name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

        // Inside a packaged (MSIX) app HKCU and %APPDATA% are private copies: whatever is written there never reaches
        // the real registry, so Windows does not see it at sign-in. A program such an app starts can be redirected the
        // same way without having a package of its own, so the package check (15700 = APPMODEL_ERROR_NO_PACKAGE) is
        // not enough: a new file in %LOCALAPPDATA% shows where writes really land.
        public static readonly bool Active = HasPackage() || Redirected();

        static bool HasPackage() { int n = 0; return GetCurrentPackageFullName(ref n, null) != 15700; }

        static bool Redirected()
        {
            string probe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "glassbell-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(probe, new byte[0]);
                using (var h = CreateFile(probe, 0, 7, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero))
                {
                    var real = new StringBuilder(1024);
                    if (h.IsInvalid || GetFinalPathNameByHandle(h, real, 1024, 0) == 0) return false;
                    string p = real.ToString();
                    return p.IndexOf(@"\Packages\", StringComparison.OrdinalIgnoreCase) >= 0
                        && p.IndexOf(@"\LocalCache\", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
            finally { try { File.Delete(probe); } catch { } }
        }
    }
}
