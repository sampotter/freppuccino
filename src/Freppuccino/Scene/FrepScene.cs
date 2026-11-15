using Freppuccino.Commands;
using Freppuccino.Csg;
using Freppuccino.Sdf;
using Freppuccino.Shaders;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Scene;

/// Primitive kinds the viewport shader can draw.
public enum SdKind { Sphere, Box, Torus, Cylinder, Cone, Ellipsoid, Pyramid, User }

/// One F-rep primitive: local frame (as written in SDF.Frame.*) + up to three IQ parameters packed in P.
///   Sphere    P.X = s (radius)
///   Box       P = b (half-extents along frame X/Y/Z)
///   Torus     P.X = t.x (major radius), P.Y = t.y (minor radius); axis = frame Y
///   Cylinder  P.X = h (half height), P.Y = r; axis = frame Y
///   Cone      P.X = h (half height), P.Y = r1 (at y = -h), P.Z = r2 (at y = +h); axis = frame Y
///   Ellipsoid P = r (semi-axes along frame X/Y/Z)
///   Pyramid   P.X = h (height), P.Y = s (base side); base center at the origin, apex at +Y
///   User      P = b (half-extents of the clip box, like Box); code/params/L in Fn. Field = max(f(q)/L, sdBox(q, b)).
/// Basis, when set, is the canonical-to-world map (SDF.Linear.*). The frame axes stay the parameter frame;
/// the parameters are not pre-scaled by that map.
public readonly record struct SdPrimitive(SdKind Kind, Plane Frame, Vector3d P)
{
    public SdfBasis? Basis { get; init; }
    /// Kind == User only.
    public Freppuccino.UserFn.UserFnInstance? Fn { get; init; }
}

/// One live warp node: its reference geometry (Axis for Twist/Bend, Region for Repeat) + params (FrepNode.Params, padded to 3).
/// Extent: child's max distance from the axis (Lipschitz bound clamp). Anchor: child bbox center (Repeat lattice origin).
/// Half: child bbox half-extents along the Region axes (Repeat neighbor search).
public readonly record struct SdWarp(FrepOp Op, Line Axis, Box Region, Point3d Anchor, double Extent, Vector3d P)
{
    public Vector3d Half { get; init; }
}

/// One live Pipe node: its curve sampled to a polyline (≤ FrepNodeMath.PipePoints points), a radius per point, and the curve's
/// SDF.Color (white if missing).
public readonly record struct SdPipe(Point3d[] Points, double[] Radii, System.Drawing.Color Color)
{
    public SdPipe Transformed(Transform xf)
    {
        var pts = (Point3d[])Points.Clone();
        for (int i = 0; i < pts.Length; i++) pts[i].Transform(xf);
        return this with { Points = pts };
    }
}

/// Program == null: nothing to draw. Primitives/Colors/Warps/Pipes/Ks/Codes feed <see cref="FrepShaderGen.SceneUniforms"/>.
/// Colors parallel to Primitives: each owner's SDF.Color (white if missing).
/// Ks: blend k per CSG node (tree order); Codes parallel: blend style code (FrepNodeMath.BlendCode).
public sealed record FrepSceneData(ShaderSource? Program, IReadOnlyList<SdPrimitive> Primitives, IReadOnlyList<System.Drawing.Color> Colors,
    IReadOnlyList<SdWarp> Warps, IReadOnlyList<double> Ks, int Dropped)
{
    public static readonly FrepSceneData Empty = new(null, [], [], [], [], 0);
    /// Visible FFunctions left out because their code doesn't compile (missing leaves, like Dropped).
    public int FailedFunctions { get; init; }
    /// Rhino object ids, parallel to Primitives (what the CSG tree's leaves refer to).
    public IReadOnlyList<Guid> Ids { get; init; } = [];
    /// Tree node ids, parallel to Warps.
    public IReadOnlyList<int> WarpIds { get; init; } = [];
    /// Live Pipe nodes and their node ids (parallel).
    public IReadOnlyList<SdPipe> Pipes { get; init; } = [];
    public IReadOnlyList<int> PipeIds { get; init; } = [];
    /// Blend style codes, parallel to Ks.
    public IReadOnlyList<double> Codes { get; init; } = [];
    /// Nodes owning uniform rows (live warps, shells, gyroids, live pipes), in row order (FrepShaderGen.Compile).
    public IReadOnlyList<int> RowNodes { get; init; } = [];
    /// Non-empty roots in the shader's map() order (its selection flags follow it).
    public IReadOnlyList<FrepOperand> Roots { get; init; } = [];
    /// The tree this was compiled from.
    public FrepTree Tree { get; init; } = FrepTree.Empty;
    public int Count => Primitives.Count;
}

