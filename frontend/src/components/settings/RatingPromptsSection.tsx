import { Stack, Switch, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { useRatingPromptsSettings, useSaveRatingPromptsSettings } from '../../api/hooks'
import { notifications } from '../../lib/toast'
import { SettingsSection } from '../../pages/settings/SettingsSection'

/** Whether Maki asks "how is it so far?" at a few points while you read a series. */
export function RatingPromptsSection() {
  const { t } = useLingui()
  const { data } = useRatingPromptsSettings()
  const save = useSaveRatingPromptsSettings()
  const enabled = data?.enabled ?? true
  const volumes = data?.volumes ?? false
  const update = (next: { enabled?: boolean; volumes?: boolean }) =>
    save.mutate(
      { enabled: next.enabled ?? enabled, volumes: next.volumes ?? volumes },
      { onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }) },
    )

  return (
    <SettingsSection id="rating-prompts" title={<Trans>Rating prompts</Trans>}>
      <Stack gap="sm">
        <div>
          <Switch
            label={t`Ask how a series is going while I read it`}
            checked={enabled}
            disabled={!data}
            onChange={(e) => update({ enabled: e.currentTarget.checked })}
          />
          <Text size="xs" c="var(--ink-3)" mt={4}>
            <Trans>
              About ten chapters in, and when you catch up on a series that's still running. At most once a day,
              and never once you've rated it.
            </Trans>
          </Text>
        </div>
        <Switch
          label={t`Also ask at the end of each volume`}
          checked={enabled && volumes}
          disabled={!data || !enabled}
          onChange={(e) => update({ volumes: e.currentTarget.checked })}
        />
      </Stack>
    </SettingsSection>
  )
}
