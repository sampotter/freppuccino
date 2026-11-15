using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Freppuccino.Csg;
using Freppuccino.Scene;
using Freppuccino.Sdf;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Freppuccino.Ai;

/// <summary>
/// Tools the side panel's AI agent (src/ui/src/ai) calls through the "aiTool" message. Each takes a JSON object and
/// returns one; failures throw <see cref="AiToolException"/> with a message for the model.
///
/// Objects are referred to by "ref": a primitive's name (unique names are enforced on create), its GUID, or a CSG
/// node name ("N3"). Every edit is one undo step labelled "AI: …". Shapes use <see cref="AiShapes"/> specs.
/// Tool names and argument shapes must match src/ui/src/ai/tools.ts.
/// </summary>
public static partial class AiTools
{
    public static JsonNode Run(RhinoDoc doc, string tool, JsonObject args) => tool switch
    {
        "get_scene" => GetScene(doc),
        "create_primitive" => Create(doc, args),
        "update_primitive" => Update(doc, args),
        "transform" => TransformObjects(doc, args),
        "combine" => Combine(doc, args),
        "deform" => Deform(doc, args),
        "shell" => Shell(doc, args),
        "lattice" => Lattice(doc, args),
        "pipe" => Pipe(doc, args),
        "edit_operation" => EditOperation(doc, args),
        "add_operand" => AddOperand(doc, args),
        "remove_operand" => RemoveOperand(doc, args),
        "move" => Move(doc, args),
        "delete" => Delete(doc, args),
        "set_visible" => SetVisible(doc, args),
        "set_color" => SetColors(doc, args),
        "clear_scene" => ClearScene(doc),
        "capture_views" => CaptureViews(doc, args),
        "create_function" => CreateFunction(doc, args),
        "set_function_code" => SetFunctionCode(doc, args),
        "get_function" => GetFunction(doc, args),
        "set_function_params" => SetFunctionParams(doc, args),
        "help" => Help(args),
        _ => throw new AiToolException($"unknown tool '{tool}'"),
    };

    // ───────────────────────── scene ─────────────────────────

    static JsonNode GetScene(RhinoDoc doc)
    {
        var tree = FrepTrees.Get(doc);
        var objects = SdfObjects(doc).Reverse().ToList(); // creation order
        var refs = RefNames(objects);

        var usedBy = new Dictionary<FrepOperand, List<string>>();
        foreach (var n in tree.Nodes)
            foreach (var o in n.Operands)
                (usedBy.TryGetValue(o, out var l) ? l : usedBy[o] = []).Add(n.Name);
        JsonArray UsedBy(FrepOperand o) => [.. (usedBy.GetValueOrDefault(o) ?? []).Select(s => (JsonNode)s)];

        var prims = new JsonArray();
        var bbox = BoundingBox.Empty;
        foreach (var obj in objects)
        {
            var o = FrepPrimitives.TryRead(obj.Attributes, out var p)
                ? AiShapes.ToSpec(p)
                : new JsonObject { ["type"] = SdfUserText.ReadType(obj.Attributes), ["invalid"] = true };
            o.Insert(0, "ref", refs[obj.Id]);
            o["color"] = Hex(SdfUserText.ReadColorOrDefault(obj.Attributes)); // the shader color (white when unset)
            if (obj.IsHidden) o["visible"] = false;
            var used = UsedBy(FrepOperand.Object(obj.Id));
            if (used.Count > 0) o["used_by"] = used;
            prims.Add(o);
        }

        var ops = new JsonArray();
        foreach (var n in tree.Nodes)
        {
            var o = n.Op.IsWarp() ? DescribeWarp(doc, tree, n, refs)
                : n.Op.IsModifier() ? DescribeModifier(doc, n, refs)
                : n.Op == FrepOp.Pipe ? DescribePipe(doc, n)
                : DescribeCsg(doc, n, refs);
            if (n.Hidden) o["visible"] = false;
            var used = UsedBy(FrepOperand.Node(n.Id));
            if (used.Count > 0) o["used_by"] = used;
            ops.Add(o);
        }

        var roots = tree.Nodes.Where(n => !tree.IsReferenced(FrepOperand.Node(n.Id))).Select(n => (JsonNode)n.Name)
            .Concat(objects.Where(o => !tree.IsReferenced(FrepOperand.Object(o.Id))).Select(o => (JsonNode)refs[o.Id]));
        foreach (var b in VisibleRootBounds(doc)) bbox.Union(b);

        var result = new JsonObject
        {
            ["units"] = doc.ModelUnitSystem.ToString(),
            ["primitives"] = prims,
            ["operations"] = ops,
            ["roots"] = new JsonArray([.. roots]),
        };
        if (bbox.IsValid) result["bounding_box"] = new JsonObject { ["min"] = AiShapes.Json(bbox.Min), ["max"] = AiShapes.Json(bbox.Max) };
        var drawn = FrepScene.Get(doc);
        var warnings = new List<string>();
        if (drawn.Dropped is > 0 and var dropped)
            warnings.Add($"{dropped} primitives are not drawn: the viewport shader is full. Use fewer primitives.");
        if (drawn.FailedFunctions > 0)
            warnings.Add($"{drawn.FailedFunctions} functions are not drawn: their code has errors (ok: false; see get_function).");
        if (Display.ShaderConduit.ShaderError(doc) != null)
            warnings.Add("the viewport shader failed to compile (get_function shows shader_error); the last working version is shown, if any.");
        if (warnings.Count > 0) result["warning"] = string.Join(" ", warnings);
        return result;
    }

