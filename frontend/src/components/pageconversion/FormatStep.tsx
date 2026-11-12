import { useState } from 'react'
import { Checkbox, Divider, Input, NumberInput, Radio, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import {
  DEFAULT_SPLIT_HEIGHT,
  MAX_SPLIT_HEIGHT,
  MIN_SPLIT_HEIGHT,
  type PageConversionFormat,
  type PageConversionSettings,
} from '../../api/pageConversion'
import { SplitPreview } from './SplitPreview'

type Choice = PageConversionFormat | 'off'

/** Step 1: which format new and existing pages are stored in, or none. */
export function FormatStep({
  value,
  onChange,
}: {
  value: PageConversionSettings
  onChange: (changes: Partial<PageConversionSettings>) => void
}) {
  const { t } = useLingui()
  const choice: Choice = value.format ?? 'off'
  const [lastHeight, setLastHeight] = useState(value.splitHeight ?? DEFAULT_SPLIT_HEIGHT)
  const splitting = value.splitHeight != null

  return (
    <Stack gap="lg">
      <Radio.Group
        label={t`Format`}
        description={t`The next step shows your own pages in the format you pick here.`}
        value={choice}
        onChange={(next) => onChange({ format: next === 'off' ? null : (next as PageConversionFormat) })}
      >
        <Stack gap="sm" mt="sm">
          <Radio
            value="webp"
            label={t`WebP`}
            description={t`Opens in every reader Maki works with. Recommended.`}
          />
          <Radio
            value="avif"
            label={t`AVIF`}
            description={t`Smaller than WebP at the same look. KOReader and some e-readers can't open AVIF pages.`}
          />
          <Radio
            value="off"
            label={t`Off`}
            description={t`Pages stay as they are downloaded. Files already converted stay converted.`}
          />
        </Stack>
      </Radio.Group>

      {value.format && (
        <>
          <Divider />
          <Input.Wrapper label={t`PNG pages`} labelElement="div">
            <Checkbox
              mt="sm"
              label={t`Keep PNG pages lossless`}
              description={t`PNG pages are stored as lossless WebP at every stop, so no pixel changes. This saves little. Leave it off to let PNG pages follow the quality you choose.`}
              checked={value.pngLossless}
              onChange={(event) => onChange({ pngLossless: event.currentTarget.checked })}
            />
          </Input.Wrapper>
          <Divider />
          <Input.Wrapper label={t`Tall pages`} labelElement="div">
            <Stack gap="sm" mt="sm">
              <Checkbox
                label={t`Split very tall pages`}
                description={t`Webtoon strips taller than the height below are cut into several pages, in the gaps between panels where there are any. E-readers and KOReader handle these far better. Reading positions, bookmarks and notes move with their page.`}
                checked={splitting}
                onChange={(event) => onChange({ splitHeight: event.currentTarget.checked ? lastHeight : null })}
              />
              {splitting && (
                <>
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
                </>
              )}
            </Stack>
          </Input.Wrapper>
        </>
      )}

      {!value.format && (
        <Text size="sm" c="var(--ink-3)">
          <Trans>With conversion off, there is nothing to compare. Continue to save that choice.</Trans>
        </Text>
      )}
    </Stack>
  )
}
