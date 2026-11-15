import { ArrowUpDown, Trash2, Ungroup } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { BrandIcon, NodeIcon, type BrandIconName } from '@/components/brand-icon'
import { ParamSlider } from '@/components/param-slider'
import { ColorField } from '@/components/color-field'
import { CodeView } from '@/components/code-view'
import { send, useHost } from '@/bridge/host'
import type { AllowedCommand, BlendStyle, DeformKind, Id, OperationKind, SceneNode } from '@/bridge/protocol'
import { BLEND_STYLES, DEFORMS, OPERATIONS, PRIMITIVES, displayParams, isDeform, typeLabel } from '@/lib/scene'

/** Bottom half of the panel; what it shows depends on the selection (pick order). */
export function Inspector(props: { selected: SceneNode[]; labels: Map<string, string>; roots: Set<string> }) {
  const { labels, roots } = props
  const selected = props.selected.filter((n) => !n.missing)
  if (selected.length === 0) return <CreateSection />
  const one = selected[0]
  if (selected.length === 1)
    return (
      <>
        {one.kind === 'primitive' ? (
          <PrimitiveSection key={one.id} node={one} labels={labels} />
        ) : one.op === 'blend' ? (
          <BlendSection key={one.id} node={one} labels={labels} />
        ) : one.op === 'offset' ? (
          <OffsetSection key={one.id} node={one} labels={labels} />
        ) : isDeform(one.op) ? (
          <DeformSection key={one.id} node={one} labels={labels} />
        ) : (
          <CombineSection key={one.id} operands={one.children ?? []} labels={labels} existing={one} />
        )}
        <DeformButtons
          repeatId={one.op === 'repeat' ? one.id : undefined}
          offsetId={one.op === 'union' || one.op === 'subtract' || one.op === 'intersect' ? one.id : undefined}
        />
      </>
    )
  const canCombine = selected.length === 2 && selected.every((n) => roots.has(n.id))
  if (canCombine) return <CombineSection key={selected.map((s) => s.id).join()} operands={selected} labels={labels} />
  return (
    <Section title="Combine">
      <p className="type-small text-muted-foreground">Select exactly two top-level nodes to combine them.</p>
    </Section>
  )
}

function Section(props: { title: string; badge?: string; children: React.ReactNode; icon?: React.ReactNode }) {
  return (
    <section className="grid gap-3 px-4 py-4">
      <header className="flex min-w-0 items-center gap-2">
        {props.icon}
        <h2 className="type-heading truncate">{props.title}</h2>
        {props.badge && <Badge variant="secondary" className="ml-auto">{props.badge}</Badge>}
      </header>
      {props.children}
    </section>
  )
}

// ── Nothing selected: create ──

function CreateSection() {
  return (
    <Section title="Create">
      <div className="grid grid-cols-[repeat(auto-fill,minmax(72px,1fr))] gap-2">
        {Object.values(PRIMITIVES).map((p) => (
          <CommandTile key={p.command} label={p.label} icon={p.icon} command={p.command} />
        ))}
        {Object.values(DEFORMS)
          .filter((d) => d.create)
          .map((d) => (
            <CommandTile key={d.command} label={d.label} icon={d.icon} command={d.command} />
          ))}
      </div>
    </Section>
  )
}

// ── One primitive: its SDF parameters ──

