using System.Collections.Immutable;
using Freppuccino.Scene;
using Rhino;
using Rhino.Commands;
using Rhino.FileIO;

namespace Freppuccino.Csg;

/// Union/Intersect/Difference: CSG over ≥ 1 operand (created with ≥ 2; operand edits may leave 1 = pass-through), blend K.
/// Blend: one child (a Repeat); K fillets its copies.
/// Offset: one child, a union, intersection, or difference; K moves its surface out (positive) or in.
/// Twist/Bend/Repeat: domain warps of one operand, shaped by a referenced Rhino object
/// (<see cref="FrepNode.Ref"/>: line for Twist/Bend, box for Repeat) + <see cref="FrepNode.Params"/>.
public enum FrepOp { Union = 0, Intersect = 1, Difference = 2, Twist = 3, Bend = 4, Repeat = 5, Blend = 6, Offset = 7, Group = 8 }

public enum FrepMode { Add = 0, Subtract = 1, Intersect = 2 }

public static class FrepOps
{
    public static bool IsWarp(this FrepOp op) => op is FrepOp.Twist or FrepOp.Bend or FrepOp.Repeat;

    public static bool IsPreset(this FrepOp op) => op is FrepOp.Union or FrepOp.Intersect or FrepOp.Difference;

    public static FrepMode PresetMode(FrepOp op, int row) => op switch
    {
        FrepOp.Intersect => FrepMode.Intersect,
        FrepOp.Difference when row > 0 => FrepMode.Subtract,
        _ => FrepMode.Add,
    };

    public static ImmutableArray<FrepMode> PresetModes(FrepOp op, int count) => [.. Enumerable.Range(0, count).Select(i => PresetMode(op, i))];

    public static FrepOp? PresetOf(ImmutableArray<FrepMode> modes, int count)
    {
        foreach (var op in new[] { FrepOp.Union, FrepOp.Difference, FrepOp.Intersect })
            if (Enumerable.Range(1, Math.Max(0, count - 1)).All(i => Mode(modes, i) == PresetMode(op, i))) return op;
        return null;
    }

    static FrepMode Mode(ImmutableArray<FrepMode> modes, int i) => !modes.IsDefault && i < modes.Length ? modes[i] : FrepMode.Add;

    /// Param names per warp op (index = slot in FrepNode.Params). Angles in degrees.
    ///   Twist  angle: total twist from line start to end (linear, continues past the ends)
    ///   Bend   angle: total bend over the line's length (straight past the ends); direction: bend direction around the line
    ///   Repeat x/y/z: spacing along the box axes (≤ 0 = no repetition on that axis)
    public static IReadOnlyList<string> ParamNames(this FrepOp op) => op switch
    {
        FrepOp.Twist => ["angle"],
        FrepOp.Bend => ["angle", "direction"],
        FrepOp.Repeat => ["x", "y", "z"],
        _ => [],
    };
}

/// Leaf (Rhino object id of an SDF primitive) or a reference to another node.
public readonly record struct FrepOperand(int NodeId, Guid ObjectId)
{
    public bool IsNode => ObjectId == Guid.Empty;
    public static FrepOperand Node(int id) => new(id, Guid.Empty);
    public static FrepOperand Object(Guid id) => new(0, id);
}

