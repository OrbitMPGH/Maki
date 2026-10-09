import { Link } from 'react-router-dom'
import { useQueryClient } from '@tanstack/react-query'
import { useLanguageChoice } from '../../i18n-context'
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

export function DiscoverSection() {
  const { data: settings } = useDiscoverSettings()
  const save = useSaveDiscoverSettings()

  return (
    <SettingsSection
      id="discover-rating"
      title={<Trans>Content rating</Trans>}
      description={
        <Trans>
          The most explicit rating shown to you in search, Discover, recommendations and the rails on
          a series page. Everything up to and including it is allowed. Your own library is never
          filtered.
        </Trans>
      }
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
  const discoverAvailable = Boolean(metadata?.useLocalDb && metadata?.dumpPresent)
  const homeEnabled = ui?.homeLayout.enabled ?? true

  return (
    <Select
      label={t`Start page`}
      description={t`On every device. Discover needs the local MangaBaka database, which an admin turns on under Library.`}
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
  const { locale, locales } = useLanguageChoice()
  const options = useLanguageOptions()
  const apply = useApplyLanguage()

  const currentLocaleLabel = locales.find((l) => l.code === locale)?.label ?? locale

  return (
    <SettingsSection
      id="language"
      title={<Trans>Language</Trans>}
      description={
        <Trans>
          The language of Maki's menus and text, on every device. Series titles have their own
          setting below.
        </Trans>
      }
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
        <Trans>
          Showing {currentLocaleLabel}. Non-English translations are machine-made and improving;
          anything untranslated shows in English.
        </Trans>
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
      description={
        <Trans>
          Which language series titles are shown in, when MangaBaka has one. Display only: folders,
          file names and sorting keep the English title.
        </Trans>
      }
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
      description={
        <Trans>
          Which rails appear below the chapter list. Turning one off also stops it being fetched.
        </Trans>
      }
    >
      <Stack gap="sm">
        <Switch
          checked={related}
          disabled={!patch}
          onChange={(e) => write({ related: e.currentTarget.checked })}
          label={t`Related series`}
          description={t`Sequels, prequels, spin-offs and side stories that MangaBaka has linked to this one.`}
        />
        <Switch
          checked={similar}
          disabled={!patch}
          onChange={(e) => write({ similar: e.currentTarget.checked })}
          label={t`More like this`}
          description={t`Titles that read alike, matched on feel rather than a declared relation. Needs semantic search, which an admin turns on under Library.`}
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
  const discoverAvailable = Boolean(metadata?.useLocalDb && metadata?.dumpPresent)

  return (
    <SettingsSection
      id="home-screen"
      title={<Trans>Start page &amp; Home</Trans>}
      description={
        <Trans>Which page Maki opens on, and whether the Home page exists at all.</Trans>
      }
    >
      <StartPageSelect />

      <Switch
        checked={homeEnabled}
        disabled={!patch || !ui}
        onChange={(e) =>
          ui && patch?.({ homeLayout: { ...ui.homeLayout, enabled: e.currentTarget.checked } })
        }
        label={t`Show the Home page`}
        description={t`Turn it off if you don't read in Maki: the tab disappears and Library becomes the start page.`}
      />

      <Text size="sm" c="var(--ink-3)" mt="md">
        <Trans>Sections, their order and your own rails are arranged on the pages themselves.</Trans>
      </Text>

      <Group gap="xs" mt="sm">
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
      description={
        <Trans>
          Background and accent colour. Remembered on this device only; everything else under
          Preferences follows you to every device.
        </Trans>
      }
    >
      <AppearancePicker />
    </SettingsSection>
  )
}

