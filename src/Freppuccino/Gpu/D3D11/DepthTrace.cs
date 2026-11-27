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
    static int frame, frameSerial = -1;
    static StringBuilder? log;
    static IntPtr device, ctxState, vs, psMs, ps1;

    public static bool InjectFailure { get; set; }

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
        IntPtr dsv = IntPtr.Zero, rtRes = IntPtr.Zero, res = IntPtr.Zero, dssObj = IntPtr.Zero;
        try
        {
            OMGetRenderTargets(ctx, rtvs, out dsv);
            uint* rtd = stackalloc uint[11];
            if (rtvs[0] != IntPtr.Zero)
            {
                ((delegate* unmanaged<IntPtr, IntPtr*, void>)V(rtvs[0])[7])(rtvs[0], &rtRes);
                ((delegate* unmanaged<IntPtr, uint*, void>)V(rtRes)[10])(rtRes, rtd);
            }
            sb.Append($" rt0 {(long)rtRes:X} {rtd[0]}x{rtd[1]} fmt {rtd[4]} msaa {rtd[5]}");
            dssObj = OMGetDepthStencilState(ctx, out _);
            int* dssd = stackalloc int[13];
            if (dssObj != IntPtr.Zero) ((delegate* unmanaged<IntPtr, int*, void>)V(dssObj)[7])(dssObj, dssd);
            sb.Append(dssObj == IntPtr.Zero ? " dss default" : $" dss enable {dssd[0]} write {dssd[1]} func {dssd[2]}");
            Span<Viewport> vps = stackalloc Viewport[16];
            int nvp = RSGetViewports(ctx, vps);
            if (nvp > 0) sb.Append($" vp0 {vps[0].X},{vps[0].Y} {vps[0].Width}x{vps[0].Height} z {vps[0].MinDepth}..{vps[0].MaxDepth}");
            if (dsv == IntPtr.Zero) return sb.Append(" dsv none").ToString();

            ((delegate* unmanaged<IntPtr, IntPtr*, void>)V(dsv)[7])(dsv, &res);
            uint* td = stackalloc uint[11];
            ((delegate* unmanaged<IntPtr, uint*, void>)V(res)[10])(res, td);
            sb.Append($" dsv {(long)res:X} {td[0]}x{td[1]} fmt {td[4]} msaa {td[5]} bind {td[8]}");

            int w = nvp > 0 ? (int)vps[0].Width : (int)td[0], h = nvp > 0 ? (int)vps[0].Height : (int)td[1];
            int ox = nvp > 0 ? (int)vps[0].X : 0, oy = nvp > 0 ? (int)vps[0].Y : 0;
            var grid = Enumerable.Range(0, 64).Select(i => (X: (int)(w * (0.1 + 0.8 * (i % 8) / 7.0)), Y: (int)(h * (0.1 + 0.8 * (i / 8) / 7.0)))).ToArray();
            var source = backend?.TraceSource ?? default;
            bool here = backend != null && source.Viewport == vp?.Id;
            if (stage is "pre-after-early" or "post-after-fallback") frameSerial = here ? source.Serial : -1;
            if (stage == "post-entry-no-pre") frameSerial = -1;
            bool current = here && source.Serial == frameSerial;
            sb.Append($" gpos {(backend == null ? "none" : $"vp {source.Viewport.ToString()[..8]} draw {source.Serial} {source.W}x{source.H}")} {(current ? "current" : "stale")}");
            var scan = current ? backend!.TraceFrepDepth(dev, ctx, grid) : null;
            var hits = scan == null ? [] : Enumerable.Range(0, scan.Length).Where(i => scan[i].W > 0 || scan[i].W == -1).ToList();
            var pick = hits.Count >= 5 ? Enumerable.Range(0, 5).Select(k => hits[k * (hits.Count - 1) / 4]).ToList() : hits.Concat(Enumerable.Range(0, 64).Where(i => !hits.Contains(i))).Take(5).ToList();
            var px = pick.Select(i => (X: ox + grid[i].X, Y: oy + grid[i].Y)).ToArray();
            var frep = scan == null ? null : pick.Select(i => scan[i]).ToArray();
            string note = "not-shader-readable";
            float[][]? buffer = (td[8] & BIND_SHADER_RESOURCE) == 0 ? null : ReadDepth(dev, ctx, res, td, px, out note);
            sb.Append($" frep-cover {hits.Count}/64");
            if (buffer == null) sb.Append($" buf-unavailable {note}");
            for (int i = 0; i < px.Length; i++)
            {
                sb.Append($" | p{i} ({px[i].X},{px[i].Y})");
                if (frep != null) sb.Append($" frep {(frep[i].W > 0 || frep[i].W == -1 ? frep[i].Depth.ToString("0.000000") + (frep[i].W == -1 ? " zebra" : "") : "w=" + frep[i].W)}");
                else sb.Append(" frep unavailable");
                if (buffer != null) sb.Append(" buf " + string.Join("/", buffer[i].Select(d => d.ToString("0.000000"))));
            }
            return sb.ToString();
        }
        finally
        {
            for (int i = 0; i < rtvs.Length; i++) SafeRelease(ref rtvs[i]);
            SafeRelease(ref rtRes);
            SafeRelease(ref res);
            SafeRelease(ref dsv);
            SafeRelease(ref dssObj);
        }
    }

    const string Vs = "float4 main(uint id : SV_VertexID) : SV_Position { float2 uv = float2((id << 1) & 2, id & 2); return float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0); }";
    const string PsMs = "Texture2DMS<float> t : register(t0); cbuffer C : register(b0) { int4 px[8]; int4 info; }; float4 main(float4 sv : SV_Position) : SV_Target { int i = (int)sv.x, s = i % 8; if (s >= info.x) return -1.0; float d = t.Load(px[i / 8].xy, s); return float4(d, d, d, d); }";
    const string Ps1 = "Texture2D<float> t : register(t0); cbuffer C : register(b0) { int4 px[8]; int4 info; }; float4 main(float4 sv : SV_Position) : SV_Target { int i = (int)sv.x; if (i % 8 > 0) return -1.0; float d = t.Load(int3(px[i / 8].xy, 0)); return float4(d, d, d, d); }";

    static int SrvFormat(uint typeless) => typeless switch { 19 or 20 => 21, 44 or 45 => 46, 39 or 40 => 41, 53 or 55 => 56, _ => -1 };

    [SupportedOSPlatform("windows")]
    static bool EnsureSetup(IntPtr dev)
    {
        if (dev == device && ctxState != IntPtr.Zero) return true;
        SafeRelease(ref vs); SafeRelease(ref psMs); SafeRelease(ref ps1); SafeRelease(ref ctxState);
        device = IntPtr.Zero;
        IntPtr v = IntPtr.Zero, a = IntPtr.Zero, b = IntPtr.Zero, state = IntPtr.Zero, dev1 = QI(dev, IID_ID3D11Device1);
        try
        {
            if (dev1 == IntPtr.Zero) return false;
            Check(CreateVertexShader(dev, Compile(Vs, "main", "vs_5_0", "trace.vs.hlsl"), out v));
            Check(CreatePixelShader(dev, Compile(PsMs, "main", "ps_5_0", "trace_ms.hlsl"), out a));
            Check(CreatePixelShader(dev, Compile(Ps1, "main", "ps_5_0", "trace_1.hlsl"), out b));
            uint flags = (CreationFlags(dev) & CREATE_DEVICE_SINGLETHREADED) != 0 ? CONTEXT_STATE_SINGLETHREADED : 0;
            Check(CreateDeviceContextState(dev1, flags, FeatureLevel(dev), IID_ID3D11Device1, out state));
            vs = v; psMs = a; ps1 = b; ctxState = state; device = dev;
            v = a = b = state = IntPtr.Zero;
            return true;
        }
        catch { return false; }
        finally { SafeRelease(ref v); SafeRelease(ref a); SafeRelease(ref b); SafeRelease(ref state); SafeRelease(ref dev1); }
    }

    [SupportedOSPlatform("windows")]
    static float[][]? ReadDepth(IntPtr dev, IntPtr ctx, IntPtr res, uint* td, (int X, int Y)[] px, out string note)
    {
        note = "";
        uint samples = td[5];
        int fmt = SrvFormat(td[4]);
        if (samples < 1 || samples > 8) { note = $"unsupported-msaa-{samples}"; return null; }
        if (fmt < 0) { note = $"unsupported-format-{td[4]}"; return null; }
        if (!EnsureSetup(dev)) { note = "setup-failed"; return null; }
        IntPtr srv = IntPtr.Zero, tex = IntPtr.Zero, rtv = IntPtr.Zero, cb = IntPtr.Zero, st = IntPtr.Zero;
        try
        {
            uint* sd = stackalloc uint[6] { (uint)fmt, samples > 1 ? 6u : 4u, 0, 1, 0, 0 };
            Check(((delegate* unmanaged<IntPtr, IntPtr, uint*, IntPtr*, int>)V(dev)[7])(dev, res, sd, &srv));
            Check(CreateTexture2D(dev, new Texture2DDesc { Width = 64, Height = 1, MipLevels = 1, ArraySize = 1, Format = FORMAT_R32G32B32A32_FLOAT, SampleCount = 1, BindFlags = BIND_RENDER_TARGET }, out tex));
            Check(CreateRenderTargetView(dev, tex, out rtv));
            Check(CreateTexture2D(dev, new Texture2DDesc { Width = 64, Height = 1, MipLevels = 1, ArraySize = 1, Format = FORMAT_R32G32B32A32_FLOAT, SampleCount = 1, Usage = 3, CPUAccessFlags = 0x20000 }, out st));
            int* cbd = stackalloc int[36];
            for (int i = 0; i < 36; i++) cbd[i] = 0;
            for (int i = 0; i < 8 && i < px.Length; i++) { cbd[i * 4] = px[i].X; cbd[i * 4 + 1] = px[i].Y; }
            cbd[32] = (int)samples;
            Check(CreateBuffer(dev, new BufferDesc { ByteWidth = 144, BindFlags = BIND_CONSTANT_BUFFER }, out cb));
            UpdateSubresource(ctx, cb, new ReadOnlySpan<byte>(cbd, 144));

            IntPtr prev = SwapDeviceContextState(ctx, ctxState, wantPrevious: true);
            try
            {
                if (InjectFailure) throw new InvalidOperationException("injected failure");
                OMSetRenderTargets(ctx, [rtv], IntPtr.Zero);
                RSSetViewports(ctx, [new Viewport { Width = 64, Height = 1, MaxDepth = 1 }]);
                IASetPrimitiveTopology(ctx, TOPOLOGY_TRIANGLELIST);
                VSSetShader(ctx, vs);
                PSSetShader(ctx, samples > 1 ? psMs : ps1);
                PSSetShaderResources(ctx, 0, [srv]);
                PSSetConstantBuffer(ctx, 0, cb);
                D3D11Native.Draw(ctx, 3, 0);
            }
            finally
            {
                PSSetShaderResources(ctx, 0, [IntPtr.Zero]);
                OMSetRenderTargets(ctx, ReadOnlySpan<IntPtr>.Empty, IntPtr.Zero);
                SwapDeviceContextState(ctx, prev, wantPrevious: false);
                SafeRelease(ref prev);
            }

            ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)V(ctx)[47])(ctx, st, tex);
            void** map = stackalloc void*[2];
            Check(((delegate* unmanaged<IntPtr, IntPtr, uint, int, uint, void**, int>)V(ctx)[14])(ctx, st, 0, 1, 0, map));
            var f = (float*)map[0];
            var r = new float[px.Length][];
            for (int i = 0; i < px.Length; i++) r[i] = Enumerable.Range(0, (int)samples).Select(k => f[(i * 8 + k) * 4]).ToArray();
            ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)V(ctx)[15])(ctx, st, 0);
            return r;
        }
        catch (Exception ex) { note = "read-failed:" + ex.Message.Replace(' ', '_'); return null; }
        finally
        {
            SafeRelease(ref srv); SafeRelease(ref tex); SafeRelease(ref rtv); SafeRelease(ref cb); SafeRelease(ref st);
        }
    }

    static void Check(int hr) { if (hr < 0) throw new InvalidOperationException($"0x{hr:X8}"); }
}
