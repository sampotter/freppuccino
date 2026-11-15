using System.Runtime.InteropServices;
using Freppuccino.Csg;
using Freppuccino.Scene;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.DocObjects.Custom;
using Rhino.Geometry;
using Rhino.Input.Custom;

namespace Freppuccino.Selection;

/// The gumball carrier of a node root: an invisible mesh whose geometry is the root's world bounding box, so Rhino's
/// native gumball sits on the blend and moves/rotates/scales it (plan section 4). Selecting a node root = selecting its
/// carrier, so Rhino's own deselection (Esc, SelNone, empty click, commands) applies to roots too.
/// Never drawn, never picked by Rhino (the picker selects it), not an F-rep leaf, not baked.
[Guid("6b2f9d40-1c7e-4a58-9e3b-5d0a8c21f7e4")]
public sealed class FrepCarrier : CustomMeshObject
{
    public FrepCarrier() { }
    public FrepCarrier(Mesh m) : base(m) { }

    protected override void OnDraw(DrawEventArgs e) { }
    protected override IEnumerable<ObjRef> OnPick(PickContext context) => [];
}

/// Keeps one carrier per visible node root (created/updated/deleted on Idle, outside any undo record), and turns a
/// transform of carriers into the roots' matrices (one undo step with the command that moved them).
/// Carriers are transient: removed before a save and on open (a plain mesh tagged <see cref="Key"/> is a leftover), so
/// they never reach files or other plugins.
public static class FrepCarriers
{
    public const string Key = "FREP.Carrier";
    static bool dirty = true, saving;
    static readonly HashSet<int> reselect = [];
    static readonly HashSet<string> reselectInstances = [];

    public static void Install()
    {
        RhinoApp.Idle += (_, _) =>
        {
            if (!dirty || saving || RhinoDoc.ActiveDoc is not { } doc || Rhino.Commands.Command.InCommand()) return;
            dirty = false;
            Sync(doc);
        };
        FrepTrees.Changed += _ => dirty = true;
        Rhino.Commands.Command.EndCommand += (_, e) => { if (e.Document is { } d) Restore(d); };
        RhinoApp.Idle += (_, _) => { if (restore.Count > 0 && RhinoDoc.ActiveDoc is { } d && !Rhino.Commands.Command.InCommand()) Restore(d); };
        RhinoDoc.AddRhinoObject += (_, _) => dirty = true;
        RhinoDoc.DeleteRhinoObject += (_, e) =>
        {
            dirty = true;
            if (internalDeletes == 0) OnUserDelete(e.TheObject);
        };
        RhinoDoc.UndeleteRhinoObject += (_, _) => dirty = true;
        RhinoDoc.ReplaceRhinoObject += (_, _) => dirty = true;
        RhinoDoc.ModifyObjectAttributes += (_, _) => dirty = true;
        RhinoDoc.EndOpenDocument += (_, _) => dirty = true;
        RhinoDoc.BeginSaveDocument += (_, e) =>
        {
            saving = true;
            // Selected roots get their (rebuilt) carriers selected again after the save.
            var selected = e.Document.Objects.GetSelectedObjects(false, false).ToList();
            reselect.UnionWith(selected.Select(NodeOf).OfType<int>());
            // Selected instance carriers too: the logical leaf/path, rebuilt and reselected after the save.
            reselectInstances.UnionWith(selected.Select(o => InstanceOf(e.Document, o) is { } i ? Encode(i) : null).OfType<string>());
            RemoveAll(e.Document);
        };
        RhinoDoc.EndSaveDocument += (_, _) =>
        {
            saving = false;
            dirty = true;
        };
    }

    public static int? NodeOf(RhinoObject? o) =>
        o?.Attributes.GetUserString(Key) is { } s && int.TryParse(s, out var id) ? id : null;

    public static bool IsCarrier(RhinoObject? o) => NodeOf(o) != null || o?.Attributes.GetUserString(InstanceKey) != null;

    // ─────────────── instance carriers ───────────────

    /// A shared leaf picked through a path whose transform differs from its primary instance's: its real brep sits at the
    /// primary place, so a transient carrier at that instance holds the gumball. Tag: "object|root|path".
    public const string InstanceKey = "FREP.Instance";

