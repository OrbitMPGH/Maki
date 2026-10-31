import { useEffect, useState, type FormEvent } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Trans, Plural, useLingui } from '@lingui/react/macro'
import { plural, t as now } from '@lingui/core/macro'
import {
  ActionIcon,
  Alert,
  Button,
  Code,
  Group,
  List,
  Modal,
  PasswordInput,
  Stack,
  Switch,
  Text,
  Tooltip,
} from '@mantine/core'
import { IconCopy, IconRefresh } from '@tabler/icons-react'
import { notifications } from '../../lib/toast'
import { SettingsSection } from './SettingsSection'
import { useAuth } from '../../auth/AuthProvider'
import { useOpdsSettings, useRotateOpdsToken, useSaveOpdsSettings } from '../../api/hooks'
import {
  useKavitaReadImport,
  useReaderSettings,
  useReaderUsed,
  useSaveReaderSettings,
} from '../../api/reader'
import { useCopyText } from '../../components/ui/useCopyText'

/**
 * Keeping Maki's reader and Kavita in step. The reader's own settings live on the Reader card
 * (ReadingProfilesSection); this is only the two Kavita actions that used to sit underneath them.
 */
export function KavitaSyncSection() {
  const { t } = useLingui()
  const { data: settings } = useReaderSettings()
  const save = useSaveReaderSettings()
  const { me } = useAuth()
  const { data: readerUsed } = useReaderUsed()

  // Push-back and the read-status import are only meaningful for the account Kavita is bound to:
  // pushing somebody else's read would land the echo in a different high-water row and count every
  // chapter into Rewind twice.
  const ownsKavita = settings?.kavitaUserId != null && settings.kavitaUserId === me?.id

  if (!readerUsed?.kavita) return null

  return (
    <SettingsSection
      id="kavita-sync"
      title={<Trans>Kavita read sync</Trans>}
    >
      <Stack gap="md">
        <div>
          <Switch
            label={t`Mark chapters read in Kavita too`}
            checked={settings?.pushToKavita ?? false}
            disabled={!ownsKavita || !settings}
            onChange={(e) =>
              settings &&
              save.mutate(
                { defaults: settings.defaults, pushToKavita: e.currentTarget.checked },
                { onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }) },
              )
            }
          />
          <Text size="xs" c="var(--ink-3)" mt={4}>
            <Trans>Only for series matched to a Kavita series.</Trans>
          </Text>
          {ownsKavita ? null : (
            <Text size="xs" c="var(--ink-3)" mt={4}>
              <Trans>Kavita's reading belongs to another Maki account.</Trans>
            </Text>
          )}
        </div>

        {ownsKavita ? <KavitaLiveReadControl /> : null}

        {ownsKavita ? <KavitaReadImportControl /> : null}
      </Stack>
    </SettingsSection>
  )
}

/**
 * Live pull from Kavita. Kavita only pushes progress events to admins, so a non-admin API key is
 * the one failure worth explaining; everything else falls back to the regular sync anyway.
 */
