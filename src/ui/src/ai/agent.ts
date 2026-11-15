import { useSyncExternalStore } from 'react'
import { getHostState, send as sendToHost } from '@/bridge/host'
import { getApiKey, KEEP_SCREENSHOT_SETS, keySource, loadModel, MAX_STEPS, saveModel, storeApiKey, type KeySource } from './config'
import { dataUrlExtension, type Attachment } from './images'
import { ApiError, chatCompletion, type ContentPart, type Message } from './openrouter'
import { systemPrompt } from './prompt'
import { runTool, TOOLS } from './tools'

/** Starts the text part in front of user-attached images; such messages are never pruned (see prepareForSend). */
const REFERENCE_LABEL = '[reference images attached by the user'
/** Prompt sent when the user attaches images without typing anything. */
const IMAGE_ONLY_PROMPT = 'Model the object shown in the attached reference image.'

// The agent loop: prompt → model → tool calls (run in Rhino) → results (+ screenshots as images) → model → ... until the
// model answers without tool calls. The conversation persists across prompts until "New chat".

// ── Chat store (what the AI tab renders) ──

export type ChatItem =
  | { kind: 'user'; id: number; text: string; images?: Attachment[] }
  | { kind: 'assistant'; id: number; text: string }
  | { kind: 'tool'; id: number; name: string; args: string; status: 'running' | 'ok' | 'error'; summary?: string; images?: { view: string; dataUrl: string }[] }
  | { kind: 'info'; id: number; text: string; error?: boolean }

export interface Usage {
  prompt: number
  completion: number
  /** USD, as reported by OpenRouter */
  cost: number
}

export interface ChatState {
  items: ChatItem[]
  running: boolean
  model: string
  usage: Usage
  /** where the OpenRouter key comes from */
  keySource: KeySource
}

let state: ChatState = { items: [], running: false, model: loadModel(), usage: { prompt: 0, completion: 0, cost: 0 }, keySource: keySource() }
const listeners = new Set<() => void>()
let nextId = 0

function set(patch: Partial<ChatState>) {
  state = { ...state, ...patch }
  listeners.forEach((l) => l())
}

/** Omit that keeps the union cases apart. */
type NewItem = ChatItem extends unknown ? (ChatItem extends infer T ? (T extends ChatItem ? Omit<T, 'id'> : never) : never) : never

function push(item: NewItem): number {
  const id = ++nextId
  set({ items: [...state.items, { ...item, id } as ChatItem] })
  return id
}

function update(id: number, patch: Partial<ChatItem>) {
  set({ items: state.items.map((it) => (it.id === id ? ({ ...it, ...patch } as ChatItem) : it)) })
}

export function useChat<T>(selector: (s: ChatState) => T): T {
  return useSyncExternalStore(
    (l) => {
      listeners.add(l)
      return () => listeners.delete(l)
    },
    () => selector(state),
  )
}

export function setModel(model: string) {
  saveModel(model)
  set({ model })
}

/** Saves a pasted OpenRouter key in the panel (localStorage); empty removes it. */
export function setApiKey(key: string) {
  storeApiKey(key.trim())
  set({ keySource: keySource() })
}

// ── Conversation ──

/** Everything after the system prompt, in OpenAI chat format. */
let history: Message[] = []
let abort: AbortController | null = null

export function newChat() {
  if (state.running) return
  history = []
  set({ items: [], usage: { prompt: 0, completion: 0, cost: 0 } })
}

export function stop() {
  abort?.abort()
}

