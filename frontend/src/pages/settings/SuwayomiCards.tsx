import { useEffect, useMemo, useReducer, useState } from 'react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import {
  Alert, Button, Checkbox, Group, Loader, Modal, PasswordInput, Select, Stack, Switch, Text, TextInput,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { SettingsSection } from './SettingsSection'
import { savedToast } from './sharedRecord'
import { useSourceLanguages, useSources } from '../../api/hooks'
import { languageName } from '../../api/titles'
import { baseLanguage } from '../../sourceIcons'
import { ConfirmDialog } from '../../components/ui/ConfirmDialog'
import {
  useSaveSuwayomiConnection, useSaveSuwayomiSources, useSuwayomiConnection, useSuwayomiSources,
  useTestSuwayomiConnection, type SuwayomiAuthMode, type SuwayomiConnectionInput, type SuwayomiSourceRow,
} from '../../api/suwayomi'

/**
 * The optional Suwayomi connection. Extensions are installed in Suwayomi itself; this card only
 * says where it is, how to sign in, and which of its sources Maki may use.
 */
export function SuwayomiCard() {
  const { t } = useLingui()
  const { data: saved } = useSuwayomiConnection()
  const { data: sources } = useSources()
  const save = useSaveSuwayomiConnection()
  const test = useTestSuwayomiConnection()
  const [url, setUrl] = useState('')
  const [authMode, setAuthMode] = useState<SuwayomiAuthMode>('none')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [picking, setPicking] = useState(false)
  const [discarded, discard] = useReducer((n: number) => n + 1, 0)

  useEffect(() => {
    if (saved) {
      setUrl(saved.url ?? '')
      setAuthMode(saved.authMode)
      setUsername(saved.username ?? '')
      setPassword('')
    }
  }, [saved, discarded])

  const dirty =
    saved !== undefined &&
    (url !== (saved.url ?? '') ||
      authMode !== saved.authMode ||
      username !== (saved.username ?? '') ||
      password !== '')

  const input = (): SuwayomiConnectionInput => ({
    url: url.trim() || null,
    authMode,
    username: authMode === 'none' ? null : username.trim() || null,
    password: password === '' ? null : password,
  })

  const inUse = (sources ?? []).filter((s) => s.provider === 'suwayomi').length
  const connected = Boolean(saved?.url)

  return (
    <SettingsSection
      id="suwayomi"
      title="Suwayomi"
      description={t`Optional. Point Maki at a Suwayomi server to use the sources of the extensions installed there, alongside Maki's own. Extensions are installed and configured in Suwayomi; Maki only reads from it.`}
      dirty={dirty}
      saving={save.isPending}
      onSave={() => save.mutate(input(), { onSuccess: savedToast })}
      onDiscard={discard}
    >
      <Stack gap="sm">
        <Group align="flex-end" wrap="wrap">
          <TextInput
            autoComplete="off"
            label={t`URL`}
            description={t`Include the subpath if Suwayomi runs under one.`}
            placeholder="http://suwayomi:4567"
            value={url}
            onChange={(e) => setUrl(e.currentTarget.value)}
            style={{ flex: 2, minWidth: 220 }}
          />
          <Select
            label={t`Sign-in`}
            description={t`Match Suwayomi's own auth mode setting.`}
            value={authMode}
            onChange={(value) => setAuthMode((value as SuwayomiAuthMode | null) ?? 'none')}
            allowDeselect={false}
            data={[
              { value: 'none', label: t`None` },
              { value: 'basic', label: t`Basic auth` },
              { value: 'uilogin', label: t`UI login (v2.2 or later)` },
            ]}
            style={{ flex: 1, minWidth: 180 }}
          />
        </Group>
        {authMode !== 'none' && (
          <Group align="flex-end" wrap="wrap">
            <TextInput
              autoComplete="off"
              label={t`Username`}
              value={username}
              onChange={(e) => setUsername(e.currentTarget.value)}
              style={{ flex: 1, minWidth: 180 }}
            />
            <PasswordInput
              autoComplete="new-password"
              label={t`Password`}
              placeholder={saved?.passwordSet ? t`Saved. Leave empty to keep it.` : undefined}
              value={password}
              onChange={(e) => setPassword(e.currentTarget.value)}
              style={{ flex: 1, minWidth: 180 }}
            />
          </Group>
        )}
        <Group justify="space-between" wrap="wrap">
          <Text size="sm" c="dimmed">
            {connected ? (
              <Plural value={inUse} _0="No Suwayomi sources in use yet." one="# Suwayomi source in use." other="# Suwayomi sources in use." />
            ) : (
              <Trans>Not connected.</Trans>
            )}
          </Text>
          <Group gap="xs">
            <Button
              variant="default"
              loading={test.isPending}
              disabled={!url.trim()}
              onClick={() =>
                test.mutate(input(), {
                  onSuccess: ({ version, sourceCount }) =>
                    notifications.show({
                      color: 'var(--ok)',
                      message: plural(sourceCount, {
                        one: `Suwayomi ${version} answered with # source installed.`,
                        other: `Suwayomi ${version} answered with # sources installed.`,
                      }),
                    }),
                })
              }
            >
              <Trans>Test</Trans>
            </Button>
            <Button disabled={!connected || dirty} onClick={() => setPicking(true)}>
              <Trans>Choose sources</Trans>
            </Button>
          </Group>
        </Group>
      </Stack>
      <SuwayomiSourcesModal opened={picking} onClose={() => setPicking(false)} />
    </SettingsSection>
  )
}

