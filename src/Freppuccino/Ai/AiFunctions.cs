using System.Globalization;
using System.Text.Json.Nodes;
using Freppuccino.Commands;
using Freppuccino.Scene;
using Freppuccino.UserFn;
using Rhino;
using Rhino.DocObjects;

namespace Freppuccino.Ai;

/// <summary>
/// FFunction tools (user-written SDF code clipped by a box; DSL: UserFn/, guide: .claude/skills/freppuccino-ffunction/SKILL.md):
///   create_function      {name?, center, size, x_axis?, y_axis?, code, params?: {name: value}, lipschitz?, color?, allow_errors?}
///   set_function_code    {target, code, allow_errors?}
///   get_function         {target}
///   set_function_params  {target, params?: {name: value}, lipschitz?}
///   help                 {topic?}: the guide + every tool, for callers outside the panel
/// Code with errors is rejected (nothing written) unless allow_errors; a function whose code doesn't compile isn't drawn.
/// <see cref="RunJson"/> is the string-in / string-out entry point for run_csharp (reflection) callers.
/// </summary>
public static partial class AiTools
{
    static JsonNode CreateFunction(RhinoDoc doc, JsonObject args)
    {
        var box = AiShapes.FromSpec("function", args);
        var code = Str(args, "code");
        var result = UserFnCompiler.Compile(code);
        if (!result.Ok && !AllowErrors(args)) throw CodeErrors(result, "nothing created");
        double L = args.ContainsKey("lipschitz") ? Lipschitz(args) : 1;
        var raw = ParamArgs(args, result.Program, []);
        var prim = box with { Fn = FFunctionCommand.Instance(code, L, raw) };
        var name = UniqueName(doc, args["name"]?.GetValue<string>() ?? "function");
        var color = AiShapes.Color(args);
        Guid id = Guid.Empty;
        Mutate(doc, $"create {name}", () => id = FrepPrimitives.Add(doc, prim, name, color, raw)); // raw: kept verbatim if the code fails
        var o = Describe(prim.Fn!, false);
        o.Insert(0, "ref", name);
        o.Insert(1, "guid", id.ToString("D"));
        return o;
    }

    static JsonNode SetFunctionCode(RhinoDoc doc, JsonObject args)
    {
        var (obj, prim) = ResolveFunction(doc, args);
        var code = Str(args, "code");
        var result = UserFnCompiler.Compile(code);
        if (!result.Ok && !AllowErrors(args)) throw CodeErrors(result, "code not changed");
        // Values carry over by param name; new params start at their default.
        var fn = FFunctionCommand.Instance(code, prim.Fn!.L, FFunctionCommand.RawParams(obj.Attributes));
        var label = RefOf(doc, obj);
        Mutate(doc, $"code {label}", () => FrepPrimitives.Replace(doc, obj, prim with { Fn = fn }));
        var o = Describe(fn, false);
        o.Insert(0, "ref", label);
        return o;
    }

    static JsonNode GetFunction(RhinoDoc doc, JsonObject args)
    {
        var (obj, prim) = ResolveFunction(doc, args);
        var o = AiShapes.ToSpec(prim);
        foreach (var key in new[] { "ok", "error", "lipschitz", "params" }) o.Remove(key);
        o.Insert(0, "ref", RefOf(doc, obj));
        if (prim.Basis is { } basis)
            o["linear"] = new JsonArray(AiShapes.Json(basis.X), AiShapes.Json(basis.Y), AiShapes.Json(basis.Z));
        foreach (var (key, value) in Describe(prim.Fn!, true)) o[key] = value?.DeepClone();
        if (!obj.Visible) o["visible"] = false;
        if (Display.ShaderConduit.ShaderError(doc) is { } shaderError) o["shader_error"] = shaderError; // the doc's current scene program
        return o;
    }

