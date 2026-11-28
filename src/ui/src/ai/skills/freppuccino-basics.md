---
name: freppuccino-basics
description: Coordinate system, primitives, colors, CSG semantics, blending rules and when to deform. Always loaded.
always: true
---
## Space and scale
- World coordinates, Z is up, XY is the ground plane. Build objects standing on z = 0, centered on the origin in X/Y,
  unless the user says otherwise or the scene already has something there (then place the new object beside it).
- Use real-world sizes in the document's units. A coffee cup is ~95 mm tall (3.7 in, 0.095 m). Convert if the units
  aren't millimeters. If the user gives no size, pick a typical real one.

## Primitives (create_primitive)
- sphere(center, radius)
- box(center, size): size is FULL lengths, not half. Rotate with x_axis / y_axis.
- ellipsoid(center, radii): radii are semi-axes (half lengths). Great for organic shapes.
- cylinder(start, end, radius): start / end are the cap centers, so the axis can point anywhere.
- cone(start, end, start_radius, end_radius): one radius 0 = pointed cone; both > 0 = frustum (tapered cylinder).
- torus(center, axis, major_radius, minor_radius): the ring lies in the plane perpendicular to axis.
  axis [0,0,1] = lying flat; axis [0,1,0] = standing up in the XZ plane (like a mug handle seen from the front).
- pyramid(base_center, height, base_size, axis): square base, apex at base_center + axis * height.
- Every primitive takes an optional color ("#RRGGBB" or a name). Give parts their real colors (a tan cone, red stripes);
  colors blend smoothly across blended joins. Uncolored parts render light grey.

## Colors (set_color)
- set_color recolors existing primitives without touching their shape: refs + color for one color, or
  colors: [{ref, color}, ...] to paint many parts in one call (one undo step). A node ref paints every primitive under it.
- update_primitive also takes color, but use set_color for color-only changes, especially several parts at once.
- How colors combine: add / intersect rows fade between the colors across the blend (k); a subtract row keeps the
  color of the result so far on the cut faces (the cutter's color never shows); deforms keep the child's colors.
  So to color a cut surface (a cup's inside), color the base, or add a thin colored part inside it.
- get_scene lists every primitive's color.

## Deforms (deform tool)
- twist, bend and repeat wrap one finished part: spirals and swirls, coils, ropes, hooks and curved parts, arrays and
  patterns of identical features (studs, pickets, holes). Load the deformations skill before using them.
- Repeat beats creating many identical primitives: it is one primitive in the shader budget, and edits apply to every copy.

## CSG (combine, groups)
- Roots (anything not used by an operation) are all drawn, merged with a sharp union.
- combine makes a group and consumes its operands: they stop being roots. Build bottom-up: small sub-assemblies first
  (e.g. the handle), then combine those nodes. Don't put the same operand into two groups.
- A group reads top to bottom like a feature history: it starts empty, and each operand adds to, subtracts from or
  intersects with the result of the operands above it (its mode, listed in get_scene "modes"). combine's op only picks
  the starting modes (subtract: everything after operands[0] cuts). set_mode changes one operand; one group can mix
  all three, e.g. [shell, +flange, -bore, ∩envelope], instead of nesting a union inside a subtract.
- A hidden or missing operand is skipped. Subtract and intersect operands with nothing added above them do nothing, so
  a group whose first shape is hidden or deleted starts at its next add operand. Moving an operand to index 0 makes it add.
- Editing a primitive (update_primitive / transform) updates every operation above it. Prefer editing over deleting.
- Restructure in place: add_operand / remove_operand change an existing group's operands (add_operand takes a mode);
  move re-parents or reorders a primitive or node (to another node, or to the root with no "to"); reordering a group
  changes its shape. A node keeps ≥ 1 operand; a deform exactly 1 (move into it to swap its target). Node refs (N#)
  never change.
- Order matters for cavities: add the outside parts first, THEN the subtract row for the cavity, so the cut also clears
  any part that pokes into it (e.g. the inner half of a handle torus).

## Blending (k)
- k is a blend radius in model units. 0 = sharp crease. > 0 = smooth, fillet-like transition of roughly that size.
- An add row with k adds material in the joint (a fillet). A subtract row with k rounds the cut edge. An intersect row
  with k rounds the edges of the overlap. One k per group, for every join in it.
- Pick k relative to the smaller feature: ~5-15% of its size for organic joins, 0 to 2% for crisp mechanical parts.
  Too big makes everything blobby and swallows small parts. Too small leaves a visible crease.
- Parts closer than k get pulled together even if they don't touch. To keep small details (eyes, buttons) crisp,
  combine them in a separate operation with a small k.
- A blend only acts between the operands of that one group.

## Practical rules
- Unioned parts should overlap a little (or use k > 0). Exactly touching surfaces render as a seam.
- A cutting tool must stick out past the surface it opens (5% or more), or a paper-thin skin remains.
- Walls thinner than ~2% of the object's size look broken in the render.
- Keep it to roughly 40 primitives or fewer: the viewport shader has a budget (get_scene warns when it is full) and
  every primitive costs render time.
- Create several primitives per turn with parallel tool calls. Capture after a meaningful stage, not after every primitive.

## Beyond primitives
- FFunctions (create_function): your own SDF code in a box, for what primitives can't do: lattices, gyroids / TPMS,
  foams, minimal surfaces, ribs, textures. Load freppuccino-ffunction (and lattices for lattice work); start from
  get_function_example. They combine, deform and color like any primitive.
- wrap op=offset on a group node shifts its whole surface (negative = thinner); wrap op=blend
  on a repeat node fillets its copies together. unwrap removes a node and keeps its children.
- The user's selection is in get_scene ("selection"). End by selecting the finished part (select) so they can tweak it.
- mesh bakes roots into a Rhino mesh, only when asked. set_zebra checks blend smoothness in captures.