function KavitaLiveReadControl() {
  const { t } = useLingui()
  const { data: settings } = useReaderSettings()
  const save = useSaveReaderSettings()
  const queryClient = useQueryClient()
  const enabled = settings?.pullFromKavita ?? false
  const status = settings?.kavitaLive

  // The connection settles a moment after the switch flips, so refetch until it does.
  const settling = enabled && status !== 'Connected' && status !== 'NotAdmin'
  // Backs off after 30 s so a Kavita that stays down is not polled every few seconds forever.
  useEffect(() => {
    if (!settling) return
    const startedAt = Date.now()
    let id: ReturnType<typeof setTimeout>
    const tick = () => {
      void queryClient.invalidateQueries({ queryKey: ['settings', 'reader'] })
      id = setTimeout(tick, Date.now() - startedAt < 30_000 ? 3000 : 30_000)
    }
    id = setTimeout(tick, 3000)
    return () => clearTimeout(id)
  }, [settling, queryClient])

  return (
    <div>
      <Switch
        label={t`Mark chapters read here as soon as they're read in Kavita`}
        checked={enabled}
        disabled={!settings}
        onChange={(e) =>
          settings &&
          save.mutate(
            {
              defaults: settings.defaults,
              pushToKavita: settings.pushToKavita,
              pullFromKavita: e.currentTarget.checked,
            },
            { onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }) },
          )
        }
      />
      {enabled && status === 'Connected' && (
        <Text size="xs" c="var(--ok)" mt={4}>
          <Trans>Connected to Kavita.</Trans>
        </Text>
      )}
      {enabled && (status === 'Connecting' || status === 'Off') && (
        <Text size="xs" c="var(--ink-3)" mt={4}>
          <Trans>Connecting to Kavita…</Trans>
        </Text>
      )}
      {enabled && status === 'NotAdmin' && (
        <Text size="xs" c="var(--danger)" mt={4}>
          <Trans>
            Kavita only sends reading updates to admin accounts. Use an API key from a Kavita admin
            under Integrations.
          </Trans>
        </Text>
      )}
      {enabled && status === 'Unreachable' && (
        <Text size="xs" c="var(--danger)" mt={4}>
          <Trans>Can't reach Kavita right now. Maki keeps retrying, and the scrobble sync still catches up.</Trans>
        </Text>
      )}
    </div>
  )
}

/**
 * OPDS is off until switched on, and enabling it is what mints the token, so the URL box only
 * appears once there is something real to copy.
 */
