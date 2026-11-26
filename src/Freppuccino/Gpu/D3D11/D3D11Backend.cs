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
    IntPtr postVs, occlusionPs, blurPs, compositePs, pointSampler, linearSampler, postCb;
    bool postFailed;
    readonly Target gColor = new(), gPos = new(), gNrm = new(), aoA = new(), aoB = new();
    int targetW, targetH;

    public bool Draw(DisplayPipeline pipeline, ShaderSource program, ReadOnlySpan<byte> uniforms) => Draw(pipeline, program, uniforms, false);

    public bool DrawOccluded(DisplayPipeline pipeline, ShaderSource program, ReadOnlySpan<byte> uniforms) => Draw(pipeline, program, uniforms, Occlusion);

    bool Draw(DisplayPipeline pipeline, ShaderSource program, ReadOnlySpan<byte> uniforms, bool occluded)
    {
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
            if (swap && occluded && EnsurePost()) DrawOccludedSwapped(ctx, draw);
            else if (swap) DrawSwapped(ctx, draw);
            else DrawManual(ctx, draw);
        }
        finally
        {
            if (ann != IntPtr.Zero) { EndEvent(ann); SafeRelease(ref ann); }
        }
        return true;
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
    void DrawOccludedSwapped(IntPtr ctx, DrawArgs draw)
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
                return;
            }
            int hw = (w + 1) / 2, hh = (h + 1) / 2;

            ClearRenderTargetView(ctx, gColor.Rtv, 0, 0, 0, 0);
            ClearRenderTargetView(ctx, gPos.Rtv, 0, 0, 0, 0);
            ClearRenderTargetView(ctx, gNrm.Rtv, 0, 0, 0, 0);
            OMSetRenderTargets(ctx, [gColor.Rtv, gPos.Rtv, gNrm.Rtv], IntPtr.Zero);
            RSSetViewports(ctx, [new Viewport { Width = w, Height = h, MaxDepth = 1 }]);
            Bind(ctx, draw);
            D3D11Native.Draw(ctx, draw.VertexCount, 0);

            PSSetSamplers(ctx, 0, [pointSampler, linearSampler]);
            VSSetShader(ctx, postVs);
            PSSetConstantBuffer(ctx, 1, postCb);
            RSSetViewports(ctx, [new Viewport { Width = hw, Height = hh, MaxDepth = 1 }]);

            PostPass(ctx, occlusionPs, aoA.Rtv, [gPos.Srv, gNrm.Srv, IntPtr.Zero, IntPtr.Zero], w, h, hw, hh, 0, 0);
            PostPass(ctx, blurPs, aoB.Rtv, [gPos.Srv, gNrm.Srv, IntPtr.Zero, aoA.Srv], w, h, hw, hh, 1, 0);
            PostPass(ctx, blurPs, aoA.Rtv, [gPos.Srv, gNrm.Srv, IntPtr.Zero, aoB.Srv], w, h, hw, hh, 0, 1);

            OMSetRenderTargets(ctx, rtvs, dsv);
            RSSetViewports(ctx, vps[..nvp]);
            SetPost(ctx, w, h, hw, hh, 0, 0, vp.X, vp.Y);
            PSSetShader(ctx, compositePs);
            PSSetShaderResources(ctx, 0, [gPos.Srv, gNrm.Srv, gColor.Srv, aoA.Srv]);
            D3D11Native.Draw(ctx, 3, 0);
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
    void SetPost(IntPtr ctx, int w, int h, int hw, int hh, float dx, float dy, float ox, float oy)
    {
        Span<float> v = [w, h, hw, hh, dx, dy, ox, oy];
        UpdateSubresource(ctx, postCb, System.Runtime.InteropServices.MemoryMarshal.AsBytes(v));
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
            Check(CreateBuffer(device, new BufferDesc { ByteWidth = 32, Usage = USAGE_DEFAULT, BindFlags = BIND_CONSTANT_BUFFER }, out postCb), "CreateBuffer");
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
        SafeRelease(ref pointSampler); SafeRelease(ref linearSampler); SafeRelease(ref postCb);
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
