using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Sdf;

/// <summary>
/// Keeps SDF.* user text in sync with the geometry when F-rep objects are transformed
/// (gumball, Move, Rotate, Scale, Mirror, Orient, ...), using RhinoDoc.BeforeTransformObjects.
///
/// In place (no copy): the source object's attributes are rewritten before Rhino transforms it.
///   Rhino copies the attributes onto the transformed replacement, and the change is part of
///   the same command's undo record, so Undo/Redo stay consistent.
/// With copy (Copy command, Alt+gumball, Array...): the originals must keep their values, so the
///   new values are remembered by the source's SDF fingerprint; matching objects added during the
///   command are queued and updated on the next Idle.
///   Limitation: that update happens outside the command's undo record, so Redo of a copy restores the
///   copies with the source's (stale) SDF values.
/// A transform that flattens the primitive leaves the values unchanged and logs a warning.
/// Shear and off-axis scale are stored as SDF.Linear.*.
/// Grip (control point) edits are not handled.
/// </summary>
public static class SdfTransformWatcher
{
    // Copy case: SDF fingerprint of a source object -> its transformed attributes.
    private static readonly Dictionary<string, ObjectAttributes> PendingCopies = new(StringComparer.Ordinal);
    // Copies seen in AddRhinoObject, waiting for Idle: (doc serial, object id, new attributes).
    private static readonly List<(uint Doc, Guid Id, ObjectAttributes Attributes)> Queued = new();
    private static bool installed;

    public static void Install()
    {
        if (installed) return;
        installed = true;
        RhinoDoc.BeforeTransformObjects += OnBeforeTransformObjects;
        RhinoDoc.AddRhinoObject += OnAddRhinoObject;
        RhinoApp.Idle += OnIdle;
    }

    public static void Uninstall()
    {
        if (!installed) return;
        installed = false;
        RhinoDoc.BeforeTransformObjects -= OnBeforeTransformObjects;
        RhinoDoc.AddRhinoObject -= OnAddRhinoObject;
        RhinoApp.Idle -= OnIdle;
        PendingCopies.Clear();
        Queued.Clear();
    }

    private static void OnBeforeTransformObjects(object? sender, RhinoTransformObjectsEventArgs e)
    {
        try
        {
            if (e.ObjectCount == 0) return;
            var xform = e.Transform;
            if (xform.IsIdentity) return;

            // Blend carriers first: their roots' matrices take the transform, and leaves Rhino moves along with them keep
            // their canonical values.
            var doc0 = e.Objects.FirstOrDefault(o => o?.Document != null)?.Document;
            var skip = doc0 == null ? new HashSet<Guid>() : Freppuccino.Selection.FrepCarriers.OnBeforeTransform(doc0, e.Objects, xform, e.ObjectsWillBeCopied);

            foreach (var obj in e.Objects)
            {
                if (obj?.Document == null || SdfUserText.ReadType(obj.Attributes) == null || skip.Contains(obj.Id)) continue;

                // A leaf under transformed roots sits at T × canonical (T: its picked or primary instance's root
                // transforms), so a world transform X is T⁻¹ X T on its canonical values.
                var local = xform;
                if (Freppuccino.Selection.FrepSelection.InstanceOf(obj.Document, obj.Id) is { } inst && !inst.T.IsIdentity && inst.T.TryGetInverse(out var ti))
                    local = ti * xform * inst.T;

                var updated = obj.Attributes.Duplicate();
                if (!SdfTransforms.TryApply(updated, local, out var reason))
                {
                    RhinoApp.WriteLine($"Freppuccino: {Describe(obj)} – F-rep values not updated: {reason}.");
                    continue;
                }

                if (e.ObjectsWillBeCopied)
                    PendingCopies[Fingerprint(obj.Attributes)] = updated;
                else
                    obj.Document.Objects.ModifyAttributes(obj, updated, true);
            }
        }
        catch (Exception ex)
        {
            RhinoApp.WriteLine("Freppuccino: SDF transform update failed: " + ex.Message);
        }
    }

    private static void OnAddRhinoObject(object? sender, RhinoObjectEventArgs e)
    {
        if (PendingCopies.Count == 0) return;
        var obj = e.TheObject;
        var doc = obj?.Document;
        if (obj == null || doc == null || doc.UndoActive || doc.RedoActive) return;
        if (SdfUserText.ReadType(obj.Attributes) == null) return;

        // Don't touch the document from inside a document event; apply on Idle.
        if (PendingCopies.TryGetValue(Fingerprint(obj.Attributes), out var updated))
            Queued.Add((doc.RuntimeSerialNumber, obj.Id, updated));
    }

    private static void OnIdle(object? sender, EventArgs e)
    {
        if (PendingCopies.Count == 0 && Queued.Count == 0) return;

        // A command that adds copies always finishes before the next Idle, so pending entries are done.
        PendingCopies.Clear();
        if (Queued.Count == 0) return;

        var redraw = new HashSet<RhinoDoc>();
        foreach (var (docSerial, id, updated) in Queued)
        {
            var doc = RhinoDoc.FromRuntimeSerialNumber(docSerial);
            var obj = doc?.Objects.FindId(id);
            if (doc == null || obj == null) continue;

            var attributes = obj.Attributes.Duplicate();
            // Drop keys the transform removed (e.g. SDF.s when a sphere became an ellipsoid).
            var keep = SdfKeys(updated).ToHashSet(StringComparer.Ordinal);
            foreach (var key in SdfKeys(attributes).Where(k => !keep.Contains(k)).ToList())
                attributes.DeleteUserString(key);
            foreach (var key in keep)
                attributes.SetUserString(key, updated.GetUserString(key));
            doc.Objects.ModifyAttributes(obj, attributes, true);
            redraw.Add(doc);
        }
        Queued.Clear();
        foreach (var doc in redraw) doc.Views.Redraw();
    }

    private static IEnumerable<string> SdfKeys(ObjectAttributes attributes)
    {
        var strings = attributes.GetUserStrings();
        return strings.AllKeys
            .Where(k => k != null && k.StartsWith(SdfUserText.Prefix, StringComparison.Ordinal))
            .Select(k => k!);
    }

    /// <summary>All SDF.* key/value pairs, sorted; identical for an object and its fresh copies.</summary>
    private static string Fingerprint(ObjectAttributes attributes)
    {
        var sb = new StringBuilder();
        foreach (var key in SdfKeys(attributes).OrderBy(k => k, StringComparer.Ordinal))
            sb.Append(key).Append('=').Append(attributes.GetUserString(key)).Append('\n');
        return sb.ToString();
    }

    private static string Describe(RhinoObject obj) =>
        string.IsNullOrEmpty(obj.Attributes.Name) ? obj.Id.ToString() : $"\"{obj.Attributes.Name}\"";
}
