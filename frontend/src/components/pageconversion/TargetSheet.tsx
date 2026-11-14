import { useState } from 'react'
import { Button, Group, Modal, SegmentedControl, Skeleton, Slider, Stack, Text } from '@mantine/core'
import { useDebouncedValue, useMediaQuery } from '@mantine/hooks'
import { IconPhotoSearch } from '../../lib/icons'
import { Trans, useLingui } from '@lingui/react/macro'
import {
  PAGE_CONVERSION_STOPS,
  usePreviewPage,
  usePreviewSize,
  useRowSamples,
  type PageConversionFormat,
  type PageConversionSample,
  type PageConversionStop,
  type PageTarget,
  type RowSamplesRequest,
  type SampleKind,
} from '../../api/pageConversion'
import { formatBytes, formatPercent } from '../../format'
import { Callout } from '../ui/Callout'
import { SelectCards } from '../SelectCards'
import { ComparePicker } from './ComparePicker'
import { CompareView } from './CompareView'
import { sampleKey } from './compareChoice'
import {
  COLOUR_STOP_HINTS,
  CONVERSION_FORMATS,
  FORMAT_HINTS,
  FORMAT_LABELS,
  KEPT_FALLBACK,
  KEPT_REASONS,
  STOP_HINTS,
  STOP_LABELS,
  useTargetLabel,
} from './labels'

const SLIDER_DEBOUNCE_MS = 250

/**
 * Tunes one target of one row of the plan: black-and-white or colour pages, of the default or of a
 * rule. The pages shown are of that kind, from the files that row converts, so a target is always
 * judged on what it will be used for. Changes apply to the plan as they are made.
 */
export function TargetSheet({
  opened,
  onClose,
  title,
  kind,
  target,
  fallback,
  pngLossless,
  samplesRequest,
  onChange,
}: {
  opened: boolean
  onClose: () => void
  title: string
  kind: SampleKind
  /** Null when colour pages take the black-and-white target. */
  target: PageTarget | null
  /** The row's black-and-white target, which colour pages take while `target` is null. */
  fallback: PageTarget
  pngLossless: boolean
  /** Built when the sheet opens, so editing the target never fetches new pages. */
  samplesRequest: RowSamplesRequest | null
  onChange: (target: PageTarget | null) => void
}) {
  const narrow = useMediaQuery('(max-width: 48em)')
  return (
    <Modal
      opened={opened}
      onClose={onClose}
      title={title}
      size={1180}
      fullScreen={narrow}
      centered
      classNames={{ title: 'pc-sheet-title' }}
    >
      {opened && (
        <SheetBody
          kind={kind}
          target={target}
          fallback={fallback}
          pngLossless={pngLossless}
          samplesRequest={samplesRequest}
          onChange={onChange}
          onDone={onClose}
        />
      )}
    </Modal>
  )
}