/// Difference: Operands[0] − Operands[1..]. K = smooth blend distance (model units, 0 = hard).
/// Hidden: dropped like a hidden primitive (a missing leaf for its parents; not drawn as a root).
/// Warps: Operands = [child], Ref = line/box object, Params per <see cref="FrepOps.ParamNames"/>.
/// Blend uses K as the fillet between a Repeat's copies (0 = hard). Offset uses K as the surface shift
/// (positive grows). Warps ignore K.
/// Matrix: optional affine transform M of the node's whole result (12 numbers, top three rows of the 4x4, row-major;
/// default = identity): field = result(M⁻¹ p) · s(M), s = smallest singular value. A root's gumball transform composes
/// into it ("transform the blend as one object"); it stays with the node if the node later becomes an operand.
public sealed record FrepNode(int Id, FrepOp Op, double K, ImmutableArray<FrepOperand> Operands, bool Hidden = false,
    Guid Ref = default, ImmutableArray<double> Params = default, ImmutableArray<double> Matrix = default,
    ImmutableArray<FrepMode> Modes = default)
{
    public string Name => FrepTree.NodeName(Id);

    public bool IsGroup => Op == FrepOp.Group;

    public FrepMode ModeAt(int i) => IsGroup && !Modes.IsDefault && i < Modes.Length ? Modes[i] : FrepMode.Add;

    public FrepMode? ModeOf(FrepOperand o) => Operands.IndexOf(o) is var i and >= 0 ? ModeAt(i) : null;

    public FrepNode WithRows(ImmutableArray<FrepOperand> operands, ImmutableArray<FrepMode> modes) =>
        this with { Operands = operands, Modes = IsGroup ? modes : default };

    public FrepNode Rekeyed(ImmutableArray<FrepOperand> operands, FrepOperand? moved = null, FrepMode movedMode = FrepMode.Add) =>
        WithRows(operands, [.. operands.Select(o => o == moved ? movedMode : ModeOf(o) ?? FrepMode.Add)]);

    public FrepNode WithoutRowAt(int i) => WithRows(Operands.RemoveAt(i), IsGroup ? [.. Enumerable.Range(0, Operands.Length).Where(j => j != i).Select(ModeAt)] : default);
    public double Param(int i) => !Params.IsDefault && i < Params.Length ? Params[i] : 0;

    public bool HasMatrix => !Matrix.IsDefault && Matrix.Length == 12;

    /// M (identity if none).
    public Rhino.Geometry.Transform M
    {
        get
        {
            var t = Rhino.Geometry.Transform.Identity;
            if (!HasMatrix) return t;
            for (int i = 0; i < 12; i++) t[i / 4, i % 4] = Matrix[i];
            return t;
        }
    }

    /// Identity clears the matrix.
    public FrepNode WithM(Rhino.Geometry.Transform m)
    {
        if (Scene.Placement.IsIdentity(m)) return this with { Matrix = default };
        var a = new double[12];
        for (int i = 0; i < 12; i++) a[i] = m[i / 4, i % 4];
        return this with { Matrix = [.. a] };
    }
}

/// Immutable CSG DAG. Nodes kept in topological order (operands before their node): creation order, unless operand
/// edits (<see cref="WithOperands"/>) re-sorted it. Consumers rely on it (FrepMeshCommand builds bottom-up in one pass).
/// Ids never change. Parentless prims/nodes are hard-min'd at the root.
public sealed record FrepTree(ImmutableList<FrepNode> Nodes, int NextId)
{
    public static readonly FrepTree Empty = new(ImmutableList<FrepNode>.Empty, 1);

    public static string NodeName(int id) => "N" + id;

    /// "N3", "n3" or "3".
    public static bool TryParseName(string? text, out int id)
    {
        id = 0;
        var s = text?.Trim() ?? "";
        if (s.StartsWith('N') || s.StartsWith('n')) s = s[1..];
        return int.TryParse(s, out id);
    }

    public FrepNode? Find(int id) => Nodes.Find(n => n.Id == id);

    public bool IsReferenced(FrepOperand o) => Nodes.Any(n => n.Operands.Contains(o));

    public (FrepTree Tree, FrepNode Node) Add(FrepOp op, double k, IEnumerable<FrepOperand> operands, IEnumerable<FrepMode>? modes = null)
    {
        ImmutableArray<FrepOperand> ops = [.. operands];
        var node = op.IsPreset() || op == FrepOp.Group
            ? new FrepNode(NextId, FrepOp.Group, k, ops, Modes: modes != null ? [.. modes] : FrepOps.PresetModes(op, ops.Length))
            : new FrepNode(NextId, op, k, ops);
        return (new FrepTree(Nodes.Add(node), NextId + 1), node);
    }

    public (FrepTree Tree, FrepNode Node) AddWarp(FrepOp op, FrepOperand child, Guid reference, IEnumerable<double> parameters)
    {
        var node = new FrepNode(NextId, op, 0, [child], Ref: reference, Params: [.. parameters]);
        return (new FrepTree(Nodes.Add(node), NextId + 1), node);
    }

