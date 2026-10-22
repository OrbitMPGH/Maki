import { useCallback, useEffect, useState } from 'react'
import { Alert, Button, Group, Stack, Switch, Text } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { useQueryClient } from '@tanstack/react-query'
import { Trans, useLingui } from '@lingui/react/macro'
import { SettingsSection } from '../../pages/settings/SettingsSection'
import { SecureContextNote } from './SecureContextNote'
import {
  pushPublicKeyQuery,
  toSubscriptionBody,
  useSendPushTest,
  useSubscribePush,
  useUnsubscribePush,
} from '../../api/push'
import {
  getPushSubscription,
  iosNeedsInstall,
  notificationPermission,
  pushSupported,
  requestNotificationPermission,
  subscribePush,
  unsubscribePush,
} from '../../lib/pwa/push'
import { pwaSupported } from '../../lib/pwa/serviceWorker'

export function PushNotificationsSection() {
  const { t } = useLingui()
  const supported = pwaSupported()
  const needsInstall = supported && iosNeedsInstall()

  return (
    <SettingsSection
      id="push-notifications"
      title={<Trans>Push notifications</Trans>}
      description={
        <Trans>
          Notifications on this device, even when Maki is closed. Which events push is picked in the
          Notifications table above.
        </Trans>
      }
    >
      {!supported ? (
        <SecureContextNote />
      ) : needsInstall ? (
        <Alert color="var(--info)" variant="light">
          <Trans>
            iPhone and iPad only deliver push notifications to apps added to the home screen. Add
            Maki to your home screen, open it from there, and come back to this card.
          </Trans>
        </Alert>
      ) : !pushSupported() ? (
        <Text size="sm" c="var(--ink-2)">
          {t`This browser does not support push notifications.`}
        </Text>
      ) : (
        <PushControls />
      )}
    </SettingsSection>
  )
}

function PushControls() {
  const { t } = useLingui()
  const queryClient = useQueryClient()
  const [permission, setPermission] = useState(notificationPermission)
  const [enabled, setEnabled] = useState<boolean | null>(null)
  const [busy, setBusy] = useState(false)
  const subscribe = useSubscribePush()
  const unsubscribe = useUnsubscribePush()
  const sendTest = useSendPushTest()

  const refresh = useCallback(async () => {
    setPermission(notificationPermission())
    setEnabled((await getPushSubscription()) !== null)
  }, [])

  useEffect(() => {
    void refresh()
  }, [refresh])

  const blocked = permission === 'denied'

  async function enable() {
    const failed = t`This device could not subscribe to push notifications.`
    setBusy(true)
    try {
      const result = await requestNotificationPermission()
      if (result !== 'granted') return
      const { publicKey } = await queryClient.fetchQuery(pushPublicKeyQuery)
      const subscription = await subscribePush(publicKey)
      const body = subscription && toSubscriptionBody(subscription)
      if (!body) throw new Error(failed)
      try {
        await subscribe.mutateAsync(body)
      } catch (error) {
        await unsubscribePush()
        throw error
      }
    } catch (error) {
      // A failed server call has already raised its own toast. Anything else is a browser
      // error whose text is untranslated, so it gets a fixed message.
      if (error instanceof Error && error.name === 'ApiError') return
      notifications.show({ message: failed, color: 'var(--danger)' })
    } finally {
      await refresh()
      setBusy(false)
    }
  }

  async function disable() {
    setBusy(true)
    try {
      const endpoint = await unsubscribePush()
      if (endpoint) await unsubscribe.mutateAsync(endpoint)
    } catch {
      // The server call already raised its own toast.
    } finally {
      await refresh()
      setBusy(false)
    }
  }

  function test() {
    sendTest.mutate(undefined, {
      onSuccess: ({ sent, failed, error }) => {
        if (sent > 0) {
          notifications.show({ message: t`Test notification sent.`, color: 'var(--ok)' })
        } else if (failed > 0) {
          notifications.show({
            title: t`The push service did not accept the test notification.`,
            message: error ?? '',
            color: 'var(--danger)',
          })
        }
      },
    })
  }

  const description = blocked
    ? t`Blocked for Maki in your browser settings. Allow notifications there, then come back.`
    : enabled
      ? t`On for this device.`
      : t`Off for this device. Turning it on asks your browser for permission.`

  return (
    <Stack gap="md">
      <Switch
        label={t`Notify this device`}
        description={description}
        checked={Boolean(enabled) && !blocked}
        disabled={blocked || enabled === null || busy}
        onChange={(e) => void (e.currentTarget.checked ? enable() : disable())}
      />
      {blocked && (
        <Alert color="var(--warn)" variant="light">
          <Trans>
            Notifications are blocked for this site. Change the permission in your browser's site
            settings to turn them on.
          </Trans>
        </Alert>
      )}
      {enabled && !blocked && (
        <Group>
          <Button variant="default" size="xs" onClick={test} loading={sendTest.isPending}>
            <Trans>Send test</Trans>
          </Button>
        </Group>
      )}
    </Stack>
  )
}
