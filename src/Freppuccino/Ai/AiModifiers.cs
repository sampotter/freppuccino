using System.Text.Json.Nodes;
using Freppuccino.Csg;
using Freppuccino.Scene;
using Freppuccino.Sdf;
using Rhino;
using Rhino.Geometry;

namespace Freppuccino.Ai;

/// <summary>
/// AI tools for Shell / Gyroid / Pipe nodes and CSG blend styles (see <see cref="FrepOps"/>):
///   shell    target, thickness                          → wall just inside the target's surface (outside unchanged)
///   lattice  target, cell_size, thickness, skin?        → gyroid lattice clipped to the target (+ a solid skin)
///   pipe     points [[x,y,z]…], radius, end_radius?,    → round tube along a curve through the points; the curve is an
///            closed?, smooth?, name?, color?              AI reference object (deleted with the node)
///   combine / edit_operation: style "round" | "chamfer" | "stairs", steps (stairs count)
/// The same fields come back from get_scene, so a description can be edited and sent back (edit_operation).
/// </summary>
public static partial class AiTools
{
    static readonly string[] StyleNames = ["round", "chamfer", "stairs"];

    static BlendStyle ParseStyle(string s) => s.ToLowerInvariant() switch
    {
        "round" or "fillet" or "smooth" => BlendStyle.Round,
        "chamfer" or "bevel" => BlendStyle.Chamfer,
        "stairs" or "steps" or "terraces" => BlendStyle.Stairs,
        _ => throw new AiToolException($"unknown style '{s}'; use round, chamfer or stairs"),
    };

    /// style / steps from args, else the node's current ones (round, 4 steps for a new node).
    static (BlendStyle Style, int Steps) StyleArgs(JsonObject args, FrepNode? current)
    {
        var style = args.ContainsKey("style") ? ParseStyle(Str(args, "style")) : current?.Style() ?? BlendStyle.Round;
        int steps = args.ContainsKey("steps")
            ? (int)Math.Round(AiShapes.Number(args, "steps"))
            : current is { ParamCount: >= 2 } ? current.Steps() : 4;
        if (steps < 1 || steps > FrepOps.MaxSteps) throw new AiToolException($"steps must be 1 … {FrepOps.MaxSteps}");
        return (style, steps);
    }

    /// CSG node → its get_scene description.
    static JsonObject DescribeCsg(RhinoDoc doc, FrepNode n, Dictionary<Guid, string>? refs)
    {
        var o = new JsonObject
        {
            ["ref"] = n.Name,
            ["op"] = OpName(n.Op),
            ["k"] = AiShapes.Round(n.K),
            ["operands"] = new JsonArray([.. n.Operands.Select(x => (JsonNode)OperandRef(doc, x, refs))]),
        };
        if (n.Style() != BlendStyle.Round) o["style"] = StyleNames[(int)n.Style()];
        if (n.Style() == BlendStyle.Stairs) o["steps"] = n.Steps();
        return o;
    }

    // ───────────────────────── shell / lattice ─────────────────────────

    static JsonNode Shell(RhinoDoc doc, JsonObject args) => Modifier(doc, args, FrepOp.Shell);
    static JsonNode Lattice(RhinoDoc doc, JsonObject args) => Modifier(doc, args, FrepOp.Gyroid);

    static JsonNode Modifier(RhinoDoc doc, JsonObject args, FrepOp op)
    {
        var tree = FrepTrees.Get(doc);
        var targetRef = Str(args, "target");
        var target = Resolve(doc, tree, targetRef);
        var box = FrepScene.WarpedBoundsOf(doc, target);
        if (!box.IsValid) throw new AiToolException($"'{targetRef}' has no geometry");
        var parameters = ModifierParams(op, args, null, box);

        FrepNode? node = null;
        Mutate(doc, $"{OpName(op)} {targetRef}", () =>
        {
            (var newTree, node) = tree.AddModifier(op, target, parameters);
            FrepTrees.Set(doc, newTree, $"AI: {OpName(op)}");
        });
        var result = DescribeModifier(doc, node!, null);
        var warnings = new List<string>();
        if (tree.IsReferenced(target))
            warnings.Add($"{targetRef} already belonged to another operation, so it is now used twice. Usually you want to " +
                         "modify the top node of a part.");
        if (op == FrepOp.Shell)
            warnings.Add("A shell is hollow inside but looks the same from outside: subtract something (e.g. a box or cylinder " +
                         "through the top) from the shell node to open it, then capture a view to check the wall.");
        if (op == FrepOp.Gyroid && parameters[0] > 0 && box.Diagonal.MaximumCoordinate / parameters[0] > 40)
            warnings.Add("That is more than 40 cells across: the lattice will look like noise and slow the viewport. Use a larger cell_size.");
        if (warnings.Count > 0) result["warning"] = string.Join(" ", warnings);
        return result;
    }

