using System.Text.Json.Nodes;
using Freppuccino.Csg;
using Freppuccino.Scene;
using Rhino;
using Rhino.Geometry;

namespace Freppuccino.Ai;

/// <summary>
/// AI tool for Displace nodes (surface textures, see <see cref="FrepOps"/>):
///   texture  target, pattern (perlin | voronoi | knurl), amplitude, scale, mask?, fade?, axis_start? / axis_end? (knurl)
/// get_scene describes them with the same fields; edit_operation takes them too (mask "" removes it).
/// A knurl axis given as points becomes an AI reference line (deleted with the node, like deform axes).
/// </summary>
public static partial class AiTools
{
    static readonly string[] PatternNames = ["perlin", "voronoi", "knurl"];

    static DisplacePattern ParsePattern(string s) => s.ToLowerInvariant() switch
    {
        "perlin" or "noise" or "organic" => DisplacePattern.Perlin,
        "voronoi" or "cells" or "scales" or "cellular" => DisplacePattern.Voronoi,
        "knurl" or "knurled" or "grip" or "diamond" => DisplacePattern.Knurl,
        _ => throw new AiToolException($"unknown pattern '{s}'; use perlin, voronoi or knurl"),
    };

    static JsonNode Texture(RhinoDoc doc, JsonObject args)
    {
        var tree = FrepTrees.Get(doc);
        var targetRef = Str(args, "target");
        var target = Resolve(doc, tree, targetRef);
        var box = FrepScene.WarpedBoundsOf(doc, target);
        if (!box.IsValid) throw new AiToolException($"'{targetRef}' has no geometry to texture");
        var parameters = DisplaceParams(args, null, box);
        FrepOperand? mask = args["mask"] is JsonValue mv && mv.TryGetValue<string>(out var ms) && ms.Length > 0 ? Resolve(doc, tree, ms) : null;
        if (mask == target) throw new AiToolException("the mask must be a different object or node than the target");
        var axisLine = AxisArg(args);

        FrepNode? node = null;
        Mutate(doc, $"texture {targetRef}", () =>
        {
            var axis = axisLine is { } l ? AddRefObject(doc, new LineCurve(l), $"{targetRef}_knurl_axis") : Guid.Empty;
            (var newTree, node) = FrepTrees.Get(doc).AddDisplace(target, mask, axis, parameters);
            FrepTrees.Set(doc, newTree, "AI: texture");
        });

        var result = DescribeDisplace(doc, FrepTrees.Get(doc).Find(node!.Id)!, null);
        var warnings = new List<string>();
        if (tree.IsReferenced(target))
            warnings.Add($"{targetRef} already belonged to another operation, so it is now used twice. Usually you texture the top node of a part.");
        if (mask is { } m && tree.IsReferenced(m))
            warnings.Add("The mask is also used elsewhere: it still isn't drawn here, only where it is drawn already.");
        if (TextureWarning(parameters, box) is { } w) warnings.Add(w);
        if (warnings.Count > 0) result["warning"] = string.Join(" ", warnings);
        return result;
    }

    /// Spec → [pattern, amplitude, scale, fade], over the node's current values or defaults sized from <paramref name="box"/>.
    static double[] DisplaceParams(JsonObject args, FrepNode? current, BoundingBox box)
    {
        double[] p = current is { } c ? [c.Param(0), c.Param(1), c.Param(2), c.Param(3)] : FrepDisplaceMath.Defaults(box);
        if (args.ContainsKey("pattern")) p[0] = (int)ParsePattern(Str(args, "pattern"));
        if (args.ContainsKey("amplitude")) p[1] = AiShapes.Number(args, "amplitude");
        if (args.ContainsKey("scale")) p[2] = AiShapes.Number(args, "scale");
        if (args.ContainsKey("fade")) p[3] = AiShapes.Number(args, "fade");
        if (!(p[2] > 0)) throw new AiToolException("scale must be > 0");
        if (p[3] < 0) throw new AiToolException("fade must be ≥ 0");
        return p;
    }

