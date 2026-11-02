import { Link } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { Trans, useLingui } from '@lingui/react/macro'
import { Button, Group, Select, Stack, Switch, Text } from '@mantine/core'
import { IconLayoutDashboard } from '@tabler/icons-react'
import { SettingsSection } from './SettingsSection'
import { ContentRatingCards } from '../../components/ContentRatingCards'
import { useApplyLanguage, useLanguageOptions } from '../../components/ui/language'
import {
  useDiscoverSettings,
  useMetadataSettings,
  useSaveDiscoverSettings,
  useSaveUiSettings,
  useUiSettings,
  type SeriesSections,
  type UiSettings,
} from '../../api/hooks'
import { AppearancePicker } from '../../components/AppearancePicker'
import { prefersReducedMotion, useMotionPref } from '../../lib/motionPrefs'

export function DiscoverSection() {
  const { data: settings } = useDiscoverSettings()
  const save = useSaveDiscoverSettings()

  return (
    <SettingsSection
      id="discover-rating"
      title={<Trans>Content rating</Trans>}
      description={<Trans>The most explicit rating shown outside your own library.</Trans>}
    >
      <ContentRatingCards
        value={settings?.maxContentRating ?? 'erotica'}
        onChange={(rating) => save.mutate(rating)}
      />
    </SettingsSection>
  )
}


/**
 * The UI settings are one record with one PUT, so each control has to send the *whole* thing.
 * This hook keeps every call site honest about that: patch what changed, carry the rest over.
 * Returns null while the settings are still loading, which is the caller's cue to stay read-only
 * rather than save a half-known record.
 */
function useUiPatch(): ((patch: Partial<UiSettings>) => void) | null {
  const { data: ui } = useUiSettings()
  const save = useSaveUiSettings()
  const queryClient = useQueryClient()
  if (!ui) return null
  // Merge over the freshest cache, not `ui`: that's a render snapshot, and two patches fired
  // before the first refetch lands would otherwise have the second undo the first.
  return (patch) => {
    const current = queryClient.getQueryData<UiSettings>(['settings', 'ui']) ?? ui
    save.mutate({ ...current, ...patch })
  }
}

/**
 * Which page "/" opens on. Server-stored (unlike Appearance, which is per-browser), so it follows
 * the user across devices. Lives on the Home card because turning Home off is what changes it most.
 */
function StartPageSelect() {
  const { t } = useLingui()
  const { data: ui } = useUiSettings()
  const patch = useUiPatch()
  const { data: metadata } = useMetadataSettings()
  const discoverAvailable = Boolean(metadata?.dumpPresent)
  const homeEnabled = ui?.homeLayout.enabled ?? true

  return (
    <Select
      label={t`Start page`}
      data={[
        // Disabled rather than hidden, mirroring how the nav drops these tabs: offering a
        // choice that silently degrades to somewhere else is worse than saying why it's out.
        { value: 'home', label: t`Home`, disabled: !homeEnabled },
        { value: 'library', label: t`Library` },
        { value: 'discover', label: t`Discover`, disabled: !discoverAvailable },
      ]}
      value={ui?.startPage ?? 'home'}
      onChange={(value) => value && patch?.({ startPage: value as UiSettings['startPage'] })}
      disabled={!patch}
      allowDeselect={false}
      maw={260}
      mb="md"
    />
  )
}

/**
 * Which language the interface is drawn in.
 *
 * Sits directly above Title language because the two get confused, and the copy on both cards
 * exists to separate them: this one is the language of the app, that one is the language of the
 * metadata. Wanting Japanese titles inside a Swedish interface is ordinary, so neither derives from
 * the other.
 *
 * Server-stored, unlike Appearance: a translation is the sort of thing somebody wants on every
 * device they read on, not a per-browser choice. `localStorage` still holds a copy, but only so the
 * first paint does not have to wait for the settings round trip.
 */
export function LanguageSection() {
  const { t } = useLingui()
  const { data: ui } = useUiSettings()
  const options = useLanguageOptions()
  const apply = useApplyLanguage()

  return (
    <SettingsSection
      id="language"
      title={<Trans>Language</Trans>}
    >
      <Select
        aria-label={t`Interface language`}
        data={options}
        value={ui?.language ?? ''}
        onChange={(value) => value !== null && apply?.(value)}
        disabled={!apply}
        allowDeselect={false}
        maw={260}
      />
      <Text size="xs" c="var(--ink-3)" mt="sm">
        <Trans>Translations are machine-made and may be incomplete.</Trans>
      </Text>
    </SettingsSection>
  )
}

/**
 * Which language series titles are shown in.
 *
 * Deliberately display-only: it never touches `Series.Title`, which is what the folder on disk and
 * every file in it are named after, so one person's preference cannot rename another's library.
 * The visible cost is that sorting still follows the canonical (English) title.
 */