    static string Encode(FrepInstance i) =>
        $"{i.Object:D}|{(i.Root.IsNode ? "N" + i.Root.NodeId : i.Root.ObjectId.ToString("D"))}|{string.Join(",", i.Path)}";

    /// The instance a carrier stands for, if it is an instance carrier and that instance still exists.
    public static FrepInstance? InstanceOf(RhinoDoc doc, RhinoObject? o)
    {
        if (o is not FrepCarrier || o.Attributes.GetUserString(InstanceKey) is not { } tag) return null;
        return FrepSelection.Structure(doc).Instances.FirstOrDefault(i => Encode(i) == tag);
    }

    /// Same, from a given structure (FrepScene itself must not call back into FrepScene.Get).
    public static FrepInstance? InstanceOf(RhinoObject? o, IReadOnlyList<FrepInstance> instances) =>
        o is FrepCarrier && o.Attributes.GetUserString(InstanceKey) is { } tag ? instances.FirstOrDefault(i => Encode(i) == tag) : null;

    /// Owner of each warp reference object: the first root (in instance order) whose subtree uses it. The commit moves
    /// a reference (and composes its A) only when its owner moves; the drag preview follows the same rule.
    public static Dictionary<Guid, FrepOperand> ReferenceOwners(FrepTree tree, IReadOnlyList<FrepOperand> roots)
    {
        var owner = new Dictionary<Guid, FrepOperand>();
        foreach (var r in roots.Where(r => r.IsNode))
            foreach (var n in Subtree(tree, r.NodeId))
                if (n.Op.HasRef() && n.Ref != Guid.Empty) owner.TryAdd(n.Ref, r);
        return owner;
    }

    /// The instance's transform relative to the real brep (which stands for the primary instance): T_inst × T_primary⁻¹.
    public static Transform? Relative(RhinoDoc doc, FrepInstance inst)
    {
        var primary = FrepSelection.Structure(doc).Instances.FirstOrDefault(i => i.Object == inst.Object);
        if (primary == null || !primary.T.TryGetInverse(out var pi)) return null;
        return inst.T * pi;
    }

    /// Whether a leaf instance needs its own carrier (its place isn't the real brep's).
    public static bool NeedsInstanceCarrier(RhinoDoc doc, FrepInstance inst) =>
        Relative(doc, inst) is { } rel && !Placement.IsIdentity(rel, 1e-9);

    /// Creates (or finds) the instance's carrier: a box mesh around the brep mapped to that instance.
    public static RhinoObject? EnsureInstance(RhinoDoc doc, FrepInstance inst)
    {
        var tag = Encode(inst);
        var settings = new ObjectEnumeratorSettings { HiddenObjects = true, LockedObjects = true, ObjectTypeFilter = ObjectType.Mesh };
        if (doc.Objects.GetObjectList(settings).FirstOrDefault(o => o is FrepCarrier && o.Attributes.GetUserString(InstanceKey) == tag) is { } found)
            return found;
        if (doc.Objects.FindId(inst.Object) is not { } leaf || Relative(doc, inst) is not { } rel) return null;
        var box = leaf.Geometry.GetBoundingBox(true);
        var corners = box.GetCorners();
        var mapped = new BoundingBox(corners.Select(c => { c.Transform(rel); return c; }));
        bool modified = doc.Modified;
        var a = new ObjectAttributes { Name = "F-rep shape instance" };
        a.SetUserString(InstanceKey, tag);
        var mesh = BoxMesh(mapped);
        var carrier = new FrepCarrier(mesh);
        doc.Objects.AddRhinoObject(carrier, mesh);
        doc.Objects.ModifyAttributes(carrier, a, true);
        doc.Modified = modified;
        return carrier;
    }

    /// The carrier of a node root (null if none yet).
    public static RhinoObject? Find(RhinoDoc doc, int nodeId) =>
        doc.Objects.GetObjectList(ObjectType.Mesh).FirstOrDefault(o => o is FrepCarrier && NodeOf(o) == nodeId);

