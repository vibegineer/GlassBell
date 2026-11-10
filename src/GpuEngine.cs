// GpuEngine: banners drawn by the graphics card from capture to screen (Settings.Engine = "gpu").
//
// The WPF renderer reads the screen back into memory (GDI), blurs or uploads it again and lets WPF draw transparent
// layered windows, which Windows also reads back on every frame. All that is waiting on copies, and it is why the
// live glass could not go much past 60 a second. Here nothing leaves the GPU:
//   - Desktop Duplication hands over each new desktop image as a texture; the strip of the screen where the
//     banners stand (the column) is copied out of it and blurred, on the GPU;
//   - the lens of the WPF renderer, the same HLSL, reads that blurred column where each card is right now;
//   - the shadows are drawn analytically, the card's text and icon are a texture WPF renders once;
//   - one click-through window (no redirection surface) shows it all through DirectComposition.
// The banner windows of WPF stay as they are, only hollow: they take the clicks and the hover and keep the stack
// logic, while the engine draws the cards where those windows are, with the same curves (it evaluates them itself,
// on its own frames, so a move is as smooth as the screen is fast). A live glass follows the desktop on every
// frame the desktop changes under the column; without the live background the column is captured again only when
// a banner arrives somewhere.
// One thread does all of it: the device context of Direct3D 11 is not shared between threads.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace GlassBell
{
    // what the UI thread hands over for one banner (all in px of the screen)
    sealed class GpuCardSpec
    {
        public int CardW, CardH;
        public byte[] Light, Dark;            // the card's text and icon, premultiplied BGRA, light or dark text
        public float[] Lens = new float[20];  // c0..c4 of the lens, exactly as the WPF effect gets them
        public int Margin, Quality;
        public float Sigma, RadiusPx, Inset;  // backdrop blur, corner radius, how far inside the card the shadow starts
        public float BlurA, DepthA, OpA, BlurB, DepthB, OpB;   // the two shadow layers, before they adapt to the backdrop
        public bool TwoShadows, Live, Glass;
        public float LumTint, TintAlpha, K;   // for the text colour, as GlassCard.Bake works it out
    }

    static class GpuEngine
    {
        public static volatile bool Failed;   // could not start (or broke): banners use the WPF renderer
        public static readonly Stopwatch Clock = Stopwatch.StartNew();
        public static double Ms { get { return Clock.Elapsed.TotalMilliseconds; } }

        static Thread thread;
        static readonly ConcurrentQueue<Action> todo = new ConcurrentQueue<Action>();
        static readonly AutoResetEvent wake = new AutoResetEvent(false);
        static readonly ManualResetEvent ready = new ManualResetEvent(false);
        static int lastId;

        // the column: where the banners can be, in screen px, and where a card stands when it is not sliding (DIP)
        static int colX, colY, colW, colH;
        static double scale, baseXDip;

        // ---- the UI thread's side ----

        // starts the engine (once); false when this PC cannot run it, then the caller uses the WPF renderer.
        // wait = false: at startup, so the first banner finds the engine (and its compiled shaders) ready
        // raised on the render thread when the engine stops for good while running (the device was lost, ...)
        public static event Action Stopped;

        public static bool Start(int x, int y, int w, int h, double screenScale, double cardXDip, bool wait = true)
        {
            if (Failed) return false;
            if (thread == null)
            {
                colX = x; colY = y; colW = w; colH = h; scale = screenScale; baseXDip = cardXDip;
                thread = new Thread(Run) { IsBackground = true, Name = "GlassBell GPU" };
                thread.Start();
                if (!wait) return true;
            }
            if (!ready.WaitOne(3000)) { Log.Write("gpu engine: no answer from the render thread"); Failed = true; return false; }
            if (Failed) return false;
            if (x != colX || y != colY || w != colW || h != colH || screenScale != scale || cardXDip != baseXDip)
                Do(delegate { Reshape(x, y, w, h, screenScale, cardXDip); });
            return !Failed;
        }

        public static int Add(GpuCardSpec spec, double topDip)
        {
            int id = Interlocked.Increment(ref lastId);
            Do(delegate { AddCard(id, spec, topDip); });
            return id;
        }

        // what: 'x' how far right of its place the card is (DIP), 'y' the top of the card (DIP), 'o' its opacity.
        // from = NaN: from wherever it is right now
        public static void Move(int id, char what, double from, double to, double ms, int ease, double delayMs)
        {
            double t0 = Ms + delayMs;
            Do(delegate { Card c = Find(id); if (c != null) c.Track(what).Start(from, to, t0, ms, ease); });
        }

        public static void Set(int id, char what, double v)
        {
            Do(delegate { Card c = Find(id); if (c != null) c.Track(what).Jump(v); });
        }

        // the card goes once its moves are over
        public static void Remove(int id) { Do(delegate { Card c = Find(id); if (c != null) c.Leaving = true; }); }

        // without the live background: capture the column again (a banner has arrived somewhere)
        public static void Refresh() { Do(delegate { pullWanted = true; }); }

        static void Do(Action a) { todo.Enqueue(a); wake.Set(); }

        // ---- moves ----

        public const int Linear = 0, EaseOut = 1, EaseIn = 2;

        sealed class Track
        {
            double from, to, t0, ms = 0, value;
            int ease;
            public void Jump(double v) { value = from = to = v; ms = 0; }
            public void Start(double f, double t, double start, double duration, int e)
            {
                value = At(Ms);
                from = double.IsNaN(f) ? value : f;
                to = t; t0 = start; ms = duration; ease = e;
            }
            public double At(double now)
            {
                if (ms <= 0 || now >= t0 + ms) return value = to;
                if (now <= t0) return value = from;
                double u = (now - t0) / ms;
                if (ease == EaseOut) u = 1 - (1 - u) * (1 - u) * (1 - u);   // WPF's CubicEase
                else if (ease == EaseIn) u = u * u * u;
                return value = from + (to - from) * u;
            }
            public bool Busy(double now) { return ms > 0 && now < t0 + ms; }
        }

        sealed class Card
        {
            public int Id;
            public GpuCardSpec Spec;
            public IntPtr LightTex, LightSrv, DarkTex, DarkSrv, MeanTex, MeanRtv;
            public IntPtr[] Staging = new IntPtr[3];
            public int StagingNext, StagingFull;
            public double Lum = -1, LumAt;
            public long LumSerial = -1;   // the capture its brightness was last measured on
            public double Added = Ms;
            public bool DarkText, Leaving;
            public readonly Track X = new Track(), Y = new Track(), O = new Track();
            public Track Track(char w) { return w == 'x' ? X : w == 'y' ? Y : O; }
            public bool Moving(double now) { return X.Busy(now) || Y.Busy(now) || O.Busy(now); }
        }

        static readonly List<Card> cards = new List<Card>();
        static Card Find(int id) { return cards.FirstOrDefault(c => c.Id == id); }

        // ---- the render thread ----

        static IntPtr device, ctx, output, dup, chain, dcomp, waiter, backBuf, rtv;
        static IntPtr deskTex, deskSrv, blurTmp, blurTmpRtv, blurTmpSrv, blurOut, blurOutRtv, blurOutSrv;
        static IntPtr vs, psBlur, psShadow, psContent, psMean, sampler, blend, cb;
        static readonly IntPtr[] psLens = new IntPtr[4];
        static IntPtr hwnd, frameInfo, rectBuf, cbData;
        static uint rectBufSize;
        static int blurDown = -1, blurW, blurH;
        static bool shown, pullWanted, haveDesktop;
        static double idleSince, dupRetryAt;
        static int dupFails;
        static long totalFrames;
        // GLASSBELL_GPUFAIL=start | run (a test setting): the engine fails to start, or stops after a moment of drawing
        static readonly string FailTest = Environment.GetEnvironmentVariable("GLASSBELL_GPUFAIL");
        static WndProcFn wndProc;   // kept: the window calls it
        static string adapterName;
        static long frames, captures, captureSerial;
        static double statsAt;
        static readonly string TraceFile = Path.Combine(Program.DataDir, "trace.flag");
        static string dirtyTrace = "";

        // GLASSBELL_GPUDUMP=file.png (a test setting): once the first card has settled, what the engine drew over the
        // desktop it captured under the column is saved, since screenshots cannot see the engine's window
        static readonly string DumpPath = Environment.GetEnvironmentVariable("GLASSBELL_GPUDUMP");
        static bool dumped;

        static void Dump()
        {
            try
            {
                IntPtr sb = Dx.Texture(device, (uint)colW, (uint)colH, 0, 3, 0x20000), sd = Dx.Texture(device, (uint)colW, (uint)colH, 0, 3, 0x20000);
                Dx.CopyAll(ctx, sb, backBuf);
                Dx.CopyAll(ctx, sd, deskTex);
                var mb = Dx.Map(ctx, sb);
                var md = Dx.Map(ctx, sd);
                var bmp = new System.Drawing.Bitmap(colW, colH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var bits = bmp.LockBits(new System.Drawing.Rectangle(0, 0, colW, colH), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                var row = new byte[colW * 4];
                var under = new byte[colW * 4];
                for (int yy = 0; yy < colH; yy++)
                {
                    Marshal.Copy(IntPtr.Add(mb.Data, yy * (int)mb.RowPitch), row, 0, row.Length);
                    Marshal.Copy(IntPtr.Add(md.Data, yy * (int)md.RowPitch), under, 0, under.Length);
                    for (int i = 0; i < row.Length; i += 4)
                    {
                        int a = row[i + 3];   // premultiplied over the desktop
                        for (int ch = 0; ch < 3; ch++) row[i + ch] = (byte)Math.Min(255, row[i + ch] + under[i + ch] * (255 - a) / 255);
                        row[i + 3] = 255;
                    }
                    Marshal.Copy(row, 0, IntPtr.Add(bits.Scan0, yy * bits.Stride), row.Length);
                }
                bmp.UnlockBits(bits);
                Dx.Unmap(ctx, sb); Dx.Unmap(ctx, sd);
                Dx.Release(ref sb); Dx.Release(ref sd);
                bmp.Save(DumpPath, System.Drawing.Imaging.ImageFormat.Png);
                bmp.Dispose();
                Log.Write("gpu engine: dump saved to " + DumpPath);
            }
            catch (Exception e) { Log.Write("gpu engine: dump: " + e.Message); }
        }

        static void Run()
        {
            try
            {
                if (FailTest == "start") throw new InvalidOperationException("GLASSBELL_GPUFAIL=start");
                Init();
                Log.Write("gpu engine: " + adapterName + ", column " + colW + "x" + colH + " px at " + colX + "," + colY + ", thread " + GetCurrentThreadId());
            }
            catch (Exception e)
            {
                Log.Write("gpu engine: cannot start, the WPF renderer takes over: " + e.Message);
                Failed = true;
                ready.Set();
                return;
            }
            ready.Set();
            try { Loop(); }
            catch (Exception e)
            {
                Log.Write("gpu engine: stopped, the WPF renderer takes over: " + e);
                Failed = true;
                try { if (hwnd != IntPtr.Zero) ShowWindow(hwnd, 0); } catch { }
                // the cards it drew are gone with it: their hollow windows must not stay behind
                var stopped = Stopped;
                if (stopped != null) try { stopped(); } catch { }
            }
        }

        static void Loop()
        {
            while (true)
            {
                Pump();
                Action a;
                while (todo.TryDequeue(out a)) a();
                double now = Ms;
                for (int i = cards.Count - 1; i >= 0; i--)
                    if (cards[i].Leaving && !cards[i].Moving(now)) { Free(cards[i]); cards.RemoveAt(i); }

                if (cards.Count == 0)
                {
                    if (shown) { ShowWindow(hwnd, 0); shown = false; idleSince = now; }
                    // the duplication keeps the compositor busy for us: let it go after a while without banners
                    if (dup != IntPtr.Zero && now - idleSince > 3000) { Dx.Release(ref dup); haveDesktop = false; }
                    wake.WaitOne(500);
                    continue;
                }
                if (!shown) { ShowWindow(hwnd, 4 /* SHOWNOACTIVATE */); shown = true; }
                // the duplication is lost on a mode change and is refused while the secure desktop is up (the lock
                // screen, a UAC prompt): asked for again twice a second, the cards keep the last picture meanwhile
                if (dup == IntPtr.Zero && now >= dupRetryAt)
                {
                    try { dup = Dx.Duplicate(output, device); if (dupFails > 0) Log.Write("gpu engine: desktop duplication back after " + dupFails + " attempt(s)"); dupFails = 0; }
                    catch (Exception e)
                    {
                        dupRetryAt = now + 500;
                        if (dupFails++ == 0) Log.Write("gpu engine: no desktop duplication for now: " + e.Message);
                    }
                    haveDesktop = false;
                }

                bool moving = cards.Any(c => c.Moving(now));
                bool live = cards.Any(c => c.Spec.Live);
                bool fresh = false;
                // the test dump waits for a frame once the card has settled, so it gets one even when nothing changes
                bool due = LumDue(now) || (DumpPath != null && !dumped && now - cards[cards.Count - 1].Added > 900);
                if (dup == IntPtr.Zero) { if (moving) WaitForSingleObject(waiter, 100); else if (!due) { wake.WaitOne(100); continue; } }
                else if (!haveDesktop) fresh = Pull(100);
                else if (moving) { WaitForSingleObject(waiter, 100); if (live || pullWanted) fresh = Pull(0); }
                else if (live) { fresh = Pull(16); if (!fresh && !due) continue; }
                else if (pullWanted) fresh = Pull(50);
                else { if (!due) { wake.WaitOne(100); continue; } }
                if (fresh) { pullWanted = false; Blur(); }

                Draw(Ms);
                int hr = Dx.Present(chain, 1);
                if (hr < 0) throw new InvalidOperationException("Present failed: 0x" + hr.ToString("X8"));
                frames++;
                if (FailTest == "run" && ++totalFrames == 90) throw new InvalidOperationException("GLASSBELL_GPUFAIL=run");
                ReadLuminance(Ms);
                if (DumpPath != null && !dumped && cards.Count > 0 && Ms - cards[cards.Count - 1].Added > 900) { dumped = true; Dump(); }
                if (Ms - statsAt >= 5000 && File.Exists(TraceFile))
                {
                    Log.Write("gpu engine: " + (frames * 1000.0 / (Ms - statsAt)).ToString("0") + " frames/s, " +
                        (captures * 1000.0 / (Ms - statsAt)).ToString("0") + " captures/s, " + cards.Count + " card(s); changed under the column: " + dirtyTrace);
                    frames = 0; captures = 0; statsAt = Ms; dirtyTrace = "";
                }
            }
        }

        // the backdrop behind a card changed since its brightness was last measured (or the card moved)
        static bool LumDue(double now) { return cards.Any(c => now - c.LumAt > 150 && (c.LumSerial != captureSerial || c.Moving(now))); }

        // the newest desktop image, if the column changed (or nothing was there yet); waits up to timeout ms for one
        static bool Pull(uint timeout)
        {
            IntPtr res;
            int hr = Dx.Acquire(dup, timeout, frameInfo, out res);
            if (hr == unchecked((int)0x887A0027)) return false;                    // WAIT_TIMEOUT: nothing new
            if (hr == unchecked((int)0x887A0026)) { Dx.Release(ref dup); return false; }   // ACCESS_LOST: mode change, secure desktop
            if (hr < 0) { Dx.Release(ref dup); return false; }
            try
            {
                if (haveDesktop && !ColumnChanged()) return false;   // the pointer moved, or something elsewhere (or our own frame)
                IntPtr desk = Dx.Query(res, Dx.IID_Texture2D);
                try
                {
                    Dx.CopyRegion(ctx, deskTex, 0, 0, desk, new Dx.Box { Left = (uint)colX, Top = (uint)colY, Right = (uint)(colX + colW), Bottom = (uint)(colY + colH), Back = 1 });
                }
                finally { Marshal.Release(desk); }
                haveDesktop = true;
                captures++;
                captureSerial++;
                return true;
            }
            finally { Marshal.Release(res); Dx.ReleaseFrame(dup); }
        }

        static bool ColumnChanged()
        {
            // AccumulatedFrames (offset 16) is 0 when only the pointer moved
            if (Marshal.ReadInt32(frameInfo, 16) == 0) return false;
            bool hit = false;
            foreach (bool move in new[] { true, false })
            {
                int[] r = Dx.ChangedRects(dup, move, ref rectBuf, ref rectBufSize);
                if (r == null)
                {
                    if (dirtyTrace.Length < 400) dirtyTrace += "(rects failed 0x" + Dx.LastRectsHr.ToString("X8") + ")";
                    return true;   // Windows did not say what changed: assume the column did
                }
                for (int i = 0; i + 3 < r.Length; i += 4)
                    if (r[i] < colX + colW && r[i + 2] > colX && r[i + 1] < colY + colH && r[i + 3] > colY)
                    {
                        hit = true;
                        if (dirtyTrace.Length < 400) dirtyTrace += (move ? "m" : "") + "[" + r[i] + "," + r[i + 1] + "," + r[i + 2] + "," + r[i + 3] + "]";
                    }
            }
            return hit;
        }

        // ---- drawing ----

        static readonly float[] consts = new float[40];

        static void Constants()
        {
            Marshal.Copy(consts, 0, cbData, consts.Length);
            Dx.Update(ctx, cb, cbData, 0);
        }

        static void Viewport(float x, float y, float w, float h)
        {
            var vp = new Dx.Viewport { X = x, Y = y, W = w, H = h, MaxDepth = 1 };
            Dx.M<Dx.Fn_V_Viewport>(ctx, 44)(ctx, 1, ref vp);
        }

        // the column blurred (as much as the most blurred card wants), at a reduced size for big blurs
        static void Blur()
        {
            float sigma = cards.Count > 0 ? cards.Max(c => c.Spec.Sigma) : 0;
            int down = sigma > 16 ? 4 : sigma > 8 ? 2 : 1;
            if (down != blurDown) MakeBlurTargets(down);
            Dx.Blend(ctx, IntPtr.Zero);
            Dx.VertexShader(ctx, vs);
            Dx.Triangles(ctx);
            Dx.PixelShader(ctx, psBlur);
            Dx.Samplers(ctx, sampler);
            Dx.PixelConstants(ctx, cb);
            float step = down, taps = sigma < 0.3f ? 0 : (float)Math.Ceiling(3 * sigma / step);
            // across: the full column into one row of reduced width
            Dx.Resources(ctx, IntPtr.Zero);
            Dx.M<Dx.Fn_V_URP>(ctx, 33)(ctx, 1, new[] { blurTmpRtv }, IntPtr.Zero);
            Viewport(0, 0, blurW, colH);
            Array.Clear(consts, 0, consts.Length);
            consts[20] = 1f / colW; consts[21] = 1f / colH; consts[22] = 1; consts[23] = 0;
            consts[24] = Math.Max(sigma, 0.3f); consts[25] = step; consts[26] = taps;
            Constants();
            Dx.Resources(ctx, deskSrv);
            Dx.Draw(ctx, 3);
            // down: into the reduced column
            Dx.Resources(ctx, IntPtr.Zero);
            Dx.M<Dx.Fn_V_URP>(ctx, 33)(ctx, 1, new[] { blurOutRtv }, IntPtr.Zero);
            Viewport(0, 0, blurW, blurH);
            consts[20] = 1f / blurW; consts[21] = 1f / colH; consts[22] = 0; consts[23] = 1;
            Constants();
            Dx.Resources(ctx, blurTmpSrv);
            Dx.Draw(ctx, 3);
            Dx.Resources(ctx, IntPtr.Zero);
        }

        static void Draw(double now)
        {
            Dx.M<Dx.Fn_V_URP>(ctx, 33)(ctx, 1, new[] { rtv }, IntPtr.Zero);
            Dx.Clear(ctx, rtv, 0, 0, 0, 0);
            Dx.Blend(ctx, blend);
            Dx.VertexShader(ctx, vs);
            Dx.Triangles(ctx);
            Dx.Samplers(ctx, sampler);
            Dx.PixelConstants(ctx, cb);
            // the oldest first: the newest card is on top when two meet in a shift
            for (int i = cards.Count - 1; i >= 0; i--) DrawCard(cards[i], now);
            Dx.Resources(ctx, IntPtr.Zero);
        }

        static void DrawCard(Card c, double now)
        {
            var s = c.Spec;
            float x = (float)((baseXDip + c.X.At(now)) * scale - colX), y = (float)(c.Y.At(now) * scale - colY);
            float o = (float)Math.Max(0, Math.Min(1, c.O.At(now)));
            if (o <= 0.002f) return;
            double lum = c.Lum >= 0 ? c.Lum : 0.5;
            float adapt = (float)Math.Max(0.45, Math.Min(1.2, 1.2 - 0.6 * lum));

            // 1. the shadow, under everything
            float opA = s.OpA * adapt, opB = s.OpB * adapt;
            if (!s.TwoShadows) { opA = 1 - (1 - opA) * (1 - opB); opB = 0; }
            if (opA > 0.003f || opB > 0.003f)
            {
                float ext = 3 * Math.Max(s.BlurA, s.BlurB) / 2.5f + Math.Max(s.DepthA, s.DepthB) + 2;
                Array.Clear(consts, 0, consts.Length);
                float vx = x - ext, vy = y - ext, vw = s.CardW + 2 * ext, vh = s.CardH + 2 * ext;
                consts[0] = vw; consts[1] = vh;
                consts[20] = vx; consts[21] = vy; consts[22] = colW; consts[23] = colH;
                consts[24] = o;
                consts[28] = s.BlurA / 2.5f; consts[29] = s.DepthA; consts[30] = opA; consts[31] = s.BlurB / 2.5f;
                consts[32] = s.DepthB; consts[33] = opB; consts[34] = s.RadiusPx; consts[35] = s.Inset;
                consts[36] = x; consts[37] = y; consts[38] = s.CardW; consts[39] = s.CardH;
                Constants();
                Viewport(vx, vy, vw, vh);
                Dx.PixelShader(ctx, psShadow);
                Dx.Draw(ctx, 3);
            }

            // 2. the glass: the lens over the blurred column, where the card is now
            Array.Copy(s.Lens, 0, consts, 0, 20);
            float m = s.Margin;
            consts[20] = x - m; consts[21] = y - m; consts[22] = colW; consts[23] = colH;
            consts[24] = o;
            Constants();
            Viewport(x - m, y - m, s.CardW + 2 * m, s.CardH + 2 * m);
            Dx.PixelShader(ctx, psLens[Math.Max(0, Math.Min(3, s.Quality))]);
            Dx.Resources(ctx, blurOutSrv);
            Dx.Draw(ctx, 3);

            // 3. the text and the icon
            consts[24] = o;
            Constants();
            Viewport(x, y, s.CardW, s.CardH);
            Dx.PixelShader(ctx, psContent);
            Dx.Resources(ctx, c.DarkText ? c.DarkSrv : c.LightSrv);
            Dx.Draw(ctx, 3);
        }

        // how bright the backdrop behind each card is (for the text colour and the shadow), read back a few frames
        // later without waiting for the GPU
        static void ReadLuminance(double now)
        {
            foreach (var c in cards)
            {
                // what has arrived from the GPU, oldest first
                while (c.StagingFull > 0)
                {
                    int slot = (c.StagingNext - c.StagingFull + 3) % 3;
                    Dx.MappedSubresource mm;
                    if (!Dx.TryMap(ctx, c.Staging[slot], out mm)) break;
                    double lum = Marshal.ReadByte(mm.Data, 2) / 255.0;   // BGRA: the red byte holds the mean
                    Dx.Unmap(ctx, c.Staging[slot]);
                    c.StagingFull--;
                    ApplyLum(c, lum);
                }
                if (now - c.LumAt < 150 || c.StagingFull >= 3) continue;
                if (c.LumSerial == captureSerial && !c.Moving(now)) continue;   // nothing behind it has changed
                c.LumAt = now;
                c.LumSerial = captureSerial;
                float x = (float)((baseXDip + c.X.At(now)) * scale - colX), y = (float)(c.Y.At(now) * scale - colY);
                Array.Clear(consts, 0, consts.Length);
                consts[22] = colW; consts[23] = colH;
                consts[36] = x; consts[37] = y; consts[38] = c.Spec.CardW; consts[39] = c.Spec.CardH;
                Constants();
                Dx.Blend(ctx, IntPtr.Zero);
                Dx.M<Dx.Fn_V_URP>(ctx, 33)(ctx, 1, new[] { c.MeanRtv }, IntPtr.Zero);
                Viewport(0, 0, 1, 1);
                Dx.PixelShader(ctx, psMean);
                Dx.Resources(ctx, blurOutSrv);
                Dx.Draw(ctx, 3);
                Dx.Resources(ctx, IntPtr.Zero);
                Dx.CopyAll(ctx, c.Staging[c.StagingNext], c.MeanTex);
                c.StagingNext = (c.StagingNext + 1) % 3;
                c.StagingFull++;
            }
        }

        static void ApplyLum(Card c, double backLum)
        {
            c.Lum = backLum;
            var s = c.Spec;
            double lumFinal = s.LumTint;
            if (s.Glass)
            {
                double lift = 0.025 + 0.045 * s.K;
                double tintLum = Math.Max(0, Math.Min(1, s.LumTint * (0.78 + 0.45 * backLum)));
                lumFinal = (backLum * (1 - lift) + lift) * (1 - s.TintAlpha) + tintLum * s.TintAlpha + 0.03 + 0.04 * s.K;
            }
            c.DarkText = lumFinal > 0.55;
        }

        // ---- setting up ----

        // The textures of a card that left are kept for the next ones: created and released one card after the
        // other, they made the driver's memory grow a little with every banner.
        sealed class Kept { public uint W, H; public IntPtr Tex, View; }
        static readonly List<Kept> keptContent = new List<Kept>();
        static readonly Stack<IntPtr[]> keptSmall = new Stack<IntPtr[]>();   // mean texture, its target, 3 staging
        const int KeepContent = 12;

        static void AddCard(int id, GpuCardSpec spec, double topDip)
        {
            var c = new Card { Id = id, Spec = spec };
            c.LightTex = Content(spec.Light, spec.CardW, spec.CardH, out c.LightSrv);
            c.DarkTex = Content(spec.Dark, spec.CardW, spec.CardH, out c.DarkSrv);
            if (keptSmall.Count > 0)
            {
                var k = keptSmall.Pop();
                c.MeanTex = k[0]; c.MeanRtv = k[1]; c.Staging[0] = k[2]; c.Staging[1] = k[3]; c.Staging[2] = k[4];
            }
            else
            {
                c.MeanTex = Dx.Texture(device, 1, 1, Dx.BindRenderTarget, 0, 0);
                c.MeanRtv = Dx.TargetView(device, c.MeanTex);
                for (int k = 0; k < 3; k++) c.Staging[k] = Dx.Texture(device, 1, 1, 0, 3 /* STAGING */, 0x20000 /* READ */);
            }
            c.X.Jump(Banner.SlideOff);
            c.Y.Jump(topDip);
            c.O.Jump(0);
            ApplyLum(c, 0.5);
            c.LumAt = -1000;
            cards.Insert(0, c);
            pullWanted = true;
        }

        static IntPtr Content(byte[] px, int w, int h, out IntPtr srv)
        {
            IntPtr t = IntPtr.Zero;
            srv = IntPtr.Zero;
            int at = keptContent.FindIndex(k => k.W == w && k.H == h);
            if (at >= 0) { t = keptContent[at].Tex; srv = keptContent[at].View; keptContent.RemoveAt(at); }
            else t = Dx.Texture(device, (uint)w, (uint)h, Dx.BindShaderResource, 0, 0);
            var pin = GCHandle.Alloc(px, GCHandleType.Pinned);
            try { Dx.Update(ctx, t, pin.AddrOfPinnedObject(), (uint)(w * 4)); }
            finally { pin.Free(); }
            if (srv == IntPtr.Zero) srv = Dx.ShaderView(device, t);
            return t;
        }

        static void Free(Card c)
        {
            Keep(c.Spec, ref c.LightTex, ref c.LightSrv);
            Keep(c.Spec, ref c.DarkTex, ref c.DarkSrv);
            keptSmall.Push(new[] { c.MeanTex, c.MeanRtv, c.Staging[0], c.Staging[1], c.Staging[2] });
            c.MeanTex = c.MeanRtv = IntPtr.Zero;
            for (int k = 0; k < 3; k++) c.Staging[k] = IntPtr.Zero;
        }

        static void Keep(GpuCardSpec spec, ref IntPtr tex, ref IntPtr view)
        {
            keptContent.Add(new Kept { W = (uint)spec.CardW, H = (uint)spec.CardH, Tex = tex, View = view });
            tex = view = IntPtr.Zero;
            while (keptContent.Count > KeepContent)
            {
                var old = keptContent[0];
                keptContent.RemoveAt(0);
                Dx.Release(ref old.View); Dx.Release(ref old.Tex);
            }
        }

        static void Init()
        {
            Dx.CreateDevice(out device, out ctx, out output, out adapterName);
            frameInfo = Marshal.AllocHGlobal(48);
            cbData = Marshal.AllocHGlobal(consts.Length * 4);
            cb = Dx.ConstantBuffer(device, (uint)(consts.Length * 4));
            sampler = Dx.LinearClampSampler(device);
            blend = Dx.PremultipliedBlend(device);
            vs = Dx.CompileShader(device, Shaders, "vs", "vs_5_0");
            psBlur = Dx.CompileShader(device, Shaders, "blur", "ps_5_0");
            psShadow = Dx.CompileShader(device, Shaders, "shadow", "ps_5_0");
            psContent = Dx.CompileShader(device, Shaders, "content", "ps_5_0");
            psMean = Dx.CompileShader(device, Shaders, "mean", "ps_5_0");
            for (int q = 0; q < 4; q++) psLens[q] = Dx.CompileShader(device, "#define QUALITY " + q + "\n" + Shaders, "lens", "ps_5_0");
            MakeWindow();
            MakeSurfaces();
            dup = Dx.Duplicate(output, device);
            statsAt = Ms;
        }

        static void MakeSurfaces()
        {
            chain = Dx.CompositionSwapChain(device, (uint)colW, (uint)colH);
            dcomp = Dx.Compose(device, hwnd, chain);
            waiter = Dx.FrameWaiter(chain);
            backBuf = Dx.BackBuffer(chain);
            rtv = Dx.TargetView(device, backBuf);
            deskTex = Dx.Texture(device, (uint)colW, (uint)colH, Dx.BindShaderResource, 0, 0);
            deskSrv = Dx.ShaderView(device, deskTex);
            blurDown = -1;
            MakeBlurTargets(1);
        }

        static void MakeBlurTargets(int down)
        {
            Dx.Release(ref blurTmpSrv); Dx.Release(ref blurTmpRtv); Dx.Release(ref blurTmp);
            Dx.Release(ref blurOutSrv); Dx.Release(ref blurOutRtv); Dx.Release(ref blurOut);
            blurDown = down;
            blurW = Math.Max(1, (colW + down - 1) / down);
            blurH = Math.Max(1, (colH + down - 1) / down);
            blurTmp = Dx.Texture(device, (uint)blurW, (uint)colH, Dx.BindShaderResource | Dx.BindRenderTarget, 0, 0);
            blurTmpRtv = Dx.TargetView(device, blurTmp);
            blurTmpSrv = Dx.ShaderView(device, blurTmp);
            blurOut = Dx.Texture(device, (uint)blurW, (uint)blurH, Dx.BindShaderResource | Dx.BindRenderTarget, 0, 0);
            blurOutRtv = Dx.TargetView(device, blurOut);
            blurOutSrv = Dx.ShaderView(device, blurOut);
        }

        // the screen or the work area changed: a column of the new size
        static void Reshape(int x, int y, int w, int h, double s, double cardXDip)
        {
            colX = x; colY = y; colW = w; colH = h; scale = s; baseXDip = cardXDip;
            Dx.Release(ref rtv); Dx.Release(ref backBuf); Dx.Release(ref dcomp); Dx.Release(ref chain);
            Dx.Release(ref deskSrv); Dx.Release(ref deskTex);
            SetWindowPos(hwnd, (IntPtr)(-1), colX, colY, colW, colH, 0x10 /* NOACTIVATE */);
            MakeSurfaces();
            haveDesktop = false;
            Log.Write("gpu engine: column now " + colW + "x" + colH + " px at " + colX + "," + colY);
        }

        // ---- the window ----

        delegate IntPtr WndProcFn(IntPtr h, uint msg, IntPtr w, IntPtr l);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WNDCLASSEX
        {
            public int cbSize, style;
            public WndProcFn lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowEx(int ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG m);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(IntPtr name);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

        static void MakeWindow()
        {
            wndProc = DefWindowProc;
            var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf(typeof(WNDCLASSEX)), lpfnWndProc = wndProc, hInstance = GetModuleHandle(IntPtr.Zero), lpszClassName = "GlassBellGpu" + Program.Instance };
            RegisterClassEx(ref wc);
            // no redirection surface (DirectComposition draws it all), on top, no taskbar button, never active, and every
            // click goes through it: the hollow WPF banner windows take the clicks meant for the cards
            const int ex = 0x00200000 | 0x8 | 0x80 | 0x08000000 | 0x20 | 0x80000;
            hwnd = CreateWindowEx(ex, wc.lpszClassName, "GlassBell", 0x80000000 /* POPUP */, colX, colY, colW, colH, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            SetLayeredWindowAttributes(hwnd, 0, 255, 2 /* LWA_ALPHA */);
            // left out of every capture, its own included: the glass must never see itself (GLASSBELL_CAPTURABLE=1 for screenshots)
            if (!Banner.Capturable) SetWindowDisplayAffinity(hwnd, 0x11);
        }

        static void Pump()
        {
            MSG m;
            while (PeekMessage(out m, IntPtr.Zero, 0, 0, 1 /* REMOVE */)) { TranslateMessage(ref m); DispatchMessage(ref m); }
        }

        // ---- the shaders ----

        // c0..c4 are those of the WPF lens (see Gpu.Hlsl); g5 tells where the viewport is in the column
        const string Shaders = @"
Texture2D back : register(t0);
SamplerState lin : register(s0);
cbuffer C : register(b0)
{
    float4 c0;   // lens: padded size px (xy), card size px (zw); shadow: viewport size px (xy)
    float4 c1;   // margin px, corner radius px, bezel px, lens depth px
    float4 c2;   // dispersion, saturation, lift, glass intensity k
    float4 c3;   // tint rgb, tint alpha
    float4 c4;   // light xy, scale, glass on (1) / flat (0)
    float4 g5;   // the viewport's origin in the column px (xy), the column's size px (zw); blur: texel (xy), direction (zw)
    float4 g6;   // opacity; blur: sigma px, step px, taps each side
    float4 g7;   // shadow: sigma A, depth A, opacity A, sigma B
    float4 g8;   // shadow: depth B, opacity B, corner radius px, inset px
    float4 g9;   // the card in the column px: x, y, w, h
};
struct V { float4 pos : SV_Position; float2 uv : TEXCOORD0; };
V vs(uint id : SV_VertexID)
{
    V o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

// ---- blur: one direction of a gaussian, sampling every step px ----
float4 blur(V i) : SV_Target
{
    float3 acc = 0.0;
    float wsum = 0.0;
    int n = (int)g6.z;
    [loop] for (int k = -n; k <= n; k++)
    {
        float t = k * g6.y;
        float w = exp(-(t * t) / (2.0 * g6.x * g6.x));
        acc += w * back.SampleLevel(lin, i.uv + g5.zw * g5.xy * t, 0).rgb;
        wsum += w;
    }
    return float4(acc / wsum, 1.0);
}

// ---- the text and the icon (premultiplied already) ----
float4 content(V i) : SV_Target { return back.Sample(lin, i.uv) * g6.x; }

// ---- the mean brightness of the backdrop behind a card ----
float4 mean(V i) : SV_Target
{
    float s = 0.0;
    [unroll] for (int y = 0; y < 6; y++)
        [unroll] for (int x = 0; x < 10; x++)
        {
            float2 p = g9.xy + g9.zw * (float2(x + 0.5, y + 0.5) / float2(10.0, 6.0));
            s += dot(back.SampleLevel(lin, p / g5.zw, 0).rgb, float3(0.299, 0.587, 0.114));
        }
    return float4(s / 60.0, 0.0, 0.0, 1.0);
}

// ---- the shadow: a gaussian-blurred rounded rectangle, worked out instead of blurred ----
float Gauss(float x, float s) { return exp(-(x * x) / (2.0 * s * s)) / (2.50662827 * s); }
float2 Erf2(float2 x)
{
    float2 s = sign(x), a = abs(x);
    x = 1.0 + (0.278393 + (0.230389 + 0.078108 * (a * a)) * a) * a;
    x *= x;
    return s - s / (x * x);
}
float Row(float x, float y, float s, float r, float2 h)
{
    float delta = min(h.y - r - abs(y), 0.0);
    float curved = h.x - r + sqrt(max(0.0, r * r - delta * delta));
    float2 integral = 0.5 + 0.5 * Erf2((x + float2(-curved, curved)) * (0.70710678 / s));
    return integral.y - integral.x;
}
float Rounded(float2 p, float2 h, float s, float r)
{
    float low = p.y - h.y, high = p.y + h.y;
    float start = clamp(-3.0 * s, low, high), end = clamp(3.0 * s, low, high);
    float stp = (end - start) / 4.0;
    float y = start + stp * 0.5;
    float v = 0.0;
    [unroll] for (int k = 0; k < 4; k++) { v += Row(p.x, p.y - y, s, r, h) * Gauss(y, s) * stp; y += stp; }
    return v;
}
float4 shadow(V i) : SV_Target
{
    float2 p = g5.xy + i.uv * c0.xy;
    float2 center = g9.xy + g9.zw * 0.5;
    float2 h = g9.zw * 0.5 - g8.w;
    float r = max(0.0, min(g8.z - g8.w, min(h.x, h.y)));
    float a = g7.z * Rounded(p - center - float2(0.0, g7.y), h, max(g7.x, 0.5), r);
    float b = g8.y * Rounded(p - center - float2(0.0, g8.x), h, max(g7.w, 0.5), r);
    float alpha = (1.0 - (1.0 - saturate(a)) * (1.0 - saturate(b))) * g6.x;
    return float4(0.0, 0.0, 0.0, alpha);
}

// ---- the lens: the WPF shader, reading the blurred column where the card is ----
static const float IOR = 1.5;
float3 S(float2 uv) { return back.SampleLevel(lin, (g5.xy + uv * c0.xy) / g5.zw, 0).rgb; }

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
float3 Spectrum(float2 uv, float2 duv)
{
    float3 acc = 0.0;
    [unroll] for (int i = 0; i < 7; i++)
    {
        float f = i / 6.0;
        float3 w = saturate(float3(1.0 - 2.0 * f, 1.0 - abs(2.0 * f - 1.0), 2.0 * f - 1.0));
        acc += w * S(uv - duv * (1.0 + c2.x * (2.0 * f - 1.0)));
    }
    return acc / float3(2.0, 3.0, 2.0);
}
#endif

float4 lens(V i) : SV_Target
{
    float2 uv = i.uv;
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
    float3 col = S(uv);
#elif QUALITY == 1
    float3 col = S(uv - n * Bend(d) / texSize);
#elif QUALITY == 2
    float2 duv = n * Bend(d) / texSize;
    float cr = S(uv - duv * (1.0 - c2.x)).r;
    float cg = S(uv - duv).g;
    float cb = S(uv - duv * (1.0 + c2.x)).b;
    float3 col = float3(cr, cg, cb);
#else
    float3 col;
    if (d > bezel + 1.0) col = S(uv);
    else
    {
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
    float backside = max(0.0, -ndl);
    float dirw = pow(face, 1.3) + 0.55 * pow(backside, 1.6);
    float ridge = exp(-(d * d) / (2.0 * pow(0.9 * scale, 2.0)));
    float glow = exp(-d / (6.0 * scale)) * 0.30;
    float rim = (0.30 + 0.70 * k) * dirw * (ridge * 0.95 + glow) + 0.10 * (0.4 + 0.6 * k) * ridge;
    rim += k * 0.16 * pow(1.0 - t, 2.5);
    float yy = (uv.y * texSize.y - c1.x) / card.y;
    rim += 0.05 * k * max(0.0, 1.0 - yy / 0.55);
    if (glassOn < 0.5) rim *= 0.35;
    rim = min(rim, 1.0);
    col += (1.0 - col) * rim;
    col *= 1.0 - 0.14 * k * backside * exp(-d / (10.0 * scale));
    col = saturate(col);
    return float4(col * cov, cov) * g6.x;
}";
    }
}
