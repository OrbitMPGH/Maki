import {
  ActionIcon,
  Button,
  Group,
  Menu,
  Popover,
  SegmentedControl,
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
  IconArrowRight,
  IconArrowsMaximize,
  IconBooks,
  IconCheck,
  IconChevronDown,
  IconEyeOff,
  IconHandTap,
  IconListCheck,
  IconMoon,
  IconPalette,
  IconSettings,
  IconAdjustmentsHorizontal,
  IconAperture,
  IconBan,
  IconGauge,
  IconServer,
  IconSquare,
  IconSun,
  IconUserCircle,
  IconViewportWide,
  IconWand,
  IconZoomIn,
} from '../../lib/icons'
import { Fragment, useEffect, useState, type ComponentProps, type ReactNode } from 'react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import type { PrefsSource, ReaderManifest } from '../../api/reader'
import type { ReadingProfile } from '../../api/readingProfiles'
import { wakeLockSupported } from '../../lib/wakeLock'
import { useLabel } from '../../i18n-context'
import { DrillPanes } from '../../components/ui/DrillPanes'
import { DrillBack, DrillChoices, DrillField, DrillRow, useDrillPages, type DrillChoice } from '../../components/ui/DrillPanel'
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
import { BACKGROUNDS, backgroundChoice, enhanceFor, toneFilter, type BackgroundChoice, type PrefsSelection, type ReaderPrefs } from './prefs'
import { useEnhanceEngine } from './enhanceRuntime'
import TapZoneSettings from './TapZoneSettings'
import { TAP_LAYOUTS } from './tapZones'

/**
 * The reader renders above Mantine's popover layer, so anything that portals to <body> has to
 * be lifted past it explicitly, otherwise it paints underneath the page and looks like the
 * control simply doesn't work.
 */
export const OVERLAY_Z = 500

// Inside the popover, a portalled list counts as outside it: the first touch on the list closed
// both, so it could never be scrolled. Rendered in place, fixed so the popover does not clip it.
const NESTED_COMBOBOX = { zIndex: OVERLAY_Z + 1, withinPortal: false, floatingStrategy: 'fixed' } as const

type View = 'main' | 'wide' | 'look' | 'enhance' | 'method' | 'skip' | 'chapters' | 'reading' | 'taps'

const PARENTS: Record<View, View> = {
  main: 'main',
  wide: 'main',
  look: 'main',
  enhance: 'main',
  method: 'enhance',
  skip: 'enhance',
  chapters: 'main',
  reading: 'main',
  taps: 'reading',
}

type Toggle =
  | 'tapZones'
  | 'showPageNumber'
  | 'chapterBanner'
  | 'autoNextChapter'
  | 'autoSkipRecap'
  | 'hideCredits'
  | 'autoWebtoon'
  | 'keepAwake'
  | 'notesBeside'
  | 'progressRail'

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
  return (
    <div>
      <DrillBack ref={backRef} className="reader-settings-back" title={title} onBack={onBack} />
      {children}
    </div>
  )
}