/// Visible objects in the active doc carrying F-rep metadata (SDF.* user text, written by the F* commands) + the doc's CSG tree,
/// compiled to a shader. The SDF values are the source of truth, not the Rhino geometry. Rebuilt lazily after any
/// doc/object/layer/tree change; blend edits rebuild too but hit the shader cache (k is a uniform).
/// Objects under a dynamic transform (gumball/Move drag) are reported at their in-progress position.
public static class FrepScene
{
    static FrepSceneData scene = FrepSceneData.Empty;
    // Parallel to scene.Primitives; safe since any delete/replace sets dirty.
    static List<RhinoObject> owners = [];
    // Parallel to scene.Warps: the referenced line/box objects.
    static List<RhinoObject> warpOwners = [];
    // Parallel to warpOwners: each object's applied root transforms A (Placement.AppliedKey).
    static List<Transform> warpApplied = [];
    // Parallel to scene.Pipes: the referenced curves, and their applied root transforms A.
    static List<RhinoObject> pipeOwners = [];
    static List<Transform> pipeApplied = [];
    static FrepTree tree = FrepTree.Empty; // the one scene was compiled from
    static uint docSerial;
    static bool dirty = true;

    public static void Install()
    {
        RhinoDoc.AddRhinoObject += Dirty;
        RhinoDoc.DeleteRhinoObject += Dirty;
        RhinoDoc.UndeleteRhinoObject += Dirty;
        RhinoDoc.ReplaceRhinoObject += (_, _) => dirty = true;
        RhinoDoc.ModifyObjectAttributes += (_, _) => dirty = true; // hide/show, layer change, SDF.* user text edits
        RhinoDoc.LayerTableEvent += (_, _) => dirty = true;
        RhinoDoc.EndOpenDocument += (_, _) => dirty = true;
        RhinoDoc.NewDocument += (_, _) => dirty = true;
    }

    public static void MarkDirty() => dirty = true;

    static void Dirty(object? sender, RhinoObjectEventArgs e) => dirty = true;

