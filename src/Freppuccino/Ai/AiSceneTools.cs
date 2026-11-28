using System.Text.Json.Nodes;
using Freppuccino.Commands;
using Freppuccino.Csg;
using Freppuccino.Display;
using Freppuccino.Scene;
using Freppuccino.Ui;
using Rhino;

namespace Freppuccino.Ai;

public static partial class AiTools
{
    static JsonNode Wrap(RhinoDoc doc, JsonObject args)
    {
        var opName = Str(args, "op").ToLowerInvariant();
        var tree = FrepTrees.Get(doc);
        var target = Resolve(doc, tree, Str(args, "target"));
        var k = args.ContainsKey("k") ? AiShapes.Number(args, "k") : 0;
        if (!double.IsFinite(k)) throw new AiToolException("'k' must be a finite number");
        var child = target.IsNode ? tree.Find(target.NodeId) : null;
        FrepOp op;
        switch (opName)
        {
            case "offset":
                if (child is not { IsGroup: true })
                    throw new AiToolException("offset wraps a group node (N#). To grow or shrink a single primitive, " +
                                              "change its size with update_primitive, or combine it first");
                op = FrepOp.Offset;
                break;
            case "blend":
                if (child is not { Op: FrepOp.Repeat })
                    throw new AiToolException("blend wraps a repeat node (made with deform op=repeat); k fillets the copies into each other");
                op = FrepOp.Blend;
                k = Math.Max(0, k);
                break;
            default:
                throw new AiToolException($"unknown op '{opName}'; use offset or blend");
        }
        if (tree.Nodes.FirstOrDefault(n => n.Op == op && n.Operands.Contains(target)) is { } existing)
            throw new AiToolException($"{FrepTree.NodeName(child.Id)} already has a {opName} node, {existing.Name}; change it with edit_operation {{ref: \"{existing.Name}\", k}}");

        var (next, node) = tree.WrapOver(op, k, child.Id);
        Mutate(doc, $"{opName} {node.Name}", () => FrepTrees.Set(doc, next, UndoPrefix + opName));
        return new JsonObject { ["ref"] = node.Name, ["op"] = opName, ["k"] = AiShapes.Round(k), ["target"] = FrepTree.NodeName(child.Id) };
    }

    static JsonNode EditWrap(RhinoDoc doc, FrepTree tree, FrepNode node, JsonObject args)
    {
        var name = OpName(node.Op);
        if (args.Select(kv => kv.Key).Where(key => key is not ("ref" or "op" or "k")).ToList() is { Count: > 0 } extra)
            throw new AiToolException($"{node.Name} is a {name} node: it only takes k (got {string.Join(", ", extra)})");
        if (args["op"] is JsonValue v && v.TryGetValue<string>(out var requested) && !requested.Equals(name, StringComparison.OrdinalIgnoreCase))
            throw new AiToolException($"{node.Name} is a {name} node and can't become a {requested}; unwrap it to remove it");
        var k = args.ContainsKey("k") ? AiShapes.Number(args, "k") : node.K;
        if (!double.IsFinite(k)) throw new AiToolException("'k' must be a finite number");
        if (node.Op == FrepOp.Blend) k = Math.Max(0, k);
        Mutate(doc, $"edit {node.Name}", () => FrepTrees.Set(doc, tree.WithK(node.Id, k), $"{UndoPrefix}edit {node.Name}"));
        return new JsonObject { ["ref"] = node.Name, ["op"] = name, ["k"] = AiShapes.Round(k) };
    }

    static JsonNode Unwrap(RhinoDoc doc, JsonObject args)
    {
        var tree = FrepTrees.Get(doc);
        var nodes = new HashSet<int>();
        foreach (var r in Refs(args, "refs"))
        {
            var o = Resolve(doc, tree, r);
            if (!o.IsNode) throw new AiToolException($"'{r}' is a primitive; unwrap takes operation nodes (N#). Use delete for primitives");
            nodes.Add(o.NodeId);
        }
        var removed = tree.Nodes.Where(n => nodes.Contains(n.Id)).ToList();
        Mutate(doc, "unwrap", () =>
        {
            var next = tree.Excise(nodes, new HashSet<Guid>());
            FrepTrees.Set(doc, next, UndoPrefix + "unwrap");
            DeleteOrphanRefs(doc, next, removed.Select(n => n.Ref));
        });
        return new JsonObject { ["unwrapped"] = new JsonArray([.. removed.Select(n => (JsonNode)n.Name)]) };
    }

    static JsonArray SelectionRefs(RhinoDoc doc, Dictionary<Guid, string> refs) =>
        [.. UiController.SelectedIds(doc).Select(k => (JsonNode)(Guid.TryParse(k, out var g) ? refs.GetValueOrDefault(g) ?? k : k))];