function PrimitiveSection(props: { node: SceneNode; labels: Map<string, string> }) {
  const { node } = props
  const units = useHost((s) => s.settings.units)
  const notice = useHost((s) => s.notice)
  const params = displayParams(node)
  const fn = node.userFn
  // FFunction: SDF.L and SDF.u.<name> go through setParam too.
  const fnValues = fn ? [{ key: 'L', value: fn.lipschitz }, ...fn.params.map((p) => ({ key: `u.${p.name}`, value: p.value }))] : []
  const values = [...params, ...fnValues]
  // Slider range is fixed per object (the section is keyed by id) so it doesn't move while dragging.
  const [max] = useState(() => Math.max(1, ...params.map((p) => p.value * 3)))
  const [maxL] = useState(() => Math.max(4, 2 * (fn?.lipschitz ?? 1)))
  // Host echoes are ignored until they catch up, so a stale scene snapshot can't yank the slider.
  const [drag, setDrag] = useState<{ key: string; value: number } | null>(null)
  const [pending, setPending] = useState<{ key: string; value: number } | null>(null)
  const live = useRef({ timer: 0, key: '', value: 0, previewed: false })
  const hostValue = values.find((p) => p.key === pending?.key)?.value
  const caughtUp = pending == null || (hostValue !== undefined && Math.abs(hostValue - pending.value) < 1e-6)
  if (!drag && pending && caughtUp) setPending(null)
  const [seenNotice, setSeenNotice] = useState(notice?.id)
  if (notice && notice.id !== seenNotice) {
    setSeenNotice(notice.id)
    // A rejected value (zero size, and so on) never lands; show the host's value again.
    if (notice.level === 'warning' && !drag) setPending(null)
  }

  const sendParam = (key: string, value: number, preview: boolean) => {
    setPending({ key, value })
    send({ type: 'setParam', id: node.id, name: key, value, preview })
  }
  const preview = (key: string, value: number) => {
    const l = live.current
    l.key = key
    l.value = value
    setDrag({ key, value })
    if (l.timer) return
    l.timer = window.setTimeout(() => {
      l.timer = 0
      l.previewed = true
      sendParam(l.key, l.value, true)
    }, l.previewed ? PREVIEW_MS : 0)
  }
  const commit = (key: string, value: number) => {
    const l = live.current
    clearTimeout(l.timer)
    l.timer = 0
    setDrag(null)
    const current = values.find((p) => p.key === key)?.value
    if (l.previewed || current === undefined || Math.abs(current - value) >= 1e-6) sendParam(key, value, false)
    l.previewed = false
  }
  // Unmounted mid-drag (selection changed): close the gesture.
  useEffect(() => {
    const l = live.current
    return () => {
      clearTimeout(l.timer)
      if (l.previewed || l.timer) send({ type: 'setParam', id: node.id, name: l.key, value: l.value })
    }
  }, [node.id])

  const shown = (key: string, value: number) =>
    drag?.key === key ? drag.value : pending?.key === key ? pending.value : value

  const [previewColor, commitColor] = useColorEdit(node.id)

  return (
    <Section title={props.labels.get(node.id) ?? ''} badge={typeLabel(node)} icon={<NodeIcon node={node} className="size-6" />}>
      <ColorField label="Color" value={node.color ?? '#FCFCFC'} onChange={previewColor} onCommit={commitColor} />
      {fn && (
        <>
          <CodeView fn={fn} />
          {fn.params.map((p) => {
            const r = userParamRange(p.default)
            return (
              <ParamSlider
                key={p.name}
                label={p.name}
                value={shown(`u.${p.name}`, p.value)}
                min={r.min}
                max={r.max}
                step={r.step}
                belowMin
                onChange={(v) => preview(`u.${p.name}`, v)}
                onCommit={(v) => commit(`u.${p.name}`, v)}
              />
            )
          })}
          <ParamSlider
            label="Lipschitz L"
            value={shown('L', fn.lipschitz)}
            min={0.01}
            max={maxL}
            onChange={(v) => preview('L', v)}
            onCommit={(v) => commit('L', v)}
          />
          {params.length > 0 && <SubHeading>Bounding box</SubHeading>}
        </>
      )}
      {params.length === 0 && !node.custom && !fn && <p className="text-muted-foreground">No parameters.</p>}
      {params.map((p) => (
        <ParamSlider
          key={p.key}
          label={p.label}
          units={units}
          value={shown(p.key, p.value)}
          min={0}
          max={max}
          onChange={(v) => preview(p.key, v)}
          onCommit={(v) => commit(p.key, v)}
        />
      ))}
      <Button variant="outline" onClick={() => send({ type: 'deleteNodes', ids: [node.id] })}>
        <Trash2 /> Delete
      </Button>
    </Section>
  )
}

