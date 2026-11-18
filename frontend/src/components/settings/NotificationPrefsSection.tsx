import { useState } from 'react'
import { Button, Checkbox, Divider, Group, Modal, SegmentedControl, Stack, Switch, Text } from '@mantine/core'
import { SettingsGroupTitle } from './SettingsGroupTitle'
import { IconBell, IconDeviceMobile, IconListCheck } from '../../lib/icons'
import { SettingsSection } from '../../pages/settings/SettingsSection'
import { newControlMark, useNewSettingSet } from '../../pages/settings/newSettings'
import { NewSettingPill } from './NewSettingPill'
import { ModalFooter, ModalHeading } from '../ui/ModalShell'
import {
  INBOX_ADMIN_ONLY,
  visibleInboxCategories,
  INBOX_TYPE_DESCRIPTIONS,
  INBOX_TYPE_LABELS,
  useInboxPrefs,
  useSaveInboxPrefs,
  type InboxEventType,
} from '../../api/inbox'
import { usePushPrefs, useSavePushPrefs } from '../../api/push'
import { useSeriesDefaultOptions } from '../ui/seriesNotifications'
import { useAuth } from '../../auth/AuthProvider'
import { useLabel } from '../../i18n-context'
import { formatList } from '../../format'
import { twoColumns } from '../../lib/columns'
import { Plural, Trans, useLingui } from '@lingui/react/macro'

/**
 * Per-event choices for the in-app notification inbox and for push. The card says what is on; the
 * table, a bell column and a push column for each event, opens in a modal in two columns. Turning on
 * push for this device is the Push card's job.
 * <p>
 * Not to be confused with the Discord & webhooks card on the Integrations tab, which manages the
 * instance-wide Discord and webhook connections. Those are admin-only and unaffected by anything
 * here. The two systems are deliberately separate so a chat channel isn't flooded with one
 * person's achievements.
 */
/** Event rows with a Settings registry entry of their own, for the New tab and deep links. */
const TYPE_ENTRIES: Partial<Record<InboxEventType, string>> = {
  newRelatedWork: 'related-work-notifications',
}

