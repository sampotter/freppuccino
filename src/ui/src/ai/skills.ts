// Skills: markdown guidance for the agent, one file per skill in ./skills (edit those, not this).
//
//   ---
//   name: vessels          (defaults to the file name)
//   description: One line: when to load it. Shown to the model in the skill index.
//   always: true           (optional: inlined into every system prompt instead of loaded on demand)
//   ---
//   Markdown body...
//
// On-demand skills are listed by name + description; the model reads one with the load_skill tool.
// With `npm run dev` (and FREPPUCCINO_UI_DEV_URL in Rhino) edits apply on the next prompt; otherwise rebuild the UI.

export interface Skill {
  name: string
  description: string
  always: boolean
  body: string
}

const files = {
  ...import.meta.glob('./skills/*.md', { query: '?raw', import: 'default', eager: true }),
  // Shared with external Claude Code sessions and the plugin's `help` tool (single source, embedded by Freppuccino.csproj).
  ...import.meta.glob('../../../../.claude/skills/freppuccino-*/SKILL.md', { query: '?raw', import: 'default', eager: true }),
} as Record<string, string>

function parse(path: string, raw: string): Skill {
  const fileName = path.split('/').pop()!.replace(/\.md$/, '')
  const text = raw.replace(/\r\n/g, '\n')
  const m = text.match(/^---\n([\s\S]*?)\n---\n?/)
  const meta: Record<string, string> = {}
  if (m)
    for (const line of m[1].split('\n')) {
      const i = line.indexOf(':')
      if (i > 0) meta[line.slice(0, i).trim()] = line.slice(i + 1).trim()
    }
  return {
    name: meta.name || fileName,
    description: meta.description || '',
    always: meta.always === 'true',
    body: (m ? text.slice(m[0].length) : text).trim(),
  }
}

export const SKILLS: Skill[] = Object.entries(files)
  .map(([path, raw]) => parse(path, raw))
  .sort((a, b) => Number(b.always) - Number(a.always) || a.name.localeCompare(b.name))

export function findSkill(name: string): Skill | undefined {
  const n = name.trim().toLowerCase()
  return SKILLS.find((s) => s.name.toLowerCase() === n)
}