    static JsonNode SetFunctionParams(RhinoDoc doc, JsonObject args)
    {
        var (obj, prim) = ResolveFunction(doc, args);
        var fn = prim.Fn!;
        if (fn.Program is not { } program)
            throw new AiToolException("the function's code has errors, so its params are unknown; fix it with set_function_code first");
        if (!args.ContainsKey("params") && !args.ContainsKey("lipschitz")) throw new AiToolException("give params and/or lipschitz");
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < program.Params.Length && i < fn.Values.Length; i++)
            current[program.Params[i].Name] = fn.Values[i].ToString("R", CultureInfo.InvariantCulture);
        var raw = ParamArgs(args, program, current);
        double L = args.ContainsKey("lipschitz") ? Lipschitz(args) : fn.L;
        var next = FFunctionCommand.Instance(fn.Code, L, raw);
        var label = RefOf(doc, obj);
        Mutate(doc, $"params {label}", () => FrepPrimitives.Replace(doc, obj, prim with { Fn = next }));
        var o = Describe(next, false);
        o.Insert(0, "ref", label);
        return o;
    }

    // ───────────────────────── help ─────────────────────────

    /// One line per tool (all of AiTools.Run). Keep in sync with Run and src/ui/src/ai/tools.ts.
    static readonly (string Name, string Description)[] ToolIndex =
    [
        ("help", "{topic?: 'tools'} this guide (FFunction DSL + workflows) and the tool list"),
        ("get_scene", "{} primitives (functions: type 'function', ok), operation nodes, roots, bounding box, units"),
        ("create_primitive", "{type, name, ...shape fields, color?} sphere / box / ellipsoid / cylinder / cone / torus / pyramid"),
        ("update_primitive", "{ref, name?, ...shape fields} edit shape fields in place (functions: clip box center/size/axes)"),
        ("create_function", "{name?, center, size, x_axis?, y_axis?, code, params?, lipschitz?, color?, allow_errors?} user SDF clipped by a box"),
        ("set_function_code", "{target, code, allow_errors?} replace a function's code; returns diagnostics, params, hash"),
        ("get_function", "{target} code, params (name/default/value), lipschitz, box, diagnostics, hash, shader_error"),
        ("set_function_params", "{target, params?: {name: value}, lipschitz?} change param values / Lipschitz scale"),
        ("transform", "{refs, translate?, rotate?: {angle_degrees, axis?}, scale?, pivot?} move / rotate / scale (functions stretch with scale)"),
        ("combine", "{op: union|subtract|intersect, operands, k?} new CSG node (N#)"),
        ("deform", "{op: twist|bend|repeat, target, ...} wrap a primitive or node in a warp node"),
        ("edit_operation", "{ref, op?, k?, ...deform fields} change a node"),
        ("add_operand", "{node, operand, index?} add an operand to an existing CSG node"),
        ("remove_operand", "{node, operand} take an operand out of a node (it becomes a root if unused)"),
        ("move", "{item, to?, from?, index?} re-parent a primitive or node (no 'to' = make it a root)"),
        ("delete", "{refs, recursive?} delete primitives / nodes"),
        ("set_visible", "{refs, visible} show / hide"),
        ("set_color", "{refs, color} | {colors: [{ref, color}]} shader color (SDF.Color); a node colors every primitive under it"),
        ("clear_scene", "{} delete every F-rep primitive and operation"),
        ("capture_views", "{views?, focus?, size?} screenshots (RunJson saves them as PNG files and returns their paths)"),
    ];

    static JsonNode Help(JsonObject args)
    {
        var tools = new JsonArray([.. ToolIndex.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["description"] = t.Description })]);
        if (args["topic"]?.GetValue<string>() is "tools") return new JsonObject { ["tools"] = tools };
        return new JsonObject { ["guide"] = Guide.Value, ["tools"] = tools };
    }

    /// The FFunction guide (single source: .claude/skills/freppuccino-ffunction/SKILL.md, embedded by the csproj), frontmatter stripped.
    static readonly Lazy<string> Guide = new(() =>
    {
        using var stream = typeof(AiTools).Assembly.GetManifestResourceStream("Freppuccino.ffunction.md");
        if (stream == null) return "(guide not embedded in this build)";
        var text = new StreamReader(stream).ReadToEnd().Replace("\r\n", "\n");
        if (text.StartsWith("---\n") && text.IndexOf("\n---\n", 4, StringComparison.Ordinal) is var end and > 0) text = text[(end + 5)..];
        return text.Trim();
    });

    // ───────────────────────── external entry ─────────────────────────

    /// <summary>
    /// For callers that can only pass strings (Rhino MCP run_csharp via reflection): runs <paramref name="tool"/> on the
    /// active document, on Rhino's UI thread (marshals and waits up to 2 min if called from another thread; skipped if it hasn't
    /// started by then). Returns the result JSON, or {"error": "..."}. capture_views images are written to PNG files and returned as "image_files" paths.
    /// </summary>
    public static string RunJson(string tool, string argsJson = "{}")
    {
        string Exec()
        {
            try
            {
                var doc = RhinoDoc.ActiveDoc ?? throw new AiToolException("no active Rhino document");
                var args = string.IsNullOrWhiteSpace(argsJson) ? [] : JsonNode.Parse(argsJson) as JsonObject
                    ?? throw new AiToolException("arguments must be a JSON object");
                var result = Run(doc, tool, args);
                if (result is JsonObject o && o["images"] is JsonArray images)
                {
                    o.Remove("images");
                    o["image_files"] = SaveImages(images, o["views"] as JsonArray);
                }
                return result.ToJsonString(RunJsonOptions);
            }
            catch (Exception ex)
            {
                return new JsonObject { ["error"] = ex is AiToolException ? ex.Message : ex.ToString() }.ToJsonString(RunJsonOptions);
            }
        }
        if (!RhinoApp.InvokeRequired) return Exec(); // already on the UI thread: inline
        string? output = null;
        int state = 0; // 0 queued, 1 running, 2 abandoned (caller timed out)
        using var done = new ManualResetEventSlim();
        RhinoApp.InvokeOnUiThread((Action)(() =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return; // caller gave up: don't run it late
            try { output = Exec(); }
            finally { done.Set(); }
        }));
        if (done.Wait(TimeSpan.FromMinutes(2))) return output!;
        if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
            return new JsonObject { ["error"] = "timed out waiting for Rhino's UI thread; the tool did not run" }.ToJsonString(RunJsonOptions);
        done.Wait(); // started just now: let it finish so the caller sees what it did
        return output!;
    }

    /// Readable output for external callers (no \u0027 / \u002B escapes).
    static readonly System.Text.Json.JsonSerializerOptions RunJsonOptions =
        new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// data:image/png;base64 URLs → files under %TEMP%/freppuccino-captures; returns their paths.
    static JsonArray SaveImages(JsonArray images, JsonArray? views)
    {
        var dir = Path.Combine(Path.GetTempPath(), "freppuccino-captures");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var paths = new JsonArray();
        for (int i = 0; i < images.Count; i++)
        {
            var url = images[i]?.GetValue<string>() ?? "";
            var comma = url.IndexOf(',');
            var view = views?.ElementAtOrDefault(i)?.GetValue<string>() ?? $"view{i}";
            var path = Path.Combine(dir, $"{stamp}-{view}.png");
            File.WriteAllBytes(path, Convert.FromBase64String(url[(comma + 1)..]));
            paths.Add(path);
        }
        return paths;
    }

    // ───────────────────────── helpers ─────────────────────────

    static (RhinoObject Obj, SdPrimitive Prim) ResolveFunction(RhinoDoc doc, JsonObject args)
    {
        var obj = ResolveObject(doc, Str(args, "target"));
        if (!FrepPrimitives.TryRead(obj.Attributes, out var prim)) throw new AiToolException("object has unreadable SDF values");
        if (prim.Kind != SdKind.User || prim.Fn == null) throw new AiToolException($"{RefOf(doc, obj)} is a {AiShapes.TypeName(prim.Kind)}, not a function");
        return (obj, prim);
    }

    /// ok, diagnostics, hash, lipschitz, params [{name, default, value}]; + code when <paramref name="withCode"/>.
    static JsonObject Describe(UserFnInstance fn, bool withCode)
    {
        var o = new JsonObject { ["ok"] = fn.Result.Ok };
        if (withCode) o["code"] = fn.Code;
        o["diagnostics"] = new JsonArray([.. fn.Result.Diagnostics.Select(d =>
            (JsonNode)new JsonObject { ["line"] = d.Line, ["column"] = d.Column, ["message"] = d.Message })]);
        if (fn.Program is { } program)
        {
            o["hash"] = program.Hash;
            o["params"] = new JsonArray([.. program.Params.Select((p, i) => (JsonNode)new JsonObject
            {
                ["name"] = p.Name,
                ["default"] = p.Default,
                ["value"] = i < fn.Values.Length ? fn.Values[i] : p.Default,
            })]);
        }
        else o["note"] = "not drawn until the code compiles";
        o["lipschitz"] = fn.L;
        return o;
    }

    static AiToolException CodeErrors(UserFnResult result, string what) =>
        new($"code has errors, {what} (pass allow_errors: true to write it anyway):\n" + string.Join("\n", result.Diagnostics));

    static bool AllowErrors(JsonObject args) => args["allow_errors"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    static double Lipschitz(JsonObject args)
    {
        var L = AiShapes.Number(args, "lipschitz");
        if (!(L > 0) || double.IsInfinity(L)) throw new AiToolException($"'lipschitz' must be > 0 (got {L})");
        return L;
    }

    /// <paramref name="current"/> overlaid with args.params (name → number), as SDF.u.* text. Unknown names are an error
    /// when the program is known.
    static Dictionary<string, string> ParamArgs(JsonObject args, UserFnProgram? program, Dictionary<string, string> current)
    {
        var raw = new Dictionary<string, string>(current, StringComparer.Ordinal);
        if (args["params"] is null) return raw;
        if (args["params"] is not JsonObject given) throw new AiToolException("'params' must be an object {name: number}");
        foreach (var (name, _) in given)
        {
            if (program != null && !program.Params.Any(p => p.Name == name))
                throw new AiToolException($"no param '{name}'; the code declares: {(program.Params.Length == 0 ? "none" : string.Join(", ", program.Params.Select(p => p.Name)))}");
            var v = AiShapes.Number(given, name);
            if (!double.IsFinite(v)) throw new AiToolException($"param '{name}' must be a finite number");
            raw[name] = v.ToString("R", CultureInfo.InvariantCulture);
        }
        return raw;
    }
}
