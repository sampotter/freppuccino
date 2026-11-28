using System.Runtime.InteropServices;
using Freppuccino.Csg;
using Freppuccino.Ui;
using Rhino;
using Rhino.Commands;

namespace Freppuccino.Commands;

[Guid("9c41e7d3-2b85-4f06-a7e1-5d3c8f02b6a4")]
public class FOffsetCommand : FrepNodeCommand
{
    public override string EnglishName => "FOffset";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var operands = new List<FrepOperand>();
        var panel = UiController.TakeSelectedNodes(doc);
        if (panel.Count == 1 && doc.Objects.GetSelectedObjects(false, false).All(o => !IsSdf(o)))
            operands.Add(FrepOperand.Node(panel[0]));
        else
        {
            BeginPicking();
            try
            {
                var rc = Pick(doc, "Group to offset", false, true, _ => { }, operands);
                if (rc != Result.Success) return rc;
            }
            finally { EndPicking(); }
        }
        if (operands.Count != 1) return Result.Cancel;

        var tree = FrepTrees.Get(doc);
        if (!operands[0].IsNode || tree.Find(operands[0].NodeId) is not { IsGroup: true } group)
        {
            RhinoApp.WriteLine($"{EnglishName}: offset works on a group. Select one in the Freppuccino panel or type its name (N3).");
            return Result.Nothing;
        }
        var (next, offset) = tree.WrapOver(FrepOp.Offset, 0, group.Id);
        FrepTrees.Set(doc, next, "Offset");
        doc.Objects.UnselectAll();
        UiController.SelectNode(doc, offset.Id);
        RhinoApp.WriteLine($"{EnglishName}: created {offset.Name}; set its distance in the panel.");
        return Result.Success;
    }
}
