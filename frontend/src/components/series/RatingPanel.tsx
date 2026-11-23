import { useCallback, useEffect, useRef, useState } from 'react'
import { ActionIcon, Button, Divider, Group, Text, TextInput, UnstyledButton } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import {
  ENDING_KEY,
  useCategoryLabel,
  useRatingFormat,
  useRatingSettings,
  useRatingText,
  useSetRatingBreakdown,
} from '../../api/ratings'
import { breakdownScore, parseRating, roundRating, RATING_STEP } from '../../lib/rating'
import { RATING_WORDS, ratingWordFor } from '../../lib/ratingWords'
import { IconCheck, IconChevronDown, IconListDetails, IconMinus, IconPencil, IconPlus, IconX } from '../../lib/icons'
import { DrillBack, DrillRow, useDrillPages } from '../ui/DrillPanel'
import { DrillPanes } from '../ui/DrillPanes'
import { RatingCategoriesEditor } from '../settings/RatingCategoriesModal'
import { RatingWordGrid } from './RatingWordGrid'

const BREAKDOWN_SAVE_DELAY_MS = 400

type Page = 'main' | 'breakdown' | 'categories'
const PARENTS: Record<Page, Page> = { main: 'main', breakdown: 'main', categories: 'breakdown' }

/** Saves the latest category scores a moment after the last change, and on the way out. */
function useBreakdownDraft(seriesId: number, initial: Record<string, number> | null | undefined) {
  const save = useSetRatingBreakdown()
  const [scores, setScores] = useState<Record<string, number>>(initial ?? {})
  const pending = useRef<Record<string, number> | null>(null)
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const { mutateAsync } = save

  /** Saves what is waiting now; resolves once it is stored (or failed), so a rating can follow it. */
  const flush = useCallback(async () => {
    if (timer.current) clearTimeout(timer.current)
    timer.current = null
    const scores = pending.current
    pending.current = null
    if (scores) await mutateAsync({ seriesId, scores }).catch(() => undefined)
  }, [mutateAsync, seriesId])

  useEffect(
    () => () => {
      void flush()
    },
    [flush],
  )

  const update = (next: Record<string, number>) => {
    setScores(next)
    pending.current = next
    if (timer.current) clearTimeout(timer.current)
    timer.current = setTimeout(() => void flush(), BREAKDOWN_SAVE_DELAY_MS)
  }
  return { scores, update, flush }
}

/**
 * The overall score as the panel holds it. Changes stay a draft until the panel closes (it unmounts)
 * or "Use" commits one, so stepping through values saves once, not each time.
 */
function useRatingDraft(value: number | null, onChange: (rating: number) => void, immediate: boolean) {
  const [draft, setDraft] = useState<number | null>(null)
  const latest = useRef<number | null>(null)
  const saved = useRef(value)
  const save = useRef(onChange)
  useEffect(() => {
    save.current = onChange
  }, [onChange])
  useEffect(
    () => () => {
      if (latest.current != null && latest.current !== saved.current) save.current(latest.current)
    },
    [],
  )

  const change = (rating: number) => {
    latest.current = rating
    setDraft(rating)
    if (immediate) {
      saved.current = rating
      onChange(rating)
    }
  }
  const commit = (rating: number) => {
    latest.current = rating
    setDraft(rating)
    saved.current = rating
    if (rating !== value) onChange(rating)
  }
  const forget = () => {
    latest.current = null
    saved.current = null
    setDraft(null)
  }
  return { draft, change, commit, forget }
}

/** Seven small cells, filled up to a score's word: the category rows' read-out. */
function MiniScale({ value }: { value: number }) {
  const level = RATING_WORDS.length - 1 - RATING_WORDS.indexOf(ratingWordFor(value))
  return (
    <span className="rating-mini" aria-hidden>
      {RATING_WORDS.map((w, i) => (
        <span key={w.score} data-filled={i <= level || undefined} />
      ))}
    </span>
  )
}

/**
 * The rating as a drill panel. The first page is the verdict (the word large, the number beside it),
 * the seven words under it and a fine-tune step; a "Break it down" row opens the categories, scored
 * on that one page, where each category opens on the same words and picking one moves to the next.
 * The overall score is saved when the panel closes; the categories suggest one through "Use".
 * Controlled: `onChange` is the host's idea of setting the rating, so the archive dialog can hold it.
 * Without `breakdown` (a list that has no category scores loaded) the categories are left out.
 */
