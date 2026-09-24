import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { Reorder, useDragControls, useReducedMotion } from 'motion/react'
import { Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { Button, Group, Loader, Text, VisuallyHidden } from '@mantine/core'
import { notifications } from '@mantine/notifications'
import {
  IconFilter,
  IconGripVertical,
  IconLayoutDashboard,
  IconLibrary,
  IconPlus,
  IconSparkles,
} from '@tabler/icons-react'
import { isRailKey, railIdOf, useSavePageLayout, type PageSection } from '../../api/hooks'
import {
  RAIL_SOURCE_LABELS,
  useCustomRailItems,
  type CustomRail,
  type CustomRailPlacement,
} from '../../api/customRails'
import { useLabel } from '../../i18n-context'
import { CustomRailEditor, type CustomRailDraft } from '../rails/CustomRailEditor'
import {
  moveSection,
  reconcileLayout,
  sameLayout,
  sectionVisible,
  type LayoutConfig,
  type SectionRegistry,
} from './pageLayout'
import { SectionProperties } from './SectionProperties'

const RAIL_ICONS = { library: IconLibrary, recommendations: IconSparkles, catalogue: IconFilter }

export interface PageLayoutEditorProps {
  page: 'home' | 'discover'
  placement: CustomRailPlacement
  registry: SectionRegistry
  config: LayoutConfig
  /** The saved layout. Captured once when the editor mounts; later changes to it are ignored. */
  initial: PageSection[]
  rails: CustomRail[] | undefined
  /** The page's own rail size, so previews read the same cached query the page filled. */
  railLimit: number
  /** One line under a section's name, from data the page already holds. Must not fetch. */
  summary?: (key: string) => ReactNode
  /** Cover URLs for a section's preview strip, from cached data only. */
  preview?: (key: string) => (string | null)[]
  /** Why a section cannot show anything right now, e.g. the local database is off. */
  unavailable?: (key: string) => MessageDescriptor | null
  /** A hint under one panel's switch, e.g. why it stays empty. */
  panelHint?: (sectionKey: string, panelKey: string) => MessageDescriptor | null
  onExit: () => void
}

/**
 * The layout editor for a page: every section as a compact card, dragged by its grip to reorder
 * and clicked to open its properties. Works on a draft; Done saves it in one request and Cancel
 * throws it away. Rails are the exception: creating, editing or deleting one saves the rail itself
 * straight away, since it is its own record, and the layout picks it up either way.
 */
export function PageLayoutEditor({
  page,
  placement,
  registry,
  config,
  initial,
  rails,
  railLimit,
  summary,
  preview,
  unavailable,
  panelHint,
  onExit,
}: PageLayoutEditorProps) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const reduceMotion = useReducedMotion()
  const save = useSavePageLayout()

  const [draft, setDraft] = useState<PageSection[]>(initial)
  const [initialDraft] = useState<PageSection[]>(initial)
  const [selected, setSelected] = useState<string | null>(null)
  const [adding, setAdding] = useState(false)
  const [created, setCreated] = useState<Set<number>>(() => new Set())
  const [announcement, setAnnouncement] = useState('')

  const railIds = useMemo(() => (rails ?? []).map((r) => r.id), [rails])
  const railById = useMemo(() => new Map((rails ?? []).map((r) => [r.id, r])), [rails])
  const sections = useMemo(() => reconcileLayout(draft, config, railIds), [draft, config, railIds])
  const baseline = useMemo(() => reconcileLayout(initialDraft, config, railIds), [initialDraft, config, railIds])
  const dirty = !sameLayout(sections, baseline)

  useEffect(() => {
    if (!dirty) return
    const warn = (e: BeforeUnloadEvent) => e.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])

  const labelOf = (key: string): string => {
    if (isRailKey(key)) return railById.get(railIdOf(key))?.name ?? t`Custom rail`
    const def = registry[key]
    return def ? renderLabel(def.label) : key
  }

  const move = (from: number, to: number) => {
    if (to < 0 || to >= sections.length || from === to) return
    setDraft(moveSection(sections, from, to))
    const label = labelOf(sections[from].key)
    const position = to + 1
    const total = sections.length
    setAnnouncement(t`${label} moved to position ${position} of ${total}`)
  }

  const done = () => {
    if (!dirty) {
      onExit()
      return
    }
    save.mutate(
      { page, sections },
      {
        onSuccess: () => {
          notifications.show({ color: 'var(--ok)', message: now`Layout saved` })
          onExit()
        },
        onError: (err) => notifications.show({ color: 'var(--danger)', message: String(err) }),
      },
    )
  }

  const addDraft: CustomRailDraft = {
    placement,
    spec: { source: placement === 'home' ? 'library' : 'recommendations' },
  }
  const onRailCreated = (rail: CustomRail) => {
    setCreated((prev) => new Set(prev).add(rail.id))
    setSelected(`rail:${rail.id}`)
  }

  const selectedIndex = selected ? sections.findIndex((s) => s.key === selected) : -1
  const selectedSection = selectedIndex >= 0 ? sections[selectedIndex] : null

  return (
    <>
      <div className="layout-edit-bar">
        <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
          <IconLayoutDashboard size={18} />
          <div style={{ minWidth: 0 }}>
            <Text fw={600} size="sm">
              <Trans>Editing layout</Trans>
            </Text>
            <Text size="xs" c="var(--ink-3)">
              <Trans>Drag to reorder. Click a section to change it.</Trans>
            </Text>
          </div>
        </Group>
        <Group gap="xs" wrap="nowrap">
          <Button variant="subtle" size="xs" leftSection={<IconPlus size={14} />} onClick={() => setAdding(true)}>
            <Trans>Add a rail</Trans>
          </Button>
          <Button variant="default" size="xs" onClick={onExit} disabled={save.isPending}>
            <Trans>Cancel</Trans>
          </Button>
          <Button size="xs" onClick={done} loading={save.isPending}>
            <Trans>Done</Trans>
          </Button>
        </Group>
      </div>

      <Reorder.Group
        as="div"
        axis="y"
        className="layout-edit-list"
        values={sections.map((s) => s.key)}
        onReorder={(keys: string[]) =>
          setDraft(keys.map((key) => sections.find((s) => s.key === key)!).filter(Boolean))
        }
      >
        {sections.map((section, index) => {
          const rail = isRailKey(section.key) ? railById.get(railIdOf(section.key)) : undefined
          const def = registry[section.key]
          const icon = rail ? RAIL_ICONS[rail.spec.source] : def?.icon
          const railSource = rail ? renderLabel(RAIL_SOURCE_LABELS[rail.spec.source]) : null
          const blocked = unavailable?.(section.key)
          return (
            <SectionCard
              key={section.key}
              section={section}
              label={labelOf(section.key)}
              icon={icon}
              summary={summary?.(section.key) ?? railSource}
              previewUrls={rail ? undefined : preview?.(section.key)}
              rail={rail}
              railLimit={railLimit}
              isNew={rail != null && created.has(rail.id)}
              unavailable={blocked ? renderLabel(blocked) : null}
              reduceMotion={reduceMotion ?? false}
              onOpen={() => setSelected(section.key)}
              onMove={(delta) => move(index, index + delta)}
            />
          )
        })}
      </Reorder.Group>

      <button type="button" className="layout-edit-add" onClick={() => setAdding(true)}>
        <IconPlus size={16} />
        <Trans>Add a rail</Trans>
      </button>

      <VisuallyHidden aria-live="polite">{announcement}</VisuallyHidden>

      <SectionProperties
        section={selectedSection}
        index={selectedIndex}
        total={sections.length}
        label={selectedSection ? labelOf(selectedSection.key) : ''}
        def={selectedSection ? registry[selectedSection.key] : undefined}
        rail={selectedSection && isRailKey(selectedSection.key) ? railById.get(railIdOf(selectedSection.key)) : undefined}
        panelHint={panelHint}
        onChange={(patch) =>
          selectedSection &&
          setDraft(sections.map((s) => (s.key === selectedSection.key ? { ...s, ...patch } : s)))
        }
        onMove={(to) => move(selectedIndex, to)}
        onClose={() => setSelected(null)}
      />

      {adding && (
        <CustomRailEditor draft={addDraft} onSaved={onRailCreated} onClose={() => setAdding(false)} />
      )}
    </>
  )
}

