import type { ReactElement } from 'react'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { abbreviate } from '@/components/param-slider'

export function blendHintBody(op: string | undefined) {
  return op === 'blend' ? 'Rounds the joins between the copies. 0 keeps them sharp.' : 'Rounds the joins between the rows of this group. 0 keeps them sharp.'
}

export function BlendHint(props: { op: string | undefined; units: string; children: ReactElement }) {
  return (
    <Tooltip>
      <TooltipTrigger asChild>{props.children}</TooltipTrigger>
      <TooltipContent side="bottom" className="block max-w-56">
        <p className="font-bold">Blend radius ({abbreviate(props.units)})</p>
        <p>{blendHintBody(props.op)}</p>
      </TooltipContent>
    </Tooltip>
  )
}
