// GpuProbe: checks the pieces of the GPU engine on this PC before the engine is built on them.
// A dark glass-like panel at the top right of the screen shows what is behind it, captured with Desktop Duplication
// and drawn with Direct3D 11 through DirectComposition, never leaving the GPU. It reports:
//   - whether the panel is left out of the desktop capture (otherwise the glass would see itself),
//   - whether clicks pass through it,
//   - frames per second in step with the screen, and as fast as it can go, with the CPU it costs.
// Build: csc /platform:x64 /r:System.Windows.Forms.dll /r:System.Drawing.dll tools\GpuProbe.cs src\Dx.cs
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace GlassBell
{
    static class GpuProbe
    {
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

        // a window with no redirection surface (DirectComposition draws it), on top, never active, clicks pass through
        sealed class Panel : Form
        {
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.Style = unchecked((int)0x80000000);   // WS_POPUP
                    cp.ExStyle |= 0x00200000 /* NOREDIRECTIONBITMAP */ | 0x8 /* TOPMOST */ | 0x80 /* TOOLWINDOW */ | 0x08000000 /* NOACTIVATE */
                        | 0x20 /* TRANSPARENT */ | 0x80000 /* LAYERED */;
                    return cp;
                }
            }
        }

        const string Shaders = @"
Texture2D back : register(t0);
SamplerState lin : register(s0);
cbuffer C : register(b0) { float4 size; float4 tint; };   // size: panel w, h (px), corner radius; tint rgb, alpha
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V vs(uint id : SV_VertexID)
{
    V o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}