    static JsonNode SelectRefs(RhinoDoc doc, JsonObject args)
    {
        var tree = FrepTrees.Get(doc);
        var mode = args["mode"] is JsonValue m && m.TryGetValue<string>(out var s) ? s.ToLowerInvariant() : "replace";
        if (mode is not ("replace" or "add" or "toggle")) throw new AiToolException($"unknown mode '{mode}'; use replace, add or toggle");
        string[] ids = args["refs"] is JsonArray { Count: > 0 } || args["refs"] is JsonValue
            ? [.. Refs(args, "refs").Select(r => Resolve(doc, tree, r)).Select(o => o.IsNode ? FrepTree.NodeName(o.NodeId) : o.ObjectId.ToString("D"))]
            : [];
        UiController.Select(doc, new SelectRequest(ids, mode));
        return new JsonObject { ["selection"] = SelectionRefs(doc, RefNames(SdfObjects(doc).ToList())) };
    }

    static JsonNode MeshRoots(RhinoDoc doc, JsonObject args)
    {
        var scene = FrepScene.Get(doc);
        if (scene.Count == 0) throw new AiToolException("there are no F-rep objects to mesh");
        var tree = FrepTrees.Get(doc);
        List<FrepOperand>? only = null;
        if (args["refs"] is JsonArray { Count: > 0 } || args["refs"] is JsonValue)
        {
            only = [.. Refs(args, "refs").Select(r => Resolve(doc, tree, r))];
            if (only.Where(tree.IsReferenced).Select(o => OperandRef(doc, o, null)).ToList() is { Count: > 0 } inner)
                throw new AiToolException($"{string.Join(", ", inner)} is inside another operation; mesh its top node (one of get_scene's roots)");
        }
        var resolution = args.ContainsKey("resolution")
            ? (int)Math.Clamp(Math.Round(AiShapes.Number(args, "resolution")), FrepMeshCommand.MinResolution, FrepMeshCommand.MaxResolution)
            : 128;
        var simplify = args.ContainsKey("simplify") ? Math.Clamp(AiShapes.Number(args, "simplify"), 0, 1) : 0.03;
        var refine = args.ContainsKey("refine") ? (int)Math.Clamp(Math.Round(AiShapes.Number(args, "refine")), 0, 3) : 0;
        var quads = args["quads"] is JsonValue q && q.TryGetValue<bool>(out var b) && b;
        var name = args["name"] is JsonValue n && n.TryGetValue<string>(out var given) && given.Length > 0 ? given : "FrepMesh";

        var field = FrepFieldBuilder.Build(scene, only, out var built);
        if (!field.HasRoots) throw new AiToolException("nothing to mesh: the roots are hidden or cancelled out by the CSG tree");
        FrepMeshCommand.BakeResult? baked = null;
        var error = "";
        Mutate(doc, $"mesh {name}", () => baked = FrepMeshCommand.Bake(doc, field, resolution, simplify, refine, quads, name, out error));
        if (baked == null) throw new AiToolException(error);
        return new JsonObject
        {
            ["mesh"] = baked.Id.ToString("D"),
            ["name"] = name,
            ["roots"] = new JsonArray([.. built.Select(o => (JsonNode)OperandRef(doc, o, null))]),
            ["faces"] = baked.FaceCount,
            ["summary"] = baked.Faces,
            ["cell_size"] = AiShapes.Round(baked.CellSize),
            ["closed"] = baked.Closed,
            ["manifold"] = baked.Manifold,
            ["self_intersections"] = baked.Crossings,
            ["quality"] = baked.Quality,
            ["ms"] = baked.Milliseconds,
            ["note"] = "A plain Rhino mesh next to the F-reps (which stay). capture_views shows only F-reps, so it is not in screenshots.",
        };
    }

    static JsonNode SetZebra(RhinoDoc doc, JsonObject args)
    {
        var on = args["on"] is JsonValue v && v.TryGetValue<bool>(out var flag) ? flag : throw new AiToolException("missing 'on' (true/false)");
        if (args.ContainsKey("stripes")) FrepSettings.ZebraStripes = (int)Math.Clamp(Math.Round(AiShapes.Number(args, "stripes")), 2, 60);
        if (args["vertical"] is JsonValue d && d.TryGetValue<bool>(out var vertical)) FrepSettings.ZebraVertical = vertical;
        if (on) ZebraOptionsForm.ShowOrFocus(doc);
        else ZebraOptionsForm.CloseIfOpen();
        doc.Views.Redraw();
        return new JsonObject
        {
            ["zebra"] = FrepSettings.Zebra,
            ["stripes"] = FrepSettings.ZebraStripes,
            ["vertical"] = FrepSettings.ZebraVertical,
            ["note"] = "Stripes draw on union surfaces only; kinks in them show where a blend isn't smooth. They show in capture_views.",
        };
    }
}
