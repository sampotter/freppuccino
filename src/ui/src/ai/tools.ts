import type { ToolDef } from './openrouter'
import { callTool, ToolError } from '@/bridge/host'
import { findSkill, SKILLS } from './skills'
import { EXAMPLES, exampleCreateArgs, findExample } from '@/lib/examples'

// Tools the model can call. Everything but load_skill and get_function_example runs in Rhino: src/Freppuccino/Ai/AiTools.cs (same names and
// argument shapes; shape fields are documented in Ai/AiShapes.cs). Keep the three in sync.

const vec3 = (description: string) => ({
  type: 'array',
  items: { type: 'number' },
  minItems: 3,
  maxItems: 3,
  description,
})
const refs = (description: string) => ({ type: 'array', items: { type: 'string' }, minItems: 1, description })

const SHAPE_FIELDS = {
  center: vec3('sphere / box / ellipsoid / torus: center point [x,y,z]'),
  radius: { type: 'number', description: 'sphere, cylinder: radius' },
  size: vec3('box: full lengths [x,y,z] along x_axis, y_axis and their cross product (world X/Y/Z by default)'),
  radii: vec3('ellipsoid: semi-axis lengths [x,y,z] along x_axis, y_axis and their cross product'),
  x_axis: vec3('box / ellipsoid / pyramid: optional local X direction for rotated shapes (default [1,0,0])'),
  y_axis: vec3('box / ellipsoid: optional local Y direction (default [0,1,0]); made perpendicular to x_axis'),
  start: vec3('cylinder / cone: center of the start cap'),
  end: vec3('cylinder / cone: center of the end cap'),
  start_radius: { type: 'number', description: 'cone: radius at start (0 = point)' },
  end_radius: { type: 'number', description: 'cone: radius at end (0 = point); both > 0 makes a frustum' },
  axis: vec3('torus: normal of the ring plane (default [0,0,1] = ring lies flat); pyramid: base → apex direction (default [0,0,1])'),
  major_radius: { type: 'number', description: 'torus: ring radius (center to tube center)' },
  minor_radius: { type: 'number', description: 'torus: tube radius' },
  base_center: vec3('pyramid: center of the square base'),
  height: { type: 'number', description: 'pyramid: height from base to apex' },
  base_size: { type: 'number', description: 'pyramid: side length of the square base' },
  color: { type: 'string', description: 'shader color, "#RRGGBB" or a color name; blends mix colors smoothly. Default white' },
}

// Deform (warp) fields: create with deform, change with edit_operation, read back from get_scene.
const DEFORM_FIELDS = {
  axis_start: vec3('twist / bend: start of the axis line (twist is 0 here; bending starts here)'),
  axis_end: vec3('twist / bend: end of the axis line. Default for both: the target\'s bounding box, bottom center → top center'),
  angle_degrees: { type: 'number', description: 'twist: total twist from axis_start to axis_end (keeps going past the ends); bend: total bend over the axis length' },
  toward: vec3('bend: direction the shape bends toward (any vector not parallel to the axis)'),
  count: vec3('repeat: number of copies along each lattice axis, e.g. [5,1,1]; 1 = no repetition on that axis'),
  spacing: vec3('repeat: distance between copies along each lattice axis (0 where count is 1)'),
  centered: { type: 'boolean', description: 'repeat: false (default) = the original is the first copy and the rest go the + way; true = copies spread both ways around it' },
  x_axis: vec3('repeat: optional lattice X direction (default [1,0,0])'),
  y_axis: vec3('repeat: optional lattice Y direction (default [0,1,0])'),
}

const FN_PARAMS = {
  type: 'object',
  additionalProperties: { type: 'number' },
  description: 'param values by name, e.g. {"cell": 8}; omitted params keep their value (or declared default)',
}
const FN_LIPSCHITZ = {
  type: 'number',
  description: 'Lipschitz scale L > 0 (default 1): sdf / L must change by ≤ 1 per unit or the render gets holes; raise it if so',
}

const TYPES = ['sphere', 'box', 'ellipsoid', 'cylinder', 'cone', 'torus', 'pyramid']

function tool(name: string, description: string, properties: Record<string, unknown> = {}, required: string[] = []): ToolDef {
  return { type: 'function', function: { name, description, parameters: { type: 'object', properties, required } } }
}

