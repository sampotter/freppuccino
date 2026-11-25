using System.Drawing;
using System.Text;
using Freppuccino.Csg;
using Freppuccino.Meshing;
using Freppuccino.Scene;
using Freppuccino.Sdf;
using Freppuccino.UserFn;
using Rhino.Display;
using Rhino.Geometry;

namespace Freppuccino.Shaders;

/// Runtime-generated SDF scene from the CSG DAG, raymarched on a fullscreen triangle.
/// Structure (prim kinds, tree shape) is baked into the source; parameters (camera, prims, colors, warp frames, per-node blend k)
/// are uniforms, so moving primitives/axes or editing blends/warp params/colors never recompiles.
/// map(): emitted lazily from the roots. A value = (operand, point context): context 0 is p; each warp node opens a new context
/// (its warped point) for its subtree, so a subtree shared by different warps is emitted once per context, else once.
/// Root = hard min over parentless prims/nodes. Missing leaves (hidden prims or nodes/deleted/over budget) are dropped at
/// codegen: union skips them, intersect becomes empty, difference with missing base becomes empty, warp of a missing child
/// becomes empty. A warp whose line/box is gone passes its child through unwarped.
/// sceneColor(): the same DAG over vec4(rgb, d), evaluated once at the hit point. Union/intersect blend operand colors with
/// the smin/smax weight (a smooth union's fillet fades between them); difference keeps the base's color; warps keep the child's.
public static class FrepShaderGen
{
    /// Uniform header = 8 vec4 rows; the rest of Uniforms.MaxBytes is data: prim rows, prim colors packed 4 per row,
    /// warp rows, then blend k's packed 4 per row.
    const int HeaderRows = 9; // ..., bound, zebraCfg, selColor

    /// Rows of root "selected" flags at the end of the data (24 exact bits per float, 96 per row), sized from the
    /// actual root count at compile time, so every root has a flag however roots share primitives.
    static int SelRows(int roots) => (roots + 95) / 96;
    /// Selection band width, screen pixels.
    public const double SelectionBandPx = 2.0;
    const int MaxDataRows = Uniforms.MaxBytes / 16 - HeaderRows;

    /// Sphere: 1 row (xyz center, w s). Ellipsoid: 3 rows (origin, unit X, unit Y; Z = X × Y).
    /// Every other kind: 4 rows. The fourth is the third row of A⁻¹ and σ_min(A), so a sheared or off-axis-scaled
    /// primitive (SDF.Linear.*) still raymarches. Rigid primitives pass the frame axes and σ_min = 1, which is the old toLocal.
    /// User (FFunction): the 4 box rows (b in .w) + ceil((1 + params) / 4) rows: L, then param values in declaration order.
    public static int Rows(SdPrimitive p) => p.Kind switch
    {
        SdKind.Sphere => 1,
        SdKind.Ellipsoid => 3,
        SdKind.User => 4 + (UserData(p).Length + 3) / 4,
        _ => 4,
    };

    /// FFunction data rows' payload: L, then each param value (declared default if missing).
    static double[] UserData(SdPrimitive p)
    {
        var fn = p.Fn;
        var prms = fn?.Program?.Params ?? [];
        var data = new double[1 + prms.Length];
        data[0] = fn?.L ?? 1;
        for (int i = 0; i < prms.Length; i++) data[1 + i] = i < fn!.Values.Length ? fn.Values[i] : prms[i].Default;
        return data;
    }

    /// GLSL name of a compiled FFunction (one definition per distinct program).
    static string UserFnName(UserFnProgram program) => "ufn_" + program.Hash;
    /// Twist: (start, rate), (dir, extent). Bend: (start, length), (dir, angle), (bend normal, min radius). Repeat: see WarpUniforms.
    static int WarpRows(FrepOp op) => op switch { FrepOp.Twist => 2, FrepOp.Bend => 3, FrepOp.Repeat => 6, _ => 0 };
    static int KRows(int nodes) => (nodes + 3) / 4;
    /// Rows holding the colors of <paramref name="prims"/> primitives (one packed 0xRRGGBB float each, 4 per row).
    public static int ColorRows(int prims) => (prims + 3) / 4;
    /// Rows left for prims (geometry + color rows) once every node's k / warp rows have a slot.
    public static int PrimRowBudget(FrepTree tree) =>
        MaxDataRows - KRows(tree.Nodes.Count(n => !n.Op.IsWarp())) - tree.Nodes.Sum(n => n.Hidden ? 0 : WarpRows(n.Op))
        - MatrixNodes(tree).Count() * Placement.ShaderRows
        // Selection flags: roots are some of the nodes and prims, and there are fewer prims than data rows.
        - SelRows(tree.Nodes.Count + Uniforms.MaxBytes / 16);

    /// Nodes whose transform M gets uniform rows (P⁻¹-style, 4 rows each), in row order: visible, usable matrix.
    static IEnumerable<FrepNode> MatrixNodes(FrepTree tree) =>
        tree.Nodes.Where(n => !n.Hidden && n.HasMatrix && Placement.TryRows(n.M, out _));

    /// 0xRRGGBB as a float: exact, since float32 holds every integer below 2^24. Unpacked by unpackColor() in the shader.
    static double PackColor(Color c) => (c.R << 16) | (c.G << 8) | c.B;

    /// Shapes are drawn this many screen pixels inside their true outline, so Rhino's wireframe edges show around them.
    /// A uniform, so changing it never recompiles.
    public const double SilhouetteInsetPx = 1.0;

    static readonly Dictionary<string, ShaderSource> cache = [];