// ── ≥ 2 objects / nodes: new operation; or one operation node: edit it ──

/** Unwrap removes the node and leaves its children in its place. Delete removes the node and everything under it. */
function NodeActions(props: { id: Id }) {
  return (
    <div className="flex gap-2">
      <Button variant="outline" className="flex-1" onClick={() => send({ type: 'deleteNodes', ids: [props.id] })}>
        <Ungroup /> Unwrap
      </Button>
      <Button variant="outline" className="flex-1" onClick={() => send({ type: 'deleteNodes', ids: [props.id], deep: true })}>
        <Trash2 /> Delete
      </Button>
    </div>
  )
}

function CombineSection(props: { operands: SceneNode[]; labels: Map<string, string>; existing?: SceneNode }) {
  const { labels, existing } = props
  const units = useHost((s) => s.settings.units)
  const [swapped, setSwapped] = useState(false)
  const existingOp = existing?.op === 'union' || existing?.op === 'subtract' || existing?.op === 'intersect' ? existing.op : undefined
  const [op, setOp] = useState<OperationKind>(existingOp ?? 'union')
  const [k, setK] = useState(existing?.k ?? 5)

  // Host values win when they change (undo, another edit); unrelated scene updates keep local edits.
  // Not mid-drag, and not until the host has caught up with our last edit (stale echoes would yank the slider back).
  const [dragging, setDragging] = useState(false)
  const [pending, setPending] = useState<{ op: OperationKind; k: number } | null>(null)
  const [synced, setSynced] = useState({ op: existing?.op, k: existing?.k })
  const caughtUp = !pending || (existing?.op === pending.op && existing?.k === pending.k)
  if (existing && !dragging && caughtUp && (pending || existing.op !== synced.op || existing.k !== synced.k)) {
    setPending(null)
    setSynced({ op: existing.op, k: existing.k })
    if (existingOp) setOp(existingOp)
    if (existing.k !== undefined) setK(existing.k)
  }

  // Live edits of an existing node. Blend previews are throttled; release sends the final value (= one undo step).
  const live = useRef({ timer: 0, k: 0, sent: null as { op: OperationKind; k: number } | null, previewed: false })
  const edit = (next: { op: OperationKind; k: number }, preview: boolean) => {
    if (!existing) return
    live.current.sent = next
    setPending(next)
    send({ type: 'setOperation', id: existing.id, ...next, preview })
  }
  const previewK = (v: number) => {
    const l = live.current
    l.k = v
    if (l.timer) return
    l.timer = window.setTimeout(() => {
      l.timer = 0
      if (l.k === (l.previewed ? l.sent?.k : existing?.k)) return
      l.previewed = true
      edit({ op, k: l.k }, true)
    }, PREVIEW_MS)
  }
  const commitK = (v: number) => {
    const l = live.current
    clearTimeout(l.timer)
    l.timer = 0
    setK(v)
    setDragging(false)
    // Final message closes the host's gesture, so send it even if the drag ended where it started.
    if (l.previewed || v !== existing?.k) edit({ op, k: v }, false)
    l.previewed = false
  }
  // Unmounted mid-drag (selection changed): close the gesture.
  const id = existing?.id
  useEffect(() => {
    const l = live.current
    return () => {
      clearTimeout(l.timer)
      if (id && l.previewed && l.sent) send({ type: 'setOperation', id, ...l.sent, k: l.k })
    }
  }, [id])

  // Blend style (round / chamfer / stairs) + stairs count: node params, edited with setNodeParam (host values win on change).
  const hostStyle = (existing?.params?.style ?? 0) as BlendStyle
  const hostSteps = existing?.params?.steps ?? 4
  const [style, setStyle] = useState<BlendStyle>(hostStyle)
  const [steps, setSteps] = useState(hostSteps)
  const [syncedStyle, setSyncedStyle] = useState({ style: hostStyle, steps: hostSteps })
  if (existing && (hostStyle !== syncedStyle.style || hostSteps !== syncedStyle.steps)) {
    setSyncedStyle({ style: hostStyle, steps: hostSteps })
    setStyle(hostStyle)
    setSteps(hostSteps)
  }
  const sentSteps = useRef(hostSteps)
  const editStyle = (name: 'style' | 'steps', value: number, preview = false) => {
    if (existing) send({ type: 'setNodeParam', id: existing.id, name, value, preview })
  }
  const styleLabel = BLEND_STYLES[style]?.label ?? 'Round'

  // Swapping only makes sense for two new operands; otherwise pick order decides the subtract target.
  const canSwap = !existing && props.operands.length === 2
  const operands = canSwap && swapped ? [...props.operands].reverse() : props.operands
  const ordered = op === 'subtract'
  const role = (i: number) => (ordered ? (i === 0 ? 'Target' : 'Tool') : `#${i + 1}`)

  return (
    <Section
      title={existing ? (labels.get(existing.id) ?? 'Operation') : 'Combine'}
      badge={existing ? 'Operation' : `${operands.length} operands`}
      icon={<BrandIcon name={OPERATIONS[op].icon} className="size-6" />}
    >
      <div className="flex items-center gap-1">
        <div className="grid min-w-0 flex-1 gap-1">
          {operands.map((o, i) => (
            <Operand key={`${i}:${o.id}`} node={o} label={labels.get(o.id)} role={role(i)} />
          ))}
        </div>
        {canSwap && (
          <Button variant="ghost" size="icon-xs" aria-label="Swap" onClick={() => setSwapped((s) => !s)}>
            <ArrowUpDown />
          </Button>
        )}
      </div>

      <ToggleGroup
        type="single"
        variant="outline"
        spacing={0}
        value={op}
        onValueChange={(v) => {
          if (!v || v === op) return
          setOp(v as OperationKind)
          edit({ op: v as OperationKind, k }, false)
        }}
        className="w-full"
      >
        {(Object.keys(OPERATIONS) as OperationKind[]).map((kind) => (
          <ToggleGroupItem key={kind} value={kind} className="flex-1 px-1" aria-label={OPERATIONS[kind].label}>
            <BrandIcon name={OPERATIONS[kind].icon} /> {OPERATIONS[kind].label}
          </ToggleGroupItem>
        ))}
      </ToggleGroup>

      <ToggleGroup
        type="single"
        variant="outline"
        size="sm"
        spacing={0}
        value={String(style)}
        onValueChange={(v) => {
          if (!v || Number(v) === style) return
          setStyle(Number(v) as BlendStyle)
          editStyle('style', Number(v))
        }}
        className="w-full"
        aria-label="Blend style"
      >
        {BLEND_STYLES.map((s) => (
          <ToggleGroupItem key={s.value} value={String(s.value)} className="flex-1 px-1" title={s.hint}>
            {s.label}
          </ToggleGroupItem>
        ))}
      </ToggleGroup>

      <ParamSlider
        label={style === 0 ? 'Blend radius' : `${styleLabel} size`}
        units={k === 0 ? 'sharp' : units}
        value={k}
        max={20}
        onChange={(v) => {
          setK(v)
          if (!existing) return
          setDragging(true)
          previewK(v)
        }}
        onCommit={existing ? commitK : setK}
      />

      {style === 2 && (
        <ParamSlider
          label="Steps"
          value={steps}
          min={1}
          max={16}
          step={1}
          onChange={(v) => {
            const n = Math.round(v)
            setSteps(n)
            // Discrete: preview only when the count changes.
            if (n !== sentSteps.current) {
              sentSteps.current = n
              editStyle('steps', n, true)
            }
          }}
          onCommit={(v) => {
            const n = Math.max(1, Math.round(v))
            setSteps(n)
            sentSteps.current = n
            editStyle('steps', n)
          }}
        />
      )}

      {existing ? (
        <NodeActions id={existing.id} />
      ) : (
        <Button onClick={() => send({ type: 'applyOperation', operands: operands.map((o) => o.id), op, k, style, steps })}>
          Apply {OPERATIONS[op].label.toLowerCase()}
        </Button>
      )}
    </Section>
  )
}

