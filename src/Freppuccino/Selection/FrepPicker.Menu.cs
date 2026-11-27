using Freppuccino.Csg;
using Freppuccino.Display;
using Freppuccino.Scene;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Selection;

/// Overlap menu: what a click could mean, so several genuinely different things under the cursor are offered as a
/// list (like Rhino's selection menu) instead of one being chosen silently.
public sealed partial class FrepPicker
{
    public enum PickMode { Root, Leaf, Remove }

    public sealed record Candidate(FrepOperand? Root, FrepInstance? Leaf, Guid Object, double Depth, string Label)
    {
        public bool IsRhino => Root == null && Leaf == null;
    }

    static double Depth(RhinoViewport vp, Point3d p) =>
        vp.IsPerspectiveProjection ? p.DistanceTo(vp.CameraLocation) : (p - vp.CameraLocation) * vp.CameraDirection;

    static Point3d At(PickRay r, double t) => new(r.Origin.X + r.Dir.X * t, r.Origin.Y + r.Dir.Y * t, r.Origin.Z + r.Dir.Z * t);

    static bool Ordinary(RhinoObject o) => o is not FrepCarrier && !ControlOutlines.IsControl(o) && !Commands.FrepMeshCommand.IsBaked(o);

    static bool IsSurface(RhinoObject o) => o.ObjectType is ObjectType.Brep or ObjectType.Extrusion or ObjectType.Mesh or ObjectType.SubD or ObjectType.Surface;

    static bool OpaqueGeometry => FrepDisplaySettings.Geometry is GeometryDisplay.Shaded or GeometryDisplay.Arctic or GeometryDisplay.Rendered;

    /// Everything Rhino's own point pick finds under the cursor, with its depth along the view.
    static List<(RhinoObject Object, double Depth)> RhinoHits(RhinoView view, System.Drawing.Point at)
    {
        using var pick = new Rhino.Input.Custom.PickContext { View = view, PickStyle = Rhino.Input.Custom.PickStyle.PointPick };
        pick.SetPickTransform(view.ActiveViewport.GetPickTransform(at));
        pick.UpdateClippingPlanes();
        var vp = view.ActiveViewport;
        return view.Document.Objects.PickObjects(pick)
            .Where(r => r.Object() != null)
            .GroupBy(r => r.ObjectId)
            .Select(g => (g.First().Object(), g.Min(r => Depth(vp, r.SelectionPoint()))))
            .ToList();
    }

    /// Rhino's point pick skips objects that are already selected; Ctrl+click needs exactly those.
    static List<(RhinoObject Object, double Depth)> SelectedHits(RhinoView view, System.Drawing.Point at)
    {
        using var pick = new Rhino.Input.Custom.PickContext { View = view, PickStyle = Rhino.Input.Custom.PickStyle.PointPick };
        pick.SetPickTransform(view.ActiveViewport.GetPickTransform(at));
        pick.UpdateClippingPlanes();
        var vp = view.ActiveViewport;
        var result = new List<(RhinoObject, double)>();
        foreach (var o in view.Document.Objects.GetSelectedObjects(false, false))
        {
            if (!Ordinary(o)) continue;
            double best = double.PositiveInfinity;
            if (o.Geometry is Curve c && c.ToNurbsCurve() is { } n && pick.PickFrustumTest(n, out var t, out _, out _))
                best = Depth(vp, c.PointAt(t));
            var meshes = o.Geometry is Mesh m ? [m] : o.GetMeshes(MeshType.Render) ?? [];
            foreach (var mesh in meshes)
                if (mesh != null && pick.PickFrustumTest(mesh, Rhino.Input.Custom.PickContext.MeshPickStyle.ShadedModePicking, out var hit, out _, out _, out _, out _))
                    best = Math.Min(best, Depth(vp, hit));
            if (!double.IsPositiveInfinity(best)) result.Add((o, best));
        }
        return result;
    }

    /// Nearest root surface along the ray and its depth.
    static (FrepOperand Root, double Depth)? RootHit(RhinoDoc doc, RhinoViewport vp, PickRay ray)
    {
        var scene = FrepScene.Get(doc);
        if (scene.Program == null) return null;
        (FrepOperand Root, double T)? best = null;
        foreach (var (root, field, box) in RootFields(scene))
            if (March(field, box, ray, best?.T ?? double.PositiveInfinity) is { } t && (best == null || t < best.Value.T))
                best = (root, t);
        return best is { } b ? (b.Root, Depth(vp, At(ray, b.T))) : null;
    }