    /// Blend node directly above <paramref name="repeatId"/>. Parents that used the repeat now use the blend.
    public (FrepTree Tree, FrepNode Node) BlendOver(int repeatId) => WrapOver(FrepOp.Blend, 0, repeatId);

    /// Node of <paramref name="op"/> directly above <paramref name="childId"/>. Parents that used the child now use it.
    public (FrepTree Tree, FrepNode Node) WrapOver(FrepOp op, double k, int childId)
    {
        var (tree, node) = Add(op, k, [FrepOperand.Node(childId)]);
        var nodes = tree.Nodes.Select(n =>
        {
            if (n.Id == node.Id || !n.Operands.Any(o => o.IsNode && o.NodeId == childId)) return n;
            return n with { Operands = [.. n.Operands.Select(o => o.IsNode && o.NodeId == childId ? FrepOperand.Node(node.Id) : o)] };
        }).ToImmutableList();
        return ((tree with { Nodes = nodes }).Sorted(), node);
    }

    public FrepTree WithParam(int id, int index, double value) => Edit(id, n =>
    {
        var p = (n.Params.IsDefault ? [] : n.Params).ToBuilder();
        while (p.Count <= index) p.Add(0);
        p[index] = value;
        return n with { Params = p.ToImmutable() };
    });

    public FrepTree WithK(int id, double k) => Edit(id, n => n with { K = k });

    public FrepTree WithOp(int id, FrepOp op, double k) => Edit(id, n => op.IsPreset() && n.IsGroup
        ? n with { K = k, Modes = FrepOps.PresetModes(op, n.Operands.Length) }
        : n with { Op = op, K = k });

    public FrepTree WithModes(IEnumerable<(int NodeId, FrepOperand Operand, FrepMode Mode)> rows)
    {
        var t = this;
        foreach (var (id, o, m) in rows)
        {
            var n = Get(id);
            if (!n.IsGroup) throw new ArgumentException($"{n.Name} is not a group");
            int i = n.Operands.IndexOf(o);
            if (i < 0) throw new ArgumentException($"{Describe(o)} is not an operand of {n.Name}");
            t = t.Edit(id, x => x.WithRows(x.Operands, [.. Enumerable.Range(0, x.Operands.Length).Select(j => j == i ? m : x.ModeAt(j))]));
        }
        return t;
    }

    public FrepTree WithHidden(int id, bool hidden) => Edit(id, n => n with { Hidden = hidden });

    public FrepTree WithM(int id, Rhino.Geometry.Transform m) => Edit(id, n => n.WithM(m));

    FrepTree Edit(int id, Func<FrepNode, FrepNode> f)
    {
        var i = Nodes.FindIndex(n => n.Id == id);
        return i < 0 ? this : this with { Nodes = Nodes.SetItem(i, f(Nodes[i])) };
    }

    // ───── operand edits ─────
    // Rules (ArgumentException otherwise): node ids exist; no duplicate operand in a node; no cycles; warps keep exactly
    // 1 operand; CSG nodes keep ≥ 1. Shader + mesher treat a 1-operand CSG node as its operand (Difference: base minus
    // nothing); a 0-operand one silently draws nothing, so emptying a node is rejected (delete it instead).
    // Leaf object ids aren't checked (no doc here); callers resolve them.

    /// Replaces a node's operands, then re-sorts nodes topologically (stable: unchanged if already ordered).
    public FrepTree WithOperands(int nodeId, ImmutableArray<FrepOperand> operands)
    {
        var node = Get(nodeId);
        Check(node, operands);
        return Edit(nodeId, n => n.Rekeyed(operands)).Sorted();
    }

    /// Inserts at index (default: end).
    public FrepTree AddOperand(int nodeId, FrepOperand operand, int? index = null, FrepMode mode = FrepMode.Add)
    {
        var node = Get(nodeId);
        var operands = Insert(node, node.Operands, operand, index);
        Check(node, operands);
        return Edit(nodeId, n => n.Rekeyed(operands, operand, mode)).Sorted();
    }

