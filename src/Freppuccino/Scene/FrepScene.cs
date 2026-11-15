using Freppuccino.Commands;
using Freppuccino.Csg;
using Freppuccino.Sdf;
using Freppuccino.Shaders;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Scene;

/// Primitive kinds the viewport shader can draw.
public enum SdKind { Sphere, Box, Torus, Cylinder, Cone, Ellipsoid, Pyramid }

/// One F-rep primitive: local frame (as written in SDF.Frame.*) + up to three IQ parameters packed in P.
///   Sphere    P.X = s (radius)
///   Box       P = b (half-extents along frame X/Y/Z)
///   Torus     P.X = t.x (major radius), P.Y = t.y (minor radius); axis = frame Y
///   Cylinder  P.X = h (half height), P.Y = r; axis = frame Y
///   Cone      P.X = h (half height), P.Y = r1 (at y = -h), P.Z = r2 (at y = +h); axis = frame Y
///   Ellipsoid P = r (semi-axes along frame X/Y/Z)
///   Pyramid   P.X = h (height), P.Y = s (base side); base center at the origin, apex at +Y
/// Basis, when set, is the canonical-to-world map (SDF.Linear.*). The frame axes stay the parameter frame;
/// the parameters are not pre-scaled by that map.
public readonly record struct SdPrimitive(SdKind Kind, Plane Frame, Vector3d P)
{
    public SdfBasis? Basis { get; init; }
}