function SheetBody({
  kind,
  target,
  fallback,
  pngLossless,
  samplesRequest,
  onChange,
  onDone,
}: {
  kind: SampleKind
  target: PageTarget | null
  fallback: PageTarget
  pngLossless: boolean
  samplesRequest: RowSamplesRequest | null
  onChange: (target: PageTarget | null) => void
  onDone: () => void
}) {
  const { t } = useLingui()
  const targetLabel = useTargetLabel()
  const samples = useRowSamples(samplesRequest)
  const [custom, setCustom] = useState<PageConversionSample | null>(null)
  const [activeKey, setActiveKey] = useState<string | null>(null)
  const [pickerOpen, setPickerOpen] = useState(false)

  const suggestions = samples.data?.samples ?? []
  const options = custom && !suggestions.some((s) => sampleKey(s) === sampleKey(custom)) ? [...suggestions, custom] : suggestions
  const active = options.find((s) => sampleKey(s) === activeKey) ?? options[0] ?? null

  const same = target === null
  const shown = target ?? fallback
  const [format] = useDebouncedValue(shown.format, SLIDER_DEBOUNCE_MS)
  const [stop] = useDebouncedValue(shown.stop, SLIDER_DEBOUNCE_MS)
  const settled = format === shown.format && stop === shown.stop
  const lossless = shown.stop === 'lossless'

  const original = usePreviewPage(
    active ? { fileId: active.fileId, page: active.page, format, stop, pngLossless, original: true } : null,
  )
  const encoded = usePreviewPage(active ? { fileId: active.fileId, page: active.page, format, stop, pngLossless } : null)

  const setStop = (next: PageConversionStop) => onChange({ format: shown.format, stop: next })
  const formatChoice = same ? 'same' : lossless ? 'webp' : shown.format
  const formatData = [
    ...(kind === 'colour' ? [{ value: 'same', label: t`Same` }] : []),
    ...CONVERSION_FORMATS.map((f) => ({ value: f, label: t(FORMAT_LABELS[f]), disabled: lossless && !same })),
  ]
  const kindName = kind === 'colour' ? t`colour` : t`black-and-white`
  const fallbackLabel = targetLabel(fallback)

  return (
    <div className="pc-sheet">
      <Stack gap="sm" className="pc-sheet-view">
        {samples.isLoading && <Skeleton height={64} radius="md" />}
        {options.length > 0 && (
          <SelectCards
            fillLeft={false}
            cols={{ base: 1, xs: Math.min(options.length, 3) }}
            value={active ? sampleKey(active) : ''}
            onChange={setActiveKey}
            options={options.map((s) => {
              const chapter = s.chapterLabel
              const page = s.page + 1
              return { value: sampleKey(s), title: s.seriesTitle, subtitle: t`${chapter}, page ${page}` }
            })}
          />
        )}
        {!samples.isLoading && options.length === 0 && (
          <Text size="sm" c="var(--ink-3)">
            <Trans>
              Maki found no {kindName} pages in the files this row converts. Pick a page from your library to see the
              target on.
            </Trans>
          </Text>
        )}
        <Group justify="flex-end">
          <Button variant="default" size="xs" leftSection={<IconPhotoSearch size={16} />} onClick={() => setPickerOpen(true)}>
            <Trans>Pick another page</Trans>
          </Button>
        </Group>

        {active && (
          <CompareView
            key={sampleKey(active)}
            originalUrl={original.data?.url}
            encodedUrl={encoded.data?.url}
            encodedLabel={targetLabel({ format, stop })}
            loading={encoded.isFetching || original.isFetching || !settled}
          />
        )}
        {encoded.isError && (
          <Callout tone="danger" role="alert">
            <Trans>Maki could not encode this page. Try another page or another format.</Trans>
          </Callout>
        )}
      </Stack>

      <Stack gap="md" className="pc-sheet-controls">
        <Stack gap={6}>
          <Text size="sm" fw={600}>
            <Trans>Format</Trans>
          </Text>
          <SegmentedControl
            fullWidth
            aria-label={t`Format`}
            value={formatChoice}
            data={formatData}
            onChange={(next) =>
              onChange(
                next === 'same'
                  ? null
                  : { format: next as PageConversionFormat, stop: lossless ? 'high' : shown.stop },
              )
            }
          />
          <Text size="xs" c="var(--ink-3)">
            {same ? (
              <Trans>Colour pages are stored like black-and-white pages in this row: {fallbackLabel}.</Trans>
            ) : lossless ? (
              <Trans>Lossless is always stored as lossless WebP.</Trans>
            ) : (
              t(FORMAT_HINTS[shown.format])
            )}
          </Text>
        </Stack>

        <Stack gap={6}>
          <Group justify="space-between" gap={4}>
            <Text size="sm" fw={600}>
              <Trans>Quality</Trans>
            </Text>
            <Text size="sm" fw={600} c="var(--brand)" aria-live="polite">
              {t(STOP_LABELS[shown.stop])}
            </Text>
          </Group>
          <Slider
            min={0}
            max={PAGE_CONVERSION_STOPS.length - 1}
            step={1}
            label={null}
            disabled={same}
            value={PAGE_CONVERSION_STOPS.indexOf(shown.stop)}
            thumbLabel={t`Quality`}
            onChange={(index) => setStop(PAGE_CONVERSION_STOPS[index])}
          />
          <Group justify="space-between" gap={4}>
            <Text size="xs" c="var(--ink-3)">
              <Trans>Smaller</Trans>
            </Text>
            <Text size="xs" c="var(--ink-3)">
              <Trans>Closer to the original</Trans>
            </Text>
          </Group>
          <Text size="xs" c="var(--ink-3)" aria-live="polite">
            {t((kind === 'colour' ? COLOUR_STOP_HINTS : STOP_HINTS)[shown.stop])}
          </Text>
        </Stack>

        {active && (
          <Sizes
            fileId={active.fileId}
            page={active.page}
            format={format}
            stop={stop}
            pngLossless={pngLossless}
            encodedBytes={encoded.data?.encodedBytes}
            originalBytes={encoded.data?.originalBytes}
            kept={encoded.data?.kept ?? false}
            keptReason={encoded.data?.keptReason ?? null}
          />
        )}

        <Button onClick={onDone} mt="auto">
          <Trans>Done</Trans>
        </Button>
      </Stack>

      <ComparePicker
        opened={pickerOpen}
        onClose={() => setPickerOpen(false)}
        onPick={(sample) => {
          setCustom(sample)
          setActiveKey(sampleKey(sample))
        }}
      />
    </div>
  )
}

