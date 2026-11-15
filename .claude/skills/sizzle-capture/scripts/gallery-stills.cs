// Gallery still: capture the ACTIVE document (opened by open-3dm.cs in a previous script run) as one 4x-jittered
// still, framed in a 3/4 view from above. Request: $SIZZLE_WORK/gallery/request.txt = "<frame index>" -> writes
// frames/f<index>_0..3.png. Driven by gallery.sh; composite with stills.sh.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

const string ClipId = "gallery";
const int W = 1920, H = 1080;
const double Lens = 50;

var work = Environment.GetEnvironmentVariable("SIZZLE_WORK")
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "freppuccino-sizzle");
var dir = Path.Combine(work, ClipId);
var framesDir = Path.Combine(dir, "frames");
Directory.CreateDirectory(framesDir);
IDisposable? clean = null;
try
{
    int index = int.Parse(File.ReadAllText(Path.Combine(dir, "request.txt")).Trim());
    var doc = RhinoDoc.ActiveDoc;
    var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Freppuccino")
              ?? throw new Exception("Freppuccino plugin not loaded (launch Rhino with launch-rhino.ps1)");
    var dm = asm.GetType("Freppuccino.Display.FreppuccinoDisplayMode")!;
    var modeId = (Guid)dm.GetProperty("Id")!.GetValue(null)!;
    var capId = (Guid)dm.GetProperty("CaptureId")!.GetValue(null)!;
    var shaderError = asm.GetType("Freppuccino.Display.ShaderConduit")!.GetMethod("ShaderError", BindingFlags.Public | BindingFlags.Static)!;
    // Just the model: no badge, no control outlines (null on builds before AiCapture.CleanCapture).
    clean = asm.GetType("Freppuccino.Ai.AiCapture")?.GetMethod("CleanCapture", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as IDisposable;
    double[][] jitter = { new[] { 0.125, 0.375 }, new[] { 0.375, -0.125 }, new[] { -0.125, -0.375 }, new[] { -0.375, 0.125 } };

    // Use the file's own active view: files save their own layout, and Views.Find("Perspective") can return a hidden view.
    var view = doc.Views.ActiveView;
    view.Maximized = true;
    var vp = view.ActiveViewport;
    var screen = view.ScreenRectangle;
    if (W > screen.Width || H > screen.Height)
        throw new Exception($"capture {W}x{H} is larger than the viewport {screen.Width}x{screen.Height} (tiling)");
    vp.DisplayMode = DisplayModeDescription.GetDisplayMode(capId);
    vp.ChangeToPerspectiveProjection(true, Lens);

    var bbox = BoundingBox.Empty;
    foreach (var o in doc.Objects.Where(o => o.Visible)) bbox.Union(o.Geometry.GetBoundingBox(true));
    if (!bbox.IsValid) throw new Exception($"{doc.Name}: nothing visible");
    var center = bbox.Center;
    var dirToCam = new Vector3d(-0.75, -1.6, 0.75); dirToCam.Unitize();   // 3/4, about 25 degrees above
    vp.SetCameraLocations(center, center + dirToCam * bbox.Diagonal.Length * 3);
    vp.CameraUp = Vector3d.ZAxis;
    vp.ZoomBoundingBox(bbox);
    var target = vp.CameraTarget;
    var camera = target + (vp.CameraLocation - target) * 0.82;   // a little tighter than ZoomBoundingBox's margin

    // The scene's shader must exist before capturing. ShaderError compiles it now (cached) and returns any build error.
    if (shaderError.Invoke(null, new object[] { doc }) is string err) throw new Exception($"{doc.Name}: shader does not compile: {err}");
    for (int j = 0; j < jitter.Length; j++)
    {
        vp.SetCameraLocations(target, camera);
        vp.CameraUp = Vector3d.ZAxis;
        vp.GetFrustum(out _, out _, out _, out double ftop, out double fnear, out _);
        double px = 2 * ftop / fnear * camera.DistanceTo(target) / H;
        var shift = vp.CameraX * (jitter[j][0] * px) + vp.CameraY * (jitter[j][1] * px);
        vp.SetCameraLocations(target + shift, camera + shift);
        vp.CameraUp = Vector3d.ZAxis;
        var vc = new ViewCapture { Width = W, Height = H, TransparentBackground = true, DrawGrid = false, DrawAxes = false, DrawGridAxes = false, ScaleScreenItems = false };
        var bmp = vc.CaptureToBitmap(view);   // not `?? throw`: see SKILL.md (silent compile failure)
        bmp?.Save(Path.Combine(framesDir, $"f{index:D4}_{j}.png"));
        bmp?.Dispose();
    }
    vp.DisplayMode = DisplayModeDescription.GetDisplayMode(modeId);
    File.WriteAllText(Path.Combine(dir, "done.txt"), $"{doc.Name} -> f{index:D4}, bbox {bbox.Diagonal}");
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
