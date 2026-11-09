import {
  ActionIcon,
  Button,
  Group,
  Menu,
  MultiSelect,
  Popover,
  SegmentedControl,
  Select,
  Slider,
  Stack,
  Switch,
  Text,
  Tooltip,
  UnstyledButton,
  VisuallyHidden,
} from '@mantine/core'
import {
  IconArrowAutofitHeight,
  IconArrowAutofitWidth,
  IconArrowLeft,
  IconArrowsMaximize,
  IconChevronDown,
  IconChevronRight,
  IconEyeOff,
  IconListCheck,
  IconMoon,
  IconPalette,
  IconSettings,
  IconAperture,
  IconSquare,
  IconSun,
  IconUserCircle,
  IconViewportWide,
} from '../../lib/icons'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import type { PrefsSource, ReaderManifest } from '../../api/reader'
import type { ReadingProfile } from '../../api/readingProfiles'
import { wakeLockSupported } from '../../lib/wakeLock'
import { useLabel } from '../../i18n-context'
import { DrillPanes } from '../../components/ui/DrillPanes'
import {
  BROWSER_ENHANCE_METHODS,
  ENHANCE_FALLBACKS,
  ENHANCE_PROGRESS_STYLES,
  ENHANCE_LEVELS,
  ENHANCE_SKIP_TIERS,
  SERVER_ENHANCE_METHODS,
  enhanceMethodInfo,
  isServerMethod,
  serverModelId,
  shownEnhanceMethod,
} from './enhanceMethods'
import { BACKGROUNDS, enhanceFor, toneFilter, type PrefsSelection, type ReaderPrefs } from './prefs'
import { useEnhanceEngine } from './enhanceRuntime'

/**
 * The reader renders above Mantine's popover layer, so anything that portals to <body> has to
 * be lifted past it explicitly, otherwise it paints underneath the page and looks like the
 * control simply doesn't work.
 */
export const OVERLAY_Z = 500

// Inside the popover, a portalled list counts as outside it: the first touch on the list closed
// both, so it could never be scrolled. Rendered in place, fixed so the popover does not clip it.
const NESTED_COMBOBOX = { zIndex: OVERLAY_Z + 1, withinPortal: false, floatingStrategy: 'fixed' } as const

type View = 'main' | 'wide' | 'look' | 'enhance' | 'reading'

type Toggle =
  | 'tapZones'
  | 'showPageNumber'
  | 'chapterBanner'
  | 'autoNextChapter'
  | 'autoWebtoon'
  | 'keepAwake'
  | 'notesBeside'

function LayoutGlyph({ mode }: { mode: ReaderPrefs['mode'] }) {
  const page = { className: 'reader-settings-glyph-page', rx: 2 }
  return (
    <svg width="40" height="30" viewBox="0 0 40 30" aria-hidden>
      {mode === 'paged' && <rect x="12" y="2" width="16" height="26" {...page} />}
      {mode === 'double' && (
        <>
          <rect x="3" y="2" width="16" height="26" {...page} />
          <rect x="21" y="2" width="16" height="26" {...page} />
        </>
      )}
      {mode === 'vertical' && (
        <>
          <rect x="13" y="-6" width="14" height="14" {...page} />
          <rect x="13" y="10" width="14" height="14" {...page} />
          <rect x="13" y="26" width="14" height="14" {...page} />
        </>
      )}
    </svg>
  )
}

function SubPage({
  title,
  onBack,
  backRef,
  children,
}: {
  title: string
  onBack: () => void
  backRef: React.Ref<HTMLButtonElement>
  children: ReactNode
}) {
  const { t } = useLingui()
  return (
    <div>
      <UnstyledButton ref={backRef} className="drill-back reader-settings-back" onClick={onBack}>
        <span className="drill-back-icon" aria-hidden>
          <IconArrowLeft size={16} />
        </span>
        <VisuallyHidden>{t`Back`}</VisuallyHidden>
        <span>{title}</span>
      </UnstyledButton>
      {children}
    </div>
  )
}

function FieldLabel({ children }: { children: ReactNode }) {
  return (
    <Text fz="xs" fw={600} c="var(--ink-3)" mb={6}>
      {children}
    </Text>
  )
}