    /// Spec → params, over the node's current values (if any) or defaults sized from <paramref name="box"/>.
    static double[] ModifierParams(FrepOp op, JsonObject args, FrepNode? current, BoundingBox box)
    {
        if (op == FrepOp.Shell)
        {
            double t = args.ContainsKey("thickness") ? AiShapes.Number(args, "thickness")
                : current?.Param(0) ?? throw new AiToolException("missing 'thickness'");
            if (!(t > 0)) throw new AiToolException("thickness must be > 0");
            return [t];
        }
        double[] d = current is { } c ? [c.Param(0), c.Param(1), c.Param(2)] : FrepNodeMath.GyroidDefaults(box);
        if (args.ContainsKey("cell_size")) d[0] = AiShapes.Number(args, "cell_size");
        if (args.ContainsKey("thickness")) d[1] = AiShapes.Number(args, "thickness");
        if (args.ContainsKey("skin")) d[2] = AiShapes.Number(args, "skin");
        if (!(d[0] > 0)) throw new AiToolException("cell_size must be > 0");
        if (!(d[1] > 0) || d[1] >= d[0]) throw new AiToolException("thickness must be > 0 and smaller than cell_size");
        if (d[2] < 0) throw new AiToolException("skin must be ≥ 0");
        return d;
    }

    static JsonObject DescribeModifier(RhinoDoc doc, FrepNode n, Dictionary<Guid, string>? refs)
    {
        var o = new JsonObject
        {
            ["ref"] = n.Name,
            ["op"] = OpName(n.Op),
            ["target"] = n.Operands.Length > 0 ? OperandRef(doc, n.Operands[0], refs) : null,
        };
        if (n.Op == FrepOp.Shell) o["thickness"] = AiShapes.Round(n.Param(0));
        else
        {
            o["cell_size"] = AiShapes.Round(n.Param(0));
            o["thickness"] = AiShapes.Round(n.Param(1));
            o["skin"] = AiShapes.Round(n.Param(2));
        }
        return o;
    }

    static JsonNode EditModifier(RhinoDoc doc, FrepTree tree, FrepNode node, JsonObject args)
    {
        var box = node.Operands.Length > 0 ? FrepScene.WarpedBoundsOf(doc, node.Operands[0]) : BoundingBox.Empty;
        var parameters = ModifierParams(node.Op, args, node, box);
        var t = tree;
        for (int i = 0; i < parameters.Length; i++) t = t.WithParam(node.Id, i, parameters[i]);
        Mutate(doc, $"edit {node.Name}", () => FrepTrees.Set(doc, t, $"AI: edit {node.Name}"));
        return DescribeModifier(doc, FrepTrees.Get(doc).Find(node.Id)!, null);
    }

    // ───────────────────────── pipe ─────────────────────────

    static JsonNode Pipe(RhinoDoc doc, JsonObject args)
    {
        var curve = PipeCurve(args) ?? throw new AiToolException("missing 'points' (at least 2 [x,y,z])");
        var (r0, r1) = PipeRadii(args, null);
        var name = args["name"]?.GetValue<string>() is { Length: > 0 } nm ? nm : "pipe";
        var color = AiShapes.Color(args);
        FrepNode? node = null;
        Mutate(doc, $"pipe {name}", () =>
        {
            var refId = AddRefObject(doc, curve, $"{name}_curve");
            if (color is { } c) SetCurveColor(doc, refId, c);
            (var newTree, node) = FrepTrees.Get(doc).AddPipe(refId, [r0, r1]);
            FrepTrees.Set(doc, newTree, "AI: pipe");
        });
        return DescribePipe(doc, node!);
    }

    /// points / closed / smooth → a curve, or null if no points were given.
    static Curve? PipeCurve(JsonObject s)
    {
        if (s["points"] is not JsonArray a) return null;
        var pts = a.Select(p => p is JsonArray v && v.Count == 3
            ? new Point3d(v[0]!.GetValue<double>(), v[1]!.GetValue<double>(), v[2]!.GetValue<double>())
            : throw new AiToolException("points must be [x,y,z] triples")).ToList();
        if (pts.Count < 2) throw new AiToolException("a pipe needs at least 2 points");
        bool closed = s["closed"]?.GetValue<bool>() ?? false;
        bool smooth = s["smooth"]?.GetValue<bool>() ?? true;
        if (closed && pts.Count < 3) throw new AiToolException("a closed pipe needs at least 3 points");
        if (closed && pts[0].DistanceTo(pts[^1]) < 1e-9) pts.RemoveAt(pts.Count - 1);
        Curve? curve;
        if (!smooth || pts.Count == 2)
        {
            if (closed) pts.Add(pts[0]);
            curve = new PolylineCurve(pts);
        }
        else curve = closed ? Curve.CreateInterpolatedCurve(pts, 3, CurveKnotStyle.ChordPeriodic) : Curve.CreateInterpolatedCurve(pts, 3);
        if (curve == null || !FrepRefs.TryCurve(curve, out _)) throw new AiToolException("couldn't build a curve through those points");
        return curve;
    }