    // ───────────────────────── primitives ─────────────────────────

    static JsonNode Create(RhinoDoc doc, JsonObject args)
    {
        var type = Str(args, "type").ToLowerInvariant();
        if (type == "function") return CreateFunction(doc, args);
        var prim = AiShapes.FromSpec(type, args);
        var name = UniqueName(doc, args["name"]?.GetValue<string>() ?? type);
        var color = AiShapes.Color(args);
        Guid id = Guid.Empty;
        Mutate(doc, $"create {name}", () => id = FrepPrimitives.Add(doc, prim, name, color));
        return new JsonObject { ["ref"] = name, ["guid"] = id.ToString("D") };
    }

    static JsonNode Update(RhinoDoc doc, JsonObject args)
    {
        var obj = ResolveObject(doc, Str(args, "ref"));
        if (!FrepPrimitives.TryRead(obj.Attributes, out var current)) throw new AiToolException("object has unreadable SDF values");

        if (current.Kind == SdKind.User && args.Select(kv => kv.Key).Where(k => k is "code" or "params" or "lipschitz" or "allow_errors").ToList() is { Count: > 0 } fnKeys)
            throw new AiToolException($"update_primitive only edits a function's clip box (center / size / axes); for {string.Join(", ", fnKeys)} " +
                                      "use set_function_code {target, code} or set_function_params {target, params?, lipschitz?}");

        // Overlay the given fields on the current description, so partial updates keep everything else.
        var spec = AiShapes.ToSpec(current);
        var type = spec["type"]!.GetValue<string>();
        foreach (var (key, value) in args)
            if (key is not ("ref" or "name" or "type" or "color")) spec[key] = value?.DeepClone();
        var prim = AiShapes.FromSpec(type, spec);
        if (current.Kind == SdKind.User) prim = prim with { Fn = current.Fn }; // code / params / L: set_function_code / _params
        if (current.Basis is { } basis)
        {
            // Stretched (SDF.Linear): the basis, not the frame axes, maps the shape to world, so a re-orientation can't
            // apply. Move / resize compose: sizes stay in the unstretched frame (as reported), the stretch stays on top.
            if (AxesChanged(current, prim))
                throw new AiToolException($"{RefOf(doc, obj)} is stretched (scaled / sheared after creation), so update_primitive can't " +
                                          "change its orientation; use transform {rotate} instead");
            var frame = current.Frame;
            frame.Origin = prim.Frame.Origin;
            prim = prim with { Frame = frame, Basis = basis };
        }

        string? newName = null;
        if (args["name"]?.GetValue<string>() is { } requested && requested != obj.Attributes.Name)
            newName = UniqueName(doc, requested);
        var label = newName ?? RefOf(doc, obj);
        var color = AiShapes.Color(args);
        Mutate(doc, $"update {label}", () => FrepPrimitives.Replace(doc, obj, prim, newName, color));

        var after = AiShapes.ToSpec(prim);
        after.Insert(0, "ref", newName ?? RefOf(doc, obj));
        if (color is { } c) after["color"] = Hex(c);
        return after;
    }