    /// Depth of the curve under the cursor: on the segment drawn nearest it, at the point drawn nearest the cursor
    /// (screen-space parameter, mapped back to the segment with the perspective divide).
    static double CurveDepth(Curve[] curves, RhinoViewport vp, Point2d client)
    {
        double best = double.PositiveInfinity, depth = double.PositiveInfinity;
        var cam = vp.CameraLocation;
        var dir = vp.CameraDirection;
        dir.Unitize();
        foreach (var c in curves)
        {
            var pts = polylines.GetValue(c, k => k.TryGetPolyline(out var pl) ? [.. pl]
                : k.ToPolyline(0, 0, Math.PI / 90, 0, 0, 0, 0, 0, true)?.ToPolyline() is { } q ? [.. q] : []);
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                Point2d a = vp.WorldToClient(pts[i]), b = vp.WorldToClient(pts[i + 1]);
                var ab = b - a;
                double len2 = ab.X * ab.X + ab.Y * ab.Y;
                double u = len2 > 0 ? Math.Clamp(((client.X - a.X) * ab.X + (client.Y - a.Y) * ab.Y) / len2, 0, 1) : 0;
                double d = client.DistanceTo(a + ab * u);
                if (d >= best) continue;
                best = d;
                double s = u;
                if (vp.IsPerspectiveProjection)
                {
                    double za = (pts[i] - cam) * dir, zb = (pts[i + 1] - cam) * dir;
                    double den = zb + u * (za - zb);
                    if (Math.Abs(den) > 1e-12) s = Math.Clamp(u * za / den, 0, 1);
                }
                depth = Depth(vp, pts[i] + (pts[i + 1] - pts[i]) * s);
            }
        }
        return depth;
    }

    static double Tol(double depth) => 1e-3 * Math.Max(1.0, Math.Abs(depth));

    /// <paramref name="a"/> clearly in front of <paramref name="b"/>; nothing is in front of "no hit" (+inf) but a hit.
    static bool InFront(double a, double b) => double.IsPositiveInfinity(b) ? !double.IsPositiveInfinity(a) : a < b - Tol(b);

    /// <paramref name="a"/> not behind <paramref name="b"/>.
    static bool NotBehind(double a, double b) => double.IsPositiveInfinity(b) || a <= b + Tol(b);

    /// The candidates at a click, already visible-filtered and merged: one per root (or leaf) and per Rhino object,
    /// nearest first. <paramref name="leaves"/>: the Shift picks already decided by the outline rules.
    static List<Candidate> Candidates(RhinoView view, System.Drawing.Point at, PickMode mode, IReadOnlyList<FrepInstance> leaves, List<(RhinoObject Object, double Depth)> hits)
    {
        var doc = view.Document;
        var vp = view.ActiveViewport;
        var client = new Point2d(at.X, at.Y);
        var ray = Ray(vp, client);
        var rootHit = RootHit(doc, vp, ray);
        double frepD = rootHit?.Depth ?? double.PositiveInfinity;
        var surfaces = hits.Where(h => Ordinary(h.Object) && IsSurface(h.Object)).ToList();
        double anyRhinoSurface = FrepDisplaySettings.Geometry == GeometryDisplay.Wireframe ? double.PositiveInfinity
            : surfaces.Select(h => h.Depth).DefaultIfEmpty(double.PositiveInfinity).Min();
        double opaque = OpaqueGeometry ? anyRhinoSurface : double.PositiveInfinity;
        var result = new Dictionary<string, Candidate>();

        void Add(string key, Candidate c)
        {
            if (!result.TryGetValue(key, out var have) || c.Depth < have.Depth) result[key] = c;
        }

        bool FrepVisible(double d) => NotBehind(d, opaque);

        // The outline pass's rule: a cutter line hides only behind Rhino depth in front of both it and the F-rep; an added
        // shape's line shows only where it is in front of the F-rep and of Rhino geometry.
        var cutters = ControlOutlines.CutterObjects(doc);
        bool ControlVisible(FrepInstance inst, double d) => cutters.Contains(inst.Object)
            ? !(InFront(anyRhinoSurface, d) && InFront(anyRhinoSurface, frepD))
            : NotBehind(d, frepD) && NotBehind(d, anyRhinoSurface);

        if (mode != PickMode.Leaf)
        {
            if (rootHit is { } rh && FrepVisible(rh.Depth)) Add(RootKey(rh.Root), RootCandidate(doc, rh.Root, rh.Depth));
            var scene = FrepScene.Get(doc);
            if (scene.Program != null)
            {
                var rings = Radii.Where(r => r <= RootOutlinePixels).Select(r => Ring.Select(d => Ray(vp, client + d * r)).Prepend(ray).ToArray()).ToArray();
                foreach (var (root, field, box) in RootFields(scene))
                    foreach (var ring in rings)
                        if (OutlineHit(field, box, ring) is { } t)
                        {
                            double d = Depth(vp, At(ray, t));
                            bool front = rootHit is not { } h || h.Root == root || NotBehind(d, frepD);
                            if (front && FrepVisible(d)) Add(RootKey(root), RootCandidate(doc, root, d));
                            break;
                        }
            }
            foreach (var (inst, curves) in ControlOutlines.Pickable(doc, vp))
            {
                if (ScreenDistance(curves, vp, client) > RootOutlinePixels) continue;
                double d = CurveDepth(curves, vp, client);
                if (ControlVisible(inst, d)) Add(RootKey(inst.Root), RootCandidate(doc, inst.Root, d));
            }
        }
        else
            foreach (var l in leaves)
            {
                double d = LeafDepth(doc, vp, client, ray, l);
                if (ControlVisible(l, d)) Add(LeafKey(l), LeafCandidate(doc, l, d, withRoot: leaves.Count(x => x.Object == l.Object) > 1));
            }

        foreach (var (o, d) in hits)
        {
            if (!Ordinary(o)) continue;
            double others = surfaces.Where(s => s.Object.Id != o.Id).Select(s => s.Depth).DefaultIfEmpty(double.PositiveInfinity).Min();
            double occluder = Math.Min(frepD, OpaqueGeometry ? others : double.PositiveInfinity);
            if (NotBehind(d, occluder)) Add(o.Id.ToString(), new Candidate(null, null, o.Id, d, RhinoLabel(o)));
        }

        var list = result.Values.ToList();
        if (mode == PickMode.Remove)
        {
            var roots = FrepSelection.Roots(doc);
            var selectedLeaves = FrepSelection.Leaves(doc);
            list = list.Where(c => c.Root is { } r ? c.Leaf == null && roots.Contains(r) : c.Leaf is { } l ? selectedLeaves.Any(x => x.SamePath(l)) : doc.Objects.FindId(c.Object)?.IsSelected(false) > 0).ToList();
            foreach (var l in selectedLeaves)
                if (!list.Any(c => c.Leaf != null && c.Leaf.SamePath(l)) && ControlOutlines.Pickable(doc, vp).FirstOrDefault(p => p.Instance.SamePath(l)) is { Curves: { } cs } && ScreenDistance(cs, vp, client) <= OutlinePixels)
                    list.Add(LeafCandidate(doc, l, CurveDepth(cs, vp, client)));
        }
        return [.. list.OrderBy(c => c.Depth)];
    }

    static double LeafDepth(RhinoDoc doc, RhinoViewport vp, Point2d client, PickRay ray, FrepInstance leaf)
    {
        if (ControlOutlines.Pickable(doc, vp).FirstOrDefault(p => p.Instance.SamePath(leaf)) is { Curves: { } cs } && ScreenDistance(cs, vp, client) <= OutlinePixels)
            return CurveDepth(cs, vp, client);
        foreach (var (inst, field, box) in InstanceFields(FrepScene.Get(doc)))
            if (inst.SamePath(leaf) && March(field, box, ray) is { } t) return Depth(vp, At(ray, t));
        return double.PositiveInfinity;
    }

    static string RootKey(FrepOperand r) => r.IsNode ? "n" + r.NodeId : "o" + r.ObjectId;

    static string LeafKey(FrepInstance l) => "l" + RootKey(l.Root) + "/" + l.Object + "/" + string.Join(",", l.Path);

    static Candidate RootCandidate(RhinoDoc doc, FrepOperand root, double depth) =>
        new(root, null, root.IsNode ? Guid.Empty : root.ObjectId, depth, TreeLabel(doc, root, cutter: false));

    /// <paramref name="withRoot"/>: one shape reached through several roots; the row names the root it is reached through.
    static Candidate LeafCandidate(RhinoDoc doc, FrepInstance leaf, double depth, bool withRoot = false)
    {
        string label = TreeLabel(doc, FrepOperand.Object(leaf.Object), ControlOutlines.CutterObjects(doc).Contains(leaf.Object));
        if (withRoot) label += ", in " + TreeLabel(doc, leaf.Root, cutter: false);
        return new(null, leaf, leaf.Object, depth, label);
    }

    /// The row reads as the panel's tree does: its label, then its type in plain words.
    static string TreeLabel(RhinoDoc doc, FrepOperand o, bool cutter)
    {
        var (label, type) = Ui.UiController.TreeLabels(doc).TryGetValue(Ui.UiController.TreeKey(o), out var l) ? l : (o.IsNode ? $"N{o.NodeId}" : "Shape", "shape");
        type = type.ToLowerInvariant();
        return cutter ? $"{label} (cutter, {type})" : $"{label} ({type})";
    }

    static string RhinoLabel(RhinoObject o)
    {
        string kind = o.ShortDescription(false);
        return !string.IsNullOrWhiteSpace(o.Name) ? $"{o.Name} ({kind})" : kind;
    }

    static void Apply(RhinoDoc doc, Candidate c, PickMode mode)
    {
        if (mode == PickMode.Remove)
        {
            if (c.Leaf != null) FrepSelection.DeselectLeaf(doc, c.Leaf);
            else if (c.Root is { } r) FrepSelection.DeselectRoot(doc, r);
            else doc.Objects.Select(c.Object, false);
            doc.Views.Redraw();
            return;
        }
        if (c.Leaf != null) FrepSelection.SelectLeaf(doc, c.Leaf, add: false);
        else if (c.Root is { } r) FrepSelection.SelectRoot(doc, r, add: false);
        else
        {
            if (mode == PickMode.Root)
            {
                doc.Objects.UnselectAll();
                UiController_ClearNodes(doc);
            }
            doc.Objects.Select(c.Object, true, true);
            FrepSelection.RefreshGumball();
            doc.Views.Redraw();
        }
    }

    /// Our own window / crossing drag, for the one case where Rhino would grab a hidden cutter at mouse-down.
    sealed class Band : DisplayConduit
    {
        public required RhinoView View;
        public required System.Drawing.Point Start;
        public System.Drawing.Point End;
        public PickMode Mode;

        public bool Dragged => Math.Abs(End.X - Start.X) > 3 || Math.Abs(End.Y - Start.Y) > 3;
        bool Crossing => End.X < Start.X;

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (e.Viewport.Id != View.ActiveViewport.Id || !Dragged) return;
            var color = System.Drawing.Color.Black;
            var a = new Point2d(Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y));
            var b = new Point2d(Math.Max(Start.X, End.X), Math.Max(Start.Y, End.Y));
            Point2d[] corners = [a, new(b.X, a.Y), b, new(a.X, b.Y)];
            for (int i = 0; i < 4; i++)
            {
                var p = corners[i];
                var q = corners[(i + 1) % 4];
                if (!Crossing)
                {
                    e.Display.Draw2dLine(new System.Drawing.PointF((float)p.X, (float)p.Y), new System.Drawing.PointF((float)q.X, (float)q.Y), color, 1);
                    continue;
                }
                double len = p.DistanceTo(q);
                for (double s = 0; s < len; s += 8)
                {
                    var u = p + (q - p) * (s / len);
                    var v = p + (q - p) * (Math.Min(s + 4, len) / len);
                    e.Display.Draw2dLine(new System.Drawing.PointF((float)u.X, (float)u.Y), new System.Drawing.PointF((float)v.X, (float)v.Y), color, 1);
                }
            }
        }

        public void Finish()
        {
            Enabled = false;
            var doc = View.Document;
            if (!Dragged)
            {
                if (Mode == PickMode.Root)
                {
                    doc.Objects.UnselectAll();
                    UiController_ClearNodes(doc);
                }
                doc.Views.Redraw();
                return;
            }
            var rect = System.Drawing.Rectangle.FromLTRB(Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y), Math.Max(Start.X, End.X), Math.Max(Start.Y, End.Y));
            using var pick = new Rhino.Input.Custom.PickContext
            {
                View = View,
                PickStyle = Crossing ? Rhino.Input.Custom.PickStyle.CrossingPick : Rhino.Input.Custom.PickStyle.WindowPick,
            };
            pick.SetPickTransform(View.ActiveViewport.GetPickTransform(rect));
            pick.UpdateClippingPlanes();
            var ids = doc.Objects.PickObjects(pick).Select(r => r.ObjectId).Distinct().ToList();
            if (Mode == PickMode.Root)
            {
                doc.Objects.UnselectAll();
                UiController_ClearNodes(doc);
            }
            foreach (var id in ids) doc.Objects.Select(id, Mode != PickMode.Remove, true);
            FrepSelection.RefreshGumball();
            doc.Views.Redraw();
        }
    }
}
