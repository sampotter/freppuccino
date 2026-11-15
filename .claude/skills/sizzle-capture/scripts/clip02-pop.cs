// Sizzle reel clip c02_pop. Seven primitives pop in, one per half beat (plan doc shot list, clip 2).
// Built from clip01-hook.cs: same HARNESS, own SCENE + ANIMATE. Run with run-clip.sh.
//
// TEMPLATE FOR OTHER CLIPS: copy this file, change ClipId, the SCENE block and the ANIMATE block. Leave HARNESS alone.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

const string ClipId = "c02_pop";
const int Fps = 60, W = 1920, H = 1080;   // W x H must stay below the on-screen viewport size (see SKILL.md: tiling)
const double Seconds = 4.0;               // the raw take, with handles; the edit is cut from it at encode time
const double Lens = 50;

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
    // The SCENE block clears the document: never run it on a saved file (an example .3dm, someone's model).
    if (!string.IsNullOrEmpty(doc.Path))
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
    // Seven primitives pop in left to right, zig-zagging between a back and a front row, one every half beat (0.25 s
    // at 120 BPM), each growing from nothing with a small overshoot. A primitive is created when its pop starts (so
    // nothing is there before), then resized every frame with update_primitive. Locked 3/4 camera.
    Tool("clear_scene", "{}");
    string[] palette = { "#3b2620", "#e0954a", "#8a5a3c" };              // Espresso, Caramel, Mocha
    var items = new (string Name, string Type, double X, double Y)[]
    {
        ("p_sphere", "sphere", -2.7, 0.85), ("p_pyramid", "pyramid", -2.2, -0.85), ("p_box", "box", -0.9, 0.85),
        ("p_ellipsoid", "ellipsoid", -0.4, -0.85), ("p_cylinder", "cylinder", 0.9, 0.85), ("p_torus", "torus", 1.4, -0.85),
        ("p_cone", "cone", 2.7, 0.85),
    };
    const double firstPop = 0.5, popStep = 0.25, popLen = 0.24;
    double Pop(double t, int i)                                           // easeOutBack 0 -> 1 with ~10% overshoot
    {
        double u = Math.Clamp((t - (firstPop + i * popStep)) / popLen, 0, 1);
        const double c1 = 1.70158, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(u - 1, 3) + c1 * Math.Pow(u - 1, 2);
    }
    string Spec(int i, double s)                                          // the primitive at scale s, sitting on z = 0
    {
        var (_, type, x, y) = items[i];
        string P(double a, double b, double c) => $"[{N(a)},{N(b)},{N(c)}]";
        return type switch
        {
            "sphere" => $"\"center\":{P(x, y, 0.55 * s)},\"radius\":{N(0.55 * s)}",
            "box" => $"\"center\":{P(x, y, 0.5 * s)},\"size\":{P(0.95 * s, 0.95 * s, s)}",
            "cylinder" => $"\"start\":{P(x, y, 0)},\"end\":{P(x, y, 1.1 * s)},\"radius\":{N(0.45 * s)}",
            "cone" => $"\"start\":{P(x, y, 0)},\"end\":{P(x, y, 1.2 * s)},\"start_radius\":{N(0.52 * s)},\"end_radius\":0",
            "pyramid" => $"\"base_center\":{P(x, y, 0)},\"height\":{N(1.1 * s)},\"base_size\":{N(1.05 * s)}",
            "ellipsoid" => $"\"center\":{P(x, y, 0.45 * s)},\"radii\":{P(0.7 * s, 0.42 * s, 0.45 * s)}",
            _ => $"\"center\":{P(x, y, 0.55 * s)},\"axis\":[0,-0.55,1],\"major_radius\":{N(0.46 * s)},\"minor_radius\":{N(0.19 * s)}",
        };
    }
    var created = new bool[items.Length];
    var target = new Point3d(-0.6, 0, 0.45);
    var toCam = new Vector3d(-0.45, -1.0, 0.72); toCam.Unitize();         // 3/4 from front-left, about 33 degrees above
    const double dist = 9.2;

    // ───────────── ANIMATE (per clip) ─────────────
    for (int i = 0; i < frames; i++)
    {
        double t = TimeOf(i);
        for (int p = 0; p < items.Length; p++)
        {
            double s = Pop(t, p);
            if (t < firstPop + p * popStep) continue;                     // not popped yet: nothing in the scene
            s = Math.Max(s, 0.02);
            if (!created[p])
            {
                Tool("create_primitive", $"{{\"type\":\"{items[p].Type}\",\"name\":\"{items[p].Name}\",{Spec(p, s)},\"color\":\"{palette[p % 3]}\"}}");
                created[p] = true;
            }
            else if (t <= firstPop + p * popStep + popLen + 1.0 / Fps)    // still growing: resize
                Tool("update_primitive", $"{{\"ref\":\"{items[p].Name}\",{Spec(p, s)}}}");
        }
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
