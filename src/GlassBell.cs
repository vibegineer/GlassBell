// GlassBell: fast notification banners with a glass look for Windows, in the style of macOS.
//
//   GlassBell.exe                               start the program (if it is not running) and open its settings
//   GlassBell.exe --daemon                      resident process (started on demand by the client)
//   GlassBell.exe --title T --body B [--app A] [--app-id AUMID] [--seconds S]
//                                               show a banner (starts the daemon if needed)
//   GlassBell.exe --settings | --test | --quit
//   GlassBell.exe --close-all                   close every banner on the screen
//   GlassBell.exe --install | --uninstall       start with Windows on / off, for a copy run without the installer
//   GlassBell.exe --update                      swap in GlassBell.new.exe from the exe's folder and restart
//   GlassBell.exe --stats                       write the memory use of the running copy to log.txt
//
// The daemon also mirrors every Windows toast of every app (UserNotificationListener),
// so the native banner can be silenced with Do Not Disturb and only this one shows.
//
// The glass is a per-pixel model of a thin convex lens, on the GPU (a pixel shader) or else on the CPU:
//   - the backdrop is lightly blurred (the glass bends light instead of frosting it),
//   - near the edge (the "bezel") each pixel samples the backdrop from further inside, by an
//     amount that follows Snell's law on a circular bezel profile, with a small per-channel
//     offset (dispersion),
//   - specular light runs around the silhouette, brighter on the side facing the light,
//   - the tint adapts to the brightness behind it, and the shadow adapts to the backdrop.
// The result, including the shadow, is baked into one bitmap, so animations only move a picture.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using Cursors = System.Windows.Input.Cursors;

[assembly: System.Reflection.AssemblyTitle("GlassBell")]