    /// A warning when the texture will look wrong or render slowly; null when fine.
    static string? TextureWarning(double[] p, BoundingBox box)
    {
        double size = box.Diagonal.MaximumCoordinate, a = Math.Abs(p[1]), scale = p[2];
        if (a > 0.5 * scale)
            return $"amplitude {a:0.###} is more than half the scale {scale:0.###}: the texture will look spiky and the viewport slows " +
                   "down (the field is divided by 1 + amplitude × steepness). Keep amplitude ≤ about a third of scale.";
        if (size / scale > 80) return "That scale is very small for this part: the texture will read as noise. Use a larger scale.";
        if (a < 0.002 * size) return "That amplitude is tiny for this part and may not be visible. Try ~1-3% of the part's size.";
        return null;
    }

    static Line? AxisArg(JsonObject s)
    {
        if (!s.ContainsKey("axis_start") && !s.ContainsKey("axis_end")) return null;
        var a = new Point3d(AiShapes.Vec(s, "axis_start"));
        var b = new Point3d(AiShapes.Vec(s, "axis_end"));
        if (a.DistanceTo(b) < 1e-9) throw new AiToolException("axis_start and axis_end must be different points");
        return new Line(a, b);
    }

    static JsonObject DescribeDisplace(RhinoDoc doc, FrepNode n, Dictionary<Guid, string>? refs)
    {
        var o = new JsonObject
        {
            ["ref"] = n.Name,
            ["op"] = "texture",
            ["target"] = n.Operands.Length > 0 ? OperandRef(doc, n.Operands[0], refs) : null,
            ["pattern"] = PatternNames[(int)n.Pattern()],
            ["amplitude"] = AiShapes.Round(n.Param(1)),
            ["scale"] = AiShapes.Round(n.Param(2)),
        };
        if (n.Mask is { } m)
        {
            o["mask"] = OperandRef(doc, m, refs);
            o["fade"] = AiShapes.Round(n.Param(3));
        }
        if (n.Pattern() == DisplacePattern.Knurl && doc.Objects.FindId(n.Ref) is { IsDeleted: false } r && FrepRefs.TryLine(r.Geometry, out var line))
        {
            o["axis_start"] = AiShapes.Json(line.From);
            o["axis_end"] = AiShapes.Json(line.To);
        }
        return o;
    }

    static JsonNode EditDisplace(RhinoDoc doc, FrepTree tree, FrepNode node, JsonObject args)
    {
        if (args.ContainsKey("op") && Str(args, "op").ToLowerInvariant() is not ("texture" or "displace"))
            throw new AiToolException($"{node.Name} is a texture; it can't change type (delete it and create a new one)");
        var box = FrepScene.WarpedBoundsOf(doc, node.Operands[0]);
        var parameters = DisplaceParams(args, node, box);
        // mask: a ref replaces it, "" removes it, absent keeps it.
        var operands = node.Operands;
        if (args["mask"] is JsonValue mv && mv.TryGetValue<string>(out var ms))
            operands = ms.Length == 0 ? [operands[0]] : [operands[0], Resolve(doc, tree, ms)];
        var axisLine = AxisArg(args);

        Mutate(doc, $"edit {node.Name}", () =>
        {
            var refId = node.Ref;
            if (axisLine is { } l)
            {
                if (doc.Objects.FindId(refId) is { IsDeleted: false }) doc.Objects.Replace(refId, new LineCurve(l));
                else refId = AddRefObject(doc, new LineCurve(l), $"{node.Name}_knurl_axis");
            }
            var t = FrepTrees.Get(doc);
            var i = t.Nodes.FindIndex(n => n.Id == node.Id);
            t = t with { Nodes = t.Nodes.SetItem(i, t.Nodes[i] with { Operands = operands, Ref = refId, Params = [.. parameters] }) };
            FrepTrees.Set(doc, t, $"AI: edit {node.Name}");
        });
        var result = DescribeDisplace(doc, FrepTrees.Get(doc).Find(node.Id)!, null);
        if (TextureWarning(parameters, box) is { } w) result["warning"] = w;
        return result;
    }
}