/** Language names in the reader's UI language, falling back to the English table in `titles.ts`. */
function useLanguageNamer() {
  const { i18n } = useLingui()
  return useMemo(() => {
    let names: Intl.DisplayNames | null = null
    try {
      names = new Intl.DisplayNames([i18n.locale], { type: 'language' })
    } catch {
      names = null
    }
    return (code: string) => {
      try {
        return names?.of(code) ?? languageName(code) ?? code
      } catch {
        return languageName(code) ?? code
      }
    }
  }, [i18n.locale])
}

/**
 * Which Suwayomi sources Maki uses. A typical install has hundreds of language variants, so the
 * list starts at the languages this instance downloads in, plus anything already chosen and the
 * sources that mix languages.
 */
function SuwayomiSourcesModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { t } = useLingui()
  const nameOfLanguage = useLanguageNamer()
  const { data, isLoading, error } = useSuwayomiSources(opened)
  const { data: languages } = useSourceLanguages(opened)
  const save = useSaveSuwayomiSources()
  const [chosen, setChosen] = useState<Map<string, string> | null>(null)
  const [search, setSearch] = useState('')
  const [allLanguages, setAllLanguages] = useState(false)
  const [confirming, setConfirming] = useState(false)

  // Seeded once per opening, so a background refetch cannot throw away ticks in progress.
  useEffect(() => {
    if (!opened) {
      setChosen(null)
      setSearch('')
      setConfirming(false)
    } else if (data && chosen === null) {
      setChosen(new Map(data.sources.filter((s) => s.exposed).map((s) => [s.id, s.chapterLanguage ?? ''])))
    }
  }, [opened, data, chosen])

  const wanted = useMemo(() => {
    const enabled = languages?.order.filter((code) => !languages.disabled.includes(code)) ?? []
    return new Set((enabled.length > 0 ? enabled : ['en']).map(baseLanguage))
  }, [languages])

  const rows = data?.sources ?? []
  const needle = search.trim().toLowerCase()
  const visible = rows.filter(
    (s) =>
      (allLanguages || s.exposed || chosen?.has(s.id) || s.mixedLanguage || wanted.has(baseLanguage(s.lang))) &&
      (!needle || `${s.displayName} ${s.name} ${s.lang}`.toLowerCase().includes(needle)),
  )
  const groups = new Map<string, SuwayomiSourceRow[]>()
  for (const row of visible) {
    const key = row.mixedLanguage ? '*' : baseLanguage(row.lang)
    groups.set(key, [...(groups.get(key) ?? []), row])
  }

  const selected = chosen ?? new Map<string, string>()
  const missingLanguage = rows.filter((s) => s.mixedLanguage && selected.has(s.id) && !selected.get(s.id)?.trim())
  const removedLinked = rows.filter((s) => s.exposed && !selected.has(s.id) && s.mappings > 0)
  const linkedSeries = removedLinked.reduce((sum, s) => sum + s.mappings, 0)
  const selectedCount = selected.size

  const toggle = (id: string, on: boolean) =>
    setChosen((current) => {
      const next = new Map(current ?? [])
      if (on) {
        next.set(id, next.get(id) ?? '')
      } else {
        next.delete(id)
      }
      return next
    })

  const setLanguage = (id: string, language: string) =>
    setChosen((current) => new Map(current ?? []).set(id, language))

  const commit = () =>
    save.mutate(
      [...selected].map(([id, language]) => ({ id, chapterLanguage: language.trim() || null })),
      {
        onSuccess: () => {
          notifications.show({ message: now`Saved`, color: 'var(--ok)' })
          onClose()
        },
      },
    )

  return (
    <Modal opened={opened} onClose={onClose} title={t`Suwayomi sources`} size="lg">
      {isLoading || (data && chosen === null) ? (
        <Group justify="center" p="lg">
          <Loader size="sm" />
        </Group>
      ) : error ? (
        <Alert color="red">{error.message}</Alert>
      ) : (
        <Stack gap="sm">
          {data && !data.reachable && (
            <Alert color="yellow">
              {data.error} <Trans>Only the sources already chosen are listed. You can still remove them.</Trans>
            </Alert>
          )}
          {data?.reachable && rows.length === 0 && (
            <Text size="sm">
              <Trans>Suwayomi has no extensions installed yet. Install them in Suwayomi, then come back here.</Trans>
            </Text>
          )}
          <Group wrap="wrap" align="flex-end">
            <TextInput
              type="search"
              value={search}
              onChange={(e) => setSearch(e.currentTarget.value)}
              placeholder={t`Search sources`}
              aria-label={t`Search sources`}
              style={{ flex: 1, minWidth: 200 }}
            />
            <Switch
              checked={allLanguages}
              onChange={(e) => setAllLanguages(e.currentTarget.checked)}
              label={t`Show every language`}
            />
          </Group>
          {[...groups].map(([key, members]) => (
            <Stack key={key} gap={6}>
              <Text size="xs" fw={600} c="dimmed" tt="uppercase">
                {key === '*' ? <Trans>Several languages</Trans> : nameOfLanguage(key)}
              </Text>
              {members.map((source) => (
                <SourceChoice
                  key={source.id}
                  source={source}
                  checked={selected.has(source.id)}
                  language={selected.get(source.id) ?? ''}
                  onToggle={(on) => toggle(source.id, on)}
                  onLanguage={(language) => setLanguage(source.id, language)}
                />
              ))}
            </Stack>
          ))}
          <Text size="sm" c="dimmed">
            <Trans>
              Each chosen source is searched for every series you add. One in English or this
              instance's language is switched on as soon as you save; the rest wait in Manage sources.
            </Trans>
          </Text>
          <Group justify="space-between">
            <Text size="sm">
              <Plural value={selectedCount} _0="No sources chosen" one="# source chosen" other="# sources chosen" />
            </Text>
            <Group gap="xs">
              <Button variant="default" onClick={onClose}>
                <Trans>Cancel</Trans>
              </Button>
              <Button
                loading={save.isPending}
                disabled={missingLanguage.length > 0}
                onClick={() => (linkedSeries > 0 ? setConfirming(true) : commit())}
              >
                <Trans>Save</Trans>
              </Button>
            </Group>
          </Group>
        </Stack>
      )}
      <ConfirmDialog
        opened={confirming}
        onClose={() => setConfirming(false)}
        title={t`Remove sources with linked series?`}
        confirmLabel={t`Remove`}
        loading={save.isPending}
        onConfirm={() => {
          setConfirming(false)
          commit()
        }}
      >
        <Plural
          value={linkedSeries}
          one="# series is linked to the sources you are removing. Its link is kept and stops syncing until the source is chosen again."
          other="# series are linked to the sources you are removing. Their links are kept and stop syncing until the source is chosen again."
        />
      </ConfirmDialog>
    </Modal>
  )
}

