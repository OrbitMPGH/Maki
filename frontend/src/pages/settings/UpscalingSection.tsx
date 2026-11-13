import { useState } from 'react'
import { Anchor, Badge, Button, Checkbox, Divider, Group, Stack, Switch, Text } from '@mantine/core'
import { IconDownload, IconGauge, IconTrash } from '../../lib/icons'
import { notifications } from '../../lib/toast'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { SettingsSection } from './SettingsSection'
import { SettingsNumberInput } from '../../components/settings/SettingsNumberInput'
import { formatBytes, formatDecimal, formatNumber } from '../../format'
import { useLabel } from '../../i18n-context'
import { enhanceMethodInfo, type ModelRating } from '../reader/enhanceMethods'
import {
  useClearUpscaleCache,
  useDeleteUpscaleModel,
  useDownloadUpscaleModel,
  useSaveUpscaling,
  useUpscaleBenchmark,
  useUpscaling,
  type PrepareAheadSettings,
  type PreparedState,
  type UpscaleCatalogEntry,
  type UpscalingSettings,
} from '../../api/upscaling'
import { MetaList } from '../../components/ui/MetaList'

const NOTICES_URL = 'https://github.com/OrbitMPGH/Maki/blob/main/THIRD_PARTY_NOTICES.md'
const MB = 1024 ** 2

const sameModels = (a: string[], b: string[]) => a.length === b.length && a.every((id) => b.includes(id))

const PREPARE_DEFAULTS: PrepareAheadSettings = { enabled: false, days: 14, series: 8, chapters: 1, mb: 4096 }

const samePrepare = (a?: PrepareAheadSettings, b?: PrepareAheadSettings) =>
  (a === undefined && b === undefined) ||
  (a !== undefined &&
    b !== undefined &&
    a.enabled === b.enabled &&
    a.days === b.days &&
    a.series === b.series &&
    a.chapters === b.chapters &&
    a.mb === b.mb)

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
      draft.jpegXl !== saved.jpegXl ||
      !samePrepare(draft.prepare, saved.prepare) ||
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

              <Switch
                label={t`JPEG XL for colour pages`}
                description={
                  saved?.jpegXlAvailable === false
                    ? t`Not available: this build of Maki has no JPEG XL library, so colour pages are WebP.`
                    : t`Smaller files and smooth dark gradients where WebP shows blocks. Browsers that can't show JPEG XL, such as Edge, get WebP. Grey pages are always WebP.`
                }
                disabled={saved?.jpegXlAvailable === false}
                checked={current.jpegXl !== false && saved?.jpegXlAvailable !== false}
                onChange={(e) => update({ jpegXl: e.currentTarget.checked })}
              />

              <CacheUsage
                bytes={state.cache.bytes - (state.prepared?.bytes ?? 0)}
                files={state.cache.files}
                limitMb={saved?.cacheMb ?? current.cacheMb}
              />

              {saved?.prepare && (
                <PrepareAhead
                  value={current.prepare ?? PREPARE_DEFAULTS}
                  onChange={(prepare) => update({ prepare })}
                  prepared={saved.prepare.enabled ? state.prepared : undefined}
                />
              )}
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

