// GlassBell-Setup: installs GlassBell for the current user (no administrator rights needed), updates an installed
// copy in place, and, copied into the install folder as uninstall.exe, removes it again.
//
//   GlassBell-Setup.exe [/quiet] [/noautostart] [/nodesktop] [/nostart] [/lang:xx]
//   uninstall.exe /uninstall [/quiet] [/purge]          /purge also deletes the settings and the log
//
// What goes where: the program and uninstall.exe in %LOCALAPPDATA%\Programs\GlassBell, a Start menu shortcut,
// optionally a desktop shortcut and the HKCU Run entry, and an entry in Settings > Apps (HKCU ...\Uninstall\GlassBell).
// The settings stay where the program keeps them, in %APPDATA%\GlassBell, so an update keeps them.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

[assembly: AssemblyTitle("GlassBell Setup")]

namespace GlassBell
{
    static class Setup
    {
        public const string Name = "GlassBell";
        public const string Repo = "https://github.com/vibegineer/GlassBell";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\GlassBell";

        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);
        public static readonly string Exe = Path.Combine(Dir, "GlassBell.exe");
        static readonly string Uninstaller = Path.Combine(Dir, "uninstall.exe");
        static readonly string StartMenuLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk");
        public static readonly string DesktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk");

        public static string Version
        {
            get { return Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion; }
        }

        public static bool Installed { get { return File.Exists(Exe); } }

        // set once an uninstall has run, so the folder (with uninstall.exe in it) is removed after this process exits
        public static bool Removed;

        [STAThread]
        static int Main(string[] args)
        {
            var a = new HashSet<string>(args.Select(x => x.ToLowerInvariant()));
            L.Init(a.Where(x => x.StartsWith("/lang:")).Select(x => x.Substring(6)).FirstOrDefault());
            bool quiet = a.Contains("/quiet");
            if (Environment.OSVersion.Version.Build < 22000) { if (!quiet) Say(L.T("setup.needWin11"), true); return 2; }
            // /force: a test inside a packaged app, where the install only lands in that app's private copy
            if (Sandbox.Active && !a.Contains("/force")) { if (!quiet) Say(L.T("setup.sandboxed"), true); return 3; }

            if (a.Contains("/uninstall"))
            {
                Environment.CurrentDirectory = Path.GetTempPath();   // not inside the folder that is about to go
                int rc;
                if (quiet) rc = Uninstall(a.Contains("/purge")) == null ? 0 : 1;
                else rc = new Application().Run(new SetupWindow(true));
                if (Removed) ScheduleFolderRemoval();
                return rc;
            }
            if (quiet) return Install(!a.Contains("/noautostart"), !a.Contains("/nodesktop"), !a.Contains("/nostart")) == null ? 0 : 1;
            return new Application().Run(new SetupWindow(false));
        }

        public static void Say(string text, bool warning)
        {
            MessageBox.Show(text, Name, MessageBoxButton.OK, warning ? MessageBoxImage.Warning : MessageBoxImage.Information,
                MessageBoxResult.OK, L.Rtl ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : MessageBoxOptions.None);
        }

        // ---- install ----

        // returns null when all went well, otherwise what went wrong
        public static string Install(bool autostart, bool desktop, bool start)
        {
            try
            {
                StopRunning();
                Directory.CreateDirectory(Dir);
                Extract("GlassBell.exe", Exe);
                Extract("LICENSE.txt", Path.Combine(Dir, "LICENSE.txt"));
                string self = Assembly.GetEntryAssembly().Location;
                if (!string.Equals(self, Uninstaller, StringComparison.OrdinalIgnoreCase)) Retry(delegate { File.Copy(self, Uninstaller, true); });
                Link(StartMenuLink);
                if (desktop) Link(DesktopLink);
                else if (File.Exists(DesktopLink)) File.Delete(DesktopLink);
                SetAutostart(autostart);
                Register();
                if (start) Process.Start(new ProcessStartInfo(Exe, "--daemon") { UseShellExecute = false, WorkingDirectory = Dir });
                return null;
            }
            catch (Exception e) { return e.Message; }
        }

