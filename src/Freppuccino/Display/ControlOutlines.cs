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

    readonly record struct Key(Guid Obj, uint Serial, string Rel, bool Persp, Point3d Eye, Vector3d Dir);
    static readonly Dictionary<Key, Curve[]> cache = [];

    /// Diagnostics: the placement (relative to the real brep) each instance outline was last drawn with.
    public static readonly Dictionary<(Guid Obj, FrepOperand Root, string Path), Transform> LastDrawn = [];

    public static bool IsControl(RhinoObject? o) => o != null && SdfUserText.ReadType(o.Attributes) != null;

    /// For DisplayConduit.PreDrawObject: hide control breps in Freppuccino-mode views.
    public static void Suppress(DrawObjectEventArgs e)
    {
        if (FreppuccinoDisplayMode.IsActive(e.Display) && IsControl(e.RhinoObject)) e.DrawObject = false;
    }

    /// <paramref name="selected"/>: false = every unselected instance; true = the selected ones.
    public static void Draw(DisplayPipeline dp, RhinoDoc doc, bool selected)
    {
        var vp = dp.Viewport;
        var color = selected ? Rhino.ApplicationSettings.AppearanceSettings.SelectedObjectColor : Unselected;
        int thickness = selected ? 2 : 1;
        // Base instance transforms from the committed tree: FrepScene.Get's tree already carries a dragged root's preview
        // matrix, and the real brep still sits at its committed primary placement. Each preview is applied once below.
        var scene = Scene.FrepScene.Get(doc);
        var (roots, instances) = FrepInstances.Compute(Csg.FrepTrees.Get(doc), scene.Ids);
        var rootSet = roots.ToHashSet();
        var selectedLeaves = FrepSelection.Leaves(doc);

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
                if (objSelected == selected) Draw(dp, o, leafDrag.TryGetValue(o.Id, out var own) ? own : null, vp, doc, color, thickness);
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
                if (isSel != selected) continue;
                // The real brep B sits at the primary instance (B = T_p c); this instance shows T_inst c, previewed:
                Transform eff;
                if (inst.Root.IsNode && rootDrag.TryGetValue(inst.Root.NodeId, out var rx)) eff = rx * inst.T * primaryInv;
                else if (underMoving.Contains(o.Id)) eff = inst.T * primaryInv;             // a moving root carries it
                else if (instDrag.TryGetValue(o.Id, out var q)) eff = inst.T * q * primaryInv;
                else if (leafDrag.TryGetValue(o.Id, out var lx)) eff = inst.T * primaryInv * lx;
                else eff = inst.T * primaryInv;
                LastDrawn[(o.Id, inst.Root, string.Join(",", inst.Path))] = eff;
                Draw(dp, o, Placement.IsIdentity(eff, 1e-12) ? null : eff, vp, doc, color, thickness);
            }
        }
    }

    static void Draw(DisplayPipeline dp, RhinoObject o, Transform? rel, RhinoViewport vp, RhinoDoc doc, System.Drawing.Color color, int thickness)
    {
        foreach (var c in Outline(o, rel, vp, doc)) dp.DrawCurve(c, color, thickness);
    }

    static Curve[] Outline(RhinoObject o, Transform? rel, RhinoViewport vp, RhinoDoc doc)
    {
        bool persp = vp.IsPerspectiveProjection;
        string relKey = rel is { } r ? string.Join(",", Enumerable.Range(0, 12).Select(i => r[i / 4, i % 4].ToString("G9"))) : "";
        var key = new Key(o.Id, o.RuntimeSerialNumber, relKey, persp, persp ? vp.CameraLocation : Point3d.Origin, persp ? Vector3d.Zero : vp.CameraDirection);
        if (cache.TryGetValue(key, out var hit)) return hit;
        Silhouette[]? s = null;
        try
        {
            GeometryBase? g = o.Geometry;
            if (rel is { } t) g = Placement.Mapped(g, t); // deformable copy under non-uniform maps (fable #173)
            double tol = doc.ModelAbsoluteTolerance, angle = doc.ModelAngleToleranceRadians;
            if (g != null)
                s = persp ? Silhouette.Compute(g, Kinds, vp.CameraLocation, tol, angle)
                          : Silhouette.Compute(g, Kinds, vp.CameraDirection, tol, angle);
        }
        catch (Exception)
        {
            // Unsupported geometry: draw nothing rather than break the frame.
        }
        var curves = s?.Select(x => x.Curve).OfType<Curve>().ToArray() ?? [];
        if (cache.Count > 4096) cache.Clear(); // stale cameras / deleted objects; a cheap bound
        cache[key] = curves;
        return curves;
    }
}
