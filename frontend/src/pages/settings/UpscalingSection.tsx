import { useState } from 'react'
import { Anchor, Badge, Button, Checkbox, Divider, Group, Stack, Switch, Text } from '@mantine/core'
import { IconDownload, IconGauge, IconTrash } from '@tabler/icons-react'
import { notifications } from '@mantine/notifications'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { SettingsSection } from './SettingsSection'
import { SettingsNumberInput } from '../../components/settings/SettingsNumberInput'
import { formatBytes, formatDecimal, formatNumber } from '../../format'
import { useLabel } from '../../i18n-context'
import { enhanceMethodInfo } from '../reader/enhanceMethods'
import {
  useClearUpscaleCache,
  useDeleteUpscaleModel,
  useDownloadUpscaleModel,
  useSaveUpscaling,
  useUpscaleBenchmark,
  useUpscaling,
  type UpscaleCatalogEntry,
  type UpscalingSettings,
} from '../../api/upscaling'

const NOTICES_URL = 'https://github.com/OrbitMPGH/Maki/blob/main/THIRD_PARTY_NOTICES.md'
const MB = 1024 ** 2

const sameModels = (a: string[], b: string[]) => a.length === b.length && a.every((id) => b.includes(id))

/** Server-side page upscaling: which ONNX models readers may use, and how much CPU and disk they get. */
export function UpscalingSection() {
  const { t } = useLingui()
  const { data: state } = useUpscaling()
  const save = useSaveUpscaling()
  const [draft, setDraft] = useState<UpscalingSettings | null>(null)

  const saved = state?.settings
  const current = draft ?? saved
  const dirty =
    draft !== null &&
    saved !== undefined &&
    (draft.enabled !== saved.enabled ||
      draft.threads !== saved.threads ||
      draft.cacheMb !== saved.cacheMb ||
      !sameModels(draft.models, saved.models))
  const update = (changes: Partial<UpscalingSettings>) => {
    if (current) setDraft({ ...current, ...changes })
  }

  return (
    <SettingsSection
      id="upscaling"
      title={<Trans>Server upscaling</Trans>}
      description={<Trans>Sharpens soft pages in the reader with models that run on this server.</Trans>}
      dirty={dirty}
      saving={save.isPending}
      onSave={() => {
        if (!draft) return
        save.mutate(draft, {
          onSuccess: () => {
            setDraft(null)
            notifications.show({ message: now`Saved`, color: 'var(--ok)' })
          },
        })
      }}
      onDiscard={() => setDraft(null)}
    >
      {current && state && (
        <Stack>
          <Switch
            label={t`Enable server upscaling`}
            description={t`Each page takes several seconds of server CPU, and upscaled pages are kept in a disk cache. Nothing runs until a reader turns Enhance on.`}
            checked={current.enabled}
            onChange={(e) => update({ enabled: e.currentTarget.checked })}
          />

          {current.enabled && (
            <>
              <Divider />
              <Stack gap="xs">
                <Text fw={500} size="sm">
                  <Trans>Models</Trans>
                </Text>
                <Text size="sm" c="var(--ink-3)">
                  <Trans>
                    Readers can pick the ticked models. Saving a ticked model that isn't downloaded yet starts its
                    download.
                  </Trans>
                </Text>
                <Stack gap="md" mt="xs">
                  {state.catalog.map((model) => (
                    <ModelRow
                      key={model.id}
                      model={model}
                      checked={current.models.includes(model.id)}
                      onToggle={(on) =>
                        update({
                          models: on
                            ? [...current.models, model.id]
                            : current.models.filter((id) => id !== model.id),
                        })
                      }
                    />
                  ))}
                </Stack>
              </Stack>

              <Divider />
              <Group gap="md" align="flex-start" wrap="wrap">
                <SettingsNumberInput
                  label={t`Threads`}
                  description={t`CPU threads used for one page.`}
                  min={1}
                  max={current.maxThreads ?? saved?.maxThreads}
                  value={current.threads}
                  onChange={(threads) => update({ threads })}
                />
                <SettingsNumberInput
                  label={t`Cache size (MB)`}
                  description={t`The oldest pages are dropped past this.`}
                  min={128}
                  value={current.cacheMb}
                  onChange={(cacheMb) => update({ cacheMb })}
                />
              </Group>

              <CacheUsage bytes={state.cache.bytes} files={state.cache.files} limitMb={saved?.cacheMb ?? current.cacheMb} />
            </>
          )}

          <Text size="sm" c="var(--ink-3)">
            <Trans>
              The models are third-party files under their own licences. The non-commercial ones bind anyone who
              receives them.
            </Trans>{' '}
            <Anchor href={NOTICES_URL} target="_blank" rel="noreferrer" size="sm">
              <Trans>Third-party notices</Trans>
            </Anchor>
          </Text>
        </Stack>
      )}
    </SettingsSection>
  )
}

