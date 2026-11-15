// Open a .3dm and prepare it for capture: Freppuccino display mode on its active view, optional colour on every F-rep
// primitive. Request: $SIZZLE_WORK/open3dm/request.txt = "<absolute path>[|#rrggbb]".
// Capture it from a SEPARATE script run (e.g. gallery-stills.cs): Rhino finishes setting up a freshly opened document's
// views only after the opening script ends, so a capture in the same run shows bare wireframes and no shader.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Rhino;
using Rhino.Display;

const string ClipId = "open3dm";
var work = Environment.GetEnvironmentVariable("SIZZLE_WORK")
           ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "freppuccino-sizzle");
var dir = Path.Combine(work, ClipId);
Directory.CreateDirectory(dir);
try
{
    // "NEW" = an empty untitled document (no template), for clip scripts that build their own scene.
    var parts = File.ReadAllText(Path.Combine(dir, "request.txt")).Trim().Split('|');
    var path = parts[0].Trim();
    bool isNew = path == "NEW";
    if (!isNew && !File.Exists(path)) throw new Exception($"no such file: {path}");
    var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Freppuccino")
              ?? throw new Exception("Freppuccino plugin not loaded (launch Rhino with launch-rhino.ps1)");
    var modeId = (Guid)asm.GetType("Freppuccino.Display.FreppuccinoDisplayMode")!.GetProperty("Id")!.GetValue(null)!;

    if (RhinoDoc.ActiveDoc != null) RhinoDoc.ActiveDoc.Modified = false;   // no "save changes?" prompt
    RhinoApp.RunScript(isNew ? "_-New _None" : $"_-Open \"{path}\"", false);
    var doc = RhinoDoc.ActiveDoc;
    if (isNew ? !string.IsNullOrEmpty(doc.Path) : !string.Equals(doc.Path, path, StringComparison.OrdinalIgnoreCase))
        throw new Exception($"{(isNew ? "new" : "open")} failed: active doc is {doc.Path}");
    int recoloured = 0;
    if (parts.Length > 1)
    {
        var setColor = asm.GetType("Freppuccino.Scene.FrepPrimitives")!.GetMethod("SetColor", new[] { typeof(RhinoDoc), typeof(Rhino.DocObjects.RhinoObject), typeof(System.Drawing.Color) })!;
        var c = System.Drawing.ColorTranslator.FromHtml(parts[1].Trim());
        foreach (var o in doc.Objects.ToList())
            if (o.Attributes.GetUserStrings().AllKeys.Any(k => k != null && k.StartsWith("SDF.")))
            {
                setColor.Invoke(null, new object[] { doc, o, c });
                recoloured++;
            }
    }
    var view = doc.Views.ActiveView;
    view.Maximized = true;
    view.ActiveViewport.DisplayMode = DisplayModeDescription.GetDisplayMode(modeId);
    doc.Views.Redraw();
    doc.Modified = false;   // recolouring is for the capture only; never prompt to save the example
    File.WriteAllText(Path.Combine(dir, "done.txt"), isNew ? "new empty document" : $"opened {Path.GetFileName(path)}, recoloured {recoloured}");
}
catch (Exception e)
{
    File.WriteAllText(Path.Combine(dir, "error.txt"), e.ToString());
}
