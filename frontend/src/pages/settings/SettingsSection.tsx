import { useContext, useEffect, useId, type ReactNode } from 'react'
import { Button, Group } from '@mantine/core'
import { Trans } from '@lingui/react/macro'
import { Panel, type PanelProps } from '../../components/ui/Panel'
import { SettingsHelp } from '../../components/settings/SettingsHelp'
import { UnsavedSettingsContext } from '../../components/settings/SaveButton'

export interface SettingsSectionProps {
  /** Registry id (or a sub-id for a card that shares one entry); the heading is `setting-{id}-title`. */
  id: string
  title: ReactNode
  description?: ReactNode
  /** Controls that belong to the heading row whatever the dirty state: "New profile", a master switch. */
  actions?: ReactNode
  dirty?: boolean
  saving?: boolean
  saveDisabled?: boolean
  onSave?: () => void
  onDiscard?: () => void
  panelProps?: Omit<PanelProps, 'children'>
  children: ReactNode
}

/**
 * One settings group: the title and its explanation above the card, the fields inside it. A group
 * that saves together shows Save and Discard in the heading row only while it holds unsaved edits,
 * and that row sticks under the app header until the section scrolls past, so a field at the
 * bottom of a long group is never a screen away from its Save.
 */
export function SettingsSection({
  id,
  title,
  description,
  actions,
  dirty,
  saving,
  saveDisabled,
  onSave,
  onDiscard,
  panelProps,
  children,
}: SettingsSectionProps) {
  const reportId = useId()
  const report = useContext(UnsavedSettingsContext)
  const unsaved = Boolean(dirty && onSave)
  const titleId = `setting-${id}-title`

  useEffect(() => {
    report?.(reportId, unsaved)
    return () => report?.(reportId, false)
  }, [report, reportId, unsaved])

  return (
    <section className="settings-section" aria-labelledby={titleId} data-dirty={unsaved || undefined}>
      <div className="settings-section-head">
        <h2 id={titleId} className="settings-section-title">
          {title}
        </h2>
        {(actions || unsaved) && (
          <Group gap="xs" wrap="nowrap" className="settings-section-actions">
            {actions}
            {unsaved && (
              <>
                <span className="save-control-hint">
                  <Trans>Unsaved</Trans>
                </span>
                {onDiscard && (
                  <Button size="xs" variant="subtle" disabled={saving} onClick={onDiscard}>
                    <Trans>Discard</Trans>
                  </Button>
                )}
                <Button size="xs" loading={saving} disabled={saveDisabled} onClick={onSave}>
                  <Trans>Save</Trans>
                </Button>
              </>
            )}
          </Group>
        )}
      </div>
      {description && (
        <div className="settings-section-desc">
          <SettingsHelp>{description}</SettingsHelp>
        </div>
      )}
      <Panel edge={unsaved ? 'brand' : undefined} {...panelProps}>
        {children}
      </Panel>
    </section>
  )
}