function StatusBadge({ status }: { status: UpscaleCatalogEntry['status'] }) {
  switch (status) {
    case 'ready':
      return (
        <Badge size="sm" variant="light" color="var(--ok)">
          <Trans>Ready</Trans>
        </Badge>
      )
    case 'downloading':
      return (
        <Badge size="sm" variant="light" color="var(--info)">
          <Trans>Downloading</Trans>
        </Badge>
      )
    case 'failed':
      return (
        <Badge size="sm" variant="light" color="var(--danger)">
          <Trans>Failed</Trans>
        </Badge>
      )
    default:
      return (
        <Badge size="sm" variant="light" color="var(--neutral)">
          <Trans>Missing</Trans>
        </Badge>
      )
  }
}

function ModelRow({
  model,
  checked,
  onToggle,
}: {
  model: UpscaleCatalogEntry
  checked: boolean
  onToggle: (on: boolean) => void
}) {
  const label = useLabel()
  const download = useDownloadUpscaleModel()
  const remove = useDeleteUpscaleModel()
  const benchmark = useUpscaleBenchmark()
  const info = enhanceMethodInfo(`server:${model.id}`)
  const name = info ? label(info.label) : model.id
  const hint = info ? label(info.hint) : null
  const size = formatBytes(model.bytes)
  const ready = model.status === 'ready'
  const seconds = benchmark.data ? formatDecimal(benchmark.data.seconds) : null

  return (
    <Stack gap={6}>
      <Group justify="space-between" wrap="wrap" gap="sm">
        <Checkbox label={name} checked={checked} onChange={(e) => onToggle(e.currentTarget.checked)} />
        <StatusBadge status={model.status} />
      </Group>
      <Text size="xs" c="var(--ink-3)">
        {hint && `${hint} \u00b7 `}
        {`${size} \u00b7 ${model.license} \u00b7 `}
        <Anchor href={model.source} target="_blank" rel="noreferrer" size="xs">
          {model.author}
        </Anchor>
      </Text>
      {model.status === 'failed' && model.error && (
        <Text size="xs" c="var(--danger-fg)">
          {model.error}
        </Text>
      )}
      <Group gap="xs" wrap="wrap">
        {ready ? (
          <Button
            variant="default"
            size="xs"
            leftSection={<IconTrash size={14} />}
            loading={remove.isPending}
            onClick={() => remove.mutate(model.id)}
          >
            <Trans>Delete</Trans>
          </Button>
        ) : (
          <Button
            variant="default"
            size="xs"
            leftSection={<IconDownload size={14} />}
            disabled={model.status === 'downloading'}
            loading={download.isPending}
            onClick={() => download.mutate(model.id)}
          >
            {model.status === 'failed' ? <Trans>Try again</Trans> : <Trans>Download</Trans>}
          </Button>
        )}
        <Button
          variant="default"
          size="xs"
          leftSection={<IconGauge size={14} />}
          disabled={!ready}
          loading={benchmark.isPending}
          onClick={() => benchmark.mutate(model.id)}
        >
          <Trans>Test speed</Trans>
        </Button>
        {seconds && (
          <Text size="sm" c="var(--ink-2)">
            <Trans>{seconds} s per sample page</Trans>
          </Text>
        )}
      </Group>
    </Stack>
  )
}

function CacheUsage({ bytes, files, limitMb }: { bytes: number; files: number; limitMb: number }) {
  const clear = useClearUpscaleCache()
  const used = `${formatNumber(Math.round(bytes / MB))} MB`
  const limit = `${formatNumber(limitMb)} MB`

  return (
    <Group justify="space-between" wrap="wrap" gap="sm">
      <Text size="sm">
        <Plural value={files} one={`${used} of ${limit}, # page`} other={`${used} of ${limit}, # pages`} />
      </Text>
      <Button
        variant="default"
        size="xs"
        leftSection={<IconTrash size={14} />}
        disabled={files === 0}
        loading={clear.isPending}
        onClick={() => clear.mutate(undefined)}
      >
        <Trans>Clear cache</Trans>
      </Button>
    </Group>
  )
}
