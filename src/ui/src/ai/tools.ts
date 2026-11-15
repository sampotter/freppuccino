import type { ToolDef } from './openrouter'
import { callTool, ToolError } from '@/bridge/host'
import { findSkill, SKILLS } from './skills'

// Tools the model can call. Everything but load_skill runs in Rhino: src/Freppuccino/Ai/AiTools.cs (same names and
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
  color: { type: 'string', description: 'display color, "#RRGGBB" or a color name; blends mix colors smoothly' },
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

// Blend style of a CSG node (combine / edit_operation; get_scene shows it when not round).
const STYLE_FIELDS = {
  style: {
    type: 'string',
    enum: ['round', 'chamfer', 'stairs'],
    description: 'how the blend of size k looks: round fillet (default), chamfer = flat 45° bevel with legs k, stairs = k split into steps terraces',
  },
  steps: { type: 'number', description: 'stairs: number of terraces (1 … 64, default 4)' },
}

// Shell / lattice / pipe fields: create with their tools, change with edit_operation, read back from get_scene.
const MODIFIER_FIELDS = {
  thickness: { type: 'number', description: 'shell: wall thickness, inward from the surface; lattice: gyroid sheet wall thickness (< cell_size)' },
  cell_size: { type: 'number', description: 'lattice: gyroid period (one cell is cell_size wide)' },
  skin: { type: 'number', description: 'lattice: solid outer wall kept around the lattice, 0 = open lattice (default 0)' },
}
const PIPE_FIELDS = {
  points: { type: 'array', items: vec3('[x,y,z]'), minItems: 2, description: 'pipe: points the centerline passes through, in order' },
  radius: { type: 'number', description: 'pipe: tube radius (at the first point)' },
  end_radius: { type: 'number', description: 'pipe: tube radius at the last point (default = radius; varies linearly by length)' },
  closed: { type: 'boolean', description: 'pipe: loop back to the first point (ring, handle, frame)' },
  smooth: { type: 'boolean', description: 'pipe: true (default) = smooth curve through the points; false = straight segments with sharp corners' },
}

const TYPES = ['sphere', 'box', 'ellipsoid', 'cylinder', 'cone', 'torus', 'pyramid']

function tool(name: string, description: string, properties: Record<string, unknown> = {}, required: string[] = []): ToolDef {
  return { type: 'function', function: { name, description, parameters: { type: 'object', properties, required } } }
}

export const TOOLS: ToolDef[] = [
  tool(
    'get_scene',
    'Lists every F-rep primitive (ref, type, shape fields, color, used_by) and operation node (ref N#; CSG: op, k, style?, operands; ' +
      'deforms: op, target and the deform fields; shell / gyroid: target, thickness, cell_size, skin; pipe: points, radius, end_radius, ' +
      'closed), the roots (what is drawn), the bounding box and the model units. ' +
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
    'Creates a CSG operation node from ≥ 2 operands (primitive refs or node refs). union: merge; subtract: operands[0] minus all ' +
      'the others; intersect: the overlap. k = blend radius in model units (0 = sharp edge, > 0 = smooth fillet-like blend). ' +
      'style changes how the blend looks (chamfer for machined parts, stairs for terraced / architectural looks). ' +
      'Operands are consumed: only roots are drawn. Returns the node ref (N#).',
    {
      op: { type: 'string', enum: ['union', 'subtract', 'intersect'] },
      operands: refs('primitive or node refs; for subtract the first is the target'),
      k: { type: 'number', description: 'blend radius, ≥ 0 (default 0)' },
      ...STYLE_FIELDS,
    },
    ['op', 'operands'],
  ),
  tool(
    'shell',
    'Hollows a primitive or node (target): keeps a wall of the given thickness just inside its surface; the outside does not move. ' +
      'The cavity is closed, so it looks unchanged until you subtract an opening from the shell node (e.g. a cylinder through the ' +
      'top for a cup or vase). Consumes the target like combine. Returns the node ref (N#).',
    { target: { type: 'string', description: 'primitive or node ref, usually the top node of a finished solid' }, thickness: MODIFIER_FIELDS.thickness },
    ['target', 'thickness'],
  ),
  tool(
    'lattice',
    'Fills a primitive or node (target) with a gyroid lattice (a smooth 3D-printable infill): the target is replaced by gyroid ' +
      'sheets clipped to its shape. cell_size = period, thickness = sheet wall, skin > 0 keeps a solid outer wall that thick (a ' +
      'lightweighted part); skin 0 shows the open lattice. Defaults: ~5 cells across, wall = cell_size / 10. The lattice is fixed in ' +
      'world space. Returns the node ref (N#).',
    { target: { type: 'string', description: 'primitive or node ref' }, ...MODIFIER_FIELDS },
    ['target'],
  ),
  tool(
    'pipe',
    'Adds a round tube along a curve through points (handles, wires, frames, tubing, rails, coils, organic limbs). Tapers ' +
      'linearly from radius to end_radius, with round ends. Combines like a primitive (union with blend k welds it smoothly). ' +
      'Returns the node ref (N#); its curve is created for you and deleted with it.',
    { name: { type: 'string', description: 'short snake_case name for its curve' }, ...PIPE_FIELDS, color: SHAPE_FIELDS.color },
    ['points', 'radius'],
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
    "Changes an operation node: a CSG node's op, blend k, style and steps; or a deform / shell / lattice / pipe node's fields " +
      "(omitted fields keep their value; only CSG nodes can change op).",
    {
      ref: { type: 'string', description: 'node ref, e.g. "N3"' },
      op: { type: 'string', enum: ['union', 'subtract', 'intersect'] },
      k: { type: 'number' },
      ...STYLE_FIELDS,
      ...DEFORM_FIELDS,
      ...MODIFIER_FIELDS,
      ...PIPE_FIELDS,
      color: { type: 'string', description: 'pipe: display color' },
    },
    ['ref'],
  ),
  tool(
    'delete',
    'Deletes primitives and/or operation nodes (a deform\'s axis line / box goes with it). Something still used by another ' +
      'operation is kept (delete the parent first). recursive=true also deletes everything under the given nodes that nothing else uses.',
    { refs: refs('refs to delete'), recursive: { type: 'boolean' } },
    ['refs'],
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

  try {
    const result = (await callTool(name, args, name === 'capture_views' ? 120_000 : 60_000)) as Record<string, unknown>
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
      return `${result.ref} = ${args.op}(${(args.operands as string[]).join(', ')})${args.k ? ` k=${args.k}` : ''}${args.style && args.style !== 'round' ? ` ${args.style}` : ''}`
    case 'shell':
    case 'lattice':
      return `${result.ref} = ${name}(${args.target})${result.warning ? ' ⚠' : ''}`
    case 'pipe':
      return `${result.ref} = pipe r=${args.radius}${args.end_radius !== undefined ? `→${args.end_radius}` : ''}`
    case 'deform':
      return `${result.ref} = ${args.op}(${args.target})${result.warning ? ' ⚠' : ''}`
    case 'edit_operation':
      return result.k !== undefined ? `${result.ref}: ${result.op} k=${result.k}` : `${result.ref}: ${result.op}${result.warning ? ' ⚠' : ''}`
    case 'delete':
      return ((result.deleted as string[]) ?? []).join(', ') || 'nothing'
    case 'transform':
      return ((result.transformed as string[]) ?? []).join(', ')
    default:
      return 'done'
  }
}