    public FrepTree RemoveOperand(int nodeId, FrepOperand operand)
    {
        var node = Get(nodeId);
        if (!node.Operands.Contains(operand)) throw new ArgumentException($"{Describe(operand)} is not an operand of {node.Name}");
        return WithOperands(nodeId, node.Operands.Remove(operand));
    }

    /// Detaches <paramref name="operand"/> from <paramref name="fromNodeId"/> (null: every parent) and inserts it into
    /// <paramref name="toNodeId"/> at <paramref name="index"/> (into the list after detaching; default end).
    /// toNodeId null = root (detach only). from == to reorders. Into a warp: replaces its child (index null or 0); the
    /// old child is dropped from it. All-or-nothing: every touched node is validated before anything changes.
    public FrepTree Move(FrepOperand operand, int? fromNodeId, int? toNodeId, int? index = null, FrepMode? mode = null)
    {
        if (operand.IsNode) Get(operand.NodeId);
        var target = toNodeId is { } tid ? Get(tid) : null;

        List<FrepNode> parents;
        if (fromNodeId is { } fid)
        {
            var from = Get(fid);
            if (!from.Operands.Contains(operand)) throw new ArgumentException($"{Describe(operand)} is not an operand of {from.Name}");
            parents = [from];
        }
        else parents = [.. Nodes.Where(n => n.Operands.Contains(operand))];
        if (target == null && parents.Count == 0) throw new ArgumentException($"{Describe(operand)} is already a root");

        var detached = parents.Select(p => p.ModeOf(operand) ?? FrepMode.Add).Distinct().ToList();
        var carried = mode ?? (detached.Count == 1 ? detached[0] : FrepMode.Add);
        var lists = parents.ToDictionary(p => p.Id, p => p.Operands.Remove(operand));
        if (target != null)
        {
            if (target.Op.IsWarp())
            {
                if (index is not (null or 0)) throw new ArgumentException($"{target.Name} is a warp: its one operand is index 0");
                lists[target.Id] = [operand];
            }
            else lists[target.Id] = Insert(target, lists.GetValueOrDefault(target.Id, target.Operands), operand, index);
        }

        foreach (var (id, ops) in lists) Check(Get(id), ops);
        var t = this;
        foreach (var (id, ops) in lists) t = t.Edit(id, n => n.Rekeyed(ops, operand, carried));
        return t.Sorted();
    }