/** A drill field, pulled out to the panel's edges the way its rows are. */
function Field(props: ComponentProps<typeof DrillField>) {
  return (
    <div className="reader-settings-inline-row">
      <DrillField {...props} />
    </div>
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
  onTapZonesShown,
  hiddenCredits,
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
  /** Whether the tap zone controls are on screen, so the reader can draw the zones on the page. */
  onTapZonesShown?: (shown: boolean) => void
  /** Credit pages left out of the chapter on screen, and a way to show them for this visit. */
  hiddenCredits?: { count: number; onShow: () => void } | null
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const pages = useDrillPages<View>('main', PARENTS)
  const { page: view, open, back, backRef } = pages
  const [profileMenu, setProfileMenu] = useState(false)

  // Reset as the popover opens, not as it closes, so its closing fade doesn't show a page sliding.
  const [wasOpened, setWasOpened] = useState(opened)
  if (opened !== wasOpened) {
    setWasOpened(opened)
    if (opened) pages.reset()
    else setProfileMenu(false)
  }

  const tapZonesShown = opened && view === 'taps' && prefs.tapZones
  useEffect(() => {
    onTapZonesShown?.(tapZonesShown)
  }, [tapZonesShown, onTapZonesShown])
  useEffect(() => () => onTapZonesShown?.(false), [onTapZonesShown])

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
      else back()
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [opened, view, profileMenu, onOpenedChange, back])

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
  const browserMethods: DrillChoice[] = BROWSER_ENHANCE_METHODS.map((m) => ({
    value: m.id,
    label: renderLabel(m.label),
    hint: enhanceNote(m.id),
    disabled: browserUnsupported,
  }))
  const serverMethods: DrillChoice[] = SERVER_ENHANCE_METHODS.flatMap((m) => {
    const model = enhanceModels?.find((entry) => entry.id === serverModelId(m.id))
    if (!model && m.id !== method) return []
    return [{ value: m.id, label: renderLabel(m.label), hint: enhanceNote(m.id), disabled: !model || !model.ready }]
  })
  const pickMethod = (value: string) => {
    onPrefs({ enhanceMethod: value as ReaderPrefs['enhanceMethod'] })
    back()
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
  type ToggleRow = { key: Toggle; label: string; hint?: string }
  // Chapters: what happens as a chapter opens and ends. While reading: what is on screen meanwhile.
  const chapterToggles: ToggleRow[] = [
    { key: 'chapterBanner', label: t`Show the chapter name`, hint: t`Briefly, when a new chapter opens` },
    { key: 'autoNextChapter', label: t`Auto-advance to next chapter` },
    { key: 'autoSkipRecap', label: t`Skip recaps`, hint: t`In Continuous, jump past the end of the previous chapter when a chapter repeats it` },
    { key: 'hideCredits', label: t`Hide credit pages`, hint: t`Leave out credit and recruitment pages that repeat at the start or end of chapters` },
    { key: 'autoWebtoon', label: t`Scroll webtoon chapters`, hint: t`Chapters of very tall pages open in Continuous` },
  ]
  const readingToggles: ToggleRow[] = [
    { key: 'showPageNumber', label: t`Show page number` },
    { key: 'notesBeside', label: t`Notes beside the page`, hint: t`When the screen has room for them` },
    { key: 'progressRail', label: t`Progress rail`, hint: t`In Continuous, a bar down the right edge you can drag to move through the chapter` },
    ...(wakeLockSupported() ? [{ key: 'keepAwake' as const, label: t`Keep the screen awake` }] : []),
  ]
  const tapToggle: ToggleRow = {
    key: 'tapZones',
    label: t`Tap zones`,
    hint: t`Tap the page edges to turn pages. In Continuous, tap the top or bottom to move a screen`,
  }

  const wideLabel = { off: t`Whole`, split: t`Split`, rotate: t`Rotate` }[prefs.splitWide]
  const backgroundName = {
    dark: t`Dark`,
    oled: t`OLED black`,
    white: t`White`,
    grey: t`Grey`,
    auto: t`Auto`,
  }[backgroundChoice(prefs.background)]
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
  const skipSummary = prefs.enhanceSkipTiers.length
    ? ENHANCE_SKIP_TIERS.filter((tier) => prefs.enhanceSkipTiers.includes(tier.value))
        .map((tier) => renderLabel(tier.label))
        .join(', ')
    : t`Every quality is enhanced`
  const hiddenCount = prefs.hideCredits ? (hiddenCredits?.count ?? 0) : 0
  const onSummary = (toggles: ToggleRow[]) => {
    const togglesOn = toggles.filter((toggle) => prefs[toggle.key]).length
    const togglesTotal = toggles.length
    return t`${togglesOn} of ${togglesTotal} on`
  }
  const tapLayout = TAP_LAYOUTS.find((entry) => entry.value === prefs.tapLayout) ?? TAP_LAYOUTS[0]
  const tapSummary = !prefs.tapZones ? t`Off` : prefs.mode === 'vertical' ? t`On` : renderLabel(tapLayout.label)
  const renderToggles = (toggles: ToggleRow[]) =>
    toggles.map((toggle) => (
      <Fragment key={toggle.key}>
        <Switch
          size="xs"
          labelPosition="left"
          label={toggle.label}
          description={toggle.hint}
          checked={prefs[toggle.key]}
          onChange={(event) => onPrefs({ [toggle.key]: event.currentTarget.checked })}
          classNames={{ body: 'reader-settings-switch', description: 'reader-settings-hint' }}
        />
        {toggle.key === 'hideCredits' && hiddenCredits && hiddenCount > 0 && (
          <Group gap={8} wrap="nowrap" justify="space-between" mt={-6}>
            <Text className="reader-settings-hint" fz="xs">
              <Plural value={hiddenCount} one="# page hidden in this chapter" other="# pages hidden in this chapter" />
            </Text>
            <Button size="compact-xs" variant="subtle" onClick={hiddenCredits.onShow}>
              <Trans>Show</Trans>
            </Button>
          </Group>
        )}
      </Fragment>
    ))

  const row = (target: View, Icon: typeof IconAperture, label: string, value: string) => (
    <DrillRow ref={pages.rowRef(target)} icon={Icon} label={label} value={value} onClick={() => open(target)} />
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
      <Popover.Dropdown className="reader-settings reader-dark" data-mantine-color-scheme="dark" p={0}>
        <div className="reader-settings-scroll">
          <DrillPanes page={view} depth={pages.depth} bleed={8}>
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
                      <Menu.Dropdown className="reader-settings-profiles reader-dark" data-mantine-color-scheme="dark">
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

                <Stack gap={0}>
                  <Field icon={IconArrowRight} title={t`Direction`}>
                    <SegmentedControl
                      aria-label={t`Direction`}
                      fullWidth
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
                  </Field>
                  {/* The segments are icons; naming the current one here is the label a touch screen never gets from the tooltips. */}
                  <Field icon={IconArrowsMaximize} title={fitName}>
                    <SegmentedControl
                      aria-label={t`Fit`}
                      fullWidth
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
                  </Field>
                  {prefs.fit === 'original' && (
                    <Field icon={IconZoomIn} title={<Trans>Scale ({scale}%)</Trans>}>
                      <Slider
                        thumbLabel={t`Scale`}
                        size="xs"
                        min={25}
                        max={400}
                        step={5}
                        value={prefs.scale}
                        onChange={(value) => onPrefs({ scale: value })}
                      />
                    </Field>
                  )}
                </Stack>

                <Field icon={IconAdjustmentsHorizontal} title={<Trans>Brightness and tint</Trans>}>
                  <Group justify="flex-end" wrap="nowrap" mb={8}>
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
                </Field>

                <Stack gap={0} className="reader-settings-rows">
                  {prefs.mode === 'paged' && row('wide', IconViewportWide, t`Wide pages`, wideLabel)}
                  {row('look', IconPalette, t`Page look`, lookSummary)}
                  {row('enhance', IconAperture, t`Enhance`, enhanceSummary)}
                  {row('chapters', IconBooks, t`Chapters`, onSummary(chapterToggles))}
                  {row('reading', IconListCheck, t`While reading`, onSummary(readingToggles))}
                </Stack>
              </Stack>
            )}


            {view === 'wide' && (
              <SubPage title={t`Wide pages`} onBack={back} backRef={backRef}>
                <Field icon={IconViewportWide} title={t`Wide pages`} help={<Trans>How a two-page spread shows in the Single layout.</Trans>}>
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
                </Field>
              </SubPage>
            )}

            {view === 'look' && (
              <SubPage title={t`Page look`} onBack={back} backRef={backRef}>
                <Stack gap="md">
                  <Field
                    icon={IconPalette}
                    title={<Trans>Background</Trans>}
                    help={
                      prefs.background === BACKGROUNDS.auto ? <Trans>Takes the colour of the page's edge.</Trans> : undefined
                    }
                  >
                    <SegmentedControl
                      aria-label={t`Background`}
                      fullWidth
                      size="xs"
                      value={backgroundChoice(prefs.background)}
                      onChange={(value) => onPrefs({ background: BACKGROUNDS[value as BackgroundChoice] })}
                      data={[
                        { label: t`Dark`, value: 'dark' },
                        { label: t`OLED black`, value: 'oled' },
                        { label: t`White`, value: 'white' },
                        { label: t`Grey`, value: 'grey' },
                        { label: t`Auto`, value: 'auto' },
                      ]}
                    />
                  </Field>
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
                  <Field
                    icon={IconAperture}
                    title={<Trans>Enhance</Trans>}
                    help={skippedHere ? <Trans>Off for this chapter because of its quality.</Trans> : undefined}
                  >
                    <SegmentedControl
                      aria-label={t`Enhance`}
                      fullWidth
                      size="xs"
                      value={prefs.enhance}
                      onChange={(value) => onPrefs({ enhance: value as ReaderPrefs['enhance'] })}
                      data={ENHANCE_LEVELS.map((level) => ({ label: renderLabel(level.label), value: level.value }))}
                    />
                  </Field>
                  {enhanceOn && (
                    <div className="reader-settings-inline-row">{row('method', IconWand, t`Method`, methodName)}</div>
                  )}
                  {enhanceOn && isServerMethod(method) && (
                    <Field icon={IconServer} title={<Trans>While the server works</Trans>}>
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
                    </Field>
                  )}
                  {enhanceOn && isServerMethod(method) && (
                    <Field
                      icon={IconGauge}
                      title={<Trans>Upscale progress</Trans>}
                      help={<Trans>While the bars are hidden. The toolbar always shows it.</Trans>}
                    >
                      <SegmentedControl
                        aria-label={t`Upscale progress`}
                        fullWidth
                        size="xs"
                        value={prefs.enhanceProgress}
                        onChange={(value) => onPrefs({ enhanceProgress: value as ReaderPrefs['enhanceProgress'] })}
                        data={ENHANCE_PROGRESS_STYLES.map((style) => ({ label: renderLabel(style.label), value: style.value }))}
                      />
                    </Field>
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
                    <div className="reader-settings-inline-row">{row('skip', IconBan, t`Don't enhance`, skipSummary)}</div>
                  )}
                </Stack>
              </SubPage>
            )}

            {view === 'skip' && (
              <SubPage title={t`Don't enhance`} onBack={back} backRef={backRef}>
                <div className="reader-settings-choices" role="group" aria-label={t`Don't enhance`}>
                  {ENHANCE_SKIP_TIERS.map((tier) => {
                    const checked = prefs.enhanceSkipTiers.includes(tier.value)
                    return (
                      <UnstyledButton
                        key={tier.value}
                        role="checkbox"
                        aria-checked={checked}
                        data-checked={checked || undefined}
                        className="drill-choice"
                        onClick={() =>
                          onPrefs({
                            enhanceSkipTiers: checked
                              ? prefs.enhanceSkipTiers.filter((value) => value !== tier.value)
                              : [...prefs.enhanceSkipTiers, tier.value],
                          })
                        }
                      >
                        <span className="drill-choice-check" aria-hidden>
                          {checked && <IconCheck size={16} />}
                        </span>
                        <span className="drill-choice-text">
                          <span className="drill-choice-label">{renderLabel(tier.label)}</span>
                        </span>
                      </UnstyledButton>
                    )
                  })}
                </div>
              </SubPage>
            )}

            {view === 'method' && (
              <SubPage title={t`Method`} onBack={back} backRef={backRef}>
                <div className="reader-settings-choices">
                  <DrillChoices
                    label={t`On this device`}
                    heading={<Trans>On this device</Trans>}
                    value={method}
                    options={browserMethods}
                    onPick={pickMethod}
                  />
                  {serverMethods.length > 0 && (
                    <DrillChoices
                      label={t`On the server`}
                      heading={<Trans>On the server</Trans>}
                      value={method}
                      options={serverMethods}
                      onPick={pickMethod}
                    />
                  )}
                </div>
              </SubPage>
            )}

            {view === 'chapters' && (
              <SubPage title={t`Chapters`} onBack={back} backRef={backRef}>
                <Stack gap={12}>{renderToggles(chapterToggles)}</Stack>
              </SubPage>
            )}

            {view === 'reading' && (
              <SubPage title={t`While reading`} onBack={back} backRef={backRef}>
                <Stack gap={12}>
                  <div className="reader-settings-inline-row">{row('taps', IconHandTap, t`Tap zones`, tapSummary)}</div>
                  {renderToggles(readingToggles)}
                </Stack>
              </SubPage>
            )}

            {view === 'taps' && (
              <SubPage title={t`Tap zones`} onBack={back} backRef={backRef}>
                <Stack gap={12}>
                  {renderToggles([tapToggle])}
                  {prefs.tapZones && (
                    <TapZoneSettings
                      prefs={prefs}
                      onPrefs={onPrefs}
                      vertical={prefs.mode === 'vertical'}
                      hintClassName="reader-settings-hint"
                      bleed
                    />
                  )}
                </Stack>
              </SubPage>
            )}
          </DrillPanes>
        </div>
      </Popover.Dropdown>
    </Popover>
  )
}