    static (double R0, double R1) PipeRadii(JsonObject args, FrepNode? current)
    {
        double r0 = args.ContainsKey("radius") ? AiShapes.Number(args, "radius") : current?.Param(0) ?? throw new AiToolException("missing 'radius'");
        double r1 = args.ContainsKey("end_radius") ? AiShapes.Number(args, "end_radius")
            : current != null && !args.ContainsKey("radius") ? current.Param(1) : r0;
        if (r0 < 0 || r1 < 0 || !(r0 + r1 > 0)) throw new AiToolException("radius / end_radius must be ≥ 0, and not both 0");
        return (r0, r1);
    }

    static void SetCurveColor(RhinoDoc doc, Guid id, System.Drawing.Color color)
    {
        if (doc.Objects.FindId(id) is not { } obj) return;
        var a = obj.Attributes.Duplicate();
        SdfUserText.WriteColor(a, color);
        doc.Objects.ModifyAttributes(obj, a, true);
    }

    static JsonObject DescribePipe(RhinoDoc doc, FrepNode n)
    {
        var o = new JsonObject { ["ref"] = n.Name, ["op"] = "pipe" };
        var obj = doc.Objects.FindId(n.Ref) is { IsDeleted: false } r ? r : null;
        if (obj != null && !string.IsNullOrEmpty(obj.Attributes.Name)) o["ref_object"] = obj.Attributes.Name;
        o["radius"] = AiShapes.Round(n.Param(0));
        o["end_radius"] = AiShapes.Round(n.Param(1));
        if (obj == null || !FrepRefs.TryCurve(obj.Geometry, out var curve))
        {
            o["ref_missing"] = true;
            return o;
        }
        // The curve as the pipe samples it (exact for polylines), so it can be edited and sent back.
        var pipe = FrepNodeMath.Make(n, curve, SdfUserText.ReadColorOrDefault(obj.Attributes));
        bool closed = curve.IsClosed;
        var pts = closed ? pipe.Points[..^1] : pipe.Points;
        o["points"] = new JsonArray([.. pts.Select(p => (JsonNode)AiShapes.Json(p))]);
        o["closed"] = closed;
        o["smooth"] = !curve.TryGetPolyline(out _);
        o["length"] = AiShapes.Round(curve.GetLength());
        if (SdfUserText.TryReadColor(obj.Attributes, out var c)) o["color"] = Hex(c);
        return o;
    }

    static JsonNode EditPipe(RhinoDoc doc, FrepTree tree, FrepNode node, JsonObject args)
    {
        var (r0, r1) = PipeRadii(args, node);
        // New points (or a change of closed / smooth) rebuild the curve from the current description.
        Curve? curve = null;
        if (args.ContainsKey("points") || args.ContainsKey("closed") || args.ContainsKey("smooth"))
        {
            var spec = DescribePipe(doc, node);
            foreach (var (key, value) in args) spec[key] = value?.DeepClone();
            curve = PipeCurve(spec);
        }
        var color = AiShapes.Color(args);
        Mutate(doc, $"edit {node.Name}", () =>
        {
            var t = tree.WithParam(node.Id, 0, r0).WithParam(node.Id, 1, r1);
            var refId = node.Ref;
            if (curve != null)
            {
                if (doc.Objects.FindId(refId) is { IsDeleted: false }) doc.Objects.Replace(refId, curve);
                else
                {
                    refId = AddRefObject(doc, curve, $"{node.Name}_curve");
                    var i = t.Nodes.FindIndex(n => n.Id == node.Id);
                    t = t with { Nodes = t.Nodes.SetItem(i, t.Nodes[i] with { Ref = refId }) };
                }
            }
            if (color is { } c) SetCurveColor(doc, refId, c);
            FrepTrees.Set(doc, t, $"AI: edit {node.Name}");
        });
        return DescribePipe(doc, FrepTrees.Get(doc).Find(node.Id)!);
    }
}
