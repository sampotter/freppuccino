import { ArrowUp, BookOpen, Camera, CircleAlert, ImagePlus, KeyRound, Loader2, Plus, Square, Wrench, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { newChat, sendPrompt, setApiKey, setModel, stop, useChat, type ChatItem } from '@/ai/agent'
import { MODEL_PRESETS } from '@/ai/config'
import { isImageFile, MAX_ATTACHMENTS, readImageFile, type Attachment } from '@/ai/images'
import { useHost } from '@/bridge/host'
import { cn } from '@/lib/utils'

const SUGGESTIONS = ['Make a coffee cup', 'Make a rubber duck', 'Make a chess pawn', 'Make a simple table']

/** AI tab: chat with the modeling agent (src/ai/agent.ts). */
export function AiPanel() {
  const items = useChat((s) => s.items)
  const running = useChat((s) => s.running)
  const host = useHost((s) => s.host)
  const keySource = useChat((s) => s.keySource)
  const [keyOpen, setKeyOpen] = useState(false)
  const [draft, setDraft] = useState('')
  const [attachments, setAttachments] = useState<Attachment[]>([])
  const [attachError, setAttachError] = useState('')
  const [loadingImages, setLoadingImages] = useState(0)
  const [dragging, setDragging] = useState(false)
  const [zoomed, setZoomed] = useState<{ view: string; dataUrl: string } | null>(null)
  const scroller = useRef<HTMLDivElement>(null)
  const fileInput = useRef<HTMLInputElement>(null)

  // Follow new messages unless the user scrolled up to read.
  const stick = useRef(true)
  useEffect(() => {
    const el = scroller.current
    if (el && stick.current) el.scrollTop = el.scrollHeight
  }, [items])

  const canSend = (draft.trim() !== '' || attachments.length > 0) && loadingImages === 0

  const submit = (text = draft, images = attachments) => {
    if ((!text.trim() && images.length === 0) || running || loadingImages > 0) return
    stick.current = true
    // Without a key nothing is sent: keep the draft so it isn't lost.
    if (keySource !== 'none') {
      setDraft('')
      setAttachments([])
      setAttachError('')
    }
    void sendPrompt(text, images)
  }

  /** Reads image files (picker, paste, drop) into attachments, up to MAX_ATTACHMENTS. */
  const addFiles = async (files: File[]) => {
    const images = files.filter(isImageFile)
    if (images.length === 0) {
      if (files.length) setAttachError('Only image files can be attached.')
      return
    }
    const room = MAX_ATTACHMENTS - attachments.length - loadingImages
    if (room <= 0) {
      setAttachError(`Up to ${MAX_ATTACHMENTS} images per message.`)
      return
    }
    const take = images.slice(0, room)
    setAttachError(images.length > room ? `Up to ${MAX_ATTACHMENTS} images per message.` : '')
    setLoadingImages((n) => n + take.length)
    const results = await Promise.allSettled(take.map(readImageFile))
    setLoadingImages((n) => n - take.length)
    const ok = results.flatMap((r) => (r.status === 'fulfilled' ? [r.value] : []))
    const failed = results.flatMap((r) => (r.status === 'rejected' ? [(r.reason as Error).message] : []))
    if (ok.length) setAttachments((a) => [...a, ...ok].slice(0, MAX_ATTACHMENTS))
    if (failed.length) setAttachError(`Could not attach: ${failed.join('; ')}`)
  }

  return (
    <div
      className="flex min-h-0 flex-1 flex-col"
      onDragOver={(e) => {
        if (!e.dataTransfer.types.includes('Files')) return
        e.preventDefault()
        e.dataTransfer.dropEffect = 'copy'
        setDragging(true)
      }}
      onDragLeave={(e) => {
        if (!e.currentTarget.contains(e.relatedTarget as Node | null)) setDragging(false)
      }}
      onDrop={(e) => {
        if (!e.dataTransfer.types.includes('Files')) return
        e.preventDefault()
        setDragging(false)
        void addFiles(Array.from(e.dataTransfer.files))
      }}
    >
      <Toolbar keyOpen={keyOpen} onToggleKey={() => setKeyOpen((o) => !o)} />
      {(keyOpen || keySource === 'none') && <KeyForm onSaved={() => setKeyOpen(false)} />}
      <div
        ref={scroller}
        onScroll={(e) => {
          const el = e.currentTarget
          stick.current = el.scrollHeight - el.scrollTop - el.clientHeight < 40
        }}
        className="min-h-0 flex-1 overflow-y-auto px-3 py-3"
      >
        {items.length === 0 ? (
          <Empty onPick={submit} mock={host === 'mock'} />
        ) : (
          <div className="grid gap-2">
            {items.map((it) => (
              <Item key={it.id} item={it} onZoom={setZoomed} />
            ))}
            {running && (
              <div className="type-small flex items-center gap-2 px-1 text-muted-foreground">
                <Loader2 className="size-3.5 animate-spin" /> Working…
              </div>
            )}
          </div>
        )}
      </div>

      <form
        className={cn('grid shrink-0 gap-2 border-t bg-card p-3', dragging && 'bg-accent ring-2 ring-ring ring-inset')}
        onSubmit={(e) => {
          e.preventDefault()
          submit()
        }}
      >
        {(attachments.length > 0 || loadingImages > 0) && (
          <div className="flex flex-wrap gap-1.5">
            {attachments.map((a, i) => (
              <div key={`${i}-${a.name}`} className="relative size-14 overflow-hidden rounded-md border">
                <button type="button" className="block size-full" title={a.name} onClick={() => setZoomed({ view: a.name, dataUrl: a.dataUrl })}>
                  <img src={a.dataUrl} alt={a.name} className="size-full object-cover" />
                </button>
                <button
                  type="button"
                  aria-label={`Remove ${a.name}`}
                  title="Remove"
                  className="absolute top-0.5 right-0.5 rounded-full bg-background/90 p-0.5 hover:bg-accent"
                  onClick={() => setAttachments((list) => list.filter((_, j) => j !== i))}
                >
                  <X className="size-3" />
                </button>
              </div>
            ))}
            {Array.from({ length: loadingImages }, (_, i) => (
              <div key={`loading-${i}`} className="flex size-14 items-center justify-center rounded-md border text-muted-foreground">
                <Loader2 className="size-4 animate-spin" />
              </div>
            ))}
          </div>
        )}
        {attachError && (
          <div className="type-small flex items-start gap-1.5 text-destructive">
            <CircleAlert className="mt-0.5 size-3.5 shrink-0" />
            <span>{attachError}</span>
          </div>
        )}
        <div className="flex items-end gap-2">
          <input
            ref={fileInput}
            type="file"
            accept="image/*"
            multiple
            hidden
            onChange={(e) => {
              void addFiles(Array.from(e.target.files ?? []))
              e.target.value = '' // picking the same file again still fires onChange
            }}
          />
          <Button
            type="button"
            variant="ghost"
            size="icon"
            aria-label="Attach reference image"
            title={`Attach reference images (or paste / drop them), up to ${MAX_ATTACHMENTS}`}
            disabled={attachments.length + loadingImages >= MAX_ATTACHMENTS}
            onClick={() => fileInput.current?.click()}
          >
            <ImagePlus />
          </Button>
          <textarea
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing) {
                e.preventDefault()
                submit()
              }
            }}
            onPaste={(e) => {
              const files = Array.from(e.clipboardData.files).filter(isImageFile)
              if (files.length === 0) return // plain text paste
              e.preventDefault()
              void addFiles(files)
            }}
            rows={2}
            placeholder={running ? 'Working…' : attachments.length ? 'Describe what to build from the image (optional)…' : 'Describe something to build, or attach an image…'}
            className="max-h-40 min-h-9 flex-1 resize-none rounded-md border border-input bg-background px-2 py-1.5 text-sm outline-none placeholder:text-muted-foreground focus-visible:ring-2 focus-visible:ring-ring"
          />
          {running ? (
            <Button type="button" variant="outline" size="icon" aria-label="Stop" title="Stop" onClick={stop}>
              <Square className="fill-current" />
            </Button>
          ) : (
            <Button type="submit" size="icon" aria-label="Send" title="Send (Enter)" disabled={!canSend}>
              <ArrowUp />
            </Button>
          )}
        </div>
      </form>

      {zoomed && (
        <button
          type="button"
          className="absolute inset-0 z-10 flex flex-col items-center justify-center gap-2 bg-background/90 p-3"
          onClick={() => setZoomed(null)}
          aria-label="Close screenshot"
        >
          <img src={zoomed.dataUrl} alt={zoomed.view} className="max-h-full max-w-full rounded-md border" />
          <span className="type-label text-muted-foreground">{zoomed.view}</span>
        </button>
      )}
    </div>
  )
}

