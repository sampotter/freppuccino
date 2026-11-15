// Sizzle reel clip c08_colonnade. A column repeats, the copies fuse with Repeat blend, the row bends (clip 8).
// Built by new-clip.sh from clip01-hook.cs: same HARNESS, own SCENE + ANIMATE. Run with run-clip.sh.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

const string ClipId = "c08_colonnade";
const int Fps = 60, W = 1920, H = 1080;   // W x H must stay below the on-screen viewport size (see SKILL.md: tiling)
const double Seconds = 5.0;               // the raw take, with handles; the edit is cut from it at encode time
const double Lens = 50;
const bool ClearsScene = true;            // false = the clip works on an opened file (open-3dm.cs) and must not clear it

// ───────────── HARNESS (same in every clip script) ─────────────
var work = Environment.GetEnvironmentVariable("SIZZLE_WORK")
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "freppuccino-sizzle");
var dir = Path.Combine(work, ClipId);
var framesDir = Path.Combine(dir, "frames");
Directory.CreateDirectory(framesDir);
IDisposable? clean = null;
try
{
    var cfgPath = Path.Combine(dir, "config.json");
    var mode = File.Exists(cfgPath) ? (string)JsonNode.Parse(File.ReadAllText(cfgPath))!["mode"]! : "full";
    foreach (var f in Directory.GetFiles(framesDir, "*.png")) File.Delete(f);

    var doc = RhinoDoc.ActiveDoc;
    // A SCENE block that clears the document must never run on a saved file (an example .3dm, someone's model).
    if (ClearsScene && !string.IsNullOrEmpty(doc.Path))
        throw new Exception($"the active document is {doc.Path}; this script clears the scene. Open an empty one first: echo NEW > $SIZZLE_WORK/open3dm/request.txt && run-clip.sh open-3dm.cs open3dm");
    var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Freppuccino")
              ?? throw new Exception("Freppuccino plugin not loaded (launch Rhino with launch-rhino.ps1)");
    var run = asm.GetType("Freppuccino.Ai.AiTools")!.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)!;
    // The AI tool API: create_primitive, combine, deform, edit_operation, set_color, clear_scene, ... (see Ai/AiTools.cs).
    JsonNode Tool(string name, string json)
    {
        try { return (JsonNode)run.Invoke(null, new object[] { doc, name, JsonNode.Parse(json)!.AsObject() })!; }
        catch (TargetInvocationException e) { throw new Exception($"{name} {json}: {e.InnerException?.Message}"); }
    }
    string N(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * x * (x * (x * 6 - 15) + 10); } // smootherstep
    var dm = asm.GetType("Freppuccino.Display.FreppuccinoDisplayMode")!;
    var modeId = (Guid)dm.GetProperty("Id")!.GetValue(null)!;
    var capId = (Guid)dm.GetProperty("CaptureId")!.GetValue(null)!; // proxies invisible, no edges
    // Captures show just the model (no FREP MODE badge, no control outlines) while this scope is alive. Builds before
    // AiCapture.CleanCapture (the gyroid / displacements branches) return null: no outlines there anyway, and
    // encode.sh still clears the badge corner.
    clean = asm.GetType("Freppuccino.Ai.AiCapture")?.GetMethod("CleanCapture", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as IDisposable;

    var view = doc.Views.Find("Perspective", false) ?? doc.Views.ActiveView;
    doc.Views.ActiveView = view;
    view.Maximized = true;                                 // a 4-view layout makes the viewport too small (tiling)
    view.Redraw();
    RhinoApp.Wait();
    var vp = view.ActiveViewport;
    vp.DisplayMode = DisplayModeDescription.GetDisplayMode(capId);
    vp.ChangeToPerspectiveProjection(true, Lens);
    var screen = view.ScreenRectangle;
    if (W > screen.Width || H > screen.Height)
        throw new Exception($"capture {W}x{H} is larger than the viewport {screen.Width}x{screen.Height}: it would tile and repeat the FREP MODE badge. Maximise the view or capture smaller.");

    // One frame = 4 captures with the camera shifted by rotated-grid sub-pixel offsets; encode.sh averages them.
    double[][] jitter = { new[] { 0.125, 0.375 }, new[] { 0.375, -0.125 }, new[] { -0.125, -0.375 }, new[] { -0.375, 0.125 } };
    // The scene's shader must exist before a capture, or that frame shows bare wireframes. ShaderError compiles it now
    // (cached, so this is cheap once built; a cold cache after a restart can take seconds) and returns any build error.
    var shaderError = asm.GetType("Freppuccino.Display.ShaderConduit")!.GetMethod("ShaderError", BindingFlags.Public | BindingFlags.Static)!;
    void Capture(int frame, Point3d target, Point3d camera)
    {
        if (shaderError.Invoke(null, new object[] { doc }) is string err) throw new Exception($"frame {frame}: shader does not compile: {err}");
        for (int j = 0; j < jitter.Length; j++)
        {
            vp.SetCameraLocations(target, camera);
            vp.CameraUp = Vector3d.ZAxis;
            vp.GetFrustum(out _, out _, out _, out double ftop, out double fnear, out _);
            double px = 2 * ftop / fnear * camera.DistanceTo(target) / H;  // world units per pixel at the target
            var shift = vp.CameraX * (jitter[j][0] * px) + vp.CameraY * (jitter[j][1] * px);
            vp.SetCameraLocations(target + shift, camera + shift);
            vp.CameraUp = Vector3d.ZAxis;
            var vc = new ViewCapture { Width = W, Height = H, TransparentBackground = true, DrawGrid = false, DrawAxes = false, DrawGridAxes = false, ScaleScreenItems = false };
            // Don't write `CaptureToBitmap(view) ?? throw ...` or similar: it makes the compiler resolve Bitmap's
            // interfaces from assemblies the Rhino script environment doesn't reference, and the script then fails to
            // compile SILENTLY (no error.txt, nothing in the logs). `?.` is fine.
            var bmp = vc.CaptureToBitmap(view);
            bmp?.Save(Path.Combine(framesDir, $"f{frame:D4}_{j}.png"));
            bmp?.Dispose();
            if (!File.Exists(Path.Combine(framesDir, $"f{frame:D4}_{j}.png"))) throw new Exception($"capture failed at frame {frame}");
        }
    }
    int frames = mode == "full" ? (int)Math.Round(Seconds * Fps) : 2;
    double TimeOf(int i) => mode == "full" ? i / (double)Fps : (i == 0 ? 0 : Seconds); // test mode: first + last pose

    // ───────────── SCENE (per clip) ─────────────
    // One column (plinth + shaft + capital, Caramel / Mocha) repeats into a row: the copies step from 1 to 7 on the
    // half beat, the Repeat's blend fuses them into an arcade, then the row bends into an arc. Sideways dolly.
    // A Repeat needs count >= 2, so the repeat -> blend -> bend chain is built on the frame the second copy appears
    // (before that, the plain column). The Repeat blend has no AI tool: it's made with the tree API
    // (FrepTree.BlendOver, by reflection) and animated with edit_operation like any node's k.
    Tool("clear_scene", "{}");
    Tool("create_primitive", "{\"type\":\"box\",\"name\":\"plinth\",\"center\":[0,0,0.08],\"size\":[0.64,0.64,0.16],\"color\":\"#8a5a3c\"}");
    Tool("create_primitive", "{\"type\":\"cylinder\",\"name\":\"shaft\",\"start\":[0,0,0.12],\"end\":[0,0,1.9],\"radius\":0.15,\"color\":\"#e0954a\"}");
    Tool("create_primitive", "{\"type\":\"box\",\"name\":\"capital\",\"center\":[0,0,1.95],\"size\":[0.64,0.64,0.14],\"color\":\"#8a5a3c\"}");
    var column = (string)Tool("combine", "{\"op\":\"union\",\"operands\":[\"plinth\",\"shaft\",\"capital\"],\"k\":0.08}")["ref"]!;
    const double spacing = 0.78;
    const int maxCount = 7;
    double rowLen = (maxCount - 1) * spacing;
    string RepeatSpec(int n) => $"\"count\":[{n},1,1],\"spacing\":[{N(spacing)},0,0],\"centered\":false";
    string? repeat = null, blend = null, bend = null;
    void BuildChain(int count)
    {
        repeat = (string)Tool("deform", $"{{\"op\":\"repeat\",\"target\":\"{column}\",{RepeatSpec(count)}}}")["ref"]!;
        var trees = asm.GetType("Freppuccino.Csg.FrepTrees")!;
        var treeType = asm.GetType("Freppuccino.Csg.FrepTree")!;
        var parseArgs = new object?[] { repeat, 0 };
        treeType.GetMethod("TryParseName")!.Invoke(null, parseArgs);
        var tree0 = trees.GetMethod("Get")!.Invoke(null, new object[] { doc })!;
        var wrapped = treeType.GetMethod("BlendOver")!.Invoke(tree0, new object[] { (int)parseArgs[1]! })!;
        trees.GetMethod("Set")!.Invoke(null, new object[] { doc, wrapped.GetType().GetField("Item1")!.GetValue(wrapped)!, "sizzle: repeat blend" });
        var blendNode = wrapped.GetType().GetField("Item2")!.GetValue(wrapped)!;
        blend = (string)blendNode.GetType().GetProperty("Name")!.GetValue(blendNode)!;
        bend = (string)Tool("deform", $"{{\"op\":\"bend\",\"target\":\"{blend}\",\"axis_start\":[0,0,0],\"axis_end\":[{N(rowLen)},0,0],\"angle_degrees\":0,\"toward\":[0,1,0]}}")["ref"]!;
    }
    const double elev = 0.55, dist = 9.8;

    // ───────────── ANIMATE (per clip) ─────────────
    int lastCount = 1;
    for (int i = 0; i < frames; i++)
    {
        double t = TimeOf(i);
        int count = Math.Min(maxCount, 1 + (int)Math.Floor(Math.Max(0, t - 0.25) / 0.25));   // +1 copy per half beat
        if (count >= 2 && repeat == null) { BuildChain(count); lastCount = count; }
        else if (count != lastCount) { Tool("edit_operation", $"{{\"ref\":\"{repeat}\",{RepeatSpec(count)}}}"); lastCount = count; }
        if (blend != null)
        {
            Tool("edit_operation", $"{{\"ref\":\"{blend}\",\"k\":{N(0.5 * Smooth((t - 1.9) / 0.9))}}}");      // 1.9 -> 2.8 s
            Tool("edit_operation", $"{{\"ref\":\"{bend}\",\"angle_degrees\":{N(80 * Smooth((t - 2.9) / 1.5))}}}"); // 2.9 -> 4.4 s
        }
        double dolly = Smooth(t / Seconds);                                // camera slides along the row
        var target = new Point3d(1.0 + 1.1 * dolly, 1.1 * dolly, 0.95);           // ends on the middle of the arc
        var toCam = new Vector3d(-0.35, -1.0, elev); toCam.Unitize();
        Capture(i, target, target + toCam * dist);
    }

    // ───────────── HARNESS (end) ─────────────
    vp.DisplayMode = DisplayModeDescription.GetDisplayMode(modeId);
    view.Redraw();
    File.WriteAllText(Path.Combine(dir, "done.txt"), $"{ClipId}: {frames} frames x {jitter.Length} captures, {W}x{H}, mode {mode} -> {framesDir}");
}
catch (Exception e)
{
    File.WriteAllText(Path.Combine(dir, "error.txt"), e.ToString());
}
finally
{
    clean?.Dispose(); // always, or the user's views lose their badge and outlines until Rhino restarts
    RhinoDoc.ActiveDoc?.Views.Redraw();
}