/**
 * The reader's settings. The first view holds what gets changed mid-chapter (layout, direction,
 * fit, brightness and tint); everything else is one row showing its current value that opens as a
 * page inside the same popover, so the popover stays short whatever is switched on.
 */
export default function ReaderSettings({
  opened,
  onOpenedChange,
  manifest,
  prefs,
  onPrefs,
  autoLayout,
  selection,
  onSelection,
  source,
  autoProfileId,
  profiles,
  incognito,
  onIncognito,
  previewSrc,
}: {
  opened: boolean
  onOpenedChange: (opened: boolean) => void
  manifest: ReaderManifest
  prefs: ReaderPrefs
  onPrefs: (patch: Partial<ReaderPrefs>) => void
  autoLayout: boolean
  selection: PrefsSelection
  onSelection: (selection: PrefsSelection) => void
  source: PrefsSource
  autoProfileId: number | null
  profiles: ReadingProfile[]
  incognito: boolean
  onIncognito: (value: boolean) => void
  /** The page on screen, shown through each colour filter. */
  previewSrc?: string
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [view, setView] = useState<View>('main')
  const [profileMenu, setProfileMenu] = useState(false)

  // Reset as the popover opens, not as it closes, so its closing fade doesn't show a page sliding.
  const [wasOpened, setWasOpened] = useState(opened)
  if (opened !== wasOpened) {
    setWasOpened(opened)
    if (opened) setView('main')
    else setProfileMenu(false)
  }

  // Opening a page puts focus on its back button; going back returns it to the row that opened it.
  const backRef = useRef<HTMLButtonElement>(null)
  const rowRefs = useRef<Partial<Record<View, HTMLButtonElement | null>>>({})
  const cameFrom = useRef<View | null>(null)
  useEffect(() => {
    if (view !== 'main') backRef.current?.focus({ preventScroll: true })
    else if (cameFrom.current) rowRefs.current[cameFrom.current]?.focus({ preventScroll: true })
  }, [view])
  const open = (next: View) => {
    cameFrom.current = next
    setView(next)
  }
  const back = () => setView('main')

  useEffect(() => {
    if (!opened) cameFrom.current = null
  }, [opened])

  // Mantine only closes on Escape when focus is inside the dropdown, and focus is usually still on
  // the gear, so the reader's own Escape (leave the reader) would win. Captured on the window so it
  // runs first, and Mantine's own is off so that on a page inside the popover Escape only steps back.
  useEffect(() => {
    if (!opened) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      event.preventDefault()
      if (profileMenu) setProfileMenu(false)
      else if (view === 'main') onOpenedChange(false)
      else setView('main')
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [opened, view, profileMenu, onOpenedChange])

  const autoProfile = profiles.find((p) => p.id === autoProfileId)
  const autoProfileName = autoProfile?.name
  const autoLabel = autoProfile ? t`Auto (${autoProfileName})` : t`Auto (my defaults)`
  const profileData = [
    { label: autoLabel, value: 'auto' },
    ...profiles.map((p) => ({ label: p.name, value: String(p.id) })),
    { label: t`Just this series`, value: 'series' },
  ]
  const profileName = profileData.find((option) => option.value === String(selection))?.label ?? autoLabel

  const inForce = profiles.find((p) => p.id === selection) ?? (selection === 'auto' ? autoProfile : undefined)
  const inForceName = inForce?.name
  // Said on the first view: a Fit tweak on a shared profile changes every series that uses it.
  const editsAffect =
    source === 'Series' ? (
      <Trans>
        Changes apply to <strong>this series</strong> only.
      </Trans>
    ) : source === 'Profile' && inForce ? (
      <Trans>
        Changes apply to the <strong>{inForceName}</strong> profile and every series using it.
      </Trans>
    ) : (
      <Trans>
        Changes apply to <strong>your reader defaults</strong>.
      </Trans>
    )

  const { scale } = prefs
  const enhanceOn = prefs.enhance !== 'off'
  const browserUnsupported = useEnhanceEngine(enhanceOn).status === 'unsupported'
  const enhanceModels = manifest.enhanceServer?.models
  const method = shownEnhanceMethod(
    prefs.enhanceMethod,
    prefs.enhanceFallback,
    !!enhanceModels?.some((entry) => entry.id === serverModelId(prefs.enhanceMethod)),
  )
  const serverOptions = SERVER_ENHANCE_METHODS.flatMap((m) => {
    const model = enhanceModels?.find((entry) => entry.id === serverModelId(m.id))
    if (!model && m.id !== method) return []
    return [{ value: m.id, label: renderLabel(m.label), disabled: !model || !model.ready }]
  })
  const enhanceOptions = [
    {
      group: t`On this device`,
      items: BROWSER_ENHANCE_METHODS.map((m) => ({ value: m.id, label: renderLabel(m.label), disabled: browserUnsupported })),
    },
    ...(serverOptions.length ? [{ group: t`On the server`, items: serverOptions }] : []),
  ]
  const enhanceNote = (value: string): string | undefined => {
    const model = enhanceModels?.find((entry) => entry.id === serverModelId(value))
    if (serverModelId(value) !== null) {
      if (!model) return t`Not available`
      if (!model.ready) return t`Downloading`
    } else if (browserUnsupported) {
      return t`Not supported on this device`
    }
    const info = enhanceMethodInfo(value)
    return info ? renderLabel(info.hint) : undefined
  }
  const skippedHere = enhanceOn && enhanceFor(prefs, manifest.tier) === 'off'

  const layouts = [
    { value: 'paged', label: t`Single` },
    { value: 'double', label: t`Double` },
    { value: 'vertical', label: t`Continuous` },
  ] as const
  const fits = [
    { value: 'height', label: t`Fit height`, icon: IconArrowAutofitHeight },
    { value: 'width', label: t`Fit width`, icon: IconArrowAutofitWidth },
    { value: 'screen', label: t`Fit screen`, icon: IconArrowsMaximize },
    { value: 'original', label: t`Original size (1:1)`, icon: IconSquare },
  ] as const
  const fitName = fits.find((fit) => fit.value === prefs.fit)?.label ?? t`Fit`
  const filters = [
    { value: 'none', label: t`None` },
    { value: 'grayscale', label: t`Gray` },
    { value: 'sepia', label: t`Sepia` },
    { value: 'invert', label: t`Invert` },
  ] as const
  const toggles: { key: Toggle; label: string; hint?: string }[] = [
    { key: 'tapZones', label: t`Tap zones`, hint: t`Tap the page edges to turn pages. In Continuous, tap the top or bottom to move a screen` },
    { key: 'showPageNumber', label: t`Show page number` },
    { key: 'chapterBanner', label: t`Show the chapter name`, hint: t`Briefly, when a new chapter opens` },
    { key: 'autoNextChapter', label: t`Auto-advance to next chapter` },
    { key: 'autoWebtoon', label: t`Scroll webtoon chapters`, hint: t`Chapters of very tall pages open in Continuous` },
    { key: 'notesBeside', label: t`Notes beside the page`, hint: t`When the screen has room for them` },
    ...(wakeLockSupported() ? [{ key: 'keepAwake' as const, label: t`Keep the screen awake` }] : []),
  ]

  const wideLabel = { off: t`Whole`, split: t`Split`, rotate: t`Rotate` }[prefs.splitWide]
  const backgroundName = prefs.background === BACKGROUNDS.oled ? t`OLED black` : t`Dark`
  const lookSummary = prefs.cropBorders ? t`${backgroundName}, cropped` : backgroundName
  const levelName = renderLabel(ENHANCE_LEVELS.find((level) => level.value === prefs.enhance)?.label ?? '')
  const methodInfo = enhanceMethodInfo(method)
  const methodName = methodInfo ? renderLabel(methodInfo.label) : ''
  const enhanceSummary = !enhanceOn
    ? t`Off`
    : skippedHere
      ? t`Off for this chapter`
      : methodName
        ? t`${levelName}, ${methodName}`
        : levelName
  const togglesOn = toggles.filter((toggle) => prefs[toggle.key]).length
  const togglesTotal = toggles.length

  const row = (target: View, Icon: typeof IconAperture, label: string, value: string) => (
    <UnstyledButton
      ref={(node) => {
        rowRefs.current[target] = node
      }}
      className="reader-settings-row"
      onClick={() => open(target)}
    >
      <Icon size={16} className="reader-settings-row-icon" aria-hidden />
      <Text fz="sm" style={{ flex: 1 }}>
        {label}
      </Text>
      <Text fz="xs" c="var(--ink-3)" truncate maw={140}>
        {value}
      </Text>
      <IconChevronRight size={16} color="var(--ink-3)" aria-hidden />
    </UnstyledButton>
  )

  return (
    <Popover
      width={320}
      position="top-end"
      withArrow
      shadow="md"
      zIndex={OVERLAY_Z}
      opened={opened}
      onChange={onOpenedChange}
      closeOnEscape={false}
    >
      <Popover.Target>
        <ActionIcon
          variant={opened ? 'light' : 'subtle'}
          color="gray"
          onClick={() => onOpenedChange(!opened)}
          aria-label={t`Reader settings`}
        >
          <IconSettings size={20} />
        </ActionIcon>
      </Popover.Target>
      {/* Taller than a landscape phone or a short window: it scrolls rather than running off the top.
          The scroller sits inside the dropdown so the arrow, which hangs below it, is not overflow. */}
      <Popover.Dropdown className="reader-settings" p={0}>
        <div className="reader-settings-scroll">
          <DrillPanes page={view} depth={view === 'main' ? 0 : 1} bleed={8}>
            {view === 'main' && (
              <Stack gap="md">
                <div>
                  <Group gap={8} wrap="nowrap">
                    <Menu
                      opened={profileMenu}
                      onChange={setProfileMenu}
                      position="bottom-start"
                      width={260}
                      offset={6}
                      {...NESTED_COMBOBOX}
                    >
                      <Menu.Target>
                        <UnstyledButton
                          className="reader-settings-chip"
                          data-expanded={profileMenu || undefined}
                          aria-label={t`Reading profile: ${profileName}`}
                        >
                          <IconUserCircle size={16} aria-hidden />
                          <Text fz="xs" fw={600} c="inherit" truncate>
                            {profileName}
                          </Text>
                          <IconChevronDown size={12} aria-hidden className="reader-settings-chip-caret" />
                        </UnstyledButton>
                      </Menu.Target>
                      <Menu.Dropdown className="reader-settings-profiles">
                        <Menu.Label>
                          <Trans>Reading profile</Trans>
                        </Menu.Label>
                        <Menu.RadioGroup
                          value={String(selection)}
                          onChange={(value) => onSelection(value === 'auto' || value === 'series' ? value : Number(value))}
                        >
                          {profileData.map((option) => (
                            <Menu.RadioItem key={option.value} value={option.value} closeMenuOnClick>
                              {option.label}
                            </Menu.RadioItem>
                          ))}
                        </Menu.RadioGroup>
                      </Menu.Dropdown>
                    </Menu>
                    <Tooltip
                      label={incognito ? t`Incognito, this session isn't being recorded` : t`Don't record this session`}
                      withArrow
                      zIndex={OVERLAY_Z + 1}
                    >
                      <Button
                        className="reader-settings-pill"
                        size="xs"
                        radius="xl"
                        variant={incognito ? 'filled' : 'default'}
                        leftSection={<IconEyeOff size={16} />}
                        aria-pressed={incognito}
                        onClick={() => onIncognito(!incognito)}
                      >
                        <Trans>Incognito</Trans>
                      </Button>
                    </Tooltip>
                  </Group>
                  <Text className="reader-settings-scope" fz="xs" c="var(--ink-3)" mt={6}>
                    {editsAffect}
                  </Text>
                </div>

                <div>
                  <div role="radiogroup" aria-label={t`Layout`} className="reader-settings-tiles">
                    {layouts.map((layout) => (
                      <label key={layout.value} className="reader-settings-tile" data-checked={prefs.mode === layout.value || undefined}>
                        <input
                          className="reader-settings-radio"
                          type="radio"
                          name="reader-layout"
                          value={layout.value}
                          checked={prefs.mode === layout.value}
                          onChange={() => onPrefs({ mode: layout.value })}
                        />
                        <LayoutGlyph mode={layout.value} />
                        <Text fz="xs" fw={prefs.mode === layout.value ? 600 : 500} c="inherit">
                          {layout.label}
                        </Text>
                      </label>
                    ))}
                  </div>
                  {autoLayout && (
                    <Text fz="xs" c="var(--ink-3)" mt={6}>
                      <Trans>Continuous, because this chapter's pages are very tall.</Trans>
                    </Text>
                  )}
                </div>

                <div>
                  <div className="reader-settings-pair">
                    <div>
                      <FieldLabel>
                        <Trans>Direction</Trans>
                      </FieldLabel>
                      <SegmentedControl
                        aria-label={t`Direction`}
                        size="xs"
                        value={prefs.direction}
                        onChange={(value) => onPrefs({ direction: value as ReaderPrefs['direction'] })}
                        data={[
                          {
                            value: 'rtl',
                            label: (
                              <>
                                <VisuallyHidden>{t`Right to left`}</VisuallyHidden>
                                <span aria-hidden>
                                  {'← '}
                                  {t({ message: 'RTL', comment: 'Short for "right to left", the reading direction' })}
                                </span>
                              </>
                            ),
                          },
                          {
                            value: 'ltr',
                            label: (
                              <>
                                <VisuallyHidden>{t`Left to right`}</VisuallyHidden>
                                <span aria-hidden>
                                  {t({ message: 'LTR', comment: 'Short for "left to right", the reading direction' })}
                                  {' →'}
                                </span>
                              </>
                            ),
                          },
                        ]}
                      />
                    </div>
                    <div>
                      {/* The segments are icons; naming the current one here is the label a touch screen never gets from the tooltips. */}
                      <FieldLabel>{fitName}</FieldLabel>
                      <SegmentedControl
                        aria-label={t`Fit`}
                        size="xs"
                        value={prefs.fit}
                        onChange={(value) => onPrefs({ fit: value as ReaderPrefs['fit'] })}
                        data={fits.map((fit) => ({
                          value: fit.value,
                          label: (
                            <Tooltip label={fit.label} withArrow zIndex={OVERLAY_Z + 1}>
                              <span className="reader-settings-fit">
                                <fit.icon size={16} aria-hidden />
                                <VisuallyHidden>{fit.label}</VisuallyHidden>
                              </span>
                            </Tooltip>
                          ),
                        }))}
                      />
                    </div>
                  </div>
                  {prefs.fit === 'original' && (
                    <div style={{ marginTop: 10 }}>
                      <FieldLabel>
                        <Trans>Scale ({scale}%)</Trans>
                      </FieldLabel>
                      <Slider
                        thumbLabel={t`Scale`}
                        size="xs"
                        min={25}
                        max={400}
                        step={5}
                        value={prefs.scale}
                        onChange={(value) => onPrefs({ scale: value })}
                      />
                    </div>
                  )}
                </div>

                <div>
                  <Group justify="space-between" wrap="nowrap" mb={8}>
                    <Text fz="xs" fw={600} c="var(--ink-3)">
                      <Trans>Brightness and tint</Trans>
                    </Text>
                    <div role="radiogroup" aria-label={t`Colour filter`} className="reader-settings-swatches">
                      {filters.map((filter) => (
                        <Tooltip key={filter.value} label={filter.label} withArrow zIndex={OVERLAY_Z + 1}>
                          <label className="reader-settings-swatch" data-checked={prefs.filter === filter.value || undefined}>
                            <input
                              className="reader-settings-radio"
                              type="radio"
                              name="reader-filter"
                              value={filter.value}
                              aria-label={filter.label}
                              checked={prefs.filter === filter.value}
                              onChange={() => onPrefs({ filter: filter.value })}
                            />
                            <span
                              aria-hidden
                              className="reader-settings-swatch-fill"
                              style={{
                                backgroundImage: previewSrc ? `url("${previewSrc}")` : undefined,
                                filter: toneFilter({ dim: 0, filter: filter.value }),
                              }}
                            />
                          </label>
                        </Tooltip>
                      ))}
                    </div>
                  </Group>
                  <Group gap={8} wrap="nowrap">
                    <IconMoon size={16} color="var(--ink-3)" aria-hidden />
                    <Slider
                      style={{ flex: 1 }}
                      thumbLabel={t`Brightness`}
                      size="xs"
                      min={20}
                      max={100}
                      step={5}
                      label={(value) => `${value}%`}
                      value={100 - prefs.dim}
                      onChange={(value) => onPrefs({ dim: 100 - value })}
                    />
                    <IconSun size={16} color="var(--ink-3)" aria-hidden />
                  </Group>
                </div>

                <Stack gap={0} className="reader-settings-rows">
                  {prefs.mode === 'paged' && row('wide', IconViewportWide, t`Wide pages`, wideLabel)}
                  {row('look', IconPalette, t`Page look`, lookSummary)}
                  {row('enhance', IconAperture, t`Enhance`, enhanceSummary)}
                  {row('reading', IconListCheck, t`While reading`, t`${togglesOn} of ${togglesTotal} on`)}
                </Stack>
              </Stack>
            )}


            {view === 'wide' && (
              <SubPage title={t`Wide pages`} onBack={back} backRef={backRef}>
                <SegmentedControl
                  aria-label={t`Wide pages`}
                  fullWidth
                  size="xs"
                  value={prefs.splitWide}
                  onChange={(value) => onPrefs({ splitWide: value as ReaderPrefs['splitWide'] })}
                  data={[
                    { label: t`Whole`, value: 'off' },
                    { label: t`Split`, value: 'split' },
                    { label: t`Rotate`, value: 'rotate' },
                  ]}
                />
                <Text fz="xs" c="var(--ink-3)" mt={8}>
                  <Trans>How a two-page spread shows in the Single layout.</Trans>
                </Text>
              </SubPage>
            )}

            {view === 'look' && (
              <SubPage title={t`Page look`} onBack={back} backRef={backRef}>
                <Stack gap="md">
                  <div>
                    <FieldLabel>
                      <Trans>Background</Trans>
                    </FieldLabel>
                    <SegmentedControl
                      aria-label={t`Background`}
                      fullWidth
                      size="xs"
                      value={prefs.background === BACKGROUNDS.oled ? 'oled' : 'dark'}
                      onChange={(value) =>
                        onPrefs({ background: value === 'oled' ? BACKGROUNDS.oled : BACKGROUNDS.dark })
                      }
                      data={[
                        { label: t`Dark`, value: 'dark' },
                        { label: t`OLED black`, value: 'oled' },
                      ]}
                    />
                  </div>
                  <Switch
                    size="xs"
                    labelPosition="left"
                    label={t`Crop borders`}
                    description={t`Trims plain white or black margins. Single and Double only.`}
                    checked={prefs.cropBorders}
                    onChange={(event) => onPrefs({ cropBorders: event.currentTarget.checked })}
                    classNames={{ body: 'reader-settings-switch', description: 'reader-settings-hint' }}
                  />
                </Stack>
              </SubPage>
            )}

            {view === 'enhance' && (
              <SubPage title={t`Enhance`} onBack={back} backRef={backRef}>
                <Stack gap="sm">
                  <div>
                    <SegmentedControl
                      aria-label={t`Enhance`}
                      fullWidth
                      size="xs"
                      value={prefs.enhance}
                      onChange={(value) => onPrefs({ enhance: value as ReaderPrefs['enhance'] })}
                      data={ENHANCE_LEVELS.map((level) => ({ label: renderLabel(level.label), value: level.value }))}
                    />
                    {skippedHere && (
                      <Text fz="xs" c="var(--ink-3)" mt={4}>
                        <Trans>Off for this chapter because of its quality.</Trans>
                      </Text>
                    )}
                  </div>
                  {enhanceOn && (
                    <div>
                      <FieldLabel>
                        <Trans>Method</Trans>
                      </FieldLabel>
                      <Select
                        aria-label={t`Method`}
                        size="xs"
                        comboboxProps={NESTED_COMBOBOX}
                        allowDeselect={false}
                        value={method}
                        onChange={(value) => value && onPrefs({ enhanceMethod: value as ReaderPrefs['enhanceMethod'] })}
                        data={enhanceOptions}
                        renderOption={({ option }) => (
                          <div>
                            <Text fz="xs">{option.label}</Text>
                            <Text fz="xs" c="var(--ink-3)">
                              {enhanceNote(option.value)}
                            </Text>
                          </div>
                        )}
                      />
                    </div>
                  )}
                  {enhanceOn && isServerMethod(method) && (
                    <div>
                      <FieldLabel>
                        <Trans>While the server works</Trans>
                      </FieldLabel>
                      <SegmentedControl
                        aria-label={t`While the server works`}
                        fullWidth
                        size="xs"
                        value={prefs.enhanceFallback}
                        onChange={(value) => onPrefs({ enhanceFallback: value as ReaderPrefs['enhanceFallback'] })}
                        data={ENHANCE_FALLBACKS.map((f) => ({
                          label: renderLabel(f.label),
                          value: f.value,
                          disabled: browserUnsupported && f.value !== 'off',
                        }))}
                      />
                    </div>
                  )}
                  {enhanceOn && isServerMethod(method) && (
                    <div>
                      <FieldLabel>
                        <Trans>Upscale progress</Trans>
                      </FieldLabel>
                      <SegmentedControl
                        aria-label={t`Upscale progress`}
                        fullWidth
                        size="xs"
                        value={prefs.enhanceProgress}
                        onChange={(value) => onPrefs({ enhanceProgress: value as ReaderPrefs['enhanceProgress'] })}
                        data={ENHANCE_PROGRESS_STYLES.map((style) => ({ label: renderLabel(style.label), value: style.value }))}
                      />
                      <Text fz="xs" c="var(--ink-3)" mt={4}>
                        <Trans>While the bars are hidden. The toolbar always shows it.</Trans>
                      </Text>
                    </div>
                  )}
                  {enhanceOn && isServerMethod(method) && (
                    <Switch
                      size="xs"
                      labelPosition="left"
                      label={t`Whole chapter`}
                      description={t`Keep upscaling every page while the chapter is open. Leaving cancels the rest.`}
                      checked={prefs.enhanceWholeChapter}
                      onChange={(event) => onPrefs({ enhanceWholeChapter: event.currentTarget.checked })}
                      classNames={{ body: 'reader-settings-switch', description: 'reader-settings-hint' }}
                    />
                  )}
                  {enhanceOn && (
                    <div>
                      <FieldLabel>
                        <Trans>Don't enhance</Trans>
                      </FieldLabel>
                      <MultiSelect
                        aria-label={t`Don't enhance`}
                        size="xs"
                        comboboxProps={NESTED_COMBOBOX}
                        placeholder={t`Every quality is enhanced`}
                        // Hidden rather than dropped once a pill is in: without one Mantine stops the field taking a click.
                        classNames={{ inputField: prefs.enhanceSkipTiers.length ? 'reader-settings-filled' : undefined }}
                        value={prefs.enhanceSkipTiers}
                        onChange={(value) => onPrefs({ enhanceSkipTiers: value as ReaderPrefs['enhanceSkipTiers'] })}
                        data={ENHANCE_SKIP_TIERS.map((tier) => ({ value: tier.value, label: renderLabel(tier.label) }))}
                      />
                    </div>
                  )}
                </Stack>
              </SubPage>
            )}

            {view === 'reading' && (
              <SubPage title={t`While reading`} onBack={back} backRef={backRef}>
                <Stack gap={12}>
                  {toggles.map((toggle) => (
                    <Switch
                      key={toggle.key}
                      size="xs"
                      labelPosition="left"
                      label={toggle.label}
                      description={toggle.hint}
                      checked={prefs[toggle.key]}
                      onChange={(event) => onPrefs({ [toggle.key]: event.currentTarget.checked })}
                      classNames={{ body: 'reader-settings-switch', description: 'reader-settings-hint' }}
                    />
                  ))}
                </Stack>
              </SubPage>
            )}
          </DrillPanes>
        </div>
      </Popover.Dropdown>
    </Popover>
  )
}
