// Sizzle reel clip c03_scoop. A hidden sphere scoops a filleted pocket through a box (clip 3).
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

const string ClipId = "c03_scoop";
const int Fps = 60, W = 1920, H = 1080;   // W x H must stay below the on-screen viewport size (see SKILL.md: tiling)
const double Seconds = 4.5;               // the raw take, with handles; the edit is cut from it at encode time
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
    // A Mocha box with a hidden sphere subtracted (filleted FDifference). The sphere enters above the front-left top
    // corner, dips into the top face and travels across it, then bites out of the top-front edge, so a filleted pocket
    // moves through the box. The cutter itself is invisible (a difference keeps the base's colour). Slow clockwise orbit.
    Tool("clear_scene", "{}");
    Tool("create_primitive", "{\"type\":\"box\",\"name\":\"block\",\"center\":[0,0,0.6],\"size\":[1.6,1.6,1.2],\"color\":\"#8a5a3c\"}");
    var p0 = new Point3d(-1.25, -1.25, 1.8);   // outside, above the front-left top corner
    var p1 = new Point3d(-0.55, -0.45, 1.3);   // dipping into the top face
    var p2 = new Point3d(0.35, 0.2, 1.28);     // across the top
    var p3 = new Point3d(0.5, -0.78, 1.12);    // biting out of the top-front edge
    Point3d Path3(double u)                    // cubic Bezier p0 -> p3
    {
        double a = 1 - u;
        return new Point3d(
            a * a * a * p0.X + 3 * a * a * u * p1.X + 3 * a * u * u * p2.X + u * u * u * p3.X,
            a * a * a * p0.Y + 3 * a * a * u * p1.Y + 3 * a * u * u * p2.Y + u * u * u * p3.Y,
            a * a * a * p0.Z + 3 * a * a * u * p1.Z + 3 * a * u * u * p2.Z + u * u * u * p3.Z);
    }
    string Center(Point3d c) => $"[{N(c.X)},{N(c.Y)},{N(c.Z)}]";
    Tool("create_primitive", $"{{\"type\":\"sphere\",\"name\":\"cutter\",\"center\":{Center(p0)},\"radius\":0.55,\"color\":\"#e0954a\"}}");
    Tool("combine", "{\"op\":\"difference\",\"operands\":[\"block\",\"cutter\"],\"k\":0.16}");
    var target = new Point3d(0, -0.05, 0.62);
    const double dist = 5.6, elev = 0.5;       // elevation as tan(angle): about 27 degrees above
    const double az0 = -118, az1 = -140;       // degrees; decreasing = clockwise seen from above

    // ───────────── ANIMATE (per clip) ─────────────
    for (int i = 0; i < frames; i++)
    {
        double t = TimeOf(i);
        double u = Smooth((t - 0.4) / 3.6);    // the cutter moves 0.4 s -> 4.0 s
        Tool("update_primitive", $"{{\"ref\":\"cutter\",\"center\":{Center(Path3(u))}}}");
        double az = (az0 + (az1 - az0) * Smooth(t / Seconds)) * Math.PI / 180;
        var toCam = new Vector3d(Math.Cos(az), Math.Sin(az), elev); toCam.Unitize();
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
