using System.Collections.Immutable;
using Freppuccino.Csg;
using Rhino.Geometry;

namespace Freppuccino.Selection;

/// One control instance: a leaf object reached from <see cref="Root"/> through the nodes in <see cref="Path"/>
/// (root node first, the leaf's parent last; empty when the leaf is itself a parentless root).
/// T: product of the Transform nodes on the path (identity until root transforms exist). The instance shows the
/// leaf's pre-warp shape: warps on the path don't deform it.
public sealed record FrepInstance(FrepOperand Root, ImmutableArray<int> Path, Guid Object, Transform T)
{
    public bool SamePath(FrepInstance o) => Root == o.Root && Object == o.Object && Path.SequenceEqual(o.Path);
}

/// Roots and control instances of a tree, in a stable order: parentless primitives (in <paramref name="leaves"/> order),
/// then parentless nodes in creation order; operands depth-first in order. An object's primary instance is its first.
public static class FrepInstances
{
    /// <paramref name="leaves"/>: the primitive objects that exist and are visible (others are missing leaves).
    /// Hidden nodes and their subtrees are skipped, like the shader. Warp nodes are passed through (their child is an instance).
    public static (List<FrepOperand> Roots, List<FrepInstance> Instances) Compute(FrepTree tree, IReadOnlyList<Guid> leaves)
    {
        var live = leaves.ToHashSet();
        var referenced = tree.Nodes.SelectMany(n => n.Operands).ToHashSet();
        var roots = new List<FrepOperand>();
        var instances = new List<FrepInstance>();

        foreach (var id in leaves)
        {
            var o = FrepOperand.Object(id);
            if (referenced.Contains(o)) continue;
            roots.Add(o);
            instances.Add(new FrepInstance(o, [], id, Transform.Identity));
        }

        // Per node, memoized: the distinct (object, T) its subtree reaches, each with the first path to it (depth-first,
        // operands in order). Paths reaching the same object under the same T are the same shape at the same place, so
        // one representative is kept: the count stays bounded by objects × distinct transforms however much of the DAG
        // is shared (expanding every path is exponential in a Fibonacci-shaped graph).
        var byId = tree.Nodes.ToDictionary(n => n.Id);
        var memo = new Dictionary<int, List<(Guid Obj, Transform T, ImmutableArray<int> Path)>>();
        List<(Guid, Transform, ImmutableArray<int>)> Reach(FrepNode node)
        {
            if (memo.TryGetValue(node.Id, out var got)) return got;
            var list = new List<(Guid Obj, Transform T, ImmutableArray<int> Path)>();
            var own = NodeTransform(node);
            void Add(Guid obj, Transform t, ImmutableArray<int> sub)
            {
                var tt = own * t;
                if (list.Any(x => x.Obj == obj && Same(x.T, tt))) return;
                list.Add((obj, tt, sub.Insert(0, node.Id)));
            }
            foreach (var c in node.Operands)
            {
                if (c.IsNode)
                {
                    if (byId.TryGetValue(c.NodeId, out var child) && !child.Hidden)
                        foreach (var (obj, t, sub) in Reach(child)) Add(obj, t, sub);
                }
                else if (live.Contains(c.ObjectId)) Add(c.ObjectId, Transform.Identity, []);
            }
            return memo[node.Id] = list;
        }

        foreach (var n in tree.Nodes)
        {
            var o = FrepOperand.Node(n.Id);
            if (referenced.Contains(o) || n.Hidden) continue;
            roots.Add(o);
            foreach (var (obj, t, path) in Reach(n)) instances.Add(new FrepInstance(o, path, obj, t));
        }
        return (roots, instances);
    }

    /// The transform a node applies to everything below it (its M).
    static Transform NodeTransform(FrepNode n) => n.M;

    public static bool Same(Transform a, Transform b)
    {
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 4; c++)
                if (Math.Abs(a[r, c] - b[r, c]) > 1e-9 * (1 + Math.Abs(a[r, c]))) return false;
        return true;
    }

    /// The root a node or object belongs to, if exactly one root reaches it (null if none or several).
    public static FrepOperand? OwningRoot(FrepTree tree, IReadOnlyList<Guid> leaves, Guid obj)
    {
        var roots = Compute(tree, leaves).Instances.Where(i => i.Object == obj).Select(i => i.Root).Distinct().ToList();
        return roots.Count == 1 ? roots[0] : null;
    }
}
