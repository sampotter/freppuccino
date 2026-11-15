---
name: freppuccino-ffunction
description: FFunction, custom SDF code in Freppuccino (Rhino): DSL reference, Lipschitz rules, tools, workflows, examples. Load before creating or editing a function primitive (gyroids, lattices, noise, anything the built-in primitives can't do).
---
# FFunction: user-written SDFs

An FFunction is a primitive whose distance field is code you write. It lives in a box (the clip box): the drawn field is

    max(sdf(p) / L, sdBox(p, b))

- `p` = the point in the box's local frame, in world units (model units), origin = box center, axes = the box axes.
- `b` = half the box size. Nothing outside the box is ever drawn; make the box a little bigger than the shape you want.
- `L` = the Lipschitz scale (`lipschitz`, default 1). See "Lipschitz" below: getting it wrong breaks the render.

A function is an ordinary primitive for everything else: combine / deform / transform / add_operand / remove_operand /
move / delete / set_visible / colors all work. Scaling or shearing it (transform, gumball) stretches the function with
the box; moving / rotating just moves it.

Each function object has its own code (copies diverge). Code that doesn't compile is not drawn (it acts like a hidden
primitive: unions skip it, an intersect or a subtract base with it becomes empty); the rest of the scene still draws.

## Reaching Rhino (external Claude Code sessions only; the panel agent calls the tools directly)

Rhino's MCP server (`_MCPStart` in Rhino) exposes `run_csharp`. Check its schema for the parameter name (usually
`script`). Its `using` set is limited: fully qualify Rhino types (`Rhino.RhinoDoc.ActiveDoc`, not `__rhino_doc__`). The
script can't reference the plugin assembly, so call the tools through reflection; `RunJson` takes and returns JSON text, runs on Rhino's UI thread (it marshals and waits when
called from another thread), and on errors returns `{"error": "..."}`:

```csharp
var asm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Freppuccino");
var run = asm.GetType("Freppuccino.Ai.AiTools").GetMethod("RunJson");
string Tool(string name, string json = "{}") => (string)run.Invoke(null, new object[] { name, json });

Console.WriteLine(Tool("help"));                       // this guide + every tool, one line each
Console.WriteLine(Tool("get_scene"));
```

Pass JSON as a C# verbatim string, quotes doubled: `@"{""center"": [0,0,0]}"`. Raw string literals (`"""`) don't
compile in `run_csharp`. `capture_views`
writes PNGs to the temp folder and returns their paths in `image_files`; read them to look at the result (or use the MCP
`get_viewport_image`). If the plugin assembly can be referenced directly, `Freppuccino.Ai.AiTools.Run(RhinoDoc doc,
string tool, System.Text.Json.Nodes.JsonObject args)` returns a `JsonNode`; it must run on the UI thread.

Every edit is one undo step ("AI: ..."). The viewport must be in the Freppuccino display mode (command `Freppuccino`)
to see F-reps.

## Tools

- `create_function {name?, center, size, x_axis?, y_axis?, code, params?: {name: value}, lipschitz?, color?, allow_errors?}`
  `center` / `size` (FULL lengths) / axes: the clip box, as for a box primitive. Rejected (nothing created) if the code
  has errors, unless `allow_errors: true`. Returns `ref`, `ok`, `diagnostics`, `params`, `hash`.
- `set_function_code {target, code, allow_errors?}`: replaces the code. Param values carry over by name; new params
  start at their default. With errors: nothing changes (diagnostics in the error) unless `allow_errors`.
