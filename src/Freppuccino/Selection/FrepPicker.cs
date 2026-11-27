using System.Runtime.CompilerServices;
using Freppuccino.Csg;
using Freppuccino.Display;
using Freppuccino.Meshing;
using Freppuccino.Scene;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.UI;

namespace Freppuccino.Selection;

/// Owns left clicks that land on F-rep geometry in views showing the Freppuccino display mode (plan section 3).
/// Acts only when the cursor is not on a gumball handle and no command is running (or the running command opened an
/// explicit F-rep selection session, see <see cref="BeginSession"/>). Anything else, and every click that hits no
/// F-rep, is left to Rhino.
///   plain click: the nearest root whose final field the click ray hits; replaces the selection. If it hits no root
///                but does hit a control shape (an exposed cutter), that shape's root.
///   Shift+click: the nearest control instance (the leaf's own pre-warp shape under T × P, i.e. what its outline shows),
///                with the root and path it was reached through; replaces the selection. A Shift+click on nothing F-rep
///                is left to Rhino (add to selection). Coincident shapes from different roots (including one shape shared
///                by several roots, where no root's surface is hit first): no pick, a message.
/// The hit is decided on mouse-down (which is when Rhino's own pick has to be cancelled), the selection is made on
/// mouse-up, after Rhino has finished with the click.
public sealed partial class FrepPicker : MouseCallback
{
    static FrepPicker? instance;
    static int sessions;

    public static void Install() => instance ??= new FrepPicker { Enabled = true };

    /// While a session is open the picker also acts during a running command (FrepMesh's pick-blends prompt).
    public static IDisposable BeginSession()
    {
        sessions++;
        return new Session();
    }

    sealed class Session : IDisposable
    {
        bool done;
        public void Dispose()
        {
            if (done) return;
            done = true;
            sessions--;
        }
    }

    /// Raised after the picker changed the selection.
    public static event Action<RhinoDoc>? Picked;

    protected override void OnMouseDown(MouseCallbackEventArgs e)
    {
        pending = null;
        CancelBand();
        if (e.MouseButton != MouseButton.Left || e.View is not { } view) return;
        var doc = view.Document;
        if (doc == null || !IsFrepView(view.ActiveViewport)) return;
        if (e.IsOverGumball() != Rhino.UI.Gumball.GumballMode.None) return;
        if (Command.InCommand() && sessions == 0) return;

        var vp = view.ActiveViewport;
        var client = new Point2d(e.ViewportPoint.X, e.ViewportPoint.Y);
        if (sessions > 0)
        {
            SessionDown(e, view, client);
            return;
        }
        if (e.CtrlKeyDown && e.ShiftKeyDown) return;
        var mode = e.CtrlKeyDown ? PickMode.Remove : e.ShiftKeyDown ? PickMode.Leaf : PickMode.Root;
        var hits = RhinoHits(view, e.ViewportPoint);
        if (mode == PickMode.Remove) hits.AddRange(SelectedHits(view, e.ViewportPoint));

        // Leaf: the nearest control outline; shapes sharing it, or tied control volumes, all compete in the menu.
        var leaves = new List<FrepInstance>();
        if (mode == PickMode.Leaf)
        {
            var outline = PickOutline(doc, vp, client, rootsToo: false, out _, out var shared)?.Instance;
            if (shared.Count > 1) leaves.AddRange(shared);
            else if (outline != null) leaves.Add(outline);
            else leaves.AddRange(PickInstanceTies(doc, vp, client));
        }

        var candidates = Candidates(view, e.ViewportPoint, mode, leaves, hits);
        if (candidates.Count == 0 && mode == PickMode.Root)
        {
            // An exposed control (e.g. a cutter's face outside the result) selects the root it belongs to.
            var depth = 0.0;
            candidates = [.. PickInstanceTies(doc, vp, client).Select(i => i.Root).Distinct().Select(r => RootCandidate(doc, r, depth++))];
        }
        // Rhino's own pick here would also reach control shapes (cutters, added shapes, carriers, baked meshes), which
        // are not picked directly in a Freppuccino view. With those under the cursor, or any F-rep candidate, the click
        // is decided here; otherwise it is Rhino's.
        if (candidates.All(c => c.IsRhino) && hits.All(h => Ordinary(h.Object)))
        {
            // Rhino deselects its own objects on an empty click, but node roots aren't Rhino objects.
            if (candidates.Count == 0 && mode == PickMode.Root) UiController_ClearNodes(doc);
            return;
        }
        e.Cancel = true;
        if (candidates.Count == 0)
        {
            // Only control shapes under the cursor: nothing to pick, but a drag here is a window or crossing.
            band = new Band { View = view, Start = e.ViewportPoint, End = e.ViewportPoint, Mode = mode, Enabled = true };
            BandHooks(true);
            return;
        }
        pending = new Pending(doc, candidates.Count == 1 ? [candidates[0]] : candidates, mode, At: view.ClientToScreen(e.ViewportPoint));
    }

