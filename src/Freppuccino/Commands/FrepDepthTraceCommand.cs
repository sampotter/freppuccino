using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;

namespace Freppuccino.Commands;

[Guid("5d0c8e2a-7f41-4b39-9a6e-1c2b3d4e5f60")]
public class FrepDepthTraceCommand : Command
{
    public override string EnglishName => "FrepDepthTrace";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (doc.Views.ActiveView is not { } view) return Result.Nothing;
#if RHINO9
        var moving = new OptionToggle(false, "No", "Yes");
        var go = new GetOption();
        go.SetCommandPrompt("FrepDepthTrace: Enter records 5 frames of this view without moving the camera");
        go.AddOptionToggle("Moving", ref moving);
        go.AcceptNothing(true);
        while (go.Get() == Rhino.Input.GetResult.Option) { }
        if (go.CommandResult() != Result.Success) return go.CommandResult();
        if (moving.CurrentValue)
        {
            Gpu.D3D11.DepthTrace.Arm(view.ActiveViewport, 30);
            RhinoApp.WriteLine("FrepDepthTrace: recording the next 30 frames of this viewport while you work. A small black marker line appears in the top-left corner while it records.");
            view.Redraw();
            return Result.Success;
        }
        Gpu.D3D11.DepthTrace.Arm(view.ActiveViewport, 5);
        RhinoApp.WriteLine("FrepDepthTrace: recording 5 frames of this viewport without moving the camera. A small black marker line appears in the top-left corner while it records.");
        for (int i = 0; i < 10 && Gpu.D3D11.DepthTrace.Recording; i++)
        {
            view.Redraw();
            RhinoApp.Wait();
        }
        if (Gpu.D3D11.DepthTrace.Recording) RhinoApp.WriteLine("FrepDepthTrace: still recording; it finishes after 5 drawn frames of this viewport.");
        return Result.Success;
#else
        RhinoApp.WriteLine("FrepDepthTrace: Rhino 9 on Windows only.");
        return Result.Nothing;
#endif
    }
}
