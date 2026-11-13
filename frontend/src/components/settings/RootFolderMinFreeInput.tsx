import { useState } from 'react'
import { NumberInput } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { notifications } from '../../lib/toast'
import { useSetRootFolderMinFree } from '../../api/hooks'
import type { RootFolder } from '../../api/types'

const GB = 1024 * 1024 * 1024
const DEFAULT_MIN_FREE = 5 * GB
const MAX_GB = 100_000

/**
 * The free space a root folder keeps: downloads into it wait while its drive has less. Whole GB,
 * saved when the field loses focus; 0 means never wait.
 */
export function RootFolderMinFreeInput({ folder }: { folder: RootFolder }) {
  const { t } = useLingui()
  const save = useSetRootFolderMinFree()
  const { path } = folder
  const current = Math.round((folder.minFreeBytes ?? folder.defaultMinFreeBytes ?? DEFAULT_MIN_FREE) / GB)
  const [draft, setDraft] = useState<number | string>(current)
  const [seen, setSeen] = useState(current)
  if (current !== seen) {
    setSeen(current)
    setDraft(current)
  }

  const commit = () => {
    const gb = typeof draft === 'number' ? Math.min(MAX_GB, Math.max(0, Math.round(draft))) : current
    setDraft(gb)
    if (gb === current) return
    save.mutate(
      { id: folder.id, minFreeBytes: gb * GB },
      {
        onError: () => {
          setDraft(current)
          notifications.show({ message: t`Couldn't save the free space to keep`, color: 'var(--danger)' })
        },
      },
    )
  }

  return (
    <NumberInput
      size="xs"
      w={110}
      min={0}
      max={MAX_GB}
      allowDecimal={false}
      allowNegative={false}
      clampBehavior="blur"
      suffix=" GB"
      value={draft}
      onChange={setDraft}
      onBlur={commit}
      onKeyDown={(event) => {
        if (event.key === 'Enter') commit()
      }}
      disabled={save.isPending}
      aria-label={t`Free space to keep on ${path}`}
    />
  )
}