    public FrepTree MoveOut(FrepOperand operand, int fromNodeId, int? toNodeId, int? index)
    {
        var from = Get(fromNodeId);
        if (from.Op.IsWarp() || from.Operands.Length != 1 || from.Operands[0] != operand || toNodeId == fromNodeId)
            return Move(operand, fromNodeId, toNodeId, index);
        var carried = from.ModeAt(0);
        var nodes = Nodes;
        var gone = new Queue<int>([fromNodeId]);
        while (gone.TryDequeue(out var id))
        {
            var self = FrepOperand.Node(id);
            nodes = nodes.RemoveAll(n => n.Id == id);
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                int at = n.Operands.IndexOf(self);
                if (at < 0) continue;
                if (n.Id == toNodeId && index is { } x && at < x) index = x - 1;
                n = n.WithoutRowAt(at);
                nodes = nodes.SetItem(i, n);
                if (n.Operands.Length > 0 || n.Id == toNodeId) continue;
                if (n.Op.IsWarp()) throw new ArgumentException($"{n.Name} is a {n.Op} warp and would lose its only operand");
                gone.Enqueue(n.Id);
            }
        }
        var t = this with { Nodes = nodes };
        if (toNodeId is not { } tid) return t;
        if (t.Find(tid) == null) throw new ArgumentException($"{NodeName(tid)} would be left empty and removed");
        return t.AddOperand(tid, operand, index, carried);
    }

    /// True if node <paramref name="id"/> is (transitively) under node <paramref name="ancestorId"/>.
    public bool IsDescendant(int id, int ancestorId)
    {
        var seen = new HashSet<int>();
        bool Walk(int a) => Find(a) is { } n &&
                            n.Operands.Any(o => o.IsNode && (o.NodeId == id || (seen.Add(o.NodeId) && Walk(o.NodeId))));
        return Walk(ancestorId);
    }

    FrepNode Get(int id) => Find(id) ?? throw new ArgumentException($"no node {NodeName(id)}");

    /// Per-node rules for a proposed operand list (see "operand edits").
    void Check(FrepNode node, ImmutableArray<FrepOperand> operands)
    {
        if (operands.IsDefault) throw new ArgumentException("operands must not be default");
        if (node.Op.IsWarp() && operands.Length != 1)
            throw new ArgumentException(operands.Length == 0
                ? $"{node.Name} is a {node.Op} warp and would lose its only operand; delete it, or move something into it to replace it"
                : $"{node.Name} is a {node.Op} warp: it takes exactly 1 operand");
        if (!node.Op.IsWarp() && operands.Length == 0)
            throw new ArgumentException($"{node.Name} would have no operands; delete the node instead");
        var seen = new HashSet<FrepOperand>();
        foreach (var o in operands)
        {
            if (!seen.Add(o)) throw new ArgumentException($"{node.Name} already has operand {Describe(o)}");
            if (!o.IsNode) continue;
            Get(o.NodeId);
            if (o.NodeId == node.Id || IsDescendant(node.Id, o.NodeId))
                throw new ArgumentException($"{NodeName(o.NodeId)} can't go under {node.Name}: {node.Name} is inside it (cycle)");
        }
    }

    static ImmutableArray<FrepOperand> Insert(FrepNode node, ImmutableArray<FrepOperand> operands, FrepOperand o, int? index)
    {
        if (operands.Contains(o)) throw new ArgumentException($"{node.Name} already has operand {Describe(o)}");
        int i = index ?? operands.Length;
        if (i < 0 || i > operands.Length) throw new ArgumentException($"index {i} out of range for {node.Name} (0..{operands.Length})");
        return operands.Insert(i, o);
    }

    static string Describe(FrepOperand o) => o.IsNode ? NodeName(o.NodeId) : o.ObjectId.ToString("D");

    /// Stable topological re-sort: DFS in current order, operands first, so an ordered list comes back as the same
    /// instance. Throws on a cycle (Check catches single edits; this guards multi-node Move). Missing node operands skipped.
    FrepTree Sorted()
    {
        var byId = Nodes.ToDictionary(n => n.Id);
        var done = new Dictionary<int, bool>(); // false = on the DFS stack
        var order = ImmutableList.CreateBuilder<FrepNode>();
        void Visit(FrepNode n)
        {
            if (done.TryGetValue(n.Id, out var d))
            {
                if (!d) throw new ArgumentException($"edit would create a cycle through {n.Name}");
                return;
            }
            done[n.Id] = false;
            foreach (var o in n.Operands)
                if (o.IsNode && byId.TryGetValue(o.NodeId, out var c)) Visit(c);
            done[n.Id] = true;
            order.Add(n);
        }
        foreach (var n in Nodes) Visit(n);
        return order.SequenceEqual(Nodes, ReferenceEqualityComparer.Instance) ? this : this with { Nodes = order.ToImmutable() };
    }

    /// Caller ensures nothing references it.
    public FrepTree Remove(int id) => this with { Nodes = Nodes.RemoveAll(n => n.Id == id) };

    /// A transform replaces the Rhino object, so operands and warp refs follow the new id.
    public FrepTree Retarget(Guid from, Guid to)
    {
        if (from == Guid.Empty || to == Guid.Empty || from == to) return this;
        var nodes = new List<FrepNode>(Nodes.Count);
        bool changed = false;
        foreach (var n in Nodes)
        {
            bool refHit = n.Ref == from;
            bool opHit = n.Operands.Any(o => !o.IsNode && o.ObjectId == from);
            if (!refHit && !opHit) { nodes.Add(n); continue; }
            changed = true;
            var ops = opHit
                ? n.Operands.Select(o => !o.IsNode && o.ObjectId == from ? FrepOperand.Object(to) : o).ToImmutableArray()
                : n.Operands;
            nodes.Add(n with { Ref = refHit ? to : n.Ref, Operands = ops });
        }
        return changed ? this with { Nodes = nodes.ToImmutableList() } : this;
    }

    /// Drop these nodes and primitive operands. A deleted node's children take its place.
    /// A union, intersect, or difference left with one operand is replaced by that operand;
    /// a node left with none is removed. Twist, bend, repeat, and blend keep their one child.
    public FrepTree Excise(IReadOnlySet<int> dropNodes, IReadOnlySet<Guid> dropObjects)
    {
        List<(FrepOperand O, FrepMode M)> Expand(FrepOperand o, FrepMode m, HashSet<int> stack)
        {
            if (!o.IsNode)
                return dropObjects.Contains(o.ObjectId) ? [] : [(o, m)];
            if (!dropNodes.Contains(o.NodeId))
                return [(o, m)];
            if (!stack.Add(o.NodeId))
                return [];
            var found = Find(o.NodeId);
            var acc = new List<(FrepOperand, FrepMode)>();
            if (found != null)
                for (int i = 0; i < found.Operands.Length; i++)
                    acc.AddRange(Expand(found.Operands[i], acc.Count == 0 ? m : found.ModeAt(i), stack));
            stack.Remove(o.NodeId);
            return acc;
        }

        var kept = new List<FrepNode>(Nodes.Count);
        bool changed = false;
        foreach (var n in Nodes)
        {
            if (dropNodes.Contains(n.Id)) { changed = true; continue; }
            var rows = new List<(FrepOperand O, FrepMode M)>();
            for (int i = 0; i < n.Operands.Length; i++)
                rows.AddRange(Expand(n.Operands[i], n.ModeAt(i), []));
            if (rows.Count == n.Operands.Length && rows.Select(r => r.O).SequenceEqual(n.Operands) && rows.Select(r => r.M).SequenceEqual(Enumerable.Range(0, rows.Count).Select(n.ModeAt)))
                kept.Add(n);
            else
            {
                kept.Add(n.WithRows([.. rows.Select(r => r.O)], [.. rows.Select(r => r.M)]));
                changed = true;
            }
        }
        if (!changed) return this;
        return new FrepTree(kept.ToImmutableList(), NextId).Collapse();
    }

    /// Combine ops with one operand become that operand; empty nodes go away. Repeats until nothing else folds.
    FrepTree Collapse()
    {
        var nodes = Nodes;
        while (true)
        {
            // A one-operand node with a transform M is kept: folding it into its operand would drop M.
            int i = nodes.FindIndex(n => n.Operands.Length == 0
                || (n.Operands.Length == 1 && !n.HasMatrix && n.IsGroup));
            if (i < 0) break;
            var gone = nodes[i];
            FrepOperand? only = gone.Operands.Length == 1 ? gone.Operands[0] : null;
            nodes = nodes.RemoveAt(i).Select(n => ReplaceOperand(n, gone.Id, only)).ToImmutableList();
        }
        return ReferenceEquals(nodes, Nodes) ? this : this with { Nodes = nodes };
    }

    static FrepNode ReplaceOperand(FrepNode n, int goneId, FrepOperand? only)
    {
        if (!n.Operands.Any(o => o.IsNode && o.NodeId == goneId)) return n;
        var ops = ImmutableArray.CreateBuilder<FrepOperand>(n.Operands.Length);
        var modes = ImmutableArray.CreateBuilder<FrepMode>(n.Operands.Length);
        for (int i = 0; i < n.Operands.Length; i++)
        {
            var o = n.Operands[i];
            if (o.IsNode && o.NodeId == goneId)
            {
                if (only is not { } kept || ops.Contains(kept)) continue;
                ops.Add(kept);
            }
            else ops.Add(o);
            modes.Add(n.ModeAt(i));
        }
        return n.WithRows(ops.ToImmutable(), modes.ToImmutable());
    }

    const int Major = 1, Minor = 5; // 1.1: per-node Hidden; 1.2: Ref + Params; 1.3: Matrix; 1.4: followed by a shader cache; 1.5: row modes after the cache

    public void Write(BinaryArchiveWriter w)
    {
        w.Write3dmChunkVersion(Major, Minor);
        w.WriteInt(NextId);
        w.WriteInt(Nodes.Count);
        foreach (var n in Nodes)
        {
            w.WriteInt(n.Id);
            w.WriteInt((int)(n.IsGroup ? FrepOps.PresetOf(n.Modes, n.Operands.Length) ?? FrepOp.Union : n.Op));
            w.WriteDouble(n.K);
            w.WriteInt(n.Operands.Length);
            foreach (var o in n.Operands)
            {
                w.WriteInt(o.NodeId);
                w.WriteGuid(o.ObjectId);
            }
        }
        foreach (var n in Nodes) w.WriteBool(n.Hidden);
        foreach (var n in Nodes)
        {
            w.WriteGuid(n.Ref);
            var p = n.Params.IsDefault ? [] : n.Params;
            w.WriteInt(p.Length);
            foreach (var v in p) w.WriteDouble(v);
        }
        foreach (var n in Nodes)
        {
            var m = n.HasMatrix ? n.Matrix : [];
            w.WriteInt(m.Length);
            foreach (var v in m) w.WriteDouble(v);
        }
    }

    public void WriteModes(BinaryArchiveWriter w)
    {
        w.WriteInt(Nodes.Count);
        foreach (var n in Nodes)
        {
            w.WriteInt(n.IsGroup ? n.Operands.Length : 0);
            if (n.IsGroup)
                for (int i = 0; i < n.Operands.Length; i++) w.WriteInt((int)n.ModeAt(i));
        }
    }

    public FrepTree ReadModes(BinaryArchiveReader r)
    {
        int count = r.ReadInt();
        if (count != Nodes.Count) throw new InvalidDataException("row modes don't match the tree");
        var nodes = Nodes.ToBuilder();
        for (int i = 0; i < count; i++)
        {
            var modes = new FrepMode[r.ReadInt()];
            for (int j = 0; j < modes.Length; j++) modes[j] = (FrepMode)r.ReadInt();
            if (nodes[i].IsGroup && modes.Length == nodes[i].Operands.Length) nodes[i] = nodes[i] with { Modes = [.. modes] };
        }
        return this with { Nodes = nodes.ToImmutable() };
    }

    public static FrepTree Read(BinaryArchiveReader r, out int minor)
    {
        r.Read3dmChunkVersion(out var major, out minor);
        if (major != Major) throw new InvalidDataException($"F-rep tree version {major} unsupported");
        int nextId = r.ReadInt();
        int count = r.ReadInt();
        var nodes = ImmutableList.CreateBuilder<FrepNode>();
        for (int i = 0; i < count; i++)
        {
            int id = r.ReadInt();
            var op = (FrepOp)r.ReadInt();
            double k = r.ReadDouble();
            int nOps = r.ReadInt();
            var ops = ImmutableArray.CreateBuilder<FrepOperand>(nOps);
            for (int j = 0; j < nOps; j++)
                ops.Add(new FrepOperand(r.ReadInt(), r.ReadGuid()));
            nodes.Add(op.IsPreset()
                ? new FrepNode(id, FrepOp.Group, k, ops.MoveToImmutable(), Modes: FrepOps.PresetModes(op, nOps))
                : new FrepNode(id, op, k, ops.MoveToImmutable()));
        }
        if (minor >= 1)
            for (int i = 0; i < count; i++)
                nodes[i] = nodes[i] with { Hidden = r.ReadBool() };
        if (minor >= 2)
            for (int i = 0; i < count; i++)
            {
                var reference = r.ReadGuid();
                var p = new double[r.ReadInt()];
                for (int j = 0; j < p.Length; j++) p[j] = r.ReadDouble();
                nodes[i] = nodes[i] with { Ref = reference, Params = [.. p] };
            }
        if (minor >= 3)
            for (int i = 0; i < count; i++)
            {
                var m = new double[r.ReadInt()];
                for (int j = 0; j < m.Length; j++) m[j] = r.ReadDouble();
                if (m.Length == 12) nodes[i] = nodes[i] with { Matrix = [.. m] };
            }
        return new FrepTree(nodes.ToImmutable(), nextId);
    }
}

