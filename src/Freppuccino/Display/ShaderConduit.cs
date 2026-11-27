using Freppuccino.Ai;
using Freppuccino.Gpu;
using Freppuccino.Scene;
using Freppuccino.Shaders;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace Freppuccino.Display;

/// Order (observed, Mac): unselected objects -> PostDrawObjects (us) -> DrawForeground -> selected/highlight pass -> DrawOverlay (gumball etc).
/// We draw depth-off over the unselected wires; later Rhino draws land on top. One RhinoStateReset after our draws (Rhino state cache).
public sealed class ShaderConduit : DisplayConduit
{
    const string Badge = "FREP MODE";
    const string Compiling = "Compiling shader…";
    const int BadgeTextHeight = 11, CompilingTextHeight = 14, BadgeMargin = 10, BadgePadX = 9, BadgePadY = 5, Spokes = 8;
    /// Rhino draws the viewport title (view name, e.g. "Perspective" or a restored named view) in the top-left corner;
    /// the badge sits just below it so long names never run into it. Logical px, y down.
    const int TitleClearance = 28;
    bool loggedFailure, loggedCap;
    Guid? early;
    bool earlyWroteDepth, earlyOwnOutlines;
    int earlySerial = -1;

    static readonly Eto.Forms.UITimer spin = new() { Interval = 1.0 / 12 };

    static ShaderConduit()
    {
        spin.Elapsed += (_, _) => RedrawFrepViews();
        ShaderBuilds.Changed += () =>
        {
            if (ShaderBuilds.Active) spin.Start();
            else spin.Stop();
        };
    }

    static void RedrawFrepViews()
    {
        foreach (var doc in RhinoDoc.OpenDocuments())
            foreach (var view in doc.Views.GetViewList(Rhino.Display.ViewTypeFilter.Model))
                if (view.ActiveViewport.DisplayMode?.Id == FreppuccinoDisplayMode.Id)
                    view.Redraw();
    }

    static void DrawCompiling(DisplayPipeline dp, System.Drawing.RectangleF badge)
    {
        var ink = System.Drawing.Color.FromArgb(30, 30, 30);
        float mid = (badge.Top + badge.Bottom) * 0.5f, radius = badge.Height * 0.5f;
        float cx = badge.Right + BadgeMargin + radius;
        int lead = (int)(Environment.TickCount64 / 80 % Spokes);
        for (int i = 0; i < Spokes; i++)
        {
            double a = 2 * Math.PI * i / Spokes;
            float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
            int age = (lead - i + Spokes) % Spokes;
            var c = System.Drawing.Color.FromArgb(255 - age * 28, ink);
            dp.Draw2dLine(new System.Drawing.PointF(cx + dx * radius * 0.45f, mid + dy * radius * 0.45f),
                new System.Drawing.PointF(cx + dx * radius, mid + dy * radius), c, 2.5f);
        }
        dp.Draw2dText(Compiling, ink, new Point2d(cx + radius + 6, mid - CompilingTextHeight * 0.5), false, CompilingTextHeight, "Arial");
    }

    /// F-rep control breps aren't drawn by Rhino in this mode; we outline them (ControlOutlines).
    protected override void PreDrawObject(DrawObjectEventArgs e) => ControlOutlines.Suppress(e);

    /// Build error (shaderc / MSL / HLSL / PSO text) of <paramref name="doc"/>'s current scene program, or null (no program,
    /// it builds, or nothing has drawn yet). Builds it now if needed (cached), so it's current before the next redraw.
    /// Read by the AI tools (get_scene, get_function). Draws keep the last good program meanwhile (see DrawScene).
    public static string? ShaderError(RhinoDoc doc) =>
        FrepScene.Get(doc).Program is { } program ? GpuBackends.CompileError(program) : null;
    // Per doc (RuntimeSerialNumber): last scene program that drew.
    static readonly Dictionary<uint, ShaderSource> lastGood = [];

    protected override void PreDrawObjects(DrawEventArgs e)
    {
        var dp = e.Display;
        early = null;
        earlyWroteDepth = false;
        earlyOwnOutlines = false;
        earlySerial = -1;
        if (!FreppuccinoDisplayMode.IsActive(dp)) return;
        TraceBegin(dp);
        try
        {
            if (GpuBackends.For(dp) is not { } gpu || !WritesDepth(gpu, dp)) return;
            bool own = OwnOutlinesPossible(gpu, dp);
            int before = GBufferSerial(gpu);
            var wrote = DrawModel(gpu, dp, e.RhinoDoc, curves: !own, depth: true).WroteDepth;
            int after = GBufferSerial(gpu);
            earlyWroteDepth = wrote;
            earlyOwnOutlines = own;
            earlySerial = after > before ? after : -1;
            early = dp.Viewport?.Id;
            Trace(dp, "pre-after-early", early, wrote, frame: true);
        }
        catch (Exception ex)
        {
            if (!loggedFailure) RhinoApp.WriteLine("Freppuccino: " + ex);
            loggedFailure = true;
        }
    }