// ── One deform node: its params ──

function DeformSection(props: { node: SceneNode; labels: Map<string, string> }) {
  const { node, labels } = props
  const units = useHost((s) => s.settings.units)
  const params = displayParams(node)
  const info = isDeform(node.op) ? DEFORMS[node.op] : undefined
  const isAngle = !!info?.angle
  const [max] = useState(() => (isAngle ? 360 : Math.max(1, ...params.map((p) => p.value * 3))))
  const [previewColor, commitColor] = useColorEdit(node.id)

  // Throttled previews while dragging; release sends the final value (= one undo step).
  const live = useRef({ timer: 0, name: '', value: 0, previewed: false })
  const preview = (name: string, value: number) => {
    const l = live.current
    Object.assign(l, { name, value })
    if (l.timer) return
    l.timer = window.setTimeout(() => {
      l.timer = 0
      l.previewed = true
      send({ type: 'setNodeParam', id: node.id, name: l.name, value: l.value, preview: true })
    }, PREVIEW_MS)
  }
  const commit = (name: string, value: number) => {
    const l = live.current
    clearTimeout(l.timer)
    Object.assign(l, { timer: 0, previewed: false })
    send({ type: 'setNodeParam', id: node.id, name, value })
  }
  useEffect(() => {
    const l = live.current
    return () => {
      clearTimeout(l.timer)
      if (l.previewed) send({ type: 'setNodeParam', id: node.id, name: l.name, value: l.value })
    }
  }, [node.id])

  const child = node.children?.[0]
  return (
    <Section title={labels.get(node.id) ?? ''} badge={typeLabel(node)} icon={<NodeIcon node={node} className="size-6" />}>
      {child && <Operand node={child} label={labels.get(child.id)} role="Child" />}
      {node.refMissing && (
        <p className="type-small text-muted-foreground">
          {info?.ref ?? 'Reference'} deleted; {node.op === 'pipe' ? 'not drawn' : 'drawn unwarped'}.
        </p>
      )}
      {node.op === 'pipe' && !node.refMissing && (
        <ColorField label="Color" value={node.color ?? '#FCFCFC'} onChange={previewColor} onCommit={commitColor} />
      )}
      {node.op === 'shell' && (
        <p className="type-small text-muted-foreground">Hollow inside; subtract an opening from it to see the wall.</p>
      )}
      {params.map((p) => (
        <ParamSlider
          key={p.key}
          label={p.label}
          units={isAngle ? '°' : units}
          value={p.value}
          min={isAngle ? -max : 0}
          max={max}
          step={isAngle ? 1 : 0.01}
          onChange={(v) => preview(p.key, v)}
          onCommit={(v) => commit(p.key, v)}
        />
      ))}
      <NodeActions id={node.id} />
    </Section>
  )
}

