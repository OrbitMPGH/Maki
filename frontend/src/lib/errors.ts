import { notifications } from '@mantine/notifications'

/** Reports a failed mutation as a toast. Pass as `onError` to a `useMutation` call. */
export const onErrorToast = (err: unknown) =>
  notifications.show({
    color: 'var(--danger)',
    message: err instanceof Error ? err.message : String(err),
  })
