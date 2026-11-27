using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;

namespace Freppuccino.Commands;

[Guid("5d0c8e2a-7f41-4b39-9a6e-1c2b3d4e5f60")]
public class FrepDepthTraceCommand : Command
{
    public override string EnglishName => "FrepDepthTrace";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (doc.Views.ActiveView is not { } view) return Result.Nothing;
#if RHINO9
        Gpu.D3D11.DepthTrace.Arm(view.ActiveViewport);
        RhinoApp.WriteLine("FrepDepthTrace: recording the next 30 frames of this viewport; keep working as usual. A small black marker line appears in the top-left corner while it records.");
        view.Redraw();
        return Result.Success;
#else
        RhinoApp.WriteLine("FrepDepthTrace: Rhino 9 on Windows only.");
        return Result.Nothing;
#endif
    }
}
