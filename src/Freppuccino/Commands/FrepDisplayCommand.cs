using System.Runtime.InteropServices;
using Freppuccino.Display;
using Freppuccino.Ui;
using Rhino;
using Rhino.Commands;
using Rhino.Input.Custom;

namespace Freppuccino.Commands;

[Guid("3d8f2b71-6c4e-4a9d-9e15-b2c7a0f4d863")]
public class FrepDisplayCommand : Command
{
    public override string EnglishName => "FrepDisplay";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (mode == RunMode.Interactive)
        {
            FrepDisplayForm.ShowOrFocus();
            return Result.Success;
        }
        var cutters = FrepDisplaySettings.Cutters;
        var geometry = FrepDisplaySettings.Geometry;
        var go = new GetOption();
        go.SetCommandPrompt("Freppuccino display");
        go.AcceptNothing(true);
        while (true)
        {
            go.ClearCommandOptions();
            int c = go.AddOptionEnumList("Cutters", cutters);
            int g = go.AddOptionEnumList("Geometry", geometry);
            var r = go.Get();
            if (r == Rhino.Input.GetResult.Nothing) break;
            if (r != Rhino.Input.GetResult.Option) return Result.Cancel;
            var o = go.Option();
            if (o.Index == c) cutters = go.GetSelectedEnumValue<CutterDisplay>();
            else if (o.Index == g) geometry = go.GetSelectedEnumValue<GeometryDisplay>();
        }
        FrepDisplaySettings.Cutters = cutters;
        FrepDisplaySettings.Geometry = geometry;
        return Result.Success;
    }
}