    /// Orientation changed, comparing only the axes the kind's spec carries (round-trips may re-pick the others).
    static bool AxesChanged(SdPrimitive a, SdPrimitive b)
    {
        static bool Same(Vector3d u, Vector3d v) => (u - v).Length <= 1e-3; // specs round to 4 decimals
        return a.Kind switch
        {
            SdKind.Sphere => false,
            SdKind.Cylinder or SdKind.Cone or SdKind.Torus => !Same(a.Frame.YAxis, b.Frame.YAxis),
            _ => !Same(a.Frame.XAxis, b.Frame.XAxis) || !Same(a.Frame.YAxis, b.Frame.YAxis),
        };
    }

    /// Moves / rotates / scales primitives, or every primitive under a node, with the same rules as the gumball.
    static JsonNode TransformObjects(RhinoDoc doc, JsonObject args)
    {
        var tree = FrepTrees.Get(doc);
        var targets = Refs(args, "refs").Select(r => Resolve(doc, tree, r)).ToList();
        var leaves = targets.SelectMany(t => Leaves(doc, tree, t)).Distinct().ToList();
        // Warps and pipes under the targets move with them: their reference lines / boxes / curves get the same transform.
        var subtree = new HashSet<FrepOperand>(targets);
        foreach (var t in targets) AddDescendants(tree, t, subtree);
        var warps = subtree.Where(o => o.IsNode).Select(o => tree.Find(o.NodeId)).OfType<FrepNode>().Where(n => n.Op.HasRef()).ToList();
        var pipeCurves = warps.Where(n => n.Op == FrepOp.Pipe).Select(n => doc.Objects.FindId(n.Ref)).OfType<RhinoObject>().ToList();
        if (leaves.Count == 0 && pipeCurves.Count == 0) throw new AiToolException("nothing to transform");

        var bbox = BoundingBox.Empty;
        foreach (var o in leaves.Concat(pipeCurves)) bbox.Union(o.Geometry.GetBoundingBox(true));
        var pivot = args.ContainsKey("pivot") ? new Point3d(AiShapes.Vec(args, "pivot")) : bbox.Center;

        var xf = Transform.Identity;
        if (args["scale"] is JsonArray)
        {
            var s = AiShapes.Vec(args, "scale"); // per world axis, about the pivot
            xf = Transform.Scale(new Plane(pivot, Vector3d.XAxis, Vector3d.YAxis), s.X, s.Y, s.Z);
        }
        else if (args.ContainsKey("scale"))
            xf = Transform.Scale(pivot, AiShapes.Number(args, "scale"));
        if (args["rotate"] is JsonObject rot)
        {
            var angle = RhinoMath.ToRadians(AiShapes.Number(rot, "angle_degrees"));
            var axis = rot.ContainsKey("axis") ? AiShapes.Vec(rot, "axis") : Vector3d.ZAxis;
            if (!axis.Unitize()) throw new AiToolException("rotate.axis must not be a zero vector");
            xf = Transform.Rotation(angle, axis, pivot) * xf;
        }
        if (args.ContainsKey("translate"))
            xf = Transform.Translation(AiShapes.Vec(args, "translate")) * xf;
        if (xf.IsIdentity) throw new AiToolException("give at least one of translate, rotate, scale");

        // Validate everything first so a transform the SDF can't represent changes nothing.
        var updates = new List<(RhinoObject Obj, SdPrimitive Prim)>();
        foreach (var obj in leaves)
        {
            var attributes = obj.Attributes.Duplicate();
            if (!SdfTransforms.TryApply(attributes, xf, out var reason))
                throw new AiToolException($"{RefOf(doc, obj)}: {reason}");
            if (!FrepPrimitives.TryRead(attributes, out var prim))
                throw new AiToolException($"{RefOf(doc, obj)}: transform produced invalid values");
            updates.Add((obj, prim));
        }
        Mutate(doc, "transform", () =>
        {
            foreach (var (obj, prim) in updates) FrepPrimitives.Replace(doc, obj, prim);
            foreach (var refId in warps.Select(n => n.Ref).Distinct())
                if (doc.Objects.FindId(refId) is { IsDeleted: false }) doc.Objects.Transform(refId, xf, true);
            // Lengths stored as params follow a uniform scale: repeat spacing, pipe radii, shell / lattice sizes
            // (twist / bend follow their line, pipes their curve).
            var sized = subtree.Where(o => o.IsNode).Select(o => tree.Find(o.NodeId)).OfType<FrepNode>()
                .Where(n => n.Op is FrepOp.Repeat or FrepOp.Pipe or FrepOp.Shell or FrepOp.Gyroid).ToList();
            if (args["scale"] is JsonValue && AiShapes.Number(args, "scale") is var f && f > 0 && sized.Count > 0)
            {
                var t = FrepTrees.Get(doc);
                foreach (var n in sized)
                    for (int i = 0; i < n.Op.ParamNames().Count; i++) t = t.WithParam(n.Id, i, n.Param(i) * f);
                FrepTrees.Set(doc, t, "AI: transform");
            }
        });
        return new JsonObject { ["transformed"] = new JsonArray([.. updates.Select(u => (JsonNode)RefOf(doc, u.Obj))]) };
    }

