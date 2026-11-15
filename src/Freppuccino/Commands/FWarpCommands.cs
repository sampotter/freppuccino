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

[Guid("5d2a7c91-3e64-4b8f-a1d9-0c6e2f8b4a73")]
public class FTwistCommand : FWarpCommand
{
    public override string EnglishName => "FTwist";
    protected override FrepOp Op => FrepOp.Twist;
    readonly double[] last = [90];
    protected override double[] Last => last;
    protected override void AddOptions(GetObject go, OptionDouble[] o) => go.AddOptionDouble("Angle", ref o[0]);
}

[Guid("b8e14f62-7a39-4c05-9e2d-d3f6a1c87b50")]
public class FBendCommand : FWarpCommand
{
    public override string EnglishName => "FBend";
    protected override FrepOp Op => FrepOp.Bend;
    readonly double[] last = [90, 0];
    protected override double[] Last => last;
    protected override void AddOptions(GetObject go, OptionDouble[] o)
    {
        go.AddOptionDouble("Angle", ref o[0]);
        go.AddOptionDouble("Direction", ref o[1]);
    }
}

/// Copies of the child at child + (i·x, j·y, k·z) along the box axes, for every lattice point inside the box.
[Guid("e37b9d05-6c1a-4f48-b2e7-9a4d0c5f1e86")]
public class FRepeatCommand : FWarpCommand
{
    public override string EnglishName => "FRepeat";
    protected override FrepOp Op => FrepOp.Repeat;
    readonly double[] last = [double.NaN, double.NaN, double.NaN]; // NaN: default from the child's size
    protected override double[] Last => last;
    protected override void AddOptions(GetObject go, OptionDouble[] o)
    {
        go.AddOptionDouble("SpacingX", ref o[0]);
        go.AddOptionDouble("SpacingY", ref o[1]);
        go.AddOptionDouble("SpacingZ", ref o[2]);
    }
}

/// Wraps one operand (SDF object or node) in a warp node driven by a picked line (Twist/Bend) or box (Repeat).
/// The reference object stays live: moving it updates the warp; deleting it leaves the child unwarped.
public abstract class FWarpCommand : FrepNodeCommand
{
    protected abstract FrepOp Op { get; }
    /// Param values remembered between runs, per <see cref="FrepOps.ParamNames"/>.
    protected abstract double[] Last { get; }
    protected abstract void AddOptions(GetObject go, OptionDouble[] options);

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var verb = EnglishName[1..].ToLowerInvariant();
        var rc = PickOne(doc, $"Object or node to {verb}", out var child);
        if (rc != Result.Success) return rc;

        var size = FrepScene.BoundsOf(doc, child);
        double auto = size.IsValid ? 1.25 * Math.Max(size.Diagonal.MaximumCoordinate, doc.ModelAbsoluteTolerance) : 1;
        var options = Last.Select(v => new OptionDouble(double.IsNaN(v) ? auto : v)).ToArray();

        var go = new GetObject();
        go.SetCommandPrompt(Op == FrepOp.Repeat ? "Box bounding the copies" : "Axis line");
        go.GeometryFilter = Op == FrepOp.Repeat ? ObjectType.Brep | ObjectType.Extrusion : ObjectType.Curve;
        go.SetCustomGeometryFilter((o, g, _) => !IsSdf(o) && FrepRefs.Fits(Op, g ?? o?.Geometry));
        go.EnablePreSelect(false, true);
        AddOptions(go, options);
        while (true)
        {
            var r = go.Get();
            if (r == GetResult.Option) continue;
            if (r != GetResult.Object) return go.CommandResult();
            break;
        }

        for (int i = 0; i < options.Length; i++) Last[i] = options[i].CurrentValue;
        var (tree, node) = FrepTrees.Get(doc).AddWarp(Op, child, go.Object(0).ObjectId, Last);
        FrepTrees.Set(doc, tree, EnglishName);
        doc.Objects.UnselectAll();
        RhinoApp.WriteLine($"{EnglishName}: created {node.Name}.");
        return Result.Success;
    }
}
