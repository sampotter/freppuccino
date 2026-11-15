using System.Runtime.InteropServices;
using Freppuccino.Csg;
using Freppuccino.Scene;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;

namespace Freppuccino.Commands;

/// Hollows an object or node: a wall of Thickness just inside its surface (the outside doesn't move).
/// The cavity is closed, so cut the shell open with FDifference to see inside.
[Guid("83b4e7ca-f11c-4c58-afa1-ee0fb764d6b3")]
public class FShellCommand : FModifierCommand
{
    public override string EnglishName => "FShell";
    protected override FrepOp Op => FrepOp.Shell;
    protected override string[] OptionNames => ["Thickness"];
    readonly double[] last = [double.NaN];
    protected override double[] Last => last;
    // A tenth of the smallest side.
    protected override double[] Defaults(BoundingBox child) =>
        [child.IsValid ? Math.Max(0.1 * Math.Min(child.Diagonal.X, Math.Min(child.Diagonal.Y, child.Diagonal.Z)), 1e-3) : 1];
}

/// Fills an object or node with a gyroid lattice: Cell = lattice period, Thickness = sheet wall, Skin = solid outer wall kept
/// around it (0 = open lattice). The lattice is fixed in world space.
[Guid("a6a04cae-ec58-499b-8ba0-197682b7c464")]
public class FGyroidCommand : FModifierCommand
{
    public override string EnglishName => "FGyroid";
    protected override FrepOp Op => FrepOp.Gyroid;
    protected override string[] OptionNames => ["Cell", "Thickness", "Skin"];
    readonly double[] last = [double.NaN, double.NaN, 0];
    protected override double[] Last => last;
    protected override double[] Defaults(BoundingBox child) => FrepNodeMath.GyroidDefaults(child);
}

/// Wraps one operand in a Shell / Gyroid node. Params from options (NaN = sized from the child).
public abstract class FModifierCommand : FrepNodeCommand
{
    protected abstract FrepOp Op { get; }
    /// Command-line option per param (<see cref="FrepOps.ParamNames"/> order).
    protected abstract string[] OptionNames { get; }
    /// Param values remembered between runs; NaN = default from the child.
    protected abstract double[] Last { get; }
    protected abstract double[] Defaults(BoundingBox child);

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var rc = PickOne(doc, $"Object or node to {EnglishName[1..].ToLowerInvariant()}", out var child);
        if (rc != Result.Success) return rc;

        var defaults = Defaults(FrepScene.BoundsOf(doc, child));
        var options = Last.Select((v, i) => new OptionDouble(double.IsNaN(v) ? defaults[i] : v, 0, double.MaxValue)).ToArray();
        var go = new GetOption();
        go.SetCommandPrompt($"{EnglishName} options (Enter to accept)");
        go.AcceptNothing(true);
        for (int i = 0; i < options.Length; i++) go.AddOptionDouble(OptionNames[i], ref options[i]);
        while (true)
        {
            var r = go.Get();
            if (r == GetResult.Option) continue;
            if (r == GetResult.Nothing) break;
            return go.CommandResult() == Result.Success ? Result.Cancel : go.CommandResult();
        }
        if (!(options[0].CurrentValue > 0))
        {
            RhinoApp.WriteLine($"{EnglishName}: {OptionNames[0]} must be > 0.");
            return Result.Failure;
        }

        for (int i = 0; i < options.Length; i++) Last[i] = options[i].CurrentValue;
        var (tree, node) = FrepTrees.Get(doc).AddModifier(Op, child, Last);
        FrepTrees.Set(doc, tree, EnglishName);
        doc.Objects.UnselectAll();
        RhinoApp.WriteLine($"{EnglishName}: created {node.Name}.");
        return Result.Success;
    }
}

/// A round pipe along each picked curve (open or closed), Radius at the start, EndRadius at the end. The curve stays live:
/// move it, edit its points or rebuild it and the pipe follows; deleting it removes the pipe. Pipes combine like any F-rep.
[Guid("181d66e8-5444-40a8-94e0-b3663bd6a495")]
public class FPipeCommand : FrepNodeCommand
{
    public override string EnglishName => "FPipe";
    double lastRadius = double.NaN, lastEnd = double.NaN; // NaN: from the curve's length / same as Radius

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var go = new GetObject();
        go.SetCommandPrompt("Curves to pipe");
        go.GeometryFilter = ObjectType.Curve;
        go.SetCustomGeometryFilter((o, g, ci) => !IsSdf(o) && FrepRefs.TryCurve(g ?? o?.Geometry, out _));
        go.EnablePreSelect(true, true);
        go.SubObjectSelect = false;
        var radius = new OptionDouble(double.IsNaN(lastRadius) ? 1 : lastRadius, 0, double.MaxValue);
        var end = new OptionDouble(double.IsNaN(lastEnd) ? radius.CurrentValue : lastEnd, 0, double.MaxValue);
        bool sized = !double.IsNaN(lastRadius);
        go.AddOptionDouble("Radius", ref radius);
        go.AddOptionDouble("EndRadius", ref end);
        while (true)
        {
            var r = go.GetMultiple(1, 0);
            if (r == GetResult.Option)
            {
                sized = true;
                continue;
            }
            if (r != GetResult.Object) return go.CommandResult();
            break;
        }

        var curves = Enumerable.Range(0, go.ObjectCount).Select(i => go.Object(i)).ToList();
        // First run without options: a radius of 5% of the shortest picked curve.
        if (!sized && curves.Select(c => c.Curve()?.GetLength() ?? 0).Where(l => l > 0).DefaultIfEmpty(20).Min() is var len)
        {
            radius = new OptionDouble(0.05 * len, 0, double.MaxValue);
            end = new OptionDouble(radius.CurrentValue, 0, double.MaxValue);
        }
        if (!(radius.CurrentValue > 0 || end.CurrentValue > 0))
        {
            RhinoApp.WriteLine("FPipe: Radius or EndRadius must be > 0.");
            return Result.Failure;
        }
        lastRadius = radius.CurrentValue;
        lastEnd = end.CurrentValue;

        var tree = FrepTrees.Get(doc);
        var names = new List<string>();
        foreach (var c in curves)
        {
            (tree, var node) = tree.AddPipe(c.ObjectId, [lastRadius, lastEnd]);
            names.Add(node.Name);
        }
        FrepTrees.Set(doc, tree, EnglishName);
        doc.Objects.UnselectAll();
        RhinoApp.WriteLine($"FPipe: created {string.Join(", ", names)}.");
        return Result.Success;
    }
}