    (bool Drew, bool WroteDepth) DrawModel(IGpuBackend gpu, DisplayPipeline dp, RhinoDoc doc, bool curves, bool depth)
    {
        var scene = FrepScene.Get(doc);
        if (scene.Dropped > 0 && !loggedCap)
        {
            RhinoApp.WriteLine($"Freppuccino: uniform budget full, {scene.Dropped} primitives not drawn");
            loggedCap = true;
        }
        // Control outlines first, so blends drawn over them hide what they cover. Captures (AI screenshots,
        // AiCapture.CleanCapture) show just the model.
        if (!AiCapture.Active && curves)
        {
            if (depth) dp.PushDepthWriting(false);
            try
            {
                ControlOutlines.Draw(dp, doc, selected: false);
                dp.Flush(); // Rhino batches curves; without this they land after (over) our GPU draw
            }
            finally
            {
                if (depth) dp.PopDepthWriting();
            }
        }
        bool drew = scene.Program != null && DrawScene(gpu, dp, doc, scene, depth);
        bool wrote = drew && DrewDepth(gpu);
        if (drew) RhinoStateReset.Apply(dp);
        return (drew, wrote);
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
        var dp = e.Display;
        bool late = early != null && early == dp.Viewport?.Id, wroteDepth = late && earlyWroteDepth, own = late && earlyOwnOutlines;
        int serial = late ? earlySerial : -1;
        Trace(dp, late ? "post-entry" : "post-entry-no-pre", early, wroteDepth, frame: !late);
        early = null;
        if (!FreppuccinoDisplayMode.IsActive(dp)) return;
        try
        {
            var gpu = GpuBackends.For(dp);
            if (gpu == null) return;
            if (!late) TraceBegin(dp);
            bool drew = !late && DrawModel(gpu, dp, e.RhinoDoc, curves: true, depth: false).Drew;
            if (!late) Trace(dp, "post-after-fallback", null, false);
            if (!AiCapture.Active && own) DrawOwnOutlines(gpu, dp, e.RhinoDoc, serial, wroteDepth);

            if (!AiCapture.Active) // AI screenshots: just the model
            {
                var (r, size) = BadgeRect(dp);
                drew |= gpu.Draw(dp, PillShader.Source, PillShader.Uniforms(r.Left, r.Top, r.Right, r.Bottom, size.Width, size.Height));
            }

            if (drew) RhinoStateReset.Apply(dp);
        }
        catch (Exception ex)
        {
            if (!loggedFailure) RhinoApp.WriteLine("Freppuccino: " + ex);
            loggedFailure = true;
        }
    }

    /// Draws the scene program. If it failed to compile, draws the doc's last good program instead, but only when it reads
    /// the uniforms the same way (same Layout: e.g. FFunction code edited, param count unchanged); else nothing. The error
    /// is reported by <see cref="ShaderError"/>; failed programs are not retried (backend cache).
    static bool DrawScene(IGpuBackend gpu, DisplayPipeline dp, RhinoDoc doc, FrepSceneData scene, bool depth)
    {
        var program = scene.Program!;
        var uniforms = FrepShaderGen.SceneUniforms(dp.Viewport, scene, Selection.FrepSelection.DrawnRoots(doc));
        if (DrawProgram(gpu, dp, program, uniforms, depth))
        {
            lastGood[doc.RuntimeSerialNumber] = program;
            return true;
        }
        if (gpu.CompileError(program) == null && !gpu.Building(program)) return false; // e.g. a pass we can't draw in
        return lastGood.TryGetValue(doc.RuntimeSerialNumber, out var good) && good.Hash != program.Hash &&
               good.Layout.Length > 0 && good.Layout == program.Layout && DrawProgram(gpu, dp, good, uniforms, depth);
    }

#if RHINO9
    static bool WritesDepth(IGpuBackend gpu, DisplayPipeline dp) => gpu is Gpu.D3D11.D3D11Backend d3d && d3d.ExpectsDepth(dp);

    static bool DrewDepth(IGpuBackend gpu) => gpu is Gpu.D3D11.D3D11Backend { WroteDepth: true };

    public static bool OwnOutlines { get; set; } = true;
    static bool loggedOutlineSkip;
    public static double OutlinePullPx { get; set; } = 0.0;

    static bool OwnOutlinesPossible(IGpuBackend gpu, DisplayPipeline dp) => OwnOutlines && gpu is Gpu.D3D11.D3D11Backend d3d && d3d.OutlinesPossible(dp);

    static int GBufferSerial(IGpuBackend gpu) => gpu is Gpu.D3D11.D3D11Backend d3d ? d3d.GBufferSerial : 0;

