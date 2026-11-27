using Freppuccino.Csg;
using Freppuccino.Scene;
using Freppuccino.Sdf;
using Freppuccino.Selection;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Display;

/// Outline-only drawing of F-rep control shapes in the Freppuccino display mode (plan section 5).
/// Rhino doesn't draw the control breps there at all (<see cref="Suppress"/>): no faces, edges, isocurves or selection
/// highlight, but they stay pickable and keep their gumball. We draw view silhouettes instead, per control instance:
/// the real brep stands for its primary instance; an instance under a different root transform (a shared shape) is the
/// brep mapped by T_instance × T_primary⁻¹. Cached per object version, instance transform and camera.
///   unselected: thin grey, before the shader pass, so blends cover them (only uncovered outlines show);
///   selected (the picked instance of a selected leaf, or a selected standalone shape): selection colour, on top.
public static class ControlOutlines
{
    const SilhouetteType Kinds = SilhouetteType.Projecting | SilhouetteType.TangentProjects | SilhouetteType.Crease | SilhouetteType.Boundary;
    static readonly System.Drawing.Color Unselected = System.Drawing.Color.FromArgb(70, 70, 70);
    public static System.Drawing.Color UnselectedColor => Unselected;
    public static int HiddenAlpha { get; set; } = 64;

    readonly record struct Key(Guid Obj, uint Serial, string Rel, int Wire, bool Persp, Point3d Eye, Vector3d Dir);
    static readonly Dictionary<Key, Curve[]> cache = [];

    /// Diagnostics: the placement (relative to the real brep) each instance outline was last drawn with.
    public static readonly Dictionary<(Guid Obj, FrepOperand Root, string Path), Transform> LastDrawn = [];

    public static bool IsControl(RhinoObject? o) => o != null && SdfUserText.ReadType(o.Attributes) != null;

    /// For DisplayConduit.PreDrawObject: hide control breps in Freppuccino-mode views.
    public static void Suppress(DrawObjectEventArgs e)
    {
        if (FreppuccinoDisplayMode.IsActive(e.Display) && (IsControl(e.RhinoObject) || Commands.FrepMeshCommand.IsBaked(e.RhinoObject))) e.DrawObject = false;
    }

    /// <paramref name="selected"/>: false = every unselected instance; true = the selected ones.
    public static void Draw(DisplayPipeline dp, RhinoDoc doc, bool selected, bool? cutters = null, bool hidden = false)
    {
        var vp = dp.Viewport;
        var color = selected ? Rhino.ApplicationSettings.AppearanceSettings.SelectedObjectColor : Unselected;
        if (hidden) color = System.Drawing.Color.FromArgb(HiddenAlpha, color);
        int thickness = selected ? 2 : 1;
        // Base instance transforms from the committed tree: FrepScene.Get's tree already carries a dragged root's preview
        // matrix, and the real brep still sits at its committed primary placement. Each preview is applied once below.
        var scene = Scene.FrepScene.Get(doc);
        var tree = Csg.FrepTrees.Get(doc);
        var (roots, instances) = FrepInstances.Compute(tree, scene.Ids);
        var cutterObjects = Cutters(tree, roots);
        var rootSet = roots.ToHashSet();
        var selectedLeaves = FrepSelection.DrawnLeaves(doc);

        // Per object, every instance (per root; already deduplicated within a root); the first is the primary instance.
        var byObject = new Dictionary<Guid, List<FrepInstance>>();
        foreach (var i in instances)
        {
            if (!byObject.TryGetValue(i.Object, out var list)) byObject[i.Object] = list = [];
            list.Add(i);
        }

        // Drags in progress, with the same precedence as the field preview (FrepScene.Get) and the commit: a dragged root
        // carrier moves its instances (and its reachable leaves get no edit of their own); then an instance carrier's
        // Q = T_inst⁻¹ X T_inst; then a leaf's own drag X of its real brep.
        var rootDrag = new Dictionary<int, Transform>();
        var instDrag = new Dictionary<Guid, Transform>();
        var leafDrag = new Dictionary<Guid, Transform>();
        foreach (var s in doc.Objects.GetSelectedObjects(false, false))
        {
            if (!s.GetDynamicTransform(out var dx) || dx.IsIdentity) continue;
            if (FrepCarriers.NodeOf(s) is { } nid) rootDrag[nid] = dx;
            else if (FrepCarriers.InstanceOf(s, instances) is { } ci && ci.T.TryGetInverse(out var cti)) instDrag[ci.Object] = cti * dx * ci.T;
            else if (IsControl(s)) leafDrag[s.Id] = dx;
        }
        var underMoving = rootDrag.Count == 0 ? [] : instances
            .Where(i => i.Root.IsNode && rootDrag.ContainsKey(i.Root.NodeId)).Select(i => i.Object).ToHashSet();

        var settings = new ObjectEnumeratorSettings { VisibleFilter = true, HiddenObjects = false, ObjectTypeFilter = ObjectType.Brep | ObjectType.Extrusion };
        foreach (var o in doc.Objects.GetObjectList(settings))
        {
            if (!IsControl(o)) continue;
            bool objSelected = o.IsSelected(false) > 0;
            if (!byObject.TryGetValue(o.Id, out var list) || list.Count == 0)
            {
                // Not reached by any visible root (e.g. under a hidden node): its brep as is (following its own drag).
                var alone = FrepDisplaySettings.OtherShapes;
                if (objSelected == selected && cutters != true && (selected || alone != ShapeDisplay.Hidden))
                    Draw(dp, o, leafDrag.TryGetValue(o.Id, out var own) ? own : null, vp, doc, color, thickness, alone == ShapeDisplay.Wireframe);
                continue;
            }
            var primary = list[0];
            if (!primary.T.TryGetInverse(out var primaryInv)) continue;
            foreach (var inst in list)
            {
                // Selected: a standalone shape whose brep is selected, or this exact instance among the selected leaves
                // (picked through its path, or through its own instance carrier).
                bool isSel = (objSelected && rootSet.Contains(FrepOperand.Object(o.Id))) ||
                    selectedLeaves.Any(l => l.SamePath(inst)); // by path: its T may carry a drag preview
                bool isCutter = cutterObjects.Contains(inst.Object);
                var mode = FrepDisplaySettings.For(isCutter);
                if (isSel != selected || (cutters is { } c && c != isCutter) || (!selected && mode == ShapeDisplay.Hidden)) continue;
                // The real brep B sits at the primary instance (B = T_p c); this instance shows T_inst c, previewed:
                Transform eff;
                if (inst.Root.IsNode && rootDrag.TryGetValue(inst.Root.NodeId, out var rx)) eff = rx * inst.T * primaryInv;
                else if (underMoving.Contains(o.Id)) eff = inst.T * primaryInv;             // a moving root carries it
                else if (instDrag.TryGetValue(o.Id, out var q)) eff = inst.T * q * primaryInv;
                else if (leafDrag.TryGetValue(o.Id, out var lx)) eff = inst.T * primaryInv * lx;
                else eff = inst.T * primaryInv;
                LastDrawn[(o.Id, inst.Root, string.Join(",", inst.Path))] = eff;
                Draw(dp, o, Placement.IsIdentity(eff, 1e-12) ? null : eff, vp, doc, color, thickness, mode == ShapeDisplay.Wireframe);
            }
        }
    }

