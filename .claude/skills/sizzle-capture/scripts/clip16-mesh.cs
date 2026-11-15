// Sizzle reel clip c16_mesh. The coffee cup is meshed (FrepMesh's steps + QuadRemesh); shader and wireframe passes for a wipe (clip 16).
// Built by new-clip.sh from clip01-hook.cs: same HARNESS, own SCENE + ANIMATE. Run with run-clip.sh.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

const string ClipId = "c16_mesh";
const int Fps = 60, W = 1920, H = 1080;   // W x H must stay below the on-screen viewport size (see SKILL.md: tiling)
const double Seconds = 4.5;               // the raw take, with handles; the edit is cut from it at encode time
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
    // The coffee cup (the reel's bookend): (body ∪ handle) − inside, ∪ an espresso surface. It is meshed with the
    // plugin's own mesher (FrepFieldBuilder + DualContour, by reflection, as FrepMesh does) + Rhino's QuadRemesh, then
    // the same slow clockwise orbit is captured twice: pass A = the shader (frames/), pass B = the mesh's wireframe
    // (frames_mesh/, F-rep proxies hidden, Wireframe display mode). encode-mesh.sh wipes the wires across the cup.
    Tool("clear_scene", "{}");
    Tool("create_primitive", "{\"type\":\"cylinder\",\"name\":\"cup_body\",\"start\":[0,0,0],\"end\":[0,0,1.6],\"radius\":1.0,\"color\":\"#e0954a\"}");
    Tool("create_primitive", "{\"type\":\"torus\",\"name\":\"cup_handle\",\"center\":[1.12,0,0.82],\"axis\":[0,1,0],\"major_radius\":0.42,\"minor_radius\":0.12,\"color\":\"#e0954a\"}");
    Tool("create_primitive", "{\"type\":\"cylinder\",\"name\":\"cup_inside\",\"start\":[0,0,0.16],\"end\":[0,0,1.9],\"radius\":0.85,\"color\":\"#e0954a\"}");
    Tool("create_primitive", "{\"type\":\"cylinder\",\"name\":\"coffee\",\"start\":[0,0,0.16],\"end\":[0,0,1.3],\"radius\":0.87,\"color\":\"#3b2620\"}");
    var shell = (string)Tool("combine", "{\"op\":\"union\",\"operands\":[\"cup_body\",\"cup_handle\"],\"k\":0.1}")["ref"]!;
    var hollow = (string)Tool("combine", $"{{\"op\":\"subtract\",\"operands\":[\"{shell}\",\"cup_inside\"],\"k\":0.05}}")["ref"]!;
    var cup = (string)Tool("combine", $"{{\"op\":\"union\",\"operands\":[\"{hollow}\",\"coffee\"],\"k\":0.03}}")["ref"]!;

    // Mesh it (the FrepMesh command's steps, without its interactive pick).
    var treeType = asm.GetType("Freppuccino.Csg.FrepTree")!;
    var parse = new object?[] { cup, 0 };
    treeType.GetMethod("TryParseName")!.Invoke(null, parse);
    var operandType = asm.GetType("Freppuccino.Csg.FrepOperand")!;
    var roots = (System.Collections.IList)Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(operandType))!;
    roots.Add(operandType.GetMethod("Node")!.Invoke(null, new object[] { (int)parse[1]! }));
    var sceneData = asm.GetType("Freppuccino.Scene.FrepScene")!.GetMethod("Get")!.Invoke(null, new object[] { doc })!;
    var buildArgs = new object?[] { sceneData, roots, null };
    var field = asm.GetType("Freppuccino.Scene.FrepFieldBuilder")!.GetMethod("Build")!.Invoke(null, buildArgs)!;
    var dc = asm.GetType("Freppuccino.Meshing.DualContour")!.GetMethod("Mesh")!.Invoke(null, new object[] { field, 160, 0.02 })!;
    var verts = (System.Collections.IEnumerable)dc.GetType().GetProperty("Vertices")!.GetValue(dc)!;
    var tris = (System.Collections.Generic.List<int>)dc.GetType().GetProperty("Triangles")!.GetValue(dc)!;
    var mesh = new Mesh();
    foreach (var v in verts)
    {
        var vt = v.GetType();
        mesh.Vertices.Add((double)vt.GetProperty("X")!.GetValue(v)!, (double)vt.GetProperty("Y")!.GetValue(v)!, (double)vt.GetProperty("Z")!.GetValue(v)!);
    }
    for (int q = 0; q < tris.Count; q += 3) mesh.Faces.AddFace(tris[q], tris[q + 1], tris[q + 2]);
    mesh.Faces.CullDegenerateFaces(); mesh.Compact(); mesh.Normals.ComputeNormals();
    mesh.Unweld(RhinoMath.ToRadians(30), true);
    var quad = mesh.QuadRemesh(new QuadRemeshParameters { TargetQuadCount = 2600, AdaptiveQuadCount = true, AdaptiveSize = 50, DetectHardEdges = true });
    if (quad != null && quad.Faces.Count > 0) mesh = quad;
    var meshAttr = doc.CreateDefaultAttributes();
    meshAttr.Name = "cup_mesh";
    meshAttr.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject;
    meshAttr.ObjectColor = System.Drawing.ColorTranslator.FromHtml("#3b2620");
    var meshId = doc.Objects.AddMesh(mesh, meshAttr);
    doc.Objects.Hide(meshId, true);                                       // pass A: shader only
    var sdfIds = doc.Objects.Where(o => o.Attributes.GetUserStrings().AllKeys.Any(k => k != null && k.StartsWith("SDF."))).Select(o => o.Id).ToList();
    File.WriteAllText(Path.Combine(dir, "mesh.txt"), $"{mesh.Faces.Count} faces ({mesh.Faces.QuadCount} quads), closed={mesh.IsClosed}");

    var target = new Point3d(0.15, 0, 0.8);
    const double dist = 6.2, elev = 0.52;
    const double az0 = -78, az1 = -106;                                   // clockwise
    Point3d CamAt(double t) { double a = (az0 + (az1 - az0) * Smooth(t / Seconds)) * Math.PI / 180; var d = new Vector3d(Math.Cos(a), Math.Sin(a), elev); d.Unitize(); return target + d * dist; }

    // ───────────── ANIMATE (per clip) ─────────────
    for (int i = 0; i < frames; i++) Capture(i, target, CamAt(TimeOf(i)));   // pass A: the shader
    // pass B: the mesh's wireframe on the same camera path
    var meshDir = Path.Combine(dir, "frames_mesh");
    Directory.CreateDirectory(meshDir);
    foreach (var f in Directory.GetFiles(meshDir, "*.png")) File.Delete(f);
    var shaderDir = framesDir;
    try
    {
        foreach (var id in sdfIds) doc.Objects.Hide(id, true);
        doc.Objects.Show(meshId, true);
        vp.DisplayMode = DisplayModeDescription.FindByName("Wireframe");
        framesDir = meshDir;                                              // Capture() writes wherever framesDir points
        for (int i = 0; i < frames; i++) Capture(i, target, CamAt(TimeOf(i)));
    }
    finally
    {
        framesDir = shaderDir;
        foreach (var id in sdfIds) doc.Objects.Show(id, true);
        doc.Objects.Hide(meshId, true);
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