export function TitleLanguageSection() {
  const { t } = useLingui()
  const { data: ui } = useUiSettings()
  const patch = useUiPatch()

  // The languages MangaBaka actually tags primary titles with, plus "native" for the
  // original-script title, which carries no code of its own.
  const options = [
    { value: '', label: t`English (MangaBaka default)` },
    { value: 'native', label: t`Original script` },
    { value: 'ja', label: t`Japanese` },
    { value: 'ko', label: t`Korean` },
    { value: 'zh', label: t`Chinese` },
    { value: 'es', label: t`Spanish` },
    { value: 'fr', label: t`French` },
    { value: 'de', label: t`German` },
    { value: 'it', label: t`Italian` },
    { value: 'pt-br', label: t`Portuguese (Brazil)` },
    { value: 'ru', label: t`Russian` },
  ]

  // Stored as an ordered list, and English is appended as the fallback so a series with no title in
  // the chosen language reads as English rather than as whatever the provider happened to list.
  const stored = ui?.titleLanguage ?? ''
  const primary = stored.split(',')[0] ?? ''

  return (
    <SettingsSection
      id="title-language"
      title={<Trans>Title language</Trans>}
      description={<Trans>Folders and file names keep the English title.</Trans>}
    >
      <Select
        aria-label={t`Title language`}
        data={options}
        value={primary}
        onChange={(value) =>
          patch?.({ titleLanguage: !value || value === 'en' ? '' : `${value},en` })
        }
        disabled={!patch}
        allowDeselect={false}
        maw={260}
      />
    </SettingsSection>
  )
}

/**
 * The two supplementary rails on a series page. Both are extras around the chapter list and both
 * cost a catalogue query, so somebody who never uses them can turn them off and stop paying for them.
 */
export function SeriesPageSection() {
  const { t } = useLingui()
  const { data: ui } = useUiSettings()
  const patch = useUiPatch()
  const sections = ui?.seriesSections
  const related = sections?.related !== false
  const similar = sections?.similar !== false

  const write = (next: Partial<SeriesSections>) =>
    patch?.({ seriesSections: { related, similar, ...next } })

  return (
    <SettingsSection
      id="series-page"
      title={<Trans>Series page</Trans>}
    >
      <Stack gap="sm">
        <Switch
          checked={related}
          disabled={!patch}
          onChange={(e) => write({ related: e.currentTarget.checked })}
          label={t`Related series`}
        />
        <Switch
          checked={similar}
          disabled={!patch}
          onChange={(e) => write({ similar: e.currentTarget.checked })}
          label={t`More like this`}
          description={t`Needs semantic search.`}
        />
      </Stack>
    </SettingsSection>
  )
}

/**
 * Whether Home exists at all, and the way into the page layout editors. The sections themselves are
 * arranged on Home and Discover, in their edit mode, rather than from a list here.
 */
export function HomeSectionsSection() {
  const { t } = useLingui()
  const { data: ui } = useUiSettings()
  const { data: metadata } = useMetadataSettings()
  const patch = useUiPatch()
  const homeEnabled = ui?.homeLayout.enabled ?? true
  const discoverAvailable = Boolean(metadata?.dumpPresent)

  return (
    <SettingsSection
      id="home-screen"
      title={<Trans>Start page &amp; Home</Trans>}
    >
      <StartPageSelect />

      <Switch
        checked={homeEnabled}
        disabled={!patch || !ui}
        onChange={(e) =>
          ui && patch?.({ homeLayout: { ...ui.homeLayout, enabled: e.currentTarget.checked } })
        }
        label={t`Show the Home page`}
      />

      <Group gap="xs" mt="md">
        <Button
          component={Link}
          to="/home?edit=1"
          variant="default"
          leftSection={<IconLayoutDashboard size={16} />}
          disabled={!homeEnabled}
        >
          <Trans>Edit Home layout</Trans>
        </Button>
        {discoverAvailable && (
          <Button
            component={Link}
            to="/discover?edit=1"
            variant="default"
            leftSection={<IconLayoutDashboard size={16} />}
          >
            <Trans>Edit Discover layout</Trans>
          </Button>
        )}
      </Group>
    </SettingsSection>
  )
}

export function AppearanceSection() {
  return (
    <SettingsSection
      id="appearance"
      title={<Trans>Appearance</Trans>}
      description={<Trans>Saved on this device only.</Trans>}
    >
      <Stack gap={14}>
        <AppearancePicker />
        <MotionSwitches />
      </Stack>
    </SettingsSection>
  )
}

/** The cover only flies inside a page transition, and fast mode only paces those two, so both grey
 * out when transitions are off. */
function MotionSwitches() {
  const { t } = useLingui()
  const [transitions, setTransitions] = useMotionPref('pageTransitions')
  const [covers, setCovers] = useMotionPref('coverMorph')
  const [fast, setFast] = useMotionPref('fastMotion')
  const reduced = prefersReducedMotion()

  return (
    <div>
      <span className="appearance-picker-label">
        <Trans>Motion</Trans>
      </span>
      <Stack gap="sm">
        <Switch
          checked={transitions && !reduced}
          disabled={reduced}
          onChange={(e) => setTransitions(e.currentTarget.checked)}
          label={t`Page transitions`}
        />
        <Switch
          checked={transitions && covers && !reduced}
          disabled={reduced || !transitions}
          onChange={(e) => setCovers(e.currentTarget.checked)}
          label={t`Moving covers`}
        />
        <Switch
          checked={transitions && fast && !reduced}
          disabled={reduced || !transitions}
          onChange={(e) => setFast(e.currentTarget.checked)}
          label={t`Faster animations`}
        />
        {reduced && (
          <Text size="sm" c="dimmed">
            <Trans>Your device asks for reduced motion, so these stay off.</Trans>
          </Text>
        )}
      </Stack>
    </div>
  )
}

