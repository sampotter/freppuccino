using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Rhino;
using Rhino.Display;
using static Freppuccino.Gpu.D3D11.D3D11Native;

namespace Freppuccino.Gpu.D3D11;

public static unsafe class DepthTrace
{
    const int Frames = 30;
    static Guid? viewport;
    static int frame;
    static StringBuilder? log;
    static IntPtr device, vsMs, psMs, vs1, ps1;

    public static bool Armed(DisplayPipeline dp) => viewport != null && dp.Viewport?.Id == viewport;

    public static void Arm(RhinoViewport vp)
    {
        viewport = vp.Id;
        frame = 0;
        log = new StringBuilder();
        log.AppendLine($"FrepDepthTrace {DateTime.Now:O} viewport '{vp.Name}' {vp.Id} size {vp.Size.Width}x{vp.Size.Height} persp {vp.IsPerspectiveProjection}");
    }

    public static void Frame() { if (viewport != null) frame++; }

    public static void Sample(DisplayPipeline dp, string stage, D3D11Backend? backend, Guid? early, bool earlyWrote)
    {
        if (log == null || !OperatingSystem.IsWindows()) return;
        try { log.AppendLine(Read(dp, stage, backend, early, earlyWrote)); }
        catch (Exception ex) { log.AppendLine($"{frame} {stage} error {ex.GetType().Name}: {ex.Message}"); }
        if (frame >= Frames && stage == "fore") Finish();
    }

    static void Finish()
    {
        var path = Path.Combine(Path.GetTempPath(), $"FrepDepthTrace-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, log!.ToString());
        viewport = null;
        log = null;
        RhinoApp.WriteLine($"FrepDepthTrace: {Frames} frames written to {path}");
    }

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);

    static void** V(IntPtr p) => *(void***)p;

