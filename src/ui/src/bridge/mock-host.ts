import type { Transport } from './host'
import type { AllowedCommand, HostMessage, SceneNode, UiMessage, UserFnInfo } from './protocol'

/**
 * Stand-in for Rhino when the page runs in a normal browser (`npm run dev`).
 * Behaves roughly like UiController.cs, including the stubs. Operation ids are "N<n>" like the real host.
 * Add ?theme=dark / ?theme=light to the URL to force a theme.
 */
export function createMockHost(): Transport {
  let listener: ((m: HostMessage) => void) | null = null
  const emit = (m: HostMessage) => setTimeout(() => listener?.(structuredClone(m)), 0)

  let n = 0
  const id = () => `00000000-0000-4000-8000-${String(++n).padStart(12, '0')}`
  let nextNode = 1
  const opNode = (op: SceneNode['op'], k: number, children: SceneNode[]): SceneNode => ({
    id: `N${nextNode++}`, kind: 'operation', name: `N${nextNode - 1}`, op, k, visible: true, locked: false, children,
  })
  const prim = (sdfType: string, params: Record<string, number>, name?: string): SceneNode => ({
    id: id(), kind: 'primitive', sdfType, name, visible: true, locked: false, params, color: '#FCFCFC',
  })
  /** sdUser node; userFn from mockCompile, with stored u.* values (missing = default). */
  const userFn = (code: string, b: number, values: Record<string, number> = {}, lipschitz = 1, name?: string): SceneNode => ({
    ...prim('sdUser', { 'b.x': b, 'b.y': b, 'b.z': b }, name),
    userFn: mockCompile(code, values, lipschitz),
  })
  const make: Partial<Record<AllowedCommand, () => SceneNode>> = {
    FFunction: () => userFn(SAMPLE_OK, 2),
    FBox: () => prim('sdBox', { 'b.x': 1, 'b.y': 1, 'b.z': 1 }),
    FSphere: () => prim('sdSphere', { s: 1 }),
    FCylinder: () => prim('sdCappedCylinder', { h: 1, r: 0.5 }),
    FCone: () => prim('sdCappedCone', { h: 1, r1: 1, r2: 0 }),
    FPyramid: () => prim('sdPyramid', { h: 2, s: 2 }),
    FEllipsoid: () => prim('sdEllipsoid', { 'r.x': 2, 'r.y': 1, 'r.z': 1 }),
    FTorus: () => prim('sdTorus', { 't.x': 2, 't.y': 0.5 }),
  }

  const holes = opNode('union', 0.5, [
    prim('sdCappedCylinder', { h: 2, r: 0.8 }, 'Bore'),
    prim('sdEllipsoid', { 'r.x': 1.5, 'r.y': 0.6, 'r.z': 0.6 }),
  ])
  let scene: SceneNode[] = [
    prim('sdSphere', { s: 2 }),
    prim('sdTorus', { 't.x': 3, 't.y': 0.6 }),
    prim('sdCappedCone', { h: 1.5, r1: 1.2, r2: 0.3 }),
    opNode('subtract', 0.25, [prim('sdBox', { 'b.x': 3, 'b.y': 3, 'b.z': 1 }), holes]),
    prim('sdPyramid', { h: 2, s: 2.5 }),
    { ...prim('sdCappedCylinder', { h: 1, r: 0.5 }), custom: true },
    { ...opNode('repeat', 0, [prim('sdSphere', { s: 1 })]), params: { x: 3, y: 3, z: 0 } },
    userFn(SAMPLE_OK, 2, { twist: 1.5 }, 1.2, 'Gyroid'),
    userFn(SAMPLE_BAD, 1),
  ]
  let selection: string[] = []

  const forced = new URLSearchParams(location.search).get('theme')
  const theme = forced === 'dark' || forced === 'light' ? forced : matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'

  const map = (nodes: SceneNode[], fn: (n: SceneNode) => SceneNode): SceneNode[] =>
    nodes.map((x) => fn({ ...x, children: x.children && map(x.children, fn) }))
  const flat = (nodes: SceneNode[]): SceneNode[] => nodes.flatMap((x) => [x, ...flat(x.children ?? [])])

  // A deleted node's children take its place. A combine op left with one child becomes that child;
  // a node left with none is removed. Twist, bend, repeat, and blend keep their single child.
  const expand = (n: SceneNode, ids: Set<string>): SceneNode[] => {
    if (ids.has(n.id)) return (n.children ?? []).flatMap((c) => expand(c, ids))
    if (!n.children) return [n]
    return [{ ...n, children: n.children.flatMap((c) => expand(c, ids)) }]
  }
  const collapse = (list: SceneNode[]): SceneNode[] => {
    const out: SceneNode[] = []
    for (const n of list) {
      if (n.kind !== 'operation' || !n.children) {
        out.push(n)
        continue
      }
      const children = collapse(n.children)
      if (children.length === 0) continue
      const combine = n.op === 'union' || n.op === 'subtract' || n.op === 'intersect'
      if (combine && children.length === 1) {
        out.push(children[0])
        continue
      }
      out.push({ ...n, children })
    }
    return out
  }
  const subtree = (nodes: SceneNode[], roots: Set<string>): Set<string> => {
    const drop = new Set<string>()
    const visit = (n: SceneNode, inside: boolean) => {
      const hit = inside || roots.has(n.id)
      if (hit) drop.add(n.id)
      for (const c of n.children ?? []) visit(c, hit)
    }
    for (const n of nodes) visit(n, false)
    return drop
  }
  const excise = (nodes: SceneNode[], ids: Set<string>) => collapse(nodes.flatMap((n) => expand(n, ids)))

  function handle(m: UiMessage) {
    switch (m.type) {
      case 'ready':
        emit({ type: 'state', scene, selection, settings: { units: 'Millimeters' }, theme })
        break
      case 'select':
        if (m.mode === 'replace') selection = [...m.ids]
        else if (m.mode === 'add') selection = [...new Set([...selection, ...m.ids])]
        else for (const x of m.ids) selection = selection.includes(x) ? selection.filter((s) => s !== x) : [...selection, x]
        emit({ type: 'selection', ids: selection })
        break
      case 'setVisible':
        scene = map(scene, (x) => (x.id === m.id ? { ...x, visible: m.visible } : x))
        emit({ type: 'scene', scene })
        break
      case 'runCommand': {
        const created = make[m.command]?.()
        if (created) {
          scene = [...scene, created]
          emit({ type: 'scene', scene })
        }
        emit({ type: 'notice', level: 'info', text: `Ran ${m.command} (mock).` })
        break
      }
      case 'setParam':
        scene = map(scene, (x) => {
          if (x.id !== m.id) return x
          const fn = x.userFn
          if (fn && m.name === 'L') return { ...x, userFn: { ...fn, lipschitz: m.value } }
          if (fn && m.name.startsWith('u.'))
            return { ...x, userFn: { ...fn, params: fn.params.map((p) => (`u.${p.name}` === m.name ? { ...p, value: m.value } : p)) } }
          return { ...x, params: { ...x.params, [m.name]: m.value } }
        })
        emit({ type: 'scene', scene })
        break
      case 'setColor':
        scene = map(scene, (x) => (x.id === m.id ? { ...x, color: m.color.toUpperCase() } : x))
        emit({ type: 'scene', scene })
        break
      case 'offsetNode': {
        let created: string | null = null
        scene = map(scene, (x) => {
          if (x.id !== m.id || (x.op !== 'union' && x.op !== 'subtract' && x.op !== 'intersect')) return x
          const offset = opNode('offset', 0, [x])
          created = offset.id
          return offset
        })
        if (created) selection = [created]
        emit({ type: 'scene', scene })
        emit({ type: 'selection', ids: selection })
        break
      }
      case 'blendRepeat': {
        let created: string | null = null
        scene = map(scene, (x) => {
          if (x.id !== m.id || x.op !== 'repeat') return x
          const blend = opNode('blend', 0, [x])
          created = blend.id
          return blend
        })
        if (created) selection = [created]
        emit({ type: 'scene', scene })
        emit({ type: 'selection', ids: selection })
        break
      }
      case 'applyOperation': {
        const all = flat(scene)
        const operands = m.operands.map((x) => all.find((y) => y.id === x)).filter((x) => !!x)
        const node = opNode(m.op, m.k, operands)
        scene = [...scene.filter((x) => !m.operands.includes(x.id)), node]
        selection = [node.id]
        emit({ type: 'scene', scene })
        emit({ type: 'selection', ids: selection })
        break
      }
      case 'setOperation':
        scene = map(scene, (x) => (x.id === m.id ? { ...x, op: m.op, k: m.k } : x))
        emit({ type: 'scene', scene })
        break
      case 'setNodeParam':
        scene = map(scene, (x) => (x.id === m.id ? { ...x, params: { ...x.params, [m.name]: m.value } } : x))
        emit({ type: 'scene', scene })
        break
      case 'deleteNodes': {
        const ids = m.deep ? subtree(scene, new Set(m.ids)) : new Set(m.ids)
        scene = excise(scene, ids)
        const live = new Set(flat(scene).map((x) => x.id))
        selection = selection.filter((x) => live.has(x))
        emit({ type: 'scene', scene })
        emit({ type: 'selection', ids: selection })
        break
      }
      case 'aiTool': {
        let reply: HostMessage
        try {
          reply = { type: 'aiToolResult', callId: m.callId, ok: true, result: aiTool(m.tool, m.args) }
        } catch (e) {
          reply = { type: 'aiToolResult', callId: m.callId, ok: false, error: (e as Error).message }
        }
        emit(reply)
        emit({ type: 'scene', scene })
        break
      }
      case 'aiSaveRun':
        console.info('[mock] AI run (not saved in the browser):', m.title, m.transcript)
        break
    }
  }

  // ── AI tools: just enough of Ai/AiTools.cs to exercise the agent loop in a browser (no geometry, no screenshots) ──
  const specs = new Map<string, Record<string, unknown>>() // primitive id → spec
  const SDF: Record<string, string> = {
    sphere: 'sdSphere', box: 'sdBox', cylinder: 'sdCappedCylinder', cone: 'sdCappedCone',
    pyramid: 'sdPyramid', ellipsoid: 'sdEllipsoid', torus: 'sdTorus',
  }
  /** "#RRGGBB" / "RRGGBB" / CSS color name → "#RRGGBB" (the browser parses names); undefined when absent or invalid. */
  const mockColor = (raw: unknown): string | undefined => {
    if (typeof raw !== 'string' || !raw.trim()) return undefined
    const text = /^[0-9a-f]{6}$/i.test(raw.trim()) ? `#${raw.trim()}` : raw.trim()
    const ctx = document.createElement('canvas').getContext('2d')
    if (!ctx) return undefined
    ctx.fillStyle = '#010203'
    ctx.fillStyle = text // invalid values are ignored by the canvas
    const out = String(ctx.fillStyle)
    if (out === '#010203' && text.toLowerCase() !== '#010203') return undefined
    return /^#[0-9a-f]{6}$/i.test(out) ? out.toUpperCase() : undefined
  }
  const find = (ref: string) => {
    const node = flat(scene).find((x) => x.id === ref || x.name === ref)
    if (!node) throw new Error(`no object or node '${ref}'; call get_scene for current refs`)
    return node
  }
  function aiTool(tool: string, args: Record<string, unknown>): unknown {
    switch (tool) {
      case 'get_scene': {
        const all = flat(scene)
        return {
          units: 'Millimeters',
          primitives: all.filter((x) => x.kind === 'primitive').map((x) => ({ ref: x.name ?? x.id, ...specs.get(x.id), color: x.color ?? '#FCFCFC' })),
          operations: all.filter((x) => x.kind === 'operation').map((x) => ({
            ref: x.id, op: x.op, k: x.k, operands: x.children?.map((c) => c.name ?? c.id),
          })),
          roots: scene.map((x) => x.name ?? x.id),
        }
      }
      case 'create_primitive': {
        const type = String(args.type)
        if (!SDF[type]) throw new Error(`unknown type '${type}'`)
        let name = String(args.name ?? type).replace(/\s+/g, '_')
        while (flat(scene).some((x) => x.name === name)) name += '_2'
        const { name: _, color, ...spec } = args
        const p = { ...prim(SDF[type], {}, name), color: mockColor(color) ?? '#FCFCFC' }
        specs.set(p.id, spec)
        scene = [...scene, p]
        return { ref: name, guid: p.id }
      }
      case 'update_primitive': {
        const p = find(String(args.ref))
        const { ref: _, color, ...patch } = args
        specs.set(p.id, { ...specs.get(p.id), ...patch })
        const c = mockColor(color)
        if (c) scene = map(scene, (x) => (x.id === p.id ? { ...x, color: c } : x))
        return { ref: p.name, ...specs.get(p.id), ...(c ? { color: c } : {}) }
      }
      case 'set_color': {
        const requests: [string, string][] = [
          ...((args.colors as { ref: string; color: string }[] | undefined) ?? []).map((e): [string, string] => [e.ref, e.color]),
          ...((args.refs as string[] | undefined) ?? []).map((r): [string, string] => [r, String(args.color ?? '')]),
        ]
        if (requests.length === 0) throw new Error('give refs + color, or colors: [{ ref, color }, ...]')
        const colored: Record<string, string> = {}
        for (const [ref, raw] of requests) {
          const c = mockColor(raw)
          if (!c) throw new Error(`'color' must be "#RRGGBB" or a color name (got "${raw}")`)
          for (const leaf of flat([find(ref)]).filter((x) => x.kind === 'primitive')) {
            scene = map(scene, (x) => (x.id === leaf.id ? { ...x, color: c } : x))
            colored[leaf.name ?? leaf.id] = c
          }
        }
        emit({ type: 'scene', scene })
        return { colored }
      }
      case 'combine': {
        const operands = (args.operands as string[]).map(find)
        const node = opNode(args.op as SceneNode['op'], Number(args.k ?? 0), operands)
        scene = [...scene.filter((x) => !operands.includes(x)), node]
        return { ref: node.id }
      }
      case 'deform': {
        const target = find(String(args.target))
        const node = { ...opNode(args.op as SceneNode['op'], 0, [target]), params: {} }
        scene = [...scene.filter((x) => x !== target), node]
        const { op, target: _, ...fields } = args
        return { ref: node.id, op, target: target.name ?? target.id, ...fields }
      }
      case 'edit_operation': {
        const n = find(String(args.ref))
        scene = map(scene, (x) => (x.id === n.id ? { ...x, op: (args.op as SceneNode['op']) ?? x.op, k: (args.k as number) ?? x.k } : x))
        return { ref: n.id }
      }
      case 'delete': {
        const ids = new Set((args.refs as string[]).map((r) => find(r).id))
        scene = scene.filter((x) => !ids.has(x.id))
        return { deleted: [...ids] }
      }
      case 'clear_scene':
        scene = []
        return { ok: true }
      case 'transform':
      case 'set_visible':
        return { ok: true, note: 'mock host: no geometry' }
      case 'capture_views':
        throw new Error('Screenshots need Rhino (the mock host has no viewport). Skip visual checks.')
      default:
        throw new Error(`unknown tool '${tool}'`)
    }
  }

  return {
    kind: 'mock',
    post: (m) => setTimeout(() => handle(m), 0),
    listen: (cb) => (listener = cb),
  }
}

