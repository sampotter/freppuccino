using Freppuccino.Csg;
using Freppuccino.Scene;
using Freppuccino.Sdf;
using Freppuccino.Ui;
using Rhino;
using Rhino.DocObjects;

namespace Freppuccino.Selection;

/// Explicit F-rep selection per document: selected roots (blends, or parentless primitives) and selected leaves
/// (control instances). Never inferred from "all of a root's leaves happen to be selected".
///
/// Stored where the panel and Rhino already keep selection, so scene and tree stay in sync without event loops:
///   node root          the panel's node selection (UiController.SelectedNodes): its tree row;
///   parentless leaf    its Rhino object is selected: its tree row;
///   leaf inside a root its Rhino object is selected (its rows), plus the instance it was picked through (a shared
///                      leaf has one per path). Native selection (window, SelAll, a panel row) means its primary instance.
/// Writers: <see cref="SelectRoot"/>, <see cref="SelectLeaf"/>, <see cref="Clear"/>.
/// Not supported in this version: window/crossing selection of whole blends (a window selects the control breps it
/// catches, as leaves); select blends by clicking them or in the tree.
public static class FrepSelection
{
    /// Per doc: leaf object → the instance it was picked through (absent = primary). Stale entries are ignored on read.
    static readonly Dictionary<uint, Dictionary<Guid, FrepInstance>> picked = [];

    /// Node roots aren't Rhino objects, so Rhino's deselect-all (Esc, click on empty space, SelNone, commands that
    /// deselect) doesn't reach them; this does, panel open or not. Also drops state of closed documents.
    public static void Install()
    {
        RhinoDoc.DeselectAllObjects += (_, e) =>
        {
            ClearNodes(e.Document);
            picked.Remove(e.Document.RuntimeSerialNumber);
        };
        // A picked path lasts only while its object stays selected: reselecting it natively (window, panel, SelLast)
        // means its primary instance. SelectLeaf records the path after selecting, so it isn't undone by this.
        RhinoDoc.DeselectObjects += (_, e) =>
        {
            // A node root's carrier deselected (natively or by us): its tree row follows.
            var gone = e.RhinoObjects.Select(FrepCarriers.NodeOf).OfType<int>().ToHashSet();
            if (gone.Count > 0) UiController.SetSelectedNodes(e.Document, UiController.SelectedNodes(e.Document).Where(n => !gone.Contains(n)));
            if (!picked.TryGetValue(e.Document.RuntimeSerialNumber, out var map)) return;
            foreach (var o in e.RhinoObjects) map.Remove(o.Id);
        };
        // A carrier selected natively (SelAll, window, SelLast): its root's tree row follows.
        RhinoDoc.SelectObjects += (_, e) =>
        {
            var added = e.RhinoObjects.Select(FrepCarriers.NodeOf).OfType<int>().ToList();
            if (added.Count > 0) UiController.SetSelectedNodes(e.Document, UiController.SelectedNodes(e.Document).Concat(added));
        };
        // Esc at the idle prompt (EscapeKeyPressed only fires during long operations).
        RhinoApp.KeyboardEvent += key =>
        {
            if (key == 27 && RhinoDoc.ActiveDoc is { } doc && !Rhino.Commands.Command.InCommand()) ClearNodes(doc);
        };
        RhinoDoc.CloseDocument += (_, e) => picked.Remove(e.Document.RuntimeSerialNumber);
        // SelNone raises DeselectAllObjects only when some Rhino object was selected.
        Rhino.Commands.Command.BeginCommand += (_, e) =>
        {
            if (e.CommandEnglishName == "SelNone" && e.Document is { } doc) ClearNodes(doc);
        };
    }

    static void ClearNodes(RhinoDoc doc)
    {
        if (UiController.SelectedNodes(doc).Count == 0) return;
        UiController.SetSelectedNodes(doc, []);
        doc.Views.Redraw();
    }

    public static (List<FrepOperand> Roots, List<FrepInstance> Instances) Structure(RhinoDoc doc)
    {
        var scene = FrepScene.Get(doc);
        return FrepInstances.Compute(scene.Tree, scene.Ids);
    }

    /// Selected roots: node roots (selected carrier, or root row selected in the panel), then selected parentless primitives.
    public static List<FrepOperand> Roots(RhinoDoc doc)
    {
        var (roots, _) = Structure(doc);
        var set = roots.ToHashSet();
        var result = UiController.SelectedNodes(doc).Select(FrepOperand.Node).Where(set.Contains).ToList();
        foreach (var o in doc.Objects.GetSelectedObjects(false, false))
        {
            var r = FrepCarriers.NodeOf(o) is { } n ? FrepOperand.Node(n) : FrepOperand.Object(o.Id);
            if (set.Contains(r) && !result.Contains(r)) result.Add(r);
        }
        return result;
    }

