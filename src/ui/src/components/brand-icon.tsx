import { cn } from '@/lib/utils'
import { nodeIconName } from '@/lib/scene'
import type { SceneNode } from '@/bridge/protocol'

// FRepuccino command icons (brand-kit/command-icons, ink recoloured to currentColor so one set serves both themes).
// cone / pyramid / ellipsoid / function / pipe aren't in the kit yet; ours follow its style. Inlined so currentColor works.
const svgs = import.meta.glob<string>('../brand/icons/*.svg', { query: '?raw', import: 'default', eager: true })
const icons = Object.fromEntries(Object.entries(svgs).map(([path, svg]) => [path.split('/').pop()!.replace('.svg', ''), svg]))

export type BrandIconName =
  | 'box' | 'sphere' | 'cylinder' | 'cone' | 'pyramid' | 'ellipsoid' | 'torus' | 'function'
  | 'union' | 'subtract' | 'intersect' | 'blend' | 'offset' | 'preview'
  | 'twist' | 'bend' | 'repeat' | 'shell' | 'gyroid' | 'pipe'

/** 24px artwork; size it with className (size-4 / size-6 ...). Colour = ink. */
export function BrandIcon(props: { name: BrandIconName; className?: string }) {
  return (
    <span
      aria-hidden
      data-slot="brand-icon"
      className={cn('inline-block size-4 shrink-0 text-ink [&>svg]:size-full', props.className)}
      dangerouslySetInnerHTML={{ __html: icons[props.name] ?? '' }}
    />
  )
}

export function NodeIcon(props: { node: SceneNode; className?: string }) {
  const name = nodeIconName(props.node)
  return name ? (
    <BrandIcon name={name} className={props.className} />
  ) : (
    <span aria-hidden className={cn('inline-block size-4 shrink-0 rounded-sm border border-dashed border-input', props.className)} />
  )
}