function SourceChoice({
  source,
  checked,
  language,
  onToggle,
  onLanguage,
}: {
  source: SuwayomiSourceRow
  checked: boolean
  language: string
  onToggle: (on: boolean) => void
  onLanguage: (language: string) => void
}) {
  const { t } = useLingui()
  const { displayName, lang, mappings } = source
  const warning = source.contentWarning?.toUpperCase() ?? (source.isNsfw ? 'NSFW' : null)
  return (
    <Group justify="space-between" wrap="nowrap" align="flex-start">
      <Stack gap={2} style={{ minWidth: 0 }}>
        <Checkbox
          checked={checked}
          onChange={(e) => onToggle(e.currentTarget.checked)}
          label={displayName}
        />
        <div className="source-tags" style={{ paddingLeft: 30 }}>
          <span className="source-tag" data-tone="language">{lang}</span>
          {warning === 'NSFW' && <span className="source-tag" data-tone="adult"><Trans>18+</Trans></span>}
          {warning === 'MIXED' && <span className="source-tag" data-tone="mature"><Trans>Mature</Trans></span>}
          {source.installed === false && (
            <span className="source-tag" data-tone="adult"><Trans>No longer in Suwayomi</Trans></span>
          )}
          {mappings > 0 && (
            <span className="source-tag">
              <Plural value={mappings} one="# series linked" other="# series linked" />
            </span>
          )}
        </div>
      </Stack>
      {source.mixedLanguage && checked && (
        <TextInput
          size="xs"
          w={130}
          aria-label={t`Chapter language for ${displayName}`}
          placeholder={t`Language, e.g. en`}
          value={language}
          error={!language.trim() ? t`Required` : undefined}
          onChange={(e) => onLanguage(e.currentTarget.value)}
        />
      )}
    </Group>
  )
}