    public static FrepSceneData Get(RhinoDoc doc)
    {
        if (dirty || doc.RuntimeSerialNumber != docSerial) Rebuild(doc);

        // Blend carriers under a drag: their roots' matrices preview as drag × M (the tree is only written on commit).
        // Like the commit, a root wins over its descendants: leaves and warp references reachable from a dragged root
        // get no canonical preview of their own, even when they are selected (and so dragged) too.
        var previewTree = tree;
        var movingRoots = new HashSet<int>();
        foreach (var o in doc.Objects.GetSelectedObjects(false, false))
        {
            if (Selection.FrepCarriers.NodeOf(o) is not { } id || !o.GetDynamicTransform(out var cx) || cx.IsIdentity) continue;
            if (previewTree.Find(id) is not { } n || !Placement.IsUsable(cx * n.M)) continue;
            previewTree = previewTree.WithM(id, cx * n.M);
            movingRoots.Add(id);
        }
        var underMoving = new HashSet<Guid>();
        var refsUnderMoving = new HashSet<Guid>();
        // Instance carriers under a drag: the leaf previews Q = T_inst⁻¹ X T_inst (unless a moving root carries it),
        // taking precedence over the leaf's own drag, as at commit.
        var instanceDrag = new Dictionary<Guid, Transform>();
        var anyCarrierDrag = movingRoots.Count > 0 || doc.Objects.GetSelectedObjects(false, false).Any(o => o is Selection.FrepCarrier);
        if (anyCarrierDrag)
        {
            var (allRoots, instances) = Selection.FrepInstances.Compute(tree, scene.Ids);
            foreach (var inst in instances)
                if (inst.Root.IsNode && movingRoots.Contains(inst.Root.NodeId)) underMoving.Add(inst.Object);
            // References follow their owning root (first in instance order), exactly like the commit (sol #170).
            foreach (var (refId, owner) in Selection.FrepCarriers.ReferenceOwners(tree, allRoots))
                if (movingRoots.Contains(owner.NodeId)) refsUnderMoving.Add(refId);
            foreach (var o in doc.Objects.GetSelectedObjects(false, false))
            {
                if (Selection.FrepCarriers.InstanceOf(o, instances) is not { } inst || underMoving.Contains(inst.Object)) continue;
                if (!o.GetDynamicTransform(out var dx) || dx.IsIdentity || !inst.T.TryGetInverse(out var tiv)) continue;
                instanceDrag[inst.Object] = tiv * dx * inst.T;
            }
        }

        List<SdPrimitive>? prims = null;
        bool reshaped = false;
        for (int i = 0; i < owners.Count; i++)
        {
            if (underMoving.Contains(owners[i].Id)) continue;
            if (instanceDrag.TryGetValue(owners[i].Id, out var q))
            {
                if (TryTransform(scene.Primitives[i], q, out var viaInstance))
                {
                    prims ??= [.. scene.Primitives];
                    prims[i] = viaInstance;
                    reshaped |= viaInstance.Kind != scene.Primitives[i].Kind;
                }
                continue;
            }
            if (!owners[i].GetDynamicTransform(out var xf) || xf.IsIdentity) continue;
            // Under transformed roots the leaf sits at T × canonical: the drag is T⁻¹ X T on its values.
            if (Selection.FrepSelection.InstanceOf(doc, owners[i].Id, tree, scene.Ids) is { } inst && !inst.T.IsIdentity && inst.T.TryGetInverse(out var ti))
                xf = ti * xf * inst.T;
            // A transform that flattens the primitive previews at the original position.
            if (!TryTransform(scene.Primitives[i], xf, out var moved)) continue;
            prims ??= [.. scene.Primitives];
            prims[i] = moved;
            reshaped |= moved.Kind != scene.Primitives[i].Kind;
        }
        List<SdWarp>? warps = null;
        for (int i = 0; i < warpOwners.Count; i++)
        {
            if (refsUnderMoving.Contains(warpOwners[i].Id)) continue;
            if (!warpOwners[i].GetDynamicTransform(out var world) || world.IsIdentity) continue;
            // The drag moves the real object (A × canonical); in canonical space that is A⁻¹ × drag × A.
            var a = warpApplied[i];
            var xf = a.TryGetInverse(out var ai) ? ai * world * a : world;
            var w = scene.Warps[i];
            var axis = w.Axis;
            var region = w.Region;
            if (!(w.Op == FrepOp.Repeat ? region.Transform(xf) : axis.Transform(xf))) continue;
            warps ??= [.. scene.Warps];
            warps[i] = w with { Axis = axis, Region = region };
        }
        // Pipes follow their curve's drag (the sampled points move rigidly; radii stay, like the committed edit). Like warp
        // references: skipped when a moving root carries the curve, and the drag is taken into canonical space (A⁻¹ × drag × A).
        List<SdPipe>? pipes = null;
        for (int i = 0; i < pipeOwners.Count; i++)
        {
            if (refsUnderMoving.Contains(pipeOwners[i].Id)) continue;
            if (!pipeOwners[i].GetDynamicTransform(out var world) || world.IsIdentity) continue;
            var a = pipeApplied[i];
            var xf = a.TryGetInverse(out var ai) ? ai * world * a : world;
            pipes ??= [.. scene.Pipes];
            pipes[i] = scene.Pipes[i].Transformed(xf);
        }
        if (!ReferenceEquals(previewTree, tree))
        {
            var p2 = prims ?? scene.Primitives;
            var c2 = FrepShaderGen.Compile(p2, scene.Ids, previewTree, scene.WarpIds.Concat(scene.PipeIds).ToHashSet());
            return scene with
            {
                Primitives = p2, Warps = warps ?? scene.Warps, Pipes = pipes ?? scene.Pipes, Tree = previewTree,
                Program = c2.Program, Ks = c2.Ks, Codes = c2.Codes, RowNodes = c2.RowNodes, Roots = c2.Roots,
            };
        }
        if (prims == null && warps == null && pipes == null) return scene;
        var next = scene with { Primitives = prims ?? scene.Primitives, Warps = warps ?? scene.Warps, Pipes = pipes ?? scene.Pipes };
        if (!reshaped || prims == null) return next;

        // A sphere stretched into an ellipsoid changes the shader's structure, so recompile (cached by source, so once
        // per gesture). If the extra rows don't fit, preview those prims unchanged; the commit still converts them.
        if (prims.Sum(FrepShaderGen.Rows) + FrepShaderGen.ColorRows(prims.Count) > FrepShaderGen.PrimRowBudget(tree))
        {
            for (int i = 0; i < prims.Count; i++)
                if (prims[i].Kind != scene.Primitives[i].Kind) prims[i] = scene.Primitives[i];
            return next;
        }
        var compiled = FrepShaderGen.Compile(prims, scene.Ids, tree, scene.WarpIds.Concat(scene.PipeIds).ToHashSet());
        return next with { Program = compiled.Program, Ks = compiled.Ks, Codes = compiled.Codes, RowNodes = compiled.RowNodes, Roots = compiled.Roots };
    }