    static void Rel(IntPtr p) { if (p != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)V(p)[2])(p); }

    [SupportedOSPlatform("windows")]
    static string Read(DisplayPipeline dp, string stage, D3D11Backend? backend, Guid? early, bool earlyWrote)
    {
        var dev = dp.GetDirect3d11Device();
        var ctx = dp.GetDirect3d11DeviceContext();
        var vp = dp.Viewport;
        var sb = new StringBuilder();
        uint dpi = vp?.ParentView is { } view ? GetDpiForWindow(view.Handle) : 0;
        sb.Append($"{frame} {stage} vp '{vp?.Name}' {vp?.Size.Width}x{vp?.Size.Height} dpi {dpi} dynamic {dp.IsDynamicDisplay} early {(early?.ToString()[..8] ?? "none")} wrote {earlyWrote}");
        sb.Append($" depthMode {dp.DepthMode} zbias {dp.ZBiasMode}");
        if (dev == IntPtr.Zero || ctx == IntPtr.Zero) return sb.Append(" no-d3d").ToString();

        Span<IntPtr> rtvs = stackalloc IntPtr[8];
        OMGetRenderTargets(ctx, rtvs, out var dsv);
        IntPtr rtRes = IntPtr.Zero;
        uint* rtd = stackalloc uint[11];
        if (rtvs[0] != IntPtr.Zero) { ((delegate* unmanaged<IntPtr, IntPtr*, void>)V(rtvs[0])[7])(rtvs[0], &rtRes); ((delegate* unmanaged<IntPtr, uint*, void>)V(rtRes)[10])(rtRes, rtd); Rel(rtRes); }
        for (int i = 0; i < rtvs.Length; i++) SafeRelease(ref rtvs[i]);
        sb.Append($" rt0 {(long)rtRes:X} {rtd[0]}x{rtd[1]} fmt {rtd[4]} msaa {rtd[5]}");
        var dssObj = OMGetDepthStencilState(ctx, out _);
        int* dssd = stackalloc int[13];
        if (dssObj != IntPtr.Zero) { ((delegate* unmanaged<IntPtr, int*, void>)V(dssObj)[7])(dssObj, dssd); SafeRelease(ref dssObj); }
        sb.Append($" dss enable {dssd[0]} write {dssd[1]} func {dssd[2]}");
        Span<Viewport> vps = stackalloc Viewport[16];
        int nvp = RSGetViewports(ctx, vps);
        if (nvp > 0) sb.Append($" vp0 {vps[0].X},{vps[0].Y} {vps[0].Width}x{vps[0].Height} z {vps[0].MinDepth}..{vps[0].MaxDepth}");
        if (dsv == IntPtr.Zero) return sb.Append(" dsv none").ToString();

        IntPtr res = IntPtr.Zero;
        ((delegate* unmanaged<IntPtr, IntPtr*, void>)V(dsv)[7])(dsv, &res);
        SafeRelease(ref dsv);
        uint* td = stackalloc uint[11];
        ((delegate* unmanaged<IntPtr, uint*, void>)V(res)[10])(res, td);
        sb.Append($" dsv {(long)res:X} {td[0]}x{td[1]} fmt {td[4]} msaa {td[5]} bind {td[8]}");

        int w = nvp > 0 ? (int)vps[0].Width : (int)td[0], h = nvp > 0 ? (int)vps[0].Height : (int)td[1];
        int ox = nvp > 0 ? (int)vps[0].X : 0, oy = nvp > 0 ? (int)vps[0].Y : 0;
        var grid = Enumerable.Range(0, 64).Select(i => (X: (int)(w * (0.1 + 0.8 * (i % 8) / 7.0)), Y: (int)(h * (0.1 + 0.8 * (i / 8) / 7.0)))).ToArray();
        var scan = backend?.TraceFrepDepth(dev, ctx, grid);
        var hits = scan == null ? [] : Enumerable.Range(0, scan.Length).Where(i => scan[i].W > 0).ToList();
        var pick = hits.Count >= 5 ? Enumerable.Range(0, 5).Select(k => hits[k * (hits.Count - 1) / 4]).ToList() : hits.Concat(Enumerable.Range(0, 64).Where(i => !hits.Contains(i))).Take(5).ToList();
        var px = pick.Select(i => (X: ox + grid[i].X, Y: oy + grid[i].Y)).ToArray();
        var frep = scan == null ? null : pick.Select(i => scan[i]).ToArray();
        float[][]? buffer = (td[8] & 0x8) != 0 ? ReadDepth(dev, ctx, res, td, px) : null;
        Rel(res);
        sb.Append($" frep-cover {hits.Count}/64");
        for (int i = 0; i < px.Length; i++)
        {
            sb.Append($" | p{i} ({px[i].X},{px[i].Y})");
            if (frep != null) sb.Append($" frep {(frep[i].W > 0 ? frep[i].Depth.ToString("0.000000") : "w=" + frep[i].W)}");
            sb.Append(buffer != null ? " buf " + string.Join("/", buffer[i].Select(d => d.ToString("0.000000"))) : " buf unreadable");
        }
        return sb.ToString();
    }

    const string Vs = "float4 main(uint id : SV_VertexID) : SV_Position { float2 uv = float2((id << 1) & 2, id & 2); return float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0); }";
    const string PsMs = "Texture2DMS<float> t : register(t0); cbuffer C : register(b0) { int4 px[8]; }; float4 main(float4 sv : SV_Position) : SV_Target { int2 p = px[(int)sv.x].xy; return float4(t.Load(p, 0), t.Load(p, 1), t.Load(p, 2), t.Load(p, 3)); }";
    const string Ps1 = "Texture2D<float> t : register(t0); cbuffer C : register(b0) { int4 px[8]; }; float4 main(float4 sv : SV_Position) : SV_Target { int2 p = px[(int)sv.x].xy; float d = t.Load(int3(p, 0)); return float4(d, d, d, d); }";

    static int SrvFormat(uint typeless) => typeless switch { 19 or 20 => 21, 44 or 45 => 46, 39 or 40 => 41, 53 or 55 => 56, _ => (int)typeless };

    [SupportedOSPlatform("windows")]
    static float[][]? ReadDepth(IntPtr dev, IntPtr ctx, IntPtr res, uint* td, (int X, int Y)[] px)
    {
        bool ms = td[5] > 1;
        if (dev != device)
        {
            SafeRelease(ref vsMs); SafeRelease(ref psMs); SafeRelease(ref vs1); SafeRelease(ref ps1);
            device = dev;
            CreateVertexShader(dev, Compile(Vs, "main", "vs_5_0", "trace.vs.hlsl"), out vsMs);
            CreatePixelShader(dev, Compile(PsMs, "main", "ps_5_0", "trace_ms.hlsl"), out psMs);
            CreatePixelShader(dev, Compile(Ps1, "main", "ps_5_0", "trace_1.hlsl"), out ps1);
        }
        IntPtr srv = 0, tex = 0, rtv = 0, cb = 0, st = 0;
        try
        {
            uint* sd = stackalloc uint[6] { (uint)SrvFormat(td[4]), ms ? 6u : 4u, 0, 1, 0, 0 };
            if (((delegate* unmanaged<IntPtr, IntPtr, uint*, IntPtr*, int>)V(dev)[7])(dev, res, sd, &srv) < 0) return null;
            Check(CreateTexture2D(dev, new Texture2DDesc { Width = 8, Height = 1, MipLevels = 1, ArraySize = 1, Format = 2, SampleCount = 1, BindFlags = 0x20 }, out tex));
            Check(CreateRenderTargetView(dev, tex, out rtv));
            Check(CreateTexture2D(dev, new Texture2DDesc { Width = 8, Height = 1, MipLevels = 1, ArraySize = 1, Format = 2, SampleCount = 1, Usage = 3, CPUAccessFlags = 0x20000 }, out st));
            int* cbd = stackalloc int[32];
            for (int i = 0; i < 8; i++) { var q = i < px.Length ? px[i] : (0, 0); cbd[i * 4] = q.Item1; cbd[i * 4 + 1] = q.Item2; }
            Check(CreateBuffer(dev, new BufferDesc { ByteWidth = 128, BindFlags = BIND_CONSTANT_BUFFER }, out cb));
            UpdateSubresource(ctx, cb, new ReadOnlySpan<byte>(cbd, 128));

            int topo = IAGetPrimitiveTopology(ctx);
            IntPtr il = IAGetInputLayout(ctx), ovs = VSGetShader(ctx), ops = PSGetShader(ctx), ogs = GSGetShader(ctx), ors = RSGetState(ctx);
            IntPtr ocb = PSGetConstantBuffer(ctx, 0), osrv = IntPtr.Zero;
            ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, void>)V(ctx)[73])(ctx, 0, 1, &osrv);
            float* bf = stackalloc float[4];
            IntPtr obs = OMGetBlendState(ctx, bf, out uint mask);
            Span<IntPtr> orts = stackalloc IntPtr[8];
            OMGetRenderTargets(ctx, orts, out var odsv);
            Span<Viewport> ovps = stackalloc Viewport[16];
            int onvp = RSGetViewports(ctx, ovps);

            OMSetRenderTargets(ctx, [rtv], IntPtr.Zero);
            IASetPrimitiveTopology(ctx, 4);
            IASetInputLayout(ctx, IntPtr.Zero);
            VSSetShader(ctx, vsMs);
            PSSetShader(ctx, ms ? psMs : ps1);
            GSSetShader(ctx, IntPtr.Zero);
            PSSetShaderResources(ctx, 0, [srv]);
            PSSetConstantBuffer(ctx, 0, cb);
            RSSetState(ctx, IntPtr.Zero);
            OMSetBlendState(ctx, IntPtr.Zero, null, 0xffffffff);
            RSSetViewports(ctx, [new Viewport { Width = 8, Height = 1, MaxDepth = 1 }]);
            D3D11Native.Draw(ctx, 3, 0);

            PSSetShaderResources(ctx, 0, [osrv]);
            PSSetConstantBuffer(ctx, 0, ocb);
            OMSetRenderTargets(ctx, orts, odsv);
            RSSetViewports(ctx, ovps[..onvp]);
            IASetPrimitiveTopology(ctx, topo);
            IASetInputLayout(ctx, il);
            VSSetShader(ctx, ovs); PSSetShader(ctx, ops); GSSetShader(ctx, ogs);
            RSSetState(ctx, ors);
            OMSetBlendState(ctx, obs, bf, mask);
            SafeRelease(ref il); SafeRelease(ref ovs); SafeRelease(ref ops); SafeRelease(ref ogs); SafeRelease(ref ors);
            SafeRelease(ref ocb); SafeRelease(ref osrv); SafeRelease(ref obs); SafeRelease(ref odsv);
            for (int i = 0; i < orts.Length; i++) SafeRelease(ref orts[i]);

            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)V(ctx)[47])(ctx, st, tex);
            void** map = stackalloc void*[2];
            if (((delegate* unmanaged<IntPtr, IntPtr, uint, int, uint, void**, int>)V(ctx)[14])(ctx, st, 0, 1, 0, map) < 0) return null;
            var f = (float*)map[0];
            var r = new float[px.Length][];
            for (int i = 0; i < px.Length; i++) r[i] = ms ? [f[i * 4], f[i * 4 + 1], f[i * 4 + 2], f[i * 4 + 3]] : [f[i * 4]];
            ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)V(ctx)[15])(ctx, st, 0);
            return r;
        }
        finally
        {
            SafeRelease(ref srv); SafeRelease(ref tex); SafeRelease(ref rtv); SafeRelease(ref cb); SafeRelease(ref st);
        }
    }

    static void Check(int hr) { if (hr < 0) throw new InvalidOperationException($"0x{hr:X8}"); }
}