/// Per-document trees (persisted via plugin Write/ReadDocument). All edits go through <see cref="Set"/> (or
/// <see cref="Preview"/> + <see cref="Commit"/>) so they're undoable.
public static class FrepTrees
{
    static readonly Dictionary<uint, FrepTree> trees = [];
    /// Open live-edit gesture per doc: the tree to undo back to.
    static readonly Dictionary<uint, (FrepTree Before, string Description)> previews = [];

    /// Raised on the UI thread after any change (edit, undo/redo, file open).
    public static event Action<RhinoDoc>? Changed;

    public static void Install()
    {
        RhinoDoc.CloseDocument += (_, e) =>
        {
            trees.Remove(e.Document.RuntimeSerialNumber);
            previews.Remove(e.Document.RuntimeSerialNumber);
        };
        // Open (not merge/import) replaces the doc's contents; ReadDocument then fills the tree back in.
        RhinoDoc.BeginOpenDocument += (_, e) =>
        {
            if (e.Merge || e.Reference) return;
            previews.Remove(e.Document.RuntimeSerialNumber);
            Apply(e.Document, FrepTree.Empty);
        };
        // Any command (incl. Undo/Redo) ends a live gesture whose commit never came.
        Command.BeginCommand += (_, _) => CommitAll();
    }

