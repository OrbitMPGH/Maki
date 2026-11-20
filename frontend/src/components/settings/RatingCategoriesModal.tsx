import { useState } from 'react'
import { ActionIcon, Badge, Button, Group, Modal, SegmentedControl, Switch, Text, TextInput } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import {
  MAX_CUSTOM_CATEGORIES,
  MAX_ENABLED_CATEGORIES,
  useCategoryLabel,
  useRatingSettings,
  useSaveRatingSettings,
} from '../../api/ratings'
import type { RatingCategory } from '../../lib/rating'
import { randomUUID } from '../../lib/uuid'
import { IconPlus, IconTrash } from '../../lib/icons'
import { ModalHeading } from '../ui/ModalShell'
import { MODAL_SIZE } from '../ui/modalSizes'

const NAME_MAX = 40

/** Which categories the rating breakdown asks about, how much each one counts, and custom ones. */
export function RatingCategoriesEditor() {
  const { t } = useLingui()
  const { settings } = useRatingSettings()
  const save = useSaveRatingSettings()
  const label = useCategoryLabel()
  const [name, setName] = useState('')

  const categories = settings.categories
  const enabledCount = categories.filter((c) => c.enabled).length
  const customCount = categories.filter((c) => c.name != null).length
  const write = (next: RatingCategory[]) => save.mutate({ ...settings, categories: next })
  const patch = (key: string, change: Partial<RatingCategory>) =>
    write(categories.map((c) => (c.key === key ? { ...c, ...change } : c)))

  const trimmed = name.trim()
  const canAdd = trimmed.length > 0 && customCount < MAX_CUSTOM_CATEGORIES
  const add = () => {
    if (!canAdd) return
    const key = `c-${randomUUID().replace(/-/g, '').slice(0, 10)}`
    write([...categories, { key, name: trimmed, enabled: enabledCount < MAX_ENABLED_CATEGORIES, weight: 1 }])
    setName('')
  }

  const weights = [
    { value: '0.5', label: t`Half` },
    { value: '1', label: t`Normal` },
    { value: '2', label: t`Double` },
  ]

  return (
    <>
      <div className="rating-categories">
        {categories.map((c) => {
          const full = !c.enabled && enabledCount >= MAX_ENABLED_CATEGORIES
          const text = label(c)
          return (
            <div key={c.key} className="rating-categories-row" data-off={!c.enabled || undefined}>
              <Switch
                checked={c.enabled}
                disabled={full}
                aria-label={text}
                onChange={(e) => patch(c.key, { enabled: e.currentTarget.checked })}
              />
              <Group gap={8} wrap="nowrap" miw={0}>
                <Text size="sm" truncate>
                  {text}
                </Text>
                {c.name != null && (
                  <Badge size="xs" variant="light" color="gray">
                    <Trans>Custom</Trans>
                  </Badge>
                )}
              </Group>
              {c.enabled ? (
                <SegmentedControl
                  size="xs"
                  data={weights}
                  value={String(c.weight)}
                  aria-label={t`How much ${text} counts`}
                  onChange={(v) => patch(c.key, { weight: Number(v) })}
                />
              ) : (
                <span />
              )}
              {c.name != null ? (
                <ActionIcon
                  size="sm"
                  variant="subtle"
                  color="var(--neutral)"
                  aria-label={t`Remove ${text}`}
                  onClick={() => write(categories.filter((x) => x.key !== c.key))}
                >
                  <IconTrash size={16} />
                </ActionIcon>
              ) : (
                <span />
              )}
            </div>
          )
        })}
      </div>
      <Group mt="md" gap="xs" wrap="nowrap" align="flex-end">
        <TextInput
          style={{ flex: 1 }}
          label={t`New category`}
          value={name}
          maxLength={NAME_MAX}
          disabled={customCount >= MAX_CUSTOM_CATEGORIES}
          onChange={(e) => setName(e.currentTarget.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') add()
          }}
        />
        <Button variant="default" leftSection={<IconPlus size={16} />} disabled={!canAdd} onClick={add}>
          <Trans>Add</Trans>
        </Button>
      </Group>
      <Text size="xs" c="var(--ink-4)" mt="sm">
        <Trans>
          Up to {MAX_ENABLED_CATEGORIES} on at once. Changing these never changes a rating you already set.
        </Trans>
      </Text>
    </>
  )
}

/** The category editor as a dialog, for Settings. */
export function RatingCategoriesModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  return (
    <Modal opened={opened} onClose={onClose} title={<ModalHeading title={<Trans>Breakdown categories</Trans>} />} size={MODAL_SIZE.default}>
      <RatingCategoriesEditor />
    </Modal>
  )
}
