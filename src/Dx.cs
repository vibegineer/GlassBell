// Dx: the few Direct3D 11, DXGI and DirectComposition calls the GPU engine needs, made through the COM vtables
// directly, so the program still builds with the C# compiler of Windows alone (no SharpDX, no NuGet). Every method
// is called by its index in its interface's vtable; the indices are in the comments next to each call.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace GlassBell
{
    static class Dx
    {
        // ---- calling into a vtable ----

        static readonly Dictionary<long, Delegate> cache = new Dictionary<long, Delegate>();

        // the delegate for method i of a COM object; vtables are shared per type, so this is cached per vtable slot
        public static T M<T>(IntPtr obj, int i) where T : class
        {
            IntPtr slot = Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), i * IntPtr.Size);
            long key = slot.ToInt64() ^ ((long)typeof(T).GetHashCode() << 1);
            Delegate d;
            lock (cache)
                if (!cache.TryGetValue(key, out d)) { d = Marshal.GetDelegateForFunctionPointer(slot, typeof(T)); cache[key] = d; }
            return (T)(object)d;
        }

        public static void Release(ref IntPtr p) { if (p != IntPtr.Zero) { Marshal.Release(p); p = IntPtr.Zero; } }

        public static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException(what + " failed: 0x" + hr.ToString("X8")); }

        public static IntPtr Query(IntPtr obj, Guid iid)
        {
            IntPtr p;
            Check(Marshal.QueryInterface(obj, ref iid, out p), "QueryInterface " + iid);
            return p;
        }

        // ---- delegate shapes (the first argument is always the object itself) ----

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_P(IntPtr self, out IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_OutInt(IntPtr self, out int a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_UP(IntPtr self, uint a, out IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_GP(IntPtr self, ref Guid a, out IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_UGP(IntPtr self, uint a, ref Guid b, out IntPtr c);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_PPP(IntPtr self, IntPtr a, IntPtr b, out IntPtr c);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_IP(IntPtr self, IntPtr a, out IntPtr b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_I(IntPtr self, IntPtr a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_UU(IntPtr self, uint a, uint b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_PBP(IntPtr self, IntPtr a, int b, out IntPtr c);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate IntPtr Fn_Handle(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_Desc(IntPtr self, IntPtr desc);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_SwapChain(IntPtr self, IntPtr device, ref SwapChainDesc1 desc, IntPtr output, out IntPtr chain);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_Texture(IntPtr self, ref Texture2DDesc desc, IntPtr init, out IntPtr tex);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_Buffer(IntPtr self, ref BufferDesc desc, IntPtr init, out IntPtr buf);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_Sampler(IntPtr self, ref SamplerDesc desc, out IntPtr s);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_Blend(IntPtr self, ref BlendDesc desc, out IntPtr s);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_Shader(IntPtr self, IntPtr code, IntPtr size, IntPtr linkage, out IntPtr shader);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_Acquire(IntPtr self, uint timeout, IntPtr info, out IntPtr resource);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int Fn_Map(IntPtr self, IntPtr res, uint sub, int type, uint flags, out MappedSubresource m);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Fn_V_PU(IntPtr self, IntPtr a, uint b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Fn_V_UUA(IntPtr self, uint start, uint n, IntPtr[] items);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Fn_V_PPU(IntPtr self, IntPtr a, IntPtr b, uint c);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Fn_V_U(IntPtr self, uint a);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Fn_V_UU(IntPtr self, uint a, uint b);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Fn_V_URP(IntPtr self, uint n, IntPtr[] rtvs, IntPtr dsv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Fn_V_Viewport(IntPtr self, uint n, ref Viewport v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Fn_V_Clear(IntPtr self, IntPtr rtv, float[] rgba);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Fn_V_Copy(IntPtr self, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, ref Box box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Fn_V_Update(IntPtr self, IntPtr dst, uint sub, IntPtr box, IntPtr data, uint row, uint depth);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Fn_V_BlendState(IntPtr self, IntPtr state, float[] factor, uint mask);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate void Fn_V_PP(IntPtr self, IntPtr a, IntPtr b);

        // ---- structures ----

        [StructLayout(LayoutKind.Sequential)]
        public struct SwapChainDesc1
        {
            public uint Width, Height, Format;
            public int Stereo;
            public uint SampleCount, SampleQuality, BufferUsage, BufferCount, Scaling, SwapEffect, AlphaMode, Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Texture2DDesc
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BufferDesc { public uint ByteWidth, Usage, BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SamplerDesc
        {
            public uint Filter, AddressU, AddressV, AddressW;
            public float MipLODBias;
            public uint MaxAnisotropy, ComparisonFunc;
            public float B0, B1, B2, B3, MinLOD, MaxLOD;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RenderTargetBlend
        {
            public int BlendEnable;
            public uint SrcBlend, DestBlend, BlendOp, SrcBlendAlpha, DestBlendAlpha, BlendOpAlpha;
            public byte WriteMask;
            byte p0, p1, p2;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BlendDesc
        {
            public int AlphaToCoverage, IndependentBlend;
            public RenderTargetBlend T0, T1, T2, T3, T4, T5, T6, T7;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Viewport { public float X, Y, W, H, MinDepth, MaxDepth; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Box { public uint Left, Top, Front, Right, Bottom, Back; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MappedSubresource { public IntPtr Data; public uint RowPitch, DepthPitch; }

        // ---- the entry points ----

        [DllImport("d3d11.dll")]
        static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint nLevels,
            uint sdk, out IntPtr device, out int level, out IntPtr context);
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid, out IntPtr factory);
        [DllImport("dcomp.dll")] static extern int DCompositionCreateDevice(IntPtr dxgiDevice, ref Guid iid, out IntPtr device);
        [DllImport("d3dcompiler_47.dll")]
        static extern int D3DCompile(byte[] src, IntPtr size, [MarshalAs(UnmanagedType.LPStr)] string name, IntPtr defines, IntPtr include,
            [MarshalAs(UnmanagedType.LPStr)] string entry, [MarshalAs(UnmanagedType.LPStr)] string target, uint f1, uint f2, out IntPtr code, out IntPtr errors);

        public static readonly Guid IID_Factory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        public static readonly Guid IID_Factory2 = new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        public static readonly Guid IID_DxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        public static readonly Guid IID_Output1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        public static readonly Guid IID_Texture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        public static readonly Guid IID_SwapChain2 = new Guid("a8be2ac4-199f-4946-b331-79599fb98de7");
        public static readonly Guid IID_DCompDevice = new Guid("c37ea93a-e7aa-450d-b16f-9746cb0407f3");

        public const uint FormatBgra = 87;            // DXGI_FORMAT_B8G8R8A8_UNORM
        public const uint BindShaderResource = 0x8, BindRenderTarget = 0x20, BindConstantBuffer = 0x4;

        // The device lives on the adapter that drives the primary monitor: the desktop can only be duplicated there
        // (on a laptop with two GPUs that is often the integrated one). Returns the output too, for DuplicateOutput.
        public static void CreateDevice(out IntPtr device, out IntPtr context, out IntPtr output, out string adapterName)
        {
            Guid f1 = IID_Factory1;
            IntPtr factory;
            Check(CreateDXGIFactory1(ref f1, out factory), "CreateDXGIFactory1");
            try
            {
                for (uint a = 0; ; a++)
                {
                    IntPtr adapter;
                    if (M<Fn_UP>(factory, 12)(factory, a, out adapter) < 0) break;   // IDXGIFactory1::EnumAdapters1
                    try
                    {
                        for (uint o = 0; ; o++)
                        {
                            IntPtr outp;
                            if (M<Fn_UP>(adapter, 7)(adapter, o, out outp) < 0) break;   // IDXGIAdapter::EnumOutputs
                            IntPtr desc = Marshal.AllocHGlobal(96);
                            try
                            {
                                M<Fn_Desc>(outp, 7)(outp, desc);   // IDXGIOutput::GetDesc
                                int left = Marshal.ReadInt32(desc, 64), top = Marshal.ReadInt32(desc, 68);
                                if (left == 0 && top == 0)   // the primary monitor starts at the origin of the desktop
                                {
                                    IntPtr ad = Marshal.AllocHGlobal(512);
                                    M<Fn_Desc>(adapter, 8)(adapter, ad);   // IDXGIAdapter::GetDesc: Description is the first 128 WCHARs
                                    adapterName = Marshal.PtrToStringUni(ad, 128).TrimEnd('\0');
                                    Marshal.FreeHGlobal(ad);
                                    int level;
                                    Check(D3D11CreateDevice(adapter, 0 /* UNKNOWN: the adapter is given */, IntPtr.Zero, 0x20u | 0x8u /* BGRA_SUPPORT; PREVENT_INTERNAL_THREADING_OPTIMIZATIONS: the driver's worker thread cost three times the engine */,
                                        IntPtr.Zero, 0, 7, out device, out level, out context), "D3D11CreateDevice");
                                    output = Query(outp, IID_Output1);
                                    Marshal.Release(outp);
                                    return;
                                }
                            }
                            finally { Marshal.FreeHGlobal(desc); }
                            Marshal.Release(outp);
                        }
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
            throw new InvalidOperationException("no adapter drives the primary monitor");
        }

        public static IntPtr CompileShader(IntPtr device, string hlsl, string entry, string target)
        {
            byte[] src = Encoding.ASCII.GetBytes(hlsl);
            IntPtr code, err;
            int hr = D3DCompile(src, (IntPtr)src.Length, entry, IntPtr.Zero, IntPtr.Zero, entry, target, 1u << 15 /* OPTIMIZATION_LEVEL3 */, 0, out code, out err);
            if (hr < 0)
            {
                string msg = err != IntPtr.Zero ? Marshal.PtrToStringAnsi(M<Fn_Handle>(err, 3)(err)) : "";
                if (err != IntPtr.Zero) Marshal.Release(err);
                throw new InvalidOperationException("shader " + entry + ": " + msg);
            }
            if (err != IntPtr.Zero) Marshal.Release(err);
            try
            {
                IntPtr ptr = M<Fn_Handle>(code, 3)(code), size = M<Fn_Handle>(code, 4)(code);   // ID3DBlob::GetBufferPointer, GetBufferSize
                IntPtr shader;
                // ID3D11Device::CreateVertexShader (12) or CreatePixelShader (15)
                Check(M<Fn_Shader>(device, target.StartsWith("vs") ? 12 : 15)(device, ptr, size, IntPtr.Zero, out shader), "Create shader " + entry);
                return shader;
            }
            finally { Marshal.Release(code); }
        }

        public static IntPtr Texture(IntPtr device, uint w, uint h, uint bind, uint usage, uint cpu)
        {
            var d = new Texture2DDesc { Width = w, Height = h, MipLevels = 1, ArraySize = 1, Format = FormatBgra, SampleCount = 1, Usage = usage, BindFlags = bind, CPUAccessFlags = cpu };
            IntPtr t;
            Check(M<Fn_Texture>(device, 5)(device, ref d, IntPtr.Zero, out t), "CreateTexture2D");   // ID3D11Device::CreateTexture2D
            return t;
        }

        public static IntPtr ShaderView(IntPtr device, IntPtr tex)
        {
            IntPtr v;
            Check(M<Fn_PPP>(device, 7)(device, tex, IntPtr.Zero, out v), "CreateShaderResourceView");
            return v;
        }

        public static IntPtr TargetView(IntPtr device, IntPtr tex)
        {
            IntPtr v;
            Check(M<Fn_PPP>(device, 9)(device, tex, IntPtr.Zero, out v), "CreateRenderTargetView");
            return v;
        }

        public static IntPtr LinearClampSampler(IntPtr device)
        {
            var d = new SamplerDesc { Filter = 0x15, AddressU = 3, AddressV = 3, AddressW = 3, ComparisonFunc = 1, MaxLOD = float.MaxValue };
            IntPtr s;
            Check(M<Fn_Sampler>(device, 23)(device, ref d, out s), "CreateSamplerState");
            return s;
        }

        // premultiplied alpha: what is drawn goes over what is there
        public static IntPtr PremultipliedBlend(IntPtr device)
        {
            var t = new RenderTargetBlend { BlendEnable = 1, SrcBlend = 2 /* ONE */, DestBlend = 6 /* INV_SRC_ALPHA */, BlendOp = 1, SrcBlendAlpha = 2, DestBlendAlpha = 6, BlendOpAlpha = 1, WriteMask = 0x0F };
            var d = new BlendDesc { T0 = t };
            IntPtr s;
            Check(M<Fn_Blend>(device, 20)(device, ref d, out s), "CreateBlendState");
            return s;
        }

        public static IntPtr ConstantBuffer(IntPtr device, uint bytes)
        {
            var d = new BufferDesc { ByteWidth = (bytes + 15) / 16 * 16, BindFlags = BindConstantBuffer };
            IntPtr b;
            Check(M<Fn_Buffer>(device, 3)(device, ref d, IntPtr.Zero, out b), "CreateBuffer");
            return b;
        }

        // ---- the device context (ID3D11DeviceContext vtable indices) ----

        public static void SetTarget(IntPtr ctx, IntPtr rtv, float w, float h)
        {
            M<Fn_V_URP>(ctx, 33)(ctx, 1, new[] { rtv }, IntPtr.Zero);   // OMSetRenderTargets
            var vp = new Viewport { W = w, H = h, MaxDepth = 1 };
            M<Fn_V_Viewport>(ctx, 44)(ctx, 1, ref vp);                 // RSSetViewports
        }

        public static void Clear(IntPtr ctx, IntPtr rtv, float r, float g, float b, float a) { M<Fn_V_Clear>(ctx, 50)(ctx, rtv, new[] { r, g, b, a }); }
        public static void PixelShader(IntPtr ctx, IntPtr ps) { M<Fn_V_PPU>(ctx, 9)(ctx, ps, IntPtr.Zero, 0); }
        public static void VertexShader(IntPtr ctx, IntPtr vs) { M<Fn_V_PPU>(ctx, 11)(ctx, vs, IntPtr.Zero, 0); }
        public static void Resources(IntPtr ctx, params IntPtr[] srvs) { M<Fn_V_UUA>(ctx, 8)(ctx, 0, (uint)srvs.Length, srvs); }
        public static void Samplers(IntPtr ctx, params IntPtr[] s) { M<Fn_V_UUA>(ctx, 10)(ctx, 0, (uint)s.Length, s); }
        public static void PixelConstants(IntPtr ctx, IntPtr cb) { M<Fn_V_UUA>(ctx, 16)(ctx, 0, 1, new[] { cb }); }
        public static void Triangles(IntPtr ctx) { M<Fn_V_U>(ctx, 24)(ctx, 4); }   // IASetPrimitiveTopology: TRIANGLELIST
        public static void Draw(IntPtr ctx, uint vertices) { M<Fn_V_UU>(ctx, 13)(ctx, vertices, 0); }
        public static void Blend(IntPtr ctx, IntPtr state) { M<Fn_V_BlendState>(ctx, 35)(ctx, state, null, 0xFFFFFFFF); }
        public static void CopyRegion(IntPtr ctx, IntPtr dst, uint x, uint y, IntPtr src, Box box) { M<Fn_V_Copy>(ctx, 46)(ctx, dst, 0, x, y, 0, src, 0, ref box); }
        public static void Update(IntPtr ctx, IntPtr res, IntPtr data, uint rowPitch) { M<Fn_V_Update>(ctx, 48)(ctx, res, 0, IntPtr.Zero, data, rowPitch, 0); }
        public static MappedSubresource Map(IntPtr ctx, IntPtr res) { MappedSubresource m; Check(M<Fn_Map>(ctx, 14)(ctx, res, 0, 1 /* READ */, 0, out m), "Map"); return m; }
        // without waiting for the GPU: false while the copy into it has not finished yet
        public static bool TryMap(IntPtr ctx, IntPtr res, out MappedSubresource m) { return M<Fn_Map>(ctx, 14)(ctx, res, 0, 1, 0x100000 /* DO_NOT_WAIT */, out m) == 0; }
        public static void CopyAll(IntPtr ctx, IntPtr dst, IntPtr src) { M<Fn_V_PP>(ctx, 47)(ctx, dst, src); }   // CopyResource
        public static void Unmap(IntPtr ctx, IntPtr res) { M<Fn_V_PU>(ctx, 15)(ctx, res, 0); }

        // ---- the swap chain and DirectComposition ----

        // a swap chain that DirectComposition puts on the screen, with per-pixel (premultiplied) transparency
        public static IntPtr CompositionSwapChain(IntPtr device, uint w, uint h)
        {
            IntPtr dxgiDevice = Query(device, IID_DxgiDevice), adapter = IntPtr.Zero, factory = IntPtr.Zero;
            try
            {
                Check(M<Fn_P>(dxgiDevice, 7)(dxgiDevice, out adapter), "IDXGIDevice::GetAdapter");
                Guid f2 = IID_Factory2;
                Check(M<Fn_GP>(adapter, 6)(adapter, ref f2, out factory), "IDXGIObject::GetParent");
                var d = new SwapChainDesc1
                {
                    Width = w, Height = h, Format = FormatBgra, SampleCount = 1, BufferUsage = 0x20 /* RENDER_TARGET_OUTPUT */, BufferCount = 2,
                    SwapEffect = 3 /* FLIP_SEQUENTIAL */, AlphaMode = 1 /* PREMULTIPLIED */, Flags = 64 /* FRAME_LATENCY_WAITABLE_OBJECT */
                };
                IntPtr chain;
                Check(M<Fn_SwapChain>(factory, 24)(factory, device, ref d, IntPtr.Zero, out chain), "CreateSwapChainForComposition");
                return chain;
            }
            finally { Release(ref factory); Release(ref adapter); Marshal.Release(dxgiDevice); }
        }

        public static IntPtr BackBuffer(IntPtr chain)
        {
            Guid t = IID_Texture2D;
            IntPtr tex;
            Check(M<Fn_UGP>(chain, 9)(chain, 0, ref t, out tex), "IDXGISwapChain::GetBuffer");
            return tex;
        }

        // IDXGISwapChain::Present: 1 waits for the screen, 0 hands the frame over at once
        public static int Present(IntPtr chain, uint sync) { return M<Fn_UU>(chain, 8)(chain, sync, 0); }

        // the event a swap chain sets once it can take the next frame (IDXGISwapChain2), with at most one frame queued
        public static IntPtr FrameWaiter(IntPtr chain)
        {
            IntPtr c2 = Query(chain, IID_SwapChain2);
            try
            {
                M<Fn_V_U>(c2, 31)(c2, 1);                    // SetMaximumFrameLatency(1)  (returns HRESULT; ignored)
                return M<Fn_Handle>(c2, 33)(c2);              // GetFrameLatencyWaitableObject
            }
            finally { Marshal.Release(c2); }
        }

        // DirectComposition: the swap chain becomes the whole content of the window
        // target: the binding of the tree to the window. It must live as long as the window shows the chain: released,
        // it takes the tree off the window and nothing is drawn (while the chain still renders, unseen).
        public static IntPtr Compose(IntPtr device, IntPtr hwnd, IntPtr chain, out IntPtr target)
        {
            IntPtr dxgiDevice = Query(device, IID_DxgiDevice), dcomp, visual;
            Guid g = IID_DCompDevice;
            try { Check(DCompositionCreateDevice(dxgiDevice, ref g, out dcomp), "DCompositionCreateDevice"); }
            finally { Marshal.Release(dxgiDevice); }
            Check(M<Fn_PBP>(dcomp, 6)(dcomp, hwnd, 1, out target), "CreateTargetForHwnd");   // IDCompositionDevice::CreateTargetForHwnd (topmost)
            Check(M<Fn_P>(dcomp, 7)(dcomp, out visual), "CreateVisual");
            Check(M<Fn_I>(visual, 15)(visual, chain), "IDCompositionVisual::SetContent");
            Check(M<Fn_I>(target, 3)(target, visual), "IDCompositionTarget::SetRoot");
            Check(M<Fn_>(dcomp, 3)(dcomp), "IDCompositionDevice::Commit");
            Marshal.Release(visual);   // the target keeps its root visual
            return dcomp;
        }

        // IDCompositionDevice::CheckDeviceState: false once the device is lost (the graphics card was reset, ...)
        public static bool CompositionValid(IntPtr dcomp)
        {
            int valid;
            return M<Fn_OutInt>(dcomp, 26)(dcomp, out valid) >= 0 && valid != 0;
        }

        // ---- duplicating the desktop ----

        public static IntPtr Duplicate(IntPtr output1, IntPtr device)
        {
            IntPtr dup;
            Check(M<Fn_IP>(output1, 22)(output1, device, out dup), "IDXGIOutput1::DuplicateOutput");
            return dup;
        }

        // the next desktop image, if there is a new one (S_OK); 0x887A0027 = no new frame within the timeout
        public static int Acquire(IntPtr dup, uint timeout, IntPtr info, out IntPtr resource) { return M<Fn_Acquire>(dup, 8)(dup, timeout, info, out resource); }
        public static int ReleaseFrame(IntPtr dup) { return M<Fn_>(dup, 14)(dup); }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Fn_Rects(IntPtr self, uint size, IntPtr rects, out uint required);

        // The parts of the desktop that changed in the frame just acquired, as RECTs (left, top, right, bottom): the
        // dirty rectangles (method 9) or, for move = true, the destinations of the moved ones (method 10, whose items
        // are a source POINT and then the RECT). null when Windows did not say (hr in lastHr).
        public static int LastRectsHr;

        public static int[] ChangedRects(IntPtr dup, bool move, ref IntPtr buf, ref uint bufSize)
        {
            if (buf == IntPtr.Zero) { bufSize = 64 * 1024; buf = Marshal.AllocHGlobal((int)bufSize); }
            uint need;
            int index = move ? 10 : 9;
            int hr = M<Fn_Rects>(dup, index)(dup, bufSize, buf, out need);
            if (hr == unchecked((int)0x887A0003) /* MORE_DATA */)
            {
                Marshal.FreeHGlobal(buf);
                bufSize = need + 4096;
                buf = Marshal.AllocHGlobal((int)bufSize);
                hr = M<Fn_Rects>(dup, index)(dup, bufSize, buf, out need);
            }
            LastRectsHr = hr;
            if (hr < 0) return null;
            var raw = new int[need / 4];
            Marshal.Copy(buf, raw, 0, raw.Length);
            if (!move) return raw;
            var r = new int[raw.Length / 6 * 4];   // drop the source points
            for (int i = 0, j = 0; i + 5 < raw.Length; i += 6, j += 4) { r[j] = raw[i + 2]; r[j + 1] = raw[i + 3]; r[j + 2] = raw[i + 4]; r[j + 3] = raw[i + 5]; }
            return r;
        }
    }
}