export const TOOLS: ToolDef[] = [
  tool(
    'get_scene',
    'Lists every F-rep primitive (ref, type, shape fields, color, used_by) and operation node (ref N#; CSG: op, k, operands; ' +
      'deforms: op, target and the deform fields; offset / blend: op, k), the roots (what is drawn), the bounding box, the model units, ' +
      'and "selection": what the user has selected (when they say "this" / "the selected part", it means these). ' +
      'Call it first, and whenever you are unsure of refs or positions.',
  ),
  tool(
    'create_primitive',
    'Adds one primitive. All coordinates are world space, model units, Z up. Fields by type: ' +
      'sphere(center, radius); box(center, size, x_axis?, y_axis?); ellipsoid(center, radii, x_axis?, y_axis?); ' +
      'cylinder(start, end, radius); cone(start, end, start_radius, end_radius); ' +
      'torus(center, axis?, major_radius, minor_radius); pyramid(base_center, height, base_size, axis?, x_axis?). ' +
      'Returns its ref (the name, made unique). Create several at once with parallel calls.',
    { type: { type: 'string', enum: TYPES }, name: { type: 'string', description: 'short descriptive snake_case name, e.g. "cup_body"' }, ...SHAPE_FIELDS },
    ['type', 'name'],
  ),
  tool(
    'update_primitive',
    "Changes a primitive's shape fields in place (same fields as create_primitive; omitted fields keep their value) or renames it. " +
      'Its type cannot change. Operations using it update automatically.',
    { ref: { type: 'string' }, name: { type: 'string', description: 'new name (optional)' }, ...SHAPE_FIELDS },
    ['ref'],
  ),
  tool(
    'transform',
    'Moves / rotates / scales primitives, or everything under an operation node (deform axes / boxes move along). Order: scale, then rotate, then translate, ' +
      'about pivot (default: center of their bounding box). Non-uniform scale turns spheres into ellipsoids; it fails for shapes ' +
      'that cannot represent the result (e.g. a cylinder squashed across its axis).',
    {
      refs: refs('primitives or nodes to transform'),
      translate: vec3('offset [dx,dy,dz]'),
      rotate: {
        type: 'object',
        properties: { angle_degrees: { type: 'number' }, axis: vec3('rotation axis direction (default [0,0,1])') },
        required: ['angle_degrees'],
      },
      scale: { description: 'uniform factor, or [sx,sy,sz] along world axes', anyOf: [{ type: 'number' }, vec3('per-axis factors')] },
      pivot: vec3('center for scale / rotate'),
    },
    ['refs'],
  ),
  tool(
    'combine',
    'Creates a group node from ≥ 2 operands (primitive refs or node refs). A group reads top to bottom: operands[0] is the ' +
      'base (added), and every operand applies its mode (add, subtract or intersect) to the result of the ones above it; a ' +
      'subtract or intersect operand with nothing added above it does nothing. op picks the modes: ' +
      'union = all add; subtract = operands[0] minus all the others; intersect = all intersect. Change single rows later with ' +
      'set_mode. k = blend radius in model units (0 = sharp edge, > 0 = smooth fillet-like blend) for every join in the group. ' +
      'Operands are consumed: only roots are drawn. Returns the node ref (N#).',
    {
      op: { type: 'string', enum: ['union', 'subtract', 'intersect'] },
      operands: refs('primitive or node refs; the first is the base (always add)'),
      k: { type: 'number', description: 'blend radius, ≥ 0 (default 0)' },
    },
    ['op', 'operands'],
  ),
  tool(
    'deform',
    'Wraps a primitive or node (target) in a deform node, which is drawn instead of the target (like combine, it consumes it). ' +
      'twist: rotates the shape around the axis line, angle growing linearly from axis_start (0) to axis_end (angle_degrees). ' +
      'bend: bends the part between axis_start and axis_end into an arc of angle_degrees toward "toward"; the part before ' +
      'axis_start stays put and the part past axis_end continues straight (rigidly). bend radius = axis length / angle in radians. ' +
      'repeat: copies the target count[i] times, spacing[i] apart, along the lattice axes (world X/Y/Z by default). ' +
      'Deforms stack: twist a node, then bend the twisted node. A warning in the result means it will render badly: act on it. ' +
      'Returns the node ref (N#).',
    {
      op: { type: 'string', enum: ['twist', 'bend', 'repeat'] },
      target: { type: 'string', description: 'primitive or node ref to deform; usually the top node of a finished part' },
      ...DEFORM_FIELDS,
    },
    ['op', 'target'],
  ),
  tool(
    'edit_operation',
    "Changes an operation node: a group's blend k (op resets all its modes to that preset), a deform node's fields (omitted " +
      "fields keep their value; a deform can't change its op), or an offset / blend node's k (offset k may be negative: shrinks).",
    {
      ref: { type: 'string', description: 'node ref, e.g. "N3"' },
      op: { type: 'string', enum: ['union', 'subtract', 'intersect'] },
      k: { type: 'number' },
      ...DEFORM_FIELDS,
    },
    ['ref'],
  ),
  tool(
    'add_operand',
    'Adds a primitive or node to an existing group (at index, default the end; index 0 makes it the base, mode add) with a mode ' +
      '(default add). Order matters: each operand combines with the operands above it. Deforms take exactly one operand: use ' +
      'move to swap it. Cycles and duplicates are rejected.',
    {
      node: { type: 'string', description: 'group node ref, e.g. "N3"' },
      operand: { type: 'string', description: 'primitive or node ref to add' },
      index: { type: 'integer', description: 'position in the operand list (optional)' },
      mode: { type: 'string', enum: ['add', 'subtract', 'intersect'] },
    },
    ['node', 'operand'],
  ),
  tool(
    'remove_operand',
    'Takes an operand out of a CSG node; if nothing else uses it, it becomes a root (drawn on its own). A node must keep ≥ 1 ' +
      'operand (delete the node instead), a deform exactly 1.',
    { node: { type: 'string', description: 'CSG node ref' }, operand: { type: 'string', description: 'operand ref to remove' } },
    ['node', 'operand'],
  ),
  tool(
    'move',
    'Re-parents a primitive or node: detaches it from "from" (default: every operation using it) and inserts it into "to" at ' +
      'index (default end). Omit "to" (or null) to make it a root. from = to reorders, which changes the shape (operands ' +
      'combine top to bottom). It keeps its mode from the group it left; without "from" it keeps it only if every group it ' +
      'left gave it the same mode, otherwise it arrives as add. mode overrides that. Into a deform: replaces its target. ' +
      'Restructure with this instead of deleting and rebuilding.',
    {
      item: { type: 'string', description: 'primitive or node ref to move' },
      to: { type: 'string', description: 'destination node ref; omit to make it a root' },
      from: { type: 'string', description: 'detach only from this node (optional)' },
      index: { type: 'integer', description: 'position in the destination operand list (optional)' },
      mode: { type: 'string', enum: ['add', 'subtract', 'intersect'] },
    },
    ['item'],
  ),
  tool(
    'set_mode',
    "Sets how one operand of a group combines with the operands above it: add, subtract or intersect. The mode belongs to " +
      "that group only (a shared operand can add in one group and subtract in another). Every operand's mode applies, the " +
      "first one's too: a subtract or intersect operand with nothing added above it does nothing.",
    {
      node: { type: 'string', description: 'group node ref, e.g. "N3"' },
      operand: { type: 'string', description: 'primitive or node ref in that group' },
      mode: { type: 'string', enum: ['add', 'subtract', 'intersect'] },
    },
    ['node', 'operand', 'mode'],
  ),
  tool(
    'delete',
    'Deletes primitives and/or operation nodes (a deform\'s axis line / box goes with it). Something still used by another ' +
      'operation is kept (delete the parent first). recursive=true also deletes everything under the given nodes that nothing else uses.',
    { refs: refs('refs to delete'), recursive: { type: 'boolean' } },
    ['refs'],
  ),
  tool(
    'set_color',
    'Recolors primitives without changing their shape (one undo step). A node ref colors every primitive under it. ' +
      'Either refs + color (same color for all), or colors: [{ref, color}, ...] to paint several parts differently in one call. ' +
      'Colors show in capture_views: use them to match a reference image and to tell parts apart.',
    {
      refs: refs('primitives or nodes to give the same color'),
      color: SHAPE_FIELDS.color,
      colors: {
        type: 'array',
        minItems: 1,
        description: 'per-ref colors, applied in order (later entries win)',
        items: {
          type: 'object',
          properties: { ref: { type: 'string' }, color: SHAPE_FIELDS.color },
          required: ['ref', 'color'],
        },
      },
    },
  ),
  tool('set_visible', 'Shows or hides primitives / operation nodes (hidden ones are not drawn).', {
    refs: refs('refs'),
    visible: { type: 'boolean' },
  }, ['refs', 'visible']),
  tool(
    'clear_scene',
    'Deletes ALL F-rep primitives and operations (undoable). Only when the user asks for a fresh start or a new, unrelated object ' +
      'and the scene holds your previous attempt.',
  ),
  tool(
    'capture_views',
    'Renders screenshots of the F-rep result (what the user sees, CSG applied) and returns them as images. ' +
      'Views: perspective, perspective_back, perspective_below, front (looking +Y), back, left, right (looking -X), top, bottom. ' +
      'Default: perspective, front, right, top. Framed on all visible primitives, or on focus refs to zoom in on a detail.',
    {
      views: { type: 'array', items: { type: 'string' }, maxItems: 6 },
      focus: refs('optional refs to frame instead of everything'),
      size: { type: 'number', description: 'image size in px (default 512, max 1024)' },
    },
  ),
  // FFunction: user SDF code clipped by a box (Ai/AiFunctions.cs; guide: skill freppuccino-ffunction).
  tool(
    'create_function',
    'Adds a function primitive: your SDF code (GLSL subset, entry float sdf(vec3 p), p in the box frame in model units) clipped by ' +
      'a box (center, size = full lengths, x_axis?, y_axis?). Field = max(sdf(p) / lipschitz, box). Rejected with diagnostics if the ' +
      'code has errors (unless allow_errors). Load the freppuccino-ffunction skill first. Returns ref, ok, diagnostics, params.',
    {
      name: { type: 'string', description: 'short snake_case name' },
      center: SHAPE_FIELDS.center,
      size: vec3('clip box full lengths [x,y,z]; nothing outside it is drawn'),
      x_axis: SHAPE_FIELDS.x_axis,
      y_axis: SHAPE_FIELDS.y_axis,
      code: { type: 'string', description: 'DSL source: param float name = default; ... float sdf(vec3 p) { ... }' },
      params: FN_PARAMS,
      lipschitz: FN_LIPSCHITZ,
      color: SHAPE_FIELDS.color,
      allow_errors: { type: 'boolean', description: 'create even if the code has errors (it is not drawn until fixed)' },
    },
    ['center', 'size', 'code'],
  ),
  tool(
    'set_function_code',
    "Replaces a function's code. Param values carry over by name. With errors nothing changes (diagnostics in the error) " +
      'unless allow_errors. Returns ok, diagnostics, params, hash.',
    {
      target: { type: 'string', description: 'function ref' },
      code: { type: 'string' },
      allow_errors: { type: 'boolean' },
    },
    ['target', 'code'],
  ),
  tool(
    'get_function',
    "Reads a function: code, params (name, default, value), lipschitz, clip box, ok, diagnostics, hash, and shader_error if the " +
      'viewport shader failed to compile.',
    { target: { type: 'string', description: 'function ref' } },
    ['target'],
  ),
  tool(
    'set_function_params',
    "Sets a function's param values and/or lipschitz without changing its code (no recompile).",
    { target: { type: 'string', description: 'function ref' }, params: FN_PARAMS, lipschitz: FN_LIPSCHITZ },
    ['target'],
  ),
  tool(
    'check_function_code',
    'Compiles FFunction code without writing anything: ok, diagnostics [{line, column, message}], params. Cheap; use it to fix code before create_function.',
    { code: { type: 'string' } },
    ['code'],
  ),
  tool(
    'get_function_example',
    'The bundled FFunction examples (TPMS lattices: gyroid / Schwarz P / diamond / Neovius, lattice-filled sphere, Voronoi foam, ' +
      'graded gyroid, Scherk tower, nautilus, Turing vase, Michell bracket, tet goo, helicatenoid, starters). No name: the list ' +
      '(name, description, box, lipschitz). With name: its code plus ready create_function arguments (Y-up box standing on the ' +
      'ground at the origin; change center / params to place and tune it). Prefer adapting an example over writing from scratch.',
    { name: { type: 'string', enum: EXAMPLES.map((e) => e.name) } },
  ),
  tool(
    'wrap',
    'Inserts a node directly above an existing one (parents now use the new node). offset: target is a group ' +
      'node; k = surface shift in model units (positive grows, negative shrinks: wall thickening, clearances). ' +
      'blend: target is a repeat node; k fillets the copies into each other. Returns the node ref (N#); change k with edit_operation.',
    {
      op: { type: 'string', enum: ['offset', 'blend'] },
      target: { type: 'string', description: 'node ref (N#)' },
      k: { type: 'number', description: 'offset distance (signed) or blend radius (≥ 0); default 0' },
    },
    ['op', 'target'],
  ),
  tool(
    'unwrap',
    "Removes operation nodes but keeps what's under them: the children take the node's place (the panel's Unwrap). " +
      'Use it to drop a deform / offset / blend, or to ungroup a group.',
    { refs: refs('node refs (N#)') },
    ['refs'],
  ),
  tool(
    'select',
    'Selects primitives / nodes in Rhino and the panel (mode replace = default, add, toggle); no refs clears the selection. ' +
      'Select your finished part at the end so the user can tweak it in the inspector.',
    { refs: { type: 'array', items: { type: 'string' } }, mode: { type: 'string', enum: ['replace', 'add', 'toggle'] } },
  ),
  tool(
    'mesh',
    'Bakes F-rep roots into a Rhino mesh (the FrepMesh command): the F-reps stay. Only when the user asks for a mesh / export. ' +
      'Returns the mesh id, face count, closed / manifold flags and self_intersections (not manifold: raise resolution; self-intersections: raise refine).',
    {
      refs: refs('root refs to bake (default: every visible root)'),
      resolution: { type: 'integer', description: 'cells along the longest side, 16–384 (default 128; 256 for fine lattices)' },
      simplify: { type: 'number', description: 'simplification tolerance in cells, 0–1 (default 0.03; 0 = uniform)' },
      refine: { type: 'integer', description: 'extra levels for thin features and sharp creases, 0–3 (default 0; raise it when self_intersections > 0 or thin walls break)' },
      quads: { type: 'boolean', description: "remesh to quads with Rhino's QuadRemesh (slower)" },
      name: { type: 'string', description: 'Rhino object name (default "FrepMesh")' },
    },
  ),
  tool(
    'set_zebra',
    'Zebra analysis (FZebra): reflection stripes on union surfaces, so blend continuity shows in capture_views (smooth stripes = ' +
      'smooth blend, kinks = a crease). Opens the Zebra Options window; turn it off when done.',
    {
      on: { type: 'boolean' },
      stripes: { type: 'number', description: 'stripe count, 2–60 (default 12)' },
      vertical: { type: 'boolean', description: 'stripe direction (default true)' },
    },
    ['on'],
  ),
  tool(
    'load_skill',
    'Reads a skill: guidance and proven recipes for a kind of object or task. Load the matching ones before building.',
    { name: { type: 'string', enum: SKILLS.filter((s) => !s.always).map((s) => s.name) } },
    ['name'],
  ),
]