export function OpdsSection() {
  const { t } = useLingui()
  const { data: opds } = useOpdsSettings()
  const save = useSaveOpdsSettings()
  const rotate = useRotateOpdsToken()
  const [rotateModalOpen, setRotateModalOpen] = useState(false)
  const [enableModalOpen, setEnableModalOpen] = useState(false)
  const [password, setPassword] = useState('')
  const { copy: copyFeedUrl } = useCopyText()

  // The token itself is never stored, only its SHA-256 digest, so the full feed URL exists exactly
  // once, in the response that minted it. Held here for as long as the page stays open; after that
  // the only way to get a URL again is to regenerate, which is the same deal as any API key.
  const [revealedPath, setRevealedPath] = useState<string | null>(null)

  const enabled = opds?.enabled ?? false
  const trackProgress = opds?.trackProgress ?? true
  const komga = opds?.komga ?? false
  // The server emits a relative path on purpose (it can't know the host behind a reverse proxy),
  // so the address the user actually pastes is assembled here.
  const feedUrl = revealedPath ? `${window.location.origin}${revealedPath}` : null
  // Komga apps take the same token on its own, as their API key.
  const token = revealedPath ? revealedPath.split('/').pop() || null : null
  const komgaAddress = `${window.location.origin}/komga`

  const closePasswordModals = () => {
    setRotateModalOpen(false)
    setEnableModalOpen(false)
    setPassword('')
  }

  const saveWith = (
    patch: Partial<{ enabled: boolean; trackProgress: boolean; komga: boolean }>,
    confirmPassword?: string,
  ) =>
    save.mutate(
      { enabled, trackProgress, ...patch, password: confirmPassword || undefined },
      {
        onSuccess: (result) => {
          // Enabling for the first time mints the token, so this is the one save that reveals a URL.
          if (result.feedUrl) setRevealedPath(result.feedUrl)
          closePasswordModals()
          notifications.show({ message: now`Saved`, color: 'var(--ok)' })
        },
      },
    )

  const copy = (value: string | null, message: string) => {
    if (!value) return
    void copyFeedUrl(value).then((ok) => {
      if (ok) notifications.show({ message, color: 'var(--ok)' })
    })
  }

  return (
    <SettingsSection
      id="opds"
      title={<Trans>OPDS catalogue</Trans>}
      description={
        <Trans>For reading apps like Panels, KOReader and Mihon.</Trans>
      }
    >
      <Stack gap="md">
        <div>
          <Switch
            label={t`Enable the OPDS catalogue`}
            checked={enabled}
            onChange={(e) => {
              const next = e.currentTarget.checked
              if (next && !opds?.hasToken) setEnableModalOpen(true)
              else saveWith({ enabled: next })
            }}
          />
          <Text size="xs" c="var(--ink-3)" mt={4}>
            <Trans>Anyone with the feed URL can read everything your account can.</Trans>
          </Text>
        </div>

        {enabled && (
          <div>
            <Text size="sm" fw={500} mb={4}>
              <Trans>Feed URL</Trans>
            </Text>
            {feedUrl ? (
              <>
                <Group gap="xs" wrap="nowrap">
                  <Code style={{ overflowWrap: 'anywhere' }}>{feedUrl}</Code>
                  <Tooltip label={t`Copy feed URL`}>
                    <ActionIcon
                      variant="light"
                      onClick={() => copy(feedUrl, now`Feed URL copied`)}
                      aria-label={t`Copy feed URL`}
                    >
                      <IconCopy size={16} />
                    </ActionIcon>
                  </Tooltip>
                </Group>
                <Alert color="var(--warn)" variant="light" mt="xs">
                  <Trans>
                    Copy this now. It can't be shown again; if you lose it, regenerate.
                  </Trans>
                </Alert>
                <Text size="xs" c="var(--ink-3)" mt={4}>
                  <Trans>From outside your network, replace the host with the address you use there.</Trans>
                </Text>
              </>
            ) : (
              <Group gap="xs" wrap="nowrap">
                <Code>{opds?.tokenPrefix ? `${opds.tokenPrefix}…` : t`none yet`}</Code>
                <Button
                  size="compact-xs"
                  variant="light"
                  color="var(--danger)"
                  leftSection={<IconRefresh size={14} />}
                  onClick={() => setRotateModalOpen(true)}
                >
                  <Trans>Regenerate</Trans>
                </Button>
              </Group>
            )}
          </div>
        )}

        {enabled && (
          <div>
            <Switch
              label={t`Track reading progress from OPDS`}
              checked={trackProgress}
              onChange={(e) => saveWith({ trackProgress: e.currentTarget.checked })}
            />
            <Text size="xs" c="var(--ink-3)" mt={4}>
              <Trans>Turn it off if an app reports progress you didn't make.</Trans>
            </Text>
          </div>
        )}

        {enabled && (
          <div>
            <Switch
              label={t`Komga-compatible API`}
              checked={komga}
              onChange={(e) => saveWith({ komga: e.currentTarget.checked })}
            />
            <Text size="xs" c="var(--ink-3)" mt={4}>
              <Trans>For apps that only connect to Komga, like Tachimanga.</Trans>
            </Text>
            {komga && (
              <Stack gap={6} mt="xs">
                <Group gap="xs" wrap="nowrap">
                  <Text size="sm" fw={500}>
                    <Trans>Server address</Trans>
                  </Text>
                  <Code style={{ overflowWrap: 'anywhere' }}>{komgaAddress}</Code>
                  <Tooltip label={t`Copy server address`}>
                    <ActionIcon
                      variant="light"
                      onClick={() => copy(komgaAddress, now`Server address copied`)}
                      aria-label={t`Copy server address`}
                    >
                      <IconCopy size={16} />
                    </ActionIcon>
                  </Tooltip>
                </Group>
                {token && (
                  <Group gap="xs" wrap="nowrap">
                    <Text size="sm" fw={500}>
                      <Trans>API key</Trans>
                    </Text>
                    <Code style={{ overflowWrap: 'anywhere' }}>{token}</Code>
                    <Tooltip label={t`Copy API key`}>
                      <ActionIcon
                        variant="light"
                        onClick={() => copy(token, now`API key copied`)}
                        aria-label={t`Copy API key`}
                      >
                        <IconCopy size={16} />
                      </ActionIcon>
                    </Tooltip>
                  </Group>
                )}
                <Text size="xs" c="var(--ink-3)">
                  {token ? (
                    <Trans>
                      Enter the API key in the app, or any username with the API key as the password.
                    </Trans>
                  ) : (
                    <Trans>
                      The API key is the last part of your feed URL, after /opds/. Enter it in the app,
                      or use any username with the API key as the password.
                    </Trans>
                  )}
                </Text>
              </Stack>
            )}
          </div>
        )}
      </Stack>

      <Modal opened={enableModalOpen} onClose={closePasswordModals} title={t`Enable the OPDS catalogue`} centered>
        <Stack
          component="form"
          onSubmit={(e: FormEvent) => {
            e.preventDefault()
            saveWith({ enabled: true }, password)
          }}
        >
          <Text size="sm">
            <Trans>Enabling it creates the feed URL. Confirm your password to continue.</Trans>
          </Text>
          <PasswordInput
            label={t`Your password`}
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.currentTarget.value)}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={closePasswordModals}>
              <Trans>Cancel</Trans>
            </Button>
            <Button type="submit" loading={save.isPending} disabled={!password}>
              <Trans>Enable</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={rotateModalOpen} onClose={closePasswordModals} title={t`Regenerate OPDS token`} centered>
        <Stack
          component="form"
          onSubmit={(e: FormEvent) => {
            e.preventDefault()
            rotate.mutate(password, {
              onSuccess: (result) => {
                closePasswordModals()
                // The only moment the new URL exists in a readable form.
                setRevealedPath(result.feedUrl)
                notifications.show({ message: now`New OPDS feed URL generated`, color: 'var(--ok)' })
              },
            })
          }}
        >
          <Text size="sm">
            <Trans>
              The current feed URL stops working immediately. Every app using it needs the new
              one.
            </Trans>
          </Text>
          <PasswordInput
            label={t`Your password`}
            autoComplete="current-password"
            value={password}
            onChange={(e) => setPassword(e.currentTarget.value)}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={closePasswordModals}>
              <Trans>Cancel</Trans>
            </Button>
            <Button type="submit" color="var(--danger-fill)" loading={rotate.isPending} disabled={!password}>
              <Trans>Regenerate</Trans>
            </Button>
          </Group>
        </Stack>
      </Modal>
    </SettingsSection>
  )
}