// ── FFunction stand-ins (the real compiler is UserFn/UserFnCompiler.cs) ──

const SAMPLE_OK = `// Twisted gyroid shell, clipped to the box.
param float scale = 0.8;
param float thickness = 0.15;
param float twist = 0.0;

float sdf(vec3 p) {
  float a = twist * p.z;
  p.xy = mat2(cos(a), -sin(a), sin(a), cos(a)) * p.xy;
  vec3 q = p * scale;
  float g = dot(sin(q), cos(q.yzx));
  return abs(g) / scale - thickness;
}`

const SAMPLE_BAD = `param float r = 1.0;

float sdf(vec3 p) {
  return length(p) - radius;
}
@`

/** Just enough to exercise the panel: `param float x = d;` declarations; `radius` / `@` are errors. */
function mockCompile(code: string, values: Record<string, number>, lipschitz: number): UserFnInfo {
  const diagnostics: UserFnInfo['diagnostics'] = []
  code.split('\n').forEach((line, i) => {
    const at = line.indexOf('@')
    if (at >= 0) diagnostics.push({ line: i + 1, column: at + 1, message: "unexpected character '@'" })
    const r = line.search(/\bradius\b/)
    if (r >= 0) diagnostics.push({ line: i + 1, column: r + 1, message: "undeclared identifier 'radius'" })
  })
  const ok = diagnostics.length === 0
  const params = ok
    ? [...code.matchAll(/param\s+float\s+(\w+)\s*=\s*([-+.\deE]+)\s*;/g)].map(([, name, d]) => ({
        name, default: Number(d), value: values[name] ?? Number(d),
      }))
    : []
  return { code, ok, hash: ok ? '9f3c2a7be1d04c55' : undefined, diagnostics, params, lipschitz }
}
