import { useState } from 'react'
import { SegmentedControl, Stack, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { useCategoryLabel, useRatingSettings, useSaveRatingSettings } from '../../api/ratings'
import type { RatingPrecision } from '../../lib/rating'
import { notifications } from '../../lib/toast'
import { SettingsSection } from '../../pages/settings/SettingsSection'
import { RatingCategoriesModal } from './RatingCategoriesModal'
import { SettingsSummaryRow } from './SettingsSummaryRow'
import { IconListCheck } from '../../lib/icons'
import { formatList } from '../../format'

/** How precise ratings are, and which categories the rating breakdown asks about. */
export function RatingsSection() {
  const { t } = useLingui()
  const { settings, data } = useRatingSettings()
  const save = useSaveRatingSettings()
  const label = useCategoryLabel()
  const [editing, setEditing] = useState(false)
  const enabled = settings.categories.filter((c) => c.enabled).map(label)

  return (
    <SettingsSection id="ratings" title={<Trans>Ratings</Trans>}>
      <Stack gap="md">
        <div>
          <Text size="sm" fw={500} mb={6}>
            <Trans>Rating precision</Trans>
          </Text>
          <SegmentedControl
            disabled={!data}
            aria-label={t`Rating precision`}
            data={[
              { value: 'whole', label: t`Whole numbers` },
              { value: 'decimal', label: t`One decimal` },
            ]}
            value={settings.precision}
            onChange={(v) =>
              save.mutate(
                { ...settings, precision: v as RatingPrecision },
                { onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }) },
              )
            }
          />
          <Text size="xs" c="var(--ink-3)" mt={6}>
            <Trans>Trackers that only take whole numbers get the rounded score.</Trans>
          </Text>
        </div>
        <SettingsSummaryRow
          label={<Trans>Breakdown categories</Trans>}
          summary={<Text size="sm" c="var(--ink-3)" truncate>{formatList(enabled)}</Text>}
          icon={IconListCheck}
          action={<Trans>Choose categories</Trans>}
          disabled={!data}
          onClick={() => setEditing(true)}
        />
      </Stack>
      <RatingCategoriesModal opened={editing} onClose={() => setEditing(false)} />
    </SettingsSection>
  )
}