    /// In a selection session (a command asking for blends) clicks pick roots and accumulate.
    void SessionDown(MouseCallbackEventArgs e, RhinoView view, Point2d client)
    {
        var doc = view.Document;
        var vp = view.ActiveViewport;
        var hit = PickOutline(doc, vp, client, rootsToo: true, out _, out _)?.Root ?? PickRoot(doc, vp, client);
        if (hit == null && !RhinoUnder(view, e.ViewportPoint))
        {
            var inst = PickInstance(doc, vp, client, out var ambiguous);
            if (inst != null) hit = inst.Root;
            else if (ambiguous)
            {
                e.Cancel = true;
                pending = new Pending(doc, [], PickMode.Root, Ambiguous: true);
                return;
            }
        }
        if (hit is not { } root) return;
        e.Cancel = true;
        pending = new Pending(doc, [], PickMode.Root, Session: root);
    }

    sealed record Pending(RhinoDoc Doc, List<Candidate> Choices, PickMode Mode, bool Ambiguous = false, FrepOperand? Session = null, System.Drawing.Point At = default);
    Pending? pending;
    Band? band;

    /// Esc, another button or a new press abandons a drag: the rectangle goes, the selection stays as it was.
    void CancelBand()
    {
        if (band is not { } b) return;
        band = null;
        BandHooks(false);
        b.Enabled = false;
        b.View.Redraw();
    }

    /// While a drag is in progress only: Esc (Rhino's own keyboard event), Rhino losing focus, or the view or document
    /// going away end it, so a lost mouse-up can't finish it later.
    void BandHooks(bool on)
    {
        if (on)
        {
            RhinoApp.KeyboardEvent += EscapeCancelsBand;
            RhinoView.Destroy += BandViewGone;
            RhinoDoc.CloseDocument += BandDocGone;
            if (RhinoEtoApp.MainWindow is { } w) w.LostFocus += BandFocusLost;
            return;
        }
        RhinoApp.KeyboardEvent -= EscapeCancelsBand;
        RhinoView.Destroy -= BandViewGone;
        RhinoDoc.CloseDocument -= BandDocGone;
        if (RhinoEtoApp.MainWindow is { } m) m.LostFocus -= BandFocusLost;
    }

    void EscapeCancelsBand(int key)
    {
        if (key == 27) CancelBand();
    }

    void BandViewGone(object? sender, ViewEventArgs e) => CancelBand();

    void BandDocGone(object? sender, DocumentEventArgs e) => CancelBand();

    void BandFocusLost(object? sender, EventArgs e) => CancelBand();

    protected override void OnMouseMove(MouseCallbackEventArgs e)
    {
        if (band is not { } b) return;
        b.End = e.ViewportPoint;
        b.View.Redraw();
    }

    protected override void OnMouseUp(MouseCallbackEventArgs e)
    {
        if (e.MouseButton != MouseButton.Left) return;
        if (band is { } b)
        {
            band = null;
            e.Cancel = true;
            try
            {
                b.End = e.ViewportPoint;
                b.Finish();
            }
            finally
            {
                BandHooks(false);
            }
            Picked?.Invoke(b.View.Document);
            return;
        }
        if (pending is not { } p) return;
        pending = null;
        e.Cancel = true;
        if (p.Ambiguous)
        {
            RhinoApp.WriteLine("Freppuccino: several shapes share that outline. Select the one you want in the Freppuccino panel's tree.");
            return;
        }
        if (p.Session is { } r)
        {
            // In a selection session clicks accumulate (a second click on a chosen blend unchooses it).
            if (FrepSelection.Roots(p.Doc).Contains(r)) FrepSelection.DeselectRoot(p.Doc, r);
            else FrepSelection.SelectRoot(p.Doc, r, add: true);
            Picked?.Invoke(p.Doc);
            return;
        }
        if (p.Choices.Count == 1)
        {
            Apply(p.Doc, p.Choices[0], p.Mode);
            Picked?.Invoke(p.Doc);
            return;
        }
        Ui.PickMenuForm.Show(p.Doc, p.At, p.Choices, c =>
        {
            Apply(p.Doc, c, p.Mode);
            Picked?.Invoke(p.Doc);
        });
    }

