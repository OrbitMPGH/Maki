import { useEffect, useRef, useState } from 'react'
import type { Ref } from 'react'
import { ActionIcon, CloseButton, Popover, Text, Tooltip, UnstyledButton } from '@mantine/core'
import type { MantineSize } from '@mantine/core'
import { IconDice5 } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { pickRandom, prefersReducedMotion, useDiceTumble } from '../lib/lucky'

export interface LuckyCandidate {
  key: string
  title: string
  coverUrl: string | null
}

const FLIP_STEPS = [50, 60, 70, 90, 120, 160, 220, 280]
const HOLD_MS = 350

export function DiceIcon({ size = 18, spinRef }: { size?: number; spinRef?: Ref<HTMLSpanElement> }) {
  return (
    <span ref={spinRef} className="lucky-dice">
      <IconDice5 size={size} />
    </span>
  )
}

/** A few random faces from the pool, never the same one twice in a row, ending on the winner. */
function flipSequence(pool: LuckyCandidate[], winner: LuckyCandidate): LuckyCandidate[] {
  const count = 6 + Math.floor(Math.random() * 3)
  const faces: LuckyCandidate[] = []
  for (let i = 0; i < count; i++) {
    const prev = faces[faces.length - 1]
    const face = pickRandom(pool, (c) => c.key === winner.key || c.key === prev?.key)
    if (face) faces.push(face)
  }
  faces.push(winner)
  return faces
}

export function LuckyButton({
  candidates,
  onPick,
  size = 36,
  variant = 'icon',
  onDismiss,
}: {
  candidates: LuckyCandidate[]
  onPick: (key: string) => void
  size?: MantineSize | number
  variant?: 'icon' | 'pill'
  /** Pill only: the close X. */
  onDismiss?: () => void
}) {
  const { t } = useLingui()
  const { scope, tumble } = useDiceTumble()
  const [face, setFace] = useState<LuckyCandidate | null>(null)
  const [revealed, setRevealed] = useState(false)
  const rolling = useRef(false)
  const timers = useRef<number[]>([])
  const onPickRef = useRef(onPick)
  onPickRef.current = onPick

  useEffect(
    () => () => {
      for (const id of timers.current) window.clearTimeout(id)
      timers.current = []
    },
    [],
  )

  const empty = candidates.length === 0

  const roll = () => {
    if (rolling.current || empty) return
    const winner = pickRandom(candidates)
    if (!winner) return
    if (prefersReducedMotion()) {
      onPick(winner.key)
      return
    }
    rolling.current = true
    const faces = flipSequence(candidates, winner)
    for (const f of faces) {
      if (f.coverUrl) new Image().src = f.coverUrl
    }
    void tumble(700)
    setRevealed(false)
    setFace(faces[0])
    let at = 0
    const steps = FLIP_STEPS.slice(FLIP_STEPS.length - (faces.length - 1))
    faces.slice(1).forEach((f, i) => {
      at += steps[i]
      const last = i === faces.length - 2
      timers.current.push(
        window.setTimeout(() => {
          setFace(f)
          if (last) setRevealed(true)
        }, at),
      )
    })
    timers.current.push(
      window.setTimeout(() => {
        rolling.current = false
        timers.current = []
        setFace(null)
        setRevealed(false)
        onPickRef.current(winner.key)
      }, at + HOLD_MS),
    )
  }

  const label = empty ? t`Nothing to roll` : t`Feeling lucky`

  const dropdown = (
    <Popover.Dropdown className="lucky-roll" p="xs">
      <div className="lucky-roll-cover">
        {face?.coverUrl ? <img src={face.coverUrl} alt="" decoding="async" /> : null}
      </div>
      <Text className="lucky-roll-title" size="xs" fw={600} lineClamp={2} data-shown={revealed || undefined}>
        {revealed && face ? face.title : ' '}
      </Text>
    </Popover.Dropdown>
  )

  if (variant === 'pill') {
    return (
      <div className="lucky-pill" role="group">
        <Popover opened={face !== null} position="top" withArrow shadow="md" offset={12} trapFocus={false}>
          <Popover.Target>
            <UnstyledButton className="lucky-pill-roll" onClick={roll} disabled={empty} aria-label={t`Roll again`}>
              <DiceIcon spinRef={scope} />
              <span>
                <Trans>Roll again</Trans>
              </span>
            </UnstyledButton>
          </Popover.Target>
          {dropdown}
        </Popover>
        <CloseButton size="sm" radius="xl" aria-label={t`Close`} onClick={onDismiss} />
      </div>
    )
  }

  return (
    <Popover opened={face !== null} position="bottom" withArrow shadow="md" trapFocus={false}>
      <Popover.Target>
        <Tooltip label={label} disabled={face !== null}>
          <ActionIcon
            size={size}
            variant="default"
            onClick={roll}
            data-disabled={empty || undefined}
            aria-disabled={empty || undefined}
            aria-label={label}
          >
            <DiceIcon spinRef={scope} />
          </ActionIcon>
        </Tooltip>
      </Popover.Target>
      {dropdown}
    </Popover>
  )
}
