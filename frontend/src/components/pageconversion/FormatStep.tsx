import { Checkbox, Radio, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import type { PageConversionFormat, PageConversionSettings } from '../../api/pageConversion'

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
        <Checkbox
          label={t`Keep PNG pages lossless`}
          description={t`PNG pages are stored as lossless WebP at every stop, so no pixel changes. This saves little. Leave it off to let PNG pages follow the quality you choose.`}
          checked={value.pngLossless}
          onChange={(event) => onChange({ pngLossless: event.currentTarget.checked })}
        />
      )}

      {!value.format && (
        <Text size="sm" c="var(--ink-3)">
          <Trans>With conversion off, there is nothing to compare. Continue to save that choice.</Trans>
        </Text>
      )}
    </Stack>
  )
}