    // Per scene snapshot: one single-primitive field per control instance (its canonical leaf under T × P).
    static readonly ConditionalWeakTable<FrepSceneData, List<(FrepInstance Instance, FrepField Field, (V3 Min, V3 Max) Box)>> instanceFields = new();

    static List<(FrepInstance, FrepField, (V3, V3))> InstanceFields(FrepSceneData scene) =>
        instanceFields.GetValue(scene, s =>
        {
            var result = new List<(FrepInstance, FrepField, (V3, V3))>();
            var (_, instances) = FrepInstances.Compute(s.Tree, s.Ids);
            var index = new Dictionary<Guid, int>();
            for (int i = 0; i < s.Ids.Count; i++) index[s.Ids[i]] = i;
            foreach (var inst in instances)
            {
                if (!index.TryGetValue(inst.Object, out var i)) continue;
                var prim = s.Primitives[i];
                // The leaf under its instance's root transforms T, by the same rules as a gumball edit of the leaf
                // (rigid parts fold into the values, the rest into SDF.Linear's basis).
                if (!inst.T.IsIdentity && !FrepScene.TryTransform(prim, inst.T, out prim)) continue;
                var f = FrepFieldBuilder.Single(prim);
                result.Add((inst, f, f.Bounds()));
            }
            return result;
        });

    /// Nearest control instance under the cursor. <paramref name="ambiguous"/>: the nearest hits tie between instances
    /// that aren't the same shape at the same place (coincident shapes from different roots): no pick then.
    public static FrepInstance? PickInstance(RhinoDoc doc, RhinoViewport vp, Point2d client, out bool ambiguous)
    {
        var ties = PickInstanceTies(doc, vp, client);
        ambiguous = ties.Count > 1;
        return ties.Count == 1 ? ties[0] : null;
    }

    /// The nearest control volumes under the cursor, one per distinct (root, object, placement) tied at that depth.
    public static List<FrepInstance> PickInstanceTies(RhinoDoc doc, RhinoViewport vp, Point2d client)
    {
        var scene = FrepScene.Get(doc);
        var ray = Ray(vp, client);
        var hits = new List<(FrepInstance Inst, double T)>();
        var hidden = FrepDisplaySettings.Cutters == CutterDisplay.Hidden ? ControlOutlines.CutterObjects(doc) : [];
        foreach (var (inst, field, box) in InstanceFields(scene))
        {
            if (doc.Objects.FindId(inst.Object) is not { IsLocked: false, IsHidden: false } || hidden.Contains(inst.Object)) continue;
            if (March(field, box, ray) is { } t) hits.Add((inst, t));
        }
        if (hits.Count == 0) return [];
        hits.Sort((a, b) => a.T.CompareTo(b.T));
        var best = hits[0];
        double tieTol = 4 * (ray.EpsConst + ray.EpsSlope * best.T);
        // A click resolves the actual root (and instance): equal-depth hits from different roots, objects or transforms
        // are distinct candidates, even for one shared shape at one place (agreed rule, #118). Within a root, instances
        // are already deduplicated by (object, T).
        var ties = new List<FrepInstance>();
        foreach (var h in hits.Where(h => h.T - best.T <= tieTol))
            if (!ties.Any(i => i.Root == h.Inst.Root && i.Object == h.Inst.Object && FrepInstances.Same(i.T, h.Inst.T))) ties.Add(h.Inst);
        return ties;
    }


    const double OutlinePixels = 8, RootOutlinePixels = 4, SamePixels = 1.5;
    static readonly double[] Radii = [2, 4, 8];
    static readonly Vector2d[] Ring = [.. Enumerable.Range(0, 8).Select(i => new Vector2d(Math.Cos(i * Math.PI / 4), Math.Sin(i * Math.PI / 4)))];