    public static FrepTree Get(RhinoDoc doc) => trees.GetValueOrDefault(doc.RuntimeSerialNumber, FrepTree.Empty);

    /// Replaces the doc's tree with an undo step. Opens its own undo record if none is active (e.g. panel edits).
    public static void Set(RhinoDoc doc, FrepTree tree, string description)
    {
        Commit(doc);
        var old = Get(doc);
        if (ReferenceEquals(old, tree)) return;
        Record(doc, old, description);
        Apply(doc, tree);
    }

    /// Live edit (slider drag): applies without undo; the first call of a gesture remembers the tree to undo to.
    /// A different description starts a new gesture (commits the open one). End with <see cref="Commit"/>.
    public static void Preview(RhinoDoc doc, FrepTree tree, string description)
    {
        var key = doc.RuntimeSerialNumber;
        if (previews.TryGetValue(key, out var p) && p.Description != description) Commit(doc);
        if (!previews.ContainsKey(key)) previews[key] = (Get(doc), description);
        if (!ReferenceEquals(Get(doc), tree)) Apply(doc, tree);
    }

    /// Ends the doc's open gesture as one undo step (none if it ended where it started).
    public static void Commit(RhinoDoc doc)
    {
        if (!previews.Remove(doc.RuntimeSerialNumber, out var p)) return;
        var now = Get(doc);
        if (p.Before.NextId == now.NextId && p.Before.Nodes.SequenceEqual(now.Nodes)) return;
        Record(doc, p.Before, p.Description);
    }