/**
 * KOReader's "Progress sync" plugin talking to Maki's kosync endpoint. It signs in with the OPDS
 * token, so this only has instructions to give once the catalogue is on.
 */
export function KoreaderSyncSection() {
  const { t } = useLingui()
  const { data: opds } = useOpdsSettings()
  const { copy: copyServerUrl } = useCopyText()
  const serverUrl = `${window.location.origin}/api/v1/kosync`
  const enabled = opds?.enabled ?? false
  const feedPath = '/api/v1/opds/'

  const copy = () =>
    void copyServerUrl(serverUrl).then((ok) => {
      if (ok) notifications.show({ message: now`Sync server address copied`, color: 'var(--ok)' })
    })

  return (
    <SettingsSection
      id="koreader-sync"
      title={<Trans>KOReader progress sync</Trans>}
    >
      {!enabled ? (
        <Text size="sm" c="var(--ink-3)">
          <Trans>Turn on the OPDS catalogue first. KOReader signs in with its token.</Trans>
        </Text>
      ) : (
        <Stack gap="md">
          <div>
            <Text size="sm" fw={500} mb={4}>
              <Trans>Sync server</Trans>
            </Text>
            <Group gap="xs" wrap="nowrap">
              <Code style={{ overflowWrap: 'anywhere' }}>{serverUrl}</Code>
              <Tooltip label={t`Copy sync server address`}>
                <ActionIcon variant="light" onClick={copy} aria-label={t`Copy sync server address`}>
                  <IconCopy size={16} />
                </ActionIcon>
              </Tooltip>
            </Group>
          </div>

          <List type="ordered" size="sm" spacing={6}>
            <List.Item>
              <Trans>
                With a book open in KOReader, go to Tools, then Progress sync, then Custom sync
                server, and enter the address above.
              </Trans>
            </List.Item>
            <List.Item>
              <Trans>
                Choose Register / Login. The username is your OPDS token, the part of the feed URL
                after <Code>{feedPath}</Code>. Type anything as the password; Maki doesn't use it.
              </Trans>
            </List.Item>
            <List.Item>
              <Trans>Set Document matching method to Binary.</Trans>
            </List.Item>
          </List>

          <Text size="xs" c="var(--ink-3)">
            <Trans>Regenerating the OPDS token signs KOReader out too.</Trans>
          </Text>
        </Stack>
      )}
    </SettingsSection>
  )
}