    public static (FrepOperand? Root, FrepInstance? Instance)? PickOutline(RhinoDoc doc, RhinoViewport vp, Point2d client, bool rootsToo, out bool ambiguous, out List<FrepInstance> shared)
    {
        ambiguous = false;
        shared = [];
        var scene = FrepScene.Get(doc);
        double limit = rootsToo ? RootOutlinePixels : OutlinePixels;
        var hits = new List<(FrepOperand Root, FrepInstance? Inst, Curve[] Curves, double Px)>();
        foreach (var (inst, curves) in ControlOutlines.Pickable(doc, vp))
            if (ScreenDistance(curves, vp, client) is var px && px <= limit) hits.Add((inst.Root, inst, curves, px));
        if (rootsToo && scene.Program != null)
        {
            var centre = Ray(vp, client);
            var radii = Radii.Where(r => r <= limit).ToArray();
            var rings = radii.Select(r => Ring.Select(d => Ray(vp, client + d * r)).Prepend(centre).ToArray()).ToArray();
            foreach (var (root, field, box) in RootFields(scene))
                for (int i = 0; i < radii.Length; i++)
                    if (OutlineHit(field, box, rings[i]) != null)
                    {
                        hits.Add((root, null, [], radii[i]));
                        break;
                    }
        }
        if (hits.Count == 0) return null;

        FrepOperand? frontRoot = rootsToo ? PickRoot(doc, vp, client) : null;
        FrepInstance? frontInst = rootsToo ? null : PickInstance(doc, vp, client, out _);
        bool Front((FrepOperand Root, FrepInstance? Inst, Curve[] Curves, double Px) h) => rootsToo
            ? h.Root == frontRoot
            : frontInst != null && h.Inst != null && h.Inst.SamePath(frontInst);
        var best = hits.OrderBy(Front).ThenBy(h => h.Px).First();
        if (rootsToo) return (best.Root, null);

        var chosen = best.Inst!;
        var probes = Radii.Where(r => r > 2).SelectMany(r => Ring.Select(d => client + d * r)).Prepend(client).ToArray();
        var same = hits.Where(h => h.Inst != null && !h.Inst.SamePath(chosen) && Front(h) == Front(best) && h.Px - best.Px <= SamePixels &&
            probes.All(q => Math.Abs(ScreenDistance(h.Curves, vp, q) - ScreenDistance(best.Curves, vp, q)) <= SamePixels)).ToList();
        if (same.Count == 0) return (null, chosen);
        var order = hits.Select(h => h.Inst).ToList();
        shared = [.. same.Select(h => h.Inst!).Append(chosen).OrderBy(order.IndexOf)];
        ambiguous = true;
        return null;
    }

    static readonly ConditionalWeakTable<Curve, Point3d[]> polylines = new();

    static double ScreenDistance(Curve[] curves, RhinoViewport vp, Point2d client)
    {
        double best = double.PositiveInfinity;
        foreach (var c in curves)
        {
            var pts = polylines.GetValue(c, k => k.TryGetPolyline(out var pl) ? [.. pl]
                : k.ToPolyline(0, 0, Math.PI / 90, 0, 0, 0, 0, 0, true)?.ToPolyline() is { } q ? [.. q] : []);
            Point2d? prev = null;
            foreach (var p in pts)
            {
                var s = vp.WorldToClient(p);
                if (prev is { } a)
                {
                    var ab = s - a;
                    double len2 = ab.X * ab.X + ab.Y * ab.Y;
                    double u = len2 > 0 ? Math.Clamp(((client.X - a.X) * ab.X + (client.Y - a.Y) * ab.Y) / len2, 0, 1) : 0;
                    best = Math.Min(best, client.DistanceTo(a + ab * u));
                }
                prev = s;
            }
        }
        return best;
    }

    static double? OutlineHit(FrepField f, (V3 Min, V3 Max) box, PickRay[] rays)
    {
        double? nearest = null;
        bool missed = false;
        foreach (var r in rays)
            if (March(f, box, r) is { } t) nearest = Math.Min(nearest ?? t, t);
            else missed = true;
        return missed ? nearest : null;
    }

    static bool RhinoUnder(RhinoView view, System.Drawing.Point at)
    {
        using var pick = new Rhino.Input.Custom.PickContext { View = view, PickStyle = Rhino.Input.Custom.PickStyle.PointPick };
        pick.SetPickTransform(view.ActiveViewport.GetPickTransform(at));
        pick.UpdateClippingPlanes();
        return view.Document.Objects.PickObjects(pick).Any(r => r.Object() is { } o && o is not FrepCarrier &&
            !ControlOutlines.IsControl(o) && !Commands.FrepMeshCommand.IsBaked(o));
    }

    static void UiController_ClearNodes(RhinoDoc doc) => Ui.UiController.SetSelectedNodes(doc, []);

    public static bool IsFrepView(RhinoViewport vp) =>
        FreppuccinoDisplayMode.Id != Guid.Empty && vp.DisplayMode?.Id == FreppuccinoDisplayMode.Id;

    // Per scene snapshot (FrepScene.Get returns a new instance whenever anything changed): one field per root.
    static readonly ConditionalWeakTable<FrepSceneData, List<(FrepOperand Root, FrepField Field, (V3 Min, V3 Max) Box)>> fields = new();