function Toolbar(props: { keyOpen: boolean; onToggleKey: () => void }) {
  const keySource = useChat((s) => s.keySource)
  const model = useChat((s) => s.model)
  const usage = useChat((s) => s.usage)
  const running = useChat((s) => s.running)
  return (
    <div className="flex shrink-0 items-center gap-2 border-b px-3 py-2">
      {/* keyed so the field resets when the model changes */}
      <ModelInput key={model} model={model} />
      {usage.prompt > 0 && (
        <span className="type-numeric shrink-0 text-muted-foreground" title={`${usage.prompt.toLocaleString()} in / ${usage.completion.toLocaleString()} out tokens`}>
          {usage.cost > 0 ? `$${usage.cost.toFixed(2)}` : `${Math.round((usage.prompt + usage.completion) / 1000)}k tok`}
        </span>
      )}
      <Button
        variant="ghost"
        size="icon-sm"
        aria-label="OpenRouter key"
        aria-expanded={props.keyOpen}
        title={keySource === 'none' ? 'Add your OpenRouter key' : 'OpenRouter key'}
        className={cn(keySource === 'none' && 'text-destructive')}
        onClick={props.onToggleKey}
      >
        <KeyRound />
      </Button>
      <Button variant="ghost" size="icon-sm" aria-label="New chat" title="New chat" disabled={running} onClick={newChat}>
        <Plus />
      </Button>
    </div>
  )
}