function KavitaImportResultSummary({
  result,
}: {
  result: {
    seriesMatched: number
    chaptersMarked: number
    seriesUnmatched: number
    seriesFailed: number
    failedTitles: string[]
  }
}) {
  const { chaptersMarked, seriesMatched, seriesUnmatched, seriesFailed, failedTitles } = result

  // Capped so one huge Kavita library can't turn this line into a wall of titles; the rest are
  // named only by count, in a suffix that still needs its own plural forms.
  const shownTitles = failedTitles.slice(0, 5)
  const moreCount = failedTitles.length - shownTitles.length
  const titles =
    moreCount > 0
      ? `${shownTitles.join(', ')} ${plural(moreCount, { one: '+# more', other: '+# more' })}`
      : shownTitles.join(', ')

  return (
    <Stack gap={2}>
      <Text size="xs" c="var(--ink-3)">
        {seriesUnmatched > 0 ? (
          <Trans>
            <Plural value={chaptersMarked} one="# chapter" other="# chapters" /> marked read across{' '}
            <Plural value={seriesMatched} one="# series" other="# series" />,{' '}
            <Plural value={seriesUnmatched} one="# Kavita series" other="# Kavita series" /> unmatched
          </Trans>
        ) : (
          <Trans>
            <Plural value={chaptersMarked} one="# chapter" other="# chapters" /> marked read across{' '}
            <Plural value={seriesMatched} one="# series" other="# series" />
          </Trans>
        )}
      </Text>
      {seriesFailed > 0 && (
        <Text size="xs" c="var(--danger)">
          {titles ? (
            <Trans>
              Could not read progress for{' '}
              <Plural value={seriesFailed} one="# series" other="# series" />: {titles}
            </Trans>
          ) : (
            <Trans>
              Could not read progress for <Plural value={seriesFailed} one="# series" other="# series" />
            </Trans>
          )}
        </Text>
      )}
    </Stack>
  )
}

function KavitaReadImportControl() {
  const { status, start } = useKavitaReadImport()
  const result = status?.result

  return (
    <div>
      <Text fw={500} size="sm" mb={4}>
        <Trans>Import read status from Kavita</Trans>
      </Text>
      <Text size="xs" c="var(--ink-3)" mb="sm">
        <Trans>Safe to rerun, it never unmarks anything.</Trans>
      </Text>
      <Group gap="sm">
        <Button
          variant="light"
          loading={status?.running ?? false}
          onClick={() => start.mutate()}
        >
          <Trans>Import read status</Trans>
        </Button>
        {status?.running && (
          <Text size="xs" c="var(--ink-3)">
            <Trans>Reading progress from Kavita…</Trans>
          </Text>
        )}
        {!status?.running && status?.error && (
          <Text size="xs" c="var(--danger)">
            {status.error}
          </Text>
        )}
        {!status?.running && !status?.error && result && <KavitaImportResultSummary result={result} />}
      </Group>
    </div>
  )
}

