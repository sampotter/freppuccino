// Sizzle reel clip 01 (hook): espresso + caramel spheres start just apart; the union's blend k ramps 0 -> 0.7 and they
// melt into one blob while the camera pushes in. Run with run-clip.sh (not directly): it writes config.json and waits
// for done.txt / error.txt.
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

const string ClipId = "c01_hook";
const int Fps = 60, W = 1920, H = 1080;   // W x H must stay below the on-screen viewport size (see SKILL.md: tiling)
const double Seconds = 7.0;               // the raw take, with handles; the edit is cut from it at encode time
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
    Tool("clear_scene", "{}");
    // Start the spheres just apart (gap ~0.09) so the blend visibly pulls them together.
    Tool("create_primitive", "{\"type\":\"sphere\",\"name\":\"espresso\",\"center\":[-0.7,0,0],\"radius\":0.72,\"color\":\"#3b2620\"}");
    Tool("create_primitive", "{\"type\":\"sphere\",\"name\":\"caramel\",\"center\":[0.7,0.1,0.12],\"radius\":0.6,\"color\":\"#e0954a\"}");
    var node = (string)Tool("combine", "{\"op\":\"union\",\"operands\":[\"espresso\",\"caramel\"],\"k\":0}")["ref"]!;
    var target = new Point3d(-0.5, 0, -0.02);              // left of the blob, so the blob sits right of centre
    var toCam = new Vector3d(-0.85, -4.6, 1.45); toCam.Unitize(); // about 18 degrees above
    const double dist0 = 6.1, dist1 = 5.35;               // slow push in over the whole take

    // ───────────── ANIMATE (per clip) ─────────────
    for (int i = 0; i < frames; i++)
    {
        double t = TimeOf(i);
        double k = 0.7 * Smooth((t - 0.75) / 4.0);        // blend 0.75 s -> 4.75 s
        Tool("edit_operation", $"{{\"ref\":\"{node}\",\"k\":{N(k)}}}");
        double dist = dist0 + (dist1 - dist0) * Smooth(t / Seconds);
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
