import { Box, Code2, Loader2, Settings, Sparkles, X } from 'lucide-react'
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent as ReactKeyboardEvent, type PointerEvent, type ReactNode, type RefObject } from 'react'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { ScrollArea } from '@/components/ui/scroll-area'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { AiPanel } from '@/components/ai-panel'
import { BrandIcon } from '@/components/brand-icon'
import { Inspector } from '@/components/inspector'
import { ScriptPanel } from '@/components/script-panel'
import { SceneTree } from '@/components/scene-tree'
import { SettingsPanel } from '@/components/settings-panel'
import { dismissNotice, send, useHost } from '@/bridge/host'
import type { PanelTab } from '@/bridge/protocol'
import { indexNodes, labelNodes } from '@/lib/scene'
import { cn } from '@/lib/utils'
import appIcon from '@/brand/app-icon.svg'
import wordmark from '@/brand/wordmark.svg?raw'

type Tab = PanelTab

export default function App() {
  const [tab, setTab] = useState<Tab>('scene')
  const tabRequest = useHost((s) => s.tabRequest)
  const [shownRequest, setShownRequest] = useState(0)
  if (tabRequest && tabRequest.seq !== shownRequest) {
    setShownRequest(tabRequest.seq)
    setTab(tabRequest.tab)
  }
  const [editRequest, setEditRequest] = useState<{ id: string; seq: number }>()
  const host = useHost((s) => s.host)
  const synced = useHost((s) => s.synced)
  const scene = useHost((s) => s.scene)
  const selection = useHost((s) => s.selection)
  const theme = useHost((s) => s.theme)
  const frepMode = useHost((s) => s.frepMode)

  useEffect(() => {
    document.documentElement.classList.toggle('dark', theme === 'dark')
  }, [theme])

  // Esc in the panel deselects like Esc in the viewport (inputs keep Esc for themselves).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || (e.target as HTMLElement | null)?.closest('input, textarea')) return
      send({ type: 'select', ids: [], mode: 'replace' })
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])

  const inspectorRef = useRef<HTMLDivElement>(null)
  const [inspectorHeight, setInspectorHeight] = useState<number | null>(() => {
    const saved = Number(localStorage.getItem(INSPECTOR_HEIGHT_KEY))
    return saved > 0 ? saved : null
  })
  useEffect(() => {
    if (inspectorHeight === null) localStorage.removeItem(INSPECTOR_HEIGHT_KEY)
    else localStorage.setItem(INSPECTOR_HEIGHT_KEY, String(Math.round(inspectorHeight)))
  }, [inspectorHeight])

  const labels = useMemo(() => labelNodes(scene), [scene])
  const byId = useMemo(() => indexNodes(scene), [scene])
  const roots = useMemo(() => new Set(scene.map((n) => n.id)), [scene])
  const selectedSet = useMemo(() => new Set(selection), [selection])
  const selected = useMemo(() => selection.map((id) => byId.get(id)).filter((n) => !!n), [selection, byId])

  const headerRef = useRef<HTMLElement>(null)
  const [fit, setFit] = useState(0)
  const [headerWidth, setHeaderWidth] = useState(0)
  useEffect(() => {
    const el = headerRef.current
    if (!el) return
    const observer = new ResizeObserver(() => setHeaderWidth(el.clientWidth))
    observer.observe(el)
    return () => observer.disconnect()
  }, [])
  const fitKey = useRef('')
  useLayoutEffect(() => {
    const el = headerRef.current
    if (!el) return
    const key = `${headerWidth}|${tab}|${host}|${synced}`
    if (key !== fitKey.current) {
      fitKey.current = key
      if (fit !== 0) return setFit(0)
    }
    if (fit < 3 && el.scrollWidth > el.clientWidth) setFit(fit + 1)
  })
  const tabItem = (value: Tab, icon: ReactNode, label: string) => {
    const showLabel = fit === 0 || (fit === 1 && value === tab)
    const item = (
      <ToggleGroupItem value={value} className="px-2" aria-label={label}>
        {icon}
        {showLabel && label}
      </ToggleGroupItem>
    )
    return showLabel ? item : (
      <Tooltip key={value}>
        <TooltipTrigger asChild>{item}</TooltipTrigger>
        <TooltipContent side="bottom">{label}</TooltipContent>
      </Tooltip>
    )
  }

  return (
    <div className="relative flex h-dvh flex-col overflow-hidden">
      <header ref={headerRef} className="flex h-12 shrink-0 items-center gap-2 overflow-hidden border-b px-4">
        <img src={appIcon} alt="" className="h-8 w-auto shrink-0" />
        {/* Wordmark: ink = currentColor, "FRep" = --wordmark-accent (caramel / caramel-ink per theme) */}
        <span
          role="img"
          aria-label="FReppuccino"
          className={cn('h-4 shrink-0 text-ink [&>svg]:h-full [&>svg]:w-auto', fit === 3 && 'hidden')}
          dangerouslySetInnerHTML={{ __html: wordmark }}
        />
        <span className="ml-auto flex shrink-0 items-center gap-1.5">
          {host === 'mock' && <Badge variant="new">Mock host</Badge>}
          {!synced && <span className="type-small text-muted-foreground">connecting…</span>}
          <ToggleGroup
            type="single"
            variant="outline"
            size="sm"
            spacing={0}
            value={tab}
            onValueChange={(v) => v && setTab(v as Tab)}
            aria-label="Panel"
          >
            {tabItem('scene', <Box />, 'Scene')}
            {tabItem('script', <Code2 />, 'Script')}
            {tabItem('ai', <Sparkles />, 'AI')}
            {tabItem('settings', <Settings />, 'Settings')}
          </ToggleGroup>
        </span>
      </header>
      <CompileToast />

      {synced && !frepMode && (
        <div className="flex shrink-0 items-center gap-2 border-b bg-secondary px-4 py-2">
          <span className="type-small flex-1 text-secondary-foreground">This view isn't showing F-reps.</span>
          <Button size="sm" onClick={() => send({ type: 'showFrepMode' })}>
            <BrandIcon name="preview" /> F-rep display mode
          </Button>
        </div>
      )}

      {/* Both stay mounted so switching keeps scroll position and the chat draft. */}
      <div className={cn('flex min-h-0 flex-1 flex-col', tab !== 'scene' && 'hidden')}>
        <div className="flex h-9 shrink-0 items-center px-4 text-muted-foreground">
          <span className="type-label">Browser</span>
          <span className="type-numeric ml-auto">{byId.size}</span>
        </div>
        <ScrollArea className="min-h-24 flex-1">
          <SceneTree
            nodes={scene}
            labels={labels}
            selection={selectedSet}
            onSelect={(ids, mode) => send({ type: 'select', ids, mode })}
            onSetVisible={(id, visible) => send({ type: 'setVisible', id, visible })}
          />
        </ScrollArea>

        {selected.some((n) => !n.missing) && (
          <>
            <Splitter height={inspectorHeight} setHeight={setInspectorHeight} pane={inspectorRef} />
            <div
              ref={inspectorRef}
              className={cn('shrink-0 overflow-y-auto bg-card', inspectorHeight === null && 'max-h-[65%]')}
              style={inspectorHeight === null ? undefined : { height: inspectorHeight, maxHeight: `calc(100% - ${RESERVED_ABOVE}px)` }}
            >
              <Inspector
                selected={selected}
                labels={labels}
                roots={roots}
                onEditCode={(id) => {
                  setEditRequest((r) => ({ id, seq: (r?.seq ?? 0) + 1 }))
                  setTab('script')
                }}
              />
            </div>
          </>
        )}
      </div>
      <div className={cn('flex min-h-0 flex-1 flex-col', tab !== 'script' && 'hidden')}>
        <ScriptPanel editRequest={editRequest} onOpenAi={() => setTab('ai')} />
      </div>
      <div className={cn('flex min-h-0 flex-1 flex-col', tab !== 'ai' && 'hidden')}>
        <AiPanel />
      </div>
      <div className={cn('flex min-h-0 flex-1 flex-col overflow-y-auto', tab !== 'settings' && 'hidden')}>
        <SettingsPanel />
      </div>

      <div className="absolute inset-x-3 bottom-3">
        <NoticeToast />
      </div>
    </div>
  )
}