    /// Same rules as the per-command TransformSdf handlers, applied to in-memory values.
    internal static bool TryTransform(SdPrimitive p, Transform xf, out SdPrimitive result)
    {
        result = p;
        var v = p.P;
        Plane f;
        bool ok;
        switch (p.Kind)
        {
            case SdKind.Sphere:
                if (FSphereCommand.TryTransform(p.Frame, v.X, xf, out f, out var s, out _))
                {
                    result = p with { Frame = f, P = new Vector3d(s, 0, 0) };
                    return true;
                }
                // Non-uniform: becomes an ellipsoid, same as the commit in FSphereCommand.TransformSdf.
                if (!FSphereCommand.TryTransformToEllipsoid(p.Frame, v.X, xf, out f, out var er, out _)) return false;
                result = new SdPrimitive(SdKind.Ellipsoid, f, er);
                return true;
            case SdKind.Box:
            case SdKind.Torus:
            case SdKind.Cylinder:
            case SdKind.Cone:
            case SdKind.Pyramid:
            case SdKind.User:
                return TryTransformAffine(p, xf, out result);
            case SdKind.Ellipsoid:
                ok = FEllipsoidCommand.TryTransform(p.Frame, v, xf, out f, out v, out _);
                break;
            default:
                return false;
        }
        if (!ok) return false;
        result = p with { Frame = f, P = v };
        return true;
    }

    /// Rotate/scale along the parameter axes folds into P. Anything else is carried on <see cref="SdPrimitive.Basis"/>.
    static bool TryTransformAffine(SdPrimitive p, Transform xf, out SdPrimitive result)
    {
        result = p;
        var current = p.Basis ?? SdfBasis.FromPlane(p.Frame);
        if (!SdfTransforms.TryAdvance(p.Frame, current, xf, out var origin, out var factored, out var next, out var scale, out var rigid, out _))
            return false;
        // `with` keeps Fn (User).
        if (rigid && TryAbsorb(p.Kind, p.P, scale, out var np))
        {
            result = p with { Frame = factored, P = np, Basis = null };
            return true;
        }
        var frozen = new Plane(origin, p.Frame.XAxis, p.Frame.YAxis);
        result = p with { Frame = frozen, Basis = next.Matches(frozen) ? null : next };
        return true;
    }