- `get_function {target}`: `code`, `params` [{name, default, value}], `lipschitz`, the box (`center`, `size`, axes,
  `linear` if stretched), `ok`, `diagnostics` [{line, column, message}], `hash`, `shader_error` (build error of the
  doc's current viewport shader, if it fails; it covers every function in the scene).
- `set_function_params {target, params?: {name: value}, lipschitz?}`: changes values without touching the code (cheap:
  no shader recompile).
- `update_primitive {ref, center?, size?, x_axis?, y_axis?}` moves / resizes the clip box (code untouched; `code` /
  `params` / `lipschitz` are an error here, use the tools above). On a stretched function (`stretched: true`) `size` is
  in the unstretched frame and the stretch stays; changing its axes is an error (use `transform {rotate}`).
- `get_scene` lists functions as `type: "function"` with `ok`, `error` (first diagnostic), `params`, `lipschitz`.
- Tree edits: `combine`, `add_operand {node, operand, index?}`, `remove_operand {node, operand}`,
  `move {item, to?, from?, index?}` (no `to` = make it a root), `deform`, `transform`, `delete`.
- `help {topic?}`: this guide + the tool list (`topic: "tools"`: only the list).

## The DSL

A small GLSL subset, compiled to GLSL for the viewport and to C# for the mesher (FrepMesh), so both agree.

```glsl
param float radius = 10.0;     // a param: a named number, editable without recompiling (panel slider / set_function_params)
param float wobble = 1.0;

float sdf(vec3 p) {            // the entry point: signed distance at p (negative inside)
    float d = length(p) - radius;
    return d + wobble * sin(p.x) * sin(p.y) * sin(p.z);
}
```

- Entry point: `float sdf(vec3 p)`. You may write helper functions above it.
- Params: `param float name = default;` at top level, in declaration order. The default must be a number literal.
  Values live on the object (`SDF.u.<name>`, always stored, defaults included), so changing a default in the code
  only affects params that are new to the object. Code changes carry values over by name; removed params are dropped.
- Consts: `const float NAME = <constant expression>;` at top level (folded at compile time). `PI` is built in.
- Types: `float`, `int`, `bool`, `vec2`, `vec3`, `vec4`, `mat2`, `mat3` (column-major, GLSL constructors:
  `mat2(a, b, c, d)`, `mat3(col0, col1, col2)`, `mat3(1.0)` = identity). `int` converts to `float` implicitly.
  No `mat4`, int/bool vectors, arrays or structs.
- Expressions: `+ - * /` (component-wise, scalar broadcast; `mat * vec`, `vec * mat`, `mat * mat`), comparisons,
  `&& || !`, `?:`, swizzles (`p.xz`, `q.zyx`; also `rgba`), indexing `v[i]` / `m[i]` (read only).
- Statements: locals (`float a = 1.0, b;` uninitialized = 0), assignment `= += -= *= /=` (also to swizzles of locals,
  `q.xz = ...`), `++ --` (statements only, not inside expressions), `if / else`, `break`, `continue`, `return`.
  Every path of a non-void function must return.
- Loops: only `for (int i = A; i < B; i++)` style: `< <= > >= !=`, step `i++ i-- i += c i -= c`, A/B/c constant ints,
  at most 256 iterations, don't assign `i` in the body. No `while`, `do`, `switch`.
- Functions: helpers must be defined before use; no recursion; no `out` / `inout`. Params are readable in any function.
- Builtins (GLSL semantics, float and vector overloads): `radians degrees sin cos tan asin acos atan (1 or 2 args) sinh
  cosh tanh pow exp log exp2 log2 sqrt inversesqrt abs sign floor ceil fract mod min max clamp mix step smoothstep
  length distance dot cross normalize reflect transpose determinant inverse` (last three: mat2 / mat3).
- Library (same functions the built-in primitives use, local space, Y up):
  `smin(a, b, k)` / `smax(a, b, k)` (IQ quadratic smooth min/max, k = blend radius), `dot2(v)`,
  `sdBox(p, b)`, `sdTorus(p, vec2(R, r))`, `sdCappedCylinder(p, h, r)`, `sdCappedCone(p, h, r1, r2)`,
  `sdEllipsoid(p, r)`, `sdPyramid(p, h)` (unit base), `sdPyramidScaled(p, h, side)`.
- Units: `p` is in model units. Size everything from params so the shape scales sensibly (prefer params over
  literals for every length).

Diagnostics come back as `line:column: message` (1-based, into your code).

## Lipschitz (read this)

The renderer sphere-traces: it steps along each ray by the field value, trusting that the true surface is at least
that far away. The mesher uses the same bound to skip empty space. So `sdf(p) / L` must change by at most 1 per unit
of distance (1-Lipschitz). If it changes faster, rays step through the surface: holes, speckles, missing slivers,
broken FrepMesh output. If it changes much slower (L too big), it still renders correctly, just slower (more steps,
may run out of steps at grazing angles).

- Exact SDFs (`length(p) - r`, `sdBox`, library shapes, min / max / smin / smax of them, rigid moves of p): L = 1.
- `f(p * s)` for a scale s: multiply the result by `1/s`, or set L = s. Scaling inside p without compensating is the
  most common mistake.
- Gyroid `sin(x)cos(y) + sin(y)cos(z) + sin(z)cos(x)` at frequency w = 2π / cell: gradient up to ~1.5 w. Divide by w in
  the code (as in the example below) and use L ≈ 1.5 (1.5 to 2 for thin shells).
- Displacement `d + a * sin(f * p.x) ...`: adds up to `a * f` per product term to the gradient; L ≈ 1 + a·f·(number of
  terms). Keep a·f small (< 1) or the shape tears.
- `abs(f) - t` (shells) keeps f's Lipschitz bound. Domain repetition with `mod` / `fract` is fine if each cell's shape
  stays inside its cell.
- Non-uniform scaling in code (`p * vec3(1, 2, 1)`) needs L = the largest factor.
- When in doubt, raise L until holes disappear (1.5, 2, 3), then capture_views to check.

## Workflows

1. Create: `create_function` with the box where the shape goes, code, params sized to the box. Check the returned `ok`
   / `diagnostics`; fix and retry (it isn't created while the code has errors).
2. Look: `capture_views` (focus on the function's ref for detail). Holes / speckles = Lipschitz too low: raise
   `lipschitz` with `set_function_params`. Shape cut off flat = clip box too small: `update_primitive` with a bigger
   `size`.
3. Iterate on code: `set_function_code`; read `diagnostics`. Iterate on numbers: `set_function_params` (no recompile).
4. `get_function` shows the current code, values, diagnostics and `shader_error`. A shader error means the generated
   GLSL failed in the GPU compiler even though the DSL accepted it (the viewport keeps showing the last working version
   while the uniform layout still matches, e.g. param count unchanged; otherwise draws nothing). Rewrite the offending
   code; report the text if it looks like a compiler bug.
5. Compose: functions combine like any primitive: `combine` (union / subtract / intersect with blend k),
   `add_operand` / `remove_operand` / `move` to restructure, `deform` (twist / bend / repeat), `transform`.
   Prefer built-in primitives for plain shapes (cheaper, exact); use functions for what they can't do and combine.
6. Mesh: the `FrepMesh` command meshes functions with the same field (CPU copy of the code).

## Examples

Gyroid lattice filling its box (cell and wall thickness in model units; lipschitz 1.5):

```glsl
param float cell = 10.0;
param float thickness = 1.0;

float sdf(vec3 p) {
    float w = 6.2831853 / cell;
    vec3 q = p * w;
    float g = sin(q.x) * cos(q.y) + sin(q.y) * cos(q.z) + sin(q.z) * cos(q.x);
    return abs(g) / w - 0.5 * thickness;
}
```

Rounded box with a smooth spherical bite (exact pieces, L = 1):

```glsl
param float size = 20.0;
param float rounding = 2.0;
param float bite = 12.0;

float sdf(vec3 p) {
    float box = sdBox(p, vec3(size * 0.5 - rounding)) - rounding;
    float ball = length(p - vec3(size * 0.5, size * 0.5, 0.0)) - bite;
    return smax(box, -ball, rounding);
}
```

Twisted star column (2D star extruded, twisted along local Y; twist adds ~rate·radius to the gradient, so L ≈ 1 + rate·radius):

```glsl
param float radius = 10.0;
param float height = 40.0;
param float rate = 0.05;      // radians per unit height

float sdf(vec3 p) {
    float a = p.y * rate;
    vec2 xz = vec2(cos(a) * p.x - sin(a) * p.z, sin(a) * p.x + cos(a) * p.z);
    float ang = atan(xz.y, xz.x);
    float r = radius * (0.75 + 0.25 * cos(5.0 * ang));
    float d2 = length(xz) - r;
    return max(d2 * 0.7, abs(p.y) - height * 0.5);   // * 0.7: the star's r(ang) isn't an exact distance
}
```

Sphere with sine bumps (amplitude · frequency = 0.5, so L ≈ 1.5):

```glsl
param float radius = 10.0;
param float amp = 0.5;
param float freq = 1.0;

float sdf(vec3 p) {
    return length(p) - radius + amp * sin(freq * p.x) * sin(freq * p.y) * sin(freq * p.z);
}
```
