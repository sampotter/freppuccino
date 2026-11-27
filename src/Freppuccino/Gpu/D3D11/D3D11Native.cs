using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Freppuccino.Gpu.D3D11;

/// Hand-rolled COM vtable calls (no binding package). Indices verified against mingw-w64 d3d11.h / d3d11_1.h C vtables.
/// ID3D11Device1 / ID3D11DeviceContext1 vtables are linear: IUnknown(0-2) → base interface → *1 additions.
[SupportedOSPlatform("windows")]
static unsafe class D3D11Native
{
    // IUnknown
    const int QueryInterface = 0, Release = 2;

    // ID3D11Device (+ ID3D11Device1)
    const int Dev_CreateBuffer = 3, Dev_CreateTexture2D = 5, Dev_CreateShaderResourceView = 7, Dev_CreateRenderTargetView = 9,
        Dev_CreateBlendState = 20, Dev_CreateSamplerState = 23, Dev_CreateVertexShader = 12, Dev_CreatePixelShader = 15, Dev_CreateDepthStencilState = 21,
        Dev_CreateRasterizerState = 22, Dev_GetFeatureLevel = 37, Dev_GetCreationFlags = 38,
        Dev1_CreateDeviceContextState = 47;

    // ID3D11DeviceChild (all device children: shaders, states, views)
    const int Child_SetPrivateData = 5;

    // ID3D11DeviceContext (+ ID3D11DeviceContext1)
    const int Ctx_VSSetConstantBuffers = 7, Ctx_PSSetConstantBuffers = 16, Ctx_UpdateSubresource = 48,
        Ctx_VSGetConstantBuffers = 72, Ctx_PSGetConstantBuffers = 77, Ctx_PSSetShaderResources = 8, Ctx_PSSetSamplers = 10,
        Ctx_ClearRenderTargetView = 50;
    const int Ctx_PSSetShader = 9, Ctx_VSSetShader = 11, Ctx_Draw = 13, Ctx_IASetInputLayout = 17, Ctx_GSSetShader = 23,
        Ctx_IASetPrimitiveTopology = 24, Ctx_OMSetRenderTargets = 33, Ctx_OMSetBlendState = 35, Ctx_OMSetDepthStencilState = 36,
        Ctx_RSSetState = 43, Ctx_RSSetViewports = 44, Ctx_HSSetShader = 60, Ctx_DSSetShader = 64,
        Ctx_PSGetShader = 74, Ctx_VSGetShader = 76, Ctx_IAGetInputLayout = 78, Ctx_GSGetShader = 82,
        Ctx_IAGetPrimitiveTopology = 83, Ctx_OMGetRenderTargets = 89, Ctx_OMGetBlendState = 91, Ctx_OMGetDepthStencilState = 92,
        Ctx_RSGetState = 94, Ctx_RSGetViewports = 95, Ctx_HSGetShader = 98, Ctx_DSGetShader = 102, Ctx_GetType = 112,
        Ctx1_SwapDeviceContextState = 131;

    // ID3DBlob
    const int Blob_GetBufferPointer = 3, Blob_GetBufferSize = 4;

    // ID3DUserDefinedAnnotation
    const int Ann_BeginEvent = 3, Ann_EndEvent = 4;

    public static readonly Guid IID_ID3D11Device1 = new("a04bfb29-08ef-43d6-a49c-a9bdbdcbe686");
    public static readonly Guid IID_ID3DUserDefinedAnnotation = new("b2daad8b-03d4-4dbf-95eb-32ab4b63d0ab");
    static readonly Guid WKPDID_D3DDebugObjectName = new("429b8c22-9188-4b0c-8742-acb0bf85c200");