export interface ToolOutcome {
  ok: boolean
  /** JSON text for the model (images stripped) */
  content: string
  /** screenshots (data URLs) to show the model and the user */
  images?: { view: string; dataUrl: string }[]
  /** one-line summary for the chat */
  summary: string
}

export async function runTool(name: string, rawArgs: string): Promise<ToolOutcome> {
  let args: Record<string, unknown>
  try {
    args = rawArgs.trim() ? JSON.parse(rawArgs) : {}
  } catch {
    return fail(`arguments are not valid JSON: ${rawArgs.slice(0, 200)}`)
  }

  if (name === 'load_skill') {
    const skill = findSkill(String(args.name ?? ''))
    if (!skill) return fail(`no skill '${args.name}'. Available: ${SKILLS.map((s) => s.name).join(', ')}`)
    return { ok: true, content: skill.body, summary: skill.name }
  }

  if (name === 'get_function_example') {
    if (!args.name) {
      const list = EXAMPLES.map((e) => ({
        name: e.name,
        description: e.description,
        box_size: e.half.map((h) => 2 * h),
        lipschitz: e.lipschitz,
      }))
      return { ok: true, content: JSON.stringify({ examples: list }), summary: `${list.length} examples` }
    }
    const ex = findExample(String(args.name))
    if (!ex) return fail(`no example '${args.name}'. Available: ${EXAMPLES.map((e) => e.name).join(', ')}`)
    return {
      ok: true,
      content: JSON.stringify({ name: ex.name, description: ex.description, note: ex.note, create_function: exampleCreateArgs(ex) }),
      summary: ex.name,
    }
  }

  try {
    const timeout = name === 'mesh' ? 300_000 : name === 'capture_views' ? 120_000 : 60_000
    const result = (await callTool(name, args, timeout)) as Record<string, unknown>
    if (name === 'capture_views') {
      const views = (result.views as string[]) ?? []
      const images = ((result.images as string[]) ?? []).map((dataUrl, i) => ({ view: views[i] ?? `view ${i + 1}`, dataUrl }))
      return {
        ok: true,
        content: JSON.stringify({ captured: views, note: 'The images follow in the next message, in this order.' }),
        images,
        summary: views.join(', '),
      }
    }
    return { ok: true, content: JSON.stringify(result), summary: summarize(name, args, result) }
  } catch (e) {
    return fail(e instanceof ToolError ? e.message : `${(e as Error).message}`)
  }
}

