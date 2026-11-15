using Freppuccino.Csg;
using Freppuccino.Meshing;
using Rhino.Geometry;

namespace Freppuccino.Scene;

/// The scene as a CPU field, following FrepShaderGen.Compile: nodes in creation order, missing leaves (hidden or
/// deleted primitives, hidden nodes) dropped the same way, parentless primitives and nodes as roots.
/// Warps: hidden = missing, missing child = empty, ref gone = child unwarped. Shell / gyroid: missing child = empty.
/// Pipe: curve gone = empty. Shared by FrepMesh and picking.
public static class FrepFieldBuilder
{
    /// <paramref name="only"/>: restrict the field's roots to these operands (each must be a root of the tree; others are
    /// ignored). Null = every root. <paramref name="roots"/>: the operands that ended up as roots (a root whose result is
    /// empty is left out).
    public static FrepField Build(FrepSceneData scene, IReadOnlyCollection<FrepOperand>? only, out List<FrepOperand> roots)
    {
        var tree = scene.Tree;
        var field = new FrepField { ImplicitUnion = false };
        var index = new Dictionary<Guid, int>();
        // A user primitive (FFunction) without a compiled program is a missing leaf, as in the shader.
        for (int i = 0; i < scene.Primitives.Count; i++)
            if (Add(field, scene.Primitives[i]) is { } fi) index[scene.Ids[i]] = fi;

        var warps = new Dictionary<int, SdWarp>();
        for (int i = 0; i < scene.Warps.Count; i++) warps[scene.WarpIds[i]] = scene.Warps[i];
        var pipes = new Dictionary<int, SdPipe>();
        for (int i = 0; i < scene.Pipes.Count; i++) pipes[scene.PipeIds[i]] = scene.Pipes[i];

        var nodeRef = new Dictionary<int, FieldRef?>();
        FieldRef? Ref(FrepOperand o) => o.IsNode
            ? nodeRef.GetValueOrDefault(o.NodeId)
            : index.TryGetValue(o.ObjectId, out var i) ? FieldRef.Primitive(i) : null;
        foreach (var n in tree.Nodes)
        {
            nodeRef[n.Id] = null;
            if (n.Hidden) continue;
            var m = MatrixRows(n);
            if (n.Op == FrepOp.Pipe)
            {
                // Curve deleted: nothing (as in the shader).
                if (pipes.TryGetValue(n.Id, out var pipe)) nodeRef[n.Id] = FieldRef.Node(field.AddPipe(FrepNodeMath.PipeRowsOf(pipe), m));
                continue;
            }
            if (n.Op.IsModifier())
            {
                if (n.Operands.Length == 0 || Ref(n.Operands[0]) is not { } inner) continue;
                nodeRef[n.Id] = n.Op == FrepOp.Shell
                    ? FieldRef.Node(field.AddModifier(FieldOp.Shell, FrepNodeMath.ShellRows(n), inner, m))
                    : FieldRef.Node(field.AddModifier(FieldOp.Gyroid, FrepNodeMath.GyroidRows(n), inner, m));
                continue;
            }
            if (n.Op == FrepOp.Blend)
            {
                // Fillet between a Repeat's copies (main #26).
                if (n.Operands.Length == 0 || Ref(n.Operands[0]) is not { } blended) continue;
                nodeRef[n.Id] = FieldRef.Node(field.AddNode(FieldOp.Blend, n.K, [blended], m));
                continue;
            }
            if (n.Op == FrepOp.Offset)
            {
                // Surface shift of a boolean (main #28): K moves it out (positive) or in.
                if (n.Operands.Length == 0 || Ref(n.Operands[0]) is not { } shifted) continue;
                nodeRef[n.Id] = FieldRef.Node(field.AddNode(FieldOp.Offset, n.K, [shifted], m));
                continue;
            }
            if (n.Op.IsWarp())
            {
                if (n.Operands.Length == 0 || Ref(n.Operands[0]) is not { } child) continue;
                nodeRef[n.Id] = warps.TryGetValue(n.Id, out var w)
                    ? FieldRef.Node(field.AddWarp(n.Op switch { FrepOp.Twist => FieldOp.Twist, FrepOp.Bend => FieldOp.Bend, _ => FieldOp.Repeat },
                        FrepWarpMath.Rows(w), child, m))
                    // Reference gone: the child passes through unwarped, but the node's own transform still applies.
                    : m == null ? child : FieldRef.Node(field.AddNode(FieldOp.Union, 0, [child], m));
                continue;
            }
            var terms = n.Operands.Select(Ref).ToList();
            // Union skips missing operands; intersect with one missing is empty; difference needs its base.
            bool empty = n.Op switch
            {
                FrepOp.Union => false,
                FrepOp.Intersect => terms.Contains(null),
                _ => terms.Count == 0 || terms[0] == null,
            };
            var live = terms.OfType<FieldRef>().ToList();
            if (empty || live.Count == 0) continue;
            var op = n.Op switch { FrepOp.Union => FieldOp.Union, FrepOp.Intersect => FieldOp.Intersect, _ => FieldOp.Difference };
            nodeRef[n.Id] = FieldRef.Node(field.AddNode(op, n.K, live, m, FrepNodeMath.BlendCode(n)));
        }

        roots = [];
        var referenced = tree.Nodes.SelectMany(n => n.Operands).ToHashSet();
        for (int i = 0; i < scene.Primitives.Count; i++)
        {
            var o = FrepOperand.Object(scene.Ids[i]);
            if (referenced.Contains(o) || (only != null && !only.Contains(o)) || !index.TryGetValue(scene.Ids[i], out var fi)) continue;
            field.AddRoot(FieldRef.Primitive(fi));
            roots.Add(o);
        }
        foreach (var n in tree.Nodes)
        {
            var o = FrepOperand.Node(n.Id);
            if (nodeRef[n.Id] is not { } r || referenced.Contains(o) || (only != null && !only.Contains(o))) continue;
            field.AddRoot(r);
            roots.Add(o);
        }
        return field;
    }

