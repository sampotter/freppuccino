using System.Runtime.InteropServices;
using Freppuccino.Csg;
using Freppuccino.Scene;
using Freppuccino.Ui;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace Freppuccino.Commands;

/// Adds a surface texture to an object or node: Perlin (organic skin), Voronoi (scales / cells) or Knurl (diamond grip).
/// Amplitude = texture height (negative engraves), Scale = feature size / knurl pitch. Mask = an object or node marking where
/// the texture goes (it isn't drawn; Fade = how far outside it the texture fades out). Axis = the line a knurl wraps around
/// (default: vertical through the object's centre); the line stays live.
[Guid("62611748-6270-4a88-9e56-6b73649df158")]
public class FDisplaceCommand : FrepNodeCommand
{
    public override string EnglishName => "FDisplace";

    static readonly string[] PatternNames = [nameof(DisplacePattern.Perlin), nameof(DisplacePattern.Voronoi), nameof(DisplacePattern.Knurl)];
    // Remembered between runs; NaN = sized from the object.
    DisplacePattern lastPattern;
    double lastAmplitude = double.NaN, lastScale = double.NaN, lastFade = double.NaN;

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        // Target: one node selected in the panel, else pick (like FTwist).
        var operands = new List<FrepOperand>();
        var panel = UiController.TakeSelectedNodes(doc);
        if (panel.Count == 1 && doc.Objects.GetSelectedObjects(false, false).All(o => !IsSdf(o)))
            operands.Add(FrepOperand.Node(panel[0]));
        else
        {
            BeginPicking();
            try
            {
                var rc = Pick(doc, "Object or node to texture", false, true, _ => { }, operands);
                if (rc != Result.Success) return rc;
            }
            finally { EndPicking(); }
        }
        if (operands.Count != 1) return Result.Cancel;
        var child = operands[0];

        var defaults = FrepDisplaceMath.Defaults(FrepScene.BoundsOf(doc, child));
        var pattern = lastPattern;
        var amplitude = new OptionDouble(double.IsNaN(lastAmplitude) ? defaults[1] : lastAmplitude);
        var scale = new OptionDouble(double.IsNaN(lastScale) ? defaults[2] : lastScale, 1e-9, double.MaxValue);
        var fade = new OptionDouble(double.IsNaN(lastFade) ? defaults[3] : lastFade, 0, double.MaxValue);
        FrepOperand? mask = null;
        Guid axis = Guid.Empty;

        var go = new GetOption();
        go.AcceptNothing(true);
        while (true)
        {
            go.SetCommandPrompt($"{EnglishName} options (Enter to accept)" +
                                (mask is { } m ? $", mask {(m.IsNode ? FrepTree.NodeName(m.NodeId) : "object")}" : ""));
            go.ClearCommandOptions();
            int iPattern = go.AddOptionList("Pattern", PatternNames, (int)pattern);
            go.AddOptionDouble("Amplitude", ref amplitude);
            go.AddOptionDouble("Scale", ref scale);
            int iMask = go.AddOption("Mask");
            if (mask != null) go.AddOptionDouble("Fade", ref fade);
            int iAxis = pattern == DisplacePattern.Knurl ? go.AddOption("Axis") : -1;

            var r = go.Get();
            if (r == GetResult.Nothing) break;
            if (r != GetResult.Option) return go.CommandResult() == Result.Success ? Result.Cancel : go.CommandResult();
            int index = go.OptionIndex();
            if (index == iPattern) pattern = (DisplacePattern)go.Option().CurrentListOptionIndex;
            else if (index == iMask)
            {
                // An object or node (not the target) that marks where the texture goes.
                var picked = new List<FrepOperand>();
                BeginPicking();
                try
                {
                    if (Pick(doc, "Mask object or node (where the texture goes)", false, false, _ => { }, picked) != Result.Success) continue;
                }
                finally { EndPicking(); }
                if (picked.Count == 1 && picked[0] != child) mask = picked[0];
                else RhinoApp.WriteLine("FDisplace: the mask must be a different object or node.");
            }
            else if (index == iAxis)
            {
                var ga = new GetObject();
                ga.SetCommandPrompt("Knurl axis line");
                ga.GeometryFilter = ObjectType.Curve;
                ga.SetCustomGeometryFilter((o, g, ci) => !IsSdf(o) && FrepRefs.TryLine(g ?? o?.Geometry, out _));
                ga.EnablePreSelect(false, true);
                if (ga.Get() == GetResult.Object) axis = ga.Object(0).ObjectId;
            }
        }

        lastPattern = pattern;
        lastAmplitude = amplitude.CurrentValue;
        lastScale = scale.CurrentValue;
        lastFade = fade.CurrentValue;
        var (tree, node) = FrepTrees.Get(doc).AddDisplace(child, mask, axis,
            [(double)pattern, lastAmplitude, lastScale, lastFade]);
        FrepTrees.Set(doc, tree, EnglishName);
        doc.Objects.UnselectAll();
        RhinoApp.WriteLine($"{EnglishName}: created {node.Name} ({pattern}).");
        return Result.Success;
    }
}