    /// Selected leaves inside roots (selected F-rep objects that aren't parentless), each at its picked or primary instance.
    public static List<FrepInstance> Leaves(RhinoDoc doc)
    {
        var (roots, instances) = Structure(doc);
        var rootSet = roots.ToHashSet();
        var map = picked.GetValueOrDefault(doc.RuntimeSerialNumber);
        var result = new List<FrepInstance>();
        foreach (var o in doc.Objects.GetSelectedObjects(false, false))
        {
            if (FrepCarriers.InstanceOf(doc, o) is { } viaCarrier)
            {
                result.Add(viaCarrier);
                continue;
            }
            if (SdfUserText.ReadType(o.Attributes) == null || rootSet.Contains(FrepOperand.Object(o.Id))) continue;
            if (map != null && map.TryGetValue(o.Id, out var p) && instances.Any(i => i.SamePath(p))) result.Add(p);
            else if (instances.FirstOrDefault(i => i.Object == o.Id) is { } primary) result.Add(primary);
        }
        return result;
    }

    /// The instance a leaf object is being edited through: the one it was picked through if still valid, else its
    /// primary instance. Null if it isn't a leaf of any root.
    public static FrepInstance? InstanceOf(RhinoDoc doc, Guid obj)
    {
        var scene = FrepScene.Get(doc);
        return InstanceOf(doc, obj, scene.Tree, scene.Ids);
    }

    /// Same, from a given tree and leaf list (for FrepScene itself, which mustn't call back into FrepScene.Get).
    public static FrepInstance? InstanceOf(RhinoDoc doc, Guid obj, FrepTree tree, IReadOnlyList<Guid> leaves)
    {
        var (_, instances) = FrepInstances.Compute(tree, leaves);
        if (picked.GetValueOrDefault(doc.RuntimeSerialNumber)?.TryGetValue(obj, out var p) == true && instances.Any(i => i.SamePath(p)))
            return instances.First(i => i.SamePath(p));
        return instances.FirstOrDefault(i => i.Object == obj);
    }

    /// <paramref name="add"/>: keep the current F-rep and Rhino selection; otherwise replace it.
    public static void SelectRoot(RhinoDoc doc, FrepOperand root, bool add)
    {
        if (!add) ClearAll(doc);
        if (root.IsNode)
        {
            UiController.SetSelectedNodes(doc, UiController.SelectedNodes(doc).Append(root.NodeId));
            // Its carrier carries the gumball (created now if the Idle sync hasn't yet).
            if (FrepCarriers.Ensure(doc, root.NodeId) is { } carrier) doc.Objects.Select(carrier.Id, true, true);
        }
        else doc.Objects.Select(root.ObjectId, true, true);
        RefreshGumball();
        doc.Views.Redraw();
    }

    public static void DeselectRoot(RhinoDoc doc, FrepOperand root)
    {
        if (root.IsNode)
        {
            UiController.SetSelectedNodes(doc, UiController.SelectedNodes(doc).Where(n => n != root.NodeId));
            if (FrepCarriers.Find(doc, root.NodeId) is { } c) doc.Objects.Select(c.Id, false);
        }
        else doc.Objects.Select(root.ObjectId, false);
        doc.Views.Redraw();
    }

    /// Rhino only builds the gumball for selections made by its own picking; one made from code shows none until the
    /// gumball setting changes. Cycling it rebuilds the gumball without running a command (no echo), and leaves a
    /// gumball the user switched off switched off.
    public static void RefreshGumball()
    {
        if (!Rhino.ApplicationSettings.GumballSettings.EnableGumball) return;
        Rhino.ApplicationSettings.GumballSettings.EnableGumball = false;
        Rhino.ApplicationSettings.GumballSettings.EnableGumball = true;
    }

    public static void SelectLeaf(RhinoDoc doc, FrepInstance leaf, bool add)
    {
        if (!add) ClearAll(doc);
        // A shared leaf picked where its real brep isn't (another root's transform): its own carrier holds the gumball.
        if (FrepCarriers.NeedsInstanceCarrier(doc, leaf) && FrepCarriers.EnsureInstance(doc, leaf) is { } carrier)
            doc.Objects.Select(carrier.Id, true, true);
        else
        {
            if (!picked.TryGetValue(doc.RuntimeSerialNumber, out var map)) picked[doc.RuntimeSerialNumber] = map = [];
            map[leaf.Object] = leaf;
            doc.Objects.Select(leaf.Object, true, true);
        }
        RefreshGumball();
        doc.Views.Redraw();
    }

    /// Clears F-rep roots and leaves only; other Rhino objects stay selected.
    public static void Clear(RhinoDoc doc)
    {
        UiController.SetSelectedNodes(doc, []);
        foreach (var o in doc.Objects.GetSelectedObjects(false, false))
            if (SdfUserText.ReadType(o.Attributes) != null || FrepCarriers.IsCarrier(o)) o.Select(false);
        picked.Remove(doc.RuntimeSerialNumber);
        doc.Views.Redraw();
    }

    /// A replacing click: like Rhino's own click-select, everything else is deselected too.
    static void ClearAll(RhinoDoc doc)
    {
        UiController.SetSelectedNodes(doc, []);
        doc.Objects.UnselectAll();
        picked.Remove(doc.RuntimeSerialNumber);
    }
}