    /// Creates the carrier now if missing (for a selection made before the next Idle).
    public static RhinoObject? Ensure(RhinoDoc doc, int nodeId)
    {
        if (Find(doc, nodeId) is { } c) return c;
        Sync(doc);
        return Find(doc, nodeId);
    }

    /// One carrier per visible node root, its geometry the root's box; leftovers (plain tagged meshes, duplicates,
    /// carriers of roots that are gone) removed. Selected carriers keep their geometry (the gumball shouldn't jump).
    static void Sync(RhinoDoc doc)
    {
        // Carriers are display helpers: adding or replacing them must not mark the document as changed.
        bool modified = doc.Modified;
        try
        {
            SyncCore(doc);
            if (reselect.Count > 0 || reselectInstances.Count > 0)
            {
                foreach (var id in reselect)
                    if (Find(doc, id) is { } c) c.Select(true, true);
                reselect.Clear();
                var instances = FrepSelection.Structure(doc).Instances;
                foreach (var tag in reselectInstances)
                    if (instances.FirstOrDefault(i => Encode(i) == tag) is { } inst && EnsureInstance(doc, inst) is { } ic) ic.Select(true, true);
                reselectInstances.Clear();
                FrepSelection.RefreshGumball();
                doc.Views.Redraw();
            }
        }
        finally { doc.Modified = modified; }
    }

    static void SyncCore(RhinoDoc doc)
    {
        var scene = FrepScene.Get(doc);
        var roots = scene.Roots.Where(r => r.IsNode).Select(r => r.NodeId).ToHashSet();
        var seen = new HashSet<int>();
        var settings = new ObjectEnumeratorSettings { HiddenObjects = true, LockedObjects = true, ObjectTypeFilter = ObjectType.Mesh };
        foreach (var o in doc.Objects.GetObjectList(settings).ToList())
        {
            // Instance carriers are transient: only while selected and their instance exists.
            if (o.Attributes.GetUserString(InstanceKey) != null)
            {
                if (o.IsSelected(false) == 0 || InstanceOf(doc, o) == null) Delete(doc, o);
                continue;
            }
            if (NodeOf(o) is not { } id) continue;
            if (o is not FrepCarrier || !roots.Contains(id) || !seen.Add(id)) { Delete(doc, o); continue; }
            if (o.IsSelected(false) > 0) continue;
            if (RootBox(scene, id) is { } box && !Near(o.Geometry.GetBoundingBox(true), Padded(box)))
                Replace(doc, o, BoxMesh(box));
        }
        foreach (var id in roots)
        {
            if (seen.Contains(id) || RootBox(scene, id) is not { } box) continue;
            var a = new ObjectAttributes { Name = $"N{id} (blend)" };
            a.SetUserString(Key, id.ToString());
            var mesh = BoxMesh(box);
            var carrier = new FrepCarrier(mesh);
            doc.Objects.AddRhinoObject(carrier, mesh);
            doc.Objects.ModifyAttributes(carrier, a, true);
        }
    }

    /// Before a save. This runs inside the Save command, so without pausing undo recording the deletions become an undo
    /// step of their own ("Undoing SaveAs" would bring back stale carriers instead of undoing the user's last edit).
    static void RemoveAll(RhinoDoc doc)
    {
        bool recording = doc.UndoRecordingEnabled;
        doc.UndoRecordingEnabled = false;
        try
        {
            var settings = new ObjectEnumeratorSettings { HiddenObjects = true, LockedObjects = true, ObjectTypeFilter = ObjectType.Mesh };
            foreach (var o in doc.Objects.GetObjectList(settings).ToList())
                if (IsCarrier(o)) Delete(doc, o);
        }
        finally { doc.UndoRecordingEnabled = recording; }
    }

    // Our own carrier deletes (sync, replace, save); anything else deleting a carrier is the user (Delete key, Cut, ...).
    static int internalDeletes;

    static void Delete(RhinoDoc doc, RhinoObject o)
    {
        // Outside an undo record this leaves no undo step.
        internalDeletes++;
        try
        {
            doc.Objects.Unlock(o.Id, true);
            doc.Objects.Delete(o.Id, true);
        }
        finally { internalDeletes--; }
    }

