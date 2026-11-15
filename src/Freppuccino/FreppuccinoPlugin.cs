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
        return LoadReturnCode.Success;
    }

    // CSG tree persisted in the .3dm as plugin data. Skipped for export-selected (leaf ids would dangle) and import/merge (node ids would collide).
    protected override bool ShouldCallWriteDocument(FileWriteOptions options) =>
        !options.WriteSelectedObjectsOnly && !options.WriteGeometryOnly && FrepTrees.Get(options.RhinoDoc).Nodes.Count > 0;

    protected override void WriteDocument(RhinoDoc doc, BinaryArchiveWriter archive, FileWriteOptions options) =>
        FrepTrees.Get(doc).Write(archive);

    protected override void ReadDocument(RhinoDoc doc, BinaryArchiveReader archive, FileReadOptions options)
    {
        if (options.ImportMode || options.ImportReferenceMode)
        {
            RhinoApp.WriteLine("Freppuccino: F-rep tree not imported (import/merge unsupported).");
            return;
        }
        FrepTrees.Apply(doc, FrepTree.Read(archive));
    }

    protected override void OnShutdown()
    {
        SdfTransformWatcher.Uninstall();
        if (Conduit != null) Conduit.Enabled = false;
        GpuBackends.DisposeAll();
    }
}