    // ───────────────────────── CSG ─────────────────────────

    static JsonNode Combine(RhinoDoc doc, JsonObject args)
    {
        var op = ParseOp(Str(args, "op"));
        var k = args.ContainsKey("k") ? Math.Max(0, AiShapes.Number(args, "k")) : 0;
        var (style, steps) = StyleArgs(args, null);
        var tree = FrepTrees.Get(doc);
        var operands = Refs(args, "operands").Select(r => Resolve(doc, tree, r)).ToList();
        if (operands.Distinct().Count() != operands.Count) throw new AiToolException("operands must be distinct");
        if (operands.Count < 2) throw new AiToolException("combine needs at least 2 operands");

        var (newTree, node) = tree.Add(op, k, operands, style, steps);
        Mutate(doc, $"{OpName(op)} {node.Name}", () => FrepTrees.Set(doc, newTree, $"AI: {OpName(op)}"));

        var result = new JsonObject { ["ref"] = node.Name };
        // Operands already used elsewhere stay drawn there too; usually a mistake, so say so.
        var shared = operands.Where(tree.IsReferenced).Select(o => OperandRef(doc, o, null)).ToList();
        if (shared.Count > 0)
            result["warning"] = $"{string.Join(", ", shared)} already belonged to another operation, so it is now used twice (DAG). " +
                                "If that wasn't intended, delete this node and combine the parent operation instead.";
        return result;
    }