function fail(message: string): ToolOutcome {
  return { ok: false, content: JSON.stringify({ error: message }), summary: message }
}

function summarize(name: string, args: Record<string, unknown>, result: Record<string, unknown>): string {
  switch (name) {
    case 'get_scene': {
      const p = (result.primitives as unknown[])?.length ?? 0
      const o = (result.operations as unknown[])?.length ?? 0
      return `${p} primitives, ${o} operations`
    }
    case 'create_primitive':
      return `${args.type} ${result.ref}`
    case 'update_primitive':
      return String(args.ref)
    case 'combine':
      return `${result.ref} = ${args.op}(${(args.operands as string[]).join(', ')})${args.k ? ` k=${args.k}` : ''}`
    case 'deform':
      return `${result.ref} = ${args.op}(${args.target})${result.warning ? ' ⚠' : ''}`
    case 'edit_operation':
      return result.k !== undefined ? `${result.ref}: ${result.op} k=${result.k}` : `${result.ref}: ${result.op}${result.warning ? ' ⚠' : ''}`
    case 'add_operand':
    case 'remove_operand':
      return `${result.ref} = ${result.op}(${((result.operands as string[]) ?? []).join(', ')})`
    case 'move':
      return `${result.item} → ${((result.parents as string[]) ?? []).join(', ') || 'root'}`
    case 'set_mode':
      return `${result.ref}: ${args.operand} ${args.mode}`
    case 'delete':
      return ((result.deleted as string[]) ?? []).join(', ') || 'nothing'
    case 'create_function':
      return `function ${result.ref}${result.ok === false ? ' (errors)' : ''}`
    case 'set_function_code':
    case 'set_function_params':
      return `${result.ref}${result.ok === false ? ' (errors)' : ''}`
    case 'get_function':
      return `${args.target}${result.ok === false ? ' (errors)' : ''}${result.shader_error ? ' (shader error)' : ''}`
    case 'check_function_code':
      return result.ok ? 'compiles' : `${((result.diagnostics as unknown[]) ?? []).length} errors`
    case 'wrap':
      return `${result.ref} = ${args.op}(${result.target}) k=${result.k}`
    case 'unwrap':
      return ((result.unwrapped as string[]) ?? []).join(', ')
    case 'select':
      return ((result.selection as string[]) ?? []).join(', ') || 'nothing'
    case 'mesh':
      return `${result.name}: ${result.faces} faces${result.manifold === false ? ' (not manifold)' : ''}`
    case 'set_zebra':
      return result.zebra ? 'zebra on' : 'zebra off'
    case 'transform':
      return ((result.transformed as string[]) ?? []).join(', ')
    case 'set_color': {
      const colored = Object.entries((result.colored as Record<string, string>) ?? {})
      return colored.length > 3
        ? `${colored.length} primitives`
        : colored.map(([ref, color]) => `${ref} ${color}`).join(', ') || 'nothing'
    }
    default:
      return 'done'
  }
}
