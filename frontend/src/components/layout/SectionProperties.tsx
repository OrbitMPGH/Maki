import { useState } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { Button, Divider, Drawer, Group, Modal, Stack, Switch, Text } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import {
  IconArrowBarToDown,
  IconArrowBarToUp,
  IconArrowDown,
  IconArrowUp,
  IconTrash,
} from '@tabler/icons-react'
import type { PageSection } from '../../api/hooks'
import { useDeleteCustomRail, type CustomRail } from '../../api/customRails'
import { useLabel } from '../../i18n-context'
import { CustomRailForm } from '../rails/CustomRailEditor'
import type { SectionDef } from './pageLayout'

/**
 * The properties of one section in the layout editor. Everything here edits the draft layout,
 * except a rail's own fields and its deletion, which save the rail straight away.
 */
export function SectionProperties({
  section,
  index,
  total,
  label,
  def,
  rail,
  panelHint,
  onChange,
  onMove,
  onClose,
}: {
  section: PageSection | null
  index: number
  total: number
  label: string
  def: SectionDef | undefined
  rail: CustomRail | undefined
  panelHint?: (sectionKey: string, panelKey: string) => MessageDescriptor | null
  onChange: (patch: Partial<PageSection>) => void
  onMove: (to: number) => void
  onClose: () => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [confirmDelete, setConfirmDelete] = useState(false)
  const remove = useDeleteCustomRail()

  const allPanelsOff = section?.panels?.every((p) => !p.enabled) ?? false

  return (
    <>
      <Drawer
        opened={section != null}
        onClose={onClose}
        position="right"
        size={rail ? 'xl' : 'sm'}
        title={<Text fw={600}>{label}</Text>}
      >
        {section && (
          <Stack gap="md">
            {def?.description && (
              <Text size="sm" c="var(--ink-3)">
                {renderLabel(def.description)}
              </Text>
            )}

            <Switch
              label={t`Show this section`}
              checked={section.enabled}
              onChange={(e) => onChange({ enabled: e.currentTarget.checked })}
            />

            {def?.hero && (
              <Switch
                label={t`Large tiles for the first three`}
                description={t`Lead with big cover tiles instead of a plain row.`}
                checked={section.hero ?? false}
                onChange={(e) => onChange({ hero: e.currentTarget.checked })}
              />
            )}

            {section.panels && def?.panels && (
              <Stack gap="xs">
                <Text size="sm" fw={500}>
                  <Trans>Panels</Trans>
                </Text>
                {section.panels.map((panel) => {
                  const panelDef = def.panels!.find((p) => p.key === panel.key)
                  const hint = panelHint?.(section.key, panel.key)
                  return (
                    <Switch
                      key={panel.key}
                      label={panelDef ? renderLabel(panelDef.label) : panel.key}
                      description={hint ? renderLabel(hint) : undefined}
                      checked={panel.enabled}
                      onChange={(e) => {
                        const enabled = e.currentTarget.checked
                        onChange({
                          panels: section.panels!.map((p) => (p.key === panel.key ? { ...p, enabled } : p)),
                        })
                      }}
                    />
                  )
                })}
                {allPanelsOff && (
                  <Text size="xs" c="var(--ink-3)">
                    <Trans>With every panel off, this section stays empty.</Trans>
                  </Text>
                )}
              </Stack>
            )}

            <Stack gap={6}>
              <Text size="sm" fw={500}>
                <Trans>Position</Trans>
              </Text>
              <Group gap="xs">
                <Button
                  size="xs"
                  variant="default"
                  leftSection={<IconArrowBarToUp size={14} />}
                  disabled={index <= 0}
                  onClick={() => onMove(0)}
                >
                  <Trans>Top</Trans>
                </Button>
                <Button
                  size="xs"
                  variant="default"
                  leftSection={<IconArrowUp size={14} />}
                  disabled={index <= 0}
                  onClick={() => onMove(index - 1)}
                >
                  <Trans>Up</Trans>
                </Button>
                <Button
                  size="xs"
                  variant="default"
                  leftSection={<IconArrowDown size={14} />}
                  disabled={index >= total - 1}
                  onClick={() => onMove(index + 1)}
                >
                  <Trans>Down</Trans>
                </Button>
                <Button
                  size="xs"
                  variant="default"
                  leftSection={<IconArrowBarToDown size={14} />}
                  disabled={index >= total - 1}
                  onClick={() => onMove(total - 1)}
                >
                  <Trans>Bottom</Trans>
                </Button>
              </Group>
            </Stack>

            {rail && (
              <>
                <Divider />
                <Text size="xs" c="var(--ink-3)">
                  <Trans>Rail changes save straight away. Done saves the layout.</Trans>
                </Text>
                <CustomRailForm key={rail.id} rail={rail} placementLocked />
                <Divider />
                <Group>
                  <Button
                    color="var(--danger)"
                    variant="light"
                    leftSection={<IconTrash size={14} />}
                    onClick={() => setConfirmDelete(true)}
                  >
                    <Trans>Delete rail</Trans>
                  </Button>
                </Group>
              </>
            )}
          </Stack>
        )}
      </Drawer>

      <Modal opened={confirmDelete} onClose={() => setConfirmDelete(false)} title={t`Delete rail`} size="sm">
        <Stack gap="md">
          <Text size="sm">
            <Trans>Delete the rail "{label}"? Its filters go with it.</Trans>
          </Text>
          <Group justify="flex-end">
            <Button variant="subtle" onClick={() => setConfirmDelete(false)}>
              <Trans>Cancel</Trans>
            </Button>
            <Button
              color="var(--danger)"
              loading={remove.isPending}
              onClick={() =>
                rail &&
                remove.mutate(rail.id, {
                  onSuccess: () => {
                    setConfirmDelete(false)
                    onClose()
                    notifications.show({ color: 'var(--ok)', message: now`Rail deleted` })
                  },
                  onError: (err) => notifications.show({ color: 'var(--danger)', message: String(err) }),
                })
              }
            >
              <Trans>Delete</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  )
}