    static JsonNode EditOperation(RhinoDoc doc, JsonObject args)
    {
        var tree = FrepTrees.Get(doc);
        var operand = Resolve(doc, tree, Str(args, "ref"));
        if (!operand.IsNode) throw new AiToolException("edit_operation takes an operation node (N…); use update_primitive for primitives");
        var node = tree.Find(operand.NodeId)!;
        if (node.Op.IsWarp()) return EditWarp(doc, tree, node, args);
        if (!node.Op.IsCsg() && args.ContainsKey("op") && !string.Equals(Str(args, "op"), OpName(node.Op), StringComparison.OrdinalIgnoreCase))
            throw new AiToolException($"{node.Name} is a {OpName(node.Op)}; it can't change type (delete it and create a new one)");
        if (node.Op.IsModifier()) return EditModifier(doc, tree, node, args);
        if (node.Op == FrepOp.Pipe) return EditPipe(doc, tree, node, args);
        if (args.Select(kv => kv.Key).Where(key => key is not ("ref" or "op" or "k" or "style" or "steps")).ToList() is { Count: > 0 } extra)
            throw new AiToolException($"{node.Name} is a {OpName(node.Op)}: it only takes op, k, style and steps (got {string.Join(", ", extra)}); " +
                                      "deform fields apply to twist / bend / repeat nodes");
        var op = args.ContainsKey("op") ? ParseOp(Str(args, "op")) : node.Op;
        var k = args.ContainsKey("k") ? Math.Max(0, AiShapes.Number(args, "k")) : node.K;
        var (style, steps) = StyleArgs(args, node);
        var t = tree.WithOp(node.Id, op, k).WithParam(node.Id, 0, (int)style).WithParam(node.Id, 1, steps);
        Mutate(doc, $"edit {node.Name}", () => FrepTrees.Set(doc, t, $"AI: edit {node.Name}"));
        return DescribeCsg(doc, FrepTrees.Get(doc).Find(node.Id)!, null);
    }

    /// Deletes nodes and primitives. recursive: also everything under the given nodes that nothing else uses.
    static JsonNode Delete(RhinoDoc doc, JsonObject args)
    {
        var tree = FrepTrees.Get(doc);
        var pending = Refs(args, "refs").Select(r => Resolve(doc, tree, r)).ToHashSet();
        if (args["recursive"]?.GetValue<bool>() == true)
            foreach (var o in pending.ToList()) AddDescendants(tree, o, pending);

        var deleted = new List<string>();
        Mutate(doc, "delete", () =>
        {
            var t = tree;
            bool progress;
            do
            {
                progress = false;
                foreach (var o in pending.Where(o => o.IsNode && !t.IsReferenced(o)).ToList())
                {
                    t = t.Remove(o.NodeId);
                    pending.Remove(o);
                    deleted.Add(FrepTree.NodeName(o.NodeId));
                    progress = true;
                }
            } while (progress);
            FrepTrees.Set(doc, t, "AI: delete");
            DeleteOrphanRefs(doc, t, tree.Nodes.Where(n => t.Find(n.Id) == null).Select(n => n.Ref));
            foreach (var o in pending.Where(o => !o.IsNode && !t.IsReferenced(o)).ToList())
            {
                var name = OperandRef(doc, o, null);
                if (doc.Objects.Delete(o.ObjectId, true)) deleted.Add(name);
                pending.Remove(o);
            }
        });

        var result = new JsonObject { ["deleted"] = new JsonArray([.. deleted.Select(d => (JsonNode)d)]) };
        if (pending.Count > 0)
            result["not_deleted"] = $"still used by other operations: {string.Join(", ", pending.Select(o => OperandRef(doc, o, null)))} " +
                                    "(delete the parent operation first, or pass recursive=true on the parent)";
        return result;
    }