    public const int USAGE_DEFAULT = 0;
    public const uint BIND_CONSTANT_BUFFER = 4, BIND_SHADER_RESOURCE = 8, BIND_RENDER_TARGET = 32;
    public const int FORMAT_R32G32B32A32_FLOAT = 2, FORMAT_R16G16B16A16_FLOAT = 10, FORMAT_R16_FLOAT = 54;
    public const uint SDK_VERSION = 7, CREATE_DEVICE_SINGLETHREADED = 1, CONTEXT_STATE_SINGLETHREADED = 1;
    public const int FEATURE_LEVEL_11_0 = 0xb000, DEVICE_CONTEXT_IMMEDIATE = 0, TOPOLOGY_TRIANGLELIST = 4;
    public const int MaxRenderTargets = 8, MaxViewports = 16;

    static void** Vt(IntPtr p) => *(void***)p;

    // ---- IUnknown
    public static void SafeRelease(ref IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        ((delegate* unmanaged<IntPtr, uint>)Vt(p)[Release])(p);
        p = IntPtr.Zero;
    }

    public static IntPtr QI(IntPtr p, Guid iid)
    {
        IntPtr r;
        int hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Vt(p)[QueryInterface])(p, &iid, &r);
        return hr < 0 ? IntPtr.Zero : r;
    }

    // ---- ID3D11Device / ID3D11Device1
    public static int FeatureLevel(IntPtr dev) => ((delegate* unmanaged<IntPtr, int>)Vt(dev)[Dev_GetFeatureLevel])(dev);
    public static uint CreationFlags(IntPtr dev) => ((delegate* unmanaged<IntPtr, uint>)Vt(dev)[Dev_GetCreationFlags])(dev);

    public static int CreateVertexShader(IntPtr dev, ReadOnlySpan<byte> code, out IntPtr vs)
    {
        IntPtr r; int hr;
        fixed (byte* c = code) hr = ((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Vt(dev)[Dev_CreateVertexShader])(dev, c, (nuint)code.Length, IntPtr.Zero, &r);
        vs = hr < 0 ? IntPtr.Zero : r; return hr;
    }

    public static int CreatePixelShader(IntPtr dev, ReadOnlySpan<byte> code, out IntPtr ps)
    {
        IntPtr r; int hr;
        fixed (byte* c = code) hr = ((delegate* unmanaged<IntPtr, byte*, nuint, IntPtr, IntPtr*, int>)Vt(dev)[Dev_CreatePixelShader])(dev, c, (nuint)code.Length, IntPtr.Zero, &r);
        ps = hr < 0 ? IntPtr.Zero : r; return hr;
    }

    /// Default-usage buffer, no initial data.
    public static int CreateBuffer(IntPtr dev, in BufferDesc d, out IntPtr buf)
    {
        IntPtr r = IntPtr.Zero; int hr;
        fixed (BufferDesc* p = &d) hr = ((delegate* unmanaged<IntPtr, BufferDesc*, void*, IntPtr*, int>)Vt(dev)[Dev_CreateBuffer])(dev, p, null, &r);
        buf = r; return hr;
    }

    /// Whole-resource update (box null), as required for constant buffers.
    public static int CreateTexture2D(IntPtr dev, in Texture2DDesc d, out IntPtr tex)
    {
        IntPtr r; int hr;
        fixed (Texture2DDesc* p = &d) hr = ((delegate* unmanaged<IntPtr, Texture2DDesc*, void*, IntPtr*, int>)Vt(dev)[Dev_CreateTexture2D])(dev, p, null, &r);
        tex = r; return hr;
    }

    public static int CreateShaderResourceView(IntPtr dev, IntPtr res, out IntPtr srv)
    {
        IntPtr r;
        int hr = ((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Vt(dev)[Dev_CreateShaderResourceView])(dev, res, null, &r);
        srv = r; return hr;
    }

    public static int CreateRenderTargetView(IntPtr dev, IntPtr res, out IntPtr rtv)
    {
        IntPtr r;
        int hr = ((delegate* unmanaged<IntPtr, IntPtr, void*, IntPtr*, int>)Vt(dev)[Dev_CreateRenderTargetView])(dev, res, null, &r);
        rtv = r; return hr;
    }

    public static int CreateSamplerState(IntPtr dev, in SamplerDesc d, out IntPtr s)
    {
        IntPtr r; int hr;
        fixed (SamplerDesc* p = &d) hr = ((delegate* unmanaged<IntPtr, SamplerDesc*, IntPtr*, int>)Vt(dev)[Dev_CreateSamplerState])(dev, p, &r);
        s = r; return hr;
    }

    public static void PSSetShaderResources(IntPtr c, uint start, ReadOnlySpan<IntPtr> views)
    {
        fixed (IntPtr* p = views) ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Vt(c)[Ctx_PSSetShaderResources])(c, start, (uint)views.Length, p);
    }

    public static void PSSetSamplers(IntPtr c, uint start, ReadOnlySpan<IntPtr> samplers)
    {
        fixed (IntPtr* p = samplers) ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Vt(c)[Ctx_PSSetSamplers])(c, start, (uint)samplers.Length, p);
    }

    public static void ClearRenderTargetView(IntPtr c, IntPtr rtv, float r, float g, float b, float a)
    {
        float* col = stackalloc float[4] { r, g, b, a };
        ((delegate* unmanaged<IntPtr, IntPtr, float*, void>)Vt(c)[Ctx_ClearRenderTargetView])(c, rtv, col);
    }

    public static void UpdateSubresource(IntPtr c, IntPtr res, ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data) ((delegate* unmanaged<IntPtr, IntPtr, uint, void*, void*, uint, uint, void>)Vt(c)[Ctx_UpdateSubresource])(c, res, 0, null, p, 0, 0);
    }

    public static void VSSetConstantBuffer(IntPtr c, uint slot, IntPtr b) => ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Vt(c)[Ctx_VSSetConstantBuffers])(c, slot, 1, &b);
    public static void PSSetConstantBuffer(IntPtr c, uint slot, IntPtr b) => ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Vt(c)[Ctx_PSSetConstantBuffers])(c, slot, 1, &b);
    /// AddRef'd (or zero).
    public static IntPtr VSGetConstantBuffer(IntPtr c, uint slot) { IntPtr b = IntPtr.Zero; ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Vt(c)[Ctx_VSGetConstantBuffers])(c, slot, 1, &b); return b; }
    public static IntPtr PSGetConstantBuffer(IntPtr c, uint slot) { IntPtr b = IntPtr.Zero; ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Vt(c)[Ctx_PSGetConstantBuffers])(c, slot, 1, &b); return b; }

    public static int CreateRasterizerState(IntPtr dev, in RasterizerDesc d, out IntPtr rs)
    {
        IntPtr r; int hr;
        fixed (RasterizerDesc* p = &d) hr = ((delegate* unmanaged<IntPtr, RasterizerDesc*, IntPtr*, int>)Vt(dev)[Dev_CreateRasterizerState])(dev, p, &r);
        rs = hr < 0 ? IntPtr.Zero : r; return hr;
    }

    public static int CreateBlendState(IntPtr dev, in BlendDesc d, out IntPtr bs)
    {
        IntPtr r = IntPtr.Zero;
        int hr;
        fixed (BlendDesc* p = &d) hr = ((delegate* unmanaged<IntPtr, BlendDesc*, IntPtr*, int>)Vt(dev)[20])(dev, p, &r);
        bs = r;
        return hr;
    }

    public static void VSSetShaderResource(IntPtr c, uint slot, IntPtr view) => ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)Vt(c)[25])(c, slot, 1, &view);

    public static int CreateDepthStencilState(IntPtr dev, in DepthStencilDesc d, out IntPtr dss)
    {
        IntPtr r; int hr;
        fixed (DepthStencilDesc* p = &d) hr = ((delegate* unmanaged<IntPtr, DepthStencilDesc*, IntPtr*, int>)Vt(dev)[Dev_CreateDepthStencilState])(dev, p, &r);
        dss = hr < 0 ? IntPtr.Zero : r; return hr;
    }

    /// ID3D11Device1::CreateDeviceContextState(Flags, pFeatureLevels, FeatureLevels, SDKVersion, EmulatedInterface, pChosenFeatureLevel, ppContextState)
    public static int CreateDeviceContextState(IntPtr dev1, uint flags, int featureLevel, Guid emulated, out IntPtr state)
    {
        IntPtr r; int chosen;
        int hr = ((delegate* unmanaged<IntPtr, uint, int*, uint, uint, Guid*, int*, IntPtr*, int>)Vt(dev1)[Dev1_CreateDeviceContextState])
            (dev1, flags, &featureLevel, 1, SDK_VERSION, &emulated, &chosen, &r);
        state = hr < 0 ? IntPtr.Zero : r; return hr;
    }

    /// ID3D11DeviceChild::SetPrivateData(WKPDID_D3DDebugObjectName) — names show up in debug layer messages / PIX / RenderDoc.
    public static void SetName(IntPtr child, string name)
    {
        if (child == IntPtr.Zero) return;
        var g = WKPDID_D3DDebugObjectName;
        var bytes = System.Text.Encoding.ASCII.GetBytes(name);
        fixed (byte* b = bytes) ((delegate* unmanaged<IntPtr, Guid*, uint, void*, int>)Vt(child)[Child_SetPrivateData])(child, &g, (uint)bytes.Length, b);
    }

    // ---- ID3D11DeviceContext / ID3D11DeviceContext1
    public static int ContextType(IntPtr c) => ((delegate* unmanaged<IntPtr, int>)Vt(c)[Ctx_GetType])(c);
    public static void Draw(IntPtr c, uint count, uint start) => ((delegate* unmanaged<IntPtr, uint, uint, void>)Vt(c)[Ctx_Draw])(c, count, start);

    public static void VSSetShader(IntPtr c, IntPtr s) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, void>)Vt(c)[Ctx_VSSetShader])(c, s, IntPtr.Zero, 0);
    public static void PSSetShader(IntPtr c, IntPtr s) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, void>)Vt(c)[Ctx_PSSetShader])(c, s, IntPtr.Zero, 0);
    public static void GSSetShader(IntPtr c, IntPtr s) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, void>)Vt(c)[Ctx_GSSetShader])(c, s, IntPtr.Zero, 0);
    public static void HSSetShader(IntPtr c, IntPtr s) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, void>)Vt(c)[Ctx_HSSetShader])(c, s, IntPtr.Zero, 0);
    public static void DSSetShader(IntPtr c, IntPtr s) => ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, uint, void>)Vt(c)[Ctx_DSSetShader])(c, s, IntPtr.Zero, 0);

    /// xxGetShader(ppShader, ppClassInstances=null, pNumClassInstances=null). Returned shader is AddRef'd. Class instances not preserved (Rhino not expected to use them).
    static IntPtr GetShader(IntPtr c, int slot)
    {
        IntPtr s = IntPtr.Zero;
        ((delegate* unmanaged<IntPtr, IntPtr*, IntPtr, IntPtr, void>)Vt(c)[slot])(c, &s, IntPtr.Zero, IntPtr.Zero);
        return s;
    }
    public static IntPtr VSGetShader(IntPtr c) => GetShader(c, Ctx_VSGetShader);
    public static IntPtr PSGetShader(IntPtr c) => GetShader(c, Ctx_PSGetShader);
    public static IntPtr GSGetShader(IntPtr c) => GetShader(c, Ctx_GSGetShader);
    public static IntPtr HSGetShader(IntPtr c) => GetShader(c, Ctx_HSGetShader);
    public static IntPtr DSGetShader(IntPtr c) => GetShader(c, Ctx_DSGetShader);

    public static void IASetInputLayout(IntPtr c, IntPtr l) => ((delegate* unmanaged<IntPtr, IntPtr, void>)Vt(c)[Ctx_IASetInputLayout])(c, l);
    public static IntPtr IAGetInputLayout(IntPtr c) { IntPtr l = IntPtr.Zero; ((delegate* unmanaged<IntPtr, IntPtr*, void>)Vt(c)[Ctx_IAGetInputLayout])(c, &l); return l; }
    public static void IASetPrimitiveTopology(IntPtr c, int t) => ((delegate* unmanaged<IntPtr, int, void>)Vt(c)[Ctx_IASetPrimitiveTopology])(c, t);
    public static int IAGetPrimitiveTopology(IntPtr c) { int t = 0; ((delegate* unmanaged<IntPtr, int*, void>)Vt(c)[Ctx_IAGetPrimitiveTopology])(c, &t); return t; }

    public static void RSSetState(IntPtr c, IntPtr s) => ((delegate* unmanaged<IntPtr, IntPtr, void>)Vt(c)[Ctx_RSSetState])(c, s);
    public static IntPtr RSGetState(IntPtr c) { IntPtr s = IntPtr.Zero; ((delegate* unmanaged<IntPtr, IntPtr*, void>)Vt(c)[Ctx_RSGetState])(c, &s); return s; }

    public static int RSGetViewports(IntPtr c, Span<Viewport> vps)
    {
        uint n = 0;
        ((delegate* unmanaged<IntPtr, uint*, Viewport*, void>)Vt(c)[Ctx_RSGetViewports])(c, &n, null); // count query
        n = Math.Min(n, (uint)vps.Length);
        fixed (Viewport* p = vps) ((delegate* unmanaged<IntPtr, uint*, Viewport*, void>)Vt(c)[Ctx_RSGetViewports])(c, &n, p);
        return (int)n;
    }
    public static void RSSetViewports(IntPtr c, ReadOnlySpan<Viewport> vps)
    {
        fixed (Viewport* p = vps) ((delegate* unmanaged<IntPtr, uint, Viewport*, void>)Vt(c)[Ctx_RSSetViewports])(c, (uint)vps.Length, p);
    }

    /// OMSetBlendState(pBlendState, BlendFactor[4] (null = 1,1,1,1), SampleMask)
    public static void OMSetBlendState(IntPtr c, IntPtr bs, float* factor, uint mask) => ((delegate* unmanaged<IntPtr, IntPtr, float*, uint, void>)Vt(c)[Ctx_OMSetBlendState])(c, bs, factor, mask);
    public static IntPtr OMGetBlendState(IntPtr c, float* factor, out uint mask)
    {
        IntPtr bs = IntPtr.Zero; uint m = 0;
        ((delegate* unmanaged<IntPtr, IntPtr*, float*, uint*, void>)Vt(c)[Ctx_OMGetBlendState])(c, &bs, factor, &m);
        mask = m; return bs;
    }
    public static void OMSetDepthStencilState(IntPtr c, IntPtr dss, uint stencilRef) => ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Vt(c)[Ctx_OMSetDepthStencilState])(c, dss, stencilRef);
    public static IntPtr OMGetDepthStencilState(IntPtr c, out uint stencilRef)
    {
        IntPtr d = IntPtr.Zero; uint r = 0;
        ((delegate* unmanaged<IntPtr, IntPtr*, uint*, void>)Vt(c)[Ctx_OMGetDepthStencilState])(c, &d, &r);
        stencilRef = r; return d;
    }

    /// Fills rtvs (len MaxRenderTargets) + dsv, all AddRef'd.
    public static void OMGetRenderTargets(IntPtr c, Span<IntPtr> rtvs, out IntPtr dsv)
    {
        IntPtr d = IntPtr.Zero;
        fixed (IntPtr* p = rtvs) ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr*, void>)Vt(c)[Ctx_OMGetRenderTargets])(c, (uint)rtvs.Length, p, &d);
        dsv = d;
    }
    public static void OMSetRenderTargets(IntPtr c, ReadOnlySpan<IntPtr> rtvs, IntPtr dsv)
    {
        fixed (IntPtr* p = rtvs) ((delegate* unmanaged<IntPtr, uint, IntPtr*, IntPtr, void>)Vt(c)[Ctx_OMSetRenderTargets])(c, (uint)rtvs.Length, rtvs.Length == 0 ? null : p, dsv);
    }

    /// ID3D11DeviceContext1::SwapDeviceContextState(pState, ppPreviousState (AddRef'd, may be null))
    public static IntPtr SwapDeviceContextState(IntPtr c1, IntPtr state, bool wantPrevious)
    {
        IntPtr prev = IntPtr.Zero;
        ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, void>)Vt(c1)[Ctx1_SwapDeviceContextState])(c1, state, wantPrevious ? &prev : null);
        return prev;
    }

    // ---- ID3DUserDefinedAnnotation (PIX / RenderDoc / VS Graphics Diagnostics event markers)
    public static void BeginEvent(IntPtr ann, string name)
    {
        fixed (char* s = name) ((delegate* unmanaged<IntPtr, char*, int>)Vt(ann)[Ann_BeginEvent])(ann, s);
    }
    public static void EndEvent(IntPtr ann) => ((delegate* unmanaged<IntPtr, int>)Vt(ann)[Ann_EndEvent])(ann);

    // ---- d3dcompiler_47.dll (in System32 on Win10+). Loaded lazily, fail soft.
    static delegate* unmanaged<void*, nuint, byte*, void*, void*, byte*, byte*, uint, uint, IntPtr*, IntPtr*, int> d3dCompile;
    static bool d3dCompileTried;
    static readonly object d3dCompileLock = new();

    public const uint D3DCOMPILE_OPTIMIZATION_LEVEL3 = 1 << 15;

    /// D3DCompile(pSrcData, SrcDataSize, pSourceName, pDefines, pInclude, pEntrypoint, pTarget, Flags1, Flags2, ppCode, ppErrorMsgs).
    /// Returns bytecode or throws with the compiler log.
    public static byte[] Compile(string hlsl, string entry, string target, string sourceName)
    {
        lock (d3dCompileLock)
            if (!d3dCompileTried)
            {
                if (NativeLibrary.TryLoad("d3dcompiler_47.dll", out var lib) && NativeLibrary.TryGetExport(lib, "D3DCompile", out var fn))
                    d3dCompile = (delegate* unmanaged<void*, nuint, byte*, void*, void*, byte*, byte*, uint, uint, IntPtr*, IntPtr*, int>)fn;
                d3dCompileTried = true;
            }
        if (d3dCompile == null) throw new InvalidOperationException("d3dcompiler_47.dll / D3DCompile not available");

        var src = System.Text.Encoding.UTF8.GetBytes(hlsl);
        var e = Z(entry); var t = Z(target); var n = Z(sourceName);
        IntPtr code = IntPtr.Zero, errs = IntPtr.Zero;
        try
        {
            int hr;
            fixed (byte* ps = src) fixed (byte* pe = e) fixed (byte* pt = t) fixed (byte* pn = n)
                hr = d3dCompile(ps, (nuint)src.Length, pn, null, null, pe, pt, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errs);
            if (hr < 0 || code == IntPtr.Zero)
                throw new InvalidOperationException($"D3DCompile {target} failed 0x{hr:X8}: {BlobString(errs)}");
            return BlobBytes(code);
        }
        finally { SafeRelease(ref code); SafeRelease(ref errs); }
    }

    static byte[] Z(string s) => System.Text.Encoding.ASCII.GetBytes(s + "\0");

    static byte[] BlobBytes(IntPtr blob)
    {
        var p = ((delegate* unmanaged<IntPtr, void*>)Vt(blob)[Blob_GetBufferPointer])(blob);
        var n = ((delegate* unmanaged<IntPtr, nuint>)Vt(blob)[Blob_GetBufferSize])(blob);
        return new ReadOnlySpan<byte>(p, checked((int)n)).ToArray();
    }

    static string BlobString(IntPtr blob) => blob == IntPtr.Zero ? "" : System.Text.Encoding.UTF8.GetString(BlobBytes(blob)).TrimEnd('\0', '\n');
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct BlendDesc
{
    public int AlphaToCoverageEnable, IndependentBlendEnable;
    public fixed int RenderTarget[64];

    public static BlendDesc Alpha
    {
        get
        {
            var d = new BlendDesc();
            for (int i = 0; i < 8; i++)
            {
                d.RenderTarget[i * 8 + 0] = i == 0 ? 1 : 0;
                d.RenderTarget[i * 8 + 1] = 5;
                d.RenderTarget[i * 8 + 2] = 6;
                d.RenderTarget[i * 8 + 3] = 1;
                d.RenderTarget[i * 8 + 4] = 2;
                d.RenderTarget[i * 8 + 5] = 6;
                d.RenderTarget[i * 8 + 6] = 1;
                d.RenderTarget[i * 8 + 7] = 0xf;
            }
            return d;
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
struct Viewport { public float X, Y, Width, Height, MinDepth, MaxDepth; }

/// D3D11_BUFFER_DESC (24 bytes)
[StructLayout(LayoutKind.Sequential)]
struct BufferDesc { public uint ByteWidth; public int Usage; public uint BindFlags, CPUAccessFlags, MiscFlags, StructureByteStride; }

[StructLayout(LayoutKind.Sequential)]
struct Texture2DDesc
{
    public uint Width, Height, MipLevels, ArraySize;
    public int Format;
    public uint SampleCount, SampleQuality;
    public int Usage;
    public uint BindFlags, CPUAccessFlags, MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct SamplerDesc
{
    public int Filter, AddressU, AddressV, AddressW;
    public float MipLODBias;
    public uint MaxAnisotropy;
    public int ComparisonFunc;
    public fixed float BorderColor[4];
    public float MinLOD, MaxLOD;

    public static SamplerDesc Clamp(bool linear) => new() { Filter = linear ? 0x15 : 0, AddressU = 3, AddressV = 3, AddressW = 3, MaxAnisotropy = 1, ComparisonFunc = 1, MaxLOD = float.MaxValue };
}

/// D3D11_RASTERIZER_DESC (40 bytes)
[StructLayout(LayoutKind.Sequential)]
struct RasterizerDesc
{
    public int FillMode, CullMode, FrontCounterClockwise, DepthBias;
    public float DepthBiasClamp, SlopeScaledDepthBias;
    public int DepthClipEnable, ScissorEnable, MultisampleEnable, AntialiasedLineEnable;

    /// FILL_SOLID(3), CULL_NONE(1), depth clip on, everything else default.
    public static RasterizerDesc SolidNoCull => new() { FillMode = 3, CullMode = 1, DepthClipEnable = 1 };
}

/// D3D11_DEPTH_STENCILOP_DESC
[StructLayout(LayoutKind.Sequential)]
struct StencilOpDesc { public int FailOp, DepthFailOp, PassOp, Func; }

/// D3D11_DEPTH_STENCIL_DESC (52 bytes; 2 bytes padding after the masks)
[StructLayout(LayoutKind.Sequential)]
struct DepthStencilDesc
{
    public int DepthEnable, DepthWriteMask, DepthFunc, StencilEnable;
    public byte StencilReadMask, StencilWriteMask;
    public StencilOpDesc FrontFace, BackFace;

    /// Depth test+write off, stencil off. KEEP(1) / ALWAYS(8).
    public static DepthStencilDesc Disabled
    {
        get
        {
            var op = new StencilOpDesc { FailOp = 1, DepthFailOp = 1, PassOp = 1, Func = 8 };
            return new() { DepthEnable = 0, DepthWriteMask = 0, DepthFunc = 8, StencilEnable = 0, StencilReadMask = 0xff, StencilWriteMask = 0xff, FrontFace = op, BackFace = op };
        }
    }

    public static DepthStencilDesc Test(int func)
    {
        var d = Disabled;
        d.DepthEnable = 1; d.DepthWriteMask = 1; d.DepthFunc = func;
        return d;
    }
}