    static bool TryAbsorb(SdKind kind, Vector3d p, Vector3d scale, out Vector3d np)
    {
        np = p;
        switch (kind)
        {
            case SdKind.Box when FBoxCommand.TryAbsorbScale(p, scale, out var b):
                np = b;
                return true;
            case SdKind.Torus when FTorusCommand.TryAbsorbScale(new Vector2d(p.X, p.Y), scale, out var t):
                np = new Vector3d(t.X, t.Y, 0);
                return true;
            case SdKind.Cylinder when FCylinderCommand.TryAbsorbScale(p.X, p.Y, scale, out var h, out var r):
                np = new Vector3d(h, r, 0);
                return true;
            case SdKind.Cone when FConeCommand.TryAbsorbScale(p.X, p.Y, p.Z, scale, out var ch, out var r1, out var r2):
                np = new Vector3d(ch, r1, r2);
                return true;
            case SdKind.Pyramid when FPyramidCommand.TryAbsorbScale(p.X, p.Y, scale, out var ph, out var ps):
                np = new Vector3d(ph, ps, 0);
                return true;
            case SdKind.User when FFunctionCommand.IsUnitScale(scale): // rigid only; scale stretches the function (Basis)
                return true;
            default:
                return false;
        }
    }

    static void Rebuild(RhinoDoc doc)
    {
        dirty = false;
        docSerial = doc.RuntimeSerialNumber;
        tree = FrepTrees.Get(doc);

        // Prims first, so uniform rows for blends/warps are reserved up front; prims past the budget are dropped (= missing leaves).
        int budget = FrepShaderGen.PrimRowBudget(tree), rows = 0, dropped = 0, failed = 0;
        var prims = new List<SdPrimitive>();
        var colors = new List<System.Drawing.Color>();
        var newOwners = new List<RhinoObject>();
        var newIds = new List<Guid>();
        var settings = new ObjectEnumeratorSettings { VisibleFilter = true, HiddenObjects = false };
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (!TryRead(obj.Attributes, out var prim)) continue;
            // FFunction whose code doesn't compile: dropped (missing leaf); diagnostics come from compiling SDF.Code again (cached).
            if (prim.Kind == SdKind.User && prim.Fn?.Program == null) { failed++; continue; }
            int r = FrepShaderGen.Rows(prim);
            // Each prim also takes a color slot (4 per row).
            if (rows + r + FrepShaderGen.ColorRows(prims.Count + 1) > budget) { dropped++; continue; }
            rows += r;
            newIds.Add(obj.Id);
            prims.Add(prim);
            // SDF.Color metadata (set from the panel); objects without it draw white.
            colors.Add(SdfUserText.ReadColorOrDefault(obj.Attributes));
            newOwners.Add(obj);
        }

        // Warp reference objects may be hidden (axis lines usually are); deleted = missing.
        var refs = new Dictionary<int, (RhinoObject Obj, Line Axis, Box Region)>();
        var applied = new Dictionary<int, Transform>();
        foreach (var n in tree.Nodes)
            if (FrepWarpMath.TryResolve(doc, n, out var obj, out var axis, out var region, out var a))
            {
                refs[n.Id] = (obj!, axis, region);
                applied[n.Id] = a;
            }

        // Pipe curves likewise (canonical = A⁻¹ × the real curve); the curve's SDF.Color colors the pipe.
        var pipeRefs = new Dictionary<int, (RhinoObject Obj, Curve Curve, Transform Applied)>();
        foreach (var n in tree.Nodes)
            if (FrepNodeMath.TryResolve(doc, n, out var obj, out var curve, out var a)) pipeRefs[n.Id] = (obj!, curve, a);