    static JsonNode SetVisible(RhinoDoc doc, JsonObject args)
    {
        var visible = args["visible"]?.GetValue<bool>() ?? throw new AiToolException("missing 'visible' (true/false)");
        var tree = FrepTrees.Get(doc);
        var operands = Refs(args, "refs").Select(r => Resolve(doc, tree, r)).ToList();
        Mutate(doc, visible ? "show" : "hide", () =>
        {
            var t = tree;
            foreach (var o in operands)
            {
                if (o.IsNode) t = t.WithHidden(o.NodeId, !visible);
                else if (visible) doc.Objects.Show(o.ObjectId, true);
                else doc.Objects.Hide(o.ObjectId, true);
            }
            FrepTrees.Set(doc, t, "AI: visibility");
        });
        return new JsonObject { ["ok"] = true };
    }

    /// Colors primitives without touching their shape. A node ref colors every primitive under it.
    /// Either one "color" for all refs, or "colors": [{ "ref", "color" }, ...] for several colors in one undo step.
    static JsonNode SetColors(RhinoDoc doc, JsonObject args)
    {
        var tree = FrepTrees.Get(doc);
        var requests = new List<(string Ref, System.Drawing.Color Color)>();
        if (args["colors"] is JsonArray list)
        {
            foreach (var item in list)
            {
                if (item is not JsonObject entry) throw new AiToolException("'colors' must contain { \"ref\", \"color\" } objects");
                var color = AiShapes.Color(entry) ?? throw new AiToolException("every 'colors' entry needs a 'color'");
                requests.Add((Str(entry, "ref"), color));
            }
        }
        if (args.ContainsKey("refs"))
        {
            var color = AiShapes.Color(args) ?? throw new AiToolException("give 'color' with 'refs'");
            requests.AddRange(Refs(args, "refs").Select(r => (r, color)));
        }
        if (requests.Count == 0) throw new AiToolException("give 'refs' + 'color', or 'colors': [{ \"ref\", \"color\" }, ...]");

        // Resolve everything first so a bad ref changes nothing. Later entries win for a primitive listed twice.
        var targets = new Dictionary<Guid, (RhinoObject Obj, System.Drawing.Color Color)>();
        foreach (var (r, color) in requests)
        {
            var leaves = Leaves(doc, tree, Resolve(doc, tree, r)).ToList();
            if (leaves.Count == 0) throw new AiToolException($"'{r}' has no primitives to color");
            foreach (var obj in leaves) targets[obj.Id] = (obj, color);
        }

        Mutate(doc, "color", () =>
        {
            foreach (var (obj, color) in targets.Values) FrepPrimitives.SetColor(doc, obj, color);
        });
        var refs = RefNames(SdfObjects(doc).ToList());
        var colored = new JsonObject();
        foreach (var (obj, color) in targets.Values) colored[refs.GetValueOrDefault(obj.Id) ?? obj.Id.ToString("D")] = Hex(color);
        return new JsonObject { ["colored"] = colored };
    }

    static JsonNode ClearScene(RhinoDoc doc)
    {
        var objects = SdfObjects(doc).Reverse().ToList(); // creation order
        var tree = FrepTrees.Get(doc);
        Mutate(doc, "clear scene", () =>
        {
            FrepTrees.Set(doc, tree with { Nodes = [] }, "AI: clear scene");
            foreach (var o in objects) doc.Objects.Delete(o.Id, true);
            foreach (var r in AiRefObjects(doc).ToList()) doc.Objects.Delete(r.Id, true);
        });
        return new JsonObject { ["deleted_primitives"] = objects.Count, ["deleted_operations"] = tree.Nodes.Count };
    }

    // ───────────────────────── screenshots ─────────────────────────

    const int DefaultSize = 512, MaxSize = 1024;
    static readonly string[] DefaultViews = ["perspective", "front", "right", "top"];