    public static void CommitAll()
    {
        foreach (var serial in previews.Keys.ToList())
            if (RhinoDoc.FromRuntimeSerialNumber(serial) is { } doc) Commit(doc);
            else previews.Remove(serial);
    }

    /// Undo step back to <paramref name="old"/>; the current tree is the redo state.
    static void Record(RhinoDoc doc, FrepTree old, string description)
    {
        uint record = doc.UndoRecordingIsActive ? 0 : doc.BeginUndoRecord(description);
        doc.AddCustomUndoEvent(description, OnUndo, old);
        if (record != 0) doc.EndUndoRecord(record);
    }

    /// Swap in the tagged tree and register the inverse (Rhino's custom undo pattern: redo is registered from the undo handler).
    static void OnUndo(object? sender, CustomUndoEventArgs e)
    {
        var doc = e.Document;
        var current = Get(doc);
        doc.AddCustomUndoEvent(e.ActionDescription, OnUndo, current);
        Apply(doc, (FrepTree)e.Tag);
    }

    /// No undo; for file load and previews.
    public static void Apply(RhinoDoc doc, FrepTree tree)
    {
        trees[doc.RuntimeSerialNumber] = tree;
        FrepScene.MarkDirty();
        Changed?.Invoke(doc);
        doc.Views.Redraw();
    }
}
