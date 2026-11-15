using System.Runtime.InteropServices;
using Freppuccino.Csg;
using Freppuccino.Sdf;
using Freppuccino.Ui;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;

namespace Freppuccino.Commands;

[Guid("3f6b1c2e-8a47-4d19-b5e3-7c0a9d2f4e61")]
public class FUnionCommand : FCsgCommand
{
    public override string EnglishName => "FUnion";
    protected override FrepOp Op => FrepOp.Union;
}

[Guid("a2d84e7b-1c35-4f9a-8e60-5b7d3c1a9f82")]
public class FIntersectCommand : FCsgCommand
{
    public override string EnglishName => "FIntersect";
    protected override FrepOp Op => FrepOp.Intersect;
}

/// Base − (cutters...), each cutter smooth-subtracted in turn with the same k.
[Guid("c7e95a1d-4b28-4e3f-9d71-6a8b2e5c0f13")]
public class FDifferenceCommand : FCsgCommand
{
    public override string EnglishName => "FDifference";
    protected override FrepOp Op => FrepOp.Difference;
}

/// Creates a CSG node. Operands: SDF objects (pick) or existing nodes (type "N3", or click it in the Freppuccino panel).
/// Union/Intersect also take preselection: selected objects + nodes selected in the panel.
public abstract class FCsgCommand : FrepNodeCommand
{
    protected abstract FrepOp Op { get; }
    double lastK; // per command, remembered between runs
    BlendStyle lastStyle;
    int lastSteps = 4;

    static readonly string[] StyleNames = [nameof(BlendStyle.Round), nameof(BlendStyle.Chamfer), nameof(BlendStyle.Stairs)];

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        var k = new OptionDouble(lastK, 0, double.MaxValue);
        var steps = new OptionInteger(lastSteps, 1, FrepOps.MaxSteps);
        var style = lastStyle;
        // Blend = size, Style = round fillet / 45° chamfer / stairs, Steps = stair count.
        void Options(GetObject go)
        {
            go.AddOptionDouble("Blend", ref k);
            go.AddOptionList("Style", StyleNames, (int)style);
            if (style == BlendStyle.Stairs) go.AddOptionInteger("Steps", ref steps);
        }
        bool OnOption(GetObject go, CommandLineOption o)
        {
            if (o.EnglishName != "Style") return false;
            style = (BlendStyle)o.CurrentListOptionIndex;
            go.ClearCommandOptions();
            Options(go);
            return true;
        }
        var operands = new List<FrepOperand>();
        Result rc;
        BeginPicking();
        try
        {
            if (Op == FrepOp.Difference)
            {
                rc = Pick(doc, "Base object or node", false, false, Options, operands, OnOption);
                if (rc == Result.Success)
                    rc = Pick(doc, "Objects or nodes to subtract", true, false, Options, operands, OnOption);
            }
            else
            {
                operands.AddRange(UiController.TakeSelectedNodes(doc).Select(FrepOperand.Node));
                rc = operands.Count >= 2 && doc.Objects.GetSelectedObjects(false, false).All(o => !IsSdf(o))
                    ? Result.Success
                    : Pick(doc, "Objects or nodes to " + (Op == FrepOp.Union ? "union" : "intersect"), true, true,
                        Options, operands, OnOption);
            }
        }
        finally { EndPicking(); }
        if (rc != Result.Success) return rc;

        if (operands.Count < 2)
        {
            RhinoApp.WriteLine($"{EnglishName}: need at least 2 operands, got {operands.Count}.");
            return Result.Failure;
        }

        lastK = k.CurrentValue;
        lastStyle = style;
        lastSteps = steps.CurrentValue;
        var (tree, node) = FrepTrees.Get(doc).Add(Op, lastK, operands, lastStyle, lastSteps);
        FrepTrees.Set(doc, tree, EnglishName);
        doc.Objects.UnselectAll();
        RhinoApp.WriteLine($"{EnglishName}: created {node.Name}.");
        return Result.Success;
    }
}

/// Shared operand picking for commands that create tree nodes.
public abstract class FrepNodeCommand : Command
{
    /// True while one of these commands waits for operands; the panel only sends row clicks then.
    public static bool Picking { get; private set; }
    protected static void BeginPicking() => Picking = true;
    protected static void EndPicking() => Picking = false;

    protected static bool IsSdf(RhinoObject? o) => o != null && SdfUserText.ReadType(o.Attributes) != null;

    /// One operand: the single node selected in the panel (when no SDF object is selected in Rhino), else a picked object
    /// or typed / clicked node name.
    protected static Result PickOne(RhinoDoc doc, string prompt, out FrepOperand operand)
    {
        operand = default;
        var panel = UiController.TakeSelectedNodes(doc);
        if (panel.Count == 1 && doc.Objects.GetSelectedObjects(false, false).All(o => !IsSdf(o)))
        {
            operand = FrepOperand.Node(panel[0]);
            return Result.Success;
        }
        var operands = new List<FrepOperand>();
        BeginPicking();
        try
        {
            var rc = Pick(doc, prompt, false, true, _ => { }, operands);
            if (rc != Result.Success) return rc;
        }
        finally { EndPicking(); }
        if (operands.Count != 1) return Result.Cancel;
        operand = operands[0];
        return Result.Success;
    }

    /// Appends picks to <paramref name="into"/> (deduped). Node names may be typed/clicked repeatedly; picking objects or Enter ends.
    /// <paramref name="onOption"/>: handles a clicked option the OptionDouble/OptionInteger refs don't (e.g. a list); true = handled.
    protected static Result Pick(RhinoDoc doc, string prompt, bool many, bool preselect, Action<GetObject> options, List<FrepOperand> into,
        Func<GetObject, CommandLineOption, bool>? onOption = null)
    {
        var tree = FrepTrees.Get(doc);
        var go = new GetObject();
        go.SetCommandPrompt(prompt + " (type or click a node name)");
        go.SetCustomGeometryFilter((o, _, _) => IsSdf(o));
        go.AcceptString(true);
        go.AcceptNothing(many);
        go.EnablePreSelect(preselect, true);
        go.EnableClearObjectsOnEntry(false);
        go.EnableUnselectObjectsOnExit(false);
        go.DeselectAllBeforePostSelect = false;
        options(go);

        void Add(FrepOperand o)
        {
            if (into.Contains(o)) RhinoApp.WriteLine("Already added.");
            else into.Add(o);
        }

        while (true)
        {
            switch (many ? go.GetMultiple(1, 0) : go.Get())
            {
                case GetResult.Option:
                    if (go.Option() is { } option) onOption?.Invoke(go, option);
                    continue;
                case GetResult.String:
                    var text = go.StringResult();
                    if (!FrepTree.TryParseName(text, out var id) || tree.Find(id) == null)
                    {
                        RhinoApp.WriteLine($"No node \"{text}\".");
                        continue;
                    }
                    Add(FrepOperand.Node(id));
                    RhinoApp.WriteLine($"Added {FrepTree.NodeName(id)}.");
                    if (!many) return Result.Success;
                    continue;
                case GetResult.Object:
                    for (int i = 0; i < go.ObjectCount; i++) Add(FrepOperand.Object(go.Object(i).ObjectId));
                    return Result.Success;
                case GetResult.Nothing:
                    return Result.Success;
                default:
                    return go.CommandResult();
            }
        }
    }
}
