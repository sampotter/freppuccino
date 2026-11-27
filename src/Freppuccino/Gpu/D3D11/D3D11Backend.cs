using System.Runtime.Versioning;
using Freppuccino.Shaders;
using Rhino;
using Rhino.Display;
using static Freppuccino.Gpu.D3D11.D3D11Native;

namespace Freppuccino.Gpu.D3D11;

/// How we isolate our draw from Rhino's D3D11 state.
public enum D3D11StateMode
{
    /// Swap if available (immediate context + CreateDeviceContextState ok), else Manual.
    Auto,
    /// ID3D11DeviceContext1::SwapDeviceContextState to a private state object; RTV/DSV + viewports copied across.
    Swap,
    /// Save only what we change (IA topology/layout, VS/PS/GS/HS/DS, RS, blend, DSS), restore after.
    Manual,
}

/// Draws a ShaderSource on Rhino's own ID3D11Device / ID3D11DeviceContext1, inside Rhino's current pass.
/// Uses Rhino's currently bound render targets + viewports as-is (assumption, see docs/windows-validation.md).
public sealed class D3D11Backend : IGpuBackend
{
    /// Test knob (settable from run_csharp / env FREPPUCINO_D3D11_STATE=swap|manual). Takes effect next frame.
    public static D3D11StateMode StateMode { get; set; } =
        Enum.TryParse<D3D11StateMode>(Environment.GetEnvironmentVariable("FREPPUCINO_D3D11_STATE"), true, out var m) ? m : D3D11StateMode.Auto;

    public static bool Occlusion { get; set; } = Environment.GetEnvironmentVariable("FREPPUCCINO_OCCLUSION") != "0";

    sealed record Program(IntPtr Vs, IntPtr Ps);

    static readonly IntPtr[] NoViews = new IntPtr[4];

    sealed class Target
    {
        public IntPtr Tex, Rtv, Srv;
        public void Release() { SafeRelease(ref Srv); SafeRelease(ref Rtv); SafeRelease(ref Tex); }
    }

    readonly Dictionary<string, Program> programs = new();
    readonly Dictionary<string, string> failed = new(); // hash → error; never retried (cleared with the device)
    readonly HashSet<string> logged = new();
    (string Hash, Task<Bytecode> Task)? running, finished;
    ShaderSource? waiting;

    IntPtr device;          // Rhino's, not owned; identity only (cache key)
    IntPtr device1;         // ours (QI'd)
    IntPtr rs, dss;         // ours
    IntPtr cb;              // ours: Uniforms.MaxBytes constant buffer, b0
    IntPtr ctxState;        // ours (ID3DDeviceContextState), lazily created
    bool ctxStateFailed;
    int featureLevel;
    bool initOk;
    IntPtr postVs, occlusionPs, blurPs, compositePs, pointSampler, linearSampler, postCb, depthDss;
    IntPtr outlineVs, outlinePsMs, outlinePs1, alphaBlend;
    bool outlineFailed;
    byte[]? outlineUniforms;
    readonly float[] gbufClip = new float[16];
    Viewport gbufViewport;

    public int GBufferSerial => TraceSource.Serial;
    bool postFailed;
    readonly Target gColor = new(), gPos = new(), gNrm = new(), aoA = new(), aoB = new();
    int targetW, targetH;
    readonly float[] clip = new float[16];

    public static float DepthBiasPx { get; set; } = 0f;
    public bool WroteDepth { get; private set; }
    float traceMinDepth, traceMaxDepth = 1;
    int traceSerial;
    bool gbufferDrawn;
    public (Guid Viewport, int Serial, int W, int H) TraceSource { get; private set; }
    public (Rhino.Geometry.Transform Clip, double Near, double Far) TraceDraw { get; private set; }