        var compiled = FrepShaderGen.Compile(prims, newIds, tree, refs.Keys.Concat(pipeRefs.Keys).ToHashSet());
        var bounds = new Bounds(doc, tree, refs);
        var warps = new List<SdWarp>();
        var warpIds = new List<int>();
        var newWarpOwners = new List<RhinoObject>();
        var newWarpApplied = new List<Transform>();
        var pipes = new List<SdPipe>();
        var pipeIds = new List<int>();
        var newPipeOwners = new List<RhinoObject>();
        var newPipeApplied = new List<Transform>();
        foreach (var id in compiled.RowNodes)
        {
            var n = tree.Find(id)!;
            if (refs.TryGetValue(id, out var w))
            {
                warps.Add(FrepWarpMath.Make(n, w.Axis, w.Region, bounds.Of(n.Operands[0])));
                warpIds.Add(id);
                newWarpOwners.Add(w.Obj);
                newWarpApplied.Add(applied[id]);
            }
            else if (pipeRefs.TryGetValue(id, out var p))
            {
                pipes.Add(FrepNodeMath.Make(n, p.Curve, SdfUserText.ReadColorOrDefault(p.Obj.Attributes)));
                pipeIds.Add(id);
                newPipeOwners.Add(p.Obj);
                newPipeApplied.Add(p.Applied);
            }
        }

