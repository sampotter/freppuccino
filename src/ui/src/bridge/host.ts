import { useSyncExternalStore } from 'react'
import { createMockHost } from './mock-host'
import type { HostMessage, Id, PanelTab, SceneNode, Theme, UiMessage, UiSettings } from './protocol'

// ── Transport ──
// One shape for every host (post + listen); the store doesn't know which it got. C# side: Ui/WebViewHost.cs.
//   Windows (WebView2): window.chrome.webview; host → page via PostWebMessageAsJson.
//   macOS (WKWebView via Eto): window.eto.postMessage(JSON string); host → page calls window.__freppuccino.receive(msg).
//   Plain browser (`npm run dev`): a mock host stands in so the UI can be built without Rhino.

interface WebView2 {
  postMessage(message: unknown): void
  addEventListener(type: 'message', listener: (e: { data: unknown }) => void): void
}
declare global {
  interface Window {
    chrome?: { webview?: WebView2 }
    /** injected by Eto's WKWebViewHandler; the body must be a string */
    eto?: { postMessage(message: string): void }
    __freppuccino?: { receive(message: HostMessage): void }
  }
}

export interface Transport {
  kind: 'rhino' | 'mock'
  post(message: UiMessage): void
  listen(onMessage: (message: HostMessage) => void): void
}

function createTransport(): Transport {
  const webview = window.chrome?.webview
  if (webview)
    return {
      kind: 'rhino',
      post: (m) => webview.postMessage(m),
      listen: (cb) => webview.addEventListener('message', (e) => cb(e.data as HostMessage)),
    }
  const eto = window.eto
  if (eto)
    return {
      kind: 'rhino',
      post: (m) => eto.postMessage(JSON.stringify(m)),
      listen: (cb) => {
        window.__freppuccino = { receive: cb }
      },
    }
  return createMockHost()
}

// ── Store ──

export interface Notice {
  id: number
  level: 'info' | 'warning' | 'error'
  text: string
}

export interface HostState {
  host: Transport['kind']
  /** false until the first "state" arrives */
  synced: boolean
  scene: SceneNode[]
  /** selected ids in pick order (oldest first); Rhino doesn't report order, so the store keeps it */
  selection: Id[]
  settings: UiSettings
  theme: Theme
  frepMode: boolean
  notice: Notice | null
  compiling: number
  tabRequest: { tab: PanelTab; seq: number } | null
}

const transport = createTransport()

let state: HostState = {
  host: transport.kind,
  synced: false,
  scene: [],
  selection: [],
  settings: { units: '', display: { cutters: 'Silhouette', otherShapes: 'Silhouette', geometry: 'Ghosted', hiddenLines: 'Faint' } },
  theme: window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light',
  frepMode: true,
  notice: null,
  compiling: 0,
  tabRequest: null,
}
const listeners = new Set<() => void>()
let noticeId = 0
let compileId = 0

function set(patch: Partial<HostState>) {
  state = { ...state, ...patch }
  listeners.forEach((l) => l())
}

/** Keeps ids that stay selected in their old order and appends newly selected ones. */
function orderSelection(prev: Id[], next: Id[]): Id[] {
  const nextSet = new Set(next)
  const kept = prev.filter((id) => nextSet.has(id))
  const keptSet = new Set(kept)
  return [...kept, ...next.filter((id) => !keptSet.has(id))]
}

transport.listen((m) => {
  switch (m.type) {
    case 'state':
      set({ synced: true, scene: m.scene, selection: m.selection, settings: m.settings, theme: m.theme, frepMode: m.frepMode })
      break
    case 'scene':
      set({ scene: m.scene })
      break
    case 'view':
      set({ frepMode: m.frepMode })
      break
    case 'settings':
      set({ settings: m.settings })
      break
    case 'showTab':
      set({ tabRequest: { tab: m.tab, seq: (state.tabRequest?.seq ?? 0) + 1 } })
      transport.post({ type: 'tabShown' })
      break
    case 'selection':
      set({ selection: orderSelection(state.selection, m.ids) })
      break
    case 'notice':
      set({ notice: { id: ++noticeId, level: m.level, text: m.text } })
      break
    case 'compiling':
      if (m.active !== state.compiling > 0) set({ compiling: m.active ? ++compileId : 0 })
      break
    case 'aiToolResult': {
      const pending = toolCalls.get(m.callId)
      if (!pending) break
      toolCalls.delete(m.callId)
      if (m.ok) pending.resolve(m.result)
      else pending.reject(new ToolError(m.error ?? 'tool failed'))
      break
    }
  }
})

export function send(message: UiMessage) {
  transport.post(message)
}

// ── AI tool calls (request / response over the same transport) ──

/** A tool failure reported by the host; the message is meant for the model. */
export class ToolError extends Error {}

const toolCalls = new Map<string, { resolve: (r: unknown) => void; reject: (e: Error) => void }>()
let toolCallId = 0

/** Runs an AI tool on the host (Ai/AiTools.cs). Rejects with ToolError on failure, or after timeoutMs. */
export function callTool(tool: string, args: Record<string, unknown>, timeoutMs = 60_000, source: 'ai' | 'script' = 'ai'): Promise<unknown> {
  const callId = `c${++toolCallId}`
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      toolCalls.delete(callId)
      reject(new ToolError(`${tool}: no reply from Rhino within ${timeoutMs / 1000}s (is a command running?)`))
    }, timeoutMs)
    toolCalls.set(callId, {
      resolve: (r) => (clearTimeout(timer), resolve(r)),
      reject: (e) => (clearTimeout(timer), reject(e)),
    })
    transport.post({ type: 'aiTool', callId, tool, args, source })
  })
}

export function dismissNotice(id: number) {
  if (state.notice?.id === id) set({ notice: null })
}

/** Current snapshot, for non-React code (the AI agent). */
export function getHostState(): HostState {
  return state
}

export function useHost<T>(selector: (s: HostState) => T): T {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l)
      return () => listeners.delete(l)
    },
    () => selector(state),
  )
}

/** Call once at startup; the host answers with a full "state". */
export function connect() {
  transport.post({ type: 'ready' })
}