    /// The user deleted a carrier: delete what it stands for, in the same undo record (as a viewport delete of a leaf
    /// prunes the tree in UiController). Node carrier: the blend, i.e. its subtree's nodes, leaves and warp reference
    /// objects, except those another root still uses. Instance carrier: the leaf object. Undo/redo replay the recorded
    /// edits themselves.
    static void OnUserDelete(RhinoObject? o)
    {
        if (o is not FrepCarrier || o.Document is not { } doc || doc.UndoActive || doc.RedoActive) return;
        if (o.Attributes.GetUserString(InstanceKey) != null)
        {
            if (InstanceOf(doc, o) is { } inst && doc.Objects.FindId(inst.Object) is { IsDeleted: false })
                doc.Objects.Delete(inst.Object, true);
            return;
        }
        if (NodeOf(o) is not { } id) return;
        var tree = FrepTrees.Get(doc);
        if (tree.Find(id) == null) return;
        var dropNodes = Subtree(tree, id).Select(n => n.Id).ToHashSet();
        // Anything reachable from another root stays.
        var keepNodes = new HashSet<int>();
        var keepObjects = new HashSet<Guid>();
        foreach (var r in FrepScene.Get(doc).Roots)
        {
            if (r.IsNode && r.NodeId == id) continue;
            if (!r.IsNode) { keepObjects.Add(r.ObjectId); continue; }
            foreach (var n in Subtree(tree, r.NodeId))
            {
                keepNodes.Add(n.Id);
                foreach (var op in n.Operands) if (!op.IsNode) keepObjects.Add(op.ObjectId);
                if (n.Ref != Guid.Empty) keepObjects.Add(n.Ref);
            }
        }
        dropNodes.ExceptWith(keepNodes);
        var dropObjects = new HashSet<Guid>();
        foreach (var n in dropNodes.Select(tree.Find).OfType<FrepNode>())
        {
            foreach (var op in n.Operands) if (!op.IsNode) dropObjects.Add(op.ObjectId);
            if (n.Ref != Guid.Empty) dropObjects.Add(n.Ref);
        }
        dropObjects.ExceptWith(keepObjects);
        var next = tree.Excise(dropNodes, dropObjects);
        if (!ReferenceEquals(next, tree)) FrepTrees.Set(doc, next, "Delete");
        foreach (var g in dropObjects)
            if (doc.Objects.FindId(g) is { IsDeleted: false }) doc.Objects.Delete(g, true);
        doc.Views.Redraw();
    }

    static void Replace(RhinoDoc doc, RhinoObject o, Mesh mesh)
    {
        var a = o.Attributes.Duplicate();
        Delete(doc, o);
        var carrier = new FrepCarrier(mesh);
        doc.Objects.AddRhinoObject(carrier, mesh);
        doc.Objects.ModifyAttributes(carrier, a, true);
    }

    /// Component-wise, both boxes padded the same way (the carrier's mesh is <see cref="Padded"/>).
    static bool Near(BoundingBox a, BoundingBox b)
    {
        double tol = 1e-7 * Math.Max(1, b.Diagonal.Length);
        return Math.Abs(a.Min.X - b.Min.X) <= tol && Math.Abs(a.Min.Y - b.Min.Y) <= tol && Math.Abs(a.Min.Z - b.Min.Z) <= tol &&
               Math.Abs(a.Max.X - b.Max.X) <= tol && Math.Abs(a.Max.Y - b.Max.Y) <= tol && Math.Abs(a.Max.Z - b.Max.Z) <= tol;
    }

    /// A degenerate (flat) box still needs volume for the gumball frame.
    static BoundingBox Padded(BoundingBox b)
    {
        double pad = 1e-6 * Math.Max(1, b.Diagonal.Length);
        b.Inflate(pad);
        return b;
    }

    /// World box of a node root's surface (the field's sublevel bounds), or null if it's empty.
    public static BoundingBox? RootBox(FrepSceneData scene, int nodeId)
    {
        var f = FrepFieldBuilder.Build(scene, [FrepOperand.Node(nodeId)], out var got);
        if (got.Count == 0) return null;
        var (lo, hi) = f.Bounds();
        if (!(lo.X <= hi.X && lo.Y <= hi.Y && lo.Z <= hi.Z)) return null;
        return new BoundingBox(lo.X, lo.Y, lo.Z, hi.X, hi.Y, hi.Z);
    }