    static List<(FrepOperand Root, FrepField Field, (V3 Min, V3 Max) Box)> RootFields(FrepSceneData scene) =>
        fields.GetValue(scene, s =>
        {
            var result = new List<(FrepOperand, FrepField, (V3, V3))>();
            FrepFieldBuilder.Build(s, null, out var roots);
            foreach (var r in roots)
            {
                var f = FrepFieldBuilder.Build(s, [r], out var got);
                if (got.Count == 1) result.Add((r, f, f.Bounds()));
            }
            return result;
        });

    /// Nearest root whose field the ray through <paramref name="client"/> (viewport client coordinates) hits.
    public static FrepOperand? PickRoot(RhinoDoc doc, RhinoViewport vp, Point2d client)
    {
        var scene = FrepScene.Get(doc);
        if (scene.Program == null) return null;
        var ray = Ray(vp, client);
        FrepOperand? best = null;
        double bestT = double.PositiveInfinity;
        foreach (var (root, field, box) in RootFields(scene))
            if (March(field, box, ray, bestT) is { } t && t < bestT)
            {
                bestT = t;
                best = root;
            }
        return best;
    }

    /// Ray origin/direction plus the shader's hit tolerance (half a pixel footprint: eps = c + slope · t).
    public readonly record struct PickRay(V3 Origin, V3 Dir, double EpsConst, double EpsSlope);

    public static PickRay Ray(RhinoViewport vp, Point2d client)
    {
        var line = vp.ClientToWorld(client);
        // ClientToWorld runs far → near; march away from the camera, from the near end.
        if (line.Direction * vp.CameraDirection < 0) line.Flip();
        var dir = line.Direction;
        dir.Unitize();
        vp.GetFrustum(out var l, out var r, out _, out _, out var near, out _);
        double slope = (r - l) / Math.Max(1, vp.Size.Width) * 0.5;
        if (vp.IsPerspectiveProjection)
        {
            var o = vp.CameraLocation;
            return new PickRay(new V3(o.X, o.Y, o.Z), new V3(dir.X, dir.Y, dir.Z), 0, slope / near);
        }
        // Parallel: start well behind the scene so nothing in front of the near plane is missed.
        var from = line.From;
        return new PickRay(new V3(from.X, from.Y, from.Z), new V3(dir.X, dir.Y, dir.Z), slope, 0);
    }

    /// Sphere-traces <paramref name="f"/> along the ray inside <paramref name="box"/>; distance along the ray or null.
    public static double? March(FrepField f, (V3 Min, V3 Max) box, PickRay ray, double tLimit = double.PositiveInfinity)
    {
        if (!Slab(box, ray.Origin, ray.Dir, out var t0, out var t1)) return null;
        double t = Math.Max(t0, 0), tMax = Math.Min(t1, tLimit);
        for (int i = 0; i < 512 && t <= tMax; i++)
        {
            double eps = ray.EpsConst + ray.EpsSlope * t;
            double d = f.Value(ray.Origin + ray.Dir * t);
            if (d < eps) return t;
            t += Math.Max(d, eps * 0.5);
        }
        return null;
    }

    static bool Slab((V3 Min, V3 Max) b, V3 o, V3 d, out double t0, out double t1)
    {
        t0 = double.NegativeInfinity;
        t1 = double.PositiveInfinity;
        // Empty bounds (Min > Max somewhere, e.g. a disjoint intersection) or non-finite: nothing to hit.
        if (!(b.Min.X <= b.Max.X && b.Min.Y <= b.Max.Y && b.Min.Z <= b.Max.Z) ||
            !double.IsFinite(b.Min.X + b.Min.Y + b.Min.Z + b.Max.X + b.Max.Y + b.Max.Z))
            return false;
        // Pad the box slightly: it bounds the surface, and the hit tolerance can reach just outside it.
        double pad = 1e-9 + 1e-6 * Math.Max(b.Max.X - b.Min.X, Math.Max(b.Max.Y - b.Min.Y, b.Max.Z - b.Min.Z));
        for (int a = 0; a < 3; a++)
        {
            double lo = (a == 0 ? b.Min.X : a == 1 ? b.Min.Y : b.Min.Z) - pad, hi = (a == 0 ? b.Max.X : a == 1 ? b.Max.Y : b.Max.Z) + pad;
            double oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z, da = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
            if (Math.Abs(da) < 1e-300)
            {
                if (oa < lo || oa > hi) return false;
                continue;
            }
            double ta = (lo - oa) / da, tb = (hi - oa) / da;
            if (ta > tb) (ta, tb) = (tb, ta);
            t0 = Math.Max(t0, ta);
            t1 = Math.Min(t1, tb);
            if (t0 > t1) return false;
        }
        return true;
    }
}
