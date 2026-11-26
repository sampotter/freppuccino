using Freppuccino.Csg;
using Freppuccino.Display;
using Freppuccino.Gpu;
using Freppuccino.Sdf;
using Freppuccino.Scene;
using Freppuccino.Shaders;
using Freppuccino.Ui;
using Rhino;
using Rhino.FileIO;
using Rhino.PlugIns;
using Rhino.UI;

namespace Freppuccino;

public class FreppuccinoPlugin : PlugIn
{
    public const string IconResource = "Freppuccino.EmbeddedResources.plugin-freppuccino.ico";

    public FreppuccinoPlugin() => Instance = this;

    public static FreppuccinoPlugin? Instance { get; private set; }
    public static ShaderConduit? Conduit { get; private set; }

    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        NativeLibs.Install();
        FreppuccinoDisplayMode.Ensure();
        DevAutostart.Install();
        SdfTransformWatcher.Install();
        FrepScene.Install();
        FrepTrees.Install();
        Selection.FrepSelection.Install();
        Selection.FrepCarriers.Install();
        Selection.FrepPicker.Install();
        Conduit = new ShaderConduit { Enabled = true };
        Panels.RegisterPanel(this, typeof(FrepPanel), FrepPanel.Caption, GetType().Assembly,
            IconResource, PanelType.PerDoc);
        ToolbarReveal.Install(this);
        return LoadReturnCode.Success;
    }

    // CSG tree persisted in the .3dm as plugin data. Skipped for export-selected (leaf ids would dangle) and import/merge (node ids would collide).
    protected override bool ShouldCallWriteDocument(FileWriteOptions options) =>
        !options.WriteSelectedObjectsOnly && !options.WriteGeometryOnly &&
        (FrepTrees.Get(options.RhinoDoc).Nodes.Count > 0 || StoredShader(options) != null);

    protected override void WriteDocument(RhinoDoc doc, BinaryArchiveWriter archive, FileWriteOptions options)
    {
        FrepTrees.Get(doc).Write(archive);
        ShaderCache.Write(archive, StoredShader(options));
    }

    static string? StoredShader(FileWriteOptions options) =>
        options.IncludeRenderMeshes && FrepScene.Get(options.RhinoDoc).Program?.Hash is { } hash && ShaderCache.Has(hash) ? hash : null;

    protected override void ReadDocument(RhinoDoc doc, BinaryArchiveReader archive, FileReadOptions options)
    {
        if (options.ImportMode || options.ImportReferenceMode)
        {
            RhinoApp.WriteLine("Freppuccino: F-rep tree not imported (import/merge unsupported).");
            return;
        }
        var tree = FrepTree.Read(archive, out var minor);
        if (minor >= 4)
            try { ShaderCache.Read(archive); }
            catch (Exception ex) { RhinoApp.WriteLine($"Freppuccino: the shader saved in this file could not be read ({ex.Message}); it will be compiled."); }
        FrepTrees.Apply(doc, tree);
    }

    protected override void OnShutdown()
    {
        SdfTransformWatcher.Uninstall();
        if (Conduit != null) Conduit.Enabled = false;
        GpuBackends.DisposeAll();
    }
}
