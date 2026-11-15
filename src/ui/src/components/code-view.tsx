import { Check, CircleAlert, CircleCheck, Copy, Maximize2, Minimize2 } from 'lucide-react'
import { useRef, useState } from 'react'
import { Button } from '@/components/ui/button'
import { cn } from '@/lib/utils'
import type { UserFnInfo } from '@/bridge/protocol'

const LINE_PX = 20
/** Collapsed height cap; drag the corner to resize, or expand to show every line. */
const COLLAPSED_PX = 240

/**
 * Read-only FFunction source: line gutter, lines with diagnostics tinted red (hover for the message),
 * then the diagnostics list (click one to scroll to its line) or "Compiles OK" + hash.
 */
export function CodeView(props: { fn: UserFnInfo }) {
  const { fn } = props
  const lines = fn.code.replace(/\r\n?/g, '\n').split('\n')
  const byLine = new Map<number, string[]>()
  for (const d of fn.diagnostics) byLine.set(d.line, [...(byLine.get(d.line) ?? []), `${d.column}: ${d.message}`])

  const [expanded, setExpanded] = useState(false)
  const [copied, setCopied] = useState(false)
  const rows = useRef<(HTMLDivElement | null)[]>([])
  const gutter = `${String(lines.length).length + 1}ch`
  const fitted = lines.length * LINE_PX + 10

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(fn.code)
    } catch {
      // WebView without clipboard permission: select the text instead so Ctrl/Cmd+C works.
      const range = document.createRange()
      const first = rows.current[0]?.parentElement
      if (first) range.selectNodeContents(first)
      getSelection()?.removeAllRanges()
      getSelection()?.addRange(range)
      return
    }
    setCopied(true)
    setTimeout(() => setCopied(false), 1200)
  }

  return (
    <div className="grid gap-2">
      <div className="flex items-center gap-1">
        <span className="type-label min-w-0 flex-1 truncate text-muted-foreground">Code</span>
        <Button variant="ghost" size="icon-xs" aria-label="Copy code" title="Copy" onClick={copy}>
          {copied ? <Check /> : <Copy />}
        </Button>
        <Button
          variant="ghost"
          size="icon-xs"
          aria-label={expanded ? 'Collapse' : 'Expand'}
          title={expanded ? 'Collapse' : 'Show all lines'}
          onClick={() => setExpanded((e) => !e)}
        >
          {expanded ? <Minimize2 /> : <Maximize2 />}
        </Button>
      </div>
      <div
        // Remount on toggle so a size the user dragged doesn't stick.
        key={String(expanded)}
        className="resize-y overflow-auto rounded-md border border-input bg-card font-mono text-xs"
        style={{ height: expanded ? 'auto' : Math.min(fitted, COLLAPSED_PX), minHeight: Math.min(fitted, 3 * LINE_PX) }}
      >
        <div className="w-max min-w-full py-1" style={{ lineHeight: `${LINE_PX}px` }}>
          {lines.map((text, i) => {
            const issues = byLine.get(i + 1)
            return (
              <div
                key={i}
                ref={(el) => {
                  rows.current[i] = el
                }}
                title={issues?.map((m) => `${i + 1}:${m}`).join('\n')}
                className={cn('flex', issues && 'bg-destructive/12')}
              >
                <span
                  className={cn(
                    'sticky left-0 shrink-0 border-r border-border bg-muted pr-2 text-right text-muted-foreground tabular-nums select-none',
                    issues && 'font-bold text-destructive',
                  )}
                  style={{ width: `calc(${gutter} + 0.5rem)` }}
                >
                  {i + 1}
                </span>
                <span className="pr-3 pl-2 whitespace-pre">{text || ' '}</span>
              </div>
            )
          })}
        </div>
      </div>
      {fn.ok && (
        <p className="type-small flex items-center gap-1.5 text-muted-foreground">
          <CircleCheck className="size-3.5 shrink-0 text-matcha-ink" />
          Compiles OK
          {fn.hash && <span className="type-numeric truncate" title={fn.hash}>· {fn.hash.slice(0, 8)}</span>}
        </p>
      )}
      {!fn.ok && fn.diagnostics.length === 0 && <p className="type-small text-destructive">Doesn't compile.</p>}
      {fn.error && (
        <p className="type-small flex items-start gap-1.5 text-destructive">
          <CircleAlert className="mt-0.5 size-3.5 shrink-0" />
          <span className="min-w-0 break-words">{fn.error}</span>
        </p>
      )}
      {fn.diagnostics.length > 0 && (
        <ul className="grid gap-1" aria-label="Diagnostics">
          {fn.diagnostics.map((d, i) => (
            <li key={i}>
              <button
                type="button"
                className="type-small flex w-full items-start gap-1.5 rounded-sm text-left text-destructive outline-none hover:underline focus-visible:ring-2 focus-visible:ring-ring"
                onClick={() => rows.current[d.line - 1]?.scrollIntoView({ block: 'nearest' })}
              >
                <CircleAlert className="mt-0.5 size-3.5 shrink-0" />
                <span className="min-w-0 break-words">
                  <span className="type-numeric">{d.line}:{d.column}</span> {d.message}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
