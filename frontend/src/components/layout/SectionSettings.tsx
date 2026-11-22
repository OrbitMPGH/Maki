import type { ReactNode } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import type { MessageDescriptor } from '@lingui/core'
import { Button, SegmentedControl } from '@mantine/core'
import {
  IconArrowBarToDown,
  IconArrowBarToUp,
  IconArrowDown,
  IconArrowUp,
  IconLayoutRows,
  IconSelector,
  IconPencil,
  IconTrash,
} from '../../lib/icons'
import type { PageSection } from '../../api/hooks'
import type { CustomRail } from '../../api/customRails'
import { useLabel } from '../../i18n-context'
import { DrillBack, DrillField, DrillRow, useDrillPages } from '../ui/DrillPanel'
import { DrillSwitch } from '../ui/DrillControls'
import { DrillPanes } from '../ui/DrillPanes'
import type { SectionDef } from './pageLayout'

type Page = 'main' | 'move'
const PARENTS: Record<Page, Page> = { main: 'main', move: 'main' }

export interface SectionSettingsProps {
  section: PageSection
  index: number
  total: number
  def: SectionDef | undefined
  rail: CustomRail | undefined
  panelHint?: (sectionKey: string, panelKey: string) => MessageDescriptor | null
  optionHint?: (sectionKey: string, optionKey: string) => MessageDescriptor | null
  /** A bottom sheet: moves are two buttons at the foot instead of a page of their own. */
  sheet?: boolean
  onChange: (patch: Partial<PageSection>) => void
  onMove: (to: number) => void
  onEditRail: () => void
  onDeleteRail: () => void
}

/**
 * One section's settings in the layout editor, as drill panel rows. Everything here edits the
 * draft, which the page draws straight away; a rail's own fields and its deletion save the rail.
 */
export function SectionSettings({
  section,
  index,
  total,
  def,
  rail,
  panelHint,
  optionHint,
  sheet = false,
  onChange,
  onMove,
  onEditRail,
  onDeleteRail,
}: SectionSettingsProps) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const pages = useDrillPages<Page>('main', PARENTS)
  const first = index <= 0
  const last = index >= total - 1
  const position = index + 1

  const main: ReactNode = (
    <>
      {def?.description && <div className="layout-settings-about">{renderLabel(def.description)}</div>}

      <DrillSwitch
        label={t`Show this section`}
        checked={section.enabled}
        onChange={(enabled) => onChange({ enabled })}
      />

      {def?.hero && (
        <DrillSwitch
          label={t`Lead with a hero`}
          checked={section.hero ?? false}
          onChange={(hero) => onChange({ hero })}
        />
      )}

      {def?.options?.map((option) => {
        const hint = optionHint?.(section.key, option.key)
        const waits = option.hero === true && !section.hero
        const checked = section.options?.[option.key] ?? false
        const set = (value: boolean) => onChange({ options: { ...(section.options ?? {}), [option.key]: value } })
        const help = hint ? renderLabel(hint) : waits ? t`Needs the hero.` : option.description ? renderLabel(option.description) : undefined
        if (option.choices) {
          const [off, on] = option.choices
          return (
            <DrillField key={option.key} icon={option.icon ?? IconLayoutRows} title={renderLabel(option.label)} help={hint || waits ? help : undefined}>
              <SegmentedControl
                aria-label={renderLabel(option.label)}
                fullWidth
                size="xs"
                disabled={waits}
                value={checked ? 'on' : 'off'}
                onChange={(value) => set(value === 'on')}
                data={[
                  { value: 'off', label: renderLabel(off) },
                  { value: 'on', label: renderLabel(on) },
                ]}
              />
            </DrillField>
          )
        }
        return (
          <DrillSwitch
            key={option.key}
            label={renderLabel(option.label)}
            hint={help}
            disabled={waits}
            checked={checked}
            onChange={set}
          />
        )
      })}

      {section.panels && def?.panels && (
        <>
          <div className="layout-settings-heading">
            <Trans>Panels</Trans>
          </div>
          {section.panels.map((panel) => {
            const panelDef = def.panels!.find((p) => p.key === panel.key)
            const hint = panelHint?.(section.key, panel.key)
            return (
              <DrillSwitch
                key={panel.key}
                label={panelDef ? renderLabel(panelDef.label) : panel.key}
                hint={hint ? renderLabel(hint) : undefined}
                checked={panel.enabled}
                onChange={(enabled) =>
                  onChange({ panels: section.panels!.map((p) => (p.key === panel.key ? { ...p, enabled } : p)) })
                }
              />
            )
          })}
        </>
      )}

      {rail && (
        <>
          <div className="layout-settings-divider" />
          <DrillRow icon={IconPencil} label={t`Edit rail`} chevron={false} onClick={onEditRail} />
          <DrillRow icon={IconTrash} label={t`Delete rail`} chevron={false} onClick={onDeleteRail} />
          <div className="layout-settings-about">
            <Trans>Rail changes save straight away. Done saves the layout.</Trans>
          </div>
        </>
      )}

      {!sheet && (
        <>
          <div className="layout-settings-divider" />
          <DrillRow
            ref={pages.rowRef('move')}
            icon={IconSelector}
            label={t`Move`}
            value={t`${position} of ${total}`}
            onClick={() => pages.open('move')}
          />
        </>
      )}
    </>
  )

  if (sheet) {
    return (
      <div className="layout-settings">
        {main}
        <div className="layout-settings-moves">
          <Button variant="default" leftSection={<IconArrowUp size={16} />} disabled={first} onClick={() => onMove(index - 1)}>
            <Trans>Move up</Trans>
          </Button>
          <Button variant="default" leftSection={<IconArrowDown size={16} />} disabled={last} onClick={() => onMove(index + 1)}>
            <Trans>Move down</Trans>
          </Button>
        </div>
      </div>
    )
  }

  return (
    <div
      className="layout-settings"
      onKeyDown={(event) => {
        if (event.key !== 'Escape' || pages.page === 'main') return
        event.stopPropagation()
        pages.back()
      }}
    >
      <DrillPanes page={pages.page} depth={pages.depth} bleed={6}>
        {pages.page === 'main' ? (
          main
        ) : (
          <>
            <DrillBack ref={pages.backRef} title={t`Move`} onBack={pages.back} />
            <DrillRow icon={IconArrowBarToUp} label={t`Top`} chevron={false} disabled={first} onClick={() => onMove(0)} />
            <DrillRow icon={IconArrowUp} label={t`Up`} chevron={false} disabled={first} onClick={() => onMove(index - 1)} />
            <DrillRow icon={IconArrowDown} label={t`Down`} chevron={false} disabled={last} onClick={() => onMove(index + 1)} />
            <DrillRow
              icon={IconArrowBarToDown}
              label={t`Bottom`}
              chevron={false}
              disabled={last}
              onClick={() => onMove(total - 1)}
            />
          </>
        )}
      </DrillPanes>
    </div>
  )
}