    static void DrawOwnOutlines(IGpuBackend gpu, DisplayPipeline dp, RhinoDoc doc, int serial, bool wroteDepth)
    {
        if (Gpu.D3D11.DepthTrace.Armed(dp)) Gpu.D3D11.DepthTrace.Reference(dp, gpu as Gpu.D3D11.D3D11Backend);
        Trace(dp, "post-before-outlines", null, wroteDepth);
        if (gpu is not Gpu.D3D11.D3D11Backend d3d || serial < 0 || !wroteDepth)
        {
            if (!loggedOutlineSkip) RhinoApp.WriteLine("Freppuccino: cutter outlines skipped this frame (no current G-buffer).");
            loggedOutlineSkip = true;
            return;
        }
        float halfWidth = (float)(0.5 * Math.Ceiling(dp.DpiScale - 1e-3));
        d3d.DrawOutlines(dp, serial, ControlOutlines.Segments(dp, doc, cutters: true), ControlOutlines.UnselectedColor, FrepDisplaySettings.HiddenLines == HiddenLineDisplay.Faint ? ControlOutlines.HiddenAlpha / 255f : 0f, halfWidth, (float)OutlinePullPx);
        d3d.DrawOutlines(dp, serial, ControlOutlines.Segments(dp, doc, cutters: false), ControlOutlines.UnselectedColor, 1f, halfWidth, (float)OutlinePullPx, added: true);
        Trace(dp, "post-after-outlines", null, wroteDepth);
    }

    static void TraceBegin(DisplayPipeline dp) => Gpu.D3D11.DepthTrace.Begin(dp, GpuBackends.For(dp) as Gpu.D3D11.D3D11Backend);

    static void Trace(DisplayPipeline dp, string stage, Guid? early, bool wrote, bool frame = false)
    {
        if (!Gpu.D3D11.DepthTrace.Armed(dp)) return;
        if (frame) Gpu.D3D11.DepthTrace.Frame();
        Gpu.D3D11.DepthTrace.Sample(dp, stage, GpuBackends.For(dp) as Gpu.D3D11.D3D11Backend, early, wrote);
    }

    static bool DrawProgram(IGpuBackend gpu, DisplayPipeline dp, ShaderSource program, byte[] uniforms, bool depth) =>
        gpu is Gpu.D3D11.D3D11Backend d3d ? d3d.DrawOccluded(dp, program, uniforms, depth) : gpu.Draw(dp, program, uniforms);
#else
    static bool OwnOutlinesPossible(IGpuBackend gpu, DisplayPipeline dp) => false;

    static int GBufferSerial(IGpuBackend gpu) => 0;

    static void DrawOwnOutlines(IGpuBackend gpu, DisplayPipeline dp, RhinoDoc doc, int serial, bool wroteDepth) { }

    static void TraceBegin(DisplayPipeline dp) { }

    static void Trace(DisplayPipeline dp, string stage, Guid? early, bool wrote, bool frame = false) { }

    static bool WritesDepth(IGpuBackend gpu, DisplayPipeline dp) => false;

    static bool DrewDepth(IGpuBackend gpu) => false;

    static bool DrawProgram(IGpuBackend gpu, DisplayPipeline dp, ShaderSource program, byte[] uniforms, bool depth) =>
        gpu.Draw(dp, program, uniforms);
#endif

    protected override void DrawForeground(DrawEventArgs e)
    {
        var dp = e.Display;
        if (dp != null) Trace(dp, "fore", null, false);
        if (dp == null || AiCapture.Active || !FreppuccinoDisplayMode.IsActive(dp) || GpuBackends.For(dp) == null) return;
        try
        {
            // Selected leaves: their outline in the selection colour, over the blends.
            ControlOutlines.Draw(dp, e.RhinoDoc, selected: true);
            var (r, _) = BadgeRect(dp);
            dp.Draw2dText(Badge, System.Drawing.Color.FromArgb(30, 30, 30), new Point2d((r.Left + r.Right) * 0.5, (r.Top + r.Bottom) * 0.5), true, BadgeTextHeight, "Arial");
            if (FrepScene.Get(e.RhinoDoc).Program is { } program && GpuBackends.For(dp)!.Building(program))
                DrawCompiling(dp, r);
        }
        catch (Exception ex)
        {
            // Offscreen pipelines (view capture, named-view thumbnails) may lack text/viewport state.
            if (!loggedFailure) RhinoApp.WriteLine("Freppuccino: " + ex);
            loggedFailure = true;
        }
    }

    /// Badge rect in Rhino 2D screen coords (y down), sized from the measured text, offset below Rhino's viewport title.
    /// Falls back to an estimated size when measuring isn't available (e.g. offscreen capture pipelines).
    static (System.Drawing.RectangleF rect, System.Drawing.Size view) BadgeRect(DisplayPipeline dp)
    {
        var size = dp.Viewport?.Size ?? System.Drawing.Size.Empty;
        float tw, th;
        try
        {
            var t = dp.Measure2dText(Badge, new Point2d(0, 0), false, 0, BadgeTextHeight, "Arial");
            tw = Math.Abs(t.Width); th = Math.Abs(t.Height); // Height comes back negative (y-up rect)
        }
        catch (NullReferenceException)
        {
            tw = 0; th = 0;
        }
        if (tw <= 0 || th <= 0) { tw = Badge.Length * BadgeTextHeight * 0.65f; th = BadgeTextHeight; }
        float w = tw + 2 * BadgePadX, h = th + 2 * BadgePadY;
        return (new System.Drawing.RectangleF(BadgeMargin, TitleClearance, w, h), size);
    }
}
