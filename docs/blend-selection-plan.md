# Blend selection and outline-only control shapes: plan

Agreed plan (draft 2 plus astra's clarifications from room message #118). Written by fable; architecture approved by astra; corrections from sol and astra (room messages #115 and #116) included. Base: `origin/main` a3026f4. Implementer: opus, branch `blend-selection`. Reviewer: sol. Final review: fable and astra.

## What the user asked for

1. Click anywhere on a rendered blend: the whole blend is highlighted as one selection-coloured outline, and a gumball moves, rotates and scales it as a single object (the root of the model tree).
2. `FrepMesh` bakes only the selected blends.
3. Shift+click selects one of the shapes that make up a blend, to manipulate it on its own (a leaf of the tree).
4. The individual shapes draw as outlines only, but are still selected by clicking anywhere on their surface.
5. Selecting in the scene selects the matching nodes in the model tree.

## Terms

- **Leaf**: a Rhino brep carrying `SDF.*` user text.
- **Root**: a parentless node of the CSG tree, or a parentless leaf. "Blend" in the request means a root.
- **Warp reference**: the Rhino line or box a Twist, Bend or Repeat node points at.

## Decisions agreed (fable, astra, with sol's corrections)

- Selection is explicit state, per document. It is never inferred from "all of a root's leaves happen to be selected".
- A root transform is one affine applied to the completed root field, so fillets stretch with a non-uniform scale. It is stored in the tree.
- There is one source of field geometry. What Rhino shows (the real breps and reference objects) is derived from it, not a second source.
- Root hits are tested against the same field the viewport draws, built by the existing `BuildField`, warps included. No second CSG interpreter.
- Display stays on the Ghosted-based mode. Control breps become invisible; we draw their outlines ourselves.
- `FrepMesh` never falls back to baking the whole scene.

## Design

### 1. Geometry model: canonical data and control instances

Everything else rests on this.

- **Canonical leaf**: the `SDF.*` values, as today, plus an optional general affine placement `P` stored beside them. The leaf field is `sd(P⁻¹ p) × s(P)`, where `s` is the smallest singular value of the linear part. The per-type handlers still run first, so the readable values stay meaningful (a moved sphere keeps a centre and a radius). Anything they cannot represent goes into `P` and is no longer rejected. Example: under a root scaled by (2, 1, 1), an ordinary 45-degree rotation of a box leaf arrives as a sheared matrix; it is accepted through `P`.
- **Canonical warp reference**: the axis line or box in the warp node's own space. Each reference object stores `A`, the accumulated root transforms that have been applied to its real geometry. The canonical geometry is `A⁻¹ ×` the object's geometry. `FrepWarpMath.TryResolve` uses the canonical geometry, never the raw object geometry, so a root transform is not applied twice.
- **Control instance**: one per path from a root to a leaf or reference. Its transform `T` is the product of the `Transform` nodes on that path. A leaf's control shape in the world is `T × P × canonical shape`. Warps do not deform control shapes: a control shows the pre-warp shape, as today.
- **The real Rhino object** is the control instance of the object's first path, kept in place as a derived display update. Other instances of a shared object are drawn by us and have no Rhino object.

### 2. Selection state (`Selection/FrepSelection.cs`, new)

- `Roots`: ordered set of `FrepOperand`. `Leaves`: ordered set of (root, path, object id), so a shared leaf knows which instance was picked.
- One writer API (`SelectRoot`, `SelectLeaf`, `Clear`, with replace, add and toggle modes) that updates, under a re-entrancy guard:
  - the Rhino object selection (section 4 says which objects),
  - the panel's node selection in `UiController` (the root's row for a root; the object's row or rows for a leaf),
  - a redraw.
- The panel's existing `SelectRequest` calls the same API, so tree to scene works the same way.
- Entries are dropped when their node or object is deleted or hidden, on undo and redo, and on document switch.

### 3. Picking (`Selection/FrepPicker.cs`, new)

- A `Rhino.UI.MouseCallback`, enabled while a view is in the Freppuccino mode.
- It acts on a left click that ends without a drag, when the cursor is not over a gumball (`IsOverGumball`) and either no command is running or the running command has opened an explicit F-rep selection session (section 6). Every other command is left alone.
- **Plain click** marches the click ray through the final field of each visible root. Nearest hit: `SelectRoot(root)`, replacing the selection.
- **Shift+click** marches the ray against every visible control instance, each as its canonical leaf field under `T × P`. That is exactly the shape whose outline is drawn, not the warped leaf. Nearest hit: `SelectLeaf(root, path, leaf)`. This covers exposed cutters, where there is no root hit.
- **Plain click that misses every root but hits a control instance**: select that instance's root. The instance carries its root, so nothing is ambiguous.
- No hit at all: Rhino's normal behaviour (deselect). Non-F-rep objects are never touched.

### 4. Root transform

As built (replaces the unary `Transform` node of draft 2; agreed by fable and astra, room #142/#143):

- Every node may carry an optional affine matrix `M` (`FrepNode.Matrix`, tree chunk 1.3). Transforming a selected root composes `X × M` into that root's own matrix. The root keeps its id, its tree row and its selection; a wrapper node would have replaced them. `M` stays with the node if it later becomes an operand. A control instance's `T` is the product of the matrices on its path.
- Field: `node(M⁻¹ p) × s(M)` (a point context of its own, like a warp), in the shader (4 uniform rows per such node) and in `FrepField`. Bounds: the node's sublevel `{field <= t}` is `M ×` its body's sublevel at `t / s(M)`. With any matrix in the scene, the viewport's bounding sphere is taken from these CPU sublevel bounds.
- Gumball carrier (spike result): one invisible `FrepCarrier` custom mesh per visible node root, geometry = the root's world box, kept on Idle outside undo, never drawn or picked by Rhino (the picker selects it), excluded from discovery and baking, removed before a save and rebuilt after (selection restored), plain tagged leftovers purged, `doc.Modified` untouched. Selecting a node root = selecting its carrier, so Rhino's Esc / SelNone / deselect apply natively; the panel row mirrors it both ways. Parentless primitives are their own carrier.
- Selection rule, amended (#147/#148): a natively selected carrier (SelAll, window, SelLast) is a root selection; a natively selected control brep is a leaf at its primary instance; never a root inferred from selected descendants. When roots and leaves reachable from them are transformed together, the transform applies once, to the roots.
- On commit (the transform's undo record), the transform X goes into the root's `M`. As a derived display update, each real object that is an instance under that root has X applied to its geometry and composed into its `A`. `P` and the `SDF.*` values do not change. The tree edit and the object updates share one undo record.
- During a drag, `FrepScene.Get` previews the dynamic transform as a temporary root matrix. Nothing is written until commit, so a cancelled drag leaves no trace.
- Editing one Shift-selected leaf by a world transform X: the leaf's own change is `T⁻¹ X T`, applied through the handlers with `P` as the fallback.
- Derived updates happen once per object, by primary ownership (leaves: primary instance's root; warp references: first root whose subtree uses them). No moving leaf reachable from a transformed root gets a canonical update.
- Shared objects: a root transform changes only that root's `M`, so the other root does not move. The shared object's real geometry follows its first path; its instance under another path is drawn by us. Shift-editing a shared leaf changes the canonical leaf, so it moves in every root that uses it (today's linked behaviour), and all of its tree rows are selected.

### 5. Drawing

- Display mode: Ghosted copy, surface transparency 100%, isocurves and edges off. Version the mode so older copies are replaced.
- Blend: unchanged, plus a selection-coloured band where a ray just misses a selected root. Driven by a per-root "selected" uniform, so selecting never recompiles. Colour from Rhino's selected-object colour.
- Control outlines: `Silhouette.Compute` per control instance, on the canonical brep under `T × P`, cached by (object, geometry version, instance transform, camera). Drawn in the conduit before the shader pass, so the blend covers them as today. A selected leaf's outline is drawn after the shader, in the selection colour.
- Rhino's own highlight of F-rep objects is suppressed in `PreDrawObject`, so a selected root shows one outline and a selected leaf shows its outline, not full wires.

### 6. `FrepMesh`

- Reads the selected roots at the start, before its own output is added and selected.
- Builds the field from those roots only, each with its full reachable sub-graph, per-node blends, warps and transforms.
- Nothing selected, or only leaves selected: the command opens an explicit F-rep selection session, in which plain clicks select roots through the picker, and prompts to pick blends. Enter with none picked ends the command. It does not bake everything.
- A parentless leaf is a root and bakes as one.

## Implementation clarifications (astra, approved with draft 2)

These refine sections 1 to 4 and take precedence where they differ.

- **Leaf edit fallback.** For a leaf edit `Q = T⁻¹ X T`, the fallback is `P_new = Q × P` with the canonical `SDF.*` values unchanged. Once `P` is not the identity, do not pass `Q` to the per-type handlers: that composes in the wrong space. Baking a transform into the `SDF.*` values is only valid with a compensating change to `P`.
- **Gumball at every selected instance.** A selected instance needs its gumball at that instance, including a shared leaf picked through a path that is not its primary one. Selecting the primary Rhino object is not an adequate fallback, and selecting real descendants alone cannot satisfy this. The spike must establish a proxy or a custom gumball for this case.
- **Coincident control instances.** Two instances from different roots can give equal-depth hits. Carrying the root identifies each candidate but does not break the tie: use a chooser or an explicit no-selection message, never silently the first path. Keep primary-path ownership stable, and rebuild derived geometry after topology or undo changes.
- **Uniform layout for `P`.** A general affine needs at least three vec4 rows for its inverse, plus storage for the distance scale unless that is packed elsewhere. It is not one extra row. Account for it in the CPU and GPU layouts and in the capacity tests.

## Order of work

0. **Spikes (gate).** Real clicks in Rhino 9, Windows:
   - `MouseCallback` timing relative to Rhino's own pick, with gumball drags left intact;
   - suppressing a selected object's draw leaves its gumball working;
   - proxy carrier: the gumball attaches, `BeforeTransformObjects` reports the full affine, cancel and undo behave;
   - a click on an invisible face in the Ghosted copy at 100% transparency (needed only if Rhino's own pick is used as a fallback anywhere).
1. Geometry model: leaf placement `P`, canonical warp references, control instances. No behaviour change; today's scenes must render and mesh identically.
2. Selection state and scene to tree sync, with root picking by ray.
3. Display: invisible control breps, instance outlines, shader selection band, highlight suppression.
4. Shift+click picking on control instances, and the leaf outline highlight.
5. Root transform: `Transform` node in tree, shader, CPU field, bounds and serialization (bump the tree's chunk minor version); gumball carrier; preview; undo; derived updates of real objects.
6. `FrepMesh` on selected roots, with its selection session.
7. Shared-object instances under a transformed root.

Steps may land as separate commits. The feature is not merged as complete without root transforms (step 5) and the shared-root acceptance case (step 7).

## Acceptance cases (for sol; from astra's list, with sol's additions)

- Click the smooth bridge between two separate source shapes: its root is selected, only the blend's silhouette is highlighted in Rhino's selection colour, and its tree row is selected.
- Shift+click a control shape's face away from every edge: that leaf and its tree row are selected, and moving it moves only that leaf. Include a cutter face outside the final surface, and an ordinary rotation of a box leaf under a non-uniformly scaled root.
- Move, rotate, uniform scale and non-uniform scale a root containing a blend and a warp with an axis or Repeat-box reference. The warp is not transformed twice. Preview, commit, cancel, undo, redo and save/reopen agree between shader, picking, control outlines and baked mesh. A shared operand does not move in another root.
- Two roots and a standalone shape: `FrepMesh` bakes only the selected roots. With nothing or only leaves selected it never bakes the scene. The new mesh being selected does not change what was baked.
- Ordinary Rhino objects, running commands, gumball handles and other display modes behave as before. Hidden and locked objects are not selectable through our picker.
- Scene and tree selection round-trip without event loops, and survive hide, delete, undo and document switch without stale ids.

## Known risks

- Everything in step 0 is unverified by a real click.
- `Silhouette.Compute` cost while orbiting with many leaves. Mitigation: skip recomputing during a dynamic view change and refresh on idle.
- Non-uniform transforms make the field a conservative distance, which slows raymarching and meshing near strongly stretched shapes.
- The selection band costs one more root evaluation per march step for selected roots.
- Leaf placement `P` adds at least three uniform rows per placed primitive, which lowers the number of primitives that fit in the shader's uniform block.
- Mac is untested for all of this. The picker and conduit code is shared, but `MouseCallback` behaviour may differ.

## Open product questions (defaults chosen, the user can redirect)

- Control outlines show only where the blend does not cover them (today's behaviour), not through the blend.
- Control shapes under a Twist, Bend or Repeat show the pre-warp shape, as today, so a control can sit away from the part of the blend it produces.