    public static HashSet<Guid> HiddenObjects(RhinoDoc doc)
    {
        if (!FrepDisplaySettings.AnyHidden) return [];
        var cutters = CutterObjects(doc);
        var settings = new ObjectEnumeratorSettings { HiddenObjects = true, ObjectTypeFilter = ObjectType.Brep | ObjectType.Extrusion };
        return doc.Objects.GetObjectList(settings).Where(IsControl).Select(o => o.Id)
            .Where(id => FrepDisplaySettings.For(cutters.Contains(id)) == ShapeDisplay.Hidden).ToHashSet();
    }

    public static HashSet<Guid> CutterObjects(RhinoDoc doc)
    {
        var tree = Csg.FrepTrees.Get(doc);
        var (roots, _) = FrepInstances.Compute(tree, Scene.FrepScene.Get(doc).Ids);
        return Cutters(tree, roots);
    }

    static HashSet<Guid> Cutters(Csg.FrepTree tree, List<FrepOperand> roots)
    {
        var byId = tree.Nodes.ToDictionary(n => n.Id);
        var reached = new HashSet<int>();
        var cut = new HashSet<FrepOperand>();
        var result = new HashSet<Guid>();
        void Visit(FrepOperand o, bool subtracted)
        {
            if (!o.IsNode)
            {
                if (subtracted) result.Add(o.ObjectId);
                return;
            }
            if (!byId.TryGetValue(o.NodeId, out var n) || n.Hidden || !(subtracted ? cut.Add(o) : reached.Add(o.NodeId))) return;
            for (int i = 0; i < n.Operands.Length; i++) Visit(n.Operands[i], subtracted || (n.Op == Csg.FrepOp.Difference && i > 0));
        }
        foreach (var r in roots) Visit(r, false);
        return result;
    }