        public static bool AutostartOn()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(Name) != null;
            }
            catch { return false; }
        }

        // the running copy is asked to quit (over its pipe, like GlassBell.exe --quit) and given a few seconds
        static void StopRunning()
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", "GlassBell.v1", PipeDirection.Out))
                {
                    c.Connect(300);
                    byte[] b = new UTF8Encoding(false).GetBytes("\u0001quit");
                    c.Write(b, 0, b.Length);
                    c.Flush();
                }
            }
            catch { }   // nothing is running
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (true)
            {
                var left = Process.GetProcessesByName(Name).Where(IsInstalledCopy).ToList();
                if (left.Count == 0) return;
                if (DateTime.UtcNow > deadline)
                {
                    foreach (var p in left) try { p.Kill(); p.WaitForExit(3000); } catch { }
                    return;
                }
                Thread.Sleep(150);
            }
        }

        static bool IsInstalledCopy(Process p)
        {
            try { return string.Equals(p.MainModule.FileName, Exe, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        static void Extract(string resource, string target)
        {
            string fresh = target + ".new";
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using (var f = File.Create(fresh))
                s.CopyTo(f);
            Retry(delegate
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(fresh, target);
            });
        }

        // a program that has just exited can hold on to its file for a moment
        static void Retry(Action step)
        {
            for (int i = 0; ; i++)
            {
                try { step(); return; }
                catch (IOException) { if (i >= 30) throw; }
                catch (UnauthorizedAccessException) { if (i >= 30) throw; }
                Thread.Sleep(200);
            }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr data, uint flags);
            void GetIDList(out IntPtr list);
            void SetIDList(IntPtr list);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short key);
            void SetHotkey(short key);
            void GetShowCmd(out int cmd);
            void SetShowCmd(int cmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int size, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        // a shortcut to the program with no arguments: it starts GlassBell if needed and opens its settings
        static void Link(string lnk)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(Exe);
                link.SetWorkingDirectory(Dir);
                link.SetIconLocation(Exe, 0);
                link.SetDescription(L.T("setup.tagline"));
                Directory.CreateDirectory(Path.GetDirectoryName(lnk));
                ((IPersistFile)link).Save(lnk, true);
            }
            finally { Marshal.FinalReleaseComObject(link); }
        }

        // the same command GlassBell.exe writes itself, so its tray menu shows the switch as on
        static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(Name, "\"" + Exe + "\" --daemon", RegistryValueKind.String);
                else k.DeleteValue(Name, false);
            }
            // a switch-off made in Task Manager's Startup tab is remembered here and would keep the entry dead
            if (on)
                using (var k = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                    if (k != null) k.DeleteValue(Name, false);
        }

        // the entry in Settings > Apps > Installed apps
        static void Register()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Name);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "vibegineer");
                k.SetValue("DisplayIcon", Exe);
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + Uninstaller + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + Uninstaller + "\" /uninstall /quiet");
                k.SetValue("URLInfoAbout", Repo);
                k.SetValue("HelpLink", Repo);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long bytes = new[] { Exe, Uninstaller }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
                k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
            }
        }

        // ---- uninstall ----

        public static string Uninstall(bool purge)
        {
            var problems = new List<string>();
            Action<Action> step = delegate(Action a) { try { a(); } catch (Exception e) { problems.Add(e.Message); } };
            step(StopRunning);
            step(delegate { SetAutostart(false); });
            step(delegate { foreach (var l in new[] { StartMenuLink, DesktopLink }) if (File.Exists(l)) File.Delete(l); });
            step(delegate { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); });
            step(delegate { if (File.Exists(Exe)) Retry(delegate { File.Delete(Exe); }); });
            if (purge)
            {
                step(delegate { DeleteFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name)); });
                step(delegate { DeleteFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name)); });
            }
            Removed = true;
            return problems.Count == 0 ? null : string.Join("\n", problems);
        }

        static void DeleteFolder(string dir) { if (Directory.Exists(dir)) Directory.Delete(dir, true); }

        // uninstall.exe cannot delete itself: cmd removes the folder once this process has exited
        static void ScheduleFolderRemoval()
        {
            if (!Path.IsPathRooted(Dir) || !Dir.EndsWith(@"\Programs\" + Name, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(Dir)) return;
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + Dir + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath()
            });
        }
    }

    // ---- the window: install (or update), done, uninstall ----

    sealed class SetupWindow : Window
    {
        const string Styles = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='Primary' TargetType='{x:Type Button}'>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='MinWidth' Value='110'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Button}'>
          <Border x:Name='B' CornerRadius='9' Padding='18,8' Background='#0A84FF' BorderThickness='1.5' BorderBrush='Transparent' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#2B95FF'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='B' Property='Background' Value='#0070E0'/></Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#B0FFFFFF'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='B' Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key='Secondary' TargetType='{x:Type Button}' BasedOn='{StaticResource Primary}'>
    <Setter Property='FontWeight' Value='Normal'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type Button}'>
          <Border x:Name='B' CornerRadius='9' Padding='18,8' Background='#3A3A3C' BorderThickness='1.5' BorderBrush='Transparent' SnapsToDevicePixels='True'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='B' Property='Background' Value='#48484A'/></Trigger>
            <Trigger Property='IsPressed' Value='True'><Setter TargetName='B' Property='Background' Value='#2C2C2E'/></Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='B' Property='BorderBrush' Value='#B0FFFFFF'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter TargetName='B' Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='{x:Type CheckBox}'>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='{x:Type CheckBox}'>
          <Grid Background='Transparent'>
            <Grid.ColumnDefinitions><ColumnDefinition Width='Auto'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
            <Border x:Name='Box' Width='18' Height='18' CornerRadius='5' Background='#2C2C2E' BorderBrush='#636366' BorderThickness='1.5' VerticalAlignment='Center'>
              <Path x:Name='Tick' FlowDirection='LeftToRight' Data='M 3.5 8.5 L 6.8 11.8 L 12.5 5' Stroke='White' StrokeThickness='2'
                    StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' Visibility='Collapsed'/>
            </Border>
            <ContentPresenter Grid.Column='1' Margin='10,0,0,0' VerticalAlignment='Center'/>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='True'>
              <Setter TargetName='Box' Property='Background' Value='#0A84FF'/>
              <Setter TargetName='Box' Property='BorderBrush' Value='#0A84FF'/>
              <Setter TargetName='Tick' Property='Visibility' Value='Visible'/>
            </Trigger>
            <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='White'/></Trigger>
            <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

        readonly bool uninstall;
        readonly StackPanel body = new StackPanel();
        readonly StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        readonly TextBlock heading = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        readonly TextBlock subline = new TextBlock { FontSize = 12.5, Foreground = Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        readonly TextBlock status = new TextBlock { FontSize = 12, Foreground = Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
        bool busy;

        static readonly Brush Gray = Frozen(Color.FromRgb(0x98, 0x98, 0x9F));

        static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public SetupWindow(bool uninstall)
        {
            this.uninstall = uninstall;
            Title = uninstall ? L.T("setup.uninstallTitle") : L.F("setup.title", Setup.Version);
            Width = 500;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            Language = XmlLanguage.GetLanguage(L.Ietf);
            if (L.Rtl) FlowDirection = FlowDirection.RightToLeft;
            Background = Frozen(Color.FromRgb(0x1C, 0x1C, 0x1E));
            Foreground = Brushes.White;
            UseLayoutRounding = true;
            Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Styles));
            var icon = LoadIcon();
            if (icon != null) Icon = icon;
            // the dark title bar, as in the settings window
            SourceInitialized += delegate { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, 4); };
            Closing += (s, e) => { if (busy) e.Cancel = true; };

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var img = new Image { Source = icon, Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Top, FlowDirection = FlowDirection.LeftToRight };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            top.Children.Add(img);
            var titles = new StackPanel { Margin = new Thickness(16, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(heading);
            titles.Children.Add(subline);
            Grid.SetColumn(titles, 1);
            top.Children.Add(titles);

            var root = new StackPanel { Margin = new Thickness(28, 26, 28, 24) };
            root.Children.Add(top);
            root.Children.Add(body);
            var foot = new Grid();
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foot.Children.Add(status);
            Grid.SetColumn(buttons, 1);
            foot.Children.Add(buttons);
            root.Children.Add(foot);
            Content = root;

            if (uninstall) ShowUninstall(); else ShowInstall();
            if (SnapshotPath != null)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = -20000;
                Top = 0;
                ShowActivated = false;
                ShowInTaskbar = false;
                ContentRendered += delegate { Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(SaveSnapshots)); };
            }
        }

        // GLASSBELL_SNAPSHOT=file.png (a test setting): the three pages are saved as file-install.png, file-done.png and
        // file-uninstall.png, texts that do not fit are listed in file-overflow.txt, and the setup quits without
        // installing anything. This is how every translation is checked.
        static readonly string SnapshotPath = Environment.GetEnvironmentVariable("GLASSBELL_SNAPSHOT");

        void SaveSnapshots()
        {
            string stem = Path.ChangeExtension(SnapshotPath, null);
            var report = new StringBuilder();
            var pages = new Action[] { ShowInstall, ShowDone, ShowUninstall };
            string[] names = { "install", "done", "uninstall" };
            try
            {
                for (int i = 0; i < pages.Length; i++)
                {
                    pages[i]();
                    UpdateLayout();
                    var root = (FrameworkElement)Content;
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, root.ActualHeight + root.Margin.Top + root.Margin.Bottom));
                        dc.DrawRectangle(new VisualBrush(root), null, new Rect(root.Margin.Left, root.Margin.Top, root.ActualWidth, root.ActualHeight));
                    }
                    var bmp = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(root.ActualHeight + root.Margin.Top + root.Margin.Bottom),
                        96, 96, PixelFormats.Pbgra32);
                    bmp.Render(dv);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (var f = File.Create(stem + "-" + names[i] + ".png")) enc.Save(f);
                    Overflow(this, names[i], report);
                }
            }
            catch (Exception e) { report.AppendLine("snapshot: " + e.Message); }
            File.WriteAllText(stem + "-overflow.txt", report.ToString());
            Close();
        }

        static void Overflow(DependencyObject top, string page, StringBuilder report)
        {
            var todo = new Stack<DependencyObject>();
            todo.Push(top);
            while (todo.Count > 0)
            {
                var d = todo.Pop();
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) todo.Push(VisualTreeHelper.GetChild(d, i));
                var tb = d as TextBlock;
                if (tb == null || !tb.IsVisible || string.IsNullOrEmpty(tb.Text) || tb.ActualWidth <= 0) continue;
                double w = tb.ActualWidth, h = tb.ActualHeight;
                tb.Measure(new Size(tb.TextWrapping == TextWrapping.NoWrap ? double.PositiveInfinity : w + tb.Margin.Left + tb.Margin.Right, double.PositiveInfinity));
                double needW = tb.DesiredSize.Width - tb.Margin.Left - tb.Margin.Right, needH = tb.DesiredSize.Height - tb.Margin.Top - tb.Margin.Bottom;
                if (needW > w + 1 || needH > h + 1)
                    report.AppendLine(page + ": \"" + tb.Text.Replace("\n", " ") + "\" needs " + Math.Ceiling(needW) + "x" + Math.Ceiling(needH) +
                        ", has " + Math.Floor(w) + "x" + Math.Floor(h));
            }
        }

        static BitmapSource LoadIcon()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("GlassBell.ico"))
                    return new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames.OrderByDescending(f => f.PixelWidth).First();
            }
            catch { return null; }
        }

        Button AddButton(string text, bool primary, Action click)
        {
            var b = new Button { Content = text, Style = (Style)FindResource(primary ? "Primary" : "Secondary"), Margin = new Thickness(10, 0, 0, 0), IsDefault = primary };
            b.Click += delegate { click(); };
            buttons.Children.Add(b);
            return b;
        }

        CheckBox AddCheck(string text, bool on, double top)
        {
            var c = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, IsChecked = on, Margin = new Thickness(0, top, 0, 0) };
            body.Children.Add(c);
            return c;
        }

        void Busy(bool on, string text)
        {
            busy = on;
            status.Text = text ?? "";
            body.IsEnabled = !on;
            foreach (UIElement b in buttons.Children) b.IsEnabled = !on;
        }

        void ShowInstall()
        {
            bool update = Setup.Installed;
            heading.Text = Setup.Name;
            subline.Text = L.T("setup.tagline") + "  ·  " + Setup.Version;
            body.Children.Clear();
            body.Children.Add(new TextBlock { Text = L.T("setup.folder"), FontSize = 12, Foreground = Gray, Margin = new Thickness(0, 22, 0, 0) });
            body.Children.Add(new TextBlock
            {
                Text = Setup.Dir, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = Setup.Dir, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = L.Rtl ? HorizontalAlignment.Right : HorizontalAlignment.Left
            });
            var auto = AddCheck(L.T("setup.autostart"), !update || Setup.AutostartOn(), 18);
            var desk = AddCheck(L.T("setup.desktop"), !update || File.Exists(Setup.DesktopLink), 10);
            buttons.Children.Clear();
            AddButton(L.T("setup.cancel"), false, Close);
            AddButton(L.T(update ? "setup.update" : "setup.install"), true, delegate
            {
                bool a = auto.IsChecked == true, d = desk.IsChecked == true;
                Busy(true, L.T("setup.working"));
                Task.Run(() => Setup.Install(a, d, true)).ContinueWith(t =>
                {
                    Busy(false, null);
                    if (t.Result != null) Setup.Say(L.F("setup.failed", t.Result), true);
                    else ShowDone();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            });
        }

        void ShowDone()
        {
            heading.Text = L.T("setup.done");
            subline.Text = Setup.Version;
            body.Children.Clear();
            body.Children.Add(new TextBlock { Text = L.T("setup.doneHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 20, 0, 0), LineHeight = 19 });
            buttons.Children.Clear();
            AddButton(L.T("setup.close"), false, Close);
            AddButton(L.T("setup.open"), true, delegate
            {
                try { Process.Start(new ProcessStartInfo(Setup.Exe, "--settings") { UseShellExecute = false, WorkingDirectory = Setup.Dir }); } catch { }
                Close();
            });
        }

        void ShowUninstall()
        {
            heading.Text = L.T("setup.uninstallTitle");
            subline.Text = L.T("setup.uninstallAsk");
            body.Children.Clear();
            var purge = AddCheck(L.T("setup.removeSettings"), false, 22);
            buttons.Children.Clear();
            AddButton(L.T("setup.cancel"), false, Close);
            AddButton(L.T("setup.uninstall"), true, delegate
            {
                bool p = purge.IsChecked == true;
                Busy(true, null);
                Task.Run(() => Setup.Uninstall(p)).ContinueWith(t =>
                {
                    Busy(false, null);
                    if (t.Result != null) Setup.Say(L.F("setup.uninstallFailed", t.Result), true);
                    else Setup.Say(L.T("setup.uninstalled"), false);
                    Close();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            });
        }
    }
}