    static JsonNode CaptureViews(RhinoDoc doc, JsonObject args)
    {
        var views = args["views"] is JsonArray a && a.Count > 0 ? a.Select(v => v!.GetValue<string>()).ToList() : [.. DefaultViews];
        foreach (var v in views)
            if (!AiCapture.IsView(v)) throw new AiToolException($"unknown view '{v}'; use: {string.Join(", ", AiCapture.ViewNames)}");
        if (views.Count > 6) throw new AiToolException("at most 6 views per call");
        var size = args.ContainsKey("size") ? (int)Math.Clamp(AiShapes.Number(args, "size"), 128, MaxSize) : DefaultSize;

        // Frame the given refs, else everything drawn; warped results (bends, Repeat copies) included.
        var tree = FrepTrees.Get(doc);
        var boxes = args["focus"] is JsonArray
            ? Refs(args, "focus").Select(r => FrepScene.WarpedBoundsOf(doc, Resolve(doc, tree, r))).Where(b => b.IsValid).ToList()
            : VisibleRootBounds(doc);
        var images = AiCapture.Capture(doc, views, size, size, boxes);
        return new JsonObject
        {
            ["views"] = new JsonArray([.. views.Select(v => (JsonNode)v)]),
            ["images"] = new JsonArray([.. images.Select(i => (JsonNode)i)]),
        };
    }

    // ───────────────────────── helpers ─────────────────────────

    /// Bounds of everything drawn: each visible root, warps applied.
    static List<BoundingBox> VisibleRootBounds(RhinoDoc doc)
    {
        var tree = FrepTrees.Get(doc);
        var roots = tree.Nodes.Where(n => !n.Hidden && !tree.IsReferenced(FrepOperand.Node(n.Id))).Select(n => FrepOperand.Node(n.Id))
            .Concat(SdfObjects(doc).Where(o => !o.IsHidden && !tree.IsReferenced(FrepOperand.Object(o.Id))).Select(o => FrepOperand.Object(o.Id)));
        return [.. roots.Select(r => FrepScene.WarpedBoundsOf(doc, r)).Where(b => b.IsValid)];
    }

