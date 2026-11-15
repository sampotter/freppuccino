using System.Runtime.InteropServices;
using Freppuccino.Ui;
using Rhino;
using Rhino.Commands;
using Rhino.UI;

namespace Freppuccino.Commands;

/// Opens the Freppuccino side panel.
[Guid("a2d84e6f-3b19-4c57-8e0d-7f41b9c26e15")]
public class FrepPanelCommand : Command
{
    public override string EnglishName => "Freppuccino";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Panels.OpenPanel(typeof(FrepPanel).GUID);
        return Result.Success;
    }
}