/// One live warp node: its reference geometry (Axis for Twist/Bend, Region for Repeat) + params (FrepNode.Params, padded to 3).
/// Extent: child's max distance from the axis (Lipschitz bound clamp). Anchor: child bbox center (Repeat lattice origin).
public readonly record struct SdWarp(FrepOp Op, Line Axis, Box Region, Point3d Anchor, double Extent, Vector3d P);

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
    // Parallel to scene.Pipes: the referenced curves.
    static List<RhinoObject> pipeOwners = [];
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

        List<SdPrimitive>? prims = null;
        bool reshaped = false;
        for (int i = 0; i < owners.Count; i++)
        {
            if (!owners[i].GetDynamicTransform(out var xf) || xf.IsIdentity) continue;
            // A transform that flattens the primitive previews at the original position.
            if (!TryTransform(scene.Primitives[i], xf, out var moved)) continue;
            prims ??= [.. scene.Primitives];
            prims[i] = moved;
            reshaped |= moved.Kind != scene.Primitives[i].Kind;
        }
        List<SdWarp>? warps = null;
        for (int i = 0; i < warpOwners.Count; i++)
        {
            if (!warpOwners[i].GetDynamicTransform(out var xf) || xf.IsIdentity) continue;
            var w = scene.Warps[i];
            var axis = w.Axis;
            var region = w.Region;
            if (!(w.Op == FrepOp.Repeat ? region.Transform(xf) : axis.Transform(xf))) continue;
            warps ??= [.. scene.Warps];
            warps[i] = w with { Axis = axis, Region = region };
        }
        // Pipes follow their curve's drag (the sampled points move rigidly; radii stay, like the committed edit).
        List<SdPipe>? pipes = null;
        for (int i = 0; i < pipeOwners.Count; i++)
        {
            if (!pipeOwners[i].GetDynamicTransform(out var xf) || xf.IsIdentity) continue;
            pipes ??= [.. scene.Pipes];
            pipes[i] = scene.Pipes[i].Transformed(xf);
        }
        if (prims == null && warps == null && pipes == null) return scene;
        var next = scene with { Primitives = prims ?? scene.Primitives, Warps = warps ?? scene.Warps, Pipes = pipes ?? scene.Pipes };
        if (!reshaped || prims == null) return next;

        // A sphere stretched into an ellipsoid changes the shader's structure, so recompile (cached by source, so once
        // per gesture). If the extra rows don't fit, preview those prims unchanged; the commit still converts them.
        if (prims.Sum(p => FrepShaderGen.Rows(p.Kind)) + FrepShaderGen.ColorRows(prims.Count) > FrepShaderGen.PrimRowBudget(tree))
        {
            for (int i = 0; i < prims.Count; i++)
                if (prims[i].Kind != scene.Primitives[i].Kind) prims[i] = scene.Primitives[i];
            return next;
        }
        var compiled = FrepShaderGen.Compile(prims, scene.Ids, tree, scene.WarpIds.Concat(scene.PipeIds).ToHashSet());
        return next with { Program = compiled.Program, Ks = compiled.Ks, Codes = compiled.Codes };
    }

    /// Same rules as the per-command TransformSdf handlers, applied to in-memory values.
    static bool TryTransform(SdPrimitive p, Transform xf, out SdPrimitive result)
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
        if (rigid && TryAbsorb(p.Kind, p.P, scale, out var np))
        {
            result = new SdPrimitive(p.Kind, factored, np);
            return true;
        }
        var frozen = new Plane(origin, p.Frame.XAxis, p.Frame.YAxis);
        result = new SdPrimitive(p.Kind, frozen, p.P) { Basis = next.Matches(frozen) ? null : next };
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
        int budget = FrepShaderGen.PrimRowBudget(tree), rows = 0, dropped = 0;
        var prims = new List<SdPrimitive>();
        var colors = new List<System.Drawing.Color>();
        var newOwners = new List<RhinoObject>();
        var newIds = new List<Guid>();
        var settings = new ObjectEnumeratorSettings { VisibleFilter = true, HiddenObjects = false };
        foreach (var obj in doc.Objects.GetObjectList(settings))
        {
            if (!TryRead(obj.Attributes, out var prim)) continue;
            int r = FrepShaderGen.Rows(prim.Kind);
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
        foreach (var n in tree.Nodes)
            if (FrepWarpMath.TryResolve(doc, n, out var obj, out var axis, out var region)) refs[n.Id] = (obj!, axis, region);

        // Pipe curves likewise; the curve's SDF.Color colors the pipe.
        var pipeRefs = new Dictionary<int, (RhinoObject Obj, Curve Curve)>();
        foreach (var n in tree.Nodes)
            if (FrepNodeMath.TryResolve(doc, n, out var obj, out var curve)) pipeRefs[n.Id] = (obj!, curve);

        var compiled = FrepShaderGen.Compile(prims, newIds, tree, refs.Keys.Concat(pipeRefs.Keys).ToHashSet());
        var bounds = new Bounds(doc, tree, refs);
        var warps = new List<SdWarp>();
        var warpIds = new List<int>();
        var newWarpOwners = new List<RhinoObject>();
        var pipes = new List<SdPipe>();
        var pipeIds = new List<int>();
        var newPipeOwners = new List<RhinoObject>();
        foreach (var id in compiled.RowNodes)
        {
            var n = tree.Find(id)!;
            if (refs.TryGetValue(id, out var w))
            {
                warps.Add(FrepWarpMath.Make(n, w.Axis, w.Region, bounds.Of(n.Operands[0])));
                warpIds.Add(id);
                newWarpOwners.Add(w.Obj);
            }
            else if (pipeRefs.TryGetValue(id, out var p))
            {
                pipes.Add(FrepNodeMath.Make(n, p.Curve, SdfUserText.ReadColorOrDefault(p.Obj.Attributes)));
                pipeIds.Add(id);
                newPipeOwners.Add(p.Obj);
            }
        }

        scene = new FrepSceneData(compiled.Program, prims, colors, warps, compiled.Ks, dropped)
        {
            Ids = newIds, WarpIds = warpIds, Pipes = pipes, PipeIds = pipeIds, Codes = compiled.Codes, RowNodes = compiled.RowNodes,
            Tree = tree,
        };
        owners = newOwners;
        warpOwners = newWarpOwners;
        pipeOwners = newPipeOwners;
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

    /// World bounding box of an operand's result, from leaf objects' Rhino geometry; live warps grown like the shader bound.
    sealed class Bounds(RhinoDoc doc, FrepTree tree, Dictionary<int, (RhinoObject Obj, Line Axis, Box Region)> refs)
    {
        readonly Dictionary<FrepOperand, BoundingBox> memo = [];

        public BoundingBox Of(FrepOperand o)
        {
            if (memo.TryGetValue(o, out var b)) return b;
            b = BoundingBox.Empty;
            if (!o.IsNode)
            {
                if (doc.Objects.FindId(o.ObjectId) is { IsDeleted: false } obj) b = obj.Geometry.GetBoundingBox(true);
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
            }
            return memo[o] = b;
        }
    }

    /// Reads one object's SDF.* values; false if it isn't a drawable F-rep primitive (or its values are degenerate).
    public static bool TryRead(ObjectAttributes a, out SdPrimitive prim)
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
            default:
                return false;
        }
    }

    static SdPrimitive Mapped(ObjectAttributes attributes, SdKind kind, Plane frame, Vector3d p)
    {
        var prim = new SdPrimitive(kind, frame, p);
        if (SdfUserText.TryReadLinear(attributes, out var x, out var y, out var z))
            prim = prim with { Basis = new SdfBasis(x, y, z) };
        return prim;
    }
}

