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

## Deforms (deform tool)
- twist, bend and repeat wrap one finished part: spirals and swirls, coils, ropes, hooks and curved parts, arrays and
  patterns of identical features (studs, pickets, holes). Load the deformations skill before using them.
- Repeat beats creating many identical primitives: it is one primitive in the shader budget, and edits apply to every copy.

## Textures (texture tool)
- texture(target, pattern, amplitude, scale, mask?) adds surface detail to a finished part: knurl for tool / flashlight /
  knob grips (diamonds wrap around axis_start → axis_end; default vertical through the part), voronoi for scales, pebbles,
  leather or cracked glaze, perlin for organic skin, rock, bark or hammered metal.
- Sizes: scale ~3-10% of the part, amplitude ~a quarter of scale (0.3 mm on a handle, a few mm on a boulder). Negative
  amplitude engraves. Too much amplitude for the scale looks spiky and slows the viewport.
- mask confines it: e.g. a cylinder or box around just the grip area; fade softens the edge. The mask is not drawn.

## CSG (combine)
- Roots (anything not used by an operation) are all drawn, merged with a sharp union.
- combine consumes its operands: they stop being roots. Build bottom-up: small sub-assemblies first (e.g. the handle),
  then combine those nodes. Don't put the same operand into two operations.
- subtract: operands[0] is the target; every other operand is cut away from it.
- Editing a primitive (update_primitive / transform) updates every operation above it. Prefer editing over deleting.
- Order matters for cavities: union the outside parts first, THEN subtract the cavity, so the cut also clears any part
  that pokes into it (e.g. the inner half of a handle torus).

## Blending (k)
- k is a blend radius in model units. 0 = sharp crease. > 0 = smooth, fillet-like transition of roughly that size.
- Union with k adds material in the joint (a fillet). Subtract with k rounds the cut edge. Intersect with k rounds the
  edges of the overlap.
- Pick k relative to the smaller feature: ~5-15% of its size for organic joins, 0 to 2% for crisp mechanical parts.
  Too big makes everything blobby and swallows small parts. Too small leaves a visible crease.
- Parts closer than k get pulled together even if they don't touch. To keep small details (eyes, buttons) crisp,
  combine them in a separate operation with a small k.
- A blend only acts between the operands of that one operation.

## Practical rules
- Unioned parts should overlap a little (or use k > 0). Exactly touching surfaces render as a seam.
- A cutting tool must stick out past the surface it opens (5% or more), or a paper-thin skin remains.
- Walls thinner than ~2% of the object's size look broken in the render.
- Keep it to roughly 40 primitives or fewer: the viewport shader has a budget (get_scene warns when it is full) and
  every primitive costs render time.
- Create several primitives per turn with parallel tool calls. Capture after a meaningful stage, not after every primitive.