    /// GLSL call evaluating one primitive whose rows start at <paramref name="row"/> at point <paramref name="p"/>.
    static string Term(SdPrimitive prim, int row, string p)
    {
        var kind = prim.Kind;
        if (kind == SdKind.Sphere) return $"sdSphere({p}, u.data[{row}])";
        string o = $"u.data[{row}]", x = $"u.data[{row + 1}]", y = $"u.data[{row + 2}]";
        if (kind == SdKind.Ellipsoid)
            return $"sdEllipsoid(toLocal({p}, {o}, {x}, {y}), vec3({o}.w, {x}.w, {y}.w))";
        // z.xyz = third row of A⁻¹, z.w = σ_min(A). Distance times σ_min stays Lipschitz 1 in world space.
        string z = $"u.data[{row + 3}]";
        string q = $"toLocalAffine({p}, {o}, {x}, {y}, {z})";
        string sd = kind switch
        {
            SdKind.Box => $"sdBox({q}, vec3({o}.w, {x}.w, {y}.w))",
            SdKind.Torus => $"sdTorus({q}, vec2({o}.w, {x}.w))",
            SdKind.Cylinder => $"sdCappedCylinder({q}, {o}.w, {x}.w)",
            SdKind.Cone => $"sdCappedCone({q}, {o}.w, {x}.w, {y}.w)",
            SdKind.Pyramid => $"sdPyramidScaled({q}, {o}.w, {x}.w)",
            SdKind.User => UserTerm(prim, row + 4, q, $"vec3({o}.w, {x}.w, {y}.w)"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return $"({sd}) * {z}.w";
    }

    /// max(f(q) / L, sdBox(q, b)): the box always clips. Data rows from <paramref name="row"/>: .x = L, then params.
    static string UserTerm(SdPrimitive prim, int row, string q, string b)
    {
        var program = prim.Fn?.Program ?? throw new ArgumentException("FFunction without a compiled program", nameof(prim));
        string Slot(int i) => $"u.data[{row + i / 4}].{"xyzw"[i % 4]}";
        var args = string.Concat(Enumerable.Range(1, program.Params.Length).Select(i => ", " + Slot(i)));
        return $"max({UserFnName(program)}({q}{args}) / {Slot(0)}, sdBox({q}, {b}))";
    }

    /// Per-prim row shapes + warp ops + k count. Two programs with equal Layout read the same uniform bytes the same way
    /// (ShaderConduit falls back to the last good program only then).
    /// Also node-transform rows and root-flag rows, which follow the k's.
    static string Layout(IReadOnlyList<SdPrimitive> prims, IEnumerable<FrepOp> warps, int ks, int matrices, int roots) =>
        string.Join(",", prims.Select(p => p.Kind == SdKind.User ? $"U{Rows(p)}" : p.Kind.ToString())) + "|" +
        string.Join(",", warps) + "|" + ks + "|M" + matrices + "|R" + SelRows(roots);

    const string Vertex = """
        #version 450
        layout(location = 0) out vec2 vNdc;
        void main()
        {
            vec2 p = vec2(gl_VertexIndex == 1 ? 3.0 : -1.0, gl_VertexIndex == 2 ? 3.0 : -1.0);
            vNdc = p;
            gl_Position = vec4(p, 0.0, 1.0);
        }
        """;

    /// <paramref name="ids"/> parallel to <paramref name="prims"/>; prims must fit <see cref="PrimRowBudget"/>.
    /// <paramref name="liveWarps"/>: warp node ids whose reference geometry resolved. Returns Program = null if the root is empty;
    /// Ks (k slot order) and Warps (warp row order) for <see cref="SceneUniforms"/>.
    /// Roots: the non-empty roots in map() order (selection flags follow it).
    public static (ShaderSource? Program, List<double> Ks, List<int> Warps, List<FrepOperand> Roots) Compile(
        IReadOnlyList<SdPrimitive> prims, IReadOnlyList<Guid> ids, FrepTree tree, IReadOnlySet<int> liveWarps)
    {
        var index = new Dictionary<Guid, int>();
        var primRow = new int[prims.Count];
        int row = 0;
        for (int i = 0; i < prims.Count; i++)
        {
            index[ids[i]] = i;
            primRow[i] = row;
            row += Rows(prims[i]);
        }
        // Prim colors right after the prim rows.
        int cBase = row;
        row += ColorRows(prims.Count);

        // Slots are assigned up front (in tree order), so rows are fixed before lazy emission.
        var warpRow = new Dictionary<int, int>();
        var warps = new List<int>();
        foreach (var n in tree.Nodes)
            if (n.Op.IsWarp() && !n.Hidden && liveWarps.Contains(n.Id))
            {
                warpRow[n.Id] = row;
                row += WarpRows(n.Op);
                warps.Add(n.Id);
            }
        // Node transforms after the warps.
        var mRow = new Dictionary<int, int>();
        foreach (var n in MatrixNodes(tree))
        {
            mRow[n.Id] = row;
            row += Placement.ShaderRows;
        }
        int kBase = row;
        var kSlot = new Dictionary<int, int>();
        var ks = new List<double>();
        foreach (var n in tree.Nodes)
            if (!n.Op.IsWarp())
            {
                kSlot[n.Id] = ks.Count;
                ks.Add(n.K);
            }

        // map: float distances. color: the same DAG over vec4(rgb, d) (same variable names), so the shaded color follows the blend.
        var map = new StringBuilder();
        var color = new StringBuilder();
        var helpers = new StringBuilder();
        var repeatFn = new HashSet<int>();
        void Both(string line) { map.Append(line); color.Append(line); }
        var memo = new Dictionary<(FrepOperand, int), string?>();
        var fns = new SortedDictionary<string, UserFnProgram>(StringComparer.Ordinal); // FFunctions reached, by hash
        int contexts = 1;
        static string Suffix(int c) => c == 0 ? "" : $"_{c}";
        static string Point(int c) => c == 0 ? "p" : $"q{c}";
        static string Row(int r) => $"u.data[{r}]";

        string? Eval(FrepOperand o, int c)
        {
            if (memo.TryGetValue((o, c), out var v)) return v;
            v = o.IsNode ? EvalNode(o.NodeId, c) : EvalPrim(o.ObjectId, c);
            return memo[(o, c)] = v;
        }

        string? EvalPrim(Guid id, int c)
        {
            if (!index.TryGetValue(id, out var i)) return null;
            if (prims[i].Kind == SdKind.User)
            {
                if (prims[i].Fn?.Program is not { } program) return null; // doesn't compile: missing leaf
                fns[program.Hash] = program;
            }
            var v = $"p{i}{Suffix(c)}";
            var term = Term(prims[i], primRow[i], Point(c));
            map.Append($"    float {v} = {term};\n");
            color.Append($"    vec4 {v} = vec4(unpackColor(u.data[{cBase + i / 4}].{"xyzw"[i % 4]}), {term});\n");
            return v;
        }

        string? EvalNode(int id, int c)
        {
            if (tree.Find(id) is not { Hidden: false } n) return null;
            if (!mRow.TryGetValue(id, out var mr)) return EvalBody(n, c, $"n{id}{Suffix(c)}");
            // Node transform M: the body in a context of its own at M⁻¹ p, its value scaled by s(M). Color = body's.
            int cm = contexts++;
            Both($"    vec3 {Point(cm)} = place({Point(c)}, {Row(mr)}, {Row(mr + 1)}, {Row(mr + 2)});\n");
            if (EvalBody(n, cm, $"b{id}{Suffix(cm)}") is not { } body) return null;
            var vm = $"n{id}{Suffix(c)}";
            map.Append($"    float {vm} = {body} * {Row(mr + 3)}.x;\n");
            color.Append($"    vec4 {vm} = vec4({body}.rgb, {body}.w * {Row(mr + 3)}.x);\n");
            return vm;
        }

        string? EvalBody(FrepNode n, int c, string v)
        {
            int id = n.Id;
            if (n.Op.IsWarp())
            {
                if (n.Operands.Length == 0) return null;
                if (!warpRow.TryGetValue(id, out var r)) return Eval(n.Operands[0], c); // line/box deleted: unwarped
                int cc = contexts++;
                string p = Point(c), q = Point(cc), l = $"l{cc}";
                if (n.Op == FrepOp.Repeat)
                {
                    // Min over every copy that can be nearest (repeatCells); child re-emitted once, inside the cell loop.
                    string a = $"a{cc}", b = $"b{cc}", rows = $"{Row(r)}, {Row(r + 1)}, {Row(r + 2)}";
                    map.Append($"    float {v} = 1e20;\n");
                    color.Append($"    vec4 {v} = vec4(0.0, 0.0, 0.0, 1e20);\n");
                    Both($"    ivec3 {a}, {b}; repeatCells({p}, {rows}, {Row(r + 3)}, {Row(r + 4)}, {Row(r + 5)}, {a}, {b});\n" +
                         $"    for (int k{cc} = {a}.z; k{cc} <= {b}.z; k{cc}++)\n" +
                         $"    for (int j{cc} = {a}.y; j{cc} <= {b}.y; j{cc}++)\n" +
                         $"    for (int i{cc} = {a}.x; i{cc} <= {b}.x; i{cc}++)\n" +
                         $"    {{\n    vec3 {q} = repeatAt({p}, {rows}, ivec3(i{cc}, j{cc}, k{cc}));\n");
                    var copy = Eval(n.Operands[0], cc);
                    if (copy != null)
                    {
                        map.Append($"    {v} = min({v}, {copy});\n");
                        color.Append($"    {v} = cmin({v}, {copy}, 0.0);\n");
                    }
                    Both("    }\n");
                    return copy == null ? null : v;
                }
                Both(n.Op == FrepOp.Twist
                    ? $"    float {l}; vec3 {q} = opTwist({p}, {Row(r)}, {Row(r + 1)}, {l});\n"
                    : $"    float {l}; vec3 {q} = opBend({p}, {Row(r)}, {Row(r + 1)}, {Row(r + 2)}, {l});\n");
                if (Eval(n.Operands[0], cc) is not { } child) return null;
                // Twist/bend stretch space; dividing by the local Lipschitz bound keeps steps conservative. Color = child's.
                map.Append($"    float {v} = {child} / {l};\n");
                color.Append($"    vec4 {v} = vec4({child}.rgb, {child}.w / {l});\n");
                return v;
            }

            // Blend sits above a Repeat and fillets its copies. The Repeat node itself stays a hard warp.
            if (n.Op == FrepOp.Blend)
            {
                if (n.Operands.Length == 0) return null;
                var only = n.Operands[0];
                if (only.IsNode && tree.Find(only.NodeId) is { Op: FrepOp.Repeat, Hidden: false } rep
                    && warpRow.TryGetValue(rep.Id, out var rr) && rep.Operands.Length > 0)
                {
                    if (!EmitRepeat(n, rep, rr, c, v)) return null;
                    return v;
                }
                return Eval(only, c);
            }

            // Offset moves the child's surface: positive k grows the solid (d - k).
            if (n.Op == FrepOp.Offset)
            {
                if (n.Operands.Length == 0 || Eval(n.Operands[0], c) is not { } shifted) return null;
                int off = kSlot[id];
                var ok = $"u.data[{kBase + off / 4}].{"xyzw"[off % 4]}";
                map.Append($"    float {v} = {shifted} - {ok};\n");
                color.Append($"    vec4 {v} = vec4({shifted}.rgb, {shifted}.w - {ok});\n");
                return v;
            }

            var terms = n.Operands.Select(o => Eval(o, c)).ToList();
            List<string>? live = n.Op switch
            {
                FrepOp.Union => [.. terms.OfType<string>()],
                FrepOp.Intersect => terms.Contains(null) ? null : [.. terms.OfType<string>()],
                _ => terms.Count == 0 || terms[0] == null ? null : [.. terms.OfType<string>()],
            };
            if (live is not { Count: > 0 }) return null;
            int slot = kSlot[id];
            var k = $"u.data[{kBase + slot / 4}].{"xyzw"[slot % 4]}";
            map.Append($"    float {v} = {live[0]};\n");
            color.Append($"    vec4 {v} = {live[0]};\n");
            foreach (var t in live.Skip(1))
            {
                map.Append(n.Op switch
                {
                    FrepOp.Union => $"    {v} = smin({v}, {t}, {k});\n",
                    FrepOp.Intersect => $"    {v} = smax({v}, {t}, {k});\n",
                    _ => $"    {v} = smax({v}, -{t}, {k});\n",
                });
                color.Append(n.Op switch
                {
                    FrepOp.Union => $"    {v} = cmin({v}, {t}, {k});\n",
                    FrepOp.Intersect => $"    {v} = cmax({v}, {t}, {k});\n",
                    _ => $"    {v} = csub({v}, {t}, {k});\n",
                });
            }
            // FZebra: the hit point lies on this union's surface (incl. its blend fillets).
            if (n.Op == FrepOp.Union) color.Append($"    zeb = max(zeb, 1.0 - step(tol, abs({v}.w)));\n");
            return v;
        }

        // One function for the repeated child, then a fold of smin over the nearest cell and its neighbors.
        // The blend node's k drives it. k = 0 visits only the nearest cell (a hard repeat). k > 0 also visits
        // the 26 adjacent cells, so every copy fillets into its neighbors.
        bool EmitRepeat(FrepNode blend, FrepNode rep, int r, int c, string v)
        {
            if (!repeatFn.Contains(rep.Id))
            {
                int fc = contexts++;
                var savedMap = map;
                var savedColor = color;
                map = new StringBuilder();
                color = new StringBuilder();
                var child = Eval(rep.Operands[0], fc);
                var bodyMap = map;
                var bodyColor = color;
                map = savedMap;
                color = savedColor;
                if (child == null) return false;
                string arg = Point(fc);
                helpers.Append($"        float repD{rep.Id}(vec3 {arg})\n        {{\n{bodyMap}            return {child};\n        }}\n");
                helpers.Append($"        vec4 repC{rep.Id}(vec3 {arg}, float tol, inout float zeb)\n        {{\n{bodyColor}            return {child};\n        }}\n");
                repeatFn.Add(rep.Id);
            }
            int slot = kSlot[blend.Id];
            string k = $"u.data[{kBase + slot / 4}].{"xyzw"[slot % 4]}";
            string rows = $"{Row(r)}, {Row(r + 1)}, {Row(r + 2)}, {Row(r + 3)}, {Row(r + 4)}";
            string p = Point(c);
            map.Append($$"""
                    float {{v}} = 1e20;
                    {
                        float rk = {{k}};
                        int rad = rk > 0.0 ? 1 : 0;
                        for (int dz = -1; dz <= 1; ++dz)
                        for (int dy = -1; dy <= 1; ++dy)
                        for (int dx = -1; dx <= 1; ++dx)
                        {
                            if (abs(dx) > rad || abs(dy) > rad || abs(dz) > rad) continue;
                            vec3 rq;
                            if (!repeatCell({{p}}, {{rows}}, vec3(float(dx), float(dy), float(dz)), rq)) continue;
                            {{v}} = smin({{v}}, repD{{rep.Id}}(rq), rk);
                        }
                    }

                """);
            color.Append($$"""
                    vec4 {{v}} = vec4(0.0, 0.0, 0.0, 1e20);
                    {
                        float rk = {{k}};
                        int rad = rk > 0.0 ? 1 : 0;
                        bool got = false;
                        for (int dz = -1; dz <= 1; ++dz)
                        for (int dy = -1; dy <= 1; ++dy)
                        for (int dx = -1; dx <= 1; ++dx)
                        {
                            if (abs(dx) > rad || abs(dy) > rad || abs(dz) > rad) continue;
                            vec3 rq;
                            if (!repeatCell({{p}}, {{rows}}, vec3(float(dx), float(dy), float(dz)), rq)) continue;
                            vec4 cell = repC{{rep.Id}}(rq, tol, zeb);
                            if (!got) { {{v}} = cell; got = true; }
                            else {{v}} = cmin({{v}}, cell, rk);
                        }
                        zeb = max(zeb, 1.0 - step(tol, abs({{v}}.w)));
                    }

                """);
            return true;
        }

        var referenced = tree.Nodes.SelectMany(n => n.Operands).ToHashSet();
        var roots = new List<string>();
        var rootOps = new List<FrepOperand>();
        for (int i = 0; i < prims.Count; i++)
            if (!referenced.Contains(FrepOperand.Object(ids[i])) && Eval(FrepOperand.Object(ids[i]), 0) is { } v)
            {
                roots.Add(v);
                rootOps.Add(FrepOperand.Object(ids[i]));
            }
        foreach (var n in tree.Nodes)
            if (!referenced.Contains(FrepOperand.Node(n.Id)) && Eval(FrepOperand.Node(n.Id), 0) is { } v)
            {
                roots.Add(v);
                rootOps.Add(FrepOperand.Node(n.Id));
            }
        if (roots.Count == 0) return (null, ks, warps, rootOps);

        // Distance to the selected roots: flags are bits of the last data rows (24 per float, 96 per row), one per root.
        int selBase = kBase + KRows(ks.Count);
        map.Append("    s = 1e20;\n");
        for (int i = 0; i < roots.Count; i++)
        {
            double scale = 1.0 / (1 << (i % 24)); // exact power of two
            map.Append($"    if (mod(floor(u.data[{selBase + i / 96}].{"xyzw"[i / 24 % 4]} * {scale.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}), 2.0) > 0.5) s = min(s, {roots[i]});\n");
        }

        map.Append($"    float d = {roots[0]};\n");
        color.Append($"    vec4 c = {roots[0]};\n");
        foreach (var r in roots.Skip(1))
        {
            map.Append($"    d = min(d, {r});\n");
            color.Append($"    c = cmin(c, {r}, 0.0);\n");
        }

        int dataRows = selBase + SelRows(roots.Count);
        var key = dataRows + "\n" + string.Join(",", fns.Keys) + "\n" + helpers + "\n" + map + "\n" + color;
        if (!cache.TryGetValue(key, out var src))
        {
            var userFns = string.Concat(fns.Values.Select(f => f.Glsl(UserFnName(f)).TrimEnd() + "\n"));
            cache[key] = src = new ShaderSource(Vertex, Fragment(dataRows, userFns, helpers.ToString(), map.ToString(), color.ToString()))
            {
                Layout = Layout(prims, warps.Select(id => tree.Find(id)!.Op), ks.Count, mRow.Count, roots.Count),
                Background = true,
            };
        }
        return (src, ks, warps, rootOps);
    }

    /// The shared GLSL library (also callable from user code).
    static string Library => UserFnLibrary.Glsl;

    static string Fragment(int dataRows, string userFns, string helpers, string map, string color) => $$"""
        #version 450
        layout(location = 0) in vec2 vNdc;
        layout(location = 0) out vec4 outColor;
        layout(std140, set = 0, binding = 0) uniform Frame
        {
            vec4 camPos;     // xyz eye; w = 1 perspective, 0 parallel
            vec4 camX;       // xyz unit right
            vec4 camY;       // xyz unit up
            vec4 camZ;       // xyz unit view direction; w = near
            vec4 frustum;    // left, right, bottom, top (camera coords, at near)
            vec4 params;     // x = silhouette inset (screen px), y = eps per unit t, z = eps const, w unused
            vec4 bound;      // xyz center, w radius: sphere enclosing the (warped) scene + smooth-union growth; w <= 0 = empty
            vec4 zebraCfg;   // FZebra on unions: x = stripe count (0 = off), y = 1 vertical / 0 horizontal, z/w = colors (0xRRGGBB)
            vec4 selColor;   // rgb selection color, w = selection band width (screen px)
            // prims, then prim colors (packed 0xRRGGBB floats, 4 per row), then warp rows (see opTwist/opBend/opRepeat),
            // then node blend k's (4 per row)
            // sphere: 1 row  (xyz center, w radius s)
            // ellipsoid: 3 rows (xyz origin, w r.x), (xyz unit X axis, w r.y), (xyz unit Y axis, w r.z); Z = X × Y
            // others: 4 rows (xyz origin, w P.x), (xyz A⁻¹ row 0, w P.y), (xyz A⁻¹ row 1, w P.z), (xyz A⁻¹ row 2, w σ_min(A))
            //   box b = P; torus t = P.xy; cylinder h, r; cone h, r1, r2; pyramid h, s; function b = P (clip box)
            // function: + ceil((1 + params) / 4) rows: L, then param values (declaration order), packed xyzw
            vec4 data[{{dataRows}}];
        } u;

        const vec3 GREY = vec3(0.78);

        {{Library}}
        vec3 unpackColor(float packed)
        {
            // round, not +0.5: above 2^23 floats step by 1, so 0xFFFFFF + 0.5 rounds up to 0x1000000 (white came out black).
            uint c = uint(round(packed));
            return vec3(float((c >> 16) & 255u), float((c >> 8) & 255u), float(c & 255u)) / 255.0;
        }
        // Same ops over vec4(rgb, d). Colors mix with IQ's smin weight, so they fade across the blend region
        // (|a - b| < k) and are pure away from it; k = 0 picks the nearer operand's color.
        vec4 cmin(vec4 a, vec4 b, float k)
        {
            if (k <= 0.0) return a.w < b.w ? a : b;
            float h = clamp(0.5 + 0.5 * (b.w - a.w) / k, 0.0, 1.0);
            return vec4(mix(b.rgb, a.rgb, h), smin(a.w, b.w, k));
        }
        vec4 cmax(vec4 a, vec4 b, float k)
        {
            if (k <= 0.0) return a.w > b.w ? a : b;
            float h = clamp(0.5 + 0.5 * (a.w - b.w) / k, 0.0, 1.0);
            return vec4(mix(b.rgb, a.rgb, h), smax(a.w, b.w, k));
        }
        vec4 csub(vec4 a, vec4 b, float k) { return vec4(a.rgb, smax(a.w, -b.w, k)); } // cut faces keep the base's color
        // Affine map by three rows (xyz linear row, w translation): a node transform's inverse M⁻¹.
        vec3 place(vec3 p, vec4 a, vec4 b, vec4 c) { return vec3(dot(a.xyz, p) + a.w, dot(b.xyz, p) + b.w, dot(c.xyz, p) + c.w); }
        vec3 toLocal(vec3 p, vec4 o, vec4 x, vec4 y)
        {
            vec3 d = p - o.xyz;
            return vec3(dot(d, x.xyz), dot(d, y.xyz), dot(d, cross(x.xyz, y.xyz)));
        }
        // x, y, z are the rows of A⁻¹. For a rigid frame they are the frame axes, so this matches toLocal.
        vec3 toLocalAffine(vec3 p, vec4 o, vec4 x, vec4 y, vec4 z)
        {
            vec3 d = p - o.xyz;
            return vec3(dot(d, x.xyz), dot(d, y.xyz), dot(d, z.xyz));
        }

        // IQ, https://iquilezles.org/articles/distfunctions/ (all in local space, Y-up)
        float sdSphere(vec3 p, vec4 s) { return length(p - s.xyz) - s.w; }

        // Domain warps: return the point to evaluate the child at; lip = local Lipschitz bound (child distance gets divided by it).
        // Twist: a = (line start, rate rad/unit), d = (unit dir, extent = child radius around the axis, clamps the bound).
        vec3 opTwist(vec3 p, vec4 a, vec4 d, out float lip)
        {
            vec3 v = p - a.xyz;
            float h = dot(v, d.xyz);
            vec3 r = v - d.xyz * h;
            float ang = -a.w * h, c = cos(ang), s = sin(ang);
            float sh = abs(a.w) * min(length(r), d.w);
            lip = 0.5 * (sh + sqrt(sh * sh + 4.0)); // largest singular value of a shear by sh
            return a.xyz + d.xyz * h + r * c + cross(d.xyz, r) * s;
        }
        // Bend: a = (line start, length L), d = (unit dir, total angle >= 0), n = (unit bend direction, min radius for the bound).
        // [0, L] along d bends into an arc toward n; straight (rigid) before the start and past the end.
        vec3 opBend(vec3 p, vec4 a, vec4 d, vec4 n, out float lip)
        {
            lip = 1.0;
            float beta = d.w, L = a.w;
            vec3 v = p - a.xyz;
            float h = dot(v, d.xyz);
            if (beta < 1e-5 || h <= 0.0) return p;
            float y = dot(v, n.xyz);
            vec3 b = v - d.xyz * h - n.xyz * y;
            float R = L / beta;
            vec2 c = vec2(h, R - y); // from the arc center
            float alpha = atan(c.x, c.y), h0, y0;
            if (alpha <= beta)
            {
                float rho = length(c);
                h0 = R * alpha;
                y0 = R - rho;
                lip = max(1.0, R / max(rho, n.w));
            }
            else
            {
                vec2 q = vec2(h, y) - R * vec2(sin(beta), 1.0 - cos(beta)); // from the arc end
                h0 = L + dot(q, vec2(cos(beta), sin(beta)));
                y0 = dot(q, vec2(-sin(beta), cos(beta)));
            }
            return a.xyz + d.xyz * h0 + n.xyz * y0 + b;
        }
        // Limited repetition (iquilezles.org/articles/sdfrepetition): o = (anchor = child center, spacing x), x = (unit box X,
        // spacing y), y = (unit box Y, spacing z), lo/hi = copy index range per axis (box axes), r = child half-extent in cells.
        // [c0, c1] = cells whose copy can be nearest to p: nearest cell + its neighbor on p's side (2x2x2), wider if copies
        // overlap (r > 0.5). Past that range a copy is always farther than its neighbor one cell closer to p.
        void repeatCells(vec3 p, vec4 o, vec4 x, vec4 y, vec4 lo, vec4 hi, vec4 r, out ivec3 c0, out ivec3 c1)
        {
            vec3 l = toLocal(p, o, x, y) / max(vec3(o.w, x.w, y.w), vec3(1e-9));
            vec3 a = clamp(round(l), lo.xyz, hi.xyz);
            vec3 fwd = ceil(r.xyz), back = max(ceil(r.xyz - 0.5), 0.0);
            bvec3 up = greaterThanEqual(l, a);
            c0 = ivec3(max(a - mix(fwd, back, up), lo.xyz));
            c1 = ivec3(min(a + mix(back, fwd, up), hi.xyz));
        }
        vec3 repeatAt(vec3 p, vec4 o, vec4 x, vec4 y, ivec3 c)
        {
            vec3 i = vec3(c) * vec3(o.w, x.w, y.w);
            return p - x.xyz * i.x - y.xyz * i.y - cross(x.xyz, y.xyz) * i.z;
        }
        // Neighbor copy for the blend above a Repeat. An offset that would fall outside the box is rejected
        // instead of clamped: clamping lands on a copy already in the fold, and smin of a field with itself
        // grows it by k/4 (the outer copies, several times over at a corner).
        bool repeatCell(vec3 p, vec4 o, vec4 x, vec4 y, vec4 lo, vec4 hi, vec3 delta, out vec3 q)
        {
            vec3 s = vec3(o.w, x.w, y.w);
            vec3 id = clamp(round(toLocal(p, o, x, y) / max(s, vec3(1e-9))), lo.xyz, hi.xyz) + delta;
            q = p - x.xyz * id.x * s.x - y.xyz * id.y * s.y - cross(x.xyz, y.xyz) * id.z * s.z;
            return all(greaterThanEqual(id, lo.xyz)) && all(lessThanEqual(id, hi.xyz));
        }
        // FFunctions (user code, see UserFn/), one per distinct program.
        {{userFns}}
        {{helpers}}
        // s = distance to the selected roots (1e20 if none). map() drops it, so its code is dead there.
        float mapSel(vec3 p, out float s)
        {
        {{map}}    return d;
        }
        float map(vec3 p) { float s; return mapSel(p, s); }
        float selDist(vec3 p) { float s; mapSel(p, s); return s; }
        // Only evaluated once per hit pixel, so the hot march loop stays float-only.
        // zeb = 1 if p is within tol of a union node's surface (FZebra), else 0.
        vec3 sceneColor(vec3 p, float tol, out float zeb)
        {
            zeb = 0.0;
        {{color}}    return c.rgb;
        }
        // FZebra (Rhino-style): bands of the reflected view direction, measured in the camera frame so the stripes
        // follow the view. Vertical stripes vary with the reflection's left/right component, horizontal with up/down.
        vec3 zebra(vec3 rd, vec3 n)
        {
            vec3 r = reflect(rd, n);
            float s = u.zebraCfg.y > 0.5 ? dot(r, u.camX.xyz) : dot(r, u.camY.xyz);
            float a = acos(clamp(s, -1.0, 1.0)) / 3.14159265 * u.zebraCfg.x;
            return step(0.5, fract(a)) > 0.5 ? unpackColor(u.zebraCfg.w) : unpackColor(u.zebraCfg.z);
        }

        void main()
        {
            vec2 f = vNdc * 0.5 + 0.5;
            vec2 xy = mix(u.frustum.xz, u.frustum.yw, f);
            bool persp = u.camPos.w > 0.5;
            float near = u.camZ.w;
            vec3 ro, rd;
            if (persp)
            {
                ro = u.camPos.xyz;
                rd = normalize(u.camX.xyz * xy.x + u.camY.xyz * xy.y + u.camZ.xyz * near);
            }
            else
            {
                ro = u.camPos.xyz + u.camX.xyz * xy.x + u.camY.xyz * xy.y;
                rd = u.camZ.xyz;
            }
            // March only through the scene's bounding sphere, not Rhino's near/far: Rhino moves its clip planes
            // around while zooming, and starting a ray past the front of a surface lands inside the SDF (instant
            // hit, interior-gradient normals, i.e. the shape looks sliced).
            vec3 oc = ro - u.bound.xyz;
            float bb = dot(oc, rd);
            vec3 perp = oc - bb * rd; // closest approach; avoids |oc|^2 - r^2 cancellation when zoomed far out
            float disc = u.bound.w * u.bound.w - dot(perp, perp);
            if (u.bound.w <= 0.0 || disc < 0.0) discard;
            float h = sqrt(disc);
            float t = -bb - h, tMax = -bb + h;
            if (persp) t = max(t, 0.0); // nothing behind the eye
            if (tMax < t) discard;
            float eps = 0.0;
            bool hit = false;
            // Closest approach to a selected root in front of the hit: field value / pixel at the step where it was smallest.
            float nearSel = 1e20, tNear = 0.0, s = 1e20;
            for (int i = 0; i < 256; i++)
            {
                eps = u.params.z + u.params.y * t; // half-pixel footprint at t
                // Shrink the surface by params.x pixels so Rhino's wires stay visible around the shading.
                float d = mapSel(ro + rd * t, s) + u.params.x * 2.0 * eps;
                if (d < eps) { hit = true; break; }
                float ratio = s / (2.0 * eps);
                if (ratio < nearSel) { nearSel = ratio; tNear = t; }
                t += d;
                if (t > tMax) break;
            }
            // Selection band: just outside a selected root's silhouette, in front of whatever the ray hits (not on the root).
            // Fields that underestimate distance (ellipsoids, placements, node transforms) would widen it, so the width
            // is measured as value / |gradient| (first-order distance to the surface) at the closest approach.
            bool onSelected = hit && s < 2.0 * eps; // the hit surface is a selected root's (it is the field's min there)
            if (!onSelected && nearSel < 4.0 * u.selColor.w)
            {
                float en = u.params.z + u.params.y * tNear;
                vec3 pn = ro + rd * tNear;
                const vec2 kk = vec2(1.0, -1.0);
                vec3 g = (kk.xyy * selDist(pn + kk.xyy * en) + kk.yyx * selDist(pn + kk.yyx * en) +
                          kk.yxy * selDist(pn + kk.yxy * en) + kk.xxx * selDist(pn + kk.xxx * en)) / (4.0 * en);
                float dist = nearSel * 2.0 * en / max(length(g), 1e-6);
                if (dist / (2.0 * en) < u.selColor.w) { outColor = vec4(u.selColor.rgb, 1.0); return; }
            }
            if (!hit) discard;
            vec3 p = ro + rd * t;
            const vec2 k = vec2(1.0, -1.0);
            vec3 n = normalize(k.xyy * map(p + k.xyy * eps) + k.yyx * map(p + k.yyx * eps) +
                               k.yxy * map(p + k.yxy * eps) + k.xxx * map(p + k.xxx * eps));
            // Object display color, lit as before: world +Z up = full color, -Z down = GREY x color.
            // The hit sits up to ~(1 + 2 x inset) eps inside the true surface; tol covers that with margin.
            float zeb;
            vec3 col = sceneColor(p, eps * (4.0 + 2.0 * u.params.x), zeb);
            // Zebra stripes are unlit (like Rhino's), so their edges read cleanly.
            if (u.zebraCfg.x > 0.0 && zeb > 0.5) { outColor = vec4(zebra(rd, n), 1.0); return; }
            outColor = vec4(col * mix(GREY, vec3(1.0), n.z * 0.5 + 0.5), 1.0);
        }
        """;

    /// <paramref name="selectedRoots"/>: roots drawn with the selection band.
    public static byte[] SceneUniforms(RhinoViewport vp, FrepSceneData scene, IReadOnlyCollection<FrepOperand>? selectedRoots = null)
    {
        vp.GetFrustum(out var l, out var r, out var b, out var t, out var near, out var far);
        var persp = vp.IsPerspectiveProjection;
        var px = Math.Max(1, vp.Size.Width);
        var slope = (r - l) / px * 0.5; // half-pixel footprint
        var dir = -vp.CameraZ;
        var u = new Uniforms()
            .Vec4(vp.CameraLocation.X, vp.CameraLocation.Y, vp.CameraLocation.Z, persp ? 1 : 0)
            .Vec4(vp.CameraX.X, vp.CameraX.Y, vp.CameraX.Z)
            .Vec4(vp.CameraY.X, vp.CameraY.Y, vp.CameraY.Z)
            .Vec4(dir.X, dir.Y, dir.Z, near)
            .Vec4(l, r, b, t)
            .Vec4(SilhouetteInsetPx, persp ? slope / near : 0, persp ? 0 : slope, 0);
        var (center, radius) = Bound(scene);
        u.Vec4(center.X, center.Y, center.Z, radius);
        u.Vec4(Display.FrepSettings.Zebra ? Math.Max(1, Display.FrepSettings.ZebraStripes) : 0,
            Display.FrepSettings.ZebraVertical ? 1 : 0,
            PackColor(Display.FrepSettings.ZebraColor1), PackColor(Display.FrepSettings.ZebraColor2));
        var sc = Rhino.ApplicationSettings.AppearanceSettings.SelectedObjectColor;
        u.Vec4(sc.R / 255.0, sc.G / 255.0, sc.B / 255.0, SelectionBandPx);
        // Same order as Compile(): prim rows, colors, warp rows, node transforms, k's, then root selection flags.
        foreach (var prim in scene.Primitives)
        {
            var (kind, f, v) = prim;
            u.Vec4(f.Origin.X, f.Origin.Y, f.Origin.Z, v.X);
            if (kind == SdKind.Sphere) continue;
            if (kind == SdKind.Ellipsoid)
            {
                u.Vec4(f.XAxis.X, f.XAxis.Y, f.XAxis.Z, v.Y)
                 .Vec4(f.YAxis.X, f.YAxis.Y, f.YAxis.Z, v.Z);
                continue;
            }
            LocalRows(prim, out var rx, out var ry, out var rz, out var lip);
            u.Vec4(rx.X, rx.Y, rx.Z, v.Y)
             .Vec4(ry.X, ry.Y, ry.Z, v.Z)
             .Vec4(rz.X, rz.Y, rz.Z, lip);
            if (kind != SdKind.User) continue;
            var d = UserData(prim);
            double D(int i) => i < d.Length ? d[i] : 0;
            for (int i = 0; i < d.Length; i += 4) u.Vec4(D(i), D(i + 1), D(i + 2), D(i + 3));
        }
        var cs = scene.Colors;
        double C(int i) => i < cs.Count ? PackColor(cs[i]) : 0;
        for (int i = 0; i < scene.Primitives.Count; i += 4)
            u.Vec4(C(i), C(i + 1), C(i + 2), C(i + 3));
        foreach (var w in scene.Warps) WarpUniforms(u, w);
        foreach (var n in MatrixNodes(scene.Tree))
        {
            Placement.TryRows(n.M, out var mr);
            foreach (var row in mr) u.Vec4(row.X, row.Y, row.Z, row.W);
        }
        var ks = scene.Ks;
        for (int i = 0; i < ks.Count; i += 4)
            u.Vec4(ks[i], i + 1 < ks.Count ? ks[i + 1] : 0, i + 2 < ks.Count ? ks[i + 2] : 0, i + 3 < ks.Count ? ks[i + 3] : 0);
        var mask = new double[4 * SelRows(scene.Roots.Count)];
        if (selectedRoots is { Count: > 0 })
            for (int i = 0; i < scene.Roots.Count; i++)
                if (selectedRoots.Contains(scene.Roots[i])) mask[i / 24] += 1 << (i % 24);
        for (int i = 0; i < mask.Length; i += 4) u.Vec4(mask[i], mask[i + 1], mask[i + 2], mask[i + 3]);
        return u.ToBytes();
    }

    static void WarpUniforms(Uniforms u, SdWarp w)
    {
        foreach (var r in FrepWarpMath.Rows(w)) u.Vec4(r.X, r.Y, r.Z, r.W);
    }

    /// Rows of A⁻¹ and σ_min. No stored map: the frame itself, σ_min = 1.
    internal static void LocalRows(SdPrimitive prim, out Vector3d rx, out Vector3d ry, out Vector3d rz, out double lip)
    {
        var basis = prim.Basis ?? SdfBasis.FromPlane(prim.Frame);
        if (!SdfTransforms.TryMetrics(basis, out rx, out ry, out rz, out lip, out _))
        {
            rx = prim.Frame.XAxis;
            ry = prim.Frame.YAxis;
            rz = prim.Frame.ZAxis;
            lip = 1;
        }
    }

    static double PrimRadius(SdPrimitive p)
    {
        var v = p.P;
        return p.Kind switch
        {
            SdKind.Sphere => Math.Abs(v.X),
            SdKind.Box or SdKind.User => new Vector3d(v.X, v.Y, v.Z).Length, // User: the clip box
            SdKind.Torus => Math.Abs(v.X) + Math.Abs(v.Y),
            SdKind.Cylinder => Math.Sqrt(v.X * v.X + v.Y * v.Y),
            SdKind.Cone => Math.Sqrt(v.X * v.X + Math.Max(v.Y * v.Y, v.Z * v.Z)),
            SdKind.Ellipsoid => Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))),
            SdKind.Pyramid => Math.Sqrt(v.X * v.X + 0.5 * v.Y * v.Y), // origin at base center, apex at +h
            _ => 0,
        };
    }

    /// Conservative world-space bounding sphere of the drawn scene, from the in-memory prims/warps (follows gumball drags).
    /// Walks the tree like Compile(): prims → spheres, nodes union their operands, live warps grow their child's box.
    static (Point3d Center, double Radius) Bound(FrepSceneData scene)
    {
        // Node transforms (a transformed blend blended again, nested ones, ...) defeat a global padding factor; the
        // CPU field's sublevel bounds propagate through the graph exactly, so use them there. Cached per snapshot.
        if (scene.Tree.Nodes.Any(n => !n.Hidden && n.HasMatrix))
        {
            var bb = sublevelBound.GetValue(scene, s =>
            {
                var f = Scene.FrepFieldBuilder.Build(s, null, out _);
                var (lo, hi) = f.Bounds();
                if (!(lo.X <= hi.X && lo.Y <= hi.Y && lo.Z <= hi.Z)) return new BoundBox(Point3d.Origin, 0);
                var b = new BoundingBox(lo.X, lo.Y, lo.Z, hi.X, hi.Y, hi.Z);
                return new BoundBox(b.Center, 0.5 * b.Diagonal.Length * 1.01 + 1e-6);
            });
            return (bb.Center, bb.Radius);
        }
        return HeuristicBound(scene);
    }

    sealed record BoundBox(Point3d Center, double Radius);
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrepSceneData, BoundBox> sublevelBound = new();

    static (Point3d Center, double Radius) HeuristicBound(FrepSceneData scene)
    {
        var tree = scene.Tree;
        var prim = new Dictionary<Guid, int>();
        for (int i = 0; i < scene.Ids.Count; i++) prim[scene.Ids[i]] = i;
        var warp = new Dictionary<int, SdWarp>();
        for (int i = 0; i < scene.WarpIds.Count; i++) warp[scene.WarpIds[i]] = scene.Warps[i];
        var memo = new Dictionary<FrepOperand, BoundingBox>();

        BoundingBox Of(FrepOperand o)
        {
            if (memo.TryGetValue(o, out var b)) return b;
            b = BoundingBox.Empty;
            if (!o.IsNode)
            {
                if (prim.TryGetValue(o.ObjectId, out var i))
                {
                    var p = scene.Primitives[i];
                    double stretch = 1;
                    if (p.Basis is { } basis && SdfTransforms.TryMetrics(basis, out _, out _, out _, out _, out var sigmaMax))
                        stretch = sigmaMax;
                    var r = new Vector3d(1, 1, 1) * (PrimRadius(p) * stretch);
                    b = new BoundingBox(p.Frame.Origin - r, p.Frame.Origin + r);
                }
            }
            else if (tree.Find(o.NodeId) is { Hidden: false } n)
            {
                if (n.Op == FrepOp.Difference) { if (n.Operands.Length > 0) b = Of(n.Operands[0]); }
                else foreach (var c in n.Operands) b.Union(Of(c));
                if (b.IsValid && warp.TryGetValue(n.Id, out var w)) b = Warped(b, w);
                if (b.IsValid && n.HasMatrix) b.Transform(n.M); // node transform: box of the mapped corners
            }
            return memo[o] = b;
        }

        var box = BoundingBox.Empty;
        var referenced = tree.Nodes.SelectMany(n => n.Operands).ToHashSet();
        foreach (var id in scene.Ids)
            if (!referenced.Contains(FrepOperand.Object(id))) box.Union(Of(FrepOperand.Object(id)));
        foreach (var n in tree.Nodes)
            if (!referenced.Contains(FrepOperand.Node(n.Id))) box.Union(Of(FrepOperand.Node(n.Id)));
        if (!box.IsValid) return (Point3d.Origin, 0);

        // A smooth union lowers the field by up to k/4 for two operands, and by up to k for more (it
        // compounds), so pad by k per node. The ellipsoid's field underestimates distance by up to the
        // ratio of its semi-axes, so the same drop in value moves its surface that much farther.
        double pad = 0, stretch = 1;
        foreach (var k in scene.Ks) if (k > 0) pad += k;
        // A sheared / off-axis-scaled primitive (SDF.Linear) underestimates distance by its map's condition number, likewise.
        foreach (var p in scene.Primitives)
        {
            double s = 1;
            if (p.Kind == SdKind.Ellipsoid)
            {
                double lo = Math.Min(Math.Abs(p.P.X), Math.Min(Math.Abs(p.P.Y), Math.Abs(p.P.Z)));
                double hi = Math.Max(Math.Abs(p.P.X), Math.Max(Math.Abs(p.P.Y), Math.Abs(p.P.Z)));
                if (lo > 0) s = hi / lo;
            }
            if (p.Basis is { } basis && SdfTransforms.TryMetrics(basis, out _, out _, out _, out var lo2, out var hi2) && lo2 > 0)
                s *= hi2 / lo2;
            stretch = Math.Max(stretch, s);
        }
        double radius = 0.5 * box.Diagonal.Length + pad * stretch;
        return (box.Center, radius * 1.01 + 1e-6); // margin for float32 rounding
    }

    /// Box of a warp's output given its child's box; same as the mesher's (FrepField.WarpBox).
    internal static BoundingBox Warped(BoundingBox child, SdWarp w)
    {
        var op = w.Op switch { FrepOp.Twist => FieldOp.Twist, FrepOp.Bend => FieldOp.Bend, _ => FieldOp.Repeat };
        var (lo, hi) = FrepField.WarpBox(op, FrepWarpMath.Rows(w), (new(child.Min.X, child.Min.Y, child.Min.Z), new(child.Max.X, child.Max.Y, child.Max.Z)));
        return new BoundingBox(lo.X, lo.Y, lo.Z, hi.X, hi.Y, hi.Z);
    }
}

