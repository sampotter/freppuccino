using System.Text.Json.Nodes;
using Freppuccino.Csg;
using Rhino;

namespace Freppuccino.Ai;

/// Operand edits on existing nodes: add_operand, remove_operand, move. Rules live in <see cref="FrepTree.WithOperands"/>.
/// (New CSG nodes: combine.)
public static partial class AiTools
{
    static JsonNode AddOperand(RhinoDoc doc, JsonObject args)
    {
        var tree = FrepTrees.Get(doc);
        var node = NodeArg(doc, tree, args, "node");
        var operand = Resolve(doc, tree, Str(args, "operand"));
        var mode = args["mode"] is JsonValue ? ParseMode(Str(args, "mode")) : FrepMode.Add;
        var t = Tree(() => tree.AddOperand(node.Id, operand, Index(args), mode));
        Mutate(doc, $"add to {node.Name}", () => FrepTrees.Set(doc, t, $"AI: add to {node.Name}"));

        var result = NodeSummary(doc, t, node.Id);
        if (tree.IsReferenced(operand))
            result["warning"] = $"{OperandRef(doc, operand, null)} also belongs to another operation, so it is now used twice (DAG). " +
                                "To take it out of the other one instead, use move with from.";
        return result;
    }

    static JsonNode RemoveOperand(RhinoDoc doc, JsonObject args)
    {
        var tree = FrepTrees.Get(doc);
        var node = NodeArg(doc, tree, args, "node");
        var operand = Resolve(doc, tree, Str(args, "operand"));
        var t = Tree(() => tree.RemoveOperand(node.Id, operand));
        Mutate(doc, $"remove from {node.Name}", () => FrepTrees.Set(doc, t, $"AI: remove from {node.Name}"));

        var result = NodeSummary(doc, t, node.Id);
        var notes = new List<string>();
        if (!t.IsReferenced(operand)) notes.Add($"{OperandRef(doc, operand, null)} is now a root (drawn on its own); delete it if unwanted");
        Notes(doc, tree, t, node.Id, notes);
        if (notes.Count > 0) result["note"] = string.Join("; ", notes);
        return result;
    }

    static JsonNode Move(RhinoDoc doc, JsonObject args)
    {
        var tree = FrepTrees.Get(doc);
        var item = Resolve(doc, tree, Str(args, "item"));
        var from = args["from"] is JsonValue ? NodeArg(doc, tree, args, "from") : null;
        var to = args["to"] is JsonValue ? NodeArg(doc, tree, args, "to") : null; // absent / null = root
        FrepMode? mode = args["mode"] is JsonValue ? ParseMode(Str(args, "mode")) : null;
        var t = Tree(() => tree.Move(item, from?.Id, to?.Id, Index(args), mode));
        var name = OperandRef(doc, item, null);
        Mutate(doc, $"move {name}", () => FrepTrees.Set(doc, t, $"AI: move {name}"));

        var touched = tree.Nodes.Where(n => !n.Operands.SequenceEqual(t.Find(n.Id)!.Operands) || !Rows(n).SequenceEqual(Rows(t.Find(n.Id)!))).Select(n => n.Id).ToList();
        var notes = new List<string>();
        foreach (var id in touched) Notes(doc, tree, t, id, notes);
        if (to?.Op.IsWarp() == true && to.Operands.Length == 1 && to.Operands[0] != item && !t.IsReferenced(to.Operands[0]))
            notes.Add($"{OperandRef(doc, to.Operands[0], null)} was replaced in {to.Name} and is now a root; delete it if unwanted");
        var result = new JsonObject
        {
            ["item"] = name,
            ["parents"] = new JsonArray([.. t.Nodes.Where(n => n.Operands.Contains(item)).Select(n => (JsonNode)n.Name)]),
            ["changed"] = new JsonArray([.. touched.Select(id => NodeSummary(doc, t, id))]),
        };
        if (notes.Count > 0) result["note"] = string.Join("; ", notes);
        return result;
    }

    static JsonNode SetMode(RhinoDoc doc, JsonObject args)
    {
        var tree = FrepTrees.Get(doc);
        var node = NodeArg(doc, tree, args, "node");
        var operand = Resolve(doc, tree, Str(args, "operand"));
        var mode = ParseMode(Str(args, "mode"));
        var t = Tree(() => tree.WithModes([(node.Id, operand, mode)]));
        Mutate(doc, $"mode in {node.Name}", () => FrepTrees.Set(doc, t, $"AI: mode in {node.Name}"));
        var result = NodeSummary(doc, t, node.Id);
        if (mode != FrepMode.Add && !Enumerable.Range(0, node.Operands.IndexOf(operand)).Any(i => node.ModeAt(i) == FrepMode.Add))
            result["note"] = $"nothing is added above {OperandRef(doc, operand, null)} in {node.Name}, so it has nothing to act on until an add operand comes before it";
        return result;
    }

    static IEnumerable<FrepMode> Rows(FrepNode n) => Enumerable.Range(0, n.Operands.Length).Select(n.ModeAt);

    // ───────────────────────── helpers ─────────────────────────

    static FrepNode NodeArg(RhinoDoc doc, FrepTree tree, JsonObject args, string key)
    {
        var o = Resolve(doc, tree, Str(args, key));
        return o.IsNode ? tree.Find(o.NodeId)! : throw new AiToolException($"'{key}' must be an operation node (N…), not a primitive");
    }

    static int? Index(JsonObject args)
    {
        if (args["index"] is not JsonValue) return null;
        var i = AiShapes.Number(args, "index");
        return i == Math.Floor(i) ? (int)i : throw new AiToolException("'index' must be an integer");
    }

    static FrepTree Tree(Func<FrepTree> edit)
    {
        try { return edit(); }
        catch (ArgumentException e) { throw new AiToolException(e.Message); }
    }

    static JsonObject NodeSummary(RhinoDoc doc, FrepTree tree, int id)
    {
        var n = tree.Find(id)!;
        var refs = RefNames(SdfObjects(doc).ToList());
        return new JsonObject
        {
            ["ref"] = n.Name,
            ["op"] = OpName(n.Op),
            ["operands"] = new JsonArray([.. n.Operands.Select(o => (JsonNode)OperandRef(doc, o, refs))]),
            ["modes"] = n.IsGroup ? ModeNames(n) : null,
        };
    }

    /// Semantics changes worth telling the model about after an operand edit of node id.
    static void Notes(RhinoDoc doc, FrepTree before, FrepTree after, int id, List<string> notes)
    {
        var (b, a) = (before.Find(id)!, after.Find(id)!);
        if (a.Op.IsWarp()) return;
        if (a.Operands.Length == 1 && b.Operands.Length > 1)
            notes.Add(a.ModeAt(0) == FrepMode.Add
                ? $"{a.Name} has one operand left, so it just passes it through"
                : $"{a.Name} has one operand left and it is {ModeName(a.ModeAt(0))}, so the group draws nothing");
        if (a.IsGroup && b.Operands.Length > 0 && a.Operands.Length > 0 && a.Operands[0] != b.Operands[0])
            notes.Add($"{a.Name}'s first operand is now {OperandRef(doc, a.Operands[0], null)} ({ModeName(a.ModeAt(0))}); every operand applies its mode to the result of the ones above it");
    }
}