/** Runs one prompt (text and / or reference images) to the end. Resolves to false when nothing was sent (empty, already running, no key). */
export async function sendPrompt(prompt: string, images: Attachment[] = []): Promise<boolean> {
  prompt = prompt.trim()
  if ((!prompt && images.length === 0) || state.running) return false
  if (!getApiKey()) {
    push({ kind: 'info', text: 'No OpenRouter key. Paste one with the key button above.', error: true })
    return false
  }

  const runStart = history.length
  const startedAt = new Date().toISOString()
  const model = state.model
  const usageBefore = state.usage
  push({ kind: 'user', text: prompt, ...(images.length ? { images } : {}) })
  const text = prompt || IMAGE_ONLY_PROMPT
  history.push({
    role: 'user',
    content: images.length
      ? [
          { type: 'text', text: `${REFERENCE_LABEL}: ${images.map((i) => i.name.replace(/[,[\]]/g, '_')).join(', ')}]` },
          ...images.map((i): ContentPart => ({ type: 'image_url', image_url: { url: i.dataUrl } })),
          { type: 'text', text },
        ]
      : text,
  })
  if (!prompt) prompt = images.length === 1 ? `image: ${images[0].name}` : `${images.length} images`
  abort = new AbortController()
  set({ running: true })

  let outcome = 'done'
  try {
    let step = 0
    for (; step < MAX_STEPS; step++) {
      const message = await complete(model, abort.signal)
      const calls = (message.tool_calls ?? []).filter((c) => c.type === 'function' && c.function)
      history.push({ role: 'assistant', content: message.content ?? '', ...(calls.length ? { tool_calls: calls } : {}) })
      if (message.content?.trim()) push({ kind: 'assistant', text: message.content.trim() })
      if (calls.length === 0) break

      const shots: { view: string; dataUrl: string }[] = []
      for (const call of calls) {
        if (abort.signal.aborted) throw new DOMException('Stopped', 'AbortError')
        const { name, arguments: args } = call.function
        const id = push({ kind: 'tool', name, args, status: 'running' })
        const result = await runTool(name, args)
        update(id, { status: result.ok ? 'ok' : 'error', summary: result.summary, images: result.images })
        history.push({ role: 'tool', tool_call_id: call.id, content: result.content })
        if (result.images) shots.push(...result.images)
      }
      // Tool results are text-only in this API, so screenshots go to the model as a user message right after.
      if (shots.length)
        history.push({
          role: 'user',
          content: [
            { type: 'text', text: `[screenshots from capture_views: ${shots.map((s) => s.view).join(', ')}]` },
            ...shots.map((s): ContentPart => ({ type: 'image_url', image_url: { url: s.dataUrl } })),
          ],
        })
    }
    if (step === MAX_STEPS) {
      outcome = 'step limit'
      push({ kind: 'info', text: `Stopped after ${MAX_STEPS} steps. Say "continue" to keep going.` })
    }
  } catch (e) {
    if (abort.signal.aborted || (e as Error).name === 'AbortError') {
      outcome = 'stopped'
      push({ kind: 'info', text: 'Stopped.' })
    } else {
      outcome = 'error'
      push({ kind: 'info', text: describeError(e), error: true })
    }
    closeOpenToolCalls()
  } finally {
    abort = null
    set({ running: false })
    saveRun(prompt, model, startedAt, outcome, runStart, usageBefore)
  }
  return true
}

function isReference(m: Message): boolean {
  return m.role === 'user' && Array.isArray(m.content) && m.content[0]?.type === 'text' && m.content[0].text.startsWith(REFERENCE_LABEL)
}

async function complete(model: string, signal: AbortSignal) {
  const messages = prepareForSend([{ role: 'system', content: systemPrompt(getHostState().settings.units) }, ...history])
  const { message, usage: u } = await chatCompletion({ model, messages, tools: TOOLS, max_tokens: 8192 }, signal)
  if (u)
    set({
      usage: {
        prompt: state.usage.prompt + (u.prompt_tokens ?? 0),
        completion: state.usage.completion + (u.completion_tokens ?? 0),
        cost: state.usage.cost + (u.cost ?? 0),
      },
    })
  return message
}

/**
 * Per request: drop all but the newest screenshot sets (they dominate the payload), and mark cache breakpoints on the
 * system prompt and the last message (OpenRouter passes cache_control to Anthropic / Gemini; others ignore it).
 * Reference images the user attached are always kept: the model compares its screenshots against them.
 */
