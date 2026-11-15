# Viewport shaders: design (approach B, native GPU)

Goal: a Rhino 9 display mode ("Freppuccino") whose viewport content comes from runtime-generated shaders, drawn on Rhino's own GPU device/encoder. Mac (Metal) first; Windows (D3D11) is next, same shader source.

## 1. Shader definition

One source language: **GLSL 450 (Vulkan dialect)**, generated as strings at runtime.

```
ShaderSource(string Vertex, string Fragment)   // GLSL text; Hash = SHA1(Vertex + \0 + Fragment)
   │ shaderc (shaderc_compile_into_spv, target env vulkan1.0)
   ▼
SPIR-V (per stage)
   │ SPIRV-Cross C API
   ├─► MSL 2.1   → MTLDevice newLibraryWithSource → entry "main0"        (Mac)
   └─► HLSL SM5  → D3DCompile vs_5_0/ps_5_0 → CreateVertex/PixelShader  (Win, phase 2)
```

Conventions every generated shader follows:
- Clip space = **Metal/D3D convention** (y up, z ∈ [0,1]). No flips are inserted by the cross-compiler, so both targets agree.
- Vertex stage takes **no vertex buffers**; geometry is derived from `gl_VertexIndex`. `ShaderSource.VertexCount` (default 3) = triangle-list vertex count.
- Per-draw data: at most one `layout(std140, set=0, binding=0) uniform` block, vec4/mat4 members only, ≤ 4 KB (`Uniforms`). Bound to both stages: Metal `setVertexBytes`/`setFragmentBytes` at `[[buffer(30)]]` (remapped by SPIRV-Cross; high index so Rhino's cached low buffer slots aren't clobbered), D3D11 4 KB default-usage cbuffer at `b0` (`UpdateSubresource`; Manual mode saves/restores VS/PS CB slot 0).
- Fragment output: `layout(location=0) out vec4`.

