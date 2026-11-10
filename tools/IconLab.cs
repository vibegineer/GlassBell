// Draws the five GlassBell icon proposals (WPF vectors), renders them to PNG and builds a preview sheet.
//   IconLab.exe sheet <outDir>          -> icon1..5.png (512 px) and sheet.png
//   IconLab.exe ico <n> <outFile.ico>   -> multi-size .ico of proposal n
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

static class IconLab
{
    const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly string SQ = Squircle(128, 128, 120, 6.0);

    static readonly string[] Names =
    {
        "Glass banner", "Bell", "Lens", "Sunset card", "Monogram N"
    };

    // continuous-corner rounded square (superellipse), like the macOS icon shape
    static string Squircle(double cx, double cy, double half, double n)
    {
        var sb = new StringBuilder();
        int steps = 240;
        for (int i = 0; i < steps; i++)
        {
            double t = 2 * Math.PI * i / steps;
            double c = Math.Cos(t), s = Math.Sin(t);
            double x = cx + half * Math.Sign(c) * Math.Pow(Math.Abs(c), 2.0 / n);
            double y = cy + half * Math.Sign(s) * Math.Pow(Math.Abs(s), 2.0 / n);
            sb.Append(i == 0 ? "M " : " L ").Append(x.ToString("0.##", Inv)).Append(',').Append(y.ToString("0.##", Inv));
        }
        return sb.Append(" Z").ToString();
    }

    static string Circle(double cx, double cy, double r)
    {
        return string.Format(Inv, "M {0},{1} A {2},{2} 0 1 1 {3},{1} A {2},{2} 0 1 1 {0},{1} Z", cx - r, cy, r, cx + r);
    }

    // vivid diagonal bands: the "wallpaper" the lens bends in icon 3
    static string Bands()
    {
        string[] cols = { "#FFC145", "#FF5D8F", "#8B5CF6", "#22C1F1", "#2DD4A8" };
        var sb = new StringBuilder();
        double w = 36;
        for (int i = 0; i < 14; i++)
            sb.Append(string.Format(Inv, "<Rectangle Canvas.Left='{0}' Canvas.Top='-160' Width='{1}' Height='580' Fill='{2}'/>",
                -150 + i * w, w + 0.6, cols[i % cols.Length]));
        return sb.ToString();
    }