export function NotificationPrefsSection() {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { can, singleUser } = useAuth()
  const isAdmin = can('Admin')
  const seriesDefaultOptions = useSeriesDefaultOptions()

  const { data: prefs } = useInboxPrefs()
  const save = useSaveInboxPrefs()
  const { data: pushPrefs } = usePushPrefs()
  const savePush = useSavePushPrefs()
  const newIds = useNewSettingSet()
  const [open, setOpen] = useState(false)

  if (!prefs) {
    return null
  }

  const categories = visibleInboxCategories(isAdmin, singleUser).map((category) => ({
    ...category,
    // Belt and braces: the category flag already hides the System block from a reader,
    // but a type could be marked admin-only inside a mixed category later.
    types: category.types.filter((type) => isAdmin || !INBOX_ADMIN_ONLY.includes(type)),
  }))
  const visible = categories.flatMap((c) => c.types)
  const bellOnFor = (type: InboxEventType) => prefs.types[type] ?? true

  function setType(type: InboxEventType, enabled: boolean) {
    save.mutate({ types: { [type]: enabled } })
  }

  const pushed = savePush.isPending && savePush.variables ? savePush.variables : (pushPrefs?.types ?? [])
  function setPush(type: InboxEventType, on: boolean) {
    savePush.mutate(on ? [...pushed.filter((s) => s !== type), type] : pushed.filter((s) => s !== type))
  }

  const bellCount = visible.filter(bellOnFor).length
  const pushedTypes = pushPrefs ? visible.filter((type) => bellOnFor(type) && pushed.includes(type)) : []
  const total = visible.length

  // Two columns of whole categories, so the taller one is as short as it can be.
  const columns = twoColumns(categories, (c) => c.types.length + 1)

  const matrix = (cats: typeof categories) => (
    <table className="notify-matrix">
      <thead>
        <tr>
          <th scope="col">
            <Trans>Event</Trans>
          </th>
          <th scope="col" className="notify-matrix-check">
            <span className="notify-matrix-head">
              <IconBell size={16} aria-hidden />
              <Trans>Bell</Trans>
            </span>
          </th>
          <th scope="col" className="notify-matrix-check">
            <span className="notify-matrix-head">
              <IconDeviceMobile size={16} aria-hidden />
              <Trans>Push</Trans>
            </span>
          </th>
        </tr>
      </thead>
      {cats.map((category) => (
        <tbody key={category.id}>
          <tr className="notify-matrix-category">
            <th scope="rowgroup" colSpan={3}>
              {renderLabel(category.label)}
            </th>
          </tr>
          {category.types.map((type) => {
            const label = renderLabel(INBOX_TYPE_LABELS[type])
            const description = INBOX_TYPE_DESCRIPTIONS[type]
            const bellOn = bellOnFor(type)
            const entryId = TYPE_ENTRIES[type]
            return (
              <tr key={type}>
                <th scope="row">
                  <Text size="sm">
                    {label}
                    {entryId && <NewSettingPill id={entryId} />}
                  </Text>
                  {description && (
                    <Text size="xs" c="var(--ink-3)">
                      {renderLabel(description)}
                    </Text>
                  )}
                </th>
                <td className="notify-matrix-check">
                  <label className="notify-matrix-hit">
                    <Checkbox
                      aria-label={t`${label} in the bell`}
                      checked={bellOn}
                      onChange={(e) => setType(type, e.currentTarget.checked)}
                    />
                  </label>
                </td>
                <td className="notify-matrix-check">
                  <label className="notify-matrix-hit" data-disabled={!pushPrefs || !bellOn || undefined}>
                    <Checkbox
                      aria-label={t`${label} as a push notification`}
                      checked={bellOn && pushed.includes(type)}
                      disabled={!pushPrefs || !bellOn}
                      onChange={(e) => setPush(type, e.currentTarget.checked)}
                    />
                  </label>
                </td>
              </tr>
            )
          })}
        </tbody>
      ))}
    </table>
  )

  return (
    <SettingsSection id="notification-prefs" title={<Trans>Notifications</Trans>}>
      <Switch
        label={t`Show a popup when a notification arrives`}
        checked={prefs.toasts}
        onChange={(e) => save.mutate({ toasts: e.currentTarget.checked })}
      />

      <Divider my="md" />

      <SettingsGroupTitle>
        <Trans>Which series notify me</Trans>
      </SettingsGroupTitle>
      <Text size="xs" c="var(--ink-3)" mb="xs">
        <Trans>Any series can be set differently from its own page.</Trans>
      </Text>
      <SegmentedControl
        aria-label={t`Which series notify me`}
        fullWidth
        value={seriesDefaultOptions.some((o) => o.value === prefs.seriesDefault) ? prefs.seriesDefault : 'All'}
        onChange={(seriesDefault) => save.mutate({ seriesDefault })}
        data={seriesDefaultOptions}
      />

      <Divider my="md" />

      <Group
        justify="space-between"
        wrap="wrap"
        gap="md"
        id="setting-related-work-notifications"
        {...newControlMark(newIds, ...Object.values(TYPE_ENTRIES))}
      >
        <Stack gap={6} className="settings-summary">
          <Group gap="sm" wrap="nowrap">
            <IconBell size={16} aria-hidden className="notify-summary-icon" />
            <Text size="sm" fw={500} className="notify-summary-name">
              <Trans>Bell</Trans>
            </Text>
            <Text size="sm" c="var(--ink-2)">
              <Plural value={total} one={`${bellCount} of # event`} other={`${bellCount} of # events`} />
            </Text>
          </Group>
          <Group gap="sm" wrap="nowrap" miw={0}>
            <IconDeviceMobile size={16} aria-hidden className="notify-summary-icon" />
            <Text size="sm" fw={500} className="notify-summary-name">
              <Trans>Push</Trans>
            </Text>
            <Text size="sm" c="var(--ink-2)" lineClamp={1}>
              {pushedTypes.length === 0 ? (
                <Trans>Off</Trans>
              ) : pushedTypes.length <= 3 ? (
                formatList(pushedTypes.map((type) => renderLabel(INBOX_TYPE_LABELS[type])))
              ) : (
                <Plural value={pushedTypes.length} one="# event" other="# events" />
              )}
            </Text>
          </Group>
        </Stack>
        <Button
          variant="default"
          leftSection={<IconListCheck size={16} />}
          onClick={() => setOpen(true)}
          style={{ flexShrink: 0 }}
        >
          <Trans>Choose events</Trans>
        </Button>
      </Group>

      <Modal opened={open} onClose={() => setOpen(false)} size={1080} title={<ModalHeading title={t`Notification events`} />}>
        <div className="notify-columns">
          {columns.map((cats) => (
            <div key={cats[0].id}>{matrix(cats)}</div>
          ))}
        </div>
        <ModalFooter status={<Trans>Push needs Bell on, and push turned on for this device.</Trans>}>
          <Button onClick={() => setOpen(false)}>
            <Trans>Done</Trans>
          </Button>
        </ModalFooter>
      </Modal>
    </SettingsSection>
  )
}
