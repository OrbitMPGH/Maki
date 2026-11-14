import { useState } from 'react'
import { Checkbox, NumberInput, SimpleGrid, Stack } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import {
  DEFAULT_SPLIT_HEIGHT,
  MAX_SPLIT_HEIGHT,
  MIN_SPLIT_HEIGHT,
  type PageConversionSettings,
} from '../../api/pageConversion'
import { SplitPreview } from './SplitPreview'

/** The plan-wide switches: PNG pages, and splitting tall strips. */
export function PlanOptions({
  value,
  onChange,
}: {
  value: PageConversionSettings
  onChange: (changes: Partial<PageConversionSettings>) => void
}) {
  const { t } = useLingui()
  const [lastHeight, setLastHeight] = useState(value.splitHeight ?? DEFAULT_SPLIT_HEIGHT)
  const splitting = value.splitHeight != null

  return (
    <Stack gap="md">
      <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
        <Checkbox
          label={t`Keep PNG pages lossless`}
          description={t`PNG pages are stored as lossless WebP at every quality, so no pixel changes. This saves little. Leave it off to let PNG pages follow their row's quality.`}
          checked={value.pngLossless}
          onChange={(event) => onChange({ pngLossless: event.currentTarget.checked })}
        />
        <Checkbox
          label={t`Split very tall pages`}
          description={t`Webtoon strips taller than a set height are cut into several pages, in the gaps between panels where there are any. E-readers and KOReader handle these far better. Reading positions, bookmarks and notes move with their page.`}
          checked={splitting}
          onChange={(event) => onChange({ splitHeight: event.currentTarget.checked ? lastHeight : null })}
        />
      </SimpleGrid>
      {splitting && (
        <Stack gap="sm">
          <NumberInput
            label={t`Split pages taller than`}
            description={t`Only strips at least two and a half times as tall as they are wide are split.`}
            suffix=" px"
            min={MIN_SPLIT_HEIGHT}
            max={MAX_SPLIT_HEIGHT}
            step={500}
            clampBehavior="blur"
            allowDecimal={false}
            allowNegative={false}
            thousandSeparator={false}
            maw={220}
            value={value.splitHeight ?? lastHeight}
            onChange={(next) => {
              if (typeof next !== 'number' || next < MIN_SPLIT_HEIGHT || next > MAX_SPLIT_HEIGHT) return
              setLastHeight(next)
              onChange({ splitHeight: next })
            }}
          />
          <SplitPreview height={value.splitHeight ?? lastHeight} />
        </Stack>
      )}
    </Stack>
  )
}
