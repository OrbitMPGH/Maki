import { useState } from 'react'
import { Alert, Button, Group, Skeleton, Slider, Stack, Text } from '@mantine/core'
import { useDebouncedValue } from '@mantine/hooks'
import { IconPhotoSearch, IconZoomIn } from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import {
  PAGE_CONVERSION_STOPS,
  usePageConversionSamples,
  usePreviewPage,
  type PageConversionSettings,
  type PageConversionStop,
} from '../../api/pageConversion'
import { formatBytes, formatPercent } from '../../format'
import { SelectCards } from '../SelectCards'
import { CompareView } from './CompareView'
import { ComparePicker } from './ComparePicker'
import { sampleKey, type CompareChoice } from './compareChoice'
import { KEPT_FALLBACK, KEPT_REASONS, SAMPLE_REASONS, STOP_HINTS, STOP_LABELS } from './labels'

const SLIDER_DEBOUNCE_MS = 250

/** Step 2: one of the admin's own pages, before and after, with the quality stop to move. */
export function CompareStep({
  value,
  onChange,
  choice,
  onChoiceChange,
}: {
  value: PageConversionSettings
  onChange: (changes: Partial<PageConversionSettings>) => void
  choice: CompareChoice
  onChoiceChange: (choice: CompareChoice) => void
}) {
  const { t } = useLingui()
  const { data, isLoading } = usePageConversionSamples()
  const [pickerOpen, setPickerOpen] = useState(false)
  const [loupe, setLoupe] = useState(false)

  const suggestions = data?.samples ?? []
  const options =
    choice.custom && !suggestions.some((s) => sampleKey(s) === sampleKey(choice.custom!))
      ? [...suggestions, choice.custom]
      : suggestions
  const active = options.find((s) => sampleKey(s) === choice.active) ?? options[0] ?? null

  const { format, stop, pngLossless } = value
  const [formatNow] = useDebouncedValue(format, SLIDER_DEBOUNCE_MS)
  const [stopNow] = useDebouncedValue(stop, SLIDER_DEBOUNCE_MS)
  const [pngNow] = useDebouncedValue(pngLossless, SLIDER_DEBOUNCE_MS)
  const settled = formatNow === format && stopNow === stop && pngNow === pngLossless

  const original = usePreviewPage(
    active && format ? { fileId: active.fileId, page: active.page, format, stop, pngLossless, original: true } : null,
  )
  const encoded = usePreviewPage(
    active && formatNow
      ? { fileId: active.fileId, page: active.page, format: formatNow, stop: stopNow, pngLossless: pngNow }
      : null,
  )

  if (!format) {
    return (
      <Text size="sm" c="var(--ink-3)">
        <Trans>Conversion is off, so there is nothing to compare. Go back to pick a format.</Trans>
      </Text>
    )
  }

  const stopIndex = PAGE_CONVERSION_STOPS.indexOf(stop)
  const marks = PAGE_CONVERSION_STOPS.map((s, index) => ({ value: index, label: t(STOP_LABELS[s]) }))
  const result = encoded.data

  return (
    <Stack gap="lg">
      <Stack gap="xs">
        {isLoading && <Skeleton height={64} radius="md" />}
        {options.length > 0 && (
          <SelectCards
            fillLeft={false}
            value={active ? sampleKey(active) : ''}
            onChange={(next) => onChoiceChange({ ...choice, active: next })}
            options={options.map((s) => {
              const chapter = s.chapterLabel
              const reason = t(SAMPLE_REASONS[s.reason])
              return { value: sampleKey(s), title: s.seriesTitle, subtitle: t`${chapter}, ${reason}` }
            })}
          />
        )}
        {!isLoading && options.length === 0 && (
          <Text size="sm" c="var(--ink-3)">
            <Trans>Maki found no suitable pages to suggest. Pick one from your library.</Trans>
          </Text>
        )}
        <Group justify="space-between" wrap="wrap" gap="sm">
          <Text size="sm" c="var(--ink-3)">
            {active && <PageLine page={active.page + 1} pageCount={active.pageCount} />}
          </Text>
          <Group gap="xs">
            <Button
              variant={loupe ? 'filled' : 'default'}
              size="xs"
              aria-pressed={loupe}
              leftSection={<IconZoomIn size={14} />}
              onClick={() => setLoupe((on) => !on)}
            >
              <Trans>Loupe, 2x</Trans>
            </Button>
            <Button
              variant="default"
              size="xs"
              leftSection={<IconPhotoSearch size={14} />}
              onClick={() => setPickerOpen(true)}
            >
              <Trans>Pick another page</Trans>
            </Button>
          </Group>
        </Group>
      </Stack>

      {active && (
        <CompareView
          key={sampleKey(active)}
          originalUrl={original.data?.url}
          encodedUrl={encoded.data?.url}
          loading={encoded.isFetching || original.isFetching || !settled}
          loupe={loupe}
        />
      )}
      {encoded.isError && (
        <Alert color="red" variant="light">
          <Trans>Maki could not encode this page. Try another page or another format.</Trans>
        </Alert>
      )}

      <Stack gap="xs">
        <Slider
          min={0}
          max={PAGE_CONVERSION_STOPS.length - 1}
          step={1}
          marks={marks}
          label={null}
          value={stopIndex}
          thumbLabel={t`Quality`}
          onChange={(index) => onChange({ stop: PAGE_CONVERSION_STOPS[index] as PageConversionStop })}
          mb="lg"
        />
        <Text size="sm" aria-live="polite">
          {t(STOP_HINTS[stop])}
        </Text>
        <Text size="sm" c="var(--ink-3)" aria-live="polite" mih="1.4em">
          {result && <SizeLine
              encodedBytes={result.encodedBytes}
              originalBytes={result.originalBytes}
              kept={result.kept}
              keptReason={result.keptReason}
            />}
        </Text>
      </Stack>

      <ComparePicker
        opened={pickerOpen}
        onClose={() => setPickerOpen(false)}
        onPick={(sample) => onChoiceChange({ custom: sample, active: sampleKey(sample) })}
      />
    </Stack>
  )
}

function SizeLine({
  encodedBytes,
  originalBytes,
  kept,
  keptReason,
}: {
  encodedBytes: number
  originalBytes: number
  kept: boolean
  keptReason: string | null
}) {
  const { t } = useLingui()
  if (kept) return <>{t(KEPT_REASONS[keptReason ?? ''] ?? KEPT_FALLBACK)}</>
  const size = formatBytes(encodedBytes)
  const percent = formatPercent(originalBytes > 0 ? encodedBytes / originalBytes : 1)
  return <>{t`${size}, ${percent} of the original`}</>
}

function PageLine({ page, pageCount }: { page: number; pageCount: number }) {
  return (
    <Trans>
      Page {page} of {pageCount}
    </Trans>
  )
}