    [SupportedOSPlatform("windows")]
    public unsafe (float W, double Depth)[]? TraceFrepDepth(IntPtr dev, IntPtr ctx, (int X, int Y)[] px)
    {
        if (gPos.Tex == IntPtr.Zero || dev != device) return null;
        IntPtr st = IntPtr.Zero;
        try
        {
            Check(CreateTexture2D(dev, new Texture2DDesc { Width = 64, Height = 1, MipLevels = 1, ArraySize = 1, Format = FORMAT_R32G32B32A32_FLOAT, SampleCount = 1, Usage = 3, CPUAccessFlags = 0x20000 }, out st), "CreateTexture2D");
            uint* box = stackalloc uint[6];
            for (int i = 0; i < px.Length && i < 64; i++)
            {
                int x = Math.Clamp(px[i].X, 0, targetW - 1), y = Math.Clamp(px[i].Y, 0, targetH - 1);
                box[0] = (uint)x; box[1] = (uint)y; box[2] = 0; box[3] = (uint)x + 1; box[4] = (uint)y + 1; box[5] = 1;
                ((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, uint*, void>)(*(void***)ctx)[46])(ctx, st, 0, (uint)i, 0, 0, gPos.Tex, 0, box);
            }
            void** map = stackalloc void*[2];
            if (((delegate* unmanaged<IntPtr, IntPtr, uint, int, uint, void**, int>)(*(void***)ctx)[14])(ctx, st, 0, 1, 0, map) < 0) return null;
            var f = (float*)map[0];
            var r = new (float, double)[Math.Min(px.Length, 64)];
            for (int i = 0; i < r.Length; i++)
            {
                double x = f[i * 4], y = f[i * 4 + 1], z = f[i * 4 + 2];
                double cz = clip[8] * x + clip[9] * y + clip[10] * z + clip[11], cw = clip[12] * x + clip[13] * y + clip[14] * z + clip[15];
                r[i] = (f[i * 4 + 3], traceMinDepth + (traceMaxDepth - traceMinDepth) * Math.Clamp(0.5 - 0.5 * cz / cw, 0, 1));
            }
            ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)(*(void***)ctx)[15])(ctx, st, 0);
            return r;
        }
        finally { SafeRelease(ref st); }
    }

    public bool Draw(DisplayPipeline pipeline, ShaderSource program, ReadOnlySpan<byte> uniforms) => Draw(pipeline, program, uniforms, false);

    public bool DrawOccluded(DisplayPipeline pipeline, ShaderSource program, ReadOnlySpan<byte> uniforms, bool depth = true)
    {
        writeDepth = depth;
        return Draw(pipeline, program, uniforms, true);
    }

    bool writeDepth;

    bool Draw(DisplayPipeline pipeline, ShaderSource program, ReadOnlySpan<byte> uniforms, bool occluded)
    {
        WroteDepth = false;
        if (!OperatingSystem.IsWindows()) return false;
        try { return DrawWin(pipeline, program, uniforms, occluded); }
        catch (Exception ex) { LogOnce("draw:" + ex.GetType().Name, "Freppuccino D3D11: " + ex); return false; }
    }

    [SupportedOSPlatform("windows")]
    bool DrawWin(DisplayPipeline pipeline, ShaderSource program, ReadOnlySpan<byte> uniforms, bool occluded)
    {
        var dev = pipeline.GetDirect3d11Device();
        var ctx = pipeline.GetDirect3d11DeviceContext();
        if (dev == IntPtr.Zero || ctx == IntPtr.Zero) { LogOnce("null", "Freppuccino D3D11: null device/context"); return false; }
        if (dev != device) { ReleaseAll(); initOk = Init(dev); }
        if (!initOk) return false;

        var prog = GetProgram(program, wait: !program.Background);
        if (prog == null) return false;

        bool swap = StateMode switch
        {
            D3D11StateMode.Manual => false,
            _ => EnsureContextState(ctx),
        };
        if (StateMode == D3D11StateMode.Swap && !swap) LogOnce("swapfallback", "Freppuccino D3D11: Swap requested but unavailable; using Manual");

        var draw = new DrawArgs(prog, uniforms.IsEmpty ? null : uniforms.ToArray(), program.VertexCount);
        var ann = QI(ctx, IID_ID3DUserDefinedAnnotation);
        if (ann != IntPtr.Zero) BeginEvent(ann, "Freppuccino");
        try
        {
            if (swap && occluded && EnsurePost())
            {
                var m = pipeline.Viewport.GetTransform(Rhino.DocObjects.CoordinateSystem.World, Rhino.DocObjects.CoordinateSystem.Clip);
                for (int i = 0; i < 16; i++) clip[i] = (float)m[i / 4, i % 4];
                pipeline.Viewport.GetFrustum(out _, out _, out _, out _, out var fn, out var ff);
                TraceDraw = (m, fn, ff);
                gbufferDrawn = false;
                WroteDepth = DrawOccludedSwapped(ctx, draw, Occlusion);
                if (gbufferDrawn) outlineUniforms = draw.Uniforms;
                if (gbufferDrawn) TraceSource = (pipeline.Viewport?.Id ?? Guid.Empty, ++traceSerial, targetW, targetH);
            }
            else if (swap) DrawSwapped(ctx, draw);
            else DrawManual(ctx, draw);
        }
        finally
        {
            if (ann != IntPtr.Zero) { EndEvent(ann); SafeRelease(ref ann); }
        }
        return true;
    }

    public bool ExpectsDepth(DisplayPipeline pipeline)
    {
        if (!OperatingSystem.IsWindows() || StateMode == D3D11StateMode.Manual) return false;
        try
        {
            var dev = pipeline.GetDirect3d11Device();
            var ctx = pipeline.GetDirect3d11DeviceContext();
            if (dev == IntPtr.Zero || ctx == IntPtr.Zero) return false;
            if (dev != device) { ReleaseAll(); initOk = Init(dev); }
            if (!initOk || !EnsureContextState(ctx) || !EnsurePost()) return false;
            Span<IntPtr> rtvs = stackalloc IntPtr[MaxRenderTargets];
            OMGetRenderTargets(ctx, rtvs, out var dsv);
            for (int i = 0; i < rtvs.Length; i++) SafeRelease(ref rtvs[i]);
            bool has = dsv != IntPtr.Zero;
            SafeRelease(ref dsv);
            Span<Viewport> vps = stackalloc Viewport[MaxViewports];
            int nvp = RSGetViewports(ctx, vps);
            int w = nvp > 0 ? (int)vps[0].Width : 0, h = nvp > 0 ? (int)vps[0].Height : 0;
            return has && w > 0 && h > 0 && EnsureTargets(w, h);
        }
        catch (Exception ex) { LogOnce("expect:" + ex.GetType().Name, "Freppuccino D3D11: " + ex); return false; }
    }

    [SupportedOSPlatform("windows")]
    bool Init(IntPtr dev)
    {
        device = dev;
        featureLevel = FeatureLevel(dev);
        if (featureLevel < FEATURE_LEVEL_11_0)
        {
            LogOnce("fl", $"Freppuccino D3D11: feature level 0x{featureLevel:X} < 11_0; SM5 shaders unsupported");
            return false;
        }
        device1 = QI(dev, IID_ID3D11Device1); // only needed for Swap
        Check(CreateRasterizerState(dev, RasterizerDesc.SolidNoCull, out rs), "CreateRasterizerState");
        Check(CreateDepthStencilState(dev, DepthStencilDesc.Disabled, out dss), "CreateDepthStencilState");
        Check(CreateBuffer(dev, new BufferDesc { ByteWidth = Uniforms.MaxBytes, Usage = USAGE_DEFAULT, BindFlags = BIND_CONSTANT_BUFFER }, out cb), "CreateBuffer");
        SetName(rs, "Freppuccino RS"); SetName(dss, "Freppuccino DSS"); SetName(cb, "Freppuccino CB");
        return true;
    }

    /// Lazily creates the private context state. Requires immediate context + ID3D11Device1.
    [SupportedOSPlatform("windows")]
    bool EnsureContextState(IntPtr ctx)
    {
        if (ctxState != IntPtr.Zero) return true;
        if (ctxStateFailed) return false;
        ctxStateFailed = true; // until proven otherwise
        int type = ContextType(ctx);
        if (type != DEVICE_CONTEXT_IMMEDIATE) { LogOnce("deferred", $"Freppuccino D3D11: context type {type} (not immediate); using Manual state save/restore"); return false; }
        if (device1 == IntPtr.Zero) { LogOnce("dev1", "Freppuccino D3D11: no ID3D11Device1; using Manual"); return false; }
        // Mirror device threading flag (required to match). Emulate ID3D11Device1 at Rhino's feature level.
        uint flags = (CreationFlags(device) & CREATE_DEVICE_SINGLETHREADED) != 0 ? CONTEXT_STATE_SINGLETHREADED : 0;
        int hr = CreateDeviceContextState(device1, flags, featureLevel, IID_ID3D11Device1, out ctxState);
        if (hr < 0) { LogOnce("cdcs", $"Freppuccino D3D11: CreateDeviceContextState failed 0x{hr:X8}; using Manual"); return false; }
        SetName(ctxState, "Freppuccino ContextState");
        ctxStateFailed = false;
        return true;
    }

    [SupportedOSPlatform("windows")]
    void DrawSwapped(IntPtr ctx, DrawArgs draw)
    {
        // Our state object starts empty: carry Rhino's RT/DS views + viewports across.
        Span<IntPtr> rtvs = stackalloc IntPtr[MaxRenderTargets];
        Span<Viewport> vps = stackalloc Viewport[MaxViewports];
        OMGetRenderTargets(ctx, rtvs, out var dsv);
        int nvp = RSGetViewports(ctx, vps);
        IntPtr prev = SwapDeviceContextState(ctx, ctxState, wantPrevious: true);
        try
        {
            OMSetRenderTargets(ctx, rtvs[..1], dsv);
            RSSetViewports(ctx, vps[..nvp]);
            Bind(ctx, draw);
            D3D11Native.Draw(ctx, draw.VertexCount, 0);
            // Don't let our (inactive) state hold Rhino's views between frames.
            OMSetRenderTargets(ctx, ReadOnlySpan<IntPtr>.Empty, IntPtr.Zero);
        }
        finally
        {
            SwapDeviceContextState(ctx, prev, wantPrevious: false);
            SafeRelease(ref prev);
            for (int i = 0; i < rtvs.Length; i++) SafeRelease(ref rtvs[i]);
            SafeRelease(ref dsv);
        }
    }

    [SupportedOSPlatform("windows")]
    bool DrawOccludedSwapped(IntPtr ctx, DrawArgs draw, bool ao)
    {
        Span<IntPtr> rtvs = stackalloc IntPtr[MaxRenderTargets];
        Span<Viewport> vps = stackalloc Viewport[MaxViewports];
        OMGetRenderTargets(ctx, rtvs, out var dsv);
        int nvp = RSGetViewports(ctx, vps);
        IntPtr prev = SwapDeviceContextState(ctx, ctxState, wantPrevious: true);
        try
        {
            var vp = nvp > 0 ? vps[0] : default;
            int w = (int)vp.Width, h = (int)vp.Height;
            if (w <= 0 || h <= 0 || !EnsureTargets(w, h))
            {
                OMSetRenderTargets(ctx, rtvs[..1], dsv);
                RSSetViewports(ctx, vps[..nvp]);
                Bind(ctx, draw);
                D3D11Native.Draw(ctx, draw.VertexCount, 0);
                return false;
            }
            int hw = (w + 1) / 2, hh = (h + 1) / 2;

            ClearRenderTargetView(ctx, gColor.Rtv, 0, 0, 0, 0);
            ClearRenderTargetView(ctx, gPos.Rtv, 0, 0, 0, 0);
            ClearRenderTargetView(ctx, gNrm.Rtv, 0, 0, 0, 0);
            OMSetRenderTargets(ctx, [gColor.Rtv, gPos.Rtv, gNrm.Rtv], IntPtr.Zero);
            RSSetViewports(ctx, [new Viewport { Width = w, Height = h, MaxDepth = 1 }]);
            Bind(ctx, draw);
            D3D11Native.Draw(ctx, draw.VertexCount, 0);
            gbufferDrawn = true;
            gbufViewport = vp;
            clip.CopyTo(gbufClip, 0);

            PSSetSamplers(ctx, 0, [pointSampler, linearSampler]);
            VSSetShader(ctx, postVs);
            PSSetConstantBuffer(ctx, 1, postCb);
            RSSetViewports(ctx, [new Viewport { Width = hw, Height = hh, MaxDepth = 1 }]);

            if (ao)
            {
                PostPass(ctx, occlusionPs, aoA.Rtv, [gPos.Srv, gNrm.Srv, IntPtr.Zero, IntPtr.Zero], w, h, hw, hh, 0, 0);
                PostPass(ctx, blurPs, aoB.Rtv, [gPos.Srv, gNrm.Srv, IntPtr.Zero, aoA.Srv], w, h, hw, hh, 1, 0);
                PostPass(ctx, blurPs, aoA.Rtv, [gPos.Srv, gNrm.Srv, IntPtr.Zero, aoB.Srv], w, h, hw, hh, 0, 1);
            }

            OMSetRenderTargets(ctx, rtvs, dsv);
            RSSetViewports(ctx, vps[..nvp]);
            PSSetShader(ctx, compositePs);
            PSSetShaderResources(ctx, 0, [gPos.Srv, gNrm.Srv, gColor.Srv, aoA.Srv]);
            OMSetDepthStencilState(ctx, writeDepth ? depthDss : dss, 0);
            traceMinDepth = vp.MinDepth; traceMaxDepth = vp.MaxDepth;
            SetPost(ctx, w, h, hw, hh, 0, 0, vp.X, vp.Y, 0, ao ? 1 : 0, vp.MinDepth, vp.MaxDepth);
            D3D11Native.Draw(ctx, 3, 0);
            OMSetDepthStencilState(ctx, dss, 0);
            SetPost(ctx, w, h, hw, hh, 0, 0, vp.X, vp.Y, 1, 0, vp.MinDepth, vp.MaxDepth);
            D3D11Native.Draw(ctx, 3, 0);
            return writeDepth && dsv != IntPtr.Zero;
        }
        finally
        {
            PSSetShaderResources(ctx, 0, NoViews);
            OMSetRenderTargets(ctx, ReadOnlySpan<IntPtr>.Empty, IntPtr.Zero);
            SwapDeviceContextState(ctx, prev, wantPrevious: false);
            SafeRelease(ref prev);
            for (int i = 0; i < rtvs.Length; i++) SafeRelease(ref rtvs[i]);
            SafeRelease(ref dsv);
        }
    }

    [SupportedOSPlatform("windows")]
    void PostPass(IntPtr ctx, IntPtr ps, IntPtr rtv, ReadOnlySpan<IntPtr> srvs, int w, int h, int hw, int hh, float dx, float dy)
    {
        Span<IntPtr> none = stackalloc IntPtr[4];
        PSSetShaderResources(ctx, 0, none);
        OMSetRenderTargets(ctx, [rtv], IntPtr.Zero);
        SetPost(ctx, w, h, hw, hh, dx, dy, 0, 0);
        PSSetShader(ctx, ps);
        PSSetShaderResources(ctx, 0, srvs);
        D3D11Native.Draw(ctx, 3, 0);
        PSSetShaderResources(ctx, 0, none);
    }

    [SupportedOSPlatform("windows")]
    void SetPost(IntPtr ctx, int w, int h, int hw, int hh, float dx, float dy, float ox, float oy, float band = 0, float ao = 0, float minDepth = 0, float maxDepth = 1)
    {
        Span<float> v = stackalloc float[36];
        v.Clear();
        v[0] = w; v[1] = h; v[2] = hw; v[3] = hh; v[4] = dx; v[5] = dy; v[6] = ox; v[7] = oy;
        clip.CopyTo(v[8..]);
        v[24] = minDepth; v[25] = maxDepth;
        v[28] = band; v[29] = ao; v[30] = DepthBiasPx;
        UpdateSubresource(ctx, postCb, System.Runtime.InteropServices.MemoryMarshal.AsBytes(v));
    }

    public bool DrawOutlines(DisplayPipeline pipeline, int serial, float[] endpoints, System.Drawing.Color ink, float faint, float halfWidth, float pullPx)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (endpoints.Length < 8) return true;
        try { return DrawOutlinesWin(pipeline, serial, endpoints, ink, faint, halfWidth, pullPx); }
        catch (Exception ex) { LogOnce("outlines:" + ex.GetType().Name, "Freppuccino D3D11: outline pass failed: " + ex.Message); return false; }
    }

    public unsafe bool OutlinesPossible(DisplayPipeline pipeline)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var dev = pipeline.GetDirect3d11Device();
            var ctx = pipeline.GetDirect3d11DeviceContext();
            if (dev == IntPtr.Zero || dev != device || !initOk || ctxState == IntPtr.Zero || !EnsurePost() || !EnsureOutline()) return false;
            Span<IntPtr> rtvs = stackalloc IntPtr[MaxRenderTargets];
            OMGetRenderTargets(ctx, rtvs, out var dsv);
            IntPtr res = IntPtr.Zero;
            try
            {
                if (dsv == IntPtr.Zero || rtvs[0] == IntPtr.Zero) return false;
                ((delegate* unmanaged<IntPtr, IntPtr*, void>)(*(void***)dsv)[7])(dsv, &res);
                Texture2DDesc td;
                ((delegate* unmanaged<IntPtr, Texture2DDesc*, void>)(*(void***)res)[10])(res, &td);
                return DepthSrvFormat(td.Format) >= 0 && (td.BindFlags & BIND_SHADER_RESOURCE) != 0 && td.SampleCount <= 8;
            }
            finally
            {
                for (int i = 0; i < rtvs.Length; i++) SafeRelease(ref rtvs[i]);
                SafeRelease(ref res); SafeRelease(ref dsv);
            }
        }
        catch (Exception ex) { LogOnce("outlinesPossible:" + ex.GetType().Name, "Freppuccino D3D11: " + ex.Message); return false; }
    }

    [SupportedOSPlatform("windows")]
    bool EnsureOutline()
    {
        if (outlineVs != IntPtr.Zero) return true;
        if (outlineFailed) return false;
        outlineFailed = true;
        try
        {
            Check(CreateVertexShader(device, D3D11Native.Compile(OcclusionShaders.OutlineVertex, "main", "vs_5_0", "freppuccino_outline.vert.hlsl"), out outlineVs), "CreateVertexShader");
            Check(CreatePixelShader(device, D3D11Native.Compile(OcclusionShaders.OutlinePixelMs, "main", "ps_5_0", "freppuccino_outline_ms.hlsl"), out outlinePsMs), "CreatePixelShader");
            Check(CreatePixelShader(device, D3D11Native.Compile(OcclusionShaders.OutlinePixel1, "main", "ps_5_0", "freppuccino_outline_1.hlsl"), out outlinePs1), "CreatePixelShader");
            Check(CreateBlendState(device, BlendDesc.Alpha, out alphaBlend), "CreateBlendState");
            outlineFailed = false;
            return true;
        }
        catch (Exception ex)
        {
            LogOnce("outline", "Freppuccino D3D11: outline pass unavailable: " + ex.Message);
            ReleaseOutline();
            return false;
        }
    }

    void ReleaseOutline()
    {
        if (!OperatingSystem.IsWindows()) return;
        SafeRelease(ref outlineVs); SafeRelease(ref outlinePsMs); SafeRelease(ref outlinePs1); SafeRelease(ref alphaBlend);
    }

    bool Reject(string why)
    {
        LogOnce("outline-reject:" + why, $"Freppuccino D3D11: cutter outlines skipped this frame ({why}).");
        return false;
    }

    static int DepthSrvFormat(int typeless) => typeless switch { 19 or 20 => 21, 44 or 45 => 46, 39 or 40 => 41, 53 or 55 => 56, _ => -1 };

    [SupportedOSPlatform("windows")]
    unsafe bool DrawOutlinesWin(DisplayPipeline pipeline, int serial, float[] endpoints, System.Drawing.Color ink, float faint, float halfWidth, float pullPx)
    {
        var dev = pipeline.GetDirect3d11Device();
        var ctx = pipeline.GetDirect3d11DeviceContext();
        if (dev == IntPtr.Zero || dev != device || !initOk || ctxState == IntPtr.Zero || !EnsurePost() || !EnsureOutline()) return Reject("setup");
        if (outlineUniforms == null || gPos.Srv == IntPtr.Zero || TraceSource.Serial != serial || TraceSource.Viewport != (pipeline.Viewport?.Id ?? Guid.Empty)) return Reject("stale-gbuffer");
        if (pipeline.Viewport is not { } viewport) return Reject("stale-gbuffer");
        var now = viewport.GetTransform(Rhino.DocObjects.CoordinateSystem.World, Rhino.DocObjects.CoordinateSystem.Clip);
        for (int i = 0; i < 16; i++)
        {
            double a = now[i / 4, i % 4], b = gbufClip[i];
            if (Math.Abs(a - b) > 1e-5 * Math.Max(1.0, Math.Abs(a))) return Reject("camera-changed");
        }
        Span<IntPtr> rtvs = stackalloc IntPtr[MaxRenderTargets];
        Span<Viewport> vps = stackalloc Viewport[MaxViewports];
        IntPtr dsv = IntPtr.Zero, depthRes = IntPtr.Zero, depthSrv = IntPtr.Zero, segBuf = IntPtr.Zero, segSrv = IntPtr.Zero, prev = IntPtr.Zero;
        try
        {
            OMGetRenderTargets(ctx, rtvs, out dsv);
            int nvp = RSGetViewports(ctx, vps);
            if (dsv == IntPtr.Zero || rtvs[0] == IntPtr.Zero || nvp == 0) return Reject("no-targets");
            var cur = vps[0];
            if (cur.X != gbufViewport.X || cur.Y != gbufViewport.Y || cur.Width != gbufViewport.Width || cur.Height != gbufViewport.Height || cur.MinDepth != gbufViewport.MinDepth || cur.MaxDepth != gbufViewport.MaxDepth) return Reject("viewport-changed");
            ((delegate* unmanaged<IntPtr, IntPtr*, void>)(*(void***)dsv)[7])(dsv, &depthRes);
            Texture2DDesc td;
            ((delegate* unmanaged<IntPtr, Texture2DDesc*, void>)(*(void***)depthRes)[10])(depthRes, &td);
            int fmt = DepthSrvFormat(td.Format);
            if (fmt < 0 || (td.BindFlags & BIND_SHADER_RESOURCE) == 0 || td.SampleCount > 8) return Reject("depth-unreadable");
            uint* sd = stackalloc uint[6] { (uint)fmt, td.SampleCount > 1 ? 6u : 4u, 0, 1, 0, 0 };
            Check(((delegate* unmanaged<IntPtr, IntPtr, uint*, IntPtr*, int>)(*(void***)device)[7])(device, depthRes, sd, &depthSrv), "CreateShaderResourceView");
            int count = endpoints.Length / 4;
            Check(CreateBuffer(device, new BufferDesc { ByteWidth = (uint)(count * 16), Usage = USAGE_DEFAULT, BindFlags = BIND_SHADER_RESOURCE, MiscFlags = 0x40, StructureByteStride = 16 }, out segBuf), "CreateBuffer");
            UpdateSubresource(ctx, segBuf, System.Runtime.InteropServices.MemoryMarshal.AsBytes(endpoints.AsSpan()));
            uint* bd = stackalloc uint[6] { 0, 1, 0, (uint)count, 0, 0 };
            Check(((delegate* unmanaged<IntPtr, IntPtr, uint*, IntPtr*, int>)(*(void***)device)[7])(device, segBuf, bd, &segSrv), "CreateShaderResourceView");

            prev = SwapDeviceContextState(ctx, ctxState, wantPrevious: true);
            try
            {
                var vp = vps[0];
                Span<byte> full = stackalloc byte[Uniforms.MaxBytes];
                full.Clear();
                outlineUniforms.CopyTo(full);
                UpdateSubresource(ctx, cb, full);
                Span<float> v = stackalloc float[36];
                v.Clear();
                v[0] = vp.Width; v[1] = vp.Height; v[6] = vp.X; v[7] = vp.Y;
                gbufClip.CopyTo(v[8..]);
                v[24] = vp.MinDepth; v[25] = vp.MaxDepth; v[26] = td.Format is 53 or 55 ? 3e-5f : td.Format is 44 or 45 ? 2e-7f : 1e-6f;
                v[28] = pullPx; v[29] = halfWidth; v[30] = faint;
                v[32] = ink.R / 255f; v[33] = ink.G / 255f; v[34] = ink.B / 255f; v[35] = 1;
                UpdateSubresource(ctx, postCb, System.Runtime.InteropServices.MemoryMarshal.AsBytes(v));
                OMSetRenderTargets(ctx, rtvs[..1], IntPtr.Zero);
                RSSetViewports(ctx, vps[..nvp]);
                IASetPrimitiveTopology(ctx, TOPOLOGY_TRIANGLELIST);
                IASetInputLayout(ctx, IntPtr.Zero);
                VSSetShader(ctx, outlineVs);
                PSSetShader(ctx, td.SampleCount > 1 ? outlinePsMs : outlinePs1);
                GSSetShader(ctx, IntPtr.Zero); HSSetShader(ctx, IntPtr.Zero); DSSetShader(ctx, IntPtr.Zero);
                VSSetConstantBuffer(ctx, 0, cb); PSSetConstantBuffer(ctx, 0, cb);
                VSSetConstantBuffer(ctx, 1, postCb); PSSetConstantBuffer(ctx, 1, postCb);
                VSSetShaderResource(ctx, 4, segSrv);
                PSSetShaderResources(ctx, 0, [gPos.Srv, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, depthSrv]);
                RSSetState(ctx, rs);
                OMSetBlendState(ctx, alphaBlend, null, 0xffffffff);
                OMSetDepthStencilState(ctx, dss, 0);
                D3D11Native.Draw(ctx, (uint)(count / 2 * 6), 0);
            }
            finally
            {
                VSSetShaderResource(ctx, 4, IntPtr.Zero);
                PSSetShaderResources(ctx, 0, [IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero]);
                OMSetBlendState(ctx, IntPtr.Zero, null, 0xffffffff);
                OMSetRenderTargets(ctx, ReadOnlySpan<IntPtr>.Empty, IntPtr.Zero);
                SwapDeviceContextState(ctx, prev, wantPrevious: false);
                SafeRelease(ref prev);
            }
            return true;
        }
        finally
        {
            for (int i = 0; i < rtvs.Length; i++) SafeRelease(ref rtvs[i]);
            SafeRelease(ref dsv); SafeRelease(ref depthRes); SafeRelease(ref depthSrv); SafeRelease(ref segBuf); SafeRelease(ref segSrv);
        }
    }

    [SupportedOSPlatform("windows")]
    bool EnsurePost()
    {
        if (postVs != IntPtr.Zero) return true;
        if (postFailed) return false;
        postFailed = true;
        try
        {
            Check(CreateVertexShader(device, D3D11Native.Compile(OcclusionShaders.Vertex, "main", "vs_5_0", "freppuccino_post.vert.hlsl"), out postVs), "CreateVertexShader");
            Check(CreatePixelShader(device, D3D11Native.Compile(OcclusionShaders.Occlusion, "main", "ps_5_0", "freppuccino_occlusion.hlsl"), out occlusionPs), "CreatePixelShader");
            Check(CreatePixelShader(device, D3D11Native.Compile(OcclusionShaders.Blur, "main", "ps_5_0", "freppuccino_blur.hlsl"), out blurPs), "CreatePixelShader");
            Check(CreatePixelShader(device, D3D11Native.Compile(OcclusionShaders.Composite, "main", "ps_5_0", "freppuccino_composite.hlsl"), out compositePs), "CreatePixelShader");
            Check(CreateSamplerState(device, SamplerDesc.Clamp(false), out pointSampler), "CreateSamplerState");
            Check(CreateSamplerState(device, SamplerDesc.Clamp(true), out linearSampler), "CreateSamplerState");
            Check(CreateBuffer(device, new BufferDesc { ByteWidth = 144, Usage = USAGE_DEFAULT, BindFlags = BIND_CONSTANT_BUFFER }, out postCb), "CreateBuffer");
            Check(CreateDepthStencilState(device, DepthStencilDesc.Test(4), out depthDss), "CreateDepthStencilState");
            postFailed = false;
            return true;
        }
        catch (Exception ex)
        {
            LogOnce("post", "Freppuccino D3D11: occlusion pass unavailable: " + ex.Message);
            ReleasePost();
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    bool EnsureTargets(int w, int h)
    {
        if (w == targetW && h == targetH && gColor.Tex != IntPtr.Zero) return true;
        ReleaseTargets();
        try
        {
            int hw = (w + 1) / 2, hh = (h + 1) / 2;
            MakeTarget(gColor, w, h, FORMAT_R16G16B16A16_FLOAT);
            MakeTarget(gPos, w, h, FORMAT_R32G32B32A32_FLOAT);
            MakeTarget(gNrm, w, h, FORMAT_R16G16B16A16_FLOAT);
            MakeTarget(aoA, hw, hh, FORMAT_R16_FLOAT);
            MakeTarget(aoB, hw, hh, FORMAT_R16_FLOAT);
            targetW = w; targetH = h;
            return true;
        }
        catch (Exception ex)
        {
            LogOnce("targets", "Freppuccino D3D11: occlusion targets unavailable: " + ex.Message);
            ReleaseTargets();
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    void MakeTarget(Target t, int w, int h, int format)
    {
        var desc = new Texture2DDesc { Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = format, SampleCount = 1, Usage = USAGE_DEFAULT, BindFlags = BIND_RENDER_TARGET | BIND_SHADER_RESOURCE };
        Check(CreateTexture2D(device, desc, out t.Tex), "CreateTexture2D");
        Check(CreateRenderTargetView(device, t.Tex, out t.Rtv), "CreateRenderTargetView");
        Check(CreateShaderResourceView(device, t.Tex, out t.Srv), "CreateShaderResourceView");
    }

    void ReleaseTargets()
    {
        if (!OperatingSystem.IsWindows()) return;
        gColor.Release(); gPos.Release(); gNrm.Release(); aoA.Release(); aoB.Release();
        targetW = targetH = 0;
    }

    void ReleasePost()
    {
        if (!OperatingSystem.IsWindows()) return;
        SafeRelease(ref postVs); SafeRelease(ref occlusionPs); SafeRelease(ref blurPs); SafeRelease(ref compositePs);
        SafeRelease(ref pointSampler); SafeRelease(ref linearSampler); SafeRelease(ref postCb); SafeRelease(ref depthDss);
    }

    [SupportedOSPlatform("windows")]
    unsafe void DrawManual(IntPtr ctx, DrawArgs draw)
    {
        // Save (Get* AddRefs). VB/IB/SRV/samplers/RT/viewport and CB slots other than 0 untouched by us → not saved.
        int topo = IAGetPrimitiveTopology(ctx);
        IntPtr il = IAGetInputLayout(ctx);
        IntPtr vs = VSGetShader(ctx), ps = PSGetShader(ctx), gs = GSGetShader(ctx), hs = HSGetShader(ctx), ds = DSGetShader(ctx);
        IntPtr rsOld = RSGetState(ctx);
        float* bf = stackalloc float[4];
        IntPtr bs = OMGetBlendState(ctx, bf, out uint mask);
        IntPtr dssOld = OMGetDepthStencilState(ctx, out uint sref);
        IntPtr vcb = draw.Uniforms != null ? VSGetConstantBuffer(ctx, 0) : IntPtr.Zero, pcb = draw.Uniforms != null ? PSGetConstantBuffer(ctx, 0) : IntPtr.Zero;
        Span<IntPtr> rtvs = stackalloc IntPtr[MaxRenderTargets];
        OMGetRenderTargets(ctx, rtvs, out var dsv);
        try
        {
            OMSetRenderTargets(ctx, rtvs[..1], dsv);
            Bind(ctx, draw);
            D3D11Native.Draw(ctx, draw.VertexCount, 0);
        }
        finally
        {
            IASetPrimitiveTopology(ctx, topo);
            IASetInputLayout(ctx, il);
            VSSetShader(ctx, vs); PSSetShader(ctx, ps); GSSetShader(ctx, gs); HSSetShader(ctx, hs); DSSetShader(ctx, ds);
            RSSetState(ctx, rsOld);
            OMSetBlendState(ctx, bs, bf, mask);
            OMSetDepthStencilState(ctx, dssOld, sref);
            OMSetRenderTargets(ctx, rtvs, dsv);
            for (int i = 0; i < rtvs.Length; i++) SafeRelease(ref rtvs[i]);
            SafeRelease(ref dsv);
            if (draw.Uniforms != null) { VSSetConstantBuffer(ctx, 0, vcb); PSSetConstantBuffer(ctx, 0, pcb); SafeRelease(ref vcb); SafeRelease(ref pcb); }
            SafeRelease(ref il); SafeRelease(ref vs); SafeRelease(ref ps); SafeRelease(ref gs); SafeRelease(ref hs); SafeRelease(ref ds);
            SafeRelease(ref rsOld); SafeRelease(ref bs); SafeRelease(ref dssOld);
        }
    }

    sealed record DrawArgs(Program Prog, byte[]? Uniforms, uint VertexCount);

    /// TriangleList, no input layout/VB, our VS/PS, no GS/HS/DS, cull none, opaque blend (null = default), depth off, uniforms → b0.
    [SupportedOSPlatform("windows")]
    unsafe void Bind(IntPtr ctx, DrawArgs draw)
    {
        var prog = draw.Prog;
        if (draw.Uniforms != null)
        {
            Span<byte> full = stackalloc byte[Uniforms.MaxBytes]; // CB updates must cover the whole buffer
            full.Clear();
            draw.Uniforms.CopyTo(full);
            UpdateSubresource(ctx, cb, full);
            VSSetConstantBuffer(ctx, 0, cb);
            PSSetConstantBuffer(ctx, 0, cb);
        }
        IASetPrimitiveTopology(ctx, TOPOLOGY_TRIANGLELIST);
        IASetInputLayout(ctx, IntPtr.Zero);
        VSSetShader(ctx, prog.Vs);
        PSSetShader(ctx, prog.Ps);
        GSSetShader(ctx, IntPtr.Zero); HSSetShader(ctx, IntPtr.Zero); DSSetShader(ctx, IntPtr.Zero);
        RSSetState(ctx, rs);
        OMSetBlendState(ctx, IntPtr.Zero, null, 0xffffffff);
        OMSetDepthStencilState(ctx, dss, 0);
    }

    sealed record Bytecode(byte[] Vs, byte[] Ps, TimeSpan Took);

    static Task<Bytecode> StartBuild(ShaderSource src) => Task.Run(() =>
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var tag = src.Hash[..8];
        var vs = D3D11Native.Compile(ShaderCompiler.ToHlsl(ShaderCompiler.ToSpirv(src.Vertex, ShaderStage.Vertex)), "main", "vs_5_0", $"freppucino_{tag}.vert.hlsl");
        var ps = D3D11Native.Compile(ShaderCompiler.ToHlsl(ShaderCompiler.ToSpirv(src.Fragment, ShaderStage.Fragment)), "main", "ps_5_0", $"freppucino_{tag}.frag.hlsl");
        return new Bytecode(vs, ps, clock.Elapsed);
    });

    public bool Building(ShaderSource program) => running?.Hash == program.Hash || waiting?.Hash == program.Hash;

    void Start(ShaderSource src)
    {
        var task = StartBuild(src);
        running = (src.Hash, task);
        task.ContinueWith(_ => RhinoApp.InvokeOnUiThread(() =>
        {
            if (running?.Task == task) { finished = running; running = null; }
            StartWaiting();
            Report();
            foreach (var doc in RhinoDoc.OpenDocuments()) doc.Views.Redraw();
        }), TaskScheduler.Default);
    }

    void Report() => ShaderBuilds.Set(running != null || waiting != null);

    void StartWaiting()
    {
        if (running != null || waiting is not { } next) return;
        waiting = null;
        Start(next);
    }

    [SupportedOSPlatform("windows")]
    Program? GetProgram(ShaderSource src, bool wait = false)
    {
        if (programs.TryGetValue(src.Hash, out var p)) return p;
        if (failed.ContainsKey(src.Hash)) return null;
        if (ShaderCache.TryGet(src.Hash, out var stored))
        {
            try { return Create(src.Hash, stored.Vs, stored.Ps); }
            catch (Exception ex)
            {
                ShaderCache.Drop(src.Hash);
                RhinoApp.WriteLine($"Freppuccino: the shader saved in this file was rejected ({ex.Message}); compiling it again.");
            }
        }
        Task<Bytecode> task;
        if (finished is { } f && f.Hash == src.Hash)
        {
            task = f.Task;
            finished = null;
        }
        else if (running is { } r && r.Hash == src.Hash && (wait || r.Task.IsCompleted))
        {
            task = r.Task;
            running = null;
            StartWaiting();
            Report();
        }
        else if (wait)
        {
            if (waiting?.Hash == src.Hash) waiting = null;
            Report();
            task = StartBuild(src);
        }
        else
        {
            if (running == null) Start(src);
            else if (running.Value.Hash != src.Hash) waiting = src;
            Report();
            return null;
        }
        try
        {
            var (vsCode, psCode, took) = task.GetAwaiter().GetResult();
            if (took.TotalSeconds >= 5) RhinoApp.WriteLine($"Freppuccino: viewport shader ready ({took.TotalSeconds:0} s to compile).");
            var made = Create(src.Hash, vsCode, psCode);
            if (src.Background) ShaderCache.Add(src.Hash, vsCode, psCode);
            return made;
        }
        catch (Exception ex)
        {
            failed[src.Hash] = ex.Message;
            RhinoApp.WriteLine($"Freppuccino D3D11: shader {src.Hash} failed: {ex.Message}");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    Program Create(string hash, byte[] vsCode, byte[] psCode)
    {
        IntPtr vs = IntPtr.Zero, ps = IntPtr.Zero;
        try
        {
            Check(CreateVertexShader(device, vsCode, out vs), "CreateVertexShader");
            Check(CreatePixelShader(device, psCode, out ps), "CreatePixelShader");
        }
        catch
        {
            SafeRelease(ref vs); SafeRelease(ref ps);
            throw;
        }
        var tag = hash[..8];
        SetName(vs, $"Freppuccino VS {tag}"); SetName(ps, $"Freppuccino PS {tag}");
        return programs[hash] = new Program(vs, ps);
    }

    static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} failed 0x{hr:X8}"); }

    void LogOnce(string key, string msg) { if (logged.Add(key)) RhinoApp.WriteLine(msg); }

    void ReleaseAll()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var p in programs.Values) { var v = p.Vs; var f = p.Ps; SafeRelease(ref v); SafeRelease(ref f); }
        programs.Clear(); failed.Clear();
        SafeRelease(ref ctxState); ctxStateFailed = false;
        ReleaseTargets(); ReleasePost(); postFailed = false;
        ReleaseOutline(); outlineFailed = false;
        SafeRelease(ref rs); SafeRelease(ref dss); SafeRelease(ref cb); SafeRelease(ref device1);
        device = IntPtr.Zero; initOk = false;
    }

    public string? CompileError(ShaderSource program) => failed.GetValueOrDefault(program.Hash);

    public string? Compile(ShaderSource program)
    {
        if (!OperatingSystem.IsWindows() || !initOk) return null;
        try { GetProgram(program, wait: true); }
        catch (Exception ex) { LogOnce("compile:" + ex.GetType().Name, "Freppuccino D3D11: " + ex); }
        return CompileError(program);
    }

    public void Dispose() => ReleaseAll();
}
