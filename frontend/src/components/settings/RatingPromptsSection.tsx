import { Switch, Text } from '@mantine/core'
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

  return (
    <SettingsSection id="rating-prompts" title={<Trans>Rating prompts</Trans>}>
      <Switch
        label={t`Ask how a series is going while I read it`}
        checked={data?.enabled ?? true}
        disabled={!data}
        onChange={(e) =>
          save.mutate(
            { enabled: e.currentTarget.checked },
            { onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }) },
          )
        }
      />
      <Text size="xs" c="var(--ink-3)" mt={4}>
        <Trans>
          About ten chapters in, and when you catch up on a series that's still running. At most once a day, and
          never once you've rated it. A "so far" score isn't sent to your trackers.
        </Trans>
      </Text>
    </SettingsSection>
  )
}