/** Bring your own key: pasted into a password field, kept in the panel's localStorage (see ai/config.ts). */
function KeyForm(props: { onSaved: () => void }) {
  const source = useChat((s) => s.keySource)
  const [value, setValue] = useState('')
  return (
    <form
      className="grid shrink-0 gap-2 border-b bg-card px-3 py-3"
      onSubmit={(e) => {
        e.preventDefault()
        if (!value.trim()) return
        setApiKey(value)
        setValue('')
        props.onSaved()
      }}
    >
      <label htmlFor="ai-key" className="type-label text-muted-foreground">
        OpenRouter API key
      </label>
      <div className="flex gap-2">
        <Input
          id="ai-key"
          type="password"
          autoComplete="off"
          spellCheck={false}
          autoFocus={source === 'none'}
          placeholder={source === 'pasted' ? 'Paste a new key to replace the saved one' : 'sk-or-v1-…'}
          value={value}
          onChange={(e) => setValue(e.target.value)}
          className="h-7 flex-1 text-xs"
        />
        <Button type="submit" size="sm" disabled={!value.trim()}>
          Save
        </Button>
      </div>
      <p className="type-small text-muted-foreground">
        {source === 'pasted' && (
          <>
            A key is saved in this panel.{' '}
            <button type="button" className="text-caramel-ink underline-offset-4 hover:underline" onClick={() => setApiKey('')}>
              Remove it
            </button>
          </>
        )}
        {source === 'env' && <>Using the key from src/ui/.env.local. A pasted key overrides it.</>}
        {source === 'none' && (
          <>
            Get one at{' '}
            <a href="https://openrouter.ai/keys" target="_blank" rel="noreferrer" className="text-caramel-ink underline-offset-4 hover:underline">
              openrouter.ai/keys
            </a>
            . It is stored only in this panel on this computer.
          </>
        )}
      </p>
    </form>
  )
}