function CompileToast() {
  const compiling = useHost((s) => s.compiling)
  const [dismissed, setDismissed] = useState(0)
  if (!compiling || dismissed === compiling) return null
  return (
    <div
      role="status"
      className="type-small mx-3 mt-2 flex shrink-0 items-center gap-2 rounded-2xl border-2 border-ink bg-popover py-1.5 pr-1.5 pl-3 text-popover-foreground"
    >
      <Loader2 className="size-4 shrink-0 animate-spin" />
      <span className="flex-1">Compiling shader… (Happens when a model opens and when you change the model tree)</span>
      <Button variant="ghost" size="icon-xs" className="rounded-full" aria-label="Dismiss" onClick={() => setDismissed(compiling)}>
        <X />
      </Button>
    </div>
  )
}

const INSPECTOR_HEIGHT_KEY = 'freppuccino.inspectorHeight'
const MIN_PANE = 120
const RESERVED_ABOVE = 36 + 120 + 6

function Splitter(props: { height: number | null; setHeight: (h: number | null) => void; pane: RefObject<HTMLDivElement | null> }) {
  const start = useRef<{ y: number; h: number; max: number } | null>(null)

  const limits = () => {
    const pane = props.pane.current
    const column = pane?.parentElement
    if (!pane || !column) return null
    return { h: pane.offsetHeight, max: Math.max(MIN_PANE, column.clientHeight - RESERVED_ABOVE) }
  }

  const clamp = (h: number, max: number) => Math.min(Math.max(h, MIN_PANE), max)

  return (
    <div
      role="separator"
      aria-orientation="horizontal"
      aria-label="Resize browser and inspector"
      tabIndex={0}
      title="Drag to resize. Double-click to reset."
      onPointerDown={(e: PointerEvent<HTMLDivElement>) => {
        const l = limits()
        if (!l || e.button !== 0) return
        e.currentTarget.setPointerCapture(e.pointerId)
        start.current = { y: e.clientY, h: l.h, max: l.max }
      }}
      onPointerMove={(e) => {
        const s = start.current
        if (s) props.setHeight(clamp(s.h + s.y - e.clientY, s.max))
      }}
      onPointerUp={(e) => {
        start.current = null
        e.currentTarget.releasePointerCapture(e.pointerId)
      }}
      onDoubleClick={() => props.setHeight(null)}
      onKeyDown={(e: ReactKeyboardEvent<HTMLDivElement>) => {
        if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') return
        const l = limits()
        if (!l) return
        e.preventDefault()
        props.setHeight(clamp(l.h + (e.key === 'ArrowUp' ? 16 : -16), l.max))
      }}
      className="group relative z-10 -my-[3px] h-1.5 shrink-0 cursor-row-resize touch-none outline-none"
    >
      <div className="pointer-events-none absolute inset-x-0 top-1/2 h-px -translate-y-1/2 bg-border transition-[height,background-color] group-hover:h-0.5 group-hover:bg-primary group-focus-visible:h-0.5 group-focus-visible:bg-ring group-active:h-0.5 group-active:bg-primary" />
    </div>
  )
}

function NoticeToast() {
  const notice = useHost((s) => s.notice)
  useEffect(() => {
    if (!notice) return
    const t = setTimeout(() => dismissNotice(notice.id), 4000)
    return () => clearTimeout(t)
  }, [notice])
  if (!notice) return null
  return (
    <div
      role="status"
      className={cn(
        'type-small flex items-center gap-2 rounded-full border-2 border-ink bg-popover py-1.5 pr-1.5 pl-4 text-popover-foreground shadow-float',
        notice.level === 'error' && 'border-destructive text-destructive',
      )}
    >
      <span className="flex-1">{notice.text}</span>
      <Button variant="ghost" size="icon-xs" className="rounded-full" aria-label="Dismiss" onClick={() => dismissNotice(notice.id)}>
        <X />
      </Button>
    </div>
  )
}