function prepareForSend(messages: Message[]): Message[] {
  let keep = KEEP_SCREENSHOT_SETS
  const out = [...messages]
  for (let i = out.length - 1; i >= 0; i--) {
    const m = out[i]
    if (m.role !== 'user' || typeof m.content === 'string' || !m.content.some((p) => p.type === 'image_url')) continue
    if (isReference(m)) continue
    if (keep-- > 0) continue
    const n = m.content.filter((p) => p.type === 'image_url').length
    out[i] = { role: 'user', content: `[${n} older screenshots removed to save space]` }
  }
  const cache = { cache_control: { type: 'ephemeral' as const } }
  const mark = (m: Message): Message => {
    if (m.role === 'assistant') return m // assistant content must stay a string here
    if (typeof m.content === 'string' && m.content) return { ...m, content: [{ type: 'text', text: m.content, ...cache }] }
    if (Array.isArray(m.content) && m.content.length)
      return { ...m, content: [...m.content.slice(0, -1), { ...m.content[m.content.length - 1], ...cache }] }
    return m
  }
  out[0] = mark(out[0])
  if (out.length > 1) out[out.length - 1] = mark(out[out.length - 1])
  return out
}

/** After a stop / error mid-step, answer any tool calls left open so the next request is valid. */
function closeOpenToolCalls() {
  const answered = new Set(history.flatMap((m) => (m.role === 'tool' ? [m.tool_call_id] : [])))
  for (let i = history.length - 1; i >= 0; i--) {
    const m = history[i]
    if (m.role !== 'assistant' || !m.tool_calls) continue
    for (const c of m.tool_calls)
      if (!answered.has(c.id)) history.push({ role: 'tool', tool_call_id: c.id, content: JSON.stringify({ error: 'cancelled by the user' }) })
    break
  }
}

function describeError(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 401) return state.keySource === 'env' ? 'OpenRouter rejected the key (401). Check VITE_OPENROUTER_API_KEY in src/ui/.env.local, or paste a key with the key button above.' : 'OpenRouter rejected the key (401). Paste a valid one with the key button above.'
    if (e.status === 402) return 'OpenRouter: out of credits (402).'
    return `OpenRouter error ${e.status ?? ''}: ${e.message}`
  }
  return `Error: ${(e as Error).message ?? String(e)}`
}

// ── Run log (Ai/AiRunLog.cs writes it to disk) ──

function saveRun(prompt: string, model: string, startedAt: string, outcome: string, runStart: number, usageBefore: Usage) {
  const images: { name: string; dataUrl: string }[] = []
  const messages = history.slice(runStart).map((m) => {
    if (m.role !== 'user' || typeof m.content === 'string') return m
    // "[screenshots from capture_views: perspective, front]" / "[reference images attached by the user: cup.jpg]"
    // names the images that follow
    const reference = isReference(m)
    const label = m.content.find((p) => p.type === 'text')?.text ?? ''
    const views = label.match(/: ([^\]]*)\]/)?.[1].split(', ') ?? []
    let v = 0
    return {
      ...m,
      content: m.content.map((p) => {
        if (p.type !== 'image_url') return p
        const raw = views[v++] ?? 'image'
        const view = reference ? `reference-${raw.replace(/\.[^.]*$/, '').replace(/[^a-z0-9_]/gi, '').slice(0, 30) || 'image'}` : raw.replace(/[^a-z0-9_]/gi, '')
        const name = `${String(images.length + 1).padStart(2, '0')}-${view}.${dataUrlExtension(p.image_url.url)}`
        images.push({ name, dataUrl: p.image_url.url })
        return { type: 'image_file', file: name }
      }),
    }
  })
  const u = state.usage
  sendToHost({
    type: 'aiSaveRun',
    title: prompt,
    transcript: {
      prompt,
      model,
      outcome,
      startedAt,
      finishedAt: new Date().toISOString(),
      usage: { prompt: u.prompt - usageBefore.prompt, completion: u.completion - usageBefore.completion, cost: u.cost - usageBefore.cost },
      earlierMessages: runStart,
      system: systemPrompt(getHostState().settings.units),
      messages,
    },
    images,
  })
}