function ModelInput(props: { model: string }) {
  const [value, setValue] = useState(props.model)
  const commit = () => {
    const v = value.trim()
    if (v && v !== props.model) setModel(v)
    else setValue(props.model)
  }
  return (
    <>
      <Input
        list="ai-models"
        value={value}
        onChange={(e) => setValue(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => e.key === 'Enter' && e.currentTarget.blur()}
        aria-label="Model"
        title="OpenRouter model id (needs vision + tools)"
        className="h-7 flex-1 text-xs"
      />
      <datalist id="ai-models">
        {MODEL_PRESETS.map((m) => (
          <option key={m} value={m} />
        ))}
      </datalist>
    </>
  )
}

function Empty(props: { onPick: (text: string) => void; mock: boolean }) {
  return (
    <div className="grid gap-3 px-1 pt-2">
      <p className="type-small text-muted-foreground">
        Describe an object and the assistant builds it from F-rep primitives and CSG, then checks screenshots and fixes what looks
        wrong. Each step is undoable in Rhino.
      </p>
      <p className="type-small text-muted-foreground">
        To model from a photo or sketch, attach it with the image button, or paste / drop it onto the panel.
      </p>
      {props.mock && (
        <p className="type-small text-muted-foreground">Mock host: no geometry or screenshots, but the tool loop runs.</p>
      )}
      <div className="flex flex-wrap gap-1.5">
        {SUGGESTIONS.map((s) => (
          <Button key={s} variant="outline" size="xs" onClick={() => props.onPick(s)}>
            {s}
          </Button>
        ))}
      </div>
    </div>
  )
}

function Item(props: { item: ChatItem; onZoom: (img: { view: string; dataUrl: string }) => void }) {
  const it = props.item
  switch (it.kind) {
    case 'user':
      return (
        <div className="ml-6 grid gap-1.5 justify-self-end rounded-lg bg-secondary px-3 py-2 whitespace-pre-wrap text-secondary-foreground">
          {it.images && it.images.length > 0 && (
            <div className="flex flex-wrap justify-end gap-1.5">
              {it.images.map((img, i) => (
                <button
                  key={`${i}-${img.name}`}
                  type="button"
                  title={img.name}
                  onClick={() => props.onZoom({ view: img.name, dataUrl: img.dataUrl })}
                  className="overflow-hidden rounded-md border"
                >
                  <img src={img.dataUrl} alt={img.name} className="block max-h-28 max-w-40 object-contain" />
                </button>
              ))}
            </div>
          )}
          {it.text && <span>{it.text}</span>}
        </div>
      )
    case 'assistant':
      return <div className="px-1 whitespace-pre-wrap">{it.text}</div>
    case 'info':
      return (
        <div className={cn('type-small flex items-start gap-1.5 px-1', it.error ? 'text-destructive' : 'text-muted-foreground')}>
          {it.error && <CircleAlert className="mt-0.5 size-3.5 shrink-0" />}
          <span>{it.text}</span>
        </div>
      )
    case 'tool':
      return <ToolRow item={it} onZoom={props.onZoom} />
  }
}

function ToolRow(props: { item: Extract<ChatItem, { kind: 'tool' }>; onZoom: (img: { view: string; dataUrl: string }) => void }) {
  const { item } = props
  const [open, setOpen] = useState(false)
  const Icon = item.status === 'running' ? Loader2 : item.name === 'capture_views' ? Camera : item.name === 'load_skill' ? BookOpen : Wrench
  return (
    <div className="grid gap-1.5">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        title="Show arguments"
        className={cn(
          'type-small flex min-w-0 items-center gap-1.5 rounded-sm px-1 py-0.5 text-left text-muted-foreground hover:bg-accent',
          item.status === 'error' && 'text-destructive',
        )}
      >
        <Icon className={cn('size-3.5 shrink-0', item.status === 'running' && 'animate-spin')} />
        <span className="type-command shrink-0">{item.name}</span>
        {item.summary && <span className="truncate">{item.summary}</span>}
        {item.status === 'error' && <X className="ml-auto size-3.5 shrink-0" />}
      </button>
      {open && (
        <pre className="type-small max-h-48 overflow-auto rounded-sm bg-muted px-2 py-1 font-mono break-all whitespace-pre-wrap">
          {pretty(item.args)}
        </pre>
      )}
      {item.images && item.images.length > 0 && (
        <div className="grid grid-cols-2 gap-1.5">
          {item.images.map((img) => (
            <button key={img.view} type="button" onClick={() => props.onZoom(img)} className="relative overflow-hidden rounded-md border">
              <img src={img.dataUrl} alt={img.view} className="block w-full" />
              <span className="type-label absolute bottom-1 left-1 rounded-sm bg-background/80 px-1">{img.view}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

function pretty(json: string): string {
  try {
    return JSON.stringify(JSON.parse(json), null, 1)
  } catch {
    return json
  }
}