/** Blend node above a Repeat. The slider is the same one a union uses; it fillets every copy. */
function BlendSection(props: { node: SceneNode; labels: Map<string, string> }) {
  const { node, labels } = props
  const child = node.children?.[0]
  return (
    <Section title={labels.get(node.id) ?? ''} badge="Blend" icon={<BrandIcon name="blend" className="size-6" />}>
      {child && <Operand node={child} label={labels.get(child.id)} role="Child" />}
      <BlendSlider id={node.id} k={node.k ?? 0} />
      <NodeActions id={node.id} />
    </Section>
  )
}

function BlendSlider(props: { id: Id; k: number }) {
  const units = useHost((s) => s.settings.units)
  const [k, setK] = useState(props.k)
  const [dragging, setDragging] = useState(false)
  const [pending, setPending] = useState<number | null>(null)
  if (!dragging && pending === null && k !== props.k) setK(props.k)
  if (!dragging && pending !== null && Math.abs((props.k ?? 0) - pending) < 1e-6) setPending(null)

  const live = useRef({ timer: 0, k: props.k, previewed: false })
  const sendK = (value: number, preview: boolean) => {
    setPending(value)
    send({ type: 'setOperation', id: props.id, op: 'blend', k: value, preview })
  }
  const preview = (value: number) => {
    const l = live.current
    l.k = value
    if (l.timer) return
    l.timer = window.setTimeout(() => {
      l.timer = 0
      l.previewed = true
      sendK(l.k, true)
    }, PREVIEW_MS)
  }
  const commit = (value: number) => {
    const l = live.current
    clearTimeout(l.timer)
    l.timer = 0
    setDragging(false)
    if (l.previewed || value !== props.k) sendK(value, false)
    l.previewed = false
  }
  useEffect(() => {
    const l = live.current
    return () => {
      clearTimeout(l.timer)
      if (l.previewed) send({ type: 'setOperation', id: props.id, op: 'blend', k: l.k })
    }
  }, [props.id])

  return (
    <ParamSlider
      label="Blend radius"
      units={k === 0 ? 'sharp' : units}
      value={k}
      max={20}
      onChange={(v) => {
        setK(v)
        setDragging(true)
        preview(v)
      }}
      onCommit={(v) => {
        setK(v)
        commit(v)
      }}
    />
  )
}