    static string Xaml(int n)
    {
        string body;
        switch (n)
        {
            case 1: body = @"
  <Rectangle Width='256' Height='256'><Rectangle.Fill>
    <LinearGradientBrush StartPoint='0,0' EndPoint='1,1'>
      <GradientStop Color='#4457D6' Offset='0'/><GradientStop Color='#1C2158' Offset='0.55'/><GradientStop Color='#0A0C22' Offset='1'/>
    </LinearGradientBrush></Rectangle.Fill></Rectangle>
  <Ellipse Canvas.Left='-50' Canvas.Top='110' Width='210' Height='210'><Ellipse.Fill><RadialGradientBrush>
    <GradientStop Color='#EE00D1FF' Offset='0'/><GradientStop Color='#0000D1FF' Offset='1'/></RadialGradientBrush></Ellipse.Fill></Ellipse>
  <Ellipse Canvas.Left='110' Canvas.Top='-60' Width='230' Height='230'><Ellipse.Fill><RadialGradientBrush>
    <GradientStop Color='#EEFF3DA5' Offset='0'/><GradientStop Color='#00FF3DA5' Offset='1'/></RadialGradientBrush></Ellipse.Fill></Ellipse>
  <Ellipse Canvas.Left='130' Canvas.Top='140' Width='150' Height='150'><Ellipse.Fill><RadialGradientBrush>
    <GradientStop Color='#DDFFA23D' Offset='0'/><GradientStop Color='#00FFA23D' Offset='1'/></RadialGradientBrush></Ellipse.Fill></Ellipse>
  <Rectangle Canvas.Left='22' Canvas.Top='84' Width='212' Height='92' RadiusX='46' RadiusY='46' StrokeThickness='3'>
    <Rectangle.Fill><LinearGradientBrush StartPoint='0,0' EndPoint='0,1'>
      <GradientStop Color='#70FFFFFF' Offset='0'/><GradientStop Color='#26FFFFFF' Offset='0.55'/><GradientStop Color='#40FFFFFF' Offset='1'/></LinearGradientBrush></Rectangle.Fill>
    <Rectangle.Stroke><LinearGradientBrush StartPoint='0,0' EndPoint='1,1'>
      <GradientStop Color='#FFFFFFFF' Offset='0'/><GradientStop Color='#30FFFFFF' Offset='0.45'/><GradientStop Color='#A0FFFFFF' Offset='1'/></LinearGradientBrush></Rectangle.Stroke>
    <Rectangle.Effect><DropShadowEffect BlurRadius='16' ShadowDepth='7' Direction='270' Opacity='0.4' Color='#000000'/></Rectangle.Effect>
  </Rectangle>
  <Path Tag='fine' Data='M 58,95 Q 128,84 198,95' Stroke='#C0FFFFFF' StrokeThickness='3.5' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
  <Ellipse Canvas.Left='40' Canvas.Top='99' Width='58' Height='58' StrokeThickness='2' Stroke='#90FFFFFF'><Ellipse.Fill>
    <LinearGradientBrush StartPoint='0,0' EndPoint='1,1'><GradientStop Color='#7FD3FF' Offset='0'/><GradientStop Color='#2F6BFF' Offset='1'/></LinearGradientBrush></Ellipse.Fill></Ellipse>
  <Ellipse Canvas.Left='58' Canvas.Top='117' Width='22' Height='22' Fill='#F5FFFFFF'/>
  <Rectangle Tag='fine' Canvas.Left='112' Canvas.Top='110' Width='92' Height='13' RadiusX='6.5' RadiusY='6.5' Fill='#F5FFFFFF'/>
  <Rectangle Tag='fine' Canvas.Left='112' Canvas.Top='134' Width='66' Height='11' RadiusX='5.5' RadiusY='5.5' Fill='#A0FFFFFF'/>";
                break;

            case 2: body = @"
  <Rectangle Width='256' Height='256'><Rectangle.Fill>
    <LinearGradientBrush StartPoint='0.15,0' EndPoint='0.85,1'>
      <GradientStop Color='#6FB1FF' Offset='0'/><GradientStop Color='#5B5CF0' Offset='0.55'/><GradientStop Color='#35239E' Offset='1'/>
    </LinearGradientBrush></Rectangle.Fill></Rectangle>
  <Ellipse Canvas.Left='-60' Canvas.Top='-80' Width='260' Height='220'><Ellipse.Fill><RadialGradientBrush>
    <GradientStop Color='#55FFFFFF' Offset='0'/><GradientStop Color='#00FFFFFF' Offset='1'/></RadialGradientBrush></Ellipse.Fill></Ellipse>
  <Canvas>
    <Canvas.Effect><DropShadowEffect BlurRadius='14' ShadowDepth='7' Direction='270' Opacity='0.4' Color='#14084A'/></Canvas.Effect>
    <Path Data='M128,56 C98,56 82,80 82,110 L82,140 C82,154 72,162 62,170 C58,174 60,180 66,180 L190,180 C196,180 198,174 194,170 C184,162 174,154 174,140 L174,110 C174,80 158,56 128,56 Z' StrokeThickness='1.5' Stroke='#80FFFFFF'>
      <Path.Fill><LinearGradientBrush StartPoint='0,0' EndPoint='1,1'><GradientStop Color='#FFFFFF' Offset='0'/><GradientStop Color='#D6E2FF' Offset='1'/></LinearGradientBrush></Path.Fill></Path>
    <Ellipse Canvas.Left='117' Canvas.Top='36' Width='22' Height='22' Fill='#F2F6FF'/>
    <Path Data='M 106,188 C 106,208 150,208 150,188 Z' Fill='#DCE6FF'/>
  </Canvas>
  <Path Tag='fine' Data='M 98,84 C 92,96 92,112 92,126' Stroke='#B0FFFFFF' StrokeThickness='7' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
  <Ellipse Canvas.Left='152' Canvas.Top='42' Width='58' Height='58' Stroke='#FFFFFFFF' StrokeThickness='5'><Ellipse.Fill>
    <LinearGradientBrush StartPoint='0,0' EndPoint='0,1'><GradientStop Color='#FF7A6B' Offset='0'/><GradientStop Color='#FF2D55' Offset='1'/></LinearGradientBrush></Ellipse.Fill>
    <Ellipse.Effect><DropShadowEffect BlurRadius='8' ShadowDepth='3' Direction='270' Opacity='0.35' Color='#000000'/></Ellipse.Effect></Ellipse>";
                break;

            case 3: body = @"
  <Rectangle Width='256' Height='256' Fill='#0B3B5C'/>
  <Canvas Opacity='0.92'><Canvas.RenderTransform><RotateTransform Angle='-32' CenterX='128' CenterY='128'/></Canvas.RenderTransform>" + Bands() + @"</Canvas>
  <Rectangle Width='256' Height='256'><Rectangle.Fill><LinearGradientBrush StartPoint='0,0' EndPoint='1,1'>
    <GradientStop Color='#40000000' Offset='0'/><GradientStop Color='#00000000' Offset='0.5'/><GradientStop Color='#55000000' Offset='1'/></LinearGradientBrush></Rectangle.Fill></Rectangle>
  <Path Data='" + Circle(128, 140, 74) + @"' Fill='#000000' Opacity='0.5'><Path.Effect><BlurEffect Radius='16'/></Path.Effect></Path>
  <Canvas Clip='" + Circle(128, 128, 74) + @"'>
    <Canvas><Canvas.RenderTransform><TransformGroup>
      <RotateTransform Angle='-32' CenterX='128' CenterY='128'/><ScaleTransform ScaleX='1.5' ScaleY='1.5' CenterX='128' CenterY='128'/><TranslateTransform X='-6' Y='-6'/>
    </TransformGroup></Canvas.RenderTransform>" + Bands() + @"</Canvas>
    <Ellipse Canvas.Left='54' Canvas.Top='54' Width='148' Height='148'><Ellipse.Fill><RadialGradientBrush>
      <GradientStop Color='#00FFFFFF' Offset='0'/><GradientStop Color='#00FFFFFF' Offset='0.62'/><GradientStop Color='#55FFFFFF' Offset='0.86'/><GradientStop Color='#EEFFFFFF' Offset='1'/></RadialGradientBrush></Ellipse.Fill></Ellipse>
    <Ellipse Canvas.Left='54' Canvas.Top='54' Width='148' Height='148'><Ellipse.Fill><LinearGradientBrush StartPoint='0,0' EndPoint='1,1'>
      <GradientStop Color='#50FFFFFF' Offset='0'/><GradientStop Color='#00FFFFFF' Offset='0.45'/><GradientStop Color='#30000000' Offset='1'/></LinearGradientBrush></Ellipse.Fill></Ellipse>
  </Canvas>
  <Path Data='" + Circle(128, 128, 73) + @"' Stroke='#E0FFFFFF' StrokeThickness='2.4' Fill='Transparent'/>
  <Path Tag='fine' Data='M 82,100 A 56,56 0 0 1 124,68' Stroke='#F2FFFFFF' StrokeThickness='6' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
  <Path Tag='fine' Data='M 176,168 A 56,56 0 0 1 150,190' Stroke='#90FFFFFF' StrokeThickness='4' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>";
                break;

            case 4: body = @"
  <Rectangle Width='256' Height='256'><Rectangle.Fill>
    <LinearGradientBrush StartPoint='0.2,0' EndPoint='0.8,1'>
      <GradientStop Color='#FFB347' Offset='0'/><GradientStop Color='#FF5A6A' Offset='0.5'/><GradientStop Color='#9B2FC4' Offset='1'/>
    </LinearGradientBrush></Rectangle.Fill></Rectangle>
  <Ellipse Canvas.Left='-40' Canvas.Top='-70' Width='250' Height='200'><Ellipse.Fill><RadialGradientBrush>
    <GradientStop Color='#66FFF1C2' Offset='0'/><GradientStop Color='#00FFF1C2' Offset='1'/></RadialGradientBrush></Ellipse.Fill></Ellipse>
  <Rectangle Canvas.Left='28' Canvas.Top='88' Width='200' Height='96' RadiusX='30' RadiusY='30'>
    <Rectangle.Fill><LinearGradientBrush StartPoint='0,0' EndPoint='0,1'><GradientStop Color='#FFFFFFFF' Offset='0'/><GradientStop Color='#F0F6F6FA' Offset='1'/></LinearGradientBrush></Rectangle.Fill>
    <Rectangle.Effect><DropShadowEffect BlurRadius='18' ShadowDepth='9' Direction='270' Opacity='0.38' Color='#3A0A30'/></Rectangle.Effect>
  </Rectangle>
  <Rectangle Canvas.Left='46' Canvas.Top='106' Width='60' Height='60' RadiusX='16' RadiusY='16'><Rectangle.Fill>
    <LinearGradientBrush StartPoint='0,0' EndPoint='1,1'><GradientStop Color='#5AD0FF' Offset='0'/><GradientStop Color='#0A74FF' Offset='1'/></LinearGradientBrush></Rectangle.Fill></Rectangle>
  <Ellipse Canvas.Left='64' Canvas.Top='124' Width='24' Height='24' Fill='#F5FFFFFF'/>
  <Rectangle Tag='fine' Canvas.Left='120' Canvas.Top='112' Width='86' Height='14' RadiusX='7' RadiusY='7' Fill='#D91C1C1E'/>
  <Rectangle Tag='fine' Canvas.Left='120' Canvas.Top='138' Width='62' Height='12' RadiusX='6' RadiusY='6' Fill='#5C3C3C43'/>
  <Ellipse Canvas.Left='176' Canvas.Top='52' Width='56' Height='56' StrokeThickness='5' Stroke='#FFFFFFFF'><Ellipse.Fill>
    <LinearGradientBrush StartPoint='0,0' EndPoint='0,1'><GradientStop Color='#FF6B5E' Offset='0'/><GradientStop Color='#FF2D3F' Offset='1'/></LinearGradientBrush></Ellipse.Fill>
    <Ellipse.Effect><DropShadowEffect BlurRadius='8' ShadowDepth='3' Direction='270' Opacity='0.35' Color='#000000'/></Ellipse.Effect></Ellipse>
  <TextBlock Tag='fine' Canvas.Left='176' Canvas.Top='52' Width='56' Height='56' TextAlignment='Center' Padding='0,8,0,0' FontFamily='Segoe UI' FontWeight='Bold' FontSize='30' Foreground='White' Text='1'/>";
                break;

            default: body = @"
  <Rectangle Width='256' Height='256'><Rectangle.Fill>
    <LinearGradientBrush StartPoint='0,0' EndPoint='0,1'><GradientStop Color='#3A3A3F' Offset='0'/><GradientStop Color='#0C0C0E' Offset='1'/></LinearGradientBrush></Rectangle.Fill></Rectangle>
  <Ellipse Canvas.Left='-40' Canvas.Top='-110' Width='340' Height='260'><Ellipse.Fill><RadialGradientBrush>
    <GradientStop Color='#44FFFFFF' Offset='0'/><GradientStop Color='#00FFFFFF' Offset='1'/></RadialGradientBrush></Ellipse.Fill></Ellipse>
  <Path Data='M 68,192 L 68,64 L 106,64 L 152,136 L 152,64 L 188,64 L 188,192 L 150,192 L 104,120 L 104,192 Z'
        StrokeThickness='12' StrokeLineJoin='Round' StrokeStartLineCap='Round' StrokeEndLineCap='Round'>
    <Path.Fill><LinearGradientBrush StartPoint='0,0' EndPoint='0,1'><GradientStop Color='#FFFFFFFF' Offset='0'/><GradientStop Color='#FFB8BCC8' Offset='1'/></LinearGradientBrush></Path.Fill>
    <Path.Stroke><LinearGradientBrush StartPoint='0,0' EndPoint='0,1'><GradientStop Color='#FFFFFFFF' Offset='0'/><GradientStop Color='#FFB8BCC8' Offset='1'/></LinearGradientBrush></Path.Stroke>
    <Path.Effect><DropShadowEffect BlurRadius='16' ShadowDepth='8' Direction='270' Opacity='0.6' Color='#000000'/></Path.Effect>
  </Path>
  <Path Tag='fine' Data='M 74,80 L 74,180' Stroke='#99FFFFFF' StrokeThickness='3' StrokeStartLineCap='Round' StrokeEndLineCap='Round'/>
  <Ellipse Canvas.Left='164' Canvas.Top='36' Width='56' Height='56' Stroke='#FF111113' StrokeThickness='7'><Ellipse.Fill>
    <LinearGradientBrush StartPoint='0,0' EndPoint='0,1'><GradientStop Color='#5CC8FF' Offset='0'/><GradientStop Color='#0A6CFF' Offset='1'/></LinearGradientBrush></Ellipse.Fill>
    <Ellipse.Effect><DropShadowEffect BlurRadius='18' ShadowDepth='0' Opacity='0.85' Color='#2E8BFF'/></Ellipse.Effect></Ellipse>";
                break;
        }
        return "<Canvas " + Ns + " Width='256' Height='256'><Canvas Clip='" + SQ + "' Width='256' Height='256'>" + body + "</Canvas></Canvas>";
    }

