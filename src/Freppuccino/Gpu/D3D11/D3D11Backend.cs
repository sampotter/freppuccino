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

    sealed record Program(IntPtr Vs, IntPtr Ps);

    readonly Dictionary<string, Program> programs = new();
    readonly Dictionary<string, string> failed = new(); // hash → error; never retried (cleared with the device)
    readonly HashSet<string> logged = new();

    IntPtr device;          // Rhino's, not owned; identity only (cache key)
    IntPtr device1;         // ours (QI'd)
    IntPtr rs, dss;         // ours
    IntPtr cb;              // ours: Uniforms.MaxBytes constant buffer, b0
    IntPtr ctxState;        // ours (ID3DDeviceContextState), lazily created
    bool ctxStateFailed;
    int featureLevel;
    bool initOk;

    public bool Draw(DisplayPipeline pipeline, ShaderSource program, ReadOnlySpan<byte> uniforms)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { return DrawWin(pipeline, program, uniforms); }
        catch (Exception ex) { LogOnce("draw:" + ex.GetType().Name, "Freppuccino D3D11: " + ex); return false; }
    }

    [SupportedOSPlatform("windows")]
    bool DrawWin(DisplayPipeline pipeline, ShaderSource program, ReadOnlySpan<byte> uniforms)
    {
        var dev = pipeline.GetDirect3d11Device();
        var ctx = pipeline.GetDirect3d11DeviceContext();
        if (dev == IntPtr.Zero || ctx == IntPtr.Zero) { LogOnce("null", "Freppuccino D3D11: null device/context"); return false; }
        if (dev != device) { ReleaseAll(); initOk = Init(dev); }
        if (!initOk) return false;

        var prog = GetProgram(program);
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
            if (swap) DrawSwapped(ctx, draw); else DrawManual(ctx, draw);
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
            OMSetRenderTargets(ctx, rtvs, dsv);
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
        try
        {
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

    [SupportedOSPlatform("windows")]
    Program? GetProgram(ShaderSource src)
    {
        if (programs.TryGetValue(src.Hash, out var p)) return p;
        if (failed.ContainsKey(src.Hash)) return null;
        IntPtr vs = IntPtr.Zero, ps = IntPtr.Zero;
        try
        {
            var tag = src.Hash[..8];
            var vsCode = D3D11Native.Compile(ShaderCompiler.ToHlsl(ShaderCompiler.ToSpirv(src.Vertex, ShaderStage.Vertex)), "main", "vs_5_0", $"freppucino_{tag}.vert.hlsl");
            var psCode = D3D11Native.Compile(ShaderCompiler.ToHlsl(ShaderCompiler.ToSpirv(src.Fragment, ShaderStage.Fragment)), "main", "ps_5_0", $"freppucino_{tag}.frag.hlsl");
            Check(CreateVertexShader(device, vsCode, out vs), "CreateVertexShader");
            Check(CreatePixelShader(device, psCode, out ps), "CreatePixelShader");
            SetName(vs, $"Freppuccino VS {tag}"); SetName(ps, $"Freppuccino PS {tag}");
            return programs[src.Hash] = new Program(vs, ps);
        }
        catch (Exception ex)
        {
            SafeRelease(ref vs); SafeRelease(ref ps);
            failed[src.Hash] = ex.Message;
            RhinoApp.WriteLine($"Freppuccino D3D11: shader {src.Hash} failed: {ex.Message}");
            return null;
        }
    }

    static void Check(int hr, string what) { if (hr < 0) throw new InvalidOperationException($"{what} failed 0x{hr:X8}"); }

    void LogOnce(string key, string msg) { if (logged.Add(key)) RhinoApp.WriteLine(msg); }

    void ReleaseAll()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var p in programs.Values) { var v = p.Vs; var f = p.Ps; SafeRelease(ref v); SafeRelease(ref f); }
        programs.Clear(); failed.Clear();
        SafeRelease(ref ctxState); ctxStateFailed = false;
        SafeRelease(ref rs); SafeRelease(ref dss); SafeRelease(ref cb); SafeRelease(ref device1);
        device = IntPtr.Zero; initOk = false;
    }

    public string? CompileError(ShaderSource program) => failed.GetValueOrDefault(program.Hash);

    public string? Compile(ShaderSource program)
    {
        if (!OperatingSystem.IsWindows() || !initOk) return null;
        try { GetProgram(program); }
        catch (Exception ex) { LogOnce("compile:" + ex.GetType().Name, "Freppuccino D3D11: " + ex); }
        return CompileError(program);
    }

    public void Dispose() => ReleaseAll();
}