/** Offset node above a union, intersection, or difference. Positive distance grows the solid. */
function OffsetSection(props: { node: SceneNode; labels: Map<string, string> }) {
  const { node, labels } = props
  const child = node.children?.[0]
  return (
    <Section title={labels.get(node.id) ?? ''} badge="Offset" icon={<BrandIcon name="offset" className="size-6" />}>
      {child && <Operand node={child} label={labels.get(child.id)} role="Child" />}
      <OffsetSlider id={node.id} k={node.k ?? 0} />
      <NodeActions id={node.id} />
    </Section>
  )
}

function OffsetSlider(props: { id: Id; k: number }) {
  const units = useHost((s) => s.settings.units)
  const [k, setK] = useState(props.k)
  const [dragging, setDragging] = useState(false)
  const [pending, setPending] = useState<number | null>(null)
  if (!dragging && pending === null && k !== props.k) setK(props.k)
  if (!dragging && pending !== null && Math.abs((props.k ?? 0) - pending) < 1e-6) setPending(null)

  const live = useRef({ timer: 0, k: props.k, previewed: false })
  const sendK = (value: number, preview: boolean) => {
    setPending(value)
    send({ type: 'setOperation', id: props.id, op: 'offset', k: value, preview })
  }
  const preview = (value: number) => {
    const l = live.current
    l.k = value
    if (l.timer) return
    l.timer = window.setTimeout(() => {
      l.timer = 0
      l.previewed = true
      sendK(l.k, true)
    }, PREVIEW_MS)
  }
  const commit = (value: number) => {
    const l = live.current
    clearTimeout(l.timer)
    l.timer = 0
    setDragging(false)
    if (l.previewed || value !== props.k) sendK(value, false)
    l.previewed = false
  }
  useEffect(() => {
    const l = live.current
    return () => {
      clearTimeout(l.timer)
      if (l.previewed) send({ type: 'setOperation', id: props.id, op: 'offset', k: l.k })
    }
  }, [props.id])

  return (
    <ParamSlider
      label="Offset"
      units={units}
      value={k}
      min={-20}
      max={20}
      belowMin
      onChange={(v) => {
        setK(v)
        setDragging(true)
        preview(v)
      }}
      onCommit={(v) => {
        setK(v)
        commit(v)
      }}
    />
  )
}

/** Runs FTwist / FBend / FRepeat / FShell / FGyroid on the selection (warps then ask for the line / box).
 *  A Repeat also gets Blend. A union, intersection, or difference also gets Offset. */