Programs:
- `Shaders/FrepShaderGen.cs`: scene. SDF prims (`Scene/FrepScene.cs`) + per-doc CSG DAG (`Csg/FrepTree.cs`: FUnion/FIntersect/FDifference nodes, IQ quadratic smin/smax, per-node k) compiled to `map()`; parentless prims/nodes hard-min'd at root. FTwist/FBend/FRepeat nodes warp the point for their subtree (`opTwist`/`opBend`/`opRepeat`; each warp = new point context, subtree re-emitted per context), driven by a live Rhino line/box (`Csg/FrepRefs.cs`); deleted line/box = child drawn unwarped; twist/bend divide the child distance by a local Lipschitz bound. Raymarched on a fullscreen triangle, only inside `bound` (sphere around the warped scene's box, from in-memory prims/warps so it follows drags). Structure baked into source (cached by generated map + color code); camera/frustum/bound/prims/colors/warp frames/k's are uniforms. Color: each prim's `SDF.Color` user text (`"#RRGGBB"`, white `#FCFCFC` by default / if missing, set from the panel; packed 0xRRGGBB floats, 4 per row after the prim rows); `sceneColor()` replays the DAG (incl. warp contexts) over `vec4(rgb, d)` once at the hit point: union/intersect mix operand colors by the smin/smax weight (pure away from the blend, fading across a smooth fillet; k = 0 picks the nearer operand), difference keeps the base's color, warps keep the child's. Shade: `color * mix(0.78, 1, n.z*0.5+0.5)`. Misses `discard`; depth off. Silhouette inset by `FrepShaderGen.SilhouetteInsetPx` (1 px, `params.x`: SDF + inset × pixel footprint at t) so Rhino's wires drawn underneath stay visible around the shading. 4 KB uniform cap (prims past it dropped).
- `Shaders/PillShader.cs`: "FREP MODE" badge background (old debug triangle's RGB as a gradient in an SDF rounded rect, 6 verts); text by Rhino `Draw2dText` in DrawForeground. Placed top-left just below Rhino's viewport title (`TitleClearance`) so view names don't overlap it.

### Scene uniform layout

FZebra (`Commands/FZebraCommand.cs`, window `Ui/ZebraOptionsForm.cs`, session-only `FrepSettings.Zebra*`): like Rhino's Zebra, the command opens a modeless "Zebra Options" window (stripe direction vertical/horizontal, stripe size thin↔thick, two stripe colors, all live); zebra is on while it is open, closing it (or `FZebraOff`) turns it off. Header row `zebraCfg` = (stripe count, 0 = off; 1 vertical / 0 horizontal; color 1; color 2 as packed 0xRRGGBB). `sceneColor()` also flags hits within the pixel tolerance of any FUnion node's surface (fillets included); those pixels are drawn unlit in bands of the reflected view direction's angle from the camera's right (vertical) or up (horizontal) axis. Uniforms only, so changing options never recompiles.

Header (8 vec4 rows, `HeaderRows`): camPos, camX, camY, camZ, frustum, params, bound, zebraCfg. Then data rows in `Compile()` order: prims (sphere 1 row, others 3), prim colors (4 per row), warp rows (one block per live warp, tree order), displace rows (4 per visible Displace node, tree order), node k's (4 per row). `PrimRowBudget(tree)` = data rows − k rows − warp / displace rows (prims + their color rows must fit); prims past it are dropped.

### Warps (FTwist / FBend / FRepeat)

One operand each. Node stores `Ref` (Guid of a Rhino line or box, read live via `Csg/FrepRefs.cs`; hidden ok) + `Params`. Moving the ref (incl. gumball drag) updates the warp without recompiling.

| Op | Ref | Params | Rows | Behaviour |
|---|---|---|---|---|
| Twist | line | angle (°, total start→end) | 2: (start, rad/unit), (dir, extent) | rotate about the infinite axis; linear past the ends |
| Bend | line | angle (°, over line length), direction (° about axis; 0 = world +Z ⟂ axis, +X if axis vertical) | 3: (start, L), (dir, β), (n, minR) | arc of radius L/β; before start unchanged, past end rigid tangent continuation |
| Repeat | box | x, y, z spacing (model units) | 5: (anchor, sx), (X, sy), (Y, sz), lo, hi | lattice anchored at child bbox centre, axes = box frame; cell = clamp(round(local/s), lo, hi), lo/hi so copies stay in the box |

- Codegen: each warp opens a point context `q{c}`; its subtree is re-emitted in that context (memoized by operand × context).
- Distance: twist/bend divide the child value by a per-point Lipschitz bound (`l{c}`, clamped by child extent); repeat doesn't (clips a child wider than the spacing).
- Missing cases: ref deleted / wrong type → passthrough (child unwarped; panel says so); child missing → empty; node hidden → absent.
- `bound` (raymarch sphere): computed per frame from in-memory prims/warps (`FrepShaderGen.Bound`), so it follows drags. Prims = sphere around frame origin; union/intersect = union of operand boxes; difference = base; hidden node = absent; warps via `FrepField.WarpBox` (shared with the mesher): twist = cylinder about axis over child's axial span, bend = child box ∪ annular arc sector ∪ rigidly moved tail corners, repeat = child box ⊕ farthest cell offsets. Padded by Σk × max ellipsoid stretch. `FrepScene.Bounds` (warp extent/anchor, nested warps) uses the same `Warped`.
- Shared math: `Csg/FrepWarpMath.cs` (ref resolution, `SdWarp` extent/anchor, packed rows, bend frame) feeds both the shader uniforms and the mesher.
- Mesher (`Meshing/FrepField.cs`): line-for-line port of `opTwist`/`opBend`/`opRepeat` with per-warp point contexts; keep in sync with the GLSL. Only diff: repeat rounds halves away from zero (GLSL `round` is driver-defined on exact cell boundaries).
- Known: bend `lip` jumps between arc and straight regions (field shrinks, surface unaffected); twist/bend Lipschitz clamp only holds within child extent; repeat assumes the child fits one cell.

### Displace (FDisplace): surface textures

One node: Operands = [child] or [child, mask], Params = pattern (0 Perlin, 1 Voronoi, 2 knurl), amplitude, scale, fade; Ref = optional knurl axis line. Everything is a uniform (4 rows, `Csg/FrepDisplaceMath.Rows`), so switching pattern or dragging a slider never recompiles.

`opDisplace = (d − a · w · h(p)) / L`, with h ∈ [0, 1] the pattern height, a the amplitude (> 0 raises, < 0 engraves) and w ∈ [0, 1] the mask weight: `clamp(1 − max(m, 0) / fade, 0, 1)` for the mask's value m (1 inside, 0 beyond fade; fade ≥ 1% of scale so it is never a step). `L = 1 + |a| · (max|∇h| + 1/fade)` keeps the field Lipschitz 1, so raymarching and meshing stay safe; L is applied everywhere (also where w = 0) so the field stays continuous.

| Pattern | h | max|∇h| (× 1/scale) |
|---|---|---|
| Perlin | `clamp(0.5 + 0.65 · fbm(p / scale))`, fbm = 3 octaves of quintic gradient noise (Perlin's 12 edge gradients), weights 1, ½, ¼ / 1.75 | 0.65 × 4.0: max|∇fbm| measured 3.52 over 3M samples (one octave 3.31; the all-octaves-aligned worst case 5.7 never occurs); 4.0 ≈ 15% margin |
| Voronoi | `min((F2 − F1) / 0.24, 1)` in cell units (3×3×3 search, feature points kept 5% off the cell faces): raised cells, grooves between | 2 / 0.24 (F1, F2 are distances, Lipschitz 1 each) |
| Knurl | `min(tri(θK + z/s), tri(θK − z/s))` around the axis, K = whole teeth / 2π (no seam at θ = ±π), faded in between 0.4 R and 0.7 R (R = child bbox corner distance from the axis / √2) | 2 √((K/0.4R)² + 1/s²) + 1/0.3R (exact bound) |

- Hashing: PCG3D (Jarzynski & Olano 2020), integer-only, so CPU and GPU pick the same gradients / cell points.
- Mask: not drawn (it is an operand, so not a root); hidden or deleted mask = textured everywhere. Objects under a mask are kept in the shader even when hidden (`FrepScene.MaskObjects`), since users hide them.
- Knurl axis: the Ref line (live, follows drags), else vertical through the child's bbox centre (recomputed on rebuild).
- Bounds: child box grown by max(a, 0) (`Bound`, `FrepScene.Bounds`); mesher asks the child for `t · L + max(a, 0)`.
- Mesher: `Meshing/FrepNoise.cs` is the line-for-line C# port (`FrepField` calls `FrepNoise.Displace`); keep in sync with the GLSL.
- Validated (scratch harness): every pattern × positive / negative amplitude × masked stays Lipschitz ≤ 1 over 300k point pairs; bounds hold; knurl seam jump 0; DualContour meshes watertight.

Original slice program (removed):

```glsl
// vertex
#version 450
layout(location = 0) out vec3 vColor;
const vec2 P[3] = vec2[](vec2(-0.5,-0.5), vec2(0.5,-0.5), vec2(0.0,0.5));
const vec3 C[3] = vec3[](vec3(1,0,0), vec3(0,1,0), vec3(0,0,1));
void main() { vColor = C[gl_VertexIndex]; gl_Position = vec4(P[gl_VertexIndex], 0.0, 1.0); }

// fragment
#version 450
layout(location = 0) in vec3 vColor;
layout(location = 0) out vec4 outColor;
void main() { outColor = vec4(vColor, 1.0); }
```

Native deps: `Silk.NET.Shaderc.Native` + `Silk.NET.SPIRV.Cross.Native` (2.23.0) for binaries only (osx-arm64, win-x64, win-arm64 present). Bindings are our own ~15 P/Invokes (handle-based C APIs, no struct marshalling). A `NativeLibrary.SetDllImportResolver` on our assembly maps `shaderc_shared` / `spirv-cross` → `<plugin dir>/runtimes/<rid>/native/…` because Rhino's plugin load context may not honor `deps.json` native probing.

## 2. Plugin wiring

```
FreppuccinoPlugin (AtStartup)
  OnLoad: NativeLibs.Install(); FreppuccinoDisplayMode.Ensure(); Conduit = new ShaderConduit { Enabled = true }
FreppuccinoDisplayMode.Ensure()   FindByName ?? CopyDisplayMode(GhostedId) + 80% transparency + UpdateDisplayMode   (Ghosted underlay; recreated if missing or not ghosted at 80%, e.g. an older Wireframe copy)
ShaderConduit.PostDrawObjects(e) if mode active: Draw(scene) if root non-empty; Draw(pill) → RhinoStateReset.Apply(dp) once
ShaderConduit.DrawForeground(e)  badge text
Commands FUnion/FIntersect/FDifference  add CSG node (Blend option); FTwist/FBend/FRepeat wrap one operand, pick axis line / box; FrepPanel lists the tree (edit op/k, delete, click node = pick it)
GpuBackends.For(p)               switch p.Technology: Metal → MetalBackend, Direct3d11 → D3D11Backend, else null
Command "Freppuccino"             set active viewport to the mode
```

Files: `Display/` (mode, conduit, state reset), `Shaders/` (source, compiler, native resolver), `Gpu/` (interface, `Metal/`, `D3D11/`). `Hack/` is excluded from the build, kept for reference.

Draw order (observed, Mac): unselected objects → **PostDrawObjects (us)** → DrawForeground → selected/highlight pass → DrawOverlay (gumball, axes icon). So: wires under us, selected wires + gumball over us.

**State cache (B1)**: Rhino skips rebinding pipeline/depth state it thinks is bound, so draws after ours (highlight pass) vanished. Mitigation: `RhinoStateReset` draws one Rhino line just behind the camera (GPU-clipped) right after our draw, forcing a rebind. Proper fix (internal offsets / McNeel invalidate API) deferred until something breaks.

Target behaviour (parity with hack): Rhino wireframes · shader layer over them · selected geometry over shader layer · gumball over all.

## 3. Metal backend (Mac)

Access to Rhino's Metal objects (not in RhinoCommon; exported by RhCore, called with the native pipeline pointer from `Interop.NativeNonConstPointer(DisplayPipeline)`):

```csharp
// RhCore: /Applications/Rhino 9.app/.../RhCore.framework/RhCore, resolved via NativeLibrary.GetExport
CRhinoDisplayPipeline::GetMetalDevice()               _ZN21CRhinoDisplayPipeline14GetMetalDeviceEv
CRhinoDisplayPipeline::GetMetalCommandQueue()         _ZN21CRhinoDisplayPipeline20GetMetalCommandQueueEv
CRhinoDisplayPipeline::GetMetalRenderCommandEncoder() _ZN21CRhinoDisplayPipeline28GetMetalRenderCommandEncoderEv
// called as delegate* unmanaged<IntPtr /*this*/, IntPtr>  (arm64: this in x0, id in x0)
```
RhCore path is found from the loaded process image (`dyld` / `NativeLibrary.Load("RhCore")` fallback to the bundle path derived from `typeof(RhinoApp).Assembly.Location`), not hardcoded.

ObjC interop: `objc_msgSend` cast to typed `delegate* unmanaged<…>` per signature; selectors cached; `objc_autoreleasePoolPush/Pop` around pipeline builds.

Per-program, per-pass-format state (cached in `MetalBackend`):
```
key = (ShaderSource.Hash, MetalPassFormat { color=RGBA8Unorm(70), depth=stencil=Depth32Float_Stencil8(260), samples })
MTLLibrary×2 → MTLFunction "main0" ×2
MTLRenderPipelineDescriptor: vertex/fragment fn, colorAttachments[0].pixelFormat, depth/stencilAttachmentPixelFormat, rasterSampleCount
MTLRenderPipelineState
MTLDepthStencilState (compare Always, write off)  — shared
```
Draw on Rhino's borrowed encoder (never `endEncoding`, never retain/release it):
```
setRenderPipelineState: pso; setDepthStencilState: dss; setCullMode: None
drawPrimitives: Triangle vertexStart: 0 vertexCount: 3
```
Lifetime: all objects we create are long-lived and owned by the backend (released on plugin shutdown). No per-frame resources yet, so command-buffer retention isn't an issue for the slice.

### Pass format detection (open problem)
Formats/sample count cannot be queried from an `MTLRenderCommandEncoder`. Plan:
1. Default `{RGBA8Unorm, D32S8, 4}` (observed on M2 Pro, Rhino 9.1, both AA=Good and AA=None).
2. If the encoder is an `MTLDebugRenderCommandEncoder` (validation layer on), parse its `-description` (contains the full render pass descriptor) and use that; log mismatches against the default.
3. Ask McNeel for `GetMetalRenderPassDescriptor()` (or formats + sample count), mirroring the D3D11 accessors they added on request.

## 4. Windows (phase 2) — see `windows-validation.md`
`GetDirect3d11Device/DeviceContext` (RhinoCommon) → HLSL via SPIRV-Cross → `D3DCompile` → shaders; save/restore state around our draw with `ID3D11DeviceContext1::SwapDeviceContextState` (clean full-state save/restore, which Metal lacks). Bindings: Vortice.Direct3D11 or hand-rolled COM vtable calls.

## 5. Validated (spikes, Rhino 9.1.26314, M2 Pro, MTL_DEBUG_LAYER=1)
- Getters return non-null device/queue/encoder from C# in conduit channels; all on UI thread.
- PreDrawObjects / PostDrawObjects / DrawForeground share one encoder; DrawOverlay uses a different pass.
- Render pass: color0 RGBA8Unorm 2DMultisample ×4 (resolve RGBA8Unorm ×1), depth+stencil Depth32Float_Stencil8 ×4.
- GLSL→SPIR-V→MSL 2.1 / HLSL SM5 via shaderc + SPIRV-Cross natives from .NET 10: works.
- MSL compiled at runtime with Rhino's device; pipeline with the formats above; triangle drawn in PreDrawObjects: **zero validation errors**, renders correctly in screen + view capture.
- Rhino objects drawn *after* our draw (box, sphere) rendered correctly; i.e. no visible fallout from our pipeline/depth/cull changes in that test.
- ObjectCulling hides doc objects; grid + axes icon remain.
- Metal pass sample count did **not** follow `OpenGLSettings.AntialiasLevel` (None, after restart: still ×4). `SupportedMultisamlpleModes(Metal)` = 1,2,4.

## 6. Risks / unknowns
- **State cache**: see §2. Dummy-line reset only proven for line drawing (highlight wires); shaded meshes/text/dots after our draw unverified.
- Pass format variance (MSAA level, HDR/linear workflow, view capture to file/print, layouts/details) — only defaults observed.
- Undocumented C++ symbols may change between service releases → resolve at runtime, fail soft (log once, draw nothing).
- Layout/detail views: conduit restricted to model space for the slice.
