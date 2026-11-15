import type { BrandIconName } from '@/components/brand-icon'
import type { AllowedCommand, DeformKind, DisplacePattern, OperationKind, SceneNode } from '@/bridge/protocol'

interface PrimitiveInfo {
  label: string
  icon: BrandIconName
  /** Rhino command that creates it */
  command: AllowedCommand
  /** display names for SDF params, in display order; unknown params show their raw key after these */
  params: Record<string, string>
}

/**
 * Keyed by SDF.Type (IQ function name), in the order the Create grid shows them.
 * Param meanings come from the F* commands (src/Freppuccino/Commands). Unknown types still show, with raw names.
 */
export const PRIMITIVES: Record<string, PrimitiveInfo> = {
  sdBox: { label: 'Box', icon: 'box', command: 'FBox', params: { 'b.x': 'Half X', 'b.y': 'Half Y', 'b.z': 'Half Z' } },
  sdSphere: { label: 'Sphere', icon: 'sphere', command: 'FSphere', params: { s: 'Radius' } },
  sdCappedCylinder: { label: 'Cylinder', icon: 'cylinder', command: 'FCylinder', params: { r: 'Radius', h: 'Half height' } },
  sdCappedCone: { label: 'Cone', icon: 'cone', command: 'FCone', params: { r1: 'Base radius', r2: 'Top radius', h: 'Half height' } },
  sdPyramid: { label: 'Pyramid', icon: 'pyramid', command: 'FPyramid', params: { s: 'Base side', h: 'Height' } },
  sdEllipsoid: { label: 'Ellipsoid', icon: 'ellipsoid', command: 'FEllipsoid', params: { 'r.x': 'Semi-axis X', 'r.y': 'Semi-axis Y', 'r.z': 'Semi-axis Z' } },
  sdTorus: { label: 'Torus', icon: 'torus', command: 'FTorus', params: { 't.x': 'Major radius', 't.y': 'Tube radius' } },
}

export const OPERATIONS: Record<OperationKind, { label: string; icon: BrandIconName }> = {
  union: { label: 'Union', icon: 'union' },
  subtract: { label: 'Subtract', icon: 'subtract' },
  intersect: { label: 'Intersect', icon: 'intersect' },
}

interface DeformInfo {
  label: string
  icon: BrandIconName
  command: AllowedCommand
  params: Record<string, string>
  /** params are angles in degrees (else lengths in model units) */
  angle?: boolean
  /** the Rhino object that drives it, for the "deleted" note */
  ref?: string
}

/** Commands take the selected object / node, then ask for the line / box (FDisplace: options, optional mask / axis). */
export const DEFORMS: Record<DeformKind, DeformInfo> = {
  twist: { label: 'Twist', icon: 'twist', command: 'FTwist', params: { angle: 'Angle' }, angle: true, ref: 'Axis line' },
  bend: { label: 'Bend', icon: 'bend', command: 'FBend', params: { angle: 'Angle', direction: 'Direction' }, angle: true, ref: 'Axis line' },
  repeat: { label: 'Repeat', icon: 'repeat', command: 'FRepeat', params: { x: 'Spacing X', y: 'Spacing Y', z: 'Spacing Z' }, ref: 'Box' },
  displace: {
    label: 'Texture',
    icon: 'displace',
    command: 'FDisplace',
    params: { pattern: 'Pattern', amplitude: 'Height', scale: 'Scale', fade: 'Mask fade' },
    ref: 'Knurl axis',
  },
}

/** Displace patterns (params.pattern). Scale is the feature size (knurl: pitch); a negative height engraves. */
export const DISPLACE_PATTERNS: { value: DisplacePattern; label: string; hint: string }[] = [
  { value: 0, label: 'Perlin', hint: 'organic noise: skins, stone, hammered metal' },
  { value: 1, label: 'Voronoi', hint: 'raised cells: scales, pebbles, leather' },
  { value: 2, label: 'Knurl', hint: 'diamond grip around an axis' },
]

export function isDeform(op: SceneNode['op']): op is DeformKind {
  return !!op && op in DEFORMS
}

export function nodeIconName(node: SceneNode): BrandIconName | undefined {
  if (node.kind === 'operation') return !node.op ? undefined : isDeform(node.op) ? DEFORMS[node.op].icon : OPERATIONS[node.op].icon
  return node.sdfType ? PRIMITIVES[node.sdfType]?.icon : undefined
}

export function typeLabel(node: SceneNode): string {
  if (node.kind === 'operation') return !node.op ? 'Operation' : isDeform(node.op) ? DEFORMS[node.op].label : OPERATIONS[node.op].label
  return (node.sdfType && PRIMITIVES[node.sdfType]?.label) || node.sdfType || 'Primitive'
}

/** Params with display labels: known ones in catalogue order, then any others (raw key). None once the primitive is custom. */
export function displayParams(node: SceneNode): { key: string; label: string; value: number }[] {
  if (node.custom) return []
  const values = node.params ?? {}
  const known = (isDeform(node.op) ? DEFORMS[node.op].params : node.sdfType && PRIMITIVES[node.sdfType]?.params) || {}
  const ordered = [...Object.keys(known).filter((k) => k in values), ...Object.keys(values).filter((k) => !(k in known))]
  return ordered.map((key) => ({ key, label: known[key] ?? key, value: values[key] }))
}

/** "Box", or "Custom Box-based FRep" once a gumball transform no longer fits the parameters. */
function nameStem(node: SceneNode): string {
  const type = typeLabel(node)
  return node.kind === 'primitive' && node.custom ? `Custom ${type}-based FRep` : type
}

/** Display labels for every node: the Rhino name if set, else "Sphere 1", "Union 2", ... in tree order.
 *  A custom primitive is always "Custom Box-based FRep 1", numbered among that stem. */
export function labelNodes(scene: SceneNode[]): Map<string, string> {
  const labels = new Map<string, string>()
  const counters = new Map<string, number>()
  const walk = (nodes: SceneNode[]) => {
    for (const node of nodes) {
      const stem = nameStem(node)
      const i = (counters.get(stem) ?? 0) + 1
      counters.set(stem, i)
      labels.set(node.id, node.custom ? `${stem} ${i}` : (node.name ?? `${stem} ${i}`))
      if (node.children) walk(node.children)
    }
  }
  walk(scene)
  return labels
}

export function indexNodes(scene: SceneNode[]): Map<string, SceneNode> {
  const byId = new Map<string, SceneNode>()
  const walk = (nodes: SceneNode[]) => nodes.forEach((n) => (byId.set(n.id, n), n.children && walk(n.children)))
  walk(scene)
  return byId
}