function StatusBadge({ status, bundled }: { status: UpscaleCatalogEntry['status']; bundled?: boolean }) {
  if (bundled && status === 'ready') {
    return (
      <Badge size="sm" variant="light" color="var(--ok)">
        <Trans>Included</Trans>
      </Badge>
    )
  }
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
  const size = model.bytes === null ? null : formatBytes(model.bytes)
  const ready = model.status === 'ready'
  const seconds = benchmark.data ? formatDecimal(benchmark.data.seconds) : null

  return (
    <Stack gap={6}>
      <Group justify="space-between" wrap="wrap" gap="sm">
        <Group gap="xs" wrap="nowrap">
          <Checkbox label={name} checked={checked} onChange={(e) => onToggle(e.currentTarget.checked)} />
          {info?.recommended && (
            <Badge size="sm" variant="light" color="var(--brand)">
              <Trans>Recommended</Trans>
            </Badge>
          )}
        </Group>
        <StatusBadge status={model.status} bundled={model.bundled} />
      </Group>
      {info?.speed && info.quality && <Ratings speed={info.speed} quality={info.quality} />}
      <Text component="div" size="xs" c="var(--ink-3)">
        <MetaList
          items={[
            hint,
            size,
            model.license,
            <Anchor key="author" href={model.source} target="_blank" rel="noreferrer" size="xs">
              {model.author}
            </Anchor>,
          ]}
        />
      </Text>
      {model.status === 'failed' && model.error && (
        <Text size="xs" c="var(--danger-fg)">
          {model.error}
        </Text>
      )}
      <Group gap="xs" wrap="wrap">
        {model.bundled ? null : ready ? (
          <Button
            variant="light"
            color="var(--danger)"
            size="xs"
            leftSection={<IconTrash size={16} />}
            loading={remove.isPending}
            onClick={() => remove.mutate(model.id)}
          >
            <Trans>Delete</Trans>
          </Button>
        ) : (
          <Button
            variant="default"
            size="xs"
            leftSection={<IconDownload size={16} />}
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
          leftSection={<IconGauge size={16} />}
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

function Ratings({ speed, quality }: { speed: ModelRating; quality: ModelRating }) {
  const { t } = useLingui()
  const speeds = { 1: t`Very slow`, 2: t`Slow`, 3: t`Medium`, 4: t`Fast`, 5: t`Very fast` }
  const qualities = { 1: t`Basic`, 2: t`Fair`, 3: t`Good`, 4: t`High`, 5: t`Best` }
  return (
    <Group gap="lg" wrap="wrap">
      <Rating label={t`Speed`} value={speed} word={speeds[speed]} minWidth="11rem" />
      <Rating label={t`Quality`} value={quality} word={qualities[quality]} />
    </Group>
  )
}

function Rating({ label, value, word, minWidth }: { label: string; value: ModelRating; word: string; minWidth?: string }) {
  return (
    <Group gap={6} wrap="nowrap" miw={minWidth} aria-label={`${label}: ${word}`} role="img">
      <Text size="xs" c="var(--ink-3)" aria-hidden>
        {label}
      </Text>
      <Group gap={3} wrap="nowrap" aria-hidden>
        {[1, 2, 3, 4, 5].map((step) => (
          <span
            key={step}
            style={{
              width: 7,
              height: 7,
              borderRadius: '50%',
              background: step <= value ? 'var(--brand)' : 'var(--border-strong)',
            }}
          />
        ))}
      </Group>
      <Text size="xs" c="var(--ink-2)" aria-hidden>
        {word}
      </Text>
    </Group>
  )
}

function PrepareAhead({
  value,
  onChange,
  prepared,
}: {
  value: PrepareAheadSettings
  onChange: (value: PrepareAheadSettings) => void
  prepared?: PreparedState
}) {
  const { t } = useLingui()
  const set = (changes: Partial<PrepareAheadSettings>) => onChange({ ...value, ...changes })
  return (
    <>
      <Divider />
      <Switch
        label={t`Prepare chapters ahead`}
        description={t`Upscales the next chapters of series set to Reading while nobody is reading, so they open sharp. Only series whose reader settings have Enhance on Always and a server model count; Auto depends on the screen, so it is left out.`}
        checked={value.enabled}
        onChange={(e) => set({ enabled: e.currentTarget.checked })}
      />
      {value.enabled && (
        <>
          <Group gap="md" align="flex-start" wrap="wrap">
            <SettingsNumberInput
              label={t`Read within (days)`}
              description={t`Series read longer ago are left alone.`}
              min={1}
              max={365}
              value={value.days}
              onChange={(days) => set({ days })}
            />
            <SettingsNumberInput
              label={t`Series per reader`}
              description={t`The most recently read ones go first.`}
              min={1}
              max={100}
              value={value.series}
              onChange={(series) => set({ series })}
            />
            <SettingsNumberInput
              label={t`Chapters per series`}
              description={t`From the chapter you would open next.`}
              min={1}
              max={3}
              value={value.chapters}
              onChange={(chapters) => set({ chapters })}
            />
            <SettingsNumberInput
              label={t`Space (MB)`}
              description={t`Prepared pages are kept until read, apart from the cache size.`}
              min={256}
              value={value.mb}
              onChange={(mb) => set({ mb })}
            />
          </Group>
          {prepared && <PreparedUsage prepared={prepared} limitMb={value.mb} />}
        </>
      )}
    </>
  )
}

function PreparedUsage({ prepared, limitMb }: { prepared: PreparedState; limitMb: number }) {
  const used = `${formatNumber(Math.round(prepared.bytes / MB))} MB`
  const limit = `${formatNumber(limitMb)} MB`
  const chapters = prepared.chapters
  const waiting = prepared.waiting
  return (
    <Text size="sm">
      <Plural value={chapters} one={`# chapter held, ${used} of ${limit}`} other={`# chapters held, ${used} of ${limit}`} />
      {waiting > 0 && (
        <>
          {' · '}
          <Plural value={waiting} one="# page still to make" other="# pages still to make" />
        </>
      )}
    </Text>
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
        variant="light"
        color="var(--danger)"
        size="xs"
        leftSection={<IconTrash size={16} />}
        disabled={files === 0}
        loading={clear.isPending}
        onClick={() => clear.mutate(undefined)}
      >
        <Trans>Clear cache</Trans>
      </Button>
    </Group>
  )
}