    /// A node's transform M as field rows, or null (none, or unusable).
    public static V4[]? MatrixRows(FrepNode n) => n.HasMatrix && Placement.TryRows(n.M, out var rows) ? rows : null;

    /// A field of one primitive (a control instance's shape, for picking).
    public static FrepField Single(SdPrimitive prim)
    {
        var field = new FrepField { ImplicitUnion = false };
        if (Add(field, prim) is { } i) field.AddRoot(FieldRef.Primitive(i));
        return field;
    }

    /// The primitive's field index, or null for a user primitive without a compiled program (a missing leaf).
    static int? Add(FrepField field, SdPrimitive prim)
    {
        var (kind, frame, p) = prim;
        // SDF.Linear (gumball shear / off-axis scale): the canonical-to-world columns replace the frame axes; the local
        // distance is scaled by σ_min so the field stays Lipschitz 1 (same as the shader).
        var x = frame.XAxis;
        var y = frame.YAxis;
        var z = frame.ZAxis;
        double lip = 1;
        if (kind is not (SdKind.Sphere or SdKind.Ellipsoid) && prim.Basis is { } basis)
        {
            x = basis.X;
            y = basis.Y;
            z = basis.Z;
            if (Sdf.SdfTransforms.TryMetrics(basis, out _, out _, out _, out var sigmaMin, out _)) lip = sigmaMin;
        }
        if (kind == SdKind.User)
        {
            // FFunction (main #23): the scene drops ones that don't compile; skipping keeps that a missing leaf here too.
            if (prim.Fn?.Program is not { } program) return null;
            var u = program.Params.Select((prm, j) => j < prim.Fn.Values.Length ? prim.Fn.Values[j] : prm.Default).ToArray();
            return field.AddUser(V(frame.Origin), V(x), V(y), V(p), new UserField(program.Cpu, u, prim.Fn.L), V(z), lip);
        }
        var fieldKind = kind switch
        {
            SdKind.Sphere => FieldKind.Sphere,
            SdKind.Box => FieldKind.Box,
            SdKind.Torus => FieldKind.Torus,
            SdKind.Cylinder => FieldKind.Cylinder,
            SdKind.Cone => FieldKind.Cone,
            SdKind.Ellipsoid => FieldKind.Ellipsoid,
            SdKind.Pyramid => FieldKind.Pyramid,
            _ => throw new NotSupportedException($"no CPU field for {kind} yet"),
        };
        return field.AddPrimitive(fieldKind, V(frame.Origin), V(x), V(y), V(p), V(z), lip);
    }

    static V3 V(Point3d p) => new(p.X, p.Y, p.Z);
    static V3 V(Vector3d v) => new(v.X, v.Y, v.Z);
}
