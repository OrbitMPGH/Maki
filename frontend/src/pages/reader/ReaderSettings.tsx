import {
  ActionIcon,
  Button,
  Group,
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
  IconChevronRight,
  IconEyeOff,
  IconListCheck,
  IconMoon,
  IconPalette,
  IconSettings,
  IconSparkles,
  IconSquare,
  IconSun,
  IconUserCircle,
  IconViewportWide,
} from '@tabler/icons-react'
import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Trans, useLingui } from '@lingui/react/macro'
import type { PrefsSource, ReaderManifest } from '../../api/reader'
import type { ReadingProfile } from '../../api/readingProfiles'
import { wakeLockSupported } from '../../lib/wakeLock'
import { useLabel } from '../../i18n-context'
import {
  BROWSER_ENHANCE_METHODS,
  ENHANCE_FALLBACKS,
  ENHANCE_LEVELS,
  ENHANCE_SKIP_TIERS,
  SERVER_ENHANCE_METHODS,
  enhanceMethodInfo,
  isServerMethod,
  serverModelId,
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

type View = 'main' | 'profile' | 'wide' | 'look' | 'enhance' | 'reading'

type Toggle = 'tapZones' | 'showPageNumber' | 'chapterBanner' | 'autoNextChapter' | 'autoWebtoon' | 'keepAwake'

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
      <Group gap={6} mb="sm" wrap="nowrap">
        <ActionIcon ref={backRef} variant="subtle" color="gray" size="sm" onClick={onBack} aria-label={t`Back`}>
          <IconArrowLeft size={16} />
        </ActionIcon>
        <Text fz="sm" fw={650}>
          {title}
        </Text>
      </Group>
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

  // Opening a page puts focus on its back button; going back returns it to the row that opened it.
  const backRef = useRef<HTMLButtonElement>(null)
  const rowRefs = useRef<Partial<Record<View, HTMLButtonElement | null>>>({})
  const cameFrom = useRef<View | null>(null)
  useEffect(() => {
    if (view !== 'main') backRef.current?.focus()
    else if (cameFrom.current) rowRefs.current[cameFrom.current]?.focus()
  }, [view])
  const open = (next: View) => {
    cameFrom.current = next
    setView(next)
  }
  const back = () => setView('main')

  useEffect(() => {
    if (!opened) {
      cameFrom.current = null
      setView('main')
    }
  }, [opened])

  // Mantine only closes on Escape when focus is inside the dropdown, and focus is usually still on
  // the gear, so the reader's own Escape (leave the reader) would win. Captured on the window so it
  // runs first, and Mantine's own is off so that on a page inside the popover Escape only steps back.
  useEffect(() => {
    if (!opened) return
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return
      event.preventDefault()
      if (view === 'main') onOpenedChange(false)
      else setView('main')
    }
    window.addEventListener('keydown', onKey, true)
    return () => window.removeEventListener('keydown', onKey, true)
  }, [opened, view, onOpenedChange])

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
  const editsAffect =
    source === 'Series'
      ? t`Changes apply to this series only.`
      : source === 'Profile' && inForce
        ? t`Changes retune "${inForceName}", so every series using it.`
        : t`Changes apply to your reader defaults.`

  const { scale } = prefs
  const enhanceOn = prefs.enhance !== 'off'
  const browserUnsupported = useEnhanceEngine(enhanceOn).status === 'unsupported'
  const enhanceModels = manifest.enhanceServer?.models
  const enhanceOptions = [
    ...BROWSER_ENHANCE_METHODS.map((m) => ({ value: m.id, label: renderLabel(m.label), disabled: browserUnsupported })),
    ...SERVER_ENHANCE_METHODS.flatMap((m) => {
      const model = enhanceModels?.find((entry) => entry.id === serverModelId(m.id))
      if (!model && m.id !== prefs.enhanceMethod) return []
      return [{ value: m.id, label: renderLabel(m.label), disabled: !model || !model.ready }]
    }),
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
  const filters = [
    { value: 'none', label: t`None` },
    { value: 'grayscale', label: t`Gray` },
    { value: 'sepia', label: t`Sepia` },
    { value: 'invert', label: t`Invert` },
  ] as const
  const toggles: { key: Toggle; label: string; hint?: string }[] = [
    { key: 'tapZones', label: t`Tap zones`, hint: t`Tap the page edges to turn pages` },
    { key: 'showPageNumber', label: t`Show page number` },
    { key: 'chapterBanner', label: t`Show the chapter name`, hint: t`Briefly, when a new chapter opens` },
    { key: 'autoNextChapter', label: t`Auto-advance to next chapter` },
    { key: 'autoWebtoon', label: t`Scroll webtoon chapters`, hint: t`Chapters of very tall pages open in Continuous` },
    ...(wakeLockSupported() ? [{ key: 'keepAwake' as const, label: t`Keep the screen awake` }] : []),
  ]

  const wideLabel = { off: t`Whole`, split: t`Split`, rotate: t`Rotate` }[prefs.splitWide]
  const backgroundName = prefs.background === BACKGROUNDS.oled ? t`OLED black` : t`Dark`
  const lookSummary = prefs.cropBorders ? t`${backgroundName}, cropped` : backgroundName
  const levelName = renderLabel(ENHANCE_LEVELS.find((level) => level.value === prefs.enhance)?.label ?? '')
  const methodInfo = enhanceMethodInfo(prefs.enhanceMethod)
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

  const row = (target: View, Icon: typeof IconSparkles, label: string, value: string) => (
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
      <IconChevronRight size={14} color="var(--ink-3)" aria-hidden />
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
          <IconSettings size={18} />
        </ActionIcon>
      </Popover.Target>
      {/* Taller than a landscape phone or a short window: it scrolls rather than running off the top. */}
      <Popover.Dropdown className="reader-settings" style={{ maxHeight: 'calc(100dvh - 72px)', overflowY: 'auto' }}>
        {view === 'main' && (
          <Stack gap="md">
            <Group gap={8} wrap="nowrap">
              <UnstyledButton
                ref={(node) => {
                  rowRefs.current.profile = node
                }}
                className="reader-settings-chip"
                onClick={() => open('profile')}
                aria-label={t`Reading profile: ${profileName}`}
              >
                <IconUserCircle size={16} aria-hidden />
                <Text fz="xs" fw={600} c="inherit" truncate>
                  {profileName}
                </Text>
                <IconChevronRight size={12} aria-hidden style={{ marginInlineStart: 'auto', flexShrink: 0 }} />
              </UnstyledButton>
              <Tooltip
                label={incognito ? t`Incognito, this session isn't being recorded` : t`Don't record this session`}
                withArrow
                zIndex={OVERLAY_Z + 1}
              >
                <Button
                  size="compact-xs"
                  radius="xl"
                  variant={incognito ? 'filled' : 'default'}
                  leftSection={<IconEyeOff size={14} />}
                  aria-pressed={incognito}
                  onClick={() => onIncognito(!incognito)}
                >
                  <Trans>Incognito</Trans>
                </Button>
              </Tooltip>
            </Group>

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
                  <FieldLabel>
                    <Trans>Fit</Trans>
                  </FieldLabel>
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
                <IconMoon size={14} color="var(--ink-3)" aria-hidden />
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
                <IconSun size={14} color="var(--ink-3)" aria-hidden />
              </Group>
            </div>

            <Stack gap={0} className="reader-settings-rows">
              {prefs.mode === 'paged' && row('wide', IconViewportWide, t`Wide pages`, wideLabel)}
              {row('look', IconPalette, t`Page look`, lookSummary)}
              {row('enhance', IconSparkles, t`Enhance`, enhanceSummary)}
              {row('reading', IconListCheck, t`While reading`, t`${togglesOn} of ${togglesTotal} on`)}
            </Stack>
          </Stack>
        )}

        {view === 'profile' && (
          <SubPage title={t`Reading profile`} onBack={back} backRef={backRef}>
            <Select
              aria-label={t`Reading profile`}
              size="xs"
              comboboxProps={NESTED_COMBOBOX}
              allowDeselect={false}
              value={String(selection)}
              onChange={(value) => {
                if (!value) return
                onSelection(value === 'auto' || value === 'series' ? value : Number(value))
              }}
              data={profileData}
            />
            <Text fz="xs" c="var(--ink-3)" mt={8}>
              {editsAffect}
            </Text>
          </SubPage>
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
                classNames={{ body: 'reader-settings-switch' }}
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
                    value={prefs.enhanceMethod}
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
              {enhanceOn && isServerMethod(prefs.enhanceMethod) && (
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
                  classNames={{ body: 'reader-settings-switch' }}
                />
              ))}
            </Stack>
          </SubPage>
        )}
      </Popover.Dropdown>
    </Popover>
  )
}