export function RatingPanel({
  seriesId,
  value,
  soFar = false,
  finished,
  breakdown,
  onChange,
  onClear,
  canEditCategories = false,
  immediate = false,
}: {
  seriesId: number
  value: number | null
  soFar?: boolean
  finished: boolean
  /**
   * The stored scores, or undefined to leave the breakdown out. Saving replaces them all, so pass
   * null or the map only once the series has loaded.
   */
  breakdown: Record<string, number> | null | undefined
  onChange: (rating: number) => void
  /** Clears the rating at once. Without it the panel offers no Clear. */
  onClear?: () => void
  canEditCategories?: boolean
  /**
   * Hands every change to `onChange` at once instead of on close, for a host that only holds the
   * score (the archive dialog), so its own confirm button never reads a stale one.
   */
  immediate?: boolean
}) {
  const { t, i18n } = useLingui()
  const { settings } = useRatingSettings()
  const { precision, format } = useRatingFormat()
  const label = useCategoryLabel()
  const pages = useDrillPages<Page>('main', PARENTS)
  const { scores, update, flush } = useBreakdownDraft(seriesId, breakdown)
  const { draft, change, commit, forget } = useRatingDraft(value, onChange, immediate)
  const [cleared, setCleared] = useState(false)
  const [typing, setTyping] = useState<string | null>(null)
  const [openKey, setOpenKey] = useState<string | null | undefined>(undefined)
  const decimal = precision === 'decimal'
  const shown = cleared ? null : (draft ?? value)
  const wordOf = (score: number) => i18n._(ratingWordFor(score).label)

  const pick = (score: number) => {
    setCleared(false)
    change(score)
  }
  const nudge = (direction: 1 | -1) => {
    const base = shown == null ? 5 : decimal ? shown : Math.round(shown)
    pick(roundRating(base + direction * (decimal ? RATING_STEP : 1)))
  }
  const finishTyping = () => {
    const parsed = typing == null ? null : parseRating(typing)
    setTyping(null)
    if (parsed != null) pick(decimal ? parsed : Math.round(parsed))
  }

  const categories = settings.categories.filter((c) => c.enabled)
  const locked = (key: string) => key === ENDING_KEY && !finished
  const scoredCount = categories.filter((c) => scores[c.key] != null).length
  const firstUnscored = (from: Record<string, number>) =>
    categories.find((c) => from[c.key] == null && !locked(c.key))?.key ?? null
  const expanded = openKey === undefined ? firstUnscored(scores) : openKey
  const suggestion = breakdownScore(scores, categories)
  const offered = suggestion == null ? null : decimal ? suggestion : Math.round(suggestion)
  const compared = shown == null ? null : decimal ? shown : Math.round(shown)
  const matches = offered != null && compared != null && Math.abs(offered - compared) < RATING_STEP / 2
  const ratingText = useRatingText()
  const suggested = suggestion != null ? ratingText(suggestion, true) : ''
  const of = categories.length

  const scoreCategory = (key: string, score: number) => {
    const next = { ...scores, [key]: score }
    update(next)
    setOpenKey(firstUnscored(next))
  }

  const main = (
    <>
      <div className="rating-verdict">
        <span className="rating-verdict-word" data-empty={shown == null || undefined} data-so-far={(soFar && shown != null) || undefined}>
          {shown != null ? wordOf(shown) : <Trans>Not rated</Trans>}
        </span>
        {shown != null && (
          <span className="rating-verdict-score tnum">
            {format(shown)}
            <span className="rating-verdict-scale">{soFar ? <Trans>/10 so far</Trans> : '/10'}</span>
          </span>
        )}
      </div>
      <RatingWordGrid
        label={t`Your rating`}
        value={shown}
        onPick={(score) => {
          if (shown == null || ratingWordFor(shown) !== ratingWordFor(score)) pick(score)
        }}
      />
      {shown != null && (
        <Group justify="space-between" wrap="nowrap" gap="sm" mt="sm" px={4}>
          <Text size="xs" fw={600} c="var(--ink-3)">
            <Trans>Fine-tune</Trans>
          </Text>
          <Group gap={6} wrap="nowrap">
            <ActionIcon variant="default" aria-label={decimal ? t`Lower by 0.1` : t`Lower by 1`} onClick={() => nudge(-1)} disabled={shown <= 1}>
              <IconMinus size={16} />
            </ActionIcon>
            {typing != null ? (
              <TextInput
                className="rating-panel-input"
                size="xs"
                value={typing}
                autoFocus
                inputMode="decimal"
                aria-label={t`Your rating`}
                onChange={(e) => setTyping(e.currentTarget.value)}
                onBlur={finishTyping}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') finishTyping()
                  if (e.key === 'Escape') setTyping(null)
                }}
              />
            ) : (
              <UnstyledButton className="rating-panel-value tnum" aria-label={t`Type a rating`} onClick={() => setTyping(format(shown))}>
                {format(shown)}
              </UnstyledButton>
            )}
            <ActionIcon variant="default" aria-label={decimal ? t`Raise by 0.1` : t`Raise by 1`} onClick={() => nudge(1)} disabled={shown >= 10}>
              <IconPlus size={16} />
            </ActionIcon>
          </Group>
        </Group>
      )}
      {(breakdown !== undefined || (onClear && shown != null)) && <Divider my="xs" />}
      {breakdown !== undefined && (
        <DrillRow
          ref={pages.rowRef('breakdown')}
          icon={IconListDetails}
          label={<Trans>Break it down</Trans>}
          value={
            suggestion != null ? (
              ratingText(suggestion, true)
            ) : scoredCount > 0 ? (
              <Trans>
                {scoredCount} of {of} scored
              </Trans>
            ) : (
              <Trans>Not started</Trans>
            )
          }
          active={suggestion != null}
          onClick={() => {
            setOpenKey(undefined)
            pages.open('breakdown')
          }}
        />
      )}
      {onClear && shown != null && (
        <DrillRow
          icon={IconX}
          label={<Trans>Clear rating</Trans>}
          chevron={false}
          onClick={() => {
            forget()
            setCleared(true)
            onClear()
          }}
        />
      )}
    </>
  )

  const breakdownPage = (
    <>
      <DrillBack ref={pages.backRef} title={<Trans>Break it down</Trans>} onBack={pages.back} />
      <div className="rating-cats">
        {categories.map((c) => {
          const score = scores[c.key]
          const name = label(c)
          const isLocked = locked(c.key)
          const isOpen = expanded === c.key && !isLocked
          return (
            <div key={c.key} className="rating-cat" data-open={isOpen || undefined} data-locked={isLocked || undefined}>
              <UnstyledButton
                className="rating-cat-head"
                aria-expanded={isOpen}
                disabled={isLocked}
                onClick={() => setOpenKey(isOpen ? null : c.key)}
              >
                <span className="rating-cat-name">{name}</span>
                {isLocked ? (
                  <span className="rating-cat-word" data-none>
                    <Trans>After the last chapter</Trans>
                  </span>
                ) : (
                  !isOpen &&
                  (score != null ? (
                    <>
                      <MiniScale value={score} />
                      <span className="rating-cat-word">{wordOf(score)}</span>
                    </>
                  ) : (
                    <span className="rating-cat-word" data-none>
                      <Trans>Not scored</Trans>
                    </span>
                  ))
                )}
                {!isLocked && <IconChevronDown size={16} className="rating-cat-chevron" aria-hidden />}
              </UnstyledButton>
              {isOpen && (
                <div className="rating-cat-body">
                  <RatingWordGrid label={name} value={score ?? null} onPick={(s) => scoreCategory(c.key, s)} />
                  {score != null && (
                    <Button
                      size="compact-xs"
                      variant="subtle"
                      color="var(--neutral)"
                      mt={6}
                      leftSection={<IconX size={12} />}
                      onClick={() => {
                        const next = { ...scores }
                        delete next[c.key]
                        update(next)
                      }}
                    >
                      <Trans>Clear {name}</Trans>
                    </Button>
                  )}
                </div>
              )}
            </div>
          )
        })}
      </div>
      <Divider my="xs" />
      <Group justify="space-between" wrap="nowrap" gap="sm" mih={30} px={4}>
        {suggestion == null ? (
          <Text size="sm" c="var(--ink-4)">
            {scoredCount === 0 ? <Trans>Score two to get a suggestion</Trans> : <Trans>One more to get a suggestion</Trans>}
          </Text>
        ) : (
          <Text size="sm" c="var(--ink-3)">
            <Trans>
              Suggests <span className="rating-panel-sum tnum">{suggested}</span>
            </Trans>
          </Text>
        )}
        {offered != null &&
          (matches ? (
            <Group gap={6} wrap="nowrap" c="var(--ink-3)">
              <IconCheck size={16} aria-hidden />
              <Text size="sm">
                <Trans>Matches your rating</Trans>
              </Text>
            </Group>
          ) : (
            <Button
              size="compact-sm"
              onClick={async () => {
                setCleared(false)
                // The rating's history entry records the breakdown, so the scores are saved first.
                await flush()
                commit(offered)
              }}
            >
              <Trans>Use</Trans>
            </Button>
          ))}
      </Group>
      {canEditCategories && (
        <DrillRow
          ref={pages.rowRef('categories')}
          icon={IconPencil}
          label={<Trans>Edit categories</Trans>}
          value={<Trans>{of} on</Trans>}
          onClick={() => pages.open('categories')}
        />
      )}
    </>
  )

  const categoriesPage = (
    <>
      <DrillBack ref={pages.backRef} title={<Trans>Edit categories</Trans>} onBack={pages.back} />
      <RatingCategoriesEditor />
    </>
  )

  return (
    <div className="rating-panel">
      <DrillPanes page={pages.page} depth={pages.depth} bleed={4}>
        {pages.page === 'main' && main}
        {pages.page === 'breakdown' && breakdownPage}
        {pages.page === 'categories' && categoriesPage}
      </DrillPanes>
    </div>
  )
}