float4 ps(V i) : SV_Target
{
    float2 p = i.uv * size.xy - size.xy * 0.5;
    float2 q = abs(p) - (size.xy * 0.5 - size.z);
    float d = length(max(q, 0)) + min(max(q.x, q.y), 0) - size.z;   // < 0 inside the rounded rectangle
    float a = saturate(0.5 - d);
    float3 c = lerp(back.Sample(lin, i.uv).rgb, tint.rgb, tint.a);
    return float4(c * a, a);   // premultiplied
}";

        [STAThread]
        static int Main(string[] args)
        {
            SetProcessDpiAwarenessContext((IntPtr)(-4));   // per-monitor v2: pixels are pixels
            int W = 420, H = 520;
            var screen = Screen.PrimaryScreen.Bounds;
            int X = screen.Right - W - 20, Y = 20;

            IntPtr device, ctx, output;
            string adapter;
            Dx.CreateDevice(out device, out ctx, out output, out adapter);
            Console.WriteLine("adapter: " + adapter + ", panel " + W + "x" + H + " at " + X + "," + Y + " on a " + screen.Width + "x" + screen.Height + " screen");

            var panel = new Panel { StartPosition = FormStartPosition.Manual, Left = X, Top = Y, Width = W, Height = H, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None };
            panel.Show();
            IntPtr hwnd = panel.Handle;
            SetLayeredWindowAttributes(hwnd, 0, 255, 2);
            bool excludedSet = SetWindowDisplayAffinity(hwnd, 0x11);   // WDA_EXCLUDEFROMCAPTURE

            IntPtr chain = Dx.CompositionSwapChain(device, (uint)W, (uint)H);
            IntPtr target;
            IntPtr dcomp = Dx.Compose(device, hwnd, chain, out target);
            IntPtr waiter = Dx.FrameWaiter(chain);
            IntPtr backbuf = Dx.BackBuffer(chain), rtv = Dx.TargetView(device, backbuf);
            IntPtr backTex = Dx.Texture(device, (uint)W, (uint)H, Dx.BindShaderResource, 0, 0), backSrv = Dx.ShaderView(device, backTex);
            IntPtr staging = Dx.Texture(device, 1, 1, 0, 3 /* STAGING */, 0x20000 /* CPU READ */);
            IntPtr vs = Dx.CompileShader(device, Shaders, "vs", "vs_5_0"), ps = Dx.CompileShader(device, Shaders, "ps", "ps_5_0");
            IntPtr sampler = Dx.LinearClampSampler(device), cb = Dx.ConstantBuffer(device, 32);
            IntPtr dup = Dx.Duplicate(output, device);
            IntPtr info = Marshal.AllocHGlobal(48);

            var constants = new float[8];
            var cbData = Marshal.AllocHGlobal(32);
            Action<float, float, float, float> setTint = delegate(float r, float g, float b, float a)
            {
                constants[0] = W; constants[1] = H; constants[2] = 26; constants[3] = 0;
                constants[4] = r; constants[5] = g; constants[6] = b; constants[7] = a;
                Marshal.Copy(constants, 0, cbData, 8);
                Dx.Update(ctx, cb, cbData, 0);
            };

            int newFrames = 0;
            Func<bool> pull = delegate
            {
                IntPtr res;
                int hr = Dx.Acquire(dup, 0, info, out res);
                if (hr < 0) return false;   // no new desktop image (or access lost)
                try
                {
                    IntPtr desk = Dx.Query(res, Dx.IID_Texture2D);
                    Dx.CopyRegion(ctx, backTex, 0, 0, desk, new Dx.Box { Left = (uint)X, Top = (uint)Y, Right = (uint)(X + W), Bottom = (uint)(Y + H), Back = 1 });
                    Marshal.Release(desk);
                    newFrames++;
                    return true;
                }
                finally { Marshal.Release(res); Dx.ReleaseFrame(dup); }
            };
            Action draw = delegate
            {
                Dx.SetTarget(ctx, rtv, W, H);
                Dx.Clear(ctx, rtv, 0, 0, 0, 0);
                Dx.Triangles(ctx);
                Dx.VertexShader(ctx, vs);
                Dx.PixelShader(ctx, ps);
                Dx.Resources(ctx, backSrv);
                Dx.Samplers(ctx, sampler);
                Dx.PixelConstants(ctx, cb);
                Dx.Draw(ctx, 3);
            };
            Func<int, int, uint> deskPixel = delegate(int px, int py)
            {
                // the desktop image at one point, read back once (only for the self-capture check)
                IntPtr res;
                for (int tries = 0; tries < 50; tries++)
                {
                    if (Dx.Acquire(dup, 100, info, out res) < 0) continue;
                    try
                    {
                        IntPtr desk = Dx.Query(res, Dx.IID_Texture2D);
                        Dx.CopyRegion(ctx, staging, 0, 0, desk, new Dx.Box { Left = (uint)px, Top = (uint)py, Right = (uint)px + 1, Bottom = (uint)py + 1, Back = 1 });
                        Marshal.Release(desk);
                    }
                    finally { Marshal.Release(res); Dx.ReleaseFrame(dup); }
                    var m = Dx.Map(ctx, staging);
                    uint v = (uint)Marshal.ReadInt32(m.Data);
                    Dx.Unmap(ctx, staging);
                    return v;
                }
                return 0;
            };

            var self = Process.GetCurrentProcess();
            Func<int, uint, bool, string> run = delegate(int seconds, uint sync, bool wait)
            {
                int presents = 0; newFrames = 0;
                self.Refresh(); var cpu0 = self.TotalProcessorTime; var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < seconds * 1000)
                {
                    if (wait) WaitForSingleObject(waiter, 1000);
                    pull();
                    draw();
                    Dx.Present(chain, sync);
                    presents++;
                    Application.DoEvents();
                }
                self.Refresh();
                double cpu = 100 * (self.TotalProcessorTime - cpu0).TotalMilliseconds / sw.ElapsedMilliseconds;
                return string.Format("{0,6:N0} frames/s drawn, {1,5:N0} new desktop images/s, CPU {2,5:N1}% of one core",
                    presents * 1000.0 / sw.ElapsedMilliseconds, newFrames * 1000.0 / sw.ElapsedMilliseconds, cpu);
            };

            try
            {
                // 1. self-capture: paint the panel pure magenta, then look at the desktop image under its centre
                setTint(1, 0, 1, 1);
                for (int i = 0; i < 10; i++) { WaitForSingleObject(waiter, 1000); draw(); Dx.Present(chain, 1); Application.DoEvents(); }
                Thread.Sleep(200);
                uint under = deskPixel(X + W / 2, Y + H / 2);
                bool seesItself = (under & 0x00FFFFFF) == 0x00FF00FF;
                Console.WriteLine("left out of the capture: " + (excludedSet && !seesItself) + " (affinity set " + excludedSet + ", desktop under the centre 0x" + (under & 0xFFFFFF).ToString("X6") + ")");

                // 2. clicks: the window under the panel's centre must be some other window
                IntPtr hit = WindowFromPoint(new POINT { X = X + W / 2, Y = Y + H / 2 });
                Console.WriteLine("clicks pass through: " + (hit != hwnd));

                // 3. speed: dark glass over the live desktop
                setTint(0.11f, 0.11f, 0.12f, 0.55f);
                Console.WriteLine("in step with the screen: " + run(5, 1, true));
                Console.WriteLine("as fast as it goes:      " + run(5, 0, false));
            }
            finally
            {
                panel.Close();
                Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(cbData);
                foreach (var p in new[] { dup, cb, sampler, ps, vs, staging, backSrv, backTex, rtv, backbuf, dcomp, chain, output, ctx, device })
                    if (p != IntPtr.Zero) Marshal.Release(p);
            }
            return 0;
        }
    }
}