/** This page's size in the target, and in each other format at the same quality. */
function Sizes({
  fileId,
  page,
  format,
  stop,
  pngLossless,
  encodedBytes,
  originalBytes,
  kept,
  keptReason,
}: {
  fileId: number
  page: number
  format: PageConversionFormat
  stop: PageConversionStop
  pngLossless: boolean
  encodedBytes: number | undefined
  originalBytes: number | undefined
  kept: boolean
  keptReason: string | null
}) {
  const { t } = useLingui()
  const others = stop === 'lossless' ? [] : CONVERSION_FORMATS.filter((f) => f !== format)
  return (
    <Stack gap={6} aria-live="polite">
      <Text size="sm" fw={600}>
        <Trans>This page</Trans>
      </Text>
      {kept ? (
        <Text size="xs" c="var(--ink-3)">
          {t(KEPT_REASONS[keptReason ?? ''] ?? KEPT_FALLBACK)}
        </Text>
      ) : (
        <Group justify="space-between" gap={4}>
          <Text size="sm">
            <Trans>Converted</Trans>
          </Text>
          <Text size="sm" fw={600}>
            {encodedBytes !== undefined && originalBytes !== undefined ? (
              <ShareOf bytes={encodedBytes} originalBytes={originalBytes} />
            ) : (
              '…'
            )}
          </Text>
        </Group>
      )}
      {originalBytes !== undefined && (
        <Group justify="space-between" gap={4}>
          <Text size="sm" c="var(--ink-3)">
            <Trans>Original</Trans>
          </Text>
          <Text size="sm" c="var(--ink-3)">
            {formatBytes(originalBytes)}
          </Text>
        </Group>
      )}
      {others.map((other) => (
        <OtherFormatSize
          key={other}
          request={{ fileId, page, format: other, stop, pngLossless }}
          label={t(FORMAT_LABELS[other])}
        />
      ))}
    </Stack>
  )
}

function OtherFormatSize({
  request,
  label,
}: {
  request: { fileId: number; page: number; format: PageConversionFormat; stop: PageConversionStop; pngLossless: boolean }
  label: string
}) {
  const size = usePreviewSize(request)
  return (
    <Group justify="space-between" gap={4}>
      <Text size="sm" c="var(--ink-3)">
        <Trans>As {label}</Trans>
      </Text>
      <Text size="sm" c="var(--ink-3)">
        {size.isError ? '–' : size.data === undefined ? '…' : size.data === null ? <Trans>Kept</Trans> : formatBytes(size.data)}
      </Text>
    </Group>
  )
}

function ShareOf({ bytes, originalBytes }: { bytes: number; originalBytes: number }) {
  const { t } = useLingui()
  const size = formatBytes(bytes)
  const percent = formatPercent(originalBytes > 0 ? bytes / originalBytes : 1)
  return <>{t`${size}, ${percent}`}</>
}