        scene = new FrepSceneData(compiled.Program, prims, colors, warps, compiled.Ks, dropped)
        {
            Ids = newIds, WarpIds = warpIds, Pipes = pipes, PipeIds = pipeIds, Codes = compiled.Codes, RowNodes = compiled.RowNodes,
            Tree = tree, Roots = compiled.Roots, FailedFunctions = failed,
        };
        owners = newOwners;
        warpOwners = newWarpOwners;
        warpApplied = newWarpApplied;
        pipeOwners = newPipeOwners;
        pipeApplied = newPipeApplied;
    }

    /// <see cref="Bounds"/> ignoring warps (no resolved refs); for command defaults.
    public static BoundingBox BoundsOf(RhinoDoc doc, FrepOperand o) => new Bounds(doc, FrepTrees.Get(doc), []).Of(o);

    /// <see cref="Bounds"/> with live warps applied (Repeat copies, bent / twisted extents); for framing views (AI screenshots).
    public static BoundingBox WarpedBoundsOf(RhinoDoc doc, FrepOperand o)
    {
        var tree = FrepTrees.Get(doc);
        var refs = new Dictionary<int, (RhinoObject Obj, Line Axis, Box Region)>();
        foreach (var n in tree.Nodes)
            if (FrepWarpMath.TryResolve(doc, n, out var obj, out var axis, out var region)) refs[n.Id] = (obj!, axis, region);
        return new Bounds(doc, tree, refs).Of(o);
    }

    /// Bounding box of an operand's result in its parent's space (world for a root): leaves from their Rhino geometry
    /// mapped back by their primary instance's root transforms T (the real object sits at T × canonical), live warps
    /// grown like the shader bound, node transforms applied.
    sealed class Bounds(RhinoDoc doc, FrepTree tree, Dictionary<int, (RhinoObject Obj, Line Axis, Box Region)> refs)
    {
        readonly Dictionary<FrepOperand, BoundingBox> memo = [];
        Dictionary<Guid, Transform>? primaryT;

        Transform PrimaryT(Guid id)
        {
            if (primaryT == null)
            {
                primaryT = [];
                var leaves = tree.Nodes.SelectMany(n => n.Operands).Where(x => !x.IsNode).Select(x => x.ObjectId).Distinct().ToList();
                foreach (var inst in Selection.FrepInstances.Compute(tree, leaves).Instances)
                    primaryT.TryAdd(inst.Object, inst.T);
            }
            return primaryT.GetValueOrDefault(id, Transform.Identity);
        }

        public BoundingBox Of(FrepOperand o)
        {
            if (memo.TryGetValue(o, out var b)) return b;
            b = BoundingBox.Empty;
            if (!o.IsNode)
            {
                if (doc.Objects.FindId(o.ObjectId) is { IsDeleted: false } obj)
                {
                    b = obj.Geometry.GetBoundingBox(true);
                    var t = PrimaryT(o.ObjectId);
                    if (!t.IsIdentity && t.TryGetInverse(out var ti)) b.Transform(ti);
                }
            }
            else if (tree.Find(o.NodeId) is { } n)
            {
                if (n.Op == FrepOp.Pipe)
                {
                    if (doc.Objects.FindId(n.Ref) is { IsDeleted: false } c && FrepRefs.TryCurve(c.Geometry, out var curve))
                    {
                        b = curve.GetBoundingBox(true);
                        b.Inflate(Math.Max(Math.Max(n.Param(0), n.Param(1)), 0));
                    }
                }
                else if (n.Op == FrepOp.Difference && n.Operands.Length > 0) b = Of(n.Operands[0]);
                else foreach (var c in n.Operands) b.Union(Of(c));
                if (b.IsValid && refs.TryGetValue(n.Id, out var r)) b = FrepShaderGen.Warped(b, FrepWarpMath.Make(n, r.Axis, r.Region, b));
                if (b.IsValid && n.HasMatrix) b.Transform(n.M);
            }
            return memo[o] = b;
        }
    }

    /// Reads one object's SDF.* values; false if it isn't a drawable F-rep primitive (or its values are degenerate).
    /// An FFunction reads even if its code doesn't compile (check Fn.Program).
    public static bool TryRead(ObjectAttributes a, out SdPrimitive prim)
    {
        // An unreadable or degenerate SDF.Linear makes the primitive degenerate (like a zero radius), never unsheared.
        if (SdfUserText.ReadLinear(a, out _, out _, out _) == SdfUserText.LinearState.Invalid)
        {
            prim = default;
            return false;
        }
        return TryReadCore(a, out prim);
    }

    static bool TryReadCore(ObjectAttributes a, out SdPrimitive prim)
    {
        prim = default;
        switch (SdfUserText.ReadType(a))
        {
            case FSphereCommand.SdfType when FSphereCommand.TryRead(a, out var f, out var s) && s > 0:
                prim = Mapped(a, SdKind.Sphere, f, new Vector3d(s, 0, 0));
                return true;
            case FBoxCommand.SdfType when FBoxCommand.TryRead(a, out var f, out var b) && b.X > 0 && b.Y > 0 && b.Z > 0:
                prim = Mapped(a, SdKind.Box, f, b);
                return true;
            case FTorusCommand.SdfType when FTorusCommand.TryRead(a, out var f, out var t) && t.X > 0 && t.Y > 0:
                prim = Mapped(a, SdKind.Torus, f, new Vector3d(t.X, t.Y, 0));
                return true;
            case FCylinderCommand.SdfType when FCylinderCommand.TryRead(a, out var f, out var h, out var r) && h > 0 && r > 0:
                prim = Mapped(a, SdKind.Cylinder, f, new Vector3d(h, r, 0));
                return true;
            case FConeCommand.SdfType when FConeCommand.TryRead(a, out var f, out var h, out var r1, out var r2) &&
                                           h > 0 && r1 >= 0 && r2 >= 0 && r1 + r2 > 0:
                prim = Mapped(a, SdKind.Cone, f, new Vector3d(h, r1, r2));
                return true;
            case FEllipsoidCommand.SdfType when FEllipsoidCommand.TryRead(a, out var f, out var r) && r.X > 0 && r.Y > 0 && r.Z > 0:
                prim = Mapped(a, SdKind.Ellipsoid, f, r);
                return true;
            case FPyramidCommand.SdfType when FPyramidCommand.TryRead(a, out var f, out var h, out var s) && h > 0 && s > 0:
                prim = Mapped(a, SdKind.Pyramid, f, new Vector3d(h, s, 0));
                return true;
            // True even when the code doesn't compile (Fn.Program null), so tools can still read / fix / move it.
            case FFunctionCommand.SdfType when FFunctionCommand.TryRead(a, out var f, out var b, out var fn):
                prim = Mapped(a, SdKind.User, f, b) with { Fn = fn };
                return true;
            default:
                return false;
        }
    }

    static SdPrimitive Mapped(ObjectAttributes attributes, SdKind kind, Plane frame, Vector3d p)
    {
        var prim = new SdPrimitive(kind, frame, p);
        if (SdfUserText.ReadLinear(attributes, out var x, out var y, out var z) == SdfUserText.LinearState.Valid)
            prim = prim with { Basis = new SdfBasis(x, y, z) };
        return prim;
    }
}

