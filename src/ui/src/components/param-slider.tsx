import { useId, useRef, useState } from 'react'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Slider } from '@/components/ui/slider'

/**
 * Label + numeric field + slider. onChange fires while dragging (use for cheap live updates),
 * onCommit on release / Enter / blur (use for anything that writes to the document); every drag that fired
 * onChange ends with one onCommit, even if it ends where it started. Typed values only fire onCommit.
 * Typed values may exceed the slider range.
 */
export function ParamSlider(props: {
  label: string
  value: number
  min?: number
  max: number
  step?: number
  /** Rhino unit system name ("Millimeters") or a word like "sharp"; shown after the number */
  units?: string
  /** typed values may also go below min (otherwise min is a hard floor) */
  belowMin?: boolean
  disabled?: boolean
  onChange?: (value: number) => void
  onCommit?: (value: number) => void
  wrapInput?: (input: React.ReactElement) => React.ReactElement
  onMaxChange?: (max: number) => void
}) {
  const { min = 0, step = 0.01 } = props
  const id = useId()
  const [value, setValue] = useState(props.value)
  const [text, setText] = useState(format(props.value))
  // Follow the prop when the host changes it (undo, command line edits). A drag keeps the finger's value.
  const [prop, setProp] = useState(props.value)
  /** value of an uncommitted drag */
  const dragged = useRef<number | null>(null)
  if (props.value !== prop && dragged.current === null) {
    setProp(props.value)
    setValue(props.value)
    setText(format(props.value))
  }

  const show = (v: number) => {
    setValue(v)
    setText(format(v))
  }
  const commit = (v: number) => {
    dragged.current = null
    props.onCommit?.(v)
  }
  const commitText = () => {
    const v = Number(text)
    if (!Number.isFinite(v) || (!props.belowMin && v < min) || v === value) return setText(format(value))
    show(v)
    commit(v)
  }

  return (
    <div className="grid gap-2">
      <div className="flex items-center justify-between gap-2">
        <Label htmlFor={id} className="type-label min-w-0 truncate text-muted-foreground">
          {props.label}
        </Label>
        <div className="flex items-center gap-1.5">
          {(props.wrapInput ?? ((input) => input))(
            <Input
              id={id}
              inputMode="decimal"
              value={text}
              disabled={props.disabled}
              onChange={(e) => setText(e.target.value)}
              onBlur={commitText}
              onKeyDown={(e) => e.key === 'Enter' && (e.currentTarget as HTMLInputElement).blur()}
              className="type-numeric h-7 w-16 px-1.5 text-right"
            />,
          )}
          {props.units && <span className="type-numeric w-8 text-muted-foreground">{abbreviate(props.units)}</span>}
        </div>
      </div>
      <div className="flex items-center gap-2">
        <Slider
          className="flex-1"
          min={Math.min(min, value)}
          max={Math.max(props.max, value)}
          step={step}
          value={[value]}
          disabled={props.disabled}
          onValueChange={([v]) => {
            show(v)
            dragged.current = v
            props.onChange?.(v)
          }}
          // Deferred: on keys Radix commits before onValueChange.
          onValueCommit={([v]) => queueMicrotask(() => commit(v))}
          // Radix skips onValueCommit when a drag ends where it started; close the gesture anyway.
          onPointerUp={() =>
            setTimeout(() => {
              if (dragged.current !== null) commit(dragged.current)
            })
          }
        />
        {props.onMaxChange && <MaxField value={props.max} onCommit={props.onMaxChange} />}
      </div>
    </div>
  )
}

function MaxField(props: { value: number; onCommit: (v: number) => void }) {
  const [text, setText] = useState(format(props.value))
  const [shown, setShown] = useState(props.value)
  if (props.value !== shown) {
    setShown(props.value)
    setText(format(props.value))
  }
  const commit = () => {
    const v = Number(text)
    if (!Number.isFinite(v) || v <= 0) return setText(format(props.value))
    props.onCommit(v)
  }
  return (
    <label className="flex shrink-0 items-center gap-1">
      <span className="type-label text-muted-foreground">max</span>
      <Input
        inputMode="decimal"
        aria-label="Slider maximum"
        value={text}
        onChange={(e) => setText(e.target.value)}
        onBlur={commit}
        onKeyDown={(e) => e.key === 'Enter' && (e.currentTarget as HTMLInputElement).blur()}
        className="type-numeric h-6 w-12 px-1 text-right"
      />
    </label>
  )
}

const format = (v: number) => String(Math.round(v * 1000) / 1000)

const UNIT_ABBREVIATIONS: Record<string, string> = {
  Millimeters: 'mm', Centimeters: 'cm', Meters: 'm', Kilometers: 'km', Microns: 'µm',
  Inches: 'in', Feet: 'ft', Yards: 'yd', Miles: 'mi',
}
export const abbreviate = (units: string) => UNIT_ABBREVIATIONS[units] ?? units