    static void HideFine(object node)
    {
        var fe = node as FrameworkElement;
        if (fe != null && "fine".Equals(fe.Tag)) fe.Visibility = Visibility.Collapsed;
        foreach (object c in LogicalTreeHelper.GetChildren((DependencyObject)node))
            if (c is DependencyObject) HideFine(c);
    }

    static FrameworkElement Build(int n, int px)
    {
        var canvas = (Canvas)XamlReader.Parse(Xaml(n));
        if (px <= 40) HideFine(canvas);
        var box = new Viewbox { Width = px, Height = px, Stretch = Stretch.Uniform, Child = canvas };
        return box;
    }

    static BitmapSource Render(FrameworkElement el, int w, int h)
    {
        el.Measure(new Size(w, h));
        el.Arrange(new Rect(0, 0, w, h));
        el.UpdateLayout();
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(el);
        rtb.Freeze();
        return rtb;
    }

    static void SavePng(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) enc.Save(fs);
    }

    static TextBlock Label(string text, double size, string color, FontWeight weight)
    {
        return new TextBlock
        {
            Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = size, FontWeight = weight,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)
        };
    }

    static void Sheet(string outDir)
    {
        Directory.CreateDirectory(outDir);
        for (int i = 1; i <= 5; i++) SavePng(Render(Build(i, 512), 512, 512), Path.Combine(outDir, "icon" + i + ".png"));

        const int colW = 330, left = 30, top = 24, big = 280;
        int W = left * 2 + colW * 5 - 50, H = 520;
        var root = new Canvas { Width = W, Height = H };
        root.Background = new LinearGradientBrush(Color.FromRgb(0x2A, 0x2D, 0x3A), Color.FromRgb(0x14, 0x15, 0x1C), 90);

        for (int i = 1; i <= 5; i++)
        {
            double x = left + (i - 1) * colW;
            var num = Label(i.ToString(), 26, "#FFFFFF", FontWeights.Bold);
            Canvas.SetLeft(num, x); Canvas.SetTop(num, top);
            root.Children.Add(num);
            var name = Label(Names[i - 1], 18, "#E8E8EE", FontWeights.SemiBold);
            Canvas.SetLeft(name, x + 28); Canvas.SetTop(name, top + 5);
            root.Children.Add(name);

            var img = new System.Windows.Controls.Image { Source = Render(Build(i, 512), 512, 512), Width = big, Height = big };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            img.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 10, Direction = 270, Opacity = 0.5, Color = Colors.Black };
            Canvas.SetLeft(img, x); Canvas.SetTop(img, top + 48);
            root.Children.Add(img);

            // real pixel sizes, as Windows shows them: tray/taskbar (dark) and Explorer (light)
            double py = top + 48 + big + 26;
            var darkPlate = new System.Windows.Shapes.Rectangle { Width = 150, Height = 76, RadiusX = 10, RadiusY = 10, Fill = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)) };
            var lightPlate = new System.Windows.Shapes.Rectangle { Width = 150, Height = 76, RadiusX = 10, RadiusY = 10, Fill = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF6)) };
            Canvas.SetLeft(darkPlate, x); Canvas.SetTop(darkPlate, py);
            Canvas.SetLeft(lightPlate, x + 160); Canvas.SetTop(lightPlate, py);
            root.Children.Add(darkPlate); root.Children.Add(lightPlate);
            int[] sizes = { 48, 32, 24, 16 };
            double sx = 10;
            foreach (int s in sizes)
            {
                var small = new System.Windows.Controls.Image { Source = Render(Build(i, s), s, s), Width = s, Height = s, Stretch = Stretch.None };
                RenderOptions.SetBitmapScalingMode(small, BitmapScalingMode.NearestNeighbor);
                Canvas.SetLeft(small, x + sx); Canvas.SetTop(small, py + 38 - s / 2.0 + (s == 48 ? -2 : 0));
                root.Children.Add(small);
                var small2 = new System.Windows.Controls.Image { Source = Render(Build(i, s), s, s), Width = s, Height = s, Stretch = Stretch.None };
                RenderOptions.SetBitmapScalingMode(small2, BitmapScalingMode.NearestNeighbor);
                Canvas.SetLeft(small2, x + 160 + sx); Canvas.SetTop(small2, py + 38 - s / 2.0 + (s == 48 ? -2 : 0));
                root.Children.Add(small2);
                sx += s + 5;
            }
        }
        var note = Label("Bottom: the real sizes (48, 32, 24, 16 px) on a dark taskbar and on a light File Explorer background", 13, "#9A9CAB", FontWeights.Normal);
        Canvas.SetLeft(note, left); Canvas.SetTop(note, H - 30);
        root.Children.Add(note);

        SavePng(Render(root, W, H), Path.Combine(outDir, "sheet.png"));
        Console.WriteLine("sheet: " + W + "x" + H);
    }

    // .ico with PNG-compressed frames
    static void Ico(int n, string path)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var frames = new List<byte[]>();
        foreach (int s in sizes)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Render(Build(n, s), s, s)));
            using (var ms = new MemoryStream()) { enc.Save(ms); frames.Add(ms.ToArray()); }
        }
        using (var bw = new BinaryWriter(File.Create(path)))
        {
            bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); bw.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                bw.Write((byte)0); bw.Write((byte)0); bw.Write((short)1); bw.Write((short)32);
                bw.Write(frames[i].Length); bw.Write(offset);
                offset += frames[i].Length;
            }
            foreach (var f in frames) bw.Write(f);
        }
        Console.WriteLine("ico: " + path);
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "sheet") Sheet(args[1]);
            else if (args.Length >= 3 && args[0] == "ico") Ico(int.Parse(args[1]), args[2]);
            else { Console.WriteLine("usage: IconLab sheet <dir> | ico <n> <file>"); return 1; }
            return 0;
        }
        catch (Exception e) { Console.WriteLine(e); return 2; }
    }
}