    static Mesh BoxMesh(BoundingBox b) => Mesh.CreateFromBox(Padded(b), 1, 1, 1);

    // ─────────────── transforms ───────────────

    /// Called first from SdfTransformWatcher's BeforeTransformObjects. For each carrier in the transform: its root's
    /// matrix becomes X × M (tree edit, recorded in the running command's undo record), and the root's real objects
    /// (leaves whose primary instance is in this root, warp reference objects) follow as derived display: their
    /// geometry gets X, a reference's applied transforms A become X × A; SDF values and leaf placements don't change.
    /// Returns the leaf objects the caller must not rewrite (Rhino is moving them as part of this same transform).
    public static HashSet<Guid> OnBeforeTransform(RhinoDoc doc, RhinoObject[] objects, Transform x, bool copy)
    {
        var skip = new HashSet<Guid>();
        var carriers = objects.Where(o => o is FrepCarrier && NodeOf(o) != null).ToList();
        // Root precedence first: a leaf reachable from any root moved here gets no edit of its own, not even through an
        // instance carrier (the roots' M carry the change).
        if (!copy)
        {
            var movingRoots = carriers.Select(c => NodeOf(c)!.Value).ToHashSet();
            var reachable = movingRoots.Count == 0 ? [] : FrepSelection.Structure(doc).Instances
                .Where(i => i.Root.IsNode && movingRoots.Contains(i.Root.NodeId)).Select(i => i.Object).ToHashSet();
            TransformInstances(doc, objects, x, skip, reachable);
        }
        if (carriers.Count == 0) return skip;
        if (copy)
        {
            RhinoApp.WriteLine("Freppuccino: copying a blend isn't supported yet; the copy is discarded.");
            return skip;
        }
        var tree = FrepTrees.Get(doc);
        var moving = objects.Select(o => o.Id).ToHashSet();
        var scene = FrepScene.Get(doc);
        var (_, instances) = FrepInstances.Compute(tree, scene.Ids);
        var primary = new Dictionary<Guid, FrepInstance>();
        foreach (var i in instances) primary.TryAdd(i.Object, i);

        var roots = new List<int>();
        foreach (var c in carriers)
        {
            int id = NodeOf(c)!.Value;
            if (tree.Find(id) is not { } n || !Placement.IsUsable(x * n.M)) continue;
            tree = tree.WithM(id, x * n.M);
            roots.Add(id);
        }
        if (roots.Count == 0) return skip;
        FrepTrees.Set(doc, tree, "Transform blend");
        var moved = roots.Select(FrepOperand.Node).ToHashSet();

        // Canonical values: no selected leaf reachable from a transformed root gets its own update (the root's M carries
        // the change, whichever root is the leaf's primary).
        foreach (var i in instances)
            if (moved.Contains(i.Root) && moving.Contains(i.Object)) skip.Add(i.Object);
        // Such a leaf whose primary root did NOT move is still moved by Rhino (it's in the selection): its real brep must
        // go back to its primary instance once the transform is done (sol #149).
        if (!X(x, out var xi)) xi = Transform.Identity;
        foreach (var obj in skip)
            if (primary.TryGetValue(obj, out var p) && !moved.Contains(p.Root)) restore.Add((doc.RuntimeSerialNumber, obj, xi));

        // Derived real geometry, once per object, by primary ownership: leaves whose primary instance is in a moved root.
        foreach (var (obj, inst) in primary)
        {
            if (!moved.Contains(inst.Root) || moving.Contains(obj) || doc.Objects.FindId(obj) is not { } o) continue;
            if (Placement.Mapped(o.Geometry, x) is { } g) doc.Objects.Replace(obj, g, true);
        }

        // Warp reference objects, once each, owned by the first root (in instance order) whose subtree uses them.
        var (allRoots, _) = FrepInstances.Compute(tree, scene.Ids);
        foreach (var (refId, root) in ReferenceOwners(tree, allRoots))
        {
            if (!moved.Contains(root) || doc.Objects.FindId(refId) is not { } r) continue;
            var a = r.Attributes.Duplicate();
            Placement.ReadApplied(a, out var applied);
            Placement.WriteApplied(a, x * applied);
            if (!moving.Contains(r.Id))
            {
                if (Placement.Mapped(r.Geometry, x) is { } g) doc.Objects.Replace(r.Id, g, true);
            }
            doc.Objects.ModifyAttributes(r.Id, a, true);
        }
        return skip;
    }