    public static List<(FrepInstance Instance, Curve[] Curves)> Pickable(RhinoDoc doc, RhinoViewport vp)
    {
        var scene = Scene.FrepScene.Get(doc);
        var tree = Csg.FrepTrees.Get(doc);
        var (roots, instances) = FrepInstances.Compute(tree, scene.Ids);
        var cutterObjects = Cutters(tree, roots);
        var result = new List<(FrepInstance, Curve[])>();
        foreach (var group in instances.GroupBy(i => i.Object))
        {
            if (doc.Objects.FindId(group.Key) is not { IsLocked: false, IsHidden: false, Visible: true } o || !IsControl(o)) continue;
            var mode = FrepDisplaySettings.For(cutterObjects.Contains(group.Key));
            if (mode == ShapeDisplay.Hidden) continue;
            if (!group.First().T.TryGetInverse(out var primaryInv)) continue;
            foreach (var inst in group)
            {
                var eff = inst.T * primaryInv;
                result.Add((inst, Outline(o, Placement.IsIdentity(eff, 1e-12) ? null : eff, vp, doc, mode == ShapeDisplay.Wireframe)));
            }
        }
        return result;
    }

    static void Draw(DisplayPipeline dp, RhinoObject o, Transform? rel, RhinoViewport vp, RhinoDoc doc, System.Drawing.Color color, int thickness, bool wire = false)
    {
        if (sink != null) { sink.AddRange(Outline(o, rel, vp, doc, wire)); return; }
        foreach (var c in Outline(o, rel, vp, doc, wire)) dp.DrawCurve(c, color, thickness);
    }

    static List<Curve>? sink;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Curve, float[]> segments = new();

    public static float[] Segments(DisplayPipeline dp, RhinoDoc doc, bool cutters)
    {
        sink = [];
        try { Draw(dp, doc, selected: false, cutters: cutters); }
        finally { var got = sink; sink = null; collected = got; }
        var parts = collected.Select(c => segments.GetValue(c, Endpoints)).ToList();
        var all = new float[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
        return all;
    }

    static List<Curve> collected = [];

    static float[] Endpoints(Curve c)
    {
        Point3d[] pts = c.TryGetPolyline(out var pl) ? [.. pl]
            : c.ToPolyline(0, 0, Math.PI / 90, 0, 0, 0, 0, 0, true)?.ToPolyline() is { } q ? [.. q] : [];
        var r = new float[Math.Max(0, pts.Length - 1) * 8];
        for (int i = 0; i + 1 < pts.Length; i++)
        {
            var a = pts[i]; var b = pts[i + 1];
            r[i * 8] = (float)a.X; r[i * 8 + 1] = (float)a.Y; r[i * 8 + 2] = (float)a.Z; r[i * 8 + 3] = 1;
            r[i * 8 + 4] = (float)b.X; r[i * 8 + 5] = (float)b.Y; r[i * 8 + 6] = (float)b.Z; r[i * 8 + 7] = 1;
        }
        return r;
    }

    static Curve[] Outline(RhinoObject o, Transform? rel, RhinoViewport vp, RhinoDoc doc, bool wire = false)
    {
        bool persp = vp.IsPerspectiveProjection;
        string relKey = rel is { } r ? string.Join(",", Enumerable.Range(0, 12).Select(i => r[i / 4, i % 4].ToString("G9"))) : "";
        int density = wire ? o.Attributes.WireDensity : -2;
        var key = new Key(o.Id, o.RuntimeSerialNumber, relKey, density, persp, persp ? vp.CameraLocation : Point3d.Origin, persp ? Vector3d.Zero : vp.CameraDirection);
        if (cache.TryGetValue(key, out var hit)) return hit;
        Curve[] curves = [];
        try
        {
            GeometryBase? g = o.Geometry;
            if (rel is { } t) g = Placement.Mapped(g, t); // deformable copy under non-uniform maps (fable #173)
            double tol = doc.ModelAbsoluteTolerance, angle = doc.ModelAngleToleranceRadians;
            if (g != null)
            {
                var kinds = wire ? SilhouetteType.Projecting | SilhouetteType.TangentProjects : Kinds;
                curves = (persp ? Silhouette.Compute(g, kinds, vp.CameraLocation, tol, angle)
                                : Silhouette.Compute(g, kinds, vp.CameraDirection, tol, angle))?.Select(x => x.Curve).OfType<Curve>().ToArray() ?? [];
            }
            if (wire && (g as Brep ?? (g as Extrusion)?.ToBrep()) is { } b)
            {
                var hard = b.Edges.Where(e => e.Valence != EdgeAdjacency.Interior || !e.IsSmoothManifoldEdge(angle)).Select(e => e.DuplicateCurve());
                var iso = SdfUserText.ReadType(o.Attributes) is "sdSphere" or "sdEllipsoid" ? b.GetWireframe(density) ?? [] : [];
                curves = [.. curves, .. hard, .. iso.Where(c => !c.IsShort(tol))];
            }
        }
        catch (Exception)
        {
            // Unsupported geometry: draw nothing rather than break the frame.
        }
        if (cache.Count > 4096) cache.Clear(); // stale cameras / deleted objects; a cheap bound
        cache[key] = curves;
        return curves;
    }
}
