// Messages between this page and Rhino.
// Mirror of src/Freppuccino/Ui/UiProtocol.cs — change both together. Docs: docs/side-panel.md.

/** Primitive: Rhino object id (GUID). Operation: CSG node name, e.g. "N3". */
export type Id = string

export type OperationKind = 'union' | 'subtract' | 'intersect'
/**
 * Domain warps of one child (children[0]), shaped by a Rhino line (twist, bend) or box (repeat); and displace, a surface
 * texture on children[0] (children[1] = optional mask, not drawn; ref = optional knurl axis line).
 */
export type DeformKind = 'twist' | 'bend' | 'repeat' | 'displace'
/** displace params.pattern */
export type DisplacePattern = 0 | 1 | 2

/**
 * One row in the browser tree. Primitives (Rhino objects with SDF.* user text) are leaves;
 * operations (CSG nodes) nest their operands as children (children[0] is the target for subtract).
 * The CSG graph is a DAG: a shared operand appears under each parent.
 */
export interface SceneNode {
  id: Id
  kind: 'primitive' | 'operation'
  name?: string
  visible: boolean
  locked: boolean
  /** primitive: IQ function name, e.g. "sdSphere", "sdBox" */
  sdfType?: string
  /** primitive: SDF.<param> values without the prefix, e.g. { s: 1 } or { "b.x": 1, "b.y": 2, "b.z": 3 } */
  params?: Record<string, number>
  /** operation */
  op?: OperationKind | DeformKind
  /** operation: blend distance in model units, 0 = sharp */
  k?: number
  children?: SceneNode[]
  /** operand whose object / node was deleted */
  missing?: boolean
  /** deform: Rhino id of its axis line / box (deform params are in `params`: twist angle; bend angle, direction; repeat x, y, z) */
  ref?: Id
  /** deform: ref deleted or no longer a line / box (child drawn unwarped; displace: knurl falls back to a vertical axis) */
  refMissing?: boolean
  /** primitive: SDF.Color "#RRGGBB" used by the viewport shader (white if the object has none) */
  color?: string
  /** primitive: a gumball scale or shear the parameters cannot express (SDF.Linear.*). Sliders are hidden. */
  custom?: boolean
}

export interface UiSettings {
  units: string
}

export type Theme = 'light' | 'dark'

// ── Host → UI ──
export type HostMessage =
  | { type: 'state'; scene: SceneNode[]; selection: Id[]; settings: UiSettings; theme: Theme }
  | { type: 'scene'; scene: SceneNode[] }
  /** selected primitives (Rhino selection) + operation nodes (kept by the host) */
  | { type: 'selection'; ids: Id[] }
  | { type: 'notice'; level: 'info' | 'warning' | 'error'; text: string }
  /** Reply to an aiTool request (same callId). error is meant for the model. */
  | { type: 'aiToolResult'; callId: string; ok: boolean; result?: unknown; error?: string }

// ── UI → Host ──
export type SelectMode = 'replace' | 'add' | 'toggle'
/** Must match UiController.AllowedCommands */
export type AllowedCommand =
  | 'FBox' | 'FSphere' | 'FCylinder' | 'FCone' | 'FPyramid' | 'FEllipsoid' | 'FTorus'
  | 'FTwist' | 'FBend' | 'FRepeat' | 'FDisplace'
  | 'Freppuccino'

export type UiMessage =
  | { type: 'ready' }
  /** While an F-CSG command waits for operands, the host feeds clicked nodes to it instead of selecting. */
  | { type: 'select'; ids: Id[]; mode: SelectMode }
  /** primitive: Rhino show/hide; operation: hides the node's result (undoable) */
  | { type: 'setVisible'; id: Id; visible: boolean }
  | { type: 'runCommand'; command: AllowedCommand }
  /** preview: live drag tick, no undo; the next non-preview setParam ends the gesture as one undo step. */
  | { type: 'setParam'; id: Id; name: string; value: number; preview?: boolean }
  /** Writes SDF.Color ("#RRGGBB") on a primitive; preview as setOperation (one undo step per gesture). */
  | { type: 'setColor'; id: Id; color: string; preview?: boolean }
  /** New CSG node over ≥ 2 primitives / nodes. For subtract, operands[0] is the target. */
  | { type: 'applyOperation'; operands: Id[]; op: OperationKind; k: number }
  /** preview: live drag tick, no undo; the next non-preview setOperation ends the gesture as one undo step. */
  | { type: 'setOperation'; id: Id; op: OperationKind; k: number; preview?: boolean }
  /** One deform param; preview as setOperation. */
  | { type: 'setNodeParam'; id: Id; name: string; value: number; preview?: boolean }
  /** Nodes still used by other nodes are kept (host sends a notice). */
  | { type: 'deleteNodes'; ids: Id[] }
  /** AI agent tool call (src/ui/src/ai/tools.ts → Ai/AiTools.cs); answered by aiToolResult. */
  | { type: 'aiTool'; callId: string; tool: string; args: Record<string, unknown> }
  /** Save a finished AI run to disk for review (Ai/AiRunLog.cs). Images become PNG files next to transcript.json. */
  | { type: 'aiSaveRun'; title: string; transcript: unknown; images: { name: string; dataUrl: string }[] }