    /// Instance carriers in the transform: the leaf is edited through that instance, Q = T_inst⁻¹ X T_inst on its canonical
    /// values (the per-type handlers / SDF.Linear), and its real brep — the primary instance — is re-placed as
    /// T_p Q T_p⁻¹. Both in the running command's undo record. The leaf itself, if also selected, is left to this path.
    static void TransformInstances(RhinoDoc doc, RhinoObject[] objects, Transform x, HashSet<Guid> skip, HashSet<Guid> reachable)
    {
        var moving = objects.Select(o => o.Id).ToHashSet();
        foreach (var c in objects)
        {
            if (InstanceOf(doc, c) is not { } inst || doc.Objects.FindId(inst.Object) is not { } leaf) continue;
            if (reachable.Contains(inst.Object)) continue; // a moved root carries it (root precedence)
            if (!skip.Add(leaf.Id)) continue;              // once per leaf
            var primary = FrepSelection.Structure(doc).Instances.FirstOrDefault(i => i.Object == inst.Object);
            if (primary == null || !inst.T.TryGetInverse(out var ti) || !primary.T.TryGetInverse(out var pi)) continue;
            var q = ti * x * inst.T;
            var a = leaf.Attributes.Duplicate();
            if (!Sdf.SdfTransforms.TryApply(a, q, out var reason))
            {
                RhinoApp.WriteLine($"Freppuccino: shape not updated: {reason}.");
                continue;
            }
            doc.Objects.ModifyAttributes(leaf, a, true);
            var place = primary.T * q * pi; // the real brep's new place
            if (moving.Contains(leaf.Id))
            {
                // Rhino is about to move the selected brep by X itself: correct it afterwards to `place`.
                if (x.TryGetInverse(out var xi)) restore.Add((doc.RuntimeSerialNumber, leaf.Id, place * xi));
            }
            else
            {
                if (Placement.Mapped(leaf.Geometry, place) is { } g) doc.Objects.Replace(leaf.Id, g, true);
            }
        }
    }

    static bool X(Transform x, out Transform inverse) => x.TryGetInverse(out inverse);

    /// Leaves Rhino moved that must return to their primary instance: (doc, object, X⁻¹).
    static readonly List<(uint Doc, Guid Obj, Transform Undo)> restore = [];

    /// After the transforming command: inside its undo record when it is still open, so one Undo covers everything;
    /// otherwise (gumball drags) on Idle in an undo record of its own.
    static void Restore(RhinoDoc doc)
    {
        if (restore.Count == 0) return;
        var items = restore.Where(r => r.Doc == doc.RuntimeSerialNumber).ToList();
        restore.RemoveAll(r => r.Doc == doc.RuntimeSerialNumber);
        uint record = doc.UndoRecordingIsActive ? 0 : doc.BeginUndoRecord("Keep shared shape");
        foreach (var (_, obj, inv) in items)
            if (doc.Objects.FindId(obj) is { } o)
            {
                if (Placement.Mapped(o.Geometry, inv) is { } g) doc.Objects.Replace(obj, g, true);
            }
        if (record != 0) doc.EndUndoRecord(record);
        doc.Views.Redraw();
    }

    static IEnumerable<FrepNode> Subtree(FrepTree tree, int id)
    {
        var seen = new HashSet<int>();
        var stack = new Stack<int>([id]);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (!seen.Add(cur) || tree.Find(cur) is not { } n) continue;
            yield return n;
            foreach (var o in n.Operands) if (o.IsNode) stack.Push(o.NodeId);
        }
    }
}