function DeformButtons(props: { repeatId?: Id; offsetId?: Id }) {
  return (
    <Section title="Modify">
      <div className="grid grid-cols-[repeat(auto-fill,minmax(72px,1fr))] gap-2">
        {(Object.keys(DEFORMS) as DeformKind[])
          .filter((kind) => !DEFORMS[kind].create)
          .map((kind) => (
            <CommandTile key={kind} {...DEFORMS[kind]} />
          ))}
        {props.repeatId && (
          <CommandTile label="Blend" icon="blend" onClick={() => send({ type: 'blendRepeat', id: props.repeatId! })} />
        )}
        {props.offsetId && (
          <CommandTile label="Offset" icon="offset" onClick={() => send({ type: 'offsetNode', id: props.offsetId! })} />
        )}
      </div>
    </Section>
  )
}

/** SDF.Color edits of a primitive or pipe node: throttled previews while the picker is open; the final value is one undo step. */
function useColorEdit(id: string): [(color: string) => void, (color: string) => void] {
  const live = useRef({ timer: 0, color: '', previewed: false })
  const preview = (color: string) => {
    const l = live.current
    l.color = color
    if (l.timer) return
    l.timer = window.setTimeout(() => {
      l.timer = 0
      l.previewed = true
      send({ type: 'setColor', id, color: l.color, preview: true })
    }, PREVIEW_MS)
  }
  const commit = (color: string) => {
    const l = live.current
    clearTimeout(l.timer)
    Object.assign(l, { timer: 0, previewed: false })
    send({ type: 'setColor', id, color })
  }
  useEffect(() => {
    const l = live.current
    return () => {
      clearTimeout(l.timer)
      if (l.previewed) send({ type: 'setColor', id, color: l.color })
    }
  }, [id])
  return [preview, commit]
}

/** Icon + label button that runs a Rhino command (Create / Deform grids). */
function CommandTile(props: { label: string; icon: BrandIconName; command?: AllowedCommand; onClick?: () => void }) {
  return (
    <button
      type="button"
      title={props.command ?? props.label}
      onClick={props.onClick ?? (() => props.command && send({ type: 'runCommand', command: props.command }))}
      className="group flex flex-col items-center gap-1 rounded-md border border-input bg-card px-1 pt-2 pb-1.5 transition-[background-color,translate,box-shadow] outline-none hover:bg-accent focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 focus-visible:ring-offset-background active:translate-y-px"
    >
      <BrandIcon name={props.icon} className="size-7 transition-transform group-hover:-translate-y-0.5" />
      <span className="type-small">{props.label}</span>
    </button>
  )
}

function SubHeading(props: { children: React.ReactNode }) {
  return <h3 className="type-label -mb-1 text-muted-foreground">{props.children}</h3>
}

/**
 * FFunction param slider range from its declared default d (no units / sign info in the code):
 * d > 0 → [0, 4d], d < 0 → [4d, 0], d = 0 → [-1, 1]. Step ≈ span / 100, a power of ten.
 * Typed values may go past either end (the slider then stretches to include them).
 */
function userParamRange(d: number): { min: number; max: number; step: number } {
  const [min, max] = d > 0 ? [0, 4 * d] : d < 0 ? [4 * d, 0] : [-1, 1]
  const step = 10 ** Math.floor(Math.log10((max - min) / 100))
  return { min, max, step }
}

/** Live blend previews: at most one per this many ms (~30 Hz); the final value always follows on release. */
const PREVIEW_MS = 33

function Operand(props: { node: SceneNode; label?: string; role: string }) {
  return (
    <div className="flex min-w-0 flex-1 items-center gap-2 rounded-md border border-input bg-card px-2 py-1.5">
      <NodeIcon node={props.node} className="size-5" />
      <div className="grid min-w-0">
        <span className="type-label text-muted-foreground">{props.role}</span>
        <span className="truncate font-bold">{props.label}</span>
      </div>
    </div>
  )
}