namespace GlassBell
{
    static class Log
    {
        public static void Write(string s)
        {
            try
            {
                string dir = Program.DataDir;
                Directory.CreateDirectory(dir);
                string f = System.IO.Path.Combine(dir, "log.txt");
                if (File.Exists(f) && new FileInfo(f).Length > 200000) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("s") + " " + s + "\r\n");
            }
            catch { }
        }
    }

    static class Program
    {
        // GLASSBELL_INSTANCE=name runs a separate test instance next to the real one: its own pipe and mutex,
        // no tray icon, no mirroring of system toasts (and GLASSBELL_SETTINGS=path for its own settings file)
        public static readonly string Instance = Environment.GetEnvironmentVariable("GLASSBELL_INSTANCE") ?? "";
        public static readonly string PipeName = "GlassBell.v1" + Instance;
        public const char Sep = '\u001f';

        // Where the program keeps its files: next to the exe when the install folder holds portable.flag
        // (settings.ini, log.txt, the flags), otherwise in %LOCALAPPDATA%\GlassBell as the test builds do.
        // A portable copy can live outside AppData on purpose: a program started from inside a packaged (MSIX) app
        // only sees a private copy of %APPDATA%, %LOCALAPPDATA% and HKCU, which Windows never reads at sign-in.
        public static readonly bool Portable = HasPortableFlag();
        public static readonly string DataDir = Portable
            ? System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location)
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBell");

        static bool HasPortableFlag()
        {
            try { return File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly().Location), "portable.flag")); }
            catch { return false; }
        }

        [STAThread]
        static int Main(string[] args)
        {
            Settings.Load();
            L.Init(Settings.Language);
            bool daemon = false, test = false, quit = false, closeAll = false, settings = false, install = false, uninstall = false, update = false, stats = false;
            string app = "", title = "", body = "", appId = "", seconds = "";
            for (int i = 0; i < args.Length; i++)
            {
                string next = i + 1 < args.Length ? args[i + 1] : "";
                switch (args[i])
                {
                    case "--daemon": daemon = true; break;
                    case "--test": test = true; break;
                    case "--quit": quit = true; break;
                    case "--close-all": closeAll = true; break;
                    case "--settings": settings = true; break;
                    case "--install": install = true; break;
                    case "--uninstall": uninstall = true; break;
                    case "--update": update = true; break;
                    case "--stats": stats = true; break;
                    case "--app": app = next; i++; break;
                    case "--title": title = next; i++; break;
                    case "--body": body = next; i++; break;
                    case "--app-id": appId = next; i++; break;
                    case "--seconds": seconds = next; i++; break;
                }
            }

            if (daemon) return Daemon.Run();
            if (install) return Startup.Install();
            if (uninstall) return Startup.Uninstall();
            if (quit) { TrySend("\u0001quit", 50); return 0; }
            if (closeAll) { TrySend("\u0001closeall", 500); return 0; }
            if (update) { TrySend("\u0001update", 500); return 0; }
            if (stats) { TrySend("\u0001stats", 500); return 0; }
            // a double click on the exe: start the program (if it is not running) and open its settings
            if (args.Length == 0 || settings) { Send("\u0001settings"); return 0; }
            if (test)
            {
                Send(Pack("GlassBell", L.T("test.title1"), L.T("test.body1"), "", ""));
                Send(Pack("Telegram", L.T("test.title2"), L.T("test.body2"), "", ""));
                return 0;
            }
            if (title.Length > 0 || body.Length > 0)
                Send(Pack(app, title, body, appId, seconds));
            return 0;
        }

        static string Pack(string app, string title, string body, string appId, string seconds)
        {
            return string.Join(Sep.ToString(), new[] { app, title, body, appId, seconds });
        }

        public static void Send(string msg)
        {
            if (TrySend(msg, 40)) return;
            string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
            Process.Start(new ProcessStartInfo(exe, "--daemon") { UseShellExecute = false, CreateNoWindow = true });
            TrySend(msg, 10000);
        }

        static bool TrySend(string msg, int timeoutMs)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    c.Connect(timeoutMs);
                    byte[] b = new UTF8Encoding(false).GetBytes(msg);
                    c.Write(b, 0, b.Length);
                    c.Flush();
                    return true;
                }
            }
            catch { return false; }
        }
    }

    static class Daemon
    {
        public static int Run()
        {
            bool created;
            using (var mutex = new Mutex(true, "Local\\GlassBell.Daemon" + Program.Instance, out created))
            {
                if (!created) return 0;
                Log.Write("daemon start: pid " + Process.GetCurrentProcess().Id + ", sandboxed " + Startup.Sandboxed
                    + ", autostart " + Startup.Enabled + ", files in " + Program.DataDir);
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // a bug while drawing one banner must not take the resident program down
                app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
                {
                    Log.Write("unhandled: " + e.Exception);
                    e.Handled = true;
                };
                var mgr = new Manager(app);
                mgr.Start();
                app.Run();
                mgr.Stop();
                return 0;
            }
        }
    }

    // ---- start with Windows -----------------------------------------------------------

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string ValueName = "GlassBell";

        // a startup entry written from inside a packaged app's private registry would never run (see Sandbox)
        public static bool Sandboxed { get { return Sandbox.Active; } }

        static string Command()
        {
            return "\"" + System.Reflection.Assembly.GetEntryAssembly().Location + "\" --daemon";
        }

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                        return k != null && string.Equals(k.GetValue(ValueName) as string, Command(), StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }

        public static bool Set(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(ValueName, Command(), RegistryValueKind.String);
                    else k.DeleteValue(ValueName, false);
                }
                // a switch-off made in Task Manager's Startup tab is remembered here and would keep the entry dead
                if (on)
                    using (var a = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                        if (a != null) a.DeleteValue(ValueName, false);
                Log.Write("autostart " + (on ? "on" : "off") + ": " + Command());
                return true;
            }
            catch (Exception e) { Log.Write("autostart: " + e.Message); return false; }
        }

        static void Say(string text, bool error)
        {
            System.Windows.Forms.MessageBox.Show(text, "GlassBell", System.Windows.Forms.MessageBoxButtons.OK,
                error ? System.Windows.Forms.MessageBoxIcon.Warning : System.Windows.Forms.MessageBoxIcon.Information,
                System.Windows.Forms.MessageBoxDefaultButton.Button1,
                L.Rtl ? System.Windows.Forms.MessageBoxOptions.RtlReading | System.Windows.Forms.MessageBoxOptions.RightAlign : 0);
        }

        public static int Install()
        {
            if (Sandboxed)
            {
                Say(L.T("msg.sandboxed"), true);
                return 1;
            }
            if (!Set(true)) { Say(L.T("msg.autostartFailed"), true); return 1; }
            Program.Send("\u0001ping");
            Say(L.T("msg.installed"), false);
            return 0;
        }

        public static int Uninstall()
        {
            Set(false);
            Program.Send("\u0001quit");
            Say(L.T("msg.uninstalled"), false);
            return 0;
        }
    }

    // ---- settings ---------------------------------------------------------------------

    static class Settings
    {
        public static bool Glass = true;
        public static string ColorSpec = "auto";   // "auto" or "#RRGGBB"
        public static int Intensity = 60;           // 0..100, strength of the glass (lens, dispersion, highlights)
        public static int TintIntensity = 60;       // 0..100, strength of the background color; 0 = pure glass
        public static int Blur = 35;                // 0..100, how much the backdrop is blurred (0 = sharp, lens only)
        public static int ShadowIntensity = 40;     // 0..100, how dark the shadow is
        public static int ShadowSoftness = 60;      // 0..100, how far the shadow spreads
        public static int ShadowOffset = 40;        // 0..100, how far the shadow drops
        public static int Quality = 2;              // 0..3 = low, medium, high, ultra (what each one does: see Look)
        public static bool Sound = true;            // the chime (Chime.cs) with every banner
        public static int SoundStyle = 0;           // 0..2, see Chime.Names
        public static int SoundVolume = 65;         // 0..100
        public static bool SoundAdapt = true;       // follow the system volume
        public static int Scene = 0;                // playground backdrop, remembered between runs
        public static bool LiveBackground = false;  // the glass follows what is behind it all the time, also while it moves
        public static int LiveFps = 30;             // how often a live glass that stands still is captured again: one of LiveRates
        public static readonly int[] LiveRates = { 10, 20, 30, 60 };
        public static string Engine = "gpu";        // "gpu": the GPU engine draws the banners (GpuEngine), "classic": WPF does
        public static string Language = "auto";     // "auto" (the Windows display language) or one of L.Codes

        public static readonly string[] QualityKeys = { "low", "medium", "high", "ultra" };

        public static string FilePath
        {
            get
            {
                string over = Environment.GetEnvironmentVariable("GLASSBELL_SETTINGS");
                if (!string.IsNullOrEmpty(over)) return over;
                if (Program.Portable) return System.IO.Path.Combine(Program.DataDir, "settings.ini");
                return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "GlassBell", "settings.ini");
            }
        }

        static int Num(string v, int old)
        {
            int n;
            return int.TryParse(v, out n) ? Math.Max(0, Math.Min(100, n)) : old;
        }

        static bool Flag(string v) { return v == "1" || v.ToLowerInvariant() == "true"; }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "glass": Glass = Flag(v); break;
                        case "sound": Sound = Flag(v); break;
                        case "soundstyle": SoundStyle = Math.Min(2, Num(v, SoundStyle)); break;
                        case "soundvol": SoundVolume = Num(v, SoundVolume); break;
                        case "soundadapt": SoundAdapt = Flag(v); break;
                        case "color": ColorSpec = v.Length == 0 ? "auto" : v; break;
                        case "intensity": Intensity = Num(v, Intensity); break;
                        case "tint": TintIntensity = Num(v, TintIntensity); break;
                        case "blur": Blur = Num(v, Blur); break;
                        case "shadow": ShadowIntensity = Num(v, ShadowIntensity); break;
                        case "shadowsoft": ShadowSoftness = Num(v, ShadowSoftness); break;
                        case "shadowoffset": ShadowOffset = Num(v, ShadowOffset); break;
                        case "scene": Scene = Math.Min(3, Num(v, Scene)); break;
                        case "livebg": LiveBackground = Flag(v); break;
                        case "livefps":
                            int fps;
                            if (int.TryParse(v, out fps)) LiveFps = LiveRates.OrderBy(r => Math.Abs(r - fps)).First();
                            break;
                        case "lang": Language = v.Length == 0 ? "auto" : v; break;
                        case "engine": Engine = v.ToLowerInvariant() == "classic" ? "classic" : "gpu"; break;
                        case "quality":
                            int q = Array.IndexOf(QualityKeys, v.ToLowerInvariant());
                            Quality = q >= 0 ? q : Math.Min(3, Num(v, Quality));
                            break;
                    }
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath,
                    "glass=" + (Glass ? "1" : "0") + "\r\ncolor=" + ColorSpec +
                    "\r\nintensity=" + Intensity + "\r\ntint=" + TintIntensity + "\r\nblur=" + Blur +
                    "\r\nshadow=" + ShadowIntensity + "\r\nshadowsoft=" + ShadowSoftness +
                    "\r\nshadowoffset=" + ShadowOffset + "\r\nquality=" + QualityKeys[Quality] +
                    "\r\nsound=" + (Sound ? "1" : "0") + "\r\nsoundstyle=" + SoundStyle + "\r\nsoundvol=" + SoundVolume +
                    "\r\nsoundadapt=" + (SoundAdapt ? "1" : "0") + "\r\nscene=" + Scene +
                    "\r\nlivebg=" + (LiveBackground ? "1" : "0") + "\r\nlivefps=" + LiveFps + "\r\nengine=" + Engine + "\r\nlang=" + Language + "\r\n");
            }
            catch { }
        }

        public static bool TryParseColor(string s, out Color c)
        {
            c = Colors.Black;
            if (s == null) return false;
            s = s.Trim().TrimStart('#');
            if (s.Length != 6) return false;
            int n;
            if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n)) return false;
            c = Color.FromRgb((byte)(n >> 16), (byte)(n >> 8), (byte)n);
            return true;
        }
    }

    // What a banner looks like, resolved from the settings and the system theme.
    sealed class Look
    {
        public bool Glass;
        public Color Tint;
        public double K, TintK, BlurK, ShadowK, SoftK, OffK;   // all 0..1
        public int Quality = 2;
        // and how it sounds (independent of the glass)
        public bool Sound, SoundAdapt;
        public int SoundStyle, SoundVolume;

        // What each quality level computes; the GPU lens and the CPU renderer follow the same switches.
        //   low:    blur + tint + rim light only, backdrop at half resolution, one shadow layer, the glass of a
        //           banner is not re-captured when the stack shifts, and banners animate at 30 fps (every frame of
        //           a layered window is read back from the GPU, so the frames are most of what a banner costs)
        //   medium: the bezel refracts the backdrop, without colour fringes; backdrop still at half resolution
        //   high:   refraction with dispersion, full-resolution gaussian blur, two shadow layers (the original look)
        //   ultra:  a smooth 7-tap spectrum with stronger dispersion, the bezel supersampled 2x2, high-quality blur and
        //           shadows
        // (a glass that follows what is behind it all the time is a switch of its own: LiveBackdrop)
        public bool Refraction { get { return Quality >= 1; } }
        public bool Dispersion { get { return Quality >= 2; } }
        public bool Spectral { get { return Quality >= 3; } }
        public int BackdropDown { get { return Quality <= 1 ? 2 : 1; } }   // resolution divisor of the blurred backdrop
        public bool TwoShadows { get { return Quality >= 1; } }
        public bool RebakeOnShift { get { return Quality >= 1; } }
        public bool LiveBackdrop;   // its own switch now (Settings.LiveBackground), no longer tied to the ultra level
        public int LiveFps = 30;    // how often a live glass that stands still is captured again (Settings.LiveFps)
        public int? AnimationFps { get { return Quality == 0 ? 30 : (int?)null; } }   // null: the display's rate

        // an animation of a banner, at the frame rate of this level
        public DoubleAnimation Anim(double? from, double to, double ms, EasingMode? easing)
        {
            var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms) };
            if (from.HasValue) a.From = from;
            if (easing.HasValue) a.EasingFunction = new CubicEase { EasingMode = easing.Value };
            Timeline.SetDesiredFrameRate(a, AnimationFps);
            return a;
        }
        public RenderingBias Bias { get { return Quality >= 3 ? RenderingBias.Quality : RenderingBias.Performance; } }
        // ultra spreads the colours over +-30 % of the bend (high: +-12 %), about +-2.5 px at the rim by default
        public double DispersionK { get { return (Spectral ? 0.30 : 0.12) * K; } }

        public double LumTint { get { return (0.299 * Tint.R + 0.587 * Tint.G + 0.114 * Tint.B) / 255.0; } }
        // 0 gives pure glass; the flat (no glass) card is opaque
        public double TintAlpha { get { return Glass ? 0.92 * TintK : 1.0; } }
        public double RadiusDip { get { return 26; } }   // the same with and without the glass
        public double ShadowBlurDip { get { return 10 + 60 * SoftK; } }
        public double ShadowDepthDip { get { return 24 * OffK; } }
        public double AmbientOpacity { get { return 0.55 * ShadowK; } }
        public double ContactOpacity { get { return 0.30 * ShadowK; } }
        // how far the CPU-rendered shadow reaches beyond the card (3 sigma of its blur plus the drop), in DIPs
        public double ShadowExtentDip { get { return Math.Min(GlassCard.ShadowPad, Math.Ceiling(1.35 * ShadowBlurDip + ShadowDepthDip + 2)); } }

        public static Look Current()
        {
            Settings.Load();
            return FromSettings();
        }

        public static Look FromSettings()
        {
            Color tint;
            if (!Settings.TryParseColor(Settings.ColorSpec, out tint))
                tint = IsLightTheme() ? Color.FromRgb(0xF5, 0xF5, 0xF7) : Color.FromRgb(0x1E, 0x1E, 0x20);
            return new Look
            {
                Glass = Settings.Glass, Tint = tint, K = Settings.Intensity / 100.0, TintK = Settings.TintIntensity / 100.0, BlurK = Settings.Blur / 100.0,
                ShadowK = Settings.ShadowIntensity / 100.0, SoftK = Settings.ShadowSoftness / 100.0, OffK = Settings.ShadowOffset / 100.0,
                Quality = Settings.Quality, LiveBackdrop = Settings.LiveBackground, LiveFps = Settings.LiveFps,
                Sound = Settings.Sound, SoundStyle = Settings.SoundStyle, SoundVolume = Settings.SoundVolume, SoundAdapt = Settings.SoundAdapt
            };
        }

        static bool IsLightTheme()
        {
            try
            {
                object v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                return v is int && (int)v != 0;
            }
            catch { return false; }
        }
    }

    // ---- app icons --------------------------------------------------------------------

    // The Start menu icon of any app that has an AppUserModelID: packaged tile logos and plain
    // desktop apps alike. The WinRT AppInfo logo only exists for packaged apps.
    static class ShellIcon
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [In] ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath([In] ref Guid id, uint flags, IntPtr token, out IntPtr path);

        // Toasts from tray-icon balloons (Qt, Electron and plain Win32 programs such as AmneziaVPN) have no Start menu
        // entry: Windows invents the AppUserModelID "NotifyIconGeneratedAumid_<id>" and keeps the program's path under
        // the same id in HKCU\Control Panel\NotifyIconSettings. The icon of that program is the one to show.
        static string TrayIconProgram(string aumid)
        {
            const string prefix = "NotifyIconGeneratedAumid_";
            if (!aumid.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings\" + aumid.Substring(prefix.Length)))
                {
                    string path = k != null ? k.GetValue("ExecutablePath") as string : null;
                    return ExpandKnownFolder(path);
                }
            }
            catch { return null; }
        }

        // the registry stores "{FOLDERID}\sub\path.exe" for programs under Program Files, System32 and so on
        static string ExpandKnownFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '{') return path;
            int end = path.IndexOf('}');
            Guid id;
            if (end < 0 || !Guid.TryParse(path.Substring(1, end - 1), out id)) return path;
            IntPtr p;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out p) != 0 || p == IntPtr.Zero) return path;
            string root = Marshal.PtrToStringUni(p);
            Marshal.FreeCoTaskMem(p);
            return System.IO.Path.Combine(root, path.Substring(end + 1).TrimStart('\\'));
        }

        public static ImageSource Get(string aumid, int px)
        {
            if (string.IsNullOrEmpty(aumid)) return null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                string target = "shell:AppsFolder\\" + aumid;
                string program = TrayIconProgram(aumid);
                if (program != null)
                {
                    if (!File.Exists(program)) { Log.Write("icon " + aumid + ": program not found: " + program); return null; }
                    target = program;
                }
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                IShellItemImageFactory f;
                SHCreateItemFromParsingName(target, IntPtr.Zero, ref iid, out f);
                int hr = f.GetImage(new SIZE { cx = px, cy = px }, 0x4 /* SIIGBF_ICONONLY */, out hbm);
                if (hr != 0 || hbm == IntPtr.Zero) { Log.Write("icon " + aumid + ": GetImage hr=" + hr); return null; }
                var src = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch (Exception e) { Log.Write("icon " + aumid + ": " + e.Message); return null; }
            finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
        }
    }

    // ---- GPU path: the lens as a WPF pixel shader --------------------------------------

    // The same lens model as Glass.Render, but evaluated per pixel on the GPU. The HLSL is compiled with the
    // D3DCompiler that ships with Windows, so no SDK is needed: one variant per quality level (QUALITY 0..3),
    // the one in use at startup, the others the first time they are asked for.
    static class Gpu
    {
        public static bool Ok;
        static readonly PixelShader[] variants = new PixelShader[4];
        static readonly bool[] failed = new bool[4];

        [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ID3DBlob
        {
            [PreserveSig] IntPtr GetBufferPointer();
            [PreserveSig] IntPtr GetBufferSize();
        }

        [DllImport("d3dcompiler_47.dll")]
        static extern int D3DCompile(byte[] src, IntPtr srcSize, [MarshalAs(UnmanagedType.LPStr)] string sourceName,
            IntPtr defines, IntPtr include, [MarshalAs(UnmanagedType.LPStr)] string entry,
            [MarshalAs(UnmanagedType.LPStr)] string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

        const string Hlsl = @"
sampler2D Input : register(s0);
float4 c0 : register(c0);   // texture size px (xy), card size px (zw)
float4 c1 : register(c1);   // margin px, corner radius px, bezel px, lens depth px
float4 c2 : register(c2);   // dispersion, saturation, lift, glass intensity k
float4 c3 : register(c3);   // tint rgb, tint alpha
float4 c4 : register(c4);   // light xy, scale, glass on (1) / flat (0)

static const float IOR = 1.5;

// signed distance (> 0 inside) to the rounded rect and its outward normal; p is in px from the card centre
float Shape(float2 p, out float2 n)
{
    float2 hs = c0.zw * 0.5;
    float r = min(c1.y, min(hs.x, hs.y));
    float2 q = abs(p) - (hs - r);
    float2 o = max(q, 0.0);
    float outside = length(o);
    n = float2(0.0, 0.0);
    if (outside > 0.0001) n = sign(p) * o / outside;
    else if (q.x > q.y) n = float2(sign(p.x), 0.0);
    else n = float2(0.0, sign(p.y));
    return -(outside + min(max(q.x, q.y), 0.0) - r);
}

// how far (px) the convex bezel bends the line of sight at a point d px inside the edge (Snell's law)
float Bend(float d)
{
    float t = saturate(d / max(c1.z, 1.0));
    float u = 1.0 - t;
    float hh = sqrt(max(0.000001, 1.0 - u * u));
    float theta = atan(u / hh * 1.8);
    float thetaR = asin(sin(theta) / IOR);
    float disp = c1.w * tan(theta - thetaR);
    if (t >= 1.0) disp = 0.0;
    return disp;
}

#if QUALITY >= 3
// a smooth spectrum instead of three hard copies: 7 taps spread along the bend, each weighted into r, g and b
float3 Spectrum(float2 uv, float2 duv)
{
    float3 acc = 0.0;
    [unroll] for (int i = 0; i < 7; i++)
    {
        float f = i / 6.0;
        float3 w = saturate(float3(1.0 - 2.0 * f, 1.0 - abs(2.0 * f - 1.0), 2.0 * f - 1.0));
        acc += w * tex2Dlod(Input, float4(uv - duv * (1.0 + c2.x * (2.0 * f - 1.0)), 0.0, 0.0)).rgb;
    }
    return acc / float3(2.0, 3.0, 2.0);
}
#endif

float4 main(float2 uv : TEXCOORD0) : COLOR
{
    float2 texSize = c0.xy;
    float2 card = c0.zw;
    float2 p = uv * texSize - c1.xx - card * 0.5;
    float2 n;
    float d = Shape(p, n);
    float cov = saturate(d + 0.5);
    if (cov <= 0.0) return float4(0.0, 0.0, 0.0, 0.0);

    float scale = c4.z;
    float k = c2.w;
    float glassOn = c4.w;
    float bezel = max(c1.z, 1.0);
    float t = saturate(d / bezel);

#if QUALITY == 0
    float3 col = tex2D(Input, uv).rgb;                              // nothing bends: blur, tint and light only
#elif QUALITY == 1
    float3 col = tex2D(Input, uv - n * Bend(d) / texSize).rgb;     // refraction, one sample for all channels
#elif QUALITY == 2
    float2 duv = n * Bend(d) / texSize;
    float cr = tex2D(Input, uv - duv * (1.0 - c2.x)).r;
    float cg = tex2D(Input, uv - duv).g;
    float cb = tex2D(Input, uv - duv * (1.0 + c2.x)).b;
    float3 col = float3(cr, cg, cb);
#else
    float3 col;
    if (d > bezel + 1.0) col = tex2Dlod(Input, float4(uv, 0.0, 0.0)).rgb;   // the flat middle bends nothing
    else
    {
        // 2x2 rotated grid: near the rim the bend changes by several px per px, so one sample per pixel steps
        col = 0.0;
        [loop] for (int s = 0; s < 4; s++)
        {
            float2 o = float2((s == 1 ? 0.375 : s == 3 ? 0.125 : s == 0 ? -0.125 : -0.375), -0.375 + 0.25 * s);
            float2 ns;
            float ds = Shape(p + o, ns);
            col += Spectrum(uv + o / texSize, ns * Bend(ds) / texSize);
        }
        col *= 0.25;
    }
#endif
    float lm = dot(col, float3(0.299, 0.587, 0.114));
    col = lm + (col - lm) * c2.y;
    col = col * (1.0 - c2.z) + c2.z;

    float3 tint = c3.rgb;
    if (glassOn > 0.5) tint = saturate(c3.rgb * (0.78 + 0.45 * lm));
    col = lerp(col, tint, c3.w);

    float ndl = dot(n, c4.xy);
    float face = max(0.0, ndl);
    float back = max(0.0, -ndl);
    float dirw = pow(face, 1.3) + 0.55 * pow(back, 1.6);
    float ridge = exp(-(d * d) / (2.0 * pow(0.9 * scale, 2.0)));
    float glow = exp(-d / (6.0 * scale)) * 0.30;
    float rim = (0.30 + 0.70 * k) * dirw * (ridge * 0.95 + glow) + 0.10 * (0.4 + 0.6 * k) * ridge;
    rim += k * 0.16 * pow(1.0 - t, 2.5);
    float yy = (uv.y * texSize.y - c1.x) / card.y;
    rim += 0.05 * k * max(0.0, 1.0 - yy / 0.55);
    if (glassOn < 0.5) rim *= 0.35;
    rim = min(rim, 1.0);
    col += (1.0 - col) * rim;
    col *= 1.0 - 0.14 * k * back * exp(-d / (10.0 * scale));
    col = saturate(col);
    return float4(col * cov, cov);
}";

        public static void Init(int quality)
        {
            try
            {
                if (Environment.GetEnvironmentVariable("GLASSBELL_CPU") == "1") { Log.Write("GPU lens: disabled by GLASSBELL_CPU"); return; }
                if (RenderCapability.Tier >> 16 < 2 || !RenderCapability.IsPixelShaderVersionSupported(3, 0))
                {
                    Log.Write("GPU lens: pixel shader 3.0 not available, using the CPU renderer");
                    return;
                }
                Ok = Compile(quality) != null || Compile(2) != null;
            }
            catch (Exception e) { Log.Write("GPU lens init: " + e.Message); }
        }

        // the lens for a quality level; a variant that does not compile falls back to one that did
        public static PixelShader Shader(int quality)
        {
            PixelShader s = Compile(quality);
            for (int q = 2; s == null && q >= 0; q--) s = variants[q];
            return s;
        }

        static PixelShader Compile(int quality)
        {
            if (variants[quality] != null || failed[quality]) return variants[quality];
            try
            {
                byte[] src = Encoding.ASCII.GetBytes("#define QUALITY " + quality + "\n" + Hlsl);
                IntPtr code, err;
                // optimisation level 3, except for ultra: its loops took 0.7 s to compile that way, 0.1 s at level 1,
                // for practically the same code
                uint flags = quality >= 3 ? 0u : 0x8000u;
                int hr = D3DCompile(src, (IntPtr)src.Length, "lens", IntPtr.Zero, IntPtr.Zero, "main", "ps_3_0", flags, 0, out code, out err);
                if (hr != 0 || code == IntPtr.Zero)
                {
                    string msg = "";
                    if (err != IntPtr.Zero)
                    {
                        var eb = (ID3DBlob)Marshal.GetObjectForIUnknown(err);
                        msg = Marshal.PtrToStringAnsi(eb.GetBufferPointer());
                    }
                    Log.Write("GPU lens: shader " + quality + " compile failed hr=" + hr + " " + msg);
                    failed[quality] = true;
                    return null;
                }
                var blob = (ID3DBlob)Marshal.GetObjectForIUnknown(code);
                int n = (int)blob.GetBufferSize();
                var bc = new byte[n];
                Marshal.Copy(blob.GetBufferPointer(), bc, 0, n);
                var ps = new PixelShader();
                ps.SetStreamSource(new MemoryStream(bc));
                if (ps.CanFreeze) ps.Freeze();
                variants[quality] = ps;
                Log.Write("GPU lens: ok, quality " + quality + ", " + n + " bytes of ps_3_0");
                return ps;
            }
            catch (Exception e) { Log.Write("GPU lens " + quality + ": " + e.Message); failed[quality] = true; return null; }
        }
    }

    sealed class LensEffect : ShaderEffect
    {
        public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(LensEffect), 0);
        public static readonly DependencyProperty C0Property = Reg("C0", 0);
        public static readonly DependencyProperty C1Property = Reg("C1", 1);
        public static readonly DependencyProperty C2Property = Reg("C2", 2);
        public static readonly DependencyProperty C3Property = Reg("C3", 3);
        public static readonly DependencyProperty C4Property = Reg("C4", 4);

        static DependencyProperty Reg(string name, int register)
        {
            return DependencyProperty.Register(name, typeof(System.Windows.Media.Media3D.Point4D), typeof(LensEffect),
                new UIPropertyMetadata(new System.Windows.Media.Media3D.Point4D(), PixelShaderConstantCallback(register)));
        }

        public LensEffect(int quality)
        {
            PixelShader = Gpu.Shader(quality);
            UpdateShaderValue(InputProperty);
            UpdateShaderValue(C0Property); UpdateShaderValue(C1Property); UpdateShaderValue(C2Property);
            UpdateShaderValue(C3Property); UpdateShaderValue(C4Property);
        }

        // unsubscribe from the shared shader (see GlassCard.ReleaseEffects)
        public void Detach() { PixelShader = null; }

        // switch to the variant of another quality level
        public void Use(int quality)
        {
            var s = Gpu.Shader(quality);
            if (s != null && PixelShader != s) PixelShader = s;
        }

        public System.Windows.Media.Media3D.Point4D C0 { get { return (System.Windows.Media.Media3D.Point4D)GetValue(C0Property); } set { SetValue(C0Property, value); } }
        public System.Windows.Media.Media3D.Point4D C1 { get { return (System.Windows.Media.Media3D.Point4D)GetValue(C1Property); } set { SetValue(C1Property, value); } }
        public System.Windows.Media.Media3D.Point4D C2 { get { return (System.Windows.Media.Media3D.Point4D)GetValue(C2Property); } set { SetValue(C2Property, value); } }
        public System.Windows.Media.Media3D.Point4D C3 { get { return (System.Windows.Media.Media3D.Point4D)GetValue(C3Property); } set { SetValue(C3Property, value); } }
        public System.Windows.Media.Media3D.Point4D C4 { get { return (System.Windows.Media.Media3D.Point4D)GetValue(C4Property); } set { SetValue(C4Property, value); } }
    }

    // ---- the glass renderer (CPU reference / fallback) ---------------------------------

    // A BGRA snapshot of the pixels around a card, with the card's top-left at (Cx, Cy).
    // A playground snapshot is only a view into the scene buffer (Source), so moving a card copies nothing.
    // A live strip (Settings.LiveBackground) covers a card's whole way across the screen: then (Cx, Cy, CW, CH) is that
    // region and (OffX, OffY) where the card sits inside it, in pixels; for a plain snapshot both are 0.
    sealed class Cap
    {
        public byte[] Data;
        public int W, H, Cx, Cy, CW, CH;
        public int OffX, OffY;
        public object Source;   // the buffer Data belongs to, or null for a fresh screen capture

        // the card-sized part of a strip where the card is (ox, oy) pixels into it
        public Cap View(int ox, int oy, int cw, int ch)
        {
            return new Cap { Data = Data, W = W, H = H, Cx = Cx + ox, Cy = Cy + oy, CW = cw, CH = ch, Source = Source };
        }
    }

    static class Glass
    {
        public const double Ior = 1.5;          // index of refraction of the glass
        const double LightX = -0.55, LightY = -0.83;   // direction towards the light (top-left)

        // ---- geometry of the lens and blur, shared by capture and render ----

        public static void Lens(Look look, double scale, int cw, int ch, out double bezel, out double thick, out double strength)
        {
            double k = look.K;
            bezel = Math.Min(Math.Min(cw, ch) * 0.5, (9 + 17 * k) * scale);
            thick = bezel * 0.9;
            strength = 0.30 + 0.70 * k;
        }

        public static double Sigma(Look look, double scale)
        {
            // its own setting now; the curve keeps the low end fine-grained (0 = no blur at all)
            return look.Glass ? 9.0 * Math.Pow(look.BlurK, 1.4) * scale : 0;
        }

        // Room around the card that the snapshot must cover. Only the blur reaches outside the card: the bezel
        // always bends the line of sight inwards (towards the middle), so refraction needs no margin of its own.
        public static int Margin(Look look, double scale, int cw, int ch)
        {
            if (!look.Glass) return 0;
            return (int)Math.Ceiling(3 * Sigma(look, scale) + 3);
        }

        // The snapshot grown to exactly card + margin on every side (edges replicated where the screen ends),
        // as the GPU lens wants it. Reuses buf when it has the right size; the 4th byte is left as it is (unused).
        public static byte[] Padded(Cap cap, int m, byte[] buf)
        {
            int tw = cap.CW + 2 * m, th = cap.CH + 2 * m;
            var o = buf != null && buf.Length == tw * th * 4 ? buf : new byte[tw * th * 4];
            int x0 = cap.Cx - m;
            // columns [inL, inR) lie inside the snapshot and are copied row by row, the rest repeat its edge
            int inL = Math.Min(tw, Math.Max(0, -x0)), inR = Math.Max(inL, Math.Min(tw, cap.W - x0));
            for (int y = 0; y < th; y++)
            {
                int row = Math.Max(0, Math.Min(cap.H - 1, cap.Cy - m + y)) * cap.W * 4;
                int di = y * tw * 4;
                if (inR > inL) Buffer.BlockCopy(cap.Data, row + (x0 + inL) * 4, o, di + inL * 4, (inR - inL) * 4);
                for (int x = 0; x < inL; x++) Buffer.BlockCopy(cap.Data, row, o, di + x * 4, 4);
                for (int x = inR; x < tw; x++) Buffer.BlockCopy(cap.Data, row + (cap.W - 1) * 4, o, di + x * 4, 4);
            }
            return o;
        }

        // 2x2 box average to half resolution (odd edges repeat the last pixel)
        public static byte[] Half(byte[] s, int w, int h, byte[] buf, out int hw, out int hh)
        {
            hw = (w + 1) / 2; hh = (h + 1) / 2;
            var o = buf != null && buf.Length == hw * hh * 4 ? buf : new byte[hw * hh * 4];
            for (int y = 0; y < hh; y++)
            {
                int r0 = 2 * y * w * 4, r1 = Math.Min(h - 1, 2 * y + 1) * w * 4, di = y * hw * 4;
                for (int x = 0; x < hw; x++, di += 4)
                {
                    int a = 2 * x * 4, b = Math.Min(w - 1, 2 * x + 1) * 4;
                    for (int c = 0; c < 3; c++)
                        o[di + c] = (byte)((s[r0 + a + c] + s[r0 + b + c] + s[r1 + a + c] + s[r1 + b + c] + 2) >> 2);
                    o[di + 3] = 255;
                }
            }
            return o;
        }

        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        static extern int memcmp(byte[] a, byte[] b, UIntPtr n);

        public static bool Same(byte[] a, byte[] b)
        {
            return a != null && b != null && a.Length == b.Length && memcmp(a, b, (UIntPtr)a.Length) == 0;
        }

        // average brightness (0..1) of a w x h area, from a coarse grid of samples
        public static double MeanLum(byte[] buf, int tw, int x0, int y0, int w, int h)
        {
            double sum = 0; int n = 0;
            int sx = Math.Max(1, w / 24), sy = Math.Max(1, h / 12);
            for (int y = y0; y < y0 + h; y += sy)
                for (int x = x0; x < x0 + w; x += sx)
                {
                    int i = (y * tw + x) * 4;
                    sum += 0.114 * buf[i] + 0.587 * buf[i + 1] + 0.299 * buf[i + 2];
                    n++;
                }
            return n > 0 ? sum / n / 255.0 : 0.5;
        }

        // ---- capturing what is behind a card ----

        public static Cap FromScreen(Rect card, double scale, Look look)
        {
            var scr = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
            return Make(scr.Left, scr.Top, scr.Right, scr.Bottom, card, scale, look, Grab);
        }

        // a view into the scene buffer, nothing is copied (Padded clamps to its edges like it does to the screen's)
        public static Cap FromBuffer(byte[] buf, int bw, int bh, Rect card, double scale, Look look)
        {
            int cx = (int)Math.Round(card.X * scale), cy = (int)Math.Round(card.Y * scale);
            int cw = Math.Max(1, (int)Math.Round(card.Width * scale)), ch = Math.Max(1, (int)Math.Round(card.Height * scale));
            return new Cap { Data = buf, W = bw, H = bh, Cx = cx, Cy = cy, CW = cw, CH = ch, Source = buf };
        }

        static Cap Make(int bl, int bt, int br, int bb, Rect card, double scale, Look look, Func<int, int, int, int, byte[]> grab)
        {
            int cx = (int)Math.Round(card.X * scale), cy = (int)Math.Round(card.Y * scale);
            int cw = Math.Max(1, (int)Math.Round(card.Width * scale)), ch = Math.Max(1, (int)Math.Round(card.Height * scale));
            int m = Margin(look, scale, cw, ch);
            int x0 = Math.Max(bl, cx - m), y0 = Math.Max(bt, cy - m);
            int x1 = Math.Min(br, cx + cw + m), y1 = Math.Min(bb, cy + ch + m);
            int w = x1 - x0, h = y1 - y0;
            if (w <= 0 || h <= 0) return null;
            return new Cap { Data = grab(x0, y0, w, h), W = w, H = h, Cx = cx - x0, Cy = cy - y0, CW = cw, CH = ch };
        }

        static byte[] Grab(int x, int y, int w, int h)
        {
            using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
                var d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var buf = new byte[d.Stride * h];
                Marshal.Copy(d.Scan0, buf, 0, buf.Length);
                bmp.UnlockBits(d);
                return buf;
            }
        }

        // ---- rendering ----

        static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }

        // Bilinear sample of one channel; (fx, fy) are pixel-center coordinates in the buffer.
        static double Bilin(byte[] b, int w, int h, double fx, double fy, int c)
        {
            fx -= 0.5; fy -= 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            int xa = Math.Max(0, Math.Min(w - 1, x0)), xb = Math.Max(0, Math.Min(w - 1, x0 + 1));
            int ya = Math.Max(0, Math.Min(h - 1, y0)), yb = Math.Max(0, Math.Min(h - 1, y0 + 1));
            double a = b[(ya * w + xa) * 4 + c] * (1 - tx) + b[(ya * w + xb) * 4 + c] * tx;
            double d = b[(yb * w + xa) * 4 + c] * (1 - tx) + b[(yb * w + xb) * 4 + c] * tx;
            return a * (1 - ty) + d * ty;
        }

        // the same for all three channels at once (red, green, blue)
        static void Bilin3(byte[] b, int w, int h, double fx, double fy, out double r, out double g, out double bl)
        {
            fx -= 0.5; fy -= 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;
            int xa = Math.Max(0, Math.Min(w - 1, x0)), xb = Math.Max(0, Math.Min(w - 1, x0 + 1));
            int ya = Math.Max(0, Math.Min(h - 1, y0)), yb = Math.Max(0, Math.Min(h - 1, y0 + 1));
            int i00 = (ya * w + xa) * 4, i01 = (ya * w + xb) * 4, i10 = (yb * w + xa) * 4, i11 = (yb * w + xb) * 4;
            double w00 = (1 - tx) * (1 - ty), w01 = tx * (1 - ty), w10 = (1 - tx) * ty, w11 = tx * ty;
            bl = b[i00] * w00 + b[i01] * w01 + b[i10] * w10 + b[i11] * w11;
            g = b[i00 + 1] * w00 + b[i01 + 1] * w01 + b[i10 + 1] * w10 + b[i11 + 1] * w11;
            r = b[i00 + 2] * w00 + b[i01 + 2] * w01 + b[i10 + 2] * w10 + b[i11 + 2] * w11;
        }

        // how far (px) the convex circular bezel bends the line of sight at depth t (0 = edge, 1 = flat), Snell's law
        static double Bend(double t, double depth)
        {
            if (t >= 1) return 0;
            double u = 1 - t;
            double hh = Math.Sqrt(Math.Max(1e-6, 1 - u * u));
            double theta = Math.Atan(u / hh * 1.8);
            double thetaR = Math.Asin(Math.Sin(theta) / Ior);
            return depth * Math.Tan(theta - thetaR);
        }

        // Signed distance to a rounded rect centred on the origin (negative inside), plus the outward normal.
        static double Sdf(double px, double py, double hx, double hy, double r, out double nx, out double ny)
        {
            double qx = Math.Abs(px) - (hx - r), qy = Math.Abs(py) - (hy - r);
            double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
            double outside = Math.Sqrt(ox * ox + oy * oy);
            if (qx > 0 && qy > 0 && outside > 1e-9) { nx = Math.Sign(px) * ox / outside; ny = Math.Sign(py) * oy / outside; }
            else if (qx > qy) { nx = Math.Sign(px); ny = 0; }
            else { nx = 0; ny = Math.Sign(py); }
            return outside + Math.Min(Math.Max(qx, qy), 0) - r;
        }

        // Renders the card plus its shadow into one premultiplied bitmap of (cw + 2 pad) x (ch + 2 pad) pixels,
        // pad being the room the shadow needs (none without one). padded is the backdrop as Padded makes it, card +
        // Margin on every side; null for the flat card or when there is no backdrop. lum is the mean brightness of the
        // finished card.
        public static BitmapSource Render(byte[] padded, int cw, int ch, double scale, double radiusPx, Look look,
            bool withShadow, out int pad, out double lum, out double backAvgOut)
        {
            pad = withShadow ? (int)Math.Ceiling(look.ShadowExtentDip * scale) : 0;
            int pw = cw + 2 * pad, ph = ch + 2 * pad;
            double k = look.K;
            bool glass = look.Glass && padded != null;

            // the backdrop around the card, blurred (at half resolution on the low levels)
            byte[] blurred = null;
            int sw = 0, sh = 0, m = 0, down = 1;
            if (glass)
            {
                m = Margin(look, scale, cw, ch);
                sw = cw + 2 * m; sh = ch + 2 * m;
                down = look.BackdropDown;
                blurred = down > 1 ? Half(padded, sw, sh, null, out sw, out sh) : (byte[])padded.Clone();
                Blur(blurred, sw, sh, 4, 3, Sigma(look, scale) / down);
            }

            double bezel = 1, thick = 0, strength = 0;
            if (glass) Lens(look, scale, cw, ch, out bezel, out thick, out strength);
            double depth = strength * thick;
            double sat = 1 + 0.65 * k, lift = 0.025 + 0.045 * k, dispersion = look.DispersionK;
            double ta = look.TintAlpha;
            double ll = Math.Sqrt(LightX * LightX + LightY * LightY), lx = LightX / ll, ly = LightY / ll;
            double hx = cw * 0.5, hy = ch * 0.5, r = Math.Min(radiusPx, Math.Min(hx, hy));

            var card = new byte[cw * ch * 4];         // premultiplied BGRA of the card alone
            double lumSum = 0, backSum = 0; long lumN = 0;
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    double px = x + 0.5 - hx, py = y + 0.5 - hy, nx, ny;
                    double d = -Sdf(px, py, hx, hy, r, out nx, out ny);   // > 0 inside
                    double cov = d <= -0.5 ? 0 : d >= 0.5 ? 1 : d + 0.5;
                    if (cov <= 0) continue;

                    double cr, cg, cb, backLum = 0.5;
                    if (glass)
                    {
                        double t = Clamp(d / bezel, 0, 1);
                        double bx = m + x + 0.5, by = m + y + 0.5;   // in the padded backdrop
                        if (!look.Refraction)
                            Bilin3(blurred, sw, sh, bx / down, by / down, out cr, out cg, out cb);
                        else if (!look.Dispersion)
                        {
                            double disp = Bend(t, depth);
                            Bilin3(blurred, sw, sh, (bx - nx * disp) / down, (by - ny * disp) / down, out cr, out cg, out cb);
                        }
                        else if (!look.Spectral || d > bezel + 1)
                        {
                            double disp = Bend(t, depth);
                            double dr = disp * (1 - dispersion), dg = disp, db = disp * (1 + dispersion);
                            cr = Bilin(blurred, sw, sh, bx - nx * dr, by - ny * dr, 2);
                            cg = Bilin(blurred, sw, sh, bx - nx * dg, by - ny * dg, 1);
                            cb = Bilin(blurred, sw, sh, bx - nx * db, by - ny * db, 0);
                        }
                        else
                        {
                            // ultra: 2x2 rotated grid inside the pixel, each a 7-tap spectrum along the bend
                            cr = cg = cb = 0;
                            for (int s = 0; s < 4; s++)
                            {
                                double ox = s == 1 ? 0.375 : s == 3 ? 0.125 : s == 0 ? -0.125 : -0.375, oy = -0.375 + 0.25 * s, snx, sny;
                                double sd = -Sdf(px + ox, py + oy, hx, hy, r, out snx, out sny);
                                double sdisp = Bend(Clamp(sd / bezel, 0, 1), depth);
                                for (int tap = 0; tap < 7; tap++)
                                {
                                    double f = tap / 6.0, sr, sg, sb;
                                    double f2 = sdisp * (1 + dispersion * (2 * f - 1));
                                    Bilin3(blurred, sw, sh, bx + ox - snx * f2, by + oy - sny * f2, out sr, out sg, out sb);
                                    cr += sr * Math.Max(0, 1 - 2 * f) / 2;
                                    cg += sg * Math.Max(0, 1 - Math.Abs(2 * f - 1)) / 3;
                                    cb += sb * Math.Max(0, 2 * f - 1) / 2;
                                }
                            }
                            cr *= 0.25; cg *= 0.25; cb *= 0.25;
                        }
                        double lm = 0.299 * cr + 0.587 * cg + 0.114 * cb;
                        backLum = lm / 255.0;
                        cr = lm + (cr - lm) * sat; cg = lm + (cg - lm) * sat; cb = lm + (cb - lm) * sat;
                        cr = cr * (1 - lift) + 255 * lift; cg = cg * (1 - lift) + 255 * lift; cb = cb * (1 - lift) + 255 * lift;
                        if (ta > 0)
                        {
                            // adaptive tint: the tone follows the brightness of what is behind
                            double f = 0.78 + 0.45 * backLum;
                            cr = cr * (1 - ta) + Clamp(look.Tint.R * f, 0, 255) * ta;
                            cg = cg * (1 - ta) + Clamp(look.Tint.G * f, 0, 255) * ta;
                            cb = cb * (1 - ta) + Clamp(look.Tint.B * f, 0, 255) * ta;
                        }

                        // specular light running around the silhouette, strongest where the edge faces the light
                        double ndl = nx * lx + ny * ly;
                        double face = Math.Max(0, ndl), back = Math.Max(0, -ndl);
                        double dir = Math.Pow(face, 1.3) + 0.55 * Math.Pow(back, 1.6);
                        double line = Math.Exp(-(d * d) / (2 * Math.Pow(0.9 * scale, 2)));
                        double glow = Math.Exp(-d / (6 * scale)) * 0.30;
                        double rim = (0.30 + 0.70 * k) * dir * (line * 0.95 + glow) + 0.10 * (0.4 + 0.6 * k) * line;
                        rim += k * 0.16 * Math.Pow(1 - t, 2.5);                       // fresnel-like brightening along the bezel
                        rim += 0.05 * k * Math.Max(0, 1 - (y + 0.5) / (ch * 0.55));    // faint sheen from above
                        rim = Math.Min(rim, 1);
                        cr += (255 - cr) * rim; cg += (255 - cg) * rim; cb += (255 - cb) * rim;
                        double inner = 1 - 0.14 * k * back * Math.Exp(-d / (10 * scale));   // soft depth on the far side
                        cr *= inner; cg *= inner; cb *= inner;
                    }
                    else
                    {
                        cr = look.Tint.R; cg = look.Tint.G; cb = look.Tint.B;
                        double edge = 0.12 * Math.Exp(-(d * d) / (2 * Math.Pow(0.9 * scale, 2)));
                        double lightTint = look.LumTint;
                        double e = lightTint > 0.6 ? -edge : edge;
                        cr += (e > 0 ? 255 - cr : cr) * e; cg += (e > 0 ? 255 - cg : cg) * e; cb += (e > 0 ? 255 - cb : cb) * e;
                        backLum = look.LumTint;
                    }
                    cr = Clamp(cr, 0, 255); cg = Clamp(cg, 0, 255); cb = Clamp(cb, 0, 255);

                    int i = (y * cw + x) * 4;
                    card[i] = (byte)(cb * cov); card[i + 1] = (byte)(cg * cov); card[i + 2] = (byte)(cr * cov); card[i + 3] = (byte)(255 * cov);
                    if (d > 3 * scale)
                    {
                        lumSum += (0.299 * cr + 0.587 * cg + 0.114 * cb) / 255.0;
                        backSum += backLum;
                        lumN++;
                    }
                }
            }
            lum = lumN > 0 ? lumSum / lumN : 0.5;
            double backAvg = lumN > 0 ? backSum / lumN : 0.5;
            backAvgOut = backAvg;

            // shadow: two layers (wide ambient + tight contact) from the blurred silhouette, adapted to the backdrop;
            // the low level keeps only the wide one, as dark as the two together
            double adapt = Clamp(1.2 - 0.6 * backAvg, 0.45, 1.2);
            var o = new byte[pw * ph * 4];
            double ambient = look.AmbientOpacity * adapt, contact = look.ContactOpacity * adapt;
            if (withShadow && (ambient > 0.002 || contact > 0.002))
            {
                byte[] mAmb = Mask(pw, ph, pad, pad + (int)Math.Round(look.ShadowDepthDip * scale), cw, ch, r);
                Blur(mAmb, pw, ph, 1, 1, look.ShadowBlurDip * scale * 0.45);
                byte[] mCon = null;
                if (look.TwoShadows)
                {
                    mCon = Mask(pw, ph, pad, pad + (int)Math.Round(look.ShadowDepthDip * 0.3 * scale), cw, ch, r);
                    Blur(mCon, pw, ph, 1, 1, Math.Max(1.5, look.ShadowBlurDip * 0.12 * scale * 0.45));
                }
                else ambient = 1 - (1 - ambient) * (1 - contact);
                for (int i = 0; i < pw * ph; i++)
                {
                    double a1 = mAmb[i] / 255.0 * ambient, a2 = mCon != null ? mCon[i] / 255.0 * contact : 0;
                    o[i * 4 + 3] = (byte)(255 * (1 - (1 - a1) * (1 - a2)));
                }
            }
            for (int y = 0; y < ch; y++)
            {
                for (int x = 0; x < cw; x++)
                {
                    int ci = (y * cw + x) * 4, oi = ((y + pad) * pw + x + pad) * 4;
                    int inv = 255 - card[ci + 3];
                    o[oi] = (byte)(card[ci] + o[oi] * inv / 255);
                    o[oi + 1] = (byte)(card[ci + 1] + o[oi + 1] * inv / 255);
                    o[oi + 2] = (byte)(card[ci + 2] + o[oi + 2] * inv / 255);
                    o[oi + 3] = (byte)(card[ci + 3] + o[oi + 3] * inv / 255);
                }
            }
            var bmp = BitmapSource.Create(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null, o, pw * 4);
            bmp.Freeze();
            return bmp;
        }

        // coverage mask of the rounded rect placed at (x0, y0)
        static byte[] Mask(int pw, int ph, int x0, int y0, int cw, int ch, double r)
        {
            var m = new byte[pw * ph];
            double hx = cw * 0.5, hy = ch * 0.5, nx, ny;
            int ya = Math.Max(0, y0 - 1), yb = Math.Min(ph, y0 + ch + 1), xa = Math.Max(0, x0 - 1), xb = Math.Min(pw, x0 + cw + 1);
            for (int y = ya; y < yb; y++)
                for (int x = xa; x < xb; x++)
                {
                    double d = -Sdf(x + 0.5 - x0 - hx, y + 0.5 - y0 - hy, hx, hy, r, out nx, out ny);
                    double cov = d <= -0.5 ? 0 : d >= 0.5 ? 1 : d + 0.5;
                    if (cov > 0) m[y * pw + x] = (byte)(255 * cov);
                }
            return m;
        }

        // ---- blur: three box passes approximate a gaussian ----

        public static void Blur(byte[] a, int w, int h, int stride, int nch, double sigma)
        {
            if (sigma < 0.6) return;
            int bw = (int)Math.Floor(Math.Sqrt(12 * sigma * sigma / 3 + 1));
            if (bw % 2 == 0) bw--;
            int r = Math.Max(1, (bw - 1) / 2);
            byte[] b = new byte[a.Length];
            for (int pass = 0; pass < 3; pass++) { BlurH(a, b, w, h, stride, nch, r); BlurV(b, a, w, h, stride, nch, r); }
        }

        static void BlurH(byte[] s, byte[] d, int w, int h, int stride, int nch, int r)
        {
            int win = 2 * r + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w * stride;
                for (int c = 0; c < nch; c++)
                {
                    int sum = 0;
                    for (int i = -r; i <= r; i++) sum += s[row + Math.Min(w - 1, Math.Max(0, i)) * stride + c];
                    for (int x = 0; x < w; x++)
                    {
                        d[row + x * stride + c] = (byte)(sum / win);
                        sum += s[row + Math.Min(w - 1, x + r + 1) * stride + c] - s[row + Math.Max(0, x - r) * stride + c];
                    }
                }
            }
        }

        static void BlurV(byte[] s, byte[] d, int w, int h, int stride, int nch, int r)
        {
            int win = 2 * r + 1, line = w * stride;
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < nch; c++)
                {
                    int col = x * stride + c, sum = 0;
                    for (int i = -r; i <= r; i++) sum += s[Math.Min(h - 1, Math.Max(0, i)) * line + col];
                    for (int y = 0; y < h; y++)
                    {
                        d[y * line + col] = (byte)(sum / win);
                        sum += s[Math.Min(h - 1, y + r + 1) * line + col] - s[Math.Max(0, y - r) * line + col];
                    }
                }
            }
        }
    }

    sealed class ShadowSpec
    {
        public const double MinOpacity = 0.003;   // below this a layer is not drawn at all
        public double Radius, BlurA, DepthA, OpA, BlurB, DepthB, OpB;
        public RenderingBias Bias;

        public bool Visible { get { return OpA >= MinOpacity || OpB >= MinOpacity; } }
        // room (DIPs) the visible layers need around the card: the blur spreads a layer by its radius, the drop moves it down
        public double Spread { get { return Math.Ceiling(Math.Max(OpA >= MinOpacity ? BlurA : 0, OpB >= MinOpacity ? BlurB : 0)) + 2; } }
        public double Below { get { return Math.Ceiling(Math.Max(OpA >= MinOpacity ? BlurA + DepthA : 0, OpB >= MinOpacity ? BlurB + DepthB : 0)) + 2; } }
    }

    // ---- a card of glass with text on it (used by banners and by the workshop) ---------

    sealed class GlassCard : Grid
    {
        public const double ShadowPad = 100;   // DIPs of room around the card for the shadow, at most

        public Look Look;
        public double CardH;
        // Banners draw their shadow in a separate click-through window (see ShadowWindow): shadow pixels inside the
        // banner's own layered window would swallow mouse clicks. The playground keeps them inside the card.
        public bool ExternalShadows;
        public ShadowSpec Shadow;
        readonly bool capsule, engine;
        readonly bool gpu = Gpu.Ok;
        FrameworkElement contentHost;   // the text and the icon
        // CPU fallback: everything baked into one bitmap
        readonly Image glassImage = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        // GPU path: a clipped host holding the (blurred) backdrop, with the lens shader on top, plus two shadow layers
        // lensHost is larger than the card, so it sits on a Canvas: a Canvas never clips its children to the layout slot
        readonly Canvas lensCanvas = new Canvas { IsHitTestVisible = false };
        // the near-invisible fill matters: an effect on an element with nothing to draw is skipped by WPF, which is
        // what broke the flat (glass off) card and left only the black shadow body showing
        readonly Grid lensHost = new Grid { ClipToBounds = true, IsHitTestVisible = false, Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        // The blurred backdrop and the shadows are cached as bitmaps (BitmapCache): WPF re-runs an effect on every
        // frame that touches it, so without the cache a tint change, a drag or one frame of the slide-in would blur
        // the backdrop and both shadows again. Now they are redrawn only when their own input changes.
        // blurHost also clips: WPF applies an element's effect before its own clip, so the lens used to receive the
        // blur's overflow as part of its input, which stretched the glass beyond the card by up to the blur radius
        // (about 4 px per side at the default blur, 20 px at the maximum). Clipped one level down, the lens input is
        // exactly lensHost and the glass sits on the card's layout bounds, like the CPU renderer's always did.
        // The cache sits on backdropHost, inside the clip: a live strip (see Cap) is bigger than the lens and slides
        // under it by stripShift while the card moves, and a cached bitmap that only moves is not blurred again.
        // blurHost is a Canvas because a Grid would cut a child bigger than itself to its own size before the shift.
        readonly Canvas blurHost = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        readonly Grid backdropHost = new Grid { IsHitTestVisible = false, CacheMode = new BitmapCache(), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        readonly TranslateTransform stripShift = new TranslateTransform();
        readonly Image backdropImg = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        readonly Grid shadowHost = new Grid { IsHitTestVisible = false, CacheMode = new BitmapCache() };
        readonly Border shadowA = new Border { IsHitTestVisible = false }, shadowB = new Border { IsHitTestVisible = false };
        LensEffect lens;
        BlurEffect blurFx;
        WriteableBitmap wb;
        // what is on the GPU now (upBuf: the padded, maybe halved backdrop) and where it came from; the CPU renderer
        // keeps its last padded backdrop in upBuf too. padBuf and halfBuf are scratch space, reused between bakes.
        byte[] padBuf, halfBuf, upBuf;
        object upSource;
        int upX = int.MinValue, upY, upM, upDown, upW, upH;
        double upLum = 0.5;
        Look cpuLook;
        double cpuScale;
        // the last live strip and where the card was in it (px): MoveBackdrop works relative to that
        Cap strip;
        int stripX, stripY;
        long cpuMoved;
        TextBlock appText, nowText, titleText, bodyText;
        FrameworkElement iconHost;       // the app icon, sized to the height of the text block
        ColumnDefinition iconColumn;
        StackPanel textPanel;

        static readonly Color[] Hues =
        {
            Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0x30, 0xB0, 0x50), Color.FromRgb(0xFF, 0x9F, 0x0A),
            Color.FromRgb(0xBF, 0x5A, 0xF2), Color.FromRgb(0xFF, 0x45, 0x3A), Color.FromRgb(0x5E, 0xB5, 0xC6)
        };

        static Brush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        // engine: the card of a banner the GPU engine draws (see GpuEngine): no glass and no shadow of its own, only the
        // text and the icon, which the engine gets as a picture (RenderContent)
        public GlassCard(Look look, double width, double minHeight, bool capsule,
            string app, string title, string body, ImageSource icon, bool engine = false)
        {
            Look = look;
            this.capsule = capsule;
            this.engine = engine;
            Width = width;
            MinHeight = minHeight;
            Background = Brushes.Transparent;
            Cursor = Cursors.Hand;
            System.Windows.Documents.TextElement.SetFontFamily(this, new FontFamily("Segoe UI Variable Text, Segoe UI"));
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(L.Ietf);   // picks the fallback font for CJK text

            if (engine) { }
            else if (gpu)
            {
                lens = new LensEffect(look.Quality);
                lensHost.Effect = lens;
                RenderOptions.SetBitmapScalingMode(backdropImg, BitmapScalingMode.Linear);
                backdropHost.RenderTransform = stripShift;
                backdropHost.Children.Add(backdropImg);
                blurHost.Children.Add(backdropHost);
                lensHost.Children.Add(blurHost);
                lensCanvas.Children.Add(lensHost);
                shadowHost.Children.Add(shadowA);
                shadowHost.Children.Add(shadowB);
                Children.Add(shadowHost);
                Children.Add(lensCanvas);
            }
            else Children.Add(glassImage);

            var content = new Grid { Margin = capsule ? new Thickness(22, 0, 22, 2) : new Thickness(16, 13, 16, 14) };
            contentHost = content;
            bool notification = app.Length > 0;
            Brush white = Brushes.White;
            if (notification)
            {
                iconColumn = new ColumnDefinition { Width = new GridLength(40) };
                content.ColumnDefinitions.Add(iconColumn);
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                FrameworkElement ic;
                if (icon != null)
                {
                    ic = new Image { Source = icon, Width = 40, Height = 40, Stretch = Stretch.Uniform, Clip = new RectangleGeometry(new Rect(0, 0, 40, 40), 9, 9) };
                    RenderOptions.SetBitmapScalingMode(ic, BitmapScalingMode.HighQuality);
                }
                else
                {
                    string first = app.Substring(0, 1).ToUpperInvariant();
                    int h = 0;
                    foreach (char ch in app) h = (h * 31 + ch) & 0x7fffffff;
                    ic = new Border
                    {
                        Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = Solid(Hues[h % Hues.Length]),
                        Child = new TextBlock
                        {
                            Text = first, Foreground = Brushes.White, FontSize = 19, FontWeight = FontWeights.SemiBold,
                            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                        }
                    };
                }
                ic.VerticalAlignment = VerticalAlignment.Top;
                iconHost = ic;
                Grid.SetColumn(ic, 0);
                content.Children.Add(ic);
            }

            var text = new StackPanel { Margin = notification ? new Thickness(12, 0, 0, 0) : new Thickness(0), VerticalAlignment = capsule ? VerticalAlignment.Center : VerticalAlignment.Top };
            textPanel = text;
            Grid.SetColumn(text, notification ? 1 : 0);
            if (notification)
            {
                // the "now" label takes the room its language needs, the app name gets the rest
                var head = new Grid();
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                appText = new TextBlock
                {
                    Text = app, Foreground = white, Opacity = 0.72, FontSize = 11.5, FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0)
                };
                nowText = new TextBlock
                {
                    Text = L.T("banner.now"), Foreground = white, Opacity = 0.72, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetColumn(nowText, 1);
                head.Children.Add(appText);
                head.Children.Add(nowText);
                text.Children.Add(head);
            }
            if (title.Length > 0)
            {
                titleText = new TextBlock
                {
                    Text = title, Foreground = white, FontSize = notification ? 13.5 : capsule ? 14 : 20, FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, notification ? 2 : 0, 0, 0),
                    HorizontalAlignment = capsule ? HorizontalAlignment.Center : HorizontalAlignment.Left
                };
                text.Children.Add(titleText);
            }
            if (body.Length > 0)
            {
                bodyText = new TextBlock
                {
                    Text = body, Foreground = white, Opacity = 0.9, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = 17,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight, MaxHeight = notification ? 34 : 120,
                    Margin = new Thickness(0, notification ? 1 : 6, 0, 0)
                };
                text.Children.Add(bodyText);
            }
            Bidi(appText);
            Bidi(titleText);
            Bidi(bodyText);
            content.Children.Add(text);
            Children.Add(content);
        }

        // Arabic, Hebrew, Persian or Urdu text reads from the right, whatever the language of the interface
        static void Bidi(TextBlock t)
        {
            if (t == null || !StartsRtl(t.Text)) return;
            t.FlowDirection = FlowDirection.RightToLeft;
            if (t.HorizontalAlignment == HorizontalAlignment.Left) t.HorizontalAlignment = HorizontalAlignment.Stretch;
        }

        // the first letter decides, as in the Unicode bidi algorithm
        static bool StartsRtl(string s)
        {
            foreach (char c in s)
            {
                if ((c >= '֐' && c <= 'ࣿ') || (c >= 'יִ' && c <= '﷿') || (c >= 'ﹰ' && c <= '﻿')) return true;
                if (char.IsLetter(c)) return false;
            }
            return false;
        }

        // measure once so the card knows its height (needed to capture the right pixels)
        void SetIconSize(double s)
        {
            iconColumn.Width = new GridLength(s);
            iconHost.Width = s;
            iconHost.Height = s;
            double r = s * 0.23;
            var img = iconHost as Image;
            if (img != null) img.Clip = new RectangleGeometry(new Rect(0, 0, s, s), r, r);
            var box = iconHost as Border;
            if (box != null)
            {
                box.CornerRadius = new CornerRadius(r);
                var letter = box.Child as TextBlock;
                if (letter != null) letter.FontSize = s * 0.46;
            }
        }

        public void LayoutNow()
        {
            if (iconHost != null)
            {
                // the icon is as tall as the three rows of text (app name, title, body)
                SetIconSize(48);
                Height = double.NaN;
                Measure(new Size(Width, double.PositiveInfinity));
                SetIconSize(Math.Max(44, Math.Min(76, textPanel.DesiredSize.Height)));
            }
            Height = double.NaN;
            Measure(new Size(Width, double.PositiveInfinity));
            CardH = Math.Max(MinHeight, DesiredSize.Height);
            Height = CardH;
            Measure(new Size(Width, CardH));
            Arrange(new Rect(0, 0, Width, CardH));
        }

        public double RadiusPx(double scale)
        {
            return capsule ? CardH * scale / 2 : Math.Min(Look.RadiusDip * scale, CardH * scale / 2);
        }

        // Sets up the glass (and shadow) for the current look from the given backdrop snapshot. Anything that comes
        // out the same as before (same backdrop pixels, same constants) is left alone, so WPF has nothing to redraw.
        public void Bake(Cap cap, double scale)
        {
            if (released || engine) return;
            if (gpu) BakeGpu(cap, scale); else BakeCpu(cap, scale);
        }

        // A ShaderEffect subscribes to its PixelShader, and the PixelShader is shared by every card: the shader keeps
        // each effect that was ever set on it, and the effect keeps the element it was applied to. So a closed banner
        // stayed in memory for good, with its buffers and bitmaps (about 5 MB each). Cutting the effect loose when the
        // card is done lets all of it go. A released card is never baked again (that would subscribe it once more).
        bool released;

        public void ReleaseEffects()
        {
            if (released || lens == null) return;
            released = true;
            lensHost.Effect = null;
            lens.Detach();
        }

        static double Clamp01(double v) { return v < 0 ? 0 : v > 1 ? 1 : v; }

        // GPU: upload the (padded) backdrop and set the shader constants; WPF does the lens and the shadows, and on
        // the high levels the blur. The low levels blur on the CPU at half resolution and let the GPU upscale.
        void BakeGpu(Cap cap, double scale)
        {
            var look = Look;
            // the card itself; the snapshot may cover more (a live strip), with the card at (OffX, OffY) in it
            int cw = Math.Max(1, (int)Math.Round(Width * scale)), ch = Math.Max(1, (int)Math.Round(CardH * scale));
            double rPx = RadiusPx(scale);
            bool glass = look.Glass && cap != null;
            int m = glass ? Glass.Margin(look, scale, cw, ch) : 0;
            double backLum = upLum;
            strip = glass ? cap : null;
            if (glass)
            {
                int rw = cap.CW, rh = cap.CH;
                int tw = rw + 2 * m, th = rh + 2 * m, down = look.BackdropDown;
                // a playground card that has not moved over an unchanged scene: the backdrop on the GPU is still right
                bool same = cap.Source != null && cap.Source == upSource && cap.Cx == upX && cap.Cy == upY &&
                    m == upM && down == upDown && rw == upW && rh == upH;
                if (!same)
                {
                    padBuf = Glass.Padded(cap, m, padBuf);
                    backLum = Glass.MeanLum(padBuf, tw, m + cap.OffX, m + cap.OffY, cw, ch);
                    byte[] up = padBuf;
                    int uw = tw, uh = th;
                    if (down > 1)
                    {
                        halfBuf = Glass.Half(padBuf, tw, th, halfBuf, out uw, out uh);
                        Glass.Blur(halfBuf, uw, uh, 4, 3, Glass.Sigma(look, scale) / down);
                        up = halfBuf;
                    }
                    // a screen capture that came out the same (the live refresh of ultra, mostly) is not uploaded again
                    if (wb == null || wb.PixelWidth != uw || wb.PixelHeight != uh || !Glass.Same(up, upBuf))
                    {
                        if (wb == null || wb.PixelWidth != uw || wb.PixelHeight != uh)
                            wb = new WriteableBitmap(uw, uh, 96, 96, PixelFormats.Bgr32, null);
                        wb.WritePixels(new Int32Rect(0, 0, uw, uh), up, uw * 4, 0);
                        // keep what was uploaded, the other buffer becomes the scratch space of the next bake
                        if (up == halfBuf) { halfBuf = upBuf; upBuf = up; } else { padBuf = upBuf; upBuf = up; }
                    }
                    upSource = cap.Source; upX = cap.Cx; upY = cap.Cy; upM = m; upDown = down; upW = rw; upH = rh; upLum = backLum;
                }
                backdropImg.Source = wb;
                backdropImg.Width = wb.PixelWidth * down / scale;
                backdropImg.Height = wb.PixelHeight * down / scale;
                stripX = cap.OffX; stripY = cap.OffY;
                stripShift.X = -stripX / scale;
                stripShift.Y = -stripY / scale;
                double sigmaDip = Glass.Sigma(look, scale) / scale;
                if (down == 1 && sigmaDip > 0.3)
                {
                    if (blurFx == null) blurFx = new BlurEffect { KernelType = KernelType.Gaussian };
                    blurFx.RenderingBias = look.Bias;
                    blurFx.Radius = sigmaDip * 2.5;
                    backdropImg.Effect = blurFx;
                }
                else backdropImg.Effect = null;
            }
            else
            {
                backdropImg.Source = null;
                backdropImg.Effect = null;
                upSource = null; upX = int.MinValue;
            }
            lens.Use(look.Quality);

            double mDip = m / scale;
            lensHost.Width = (cw + 2 * m) / scale;
            lensHost.Height = (ch + 2 * m) / scale;
            Canvas.SetLeft(lensHost, -mDip);
            Canvas.SetTop(lensHost, -mDip);

            double bezel, thick, strength;
            Glass.Lens(look, scale, cw, ch, out bezel, out thick, out strength);
            double k = glass ? look.K : 0;
            double ll = Math.Sqrt(0.55 * 0.55 + 0.83 * 0.83);
            lens.C0 = new System.Windows.Media.Media3D.Point4D(cw + 2 * m, ch + 2 * m, cw, ch);
            lens.C1 = new System.Windows.Media.Media3D.Point4D(m, rPx, bezel, glass ? strength * thick : 0);
            lens.C2 = new System.Windows.Media.Media3D.Point4D(glass ? look.DispersionK : 0, glass ? 1 + 0.65 * k : 1, glass ? 0.025 + 0.045 * k : 0, k);
            lens.C3 = new System.Windows.Media.Media3D.Point4D(look.Tint.R / 255.0, look.Tint.G / 255.0, look.Tint.B / 255.0, look.TintAlpha);
            lens.C4 = new System.Windows.Media.Media3D.Point4D(-0.55 / ll, -0.83 / ll, scale, glass ? 1 : 0);

            // two-layer shadow, lighter over bright backdrops (like Apple's adaptive shadow)
            Shadow = MakeShadow(backLum, rPx / scale);
            if (ExternalShadows) shadowHost.Visibility = Visibility.Collapsed;
            else
            {
                ConfigShadow(shadowA, Shadow.Radius, Shadow.BlurA, Shadow.DepthA, Shadow.OpA, Shadow.Bias);
                ConfigShadow(shadowB, Shadow.Radius, Shadow.BlurB, Shadow.DepthB, Shadow.OpB, Shadow.Bias);
            }

            double lumFinal = look.LumTint;
            if (glass)
            {
                double ta = look.TintAlpha;
                double lift = 0.025 + 0.045 * k;
                double tintLum = Clamp01(look.LumTint * (0.78 + 0.45 * backLum));
                lumFinal = (backLum * (1 - lift) + lift) * (1 - ta) + tintLum * ta;
                lumFinal += 0.03 + 0.04 * k;   // the highlights brighten the card a little on average
            }
            ApplyText(lumFinal > 0.55);
        }

        ShadowSpec MakeShadow(double backLum, double radiusDip)
        {
            double adapt = Math.Max(0.45, Math.Min(1.2, 1.2 - 0.6 * backLum));
            var s = new ShadowSpec
            {
                Radius = radiusDip, Bias = Look.Bias,
                BlurA = Look.ShadowBlurDip, DepthA = Look.ShadowDepthDip, OpA = Look.AmbientOpacity * adapt,
                BlurB = Math.Max(3, Look.ShadowBlurDip * 0.12), DepthB = Look.ShadowDepthDip * 0.3, OpB = Look.ContactOpacity * adapt
            };
            // low: only the wide layer, as dark as the two together
            if (!Look.TwoShadows) { s.OpA = 1 - (1 - s.OpA) * (1 - s.OpB); s.OpB = 0; }
            return s;
        }

        public static void ConfigShadow(Border b, double radiusDip, double blur, double depth, double opacity, RenderingBias bias)
        {
            if (opacity < ShadowSpec.MinOpacity) { b.Visibility = Visibility.Collapsed; return; }
            b.Visibility = Visibility.Visible;
            b.CornerRadius = new CornerRadius(radiusDip);
            b.Margin = new Thickness(1);
            b.Background = Brushes.Black;
            var fx = b.Effect as System.Windows.Media.Effects.DropShadowEffect;
            if (fx == null)
            {
                fx = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, Direction = 270 };
                b.Effect = fx;
            }
            fx.RenderingBias = bias;
            fx.BlurRadius = blur;
            fx.ShadowDepth = depth;
            fx.Opacity = Math.Min(1, opacity);
        }

        // CPU fallback: bake glass and shadow into one bitmap
        void BakeCpu(Cap cap, double scale)
        {
            int cw = Math.Max(1, (int)Math.Round(Width * scale)), ch = Math.Max(1, (int)Math.Round(CardH * scale));
            strip = Look.Glass ? cap : null;
            if (cap != null) { stripX = cap.OffX; stripY = cap.OffY; cap = cap.View(cap.OffX, cap.OffY, cw, ch); }
            RenderCpu(cap, cw, ch, scale);
        }

        // Live backdrop: the card has moved (dx, dy) px since its strip was baked, the lens looks at that part of it.
        // On the GPU that is only an offset; the CPU renderer has to draw the card again, so it follows at 10 fps.
        public void MoveBackdrop(double dx, double dy, double scale)
        {
            if (strip == null) return;
            if (gpu)
            {
                stripShift.X = -(stripX + dx) / scale;
                stripShift.Y = -(stripY + dy) / scale;
                return;
            }
            if (Environment.TickCount - cpuMoved < 100) return;
            cpuMoved = Environment.TickCount;
            int cw = Math.Max(1, (int)Math.Round(Width * scale)), ch = Math.Max(1, (int)Math.Round(CardH * scale));
            RenderCpu(strip.View(stripX + (int)Math.Round(dx), stripY + (int)Math.Round(dy), cw, ch), cw, ch, scale);
        }

        void RenderCpu(Cap cap, int cw, int ch, double scale)
        {
            byte[] padded = null;
            if (Look.Glass && cap != null)
            {
                padBuf = Glass.Padded(cap, Glass.Margin(Look, scale, cw, ch), padBuf);
                // a live refresh: same pixels behind, same look, nothing to render
                if (Look == cpuLook && scale == cpuScale && Glass.Same(padBuf, upBuf)) return;
                padded = padBuf; padBuf = upBuf; upBuf = padded;
            }
            cpuLook = Look; cpuScale = scale;
            int pad;
            double lum, backAvg;
            var bmp = Glass.Render(padded, cw, ch, scale, RadiusPx(scale), Look, !ExternalShadows, out pad, out lum, out backAvg);
            Shadow = MakeShadow(backAvg, RadiusPx(scale) / scale);
            glassImage.Source = bmp;
            // debug aid: with a dump.flag file next to the log, every baked card is also saved as dump.png
            try
            {
                string dir = Program.DataDir;
                if (File.Exists(System.IO.Path.Combine(dir, "dump.flag")))
                {
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (var fs = File.Create(System.IO.Path.Combine(dir, "dump.png"))) enc.Save(fs);
                }
            }
            catch { }
            double padDip = pad / scale;
            glassImage.Margin = new Thickness(-padDip);
            glassImage.Width = Width + 2 * padDip;
            glassImage.Height = CardH + 2 * padDip;
            ApplyText(lum > 0.55);
        }

        static readonly Brush DarkText = Solid(Color.FromRgb(0x1D, 0x1D, 0x1F)), LightText = Solid(Color.FromRgb(0xFF, 0xFF, 0xFF));

        // the text and the icon as premultiplied BGRA pixels, with light or dark text (the engine picks one by what is
        // behind the card); the card must be laid out (LayoutNow) and not yet inside a window
        public byte[] RenderContent(double scale, bool dark, out int w, out int h)
        {
            ApplyText(dark);
            w = Math.Max(1, (int)Math.Round(Width * scale));
            h = Math.Max(1, (int)Math.Round(CardH * scale));
            UpdateLayout();
            var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(this);
            var px = new byte[w * h * 4];
            rtb.CopyPixels(px, w * 4, 0);
            return px;
        }

        // a card that only takes the mouse: nothing of it is drawn, but its area stays solid enough (1/255) for the
        // layered window to send it clicks
        public void Hollow()
        {
            if (contentHost != null) contentHost.Visibility = Visibility.Hidden;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        }

        // text follows what the glass ended up looking like, like Apple's adaptive light/dark glass
        void ApplyText(bool dark)
        {
            Brush c = dark ? DarkText : LightText;
            if (appText != null) appText.Foreground = c;
            if (nowText != null) nowText.Foreground = c;
            if (titleText != null) titleText.Foreground = c;
            if (bodyText != null) bodyText.Foreground = c;
        }
    }

    // ---- the banner window ------------------------------------------------------------

    sealed class Banner : Window
    {
        public const double CardW = 360, EdgeR = 14;
        // Room around the card inside the window, for the close button over its corner. The card draws nothing outside
        // itself (its shadow has a window of its own), and a layered window is read back from the GPU in full on
        // every frame, transparent pixels too, so no more than that.
        public const double Pad = 10;
        // GLASSBELL_CAPTURABLE=1 lets screenshots see the banners (a test setting; the glass still does not, see Snap)
        public static readonly bool Capturable = Environment.GetEnvironmentVariable("GLASSBELL_CAPTURABLE") == "1";

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

        public readonly GlassCard Card;
        readonly TranslateTransform slide = new TranslateTransform();
        readonly Grid unit = new Grid();
        readonly Border closeButton = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Opacity = 0, IsHitTestVisible = false, Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Pad - 7, Pad - 7, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x3A, 0x3A, 0x3C)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.8)
        };
        public event Action<Banner, bool> Hover;   // the pointer came onto the banner (true) or left it

        void ShowClose(bool on)
        {
            closeButton.IsHitTestVisible = on && !closing;
            closeButton.BeginAnimation(OpacityProperty, new DoubleAnimation(on && !closing ? 1 : 0, TimeSpan.FromMilliseconds(on ? 120 : 180)));
        }
        readonly DispatcherTimer timer = new DispatcherTimer();
        bool closing, sliding;

        public string AppId;
        public ToastOrigin.Target Toast;   // the mirrored toast behind this banner (none for banners from the pipe)
        public ShadowWindow Shadow;
        public double BakedSlotY = double.NaN;
        public bool Shifting;   // moving to a new place in the stack
        public double TargetTop = double.NaN;   // where a shift is taking the window
        // live backdrop: the screen area (DIP) its glass was last captured from, and where the card was then
        public Rect StripRect = Rect.Empty;
        public Point LiveOrigin;
        public event Action<Banner> Dismissing;
        public event Action<Banner> Activated2;
        public event Action<Banner> Moving;     // a slide in or out has just started

        public double SlideX { get { return slide.X; } }
        public double SlideTo { get; private set; }
        public const double SlideOff = CardW + EdgeR + 20;   // how far right of its place a card starts and leaves

        public Look Look { get { return Card.Look; } }
        public bool IsClosing { get { return closing; } }
        public bool Settled { get { return !closing && !sliding && !Shifting; } }
        public double CardHeight { get { return Card.CardH; } }

        // drawn by the GPU engine (GpuEngine): this window is hollow and only takes the mouse, the engine draws the card
        public readonly int GpuId;
        public bool OnGpu { get { return GpuId > 0; } }
        public Func<Banner> Redraw;   // the same banner again, drawn by WPF (should the engine stop)

        public Banner(GlassCard card, string appId, double seconds, int gpuId = 0)
        {
            Card = card;
            AppId = appId;
            GpuId = gpuId;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            Width = CardW + Pad + EdgeR;
            UseLayoutRounding = true;

            card.Margin = new Thickness(Pad, Pad, EdgeR, Pad);
            // the slide and the fade move one cached bitmap of the card: without the cache every animation frame
            // ran the blur and the lens again (about a quarter of what a banner cost). A live glass changes on every
            // frame of a move anyway, so there the cache would only be one more copy.
            if (OnGpu) card.Hollow();
            else if (!card.Look.LiveBackdrop) card.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
            unit.Children.Add(card);
            unit.Children.Add(closeButton);
            unit.RenderTransform = slide;
            unit.Opacity = 0;
            var root = new Grid();
            root.Children.Add(unit);
            Content = root;

            // the close button over the card's top left corner, like the one of macOS, shown while the pointer is on the
            // banner; it closes the banner without opening what it points to (so does a right click)
            closeButton.Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 0,0 L 7,7 M 7,0 L 0,7"), Stroke = Brushes.White, StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            closeButton.ToolTip = L.T("banner.close");
            System.Windows.Automation.AutomationProperties.SetName(closeButton, L.T("banner.close"));
            closeButton.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; Dismiss(); };

            timer.Interval = TimeSpan.FromSeconds(seconds);
            timer.Tick += delegate { Dismiss(); };
            // the pointer on the card or on its close button keeps the banner
            unit.MouseEnter += delegate
            {
                timer.Stop();
                ShowClose(true);
                if (Hover != null) Hover(this, true);
            };
            unit.MouseLeave += delegate
            {
                ShowClose(false);
                if (Hover != null) Hover(this, false);
                if (closing) return;
                timer.Interval = TimeSpan.FromSeconds(2);
                timer.Start();
            };
            card.MouseLeftButtonUp += delegate
            {
                if (closing) return;
                if (Activated2 != null) Activated2(this);
                Dismiss();
            };
            card.MouseRightButtonUp += delegate { Dismiss(); };
        }

        // no taskbar button, no Alt+Tab entry, never takes the focus; clickThrough also lets every click pass
        public static IntPtr Passive(Window w, bool clickThrough)
        {
            IntPtr h = new WindowInteropHelper(w).Handle;
            const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | (clickThrough ? WS_EX_TRANSPARENT : 0));
            return h;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Card.ReleaseEffects();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr h = Passive(this, false);
            // keep banners out of screen captures so the glass of the next banner never samples them
            // (set GLASSBELL_CAPTURABLE=1 to disable, e.g. to take screenshots of the banners)
            if (Look.Glass && !Capturable)
                SetWindowDisplayAffinity(h, 0x11);
        }

        public void Present()
        {
            double off = SlideOff;
            if (Shadow != null) Shadow.Present(Look);
            var anim = Look.Anim(off, 0, 340, EasingMode.EaseOut);
            sliding = true;
            anim.Completed += delegate { sliding = false; };
            // Moving is raised before the animation takes over, while SlideX still tells where the card really is
            slide.X = off;
            SlideTo = 0;
            if (Moving != null) Moving(this);
            slide.BeginAnimation(TranslateTransform.XProperty, anim);
            unit.BeginAnimation(OpacityProperty, Look.Anim(0, 1, 200, null));
            if (OnGpu)
            {
                GpuEngine.Move(GpuId, 'x', off, 0, 340, GpuEngine.EaseOut, 0);
                GpuEngine.Move(GpuId, 'o', 0, 1, 200, GpuEngine.Linear, 0);
            }
            timer.Start();
        }

        // gone at once, without its slide out
        public void Vanish()
        {
            if (closing) return;
            closing = true;
            timer.Stop();
            if (Shadow != null) Shadow.Close();
            if (Dismissing != null) Dismissing(this);
            Close();
        }

        public void Dismiss()
        {
            if (closing) return;
            closing = true;
            timer.Stop();
            ShowClose(false);
            if (Shadow != null) Shadow.Dismiss(Look);
            if (Dismissing != null) Dismissing(this);
            var move = Look.Anim(0, SlideOff, 240, EasingMode.EaseIn);
            move.Completed += delegate { Close(); };
            SlideTo = SlideOff;
            if (Moving != null) Moving(this);
            slide.BeginAnimation(TranslateTransform.XProperty, move);
            var fade = Look.Anim(1, 0, 200, null);
            fade.BeginTime = TimeSpan.FromMilliseconds(40);
            unit.BeginAnimation(OpacityProperty, fade);
            if (OnGpu)
            {
                GpuEngine.Move(GpuId, 'x', double.NaN, SlideOff, 240, GpuEngine.EaseIn, 0);
                GpuEngine.Move(GpuId, 'o', double.NaN, 0, 200, GpuEngine.Linear, 40);
                GpuEngine.Remove(GpuId);
            }
        }
    }

    // The shadow of a banner, in a window of its own that never takes the mouse (WS_EX_TRANSPARENT): a layered
    // window gets every click on its non-transparent pixels, and a soft shadow has such pixels a long way out.
    // It shares the right edge of the banner window, reaches exactly as far as the shadow does (no further: every
    // pixel of a layered window costs on every frame) and animates in step with the banner.
    sealed class ShadowWindow : Window
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

        readonly TranslateTransform slide = new TranslateTransform();
        readonly Grid unit = new Grid();
        readonly Grid holder = new Grid { CacheMode = new BitmapCache() };   // the slide moves a bitmap, not two blurs
        readonly Border a = new Border { IsHitTestVisible = false }, b = new Border { IsHitTestVisible = false };
        bool closing;

        public readonly double PadTop;   // the window starts this far above the card

        public ShadowWindow(ShadowSpec s, double cardHeight)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            PadTop = s.Spread;
            Width = Banner.CardW + s.Spread + Banner.EdgeR;
            IsHitTestVisible = false;

            holder.Margin = new Thickness(s.Spread, s.Spread, Banner.EdgeR, s.Below);
            holder.Width = Banner.CardW;
            holder.Height = cardHeight;
            holder.HorizontalAlignment = HorizontalAlignment.Left;
            holder.Children.Add(a);
            holder.Children.Add(b);
            GlassCard.ConfigShadow(a, s.Radius, s.BlurA, s.DepthA, s.OpA, s.Bias);
            GlassCard.ConfigShadow(b, s.Radius, s.BlurB, s.DepthB, s.OpB, s.Bias);
            unit.Children.Add(holder);
            unit.RenderTransform = slide;
            unit.Opacity = 0;
            var root = new Grid();
            root.Children.Add(unit);
            Content = root;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr h = Banner.Passive(this, true);
            if (!Banner.Capturable)
                SetWindowDisplayAffinity(h, 0x11);
        }

        public void Present(Look look)
        {
            double off = Banner.CardW + Banner.EdgeR + 20;
            slide.BeginAnimation(TranslateTransform.XProperty, look.Anim(off, 0, 340, EasingMode.EaseOut));
            unit.BeginAnimation(OpacityProperty, look.Anim(0, 1, 200, null));
        }

        public void Dismiss(Look look)
        {
            if (closing) return;
            closing = true;
            var move = look.Anim(0, Banner.CardW + Banner.EdgeR + 20, 240, EasingMode.EaseIn);
            move.Completed += delegate { Close(); };
            slide.BeginAnimation(TranslateTransform.XProperty, move);
            var fade = look.Anim(1, 0, 200, null);
            fade.BeginTime = TimeSpan.FromMilliseconds(40);
            unit.BeginAnimation(OpacityProperty, fade);
        }
    }

    // "Close all": a small capsule under the stack, there while the pointer is on one of two or more banners (or on
    // the capsule itself). Its own window, so that it never takes room in a banner.
    sealed class ClosePill : Window
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);

        readonly Border pill;
        bool shown;
        public event Action Clicked;
        public event Action<bool> Hover;

        public ClosePill()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(L.Ietf);
            if (L.Rtl) FlowDirection = FlowDirection.RightToLeft;
            pill = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 5, 14, 6), Margin = new Thickness(4), Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x2C, 0x2C, 0x2E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0.8),
                Child = new TextBlock { Text = L.T("banner.closeAll"), Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold }
            };
            System.Windows.Automation.AutomationProperties.SetName(pill, L.T("banner.closeAll"));
            pill.MouseEnter += delegate { pill.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x48, 0x48, 0x4A)); if (Hover != null) Hover(true); };
            pill.MouseLeave += delegate { pill.Background = new SolidColorBrush(Color.FromArgb(0xEB, 0x2C, 0x2C, 0x2E)); if (Hover != null) Hover(false); };
            pill.MouseLeftButtonUp += delegate { if (Clicked != null) Clicked(); };
            Content = pill;
            Opacity = 0;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr h = Banner.Passive(this, false);
            if (!Banner.Capturable) SetWindowDisplayAffinity(h, 0x11);   // the glass of the banners must not see it
        }

        public bool Shown { get { return shown; } }

        // centred under the card column, its top at y (DIP)
        public void Place(double cardX, double y)
        {
            UpdateLayout();
            Left = cardX + (Banner.CardW - ActualWidth) / 2;
            Top = y;
        }

        public void Appear()
        {
            if (shown) return;
            shown = true;
            IsHitTestVisible = true;
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        }

        public void Fade()
        {
            if (!shown) return;
            shown = false;
            IsHitTestVisible = false;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
        }
    }

    // ---- where a click on a mirrored banner goes ---------------------------------------

    // A click on a banner does what a click on the original toast would have done: Chrome opens the page that sent
    // it, a protocol toast opens its link, and so on. The listener only hands out the app and the texts, but the
    // notification platform keeps every toast's own XML (with its launch arguments) in its database, under the same
    // Id. So the activation is replayed, in this order:
    //   1. activationType="protocol": the launch URI is opened, as the shell would.
    //   2. the app has a COM toast activator (Chrome, Edge, Telegram, Discord, Spotify, Electron apps ...): it is
    //      called with the toast's launch arguments, exactly what Windows does on a click (notification_helper.exe
    //      for Chrome, which then focuses the tab of the site).
    //   3. anything else that carries arguments (packaged apps, tray balloons): the notification center is opened
    //      (Win+N) and the toast is invoked there through UI Automation, found by its texts. The panel shows for
    //      about half a second.
    // Nothing found or nothing worked: the caller falls back to starting the app.
    static class ToastOrigin
    {
        public sealed class Target
        {
            public uint Id;
            public string Aumid, Title, Body;
            public DateTimeOffset Created;
            public int Newer;   // identical toasts of the same app that came later (they sit above it in the center)
        }

        // how the toast was activated ("protocol", "activator", "center"), or null
        public static string Activate(Target t)
        {
            string type, launch, aumid;
            if (!Payload(t.Id, out type, out launch, out aumid)) return null;   // no longer in the notification center
            if (string.IsNullOrEmpty(t.Aumid)) t.Aumid = aumid;                 // the listener did not know the app
            if (type == "protocol") return Open(launch) ? "protocol" : null;
            Guid clsid;
            if (FindActivator(t.Aumid, out clsid) && CallActivator(clsid, t.Aumid, launch)) return "activator";
            // without arguments a click only brings the app up, which starting it does as well, without the panel
            if (launch.Length > 0 || t.Aumid.StartsWith("NotifyIconGeneratedAumid_", StringComparison.Ordinal))
                return Center(t) ? "center" : null;
            return null;
        }

        // ---- the toast's own XML, from the platform's database (read-only) ----

        [DllImport("winsqlite3.dll")] static extern int sqlite3_open_v2(byte[] file, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_busy_timeout(IntPtr db, int ms);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int n, out IntPtr st, IntPtr tail);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_bind_int64(IntPtr st, int i, long v);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_step(IntPtr st);
        [DllImport("winsqlite3.dll")] static extern IntPtr sqlite3_column_blob(IntPtr st, int i);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_column_bytes(IntPtr st, int i);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_finalize(IntPtr st);
        [DllImport("winsqlite3.dll")] static extern int sqlite3_close(IntPtr db);

        static byte[] Z(string s) { return Encoding.UTF8.GetBytes(s + "\0"); }

        public static bool Payload(uint id, out string type, out string launch, out string aumid)
        {
            type = "foreground"; launch = ""; aumid = "";
            string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Windows\Notifications\wpndatabase.db");
            IntPtr db = IntPtr.Zero, st = IntPtr.Zero;
            byte[] xml = null;
            try
            {
                // SQLITE_OPEN_READONLY | SQLITE_OPEN_URI: a reader like any other, it never writes or locks the writer out
                if (sqlite3_open_v2(Z("file:" + path.Replace('\\', '/') + "?mode=ro"), out db, 0x01 | 0x40, IntPtr.Zero) != 0) return false;
                sqlite3_busy_timeout(db, 300);
                if (sqlite3_prepare_v2(db, Z("SELECT n.Payload, h.PrimaryId FROM Notification n LEFT JOIN NotificationHandler h " +
                    "ON h.RecordId = n.HandlerId WHERE n.Id = ?1 AND n.Type = 'toast'"), -1, out st, IntPtr.Zero) != 0) return false;
                sqlite3_bind_int64(st, 1, id);
                if (sqlite3_step(st) != 100) return false;   // SQLITE_ROW
                int n = sqlite3_column_bytes(st, 0);
                xml = new byte[n];
                if (n > 0) Marshal.Copy(sqlite3_column_blob(st, 0), xml, 0, n);
                int an = sqlite3_column_bytes(st, 1);
                var ab = new byte[an];
                if (an > 0) Marshal.Copy(sqlite3_column_blob(st, 1), ab, 0, an);
                aumid = Encoding.UTF8.GetString(ab);
            }
            catch (Exception e) { Log.Write("toast payload: " + e.Message); return false; }
            finally
            {
                if (st != IntPtr.Zero) sqlite3_finalize(st);
                if (db != IntPtr.Zero) sqlite3_close(db);
            }
            try
            {
                using (var r = System.Xml.XmlReader.Create(new MemoryStream(xml), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit }))
                {
                    r.MoveToContent();
                    type = r.GetAttribute("activationType") ?? "foreground";
                    launch = r.GetAttribute("launch") ?? "";
                }
                return true;
            }
            catch (Exception e) { Log.Write("toast payload xml: " + e.Message); return false; }
        }

        public static string AppOf(uint id)
        {
            string type, launch, aumid;
            return Payload(id, out type, out launch, out aumid) ? aumid : "";
        }

        // the display name an app registered next to its AppUserModelID, or ""
        public static string NameOf(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return "";
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var k = hive.OpenSubKey(@"Software\Classes\AppUserModelId\" + aumid))
                    {
                        string v = k != null ? k.GetValue("DisplayName") as string : null;
                        if (!string.IsNullOrEmpty(v) && !v.StartsWith("@")) return v;
                    }
                }
                catch { }
            }
            return "";
        }

        // a link, never a file: the shell opens it with the app registered for its scheme
        static bool Open(string uri)
        {
            Uri u;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out u) || u.IsFile || u.IsUnc) return false;
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                return true;
            }
            catch (Exception e) { Log.Write("toast link: " + e.Message); return false; }
        }

        // ---- the app's COM toast activator ----

        [ComImport, Guid("53E31837-6600-4A81-9395-75CFFE746F94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface INotificationActivationCallback
        {
            [PreserveSig] int Activate([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                [MarshalAs(UnmanagedType.LPWStr)] string invokedArgs, IntPtr data, uint count);
        }

        [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem2
        {
            void BindToHandler(); void GetParent(); void GetDisplayName(); void GetAttributes(); void Compare();
            void GetPropertyStore(); void GetPropertyStoreWithCreateObject(); void GetPropertyStoreForKeys();
            void GetPropertyDescriptionList(); void Update(); void GetProperty();
            [PreserveSig] int GetCLSID(ref PropKey key, out Guid clsid);
            void GetFileTime(); void GetInt32();
            [PreserveSig] int GetString(ref PropKey key, [MarshalAs(UnmanagedType.LPWStr)] out string s);
        }

        [StructLayout(LayoutKind.Sequential)] struct PropKey { public Guid Fmt; public uint Pid; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem2 item);
        [DllImport("ole32.dll")]
        static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        [DllImport("ole32.dll")]
        static extern int CoAllowSetForegroundWindow([MarshalAs(UnmanagedType.IUnknown)] object o, IntPtr reserved);

        static readonly object gate = new object();
        static Dictionary<string, Guid> shortcuts;   // AppUserModelID -> ToastActivatorCLSID, from the Start menu
        static DateTime scanned;

        // An app declares its activator either in the registry (AppUserModelId\<id>\CustomActivator) or on its Start
        // menu shortcut (System.AppUserModel.ToastActivatorCLSID), which is where Chrome and Edge put it.
        public static bool FindActivator(string aumid, out Guid clsid)
        {
            clsid = Guid.Empty;
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var k = hive.OpenSubKey(@"Software\Classes\AppUserModelId\" + aumid))
                    {
                        string v = k != null ? k.GetValue("CustomActivator") as string : null;
                        if (v != null && Guid.TryParse(v, out clsid)) return true;
                    }
                }
                catch { }
            }
            lock (gate)
            {
                if (shortcuts == null || (!shortcuts.ContainsKey(aumid) && (DateTime.UtcNow - scanned).TotalSeconds > 60)) ScanShortcuts();
                return shortcuts.TryGetValue(aumid, out clsid);
            }
        }

        // reading the shortcuts takes a moment, so it is done ahead, when the first toast is mirrored
        public static void Prepare() { lock (gate) if (shortcuts == null) ScanShortcuts(); }

        static void ScanShortcuts()
        {
            var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var id = new PropKey { Fmt = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Pid = 5 };
            var act = new PropKey { Fmt = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Pid = 26 };
            Guid iid = new Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93");
            foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
            {
                string[] links;
                try { links = Directory.GetFiles(Environment.GetFolderPath(folder), "*.lnk", SearchOption.AllDirectories); }
                catch { continue; }
                foreach (string lnk in links)
                {
                    try
                    {
                        IShellItem2 item;
                        if (SHCreateItemFromParsingName(lnk, IntPtr.Zero, ref iid, out item) != 0) continue;
                        string aumid;
                        Guid clsid;
                        if (item.GetString(ref id, out aumid) == 0 && !string.IsNullOrEmpty(aumid) &&
                            item.GetCLSID(ref act, out clsid) == 0 && clsid != Guid.Empty)
                            map[aumid] = clsid;
                        Marshal.ReleaseComObject(item);
                    }
                    catch { }
                }
            }
            shortcuts = map;
            scanned = DateTime.UtcNow;
        }

        static bool CallActivator(Guid clsid, string aumid, string launch)
        {
            object o = null;
            IntPtr none = Marshal.AllocCoTaskMem(16);   // the array of user inputs: empty, but it must not be null
            try
            {
                Guid unknown = new Guid("00000000-0000-0000-C000-000000000046");
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 0x4 /* CLSCTX_LOCAL_SERVER */, ref unknown, out o);
                if (hr < 0 || o == null) { Log.Write("toast activator " + clsid + ": 0x" + hr.ToString("X8")); return false; }
                CoAllowSetForegroundWindow(o, IntPtr.Zero);   // the app may bring its window to the front, as after a real click
                Marshal.WriteInt64(none, 0); Marshal.WriteInt64(none, 8, 0);
                hr = ((INotificationActivationCallback)o).Activate(aumid, launch, none, 0);
                if (hr < 0) Log.Write("toast activator " + clsid + " Activate: 0x" + hr.ToString("X8"));
                return hr >= 0;
            }
            catch (Exception e) { Log.Write("toast activator " + clsid + ": " + e.Message); return false; }
            finally
            {
                if (o != null) Marshal.ReleaseComObject(o);
                Marshal.FreeCoTaskMem(none);
            }
        }

        // ---- the notification center ----

        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        static void Keys(params byte[] vks)
        {
            foreach (byte k in vks) keybd_event(k, 0, 0, UIntPtr.Zero);
            for (int i = vks.Length - 1; i >= 0; i--) keybd_event(vks[i], 0, 2 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
        }

        // the panel is a XAML CoreWindow of ShellExperienceHost; it does not show up under the UIA root, only by handle
        static IntPtr PanelInFront()
        {
            IntPtr h = GetForegroundWindow();
            var cls = new StringBuilder(64);
            GetClassName(h, cls, 64);
            if (cls.ToString() != "Windows.UI.Core.CoreWindow") return IntPtr.Zero;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            try { using (var p = Process.GetProcessById((int)pid)) return p.ProcessName == "ShellExperienceHost" ? h : IntPtr.Zero; }
            catch { return IntPtr.Zero; }
        }

        static bool Center(Target t)
        {
            // never mixed into the user's own keys: with Shift, Ctrl, Alt or Win held, Win+N would mean something else
            foreach (int vk in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
                if ((GetAsyncKeyState(vk) & 0x8000) != 0) return false;
            Keys(0x5B, 0x4E);   // Win+N
            IntPtr panel = IntPtr.Zero;
            for (int i = 0; i < 60 && panel == IntPtr.Zero; i++) { Thread.Sleep(25); panel = PanelInFront(); }
            if (panel == IntPtr.Zero) return false;
            bool done = false;
            try
            {
                var item = FindItem(System.Windows.Automation.AutomationElement.FromHandle(panel), t);
                object p;
                if (item != null && item.TryGetCurrentPattern(System.Windows.Automation.InvokePattern.Pattern, out p))
                {
                    ((System.Windows.Automation.InvokePattern)p).Invoke();
                    done = true;
                }
            }
            catch (Exception e) { Log.Write("notification center: " + e.Message); }
            // invoking closes the panel by itself; whatever is left open (not found, or slow) is closed like the user would
            Thread.Sleep(done ? 400 : 0);
            if (PanelInFront() == panel) Keys(0x1B);
            return done;
        }

        // The toasts are ListViewItems whose name is their texts joined with ". " plus a localised "received at" part,
        // so they are matched by the toast's own texts only, never by labels. The list is virtualised: an item that
        // is not realised has no name until it is (20 and more toasts are not all realised), hence the walk through
        // ItemContainerPattern. Older toasts of one app can be folded into a subgroup: if nothing matched, everything
        // collapsed is expanded and the walk repeated. Identical toasts: the newest is on top, so the one wanted is
        // the (Newer + 1)-th match.
        static System.Windows.Automation.AutomationElement FindItem(System.Windows.Automation.AutomationElement panel, Target t)
        {
            var list = panel.FindFirst(System.Windows.Automation.TreeScope.Descendants, new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.List));
            object pattern;
            if (list == null || !list.TryGetCurrentPattern(System.Windows.Automation.ItemContainerPattern.Pattern, out pattern)) return null;
            var items = (System.Windows.Automation.ItemContainerPattern)pattern;
            for (int round = 0; round < 2; round++)
            {
                var found = new List<System.Windows.Automation.AutomationElement>();
                var folded = new List<System.Windows.Automation.ExpandCollapsePattern>();
                System.Windows.Automation.AutomationElement it = null;
                for (int guard = 0; guard < 300; guard++)
                {
                    it = items.FindItemByProperty(it, null, null);
                    if (it == null) break;
                    object p;
                    string name = it.Current.Name;
                    if (string.IsNullOrEmpty(name) && it.TryGetCurrentPattern(System.Windows.Automation.VirtualizedItemPattern.Pattern, out p))
                    {
                        ((System.Windows.Automation.VirtualizedItemPattern)p).Realize();
                        name = it.Current.Name;
                    }
                    if (Matches(name, t)) found.Add(it);
                    else if (it.TryGetCurrentPattern(System.Windows.Automation.ExpandCollapsePattern.Pattern, out p) &&
                        ((System.Windows.Automation.ExpandCollapsePattern)p).Current.ExpandCollapseState == System.Windows.Automation.ExpandCollapseState.Collapsed)
                        folded.Add((System.Windows.Automation.ExpandCollapsePattern)p);
                }
                if (found.Count > 0) return found[Math.Min(t.Newer, found.Count - 1)];
                if (folded.Count == 0) break;
                foreach (var f in folded) { try { f.Expand(); } catch { } }
                Thread.Sleep(150);
            }
            return null;
        }

        static bool Matches(string name, Target t)
        {
            if (string.IsNullOrEmpty(name)) return false;
            bool any = false;
            foreach (string part in (t.Title + "\n" + t.Body).Split('\n'))
            {
                string s = part.Trim();
                if (s.Length == 0) continue;
                if (name.IndexOf(s.Length > 60 ? s.Substring(0, 60) : s, StringComparison.Ordinal) < 0) return false;
                any = true;
            }
            return any;
        }
    }

    // ---- manager ----------------------------------------------------------------------

    sealed class Manager
    {
        readonly Application app;
        readonly Dispatcher ui;
        readonly List<Banner> active = new List<Banner>();
        readonly Dictionary<string, ImageSource> logos = new Dictionary<string, ImageSource>();
        readonly HashSet<uint> seen = new HashSet<uint>();
        readonly Dictionary<uint, ToastOrigin.Target> mirrored = new Dictionary<uint, ToastOrigin.Target>();
        UserNotificationListener listener;
        DispatcherTimer poll, recheck;
        bool busy, again, primed, shownAny;
        volatile bool watching;   // the database watcher is running, so the slow poll is only a safety net
        System.Windows.Forms.NotifyIcon tray;
        SettingsUi workshop;
        Banner previewBanner;
        Window keeper;

        public Manager(Application app)
        {
            this.app = app;
            ui = app.Dispatcher;
            // the engine stopped while it drew banners: their windows are hollow, so WPF draws them again
            GpuEngine.Stopped += delegate
            {
                ui.BeginInvoke(new Action(RedrawWithoutGpu));
            };
        }

        public void Start()
        {
            Settings.Load();
            Gpu.Init(Settings.Quality);
            new Thread(PipeLoop) { IsBackground = true }.Start();
            ui.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(Warm));
            // each chime is synthesised on its first use: do all three now, in the background, so the first one is on time
            if (Settings.Sound)
                new Thread(delegate() { for (int s = 0; s < Chime.Names.Length; s++) Chime.BuildWav(s, 0); })
                    { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();
            if (Program.Instance.Length > 0) return;   // test instance: banners from the pipe only
            StartTray();
            StartListener();
        }

        // The first banner after start used to cost ~200 ms more than the next ones (JIT, the D3D device, the shader,
        // fonts). One banner is drawn and animated off-screen at startup instead, unless a real one came first.
        // And WPF drops its Direct3D device whenever its last window closes, so every banner that came alone paid
        // ~40 ms to build it again: a 1x1 transparent window off-screen keeps it, and costs nothing while it is idle.
        void Warm()
        {
            if (keeper == null)
            {
                keeper = new Window
                {
                    WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
                    ShowActivated = false, ResizeMode = ResizeMode.NoResize, Width = 1, Height = 1, Left = SystemParameters.VirtualScreenLeft - 100, Top = 0
                };
                keeper.SourceInitialized += delegate { Banner.Passive(keeper, true); };
                keeper.Show();
            }
            if (shownAny) return;
            try { Show("GlassBell", "Warm-up", "Drawn off-screen once at startup.", "", 0.4, null, true); }
            catch (Exception e) { Log.Write("warm-up: " + e.Message); }
            // the GPU engine sets itself up meanwhile (device, shaders, window), so the first banner does not wait for it
            if (Settings.Engine == "gpu") StartGpu(false);
        }

        public void Stop()
        {
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
        }

        // ---- input from the client ------------------------------------------------

        void PipeLoop()
        {
            while (true)
            {
                try
                {
                    var s = new NamedPipeServerStream(Program.PipeName, PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances);
                    s.WaitForConnection();
                    ThreadPool.QueueUserWorkItem(delegate { Handle(s); });
                }
                catch { Thread.Sleep(200); }
            }
        }

        void Handle(NamedPipeServerStream s)
        {
            string msg;
            using (s)
            using (var r = new StreamReader(s, Encoding.UTF8)) msg = r.ReadToEnd();
            if (msg == "\u0001quit") { ui.BeginInvoke(new Action(delegate { app.Shutdown(); })); return; }
            if (msg == "\u0001settings") { ui.BeginInvoke(new Action(OpenSettings)); return; }
            if (msg == "\u0001ping") return;
            if (msg == "\u0001closeall") { ui.BeginInvoke(new Action(DismissAll)); return; }
            if (msg == "\u0001update") { ui.BeginInvoke(new Action(ApplyUpdate)); return; }
            if (msg == "\u0001stats") { ui.BeginInvoke(new Action(LogStats)); return; }
            string[] f = msg.Split(Program.Sep);
            Func<int, string> at = delegate(int i) { return i < f.Length ? f[i] : ""; };
            double secs;
            if (!double.TryParse(at(4), NumberStyles.Float, CultureInfo.InvariantCulture, out secs) || secs <= 0) secs = 5.5;
            string a = at(0).Length > 0 ? at(0) : L.T("banner.app");
            string id = at(3);
            ui.BeginInvoke(new Action(delegate { Show(a, at(1), at(2), id, secs, id.Length > 0 ? IconFor(id) : null); }));
        }

        // ---- mirroring of every Windows toast -------------------------------------

        // await on a WinRT operation without the System.Runtime.WindowsRuntime projection
        static Task<T> ToTask<T>(Windows.Foundation.IAsyncOperation<T> op)
        {
            var tcs = new TaskCompletionSource<T>();
            op.Completed = new Windows.Foundation.AsyncOperationCompletedHandler<T>(delegate(Windows.Foundation.IAsyncOperation<T> o, Windows.Foundation.AsyncStatus status)
            {
                if (status == Windows.Foundation.AsyncStatus.Completed) tcs.SetResult(o.GetResults());
                else tcs.SetException(new InvalidOperationException("WinRT operation " + status));
            });
            return tcs.Task;
        }

        async void StartListener()
        {
            try
            {
                listener = UserNotificationListener.Current;
                var st = listener.GetAccessStatus();
                if (st == UserNotificationListenerAccessStatus.Unspecified)
                    st = await ToTask(listener.RequestAccessAsync());
                if (st != UserNotificationListenerAccessStatus.Allowed) { Log.Write("listener access: " + st); return; }
                try { listener.NotificationChanged += delegate { ui.BeginInvoke(new Action(Kick)); }; }
                catch { }
                new Thread(WatchDb) { IsBackground = true }.Start();
                ThreadPool.QueueUserWorkItem(delegate { ToastOrigin.Prepare(); });   // so the first click needs no scan
                recheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                recheck.Tick += delegate { recheck.Stop(); PollOnce(); };
                // Each poll asks the platform for every toast it holds (~25 ms of CPU with a full Action Center), which
                // was two thirds of the idle cost at 1.5 s. While the watcher runs it already reacts to every
                // write, so the safety net only needs to come by now and then.
                poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                poll.Tick += delegate
                {
                    PollOnce();
                    var every = TimeSpan.FromMilliseconds(watching ? 6000 : 1500);
                    if (poll.Interval != every) poll.Interval = every;
                };
                poll.Start();
                PollOnce();
            }
            catch (Exception e) { Log.Write("StartListener: " + e.Message); }
        }

        // The notification platform commits every new toast to its SQLite WAL right away (a few ms).
        // Reading the file size and the wal-index header through an open handle is almost free, so
        // this is a near-instant trigger; the slow timer above is only a safety net.
        void WatchDb()
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\Windows\Notifications");
            FileStream wal = null, shm = null;
            long lastLen = -1;
            byte[] hdr = new byte[48], prev = new byte[48];
            bool havePrev = false;
            while (true)
            {
                try
                {
                    if (wal == null) { wal = OpenShared(System.IO.Path.Combine(dir, "wpndatabase.db-wal")); lastLen = -1; }
                    if (shm == null) { shm = OpenShared(System.IO.Path.Combine(dir, "wpndatabase.db-shm")); havePrev = false; }
                    long len = wal.Length;
                    bool changed = lastLen >= 0 && len != lastLen;
                    lastLen = len;
                    shm.Position = 0;
                    shm.Read(hdr, 0, 48);
                    if (havePrev) for (int i = 0; i < 48; i++) if (hdr[i] != prev[i]) { changed = true; break; }
                    Array.Copy(hdr, prev, 48);
                    havePrev = true;
                    watching = true;
                    if (changed) ui.BeginInvoke(new Action(Kick));
                }
                catch
                {
                    watching = false;
                    if (wal != null) { wal.Dispose(); wal = null; }
                    if (shm != null) { shm.Dispose(); shm = null; }
                    Thread.Sleep(500);
                    continue;
                }
                Thread.Sleep(8);
            }
        }

        static FileStream OpenShared(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
        }

        // poll now, and once more shortly after in case the database write beat the listener
        void Kick()
        {
            PollOnce();
            if (recheck == null) return;
            recheck.Stop();
            recheck.Start();
        }

        async void PollOnce()
        {
            if (busy) { again = true; return; }
            busy = true;
            try
            {
                var list = await ToTask(listener.GetNotificationsAsync(NotificationKinds.Toast));
                var current = new HashSet<uint>();
                foreach (var n in list)
                {
                    current.Add(n.Id);
                    if (seen.Add(n.Id) && primed) ShowFrom(n);
                }
                seen.IntersectWith(current);
                foreach (uint gone in mirrored.Keys.Where(k => !current.Contains(k)).ToList()) mirrored.Remove(gone);
                primed = true;
            }
            catch { }
            finally
            {
                busy = false;
                if (again) { again = false; PollOnce(); }
            }
        }

        ImageSource IconFor(string aumid)
        {
            ImageSource logo;
            if (!logos.TryGetValue(aumid, out logo))
            {
                logo = ShellIcon.Get(aumid, 64);
                logos[aumid] = logo;
            }
            return logo;
        }

        async void ShowFrom(UserNotification n)
        {
            try
            {
                var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding == null) return;
                var texts = binding.GetTextElements();
                string title = texts.Count > 0 ? texts[0].Text : "";
                string body = string.Join("\n", texts.Skip(1).Select(t => t.Text));
                string app = "", id = "";
                ImageSource logo = null;
                // apps known only by an AppUserModelId registry key (Windows App SDK, unpackaged) have no AppInfo
                // (E_NOTIMPL): their id comes from the notification database and their name from that key
                Windows.ApplicationModel.AppInfo info = null;
                try { info = n.AppInfo; } catch { }
                try { if (info != null) id = info.AppUserModelId ?? ""; } catch { }
                try { if (info != null) app = info.DisplayInfo.DisplayName ?? ""; } catch { }
                if (id.Length == 0) id = ToastOrigin.AppOf(n.Id);
                if (app.Length == 0) app = ToastOrigin.NameOf(id);
                if (id.Length > 0)
                {
                    logo = IconFor(id);
                    if (logo == null && info != null)
                    {
                        // not in the Start menu: fall back to the packaged-app logo
                        string key = "winrt:" + id;
                        if (!logos.TryGetValue(key, out logo)) { logo = await LoadLogo(info); logos[key] = logo; }
                    }
                }
                var toast = new ToastOrigin.Target { Id = n.Id, Aumid = id, Title = title, Body = body, Created = n.CreationTime };
                mirrored[n.Id] = toast;
                var b = Show(app.Length > 0 ? app : L.T("banner.app"), title, body, id, 6, logo);
                b.Toast = toast;
            }
            catch (Exception e) { Log.Write("ShowFrom: " + e.Message); }
        }

        static async Task<ImageSource> LoadLogo(Windows.ApplicationModel.AppInfo info)
        {
            try
            {
                var rs = info.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
                var ras = await ToTask(rs.OpenReadAsync());
                var reader = new Windows.Storage.Streams.DataReader(ras);
                uint len = (uint)ras.Size;
                await ToTask(reader.LoadAsync(len));
                var bytes = new byte[len];
                reader.ReadBytes(bytes);
                var ms = new MemoryStream(bytes);
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        // ---- banners ---------------------------------------------------------------

        public static double Scale()
        {
            return System.Windows.Forms.Screen.PrimaryScreen.Bounds.Width / SystemParameters.PrimaryScreenWidth;
        }

        static double CardX() { return SystemParameters.WorkArea.Right - Banner.EdgeR - Banner.CardW; }

        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
        [DllImport("dwmapi.dll")] static extern int DwmFlush();

        // A capture of the screen behind the banners. With GLASSBELL_CAPTURABLE=1 (a test setting, so screenshots
        // see the banners) they are still hidden from the glass's own captures, or the glass would show itself.
        Cap Snap(Rect r, double scale, Look look)
        {
            if (!Banner.Capturable) return Glass.FromScreen(r, scale, look);
            var hidden = new List<IntPtr>();
            foreach (var b in active.Concat(moving).Distinct())
            {
                hidden.Add(new WindowInteropHelper(b).Handle);
                if (b.Shadow != null) hidden.Add(new WindowInteropHelper(b.Shadow).Handle);
            }
            foreach (var h in hidden) SetWindowDisplayAffinity(h, 0x11);
            DwmFlush(); DwmFlush(); DwmFlush();   // until the exclusion is in effect they would show black
            try { return Glass.FromScreen(r, scale, look); }
            finally { foreach (var h in hidden) SetWindowDisplayAffinity(h, 0); }
        }

        // bake the glass of a banner for the screen area it sits over; returns the time the screen capture took
        long BakeOnScreen(Banner b, double slotY)
        {
            double scale = Scale();
            var look = b.Look;
            var sw = Stopwatch.StartNew();
            Cap cap = look.Glass
                ? Snap(new Rect(CardX(), slotY, Banner.CardW, b.CardHeight), scale, look)
                : null;
            long capture = sw.ElapsedMilliseconds;
            b.Card.Bake(cap, scale);
            b.BakedSlotY = slotY;
            return capture;
        }

        static readonly string TraceFlag = System.IO.Path.Combine(Program.DataDir, "trace.flag");

        Banner Show(string app, string title, string body, string appId, double seconds, ImageSource logo)
        {
            return Show(app, title, body, appId, seconds, logo, false);
        }

        // offscreen: the startup warm-up, drawn and animated where nobody sees it and not part of the stack
        Banner Show(string app, string title, string body, string appId, double seconds, ImageSource logo, bool offscreen)
        {
            var sw = Stopwatch.StartNew();
            if (!offscreen) shownAny = true;
            var look = Look.Current();
            if (!offscreen && Settings.Engine == "gpu" && !GpuEngine.Failed)
            {
                var gb = ShowOnGpu(app, title, body, appId, seconds, logo, look, sw);
                if (gb != null) return gb;
            }
            var card = new GlassCard(look, Banner.CardW, 0, false, app, title, body, logo) { ExternalShadows = true };
            card.LayoutNow();
            var b = new Banner(card, appId, seconds);
            long tCard = sw.ElapsedMilliseconds;
            double slotY = SystemParameters.WorkArea.Top + 12;
            long tCapture;
            if (Live(b))
            {
                // live: one strip for the whole slide-in, from right of the screen edge to the card's place
                var cs = Stopwatch.StartNew();
                BakeStrip(b, new Point(CardX() + Banner.SlideOff, slotY), new Point(CardX(), slotY));
                tCapture = cs.ElapsedMilliseconds;
                b.BakedSlotY = slotY;
            }
            else tCapture = BakeOnScreen(b, slotY);
            long tBake = sw.ElapsedMilliseconds;

            // no window at all for a shadow that would not show
            ShadowWindow shadow = card.Shadow.Visible ? new ShadowWindow(card.Shadow, card.CardH) : null;
            b.Shadow = shadow;
            b.Left = offscreen ? SystemParameters.VirtualScreenLeft - 2000 : SystemParameters.WorkArea.Right - b.Width;
            b.Top = -10000;
            if (shadow != null)
            {
                shadow.Left = b.Left + b.Width - shadow.Width;   // the same right edge
                shadow.Top = -10000;
            }
            if (!offscreen)
            {
                b.Dismissing += OnDismissing;
                b.Activated2 += OnActivated;
                b.Hover += OnHover;
                b.Moving += delegate(Banner m) { LiveMove(m, CardAt(m)); };
            }
            long tWindow = 0;
            if (File.Exists(TraceFlag))
                b.ContentRendered += delegate
                {
                    Log.Write("show: card " + tCard + " ms, capture " + tCapture + " ms, bake " + (tBake - tCard - tCapture) +
                        " ms, window " + tWindow + " ms, first frame " + sw.ElapsedMilliseconds + " ms (gpu=" + Gpu.Ok +
                        ", glass=" + look.Glass + ", quality=" + Settings.QualityKeys[look.Quality] + (offscreen ? ", warm-up" : "") +
                        ", inst=" + Program.Instance + ")");
                };
            if (shadow != null) shadow.Show();   // first, so it sits under the card
            b.Show();
            b.UpdateLayout();
            if (offscreen)
            {
                b.Top = 0;
                if (shadow != null) shadow.Top = 0;
                b.Present();
                tWindow = sw.ElapsedMilliseconds - tBake;
                return b;
            }
            active.Insert(0, b);
            Relayout(b);
            b.Present();
            while (active.Count > 4) active[active.Count - 1].Dismiss();
            tWindow = sw.ElapsedMilliseconds - tBake;
            if (look.Sound && !redrawing)
                b.ContentRendered += delegate { PlayChime(look); };   // once the banner is on screen, never before

            if (Live(b)) LiveStart();
            return b;
        }

        // ---- banners drawn by the GPU engine (Settings.Engine = "gpu") -----------------

        // The engine draws the card (see GpuEngine); the WPF window is hollow and only takes the clicks and the hover.
        // null when the engine cannot run here: then the banner is drawn as before.
        // the column: from the shadow's room left of the cards to the right edge of the work area, in screen px
        static bool StartGpu(bool wait)
        {
            double scale = Scale();
            var wa = SystemParameters.WorkArea;
            double cardX = CardX();
            int x = (int)Math.Floor((cardX - GlassCard.ShadowPad) * scale), y = (int)Math.Floor(wa.Top * scale);
            int w = (int)Math.Ceiling(wa.Right * scale) - x, h = (int)Math.Ceiling(wa.Bottom * scale) - y;
            return GpuEngine.Start(x, y, w, h, scale, cardX, wait);
        }

        // the engine was picked in the settings: started now (in the background) rather than with the next banner.
        // Banners already on the screen stay with the way that drew them.
        public static void EngineChanged()
        {
            if (Settings.Engine == "gpu" && !GpuEngine.Failed) StartGpu(false);
        }

        Banner ShowOnGpu(string app, string title, string body, string appId, double seconds, ImageSource logo, Look look, Stopwatch sw)
        {
            double scale = Scale();
            var wa = SystemParameters.WorkArea;
            if (!StartGpu(true)) return null;
            var card = new GlassCard(look, Banner.CardW, 0, false, app, title, body, logo, true);
            card.LayoutNow();
            int id = GpuEngine.Add(GpuSpec(card, look, scale), -10000);   // its place comes from Relayout
            long tCard = sw.ElapsedMilliseconds;
            var b = new Banner(card, appId, seconds, id);
            b.Redraw = delegate { return Show(app, title, body, appId, seconds, logo, false); };
            b.Left = wa.Right - b.Width;
            b.Top = -10000;
            b.Dismissing += OnDismissing;
            b.Activated2 += OnActivated;
            b.Hover += OnHover;
            b.Show();
            b.UpdateLayout();
            active.Insert(0, b);
            Relayout(b);
            b.Present();
            while (active.Count > 4) active[active.Count - 1].Dismiss();
            if (File.Exists(TraceFlag)) Log.Write("show (gpu): card and its pictures " + tCard + " ms, window " + (sw.ElapsedMilliseconds - tCard) + " ms");
            if (look.Sound) b.ContentRendered += delegate { PlayChime(look); };
            return b;
        }

        // the engine has stopped: the banners it drew leave at once and come back drawn by WPF, the oldest first so the
        // newest stays on top
        bool redrawing;

        void RedrawWithoutGpu()
        {
            var drawn = active.Where(x => x.OnGpu).ToList();
            foreach (var b in drawn) b.Vanish();
            redrawing = true;   // they chimed when they came
            try
            {
                for (int i = drawn.Count - 1; i >= 0; i--)
                {
                    var old = drawn[i];
                    if (old.Redraw == null) continue;
                    var nb = old.Redraw();
                    nb.Toast = old.Toast;
                }
            }
            finally { redrawing = false; }
            Log.Write("gpu engine: " + drawn.Count + " banner(s) drawn again by WPF");
        }

        // what the engine needs of a card: its pictures, and the lens and shadow of its look worked out as for WPF
        static GpuCardSpec GpuSpec(GlassCard card, Look look, double scale)
        {
            int w, h;
            var s = new GpuCardSpec();
            s.Dark = card.RenderContent(scale, true, out w, out h);
            s.Light = card.RenderContent(scale, false, out w, out h);
            s.CardW = w;
            s.CardH = h;
            bool glass = look.Glass;
            double rPx = card.RadiusPx(scale);
            int m = glass ? Glass.Margin(look, scale, w, h) : 0;
            double bezel, thick, strength;
            Glass.Lens(look, scale, w, h, out bezel, out thick, out strength);
            double k = glass ? look.K : 0, ll = Math.Sqrt(0.55 * 0.55 + 0.83 * 0.83);
            s.Lens = new float[]
            {
                w + 2 * m, h + 2 * m, w, h,
                m, (float)rPx, (float)bezel, glass ? (float)(strength * thick) : 0,
                glass ? (float)look.DispersionK : 0, glass ? (float)(1 + 0.65 * k) : 1, glass ? (float)(0.025 + 0.045 * k) : 0, (float)k,
                look.Tint.R / 255f, look.Tint.G / 255f, look.Tint.B / 255f, (float)look.TintAlpha,
                (float)(-0.55 / ll), (float)(-0.83 / ll), (float)scale, glass ? 1 : 0
            };
            s.Margin = m;
            s.Quality = look.Quality;
            s.Sigma = glass ? (float)Glass.Sigma(look, scale) : 0;
            s.RadiusPx = (float)rPx;
            s.Inset = (float)scale;   // the WPF shadow is cast by the card inset by 1 DIP
            s.BlurA = (float)(look.ShadowBlurDip * scale);
            s.DepthA = (float)(look.ShadowDepthDip * scale);
            s.OpA = (float)look.AmbientOpacity;
            s.BlurB = (float)(Math.Max(3, look.ShadowBlurDip * 0.12) * scale);
            s.DepthB = (float)(look.ShadowDepthDip * 0.3 * scale);
            s.OpB = (float)look.ContactOpacity;
            s.TwoShadows = look.TwoShadows;
            s.Live = look.LiveBackdrop && glass;
            s.Glass = glass;
            s.LumTint = (float)look.LumTint;
            s.TintAlpha = (float)look.TintAlpha;
            s.K = (float)k;
            return s;
        }

        // ---- live backdrop (Settings.LiveBackground) --------------------------------

        // The glass shows what is behind the card where the card is now, also while it moves. A move (slide in, slide
        // out, a shift in the stack) captures one strip of the screen that covers the whole way, and the lens slides
        // across it frame by frame: on the GPU that is an offset, nothing is captured or uploaded per frame. Banners
        // that stand still are captured again at the rate picked in the settings (Look.LiveFps), all of them in one
        // capture, and Bake leaves alone whatever came out the same. Those captures follow WPF's frames rather than
        // a timer, which ticks every 15.6 ms at best and so could not hold 60 per second. Nothing of this runs while
        // no live banner is on screen. Off: as before.

        readonly List<Banner> moving = new List<Banner>();
        readonly Stopwatch liveClock = new Stopwatch(), liveWindow = new Stopwatch();
        bool liveHooked, liveTrace, liveBusy;
        int liveCount;
        double liveMs, liveSnapMs, liveDue;

        static bool Live(Banner b) { return b.Look.LiveBackdrop && b.Look.Glass && !b.OnGpu; }   // the engine has its own

        // where the card of a banner is (screen DIP), now and at the end of its current moves
        static Point CardAt(Banner b) { return new Point(b.Left + Banner.Pad + b.SlideX, b.Top + Banner.Pad); }
        static Point CardEnd(Banner b)
        {
            return new Point(b.Left + Banner.Pad + b.SlideTo, (double.IsNaN(b.TargetTop) ? b.Top : b.TargetTop) + Banner.Pad);
        }

        static Rect CardRect(Banner b, Point at) { return new Rect(at.X, at.Y, Banner.CardW, b.CardHeight); }

        // bake the glass from one capture that covers the card's way from one point to the other
        void BakeStrip(Banner b, Point from, Point to)
        {
            double scale = Scale();
            var path = Rect.Union(CardRect(b, from), CardRect(b, to));
            Cap cap = Snap(path, scale, b.Look);
            if (cap == null) return;
            cap.OffX = (int)Math.Round((from.X - path.X) * scale);
            cap.OffY = (int)Math.Round((from.Y - path.Y) * scale);
            b.Card.Bake(cap, scale);
            b.StripRect = path;
            b.LiveOrigin = from;
        }

        // a banner starts to move; from is where its card is right now
        void LiveMove(Banner b, Point from)
        {
            if (!Live(b)) return;
            // a strip that already covers the way (the slide-in, baked by Show) is used as it is
            if (!b.StripRect.Contains(Rect.Union(CardRect(b, from), CardRect(b, CardEnd(b))))) BakeStrip(b, from, CardEnd(b));
            if (moving.Contains(b)) return;
            moving.Add(b);
            if (moving.Count == 1) CompositionTarget.Rendering += OnLiveFrame;
        }

        void OnLiveFrame(object sender, EventArgs e)
        {
            double scale = Scale();
            var settled = new List<Banner>();
            for (int i = moving.Count - 1; i >= 0; i--)
            {
                var b = moving[i];
                if (!b.IsVisible) { moving.RemoveAt(i); continue; }   // closed after sliding out
                Point at = CardAt(b);
                b.Card.MoveBackdrop((at.X - b.LiveOrigin.X) * scale, (at.Y - b.LiveOrigin.Y) * scale, scale);
                if (b.Settled) { moving.RemoveAt(i); settled.Add(b); }
            }
            if (settled.Count > 0) LiveRefresh(settled);   // arrived: the glass from where it is now, at once
            if (moving.Count == 0) CompositionTarget.Rendering -= OnLiveFrame;
        }

        void LiveStart()
        {
            if (liveHooked) return;
            liveHooked = true;
            liveTrace = File.Exists(TraceFlag);
            liveClock.Restart();
            liveDue = 0;
            CompositionTarget.Rendering += OnLiveStill;
        }

        void OnLiveStill(object sender, EventArgs e)
        {
            var live = active.Where(Live).ToList();
            if (live.Count == 0) { CompositionTarget.Rendering -= OnLiveStill; liveHooked = false; return; }
            if (liveBusy) return;   // the last capture is still on its way
            // Refreshes can only happen on frames, so each one is due a fixed step after the one before: on average that
            // gives the picked rate on a 60 Hz screen as well as on a 75 or 144 Hz one (2 ms early still counts).
            double now = liveClock.Elapsed.TotalMilliseconds;
            if (now + 2 < liveDue) return;
            var still = live.Where(b => b.Settled && !moving.Contains(b)).ToList();
            if (still.Count == 0) return;
            liveDue = Math.Max(liveDue + 1000.0 / live.Max(b => b.Look.LiveFps), now);   // a late frame does not start a burst
            double scale = Scale();
            Rect all;
            Look widest;
            LiveArea(still, out all, out widest);
            var at = still.Select(CardAt).ToList();
            if (Banner.Capturable) { LiveApply(still, at, all, scale, Snap(all, scale, widest)); return; }   // a test setting
            // A screen capture waits about 11 ms for the compositor, and the bake after it takes well under 1 ms. So the
            // capture runs on a pool thread and only the bake comes back here: the frames go on meanwhile, which is what
            // lets 60 per second happen at all.
            liveBusy = true;
            var took = liveTrace ? Stopwatch.StartNew() : null;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Cap shot = null;
                try { shot = Glass.FromScreen(all, scale, widest); } catch { }
                double snapMs = took != null ? took.Elapsed.TotalMilliseconds : 0;
                ui.BeginInvoke(new Action(delegate
                {
                    liveBusy = false;
                    if (shot == null) return;
                    // a banner that has started to move or has closed meanwhile is the business of its move or its fade
                    var keep = new List<Banner>();
                    var keepAt = new List<Point>();
                    for (int i = 0; i < still.Count; i++)
                        if (active.Contains(still[i]) && !moving.Contains(still[i]) && CardAt(still[i]) == at[i]) { keep.Add(still[i]); keepAt.Add(at[i]); }
                    LiveApply(keep, keepAt, all, scale, shot);
                    if (took != null) LiveTrace(took.Elapsed.TotalMilliseconds, snapMs, keep.Count);
                }));
            });
        }

        // with trace.flag: once a second, how many refreshes there were and what one cost
        void LiveTrace(double ms, double snapMs, int banners)
        {
            liveCount++;
            liveMs += ms;
            liveSnapMs += snapMs;
            if (!liveWindow.IsRunning) liveWindow.Restart();
            if (liveWindow.ElapsedMilliseconds < 1000) return;
            Log.Write("live: " + liveCount + " refreshes in " + liveWindow.ElapsedMilliseconds + " ms, " +
                (liveMs / liveCount).ToString("0.0", CultureInfo.InvariantCulture) + " ms each (capture " +
                (liveSnapMs / liveCount).ToString("0.0", CultureInfo.InvariantCulture) + "), " + banners + " banner(s)");
            liveCount = 0;
            liveMs = 0;
            liveSnapMs = 0;
            liveWindow.Restart();
        }

        // the screen area that covers the cards, and the look with the biggest blur (it needs the widest margin)
        static void LiveArea(List<Banner> banners, out Rect all, out Look widest)
        {
            all = Rect.Empty;
            widest = banners[0].Look;
            foreach (var b in banners)
            {
                all.Union(CardRect(b, CardAt(b)));
                if (Glass.Sigma(b.Look, 1) > Glass.Sigma(widest, 1)) widest = b.Look;
            }
        }

        // one capture for all of them, cut into card-sized views (at: where each card was when it was captured)
        void LiveApply(List<Banner> banners, List<Point> at, Rect all, double scale, Cap shot)
        {
            if (shot == null) return;
            for (int i = 0; i < banners.Count; i++)
            {
                var b = banners[i];
                b.Card.Bake(shot.View((int)Math.Round((at[i].X - all.X) * scale), (int)Math.Round((at[i].Y - all.Y) * scale),
                    Math.Max(1, (int)Math.Round(Banner.CardW * scale)), Math.Max(1, (int)Math.Round(b.CardHeight * scale))), scale);
                b.StripRect = CardRect(b, at[i]);
                b.LiveOrigin = at[i];
                b.BakedSlotY = at[i].Y;
            }
        }

        // right now, on this thread: for banners that have just arrived
        void LiveRefresh(List<Banner> banners)
        {
            double scale = Scale();
            Rect all;
            Look widest;
            LiveArea(banners, out all, out widest);
            LiveApply(banners, banners.Select(CardAt).ToList(), all, scale, Snap(all, scale, widest));
        }

        // on a pool thread: reading the system volume and building the wav take a few ms the UI thread does not wait for
        public static void PlayChime(Look look)
        {
            int style = look.SoundStyle, vol = look.SoundVolume;
            bool adapt = look.SoundAdapt;
            ThreadPool.QueueUserWorkItem(delegate { Chime.Play(style, vol, adapt); });
        }

        void OnDismissing(Banner b)
        {
            active.Remove(b);
            hovered.Remove(b);
            Relayout(null);
            UpdatePill();
        }

        // ---- closing banners: one by one (the close button of each), all at once (the capsule, the tray menu) ----

        ClosePill pill;
        readonly HashSet<Banner> hovered = new HashSet<Banner>();
        bool pillHovered;
        DispatcherTimer pillTimer;

        void OnHover(Banner b, bool over)
        {
            if (over) hovered.Add(b); else hovered.Remove(b);
            UpdatePill();
        }

        void UpdatePill()
        {
            bool want = active.Count >= 2 && (hovered.Count > 0 || pillHovered);
            if (want)
            {
                if (pillTimer != null) pillTimer.Stop();
                if (pill == null)
                {
                    pill = new ClosePill { Left = -10000, Top = -10000 };
                    pill.Clicked += DismissAll;
                    pill.Hover += delegate(bool over) { pillHovered = over; UpdatePill(); };
                    pill.Show();
                }
                PlacePill();
                pill.Appear();
                return;
            }
            if (pill == null || !pill.Shown) return;
            // a moment to get from a banner down to the capsule; with fewer than two banners it goes at once
            if (pillTimer == null)
            {
                pillTimer = new DispatcherTimer();
                pillTimer.Tick += delegate
                {
                    pillTimer.Stop();
                    if (active.Count < 2 || (hovered.Count == 0 && !pillHovered)) pill.Fade();
                };
            }
            pillTimer.Stop();
            pillTimer.Interval = TimeSpan.FromMilliseconds(active.Count >= 2 ? 700 : 1);
            pillTimer.Start();
        }

        // under the lowest banner, where the stack is once its shifts are done
        void PlacePill()
        {
            if (pill == null) return;
            double y = SystemParameters.WorkArea.Top + 12;
            foreach (var b in active) y += b.CardHeight + 10;
            pill.Place(CardX(), y - 4);
        }

        public void DismissAll()
        {
            var open = active.ToList();
            hovered.Clear();
            pillHovered = false;
            if (pill != null) pill.Fade();
            // one after the other, the top one first, so they leave as a wave rather than all at once
            for (int i = 0; i < open.Count; i++)
            {
                var b = open[i];
                if (i == 0) { b.Dismiss(); continue; }
                var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45 * i) };
                t.Tick += delegate { t.Stop(); b.Dismiss(); };
                t.Start();
            }
        }

        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);

        // A click goes where the original toast leads (see ToastOrigin), off the UI thread: COM servers start and UI
        // Automation calls can take a while. Starting the app stays the fallback, and the only thing a pipe banner does.
        void OnActivated(Banner b)
        {
            var t = b.Toast;
            if (t == null) { LaunchApp(b.AppId); return; }
            t.Newer = mirrored.Values.Count(o => o != t && o.Aumid == t.Aumid && o.Title == t.Title && o.Body == t.Body && o.Created > t.Created);
            AllowSetForegroundWindow(-1);   // this click is the user's last input: whatever opens may come to the front
            new Thread(delegate()
            {
                var sw = Stopwatch.StartNew();
                string how = null;
                try { how = ToastOrigin.Activate(t); }
                catch (Exception e) { Log.Write("toast click: " + e.Message); }
                Log.Write("click: toast " + t.Id + " of " + t.Aumid + " -> " + (how ?? "app started") + " (" + sw.ElapsedMilliseconds + " ms)");
                ui.BeginInvoke(new Action(delegate
                {
                    if (how == null) { LaunchApp(t.Aumid); return; }
                    // like a click on the toast itself: it is done, and leaves the notification center
                    try { listener.RemoveNotification(t.Id); }
                    catch { }
                }));
            }) { IsBackground = true }.Start();
        }

        static void LaunchApp(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return;
            try { Process.Start("explorer.exe", "shell:AppsFolder\\" + aumid); }
            catch { }
        }

        void Relayout(Banner fresh)
        {
            double y = SystemParameters.WorkArea.Top + 12;
            foreach (var b in active)
            {
                double top = y - Banner.Pad, slot = y;
                double shadowTop = b.Shadow != null ? y - b.Shadow.PadTop : 0;
                if (b == fresh)
                {
                    b.Top = top;
                    if (b.Shadow != null) b.Shadow.Top = shadowTop;
                    if (b.OnGpu) GpuEngine.Set(b.GpuId, 'y', slot);
                }
                else
                {
                    var anim = b.Look.Anim(null, top, 260, EasingMode.EaseOut);
                    Banner moved = b;
                    moved.Shifting = true;
                    anim.Completed += delegate
                    {
                        if (moved.Top != top) return;   // a newer shift took over
                        moved.Shifting = false;
                        // the glass shows what is behind its new place (the low level keeps the old picture; a live
                        // glass has followed all the way and is refreshed by OnLiveFrame)
                        if (moved.OnGpu) { if (!moved.Look.LiveBackdrop) GpuEngine.Refresh(); return; }   // the engine captures again
                        if (!moved.IsClosing && moved.Look.Glass && moved.Look.RebakeOnShift && !Live(moved) && Math.Abs(moved.BakedSlotY - slot) > 1)
                            BakeOnScreen(moved, slot);
                    };
                    Point from = CardAt(b);   // read before the new animation takes over the window's position
                    b.TargetTop = top;
                    b.BeginAnimation(Window.TopProperty, anim);
                    LiveMove(b, from);
                    if (b.OnGpu) GpuEngine.Move(b.GpuId, 'y', double.NaN, slot, 260, GpuEngine.EaseOut, 0);
                    if (b.Shadow != null)
                        b.Shadow.BeginAnimation(Window.TopProperty, b.Look.Anim(null, shadowTop, 260, EasingMode.EaseOut));
                }
                y += b.CardHeight + 10;
            }
            if (pill != null && pill.Shown) PlacePill();   // the capsule stays under the stack
        }

        // ---- settings + tray -------------------------------------------------------

        public void Preview()
        {
            if (previewBanner != null) previewBanner.Dismiss();
            previewBanner = Show("GlassBell", L.T("preview.title"), L.T("preview.body"), "", 4, null);
        }

        // a new interface language: the tray menu and the settings window are built again in it, the window where it was
        public void ChangeLanguage(string code)
        {
            Settings.Language = code;
            Settings.Save();
            L.Init(code);
            Log.Write("language: " + code + " -> " + L.Code);
            if (tray != null)
            {
                var old = tray.ContextMenuStrip;
                tray.ContextMenuStrip = TrayMenu();
                old.Dispose();
            }
            var was = workshop;
            if (was == null || !was.IsLoaded) return;
            workshop = null;
            OpenSettings(new Point(was.Left, was.Top), was.Tab);
            was.Close();
        }

        void OpenSettings() { OpenSettings(null, 0); }

        void OpenSettings(Point? at, int tab)
        {
            if (workshop == null || !workshop.IsLoaded)
            {
                workshop = new SettingsUi(this);
                if (at != null)
                {
                    workshop.WindowStartupLocation = WindowStartupLocation.Manual;
                    workshop.Left = at.Value.X;
                    workshop.Top = at.Value.Y;
                }
                workshop.SelectTab(tab);
                try
                {
                    using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("GlassBell.ico"))
                        if (s != null)
                            workshop.Icon = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad)
                                .Frames.OrderByDescending(f => f.PixelWidth).First();
                }
                catch { }
                workshop.Show();
            }
            if (SettingsUi.SnapshotPath != null) return;   // a test render off-screen: the user's focus stays where it is
            // a background process may not take the foreground, so bounce through Topmost
            workshop.WindowState = WindowState.Normal;
            workshop.Topmost = true;
            workshop.Activate();
            workshop.Topmost = false;
        }

        [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr proc, uint flags);

        // `GlassBell.exe --stats` (for a memory check): what the process holds, then once more after a full collection.
        // If the second line falls back, the first was garbage waiting for the collector; if it stays, something is held.
        string Stats()
        {
            var p = Process.GetCurrentProcess();
            return "private " + p.PrivateMemorySize64 / 1048576 + " MB, working set " + p.WorkingSet64 / 1048576 +
                " MB, managed " + GC.GetTotalMemory(false) / 1048576 + " MB, gc " + GC.CollectionCount(0) + "/" +
                GC.CollectionCount(1) + "/" + GC.CollectionCount(2) + ", handles " + p.HandleCount +
                ", gdi " + GetGuiResources(p.Handle, 0) + ", user " + GetGuiResources(p.Handle, 1) +
                ", windows " + app.Windows.Count + ", active " + active.Count + ", moving " + moving.Count +
                ", mirrored " + mirrored.Count;
        }

        void LogStats()
        {
            Log.Write("stats: " + Stats());
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Log.Write("stats after full GC: " + Stats());
        }

        // A GlassBell.new.exe next to the exe replaces it and the program starts again. This is how a new build is
        // put in place while the old one runs (`GlassBell.exe --update` asks the running copy to do it).
        void ApplyUpdate()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
                string dir = System.IO.Path.GetDirectoryName(exe);
                string fresh = System.IO.Path.Combine(dir, "GlassBell.new.exe");
                if (!File.Exists(fresh)) { Log.Write("update: no GlassBell.new.exe"); return; }
                string cmd = "/c ping 127.0.0.1 -n 3 >nul & move /y \"" + fresh + "\" \"" + exe + "\" & start \"\" \"" + exe + "\" --daemon";
                Process.Start(new ProcessStartInfo("cmd.exe", cmd) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir });
                Log.Write("update: restarting with GlassBell.new.exe");
                app.Shutdown();
            }
            catch (Exception e) { Log.Write("update: " + e.Message); }
        }

        // the embedded .ico holds 16..256 px frames; the constructor picks the one that fits the tray at this DPI
        static System.Drawing.Icon TrayIcon()
        {
            try
            {
                using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("GlassBell.ico"))
                    if (s != null) return new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
            catch { }
            return System.Drawing.SystemIcons.Information;
        }

        void StartTray()
        {
            tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = TrayIcon(),
                Text = "GlassBell",
                ContextMenuStrip = TrayMenu(),
                Visible = true
            };
            tray.DoubleClick += delegate { OpenSettings(); };
        }

        System.Windows.Forms.ContextMenuStrip TrayMenu()
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            if (L.Rtl) menu.RightToLeft = System.Windows.Forms.RightToLeft.Yes;
            menu.Items.Add(L.T("tray.settings"), null, delegate { OpenSettings(); });
            menu.Items.Add(L.T("tray.test"), null, delegate { Preview(); });
            var closeAll = new System.Windows.Forms.ToolStripMenuItem(L.T("tray.closeAll"));
            closeAll.Click += delegate { DismissAll(); };
            menu.Opening += delegate { closeAll.Enabled = active.Count > 0; };
            menu.Items.Add(closeAll);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            var auto = new System.Windows.Forms.ToolStripMenuItem(L.T("tray.autostart"));
            auto.Click += delegate { Startup.Set(!Startup.Enabled); };
            menu.Opening += delegate { auto.Checked = Startup.Enabled; auto.Enabled = !Startup.Sandboxed; };
            menu.Items.Add(auto);
            menu.Items.Add(L.T("tray.exit"), null, delegate { app.Shutdown(); });
            return menu;
        }
    }
}