    static string Hex(System.Drawing.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// One undo record per tool call.
    static void Mutate(RhinoDoc doc, string description, Action action)
    {
        var record = doc.BeginUndoRecord("AI: " + description);
        try { action(); }
        finally
        {
            if (record != 0) doc.EndUndoRecord(record);
            doc.Views.Redraw();
        }
    }

    static IEnumerable<RhinoObject> SdfObjects(RhinoDoc doc)
    {
        var settings = new ObjectEnumeratorSettings
        {
            HiddenObjects = true,
            LockedObjects = true,
            NormalObjects = true,
            IncludeLights = false,
            IncludeGrips = false,
        };
        return doc.Objects.GetObjectList(settings).Where(o => SdfUserText.ReadType(o.Attributes) != null);
    }

    /// Name if it identifies the object uniquely, else its GUID.
    static Dictionary<Guid, string> RefNames(List<RhinoObject> objects)
    {
        var counts = objects.Where(o => !string.IsNullOrEmpty(o.Attributes.Name))
            .GroupBy(o => o.Attributes.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        return objects.ToDictionary(o => o.Id, o =>
            !string.IsNullOrEmpty(o.Attributes.Name) && counts[o.Attributes.Name] == 1 && !NodeLike().IsMatch(o.Attributes.Name)
                ? o.Attributes.Name
                : o.Id.ToString("D"));
    }

    static string RefOf(RhinoDoc doc, RhinoObject obj) => RefNames(SdfObjects(doc).ToList()).GetValueOrDefault(obj.Id) ?? obj.Id.ToString("D");

    static string OperandRef(RhinoDoc doc, FrepOperand o, Dictionary<Guid, string>? refs)
    {
        if (o.IsNode) return FrepTree.NodeName(o.NodeId);
        refs ??= RefNames(SdfObjects(doc).ToList());
        return refs.GetValueOrDefault(o.ObjectId) ?? $"(deleted {o.ObjectId:D})";
    }

    [GeneratedRegex(@"^[Nn]\d+$")]
    private static partial Regex NodeLike();

    /// Unique (case-insensitive) among F-rep objects; spaces → underscores; never looks like a node name.
    static string UniqueName(RhinoDoc doc, string requested)
    {
        var name = Regex.Replace(requested.Trim(), @"\s+", "_");
        if (name.Length == 0) name = "primitive";
        if (NodeLike().IsMatch(name) || Guid.TryParse(name, out _)) name = "obj_" + name;
        var taken = SdfObjects(doc).Select(o => o.Attributes.Name).Where(n => !string.IsNullOrEmpty(n))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++)
            if (!taken.Contains($"{name}_{i}")) return $"{name}_{i}";
    }

    static FrepOperand Resolve(RhinoDoc doc, FrepTree tree, string r)
    {
        r = r.Trim();
        if (NodeLike().IsMatch(r))
        {
            var id = int.Parse(r.AsSpan(1));
            if (tree.Find(id) == null) throw new AiToolException($"no operation node '{r}'; call get_scene for current refs");
            return FrepOperand.Node(id);
        }
        return FrepOperand.Object(ResolveObject(doc, r).Id);
    }

    static RhinoObject ResolveObject(RhinoDoc doc, string r)
    {
        r = r.Trim();
        if (Guid.TryParse(r, out var g))
            return doc.Objects.FindId(g) is { IsDeleted: false } o && SdfUserText.ReadType(o.Attributes) != null
                ? o
                : throw new AiToolException($"no F-rep primitive with id {r}");
        var matches = SdfObjects(doc).Where(o => string.Equals(o.Attributes.Name, r, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new AiToolException($"no primitive named '{r}'; call get_scene for current refs"),
            _ => throw new AiToolException($"{matches.Count} primitives are named '{r}'; use the GUID from get_scene"),
        };
    }

    /// The primitives an operand stands for (itself, or every leaf under a node).
    static IEnumerable<RhinoObject> Leaves(RhinoDoc doc, FrepTree tree, FrepOperand o)
    {
        var all = new HashSet<FrepOperand> { o };
        AddDescendants(tree, o, all);
        foreach (var x in all)
            if (!x.IsNode && doc.Objects.FindId(x.ObjectId) is { IsDeleted: false } obj)
                yield return obj;
    }

    static void AddDescendants(FrepTree tree, FrepOperand o, HashSet<FrepOperand> into)
    {
        if (!o.IsNode || tree.Find(o.NodeId) is not { } n) return;
        foreach (var c in n.Operands)
            if (into.Add(c)) AddDescendants(tree, c, into);
    }

    static FrepOp ParseOp(string op) => op.ToLowerInvariant() switch
    {
        "union" => FrepOp.Union,
        "subtract" or "difference" => FrepOp.Difference,
        "intersect" or "intersection" => FrepOp.Intersect,
        _ => throw new AiToolException($"unknown op '{op}'; use union, subtract or intersect"),
    };

    static string OpName(FrepOp op) => op switch
    {
        FrepOp.Union => "union",
        FrepOp.Difference => "subtract",
        FrepOp.Intersect => "intersect",
        FrepOp.Twist => "twist",
        FrepOp.Bend => "bend",
        FrepOp.Repeat => "repeat",
        _ => op.ToString().ToLowerInvariant(),
    };

    static string Str(JsonObject args, string key) =>
        args[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
            ? s
            : throw new AiToolException($"missing '{key}' (string)");

    static List<string> Refs(JsonObject args, string key)
    {
        if (args[key] is JsonValue single && single.TryGetValue<string>(out var s)) return [s];
        if (args[key] is not JsonArray a || a.Count == 0) throw new AiToolException($"'{key}' must be a non-empty array of refs");
        return [.. a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var r) ? r : throw new AiToolException($"'{key}' must contain strings"))];
    }

    /// Parses a tool's JSON arguments (the model sometimes sends "" for no arguments).
    public static JsonObject ParseArgs(JsonElement? args) =>
        args is { ValueKind: JsonValueKind.Object } e ? JsonNode.Parse(e.GetRawText())!.AsObject() : [];
}