function SectionCard({
  section,
  label,
  icon: SectionIcon,
  summary,
  previewUrls,
  rail,
  railLimit,
  isNew,
  unavailable,
  reduceMotion,
  onOpen,
  onMove,
}: {
  section: PageSection
  label: string
  icon: PageLayoutEditorProps['registry'][string]['icon'] | undefined
  summary: ReactNode
  previewUrls: (string | null)[] | undefined
  rail: CustomRail | undefined
  railLimit: number
  isNew: boolean
  unavailable: string | null
  reduceMotion: boolean
  onOpen: () => void
  onMove: (delta: -1 | 1) => void
}) {
  const { t } = useLingui()
  const controls = useDragControls()
  const ref = useRef<HTMLDivElement>(null)
  // Cache only: a card never costs a request, which is the point of editing on compact cards.
  const { data: railItems } = useCustomRailItems(rail?.id ?? 0, railLimit, false)
  const covers = (rail ? (railItems?.items ?? []).map((i) => i.thumbUrl ?? i.coverUrl) : (previewUrls ?? []))
    .filter((u): u is string => !!u)
    .slice(0, 8)

  useEffect(() => {
    if (isNew) ref.current?.scrollIntoView({ block: 'nearest', behavior: reduceMotion ? 'auto' : 'smooth' })
  }, [isNew, reduceMotion])

  const onGripKey = (e: KeyboardEvent) => {
    if (e.key === 'ArrowUp') {
      e.preventDefault()
      onMove(-1)
    } else if (e.key === 'ArrowDown') {
      e.preventDefault()
      onMove(1)
    }
  }
  const onBodyKey = (e: KeyboardEvent) => {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault()
      onOpen()
    }
  }

  const panelsOn = section.panels?.filter((p) => p.enabled).length
  const panelsTotal = section.panels?.length

  return (
    <Reorder.Item
      as="div"
      value={section.key}
      dragListener={false}
      dragControls={controls}
      transition={reduceMotion ? { duration: 0 } : undefined}
      className="layout-edit-card"
      data-hidden={!sectionVisible(section) || undefined}
      ref={ref}
    >
      <button
        type="button"
        className="layout-edit-grip"
        aria-label={t`Move ${label}`}
        onPointerDown={(e) => controls.start(e)}
        onKeyDown={onGripKey}
      >
        <IconGripVertical size={16} />
      </button>
      <div
        role="button"
        tabIndex={0}
        className="layout-edit-body"
        onClick={onOpen}
        onKeyDown={onBodyKey}
      >
        <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
          {SectionIcon && (
            <span className="layout-edit-icon">
              <SectionIcon size={16} />
            </span>
          )}
          <div style={{ minWidth: 0, flex: 1 }}>
            <Group gap={6} wrap="wrap">
              <Text fw={600} size="sm" truncate>
                {label}
              </Text>
              {!section.enabled && (
                <span className="layout-edit-badge">
                  <Trans>Hidden</Trans>
                </span>
              )}
              {section.hero && (
                <span className="layout-edit-badge" data-tone="brand">
                  <Trans>Large tiles</Trans>
                </span>
              )}
              {panelsTotal != null && panelsOn !== panelsTotal && (
                <span className="layout-edit-badge">
                  <Trans>
                    {panelsOn} of {panelsTotal} panels
                  </Trans>
                </span>
              )}
              {unavailable && <span className="layout-edit-badge" data-tone="warn">{unavailable}</span>}
              {isNew && (
                <span className="layout-edit-badge" data-tone="brand">
                  <Trans>New</Trans>
                </span>
              )}
            </Group>
            {summary && (
              <Text size="xs" c="var(--ink-3)" truncate>
                {summary}
              </Text>
            )}
          </div>
          {covers.length > 0 && (
            <div className="layout-edit-covers" aria-hidden>
              {covers.map((src, i) => (
                <img key={i} src={src} alt="" loading="lazy" />
              ))}
            </div>
          )}
        </Group>
      </div>
    </Reorder.Item>
  )
}

/** Shown while the saved layout is still loading, so the editor never starts from a guess. */
export function PageLayoutEditorLoading() {
  return (
    <Group justify="center" py="xl">
      <Loader size="sm" />
    </Group>
  )
}
