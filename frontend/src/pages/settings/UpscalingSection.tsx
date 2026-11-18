import { useState } from 'react'
import { SettingsGroupTitle } from '../../components/settings/SettingsGroupTitle'
import { ActionIcon, Anchor, Badge, Button, Checkbox, Divider, Group, Modal, Stack, Switch, Text, Tooltip } from '@mantine/core'
import { IconClockPlay, IconDatabase, IconDownload, IconGauge, IconPackage, IconServer, IconTrash } from '../../lib/icons'
import { notifications } from '../../lib/toast'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import { SettingsSection } from './SettingsSection'
import { SettingsNumberInput } from '../../components/settings/SettingsNumberInput'
import { SettingsDrill, SettingsDrillPage } from '../../components/settings/SettingsDrill'
import { DrillBack, DrillRow, useDrillPages } from '../../components/ui/DrillPanel'
import { ModalFooter, ModalHeading } from '../../components/ui/ModalShell'
import { MODAL_SIZE } from '../../components/ui/modalSizes'
import { formatBytes, formatDecimal, formatList, formatNumber } from '../../format'
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
import { NewSettingPill } from '../../components/settings/NewSettingPill'
import { useNewControlMark } from './newSettings'

const NOTICES_URL = 'https://github.com/OrbitMPGH/Maki/blob/main/THIRD_PARTY_NOTICES.md'
const MB = 1024 ** 2

const sameModels = (a: string[], b: string[]) => a.length === b.length && a.every((id) => b.includes(id))

/** Models added since the last release, by the id of their Settings registry entry. */
const NEW_MODELS: Record<string, string> = { 'maki-2x-hq': 'upscaling-maki-2x-hq' }

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

type Page = 'main' | 'processing' | 'cache' | 'prepare'
const PARENTS: Record<Page, Page> = { main: 'main', processing: 'main', cache: 'main', prepare: 'main' }

/**
 * Server-side page upscaling: which ONNX models readers may use, and how much CPU and disk they get.
 * The model list opens in a modal; processing, the page cache and prepare-ahead are drill rows.
 */
export function UpscalingSection() {
  const { t } = useLingui()
  const label = useLabel()
  const { data: state } = useUpscaling()
  const save = useSaveUpscaling()
  const [draft, setDraft] = useState<UpscalingSettings | null>(null)
  const [modelsOpen, setModelsOpen] = useState(false)
  const pages = useDrillPages<Page>('main', PARENTS)

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
  // Prepare ahead, JPEG XL and the models only appear once upscaling is on, so until then the New tab shows the switch.
  const offMark = useNewControlMark('prepare-ahead', 'upscaling-jpeg-xl', ...Object.values(NEW_MODELS))
  const jpegXlMark = useNewControlMark('upscaling-jpeg-xl')
  const prepareMark = useNewControlMark('prepare-ahead')
  const modelsMark = useNewControlMark(...Object.values(NEW_MODELS))

  const nameOf = (id: string) => {
    const info = enhanceMethodInfo(`server:${id}`)
    return info ? label(info.label) : id
  }
  const ticked = current ? (state?.catalog ?? []).filter((m) => current.models.includes(m.id)) : []
  const jpegXlOn = current?.jpegXl !== false && saved?.jpegXlAvailable !== false
  const prepare = current?.prepare ?? PREPARE_DEFAULTS
  const prepared = saved?.prepare?.enabled ? state?.prepared : undefined

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
          <div {...(current.enabled ? {} : offMark)}>
            <Switch
              label={t`Enable server upscaling`}
              description={t`Each page takes several seconds of server CPU, and upscaled pages are kept in a disk cache. Nothing runs until a reader turns Enhance on.`}
              checked={current.enabled}
              onChange={(e) => update({ enabled: e.currentTarget.checked })}
            />
          </div>

          {current.enabled && (
            <>
              <Divider />
              <Group justify="space-between" wrap="wrap" gap="md" {...modelsMark}>
                <Stack gap={2} className="settings-summary">
                  <SettingsGroupTitle>
                    <Trans>Models readers can pick</Trans>
                  </SettingsGroupTitle>
                  <Text size="sm" c="var(--ink-3)">
                    {ticked.length > 0 ? formatList(ticked.map((m) => nameOf(m.id))) : <Trans>None ticked</Trans>}
                  </Text>
                </Stack>
                <Button
                  variant="default"
                  size="sm"
                  leftSection={<IconPackage size={16} />}
                  onClick={() => setModelsOpen(true)}
                  style={{ flexShrink: 0 }}
                >
                  <Trans>Manage models</Trans>
                </Button>
              </Group>

              <SettingsDrill page={pages.page} depth={pages.depth}>
                {pages.page === 'main' && (
                  <>
                    <DrillRow
                      ref={pages.rowRef('processing')}
                      id="setting-upscaling-jpeg-xl"
                      {...jpegXlMark}
                      icon={IconServer}
                      label={
                        <>
                          <Trans>Processing</Trans>
                          <NewSettingPill id="upscaling-jpeg-xl" />
                        </>
                      }
                      value={
                        jpegXlOn ? (
                          <Plural value={current.threads} one="# thread, JPEG XL on" other="# threads, JPEG XL on" />
                        ) : (
                          <Plural value={current.threads} one="# thread" other="# threads" />
                        )
                      }
                      onClick={() => pages.open('processing')}
                    />
                    <DrillRow
                      ref={pages.rowRef('cache')}
                      icon={IconDatabase}
                      label={<Trans>Page cache</Trans>}
                      value={
                        <CacheSummary
                          bytes={state.cache.bytes - (state.prepared?.bytes ?? 0)}
                          files={state.cache.files}
                          limitMb={saved?.cacheMb ?? current.cacheMb}
                        />
                      }
                      onClick={() => pages.open('cache')}
                    />
                    {saved?.prepare && (
                      <DrillRow
                        ref={pages.rowRef('prepare')}
                        id="setting-prepare-ahead"
                        {...prepareMark}
                        icon={IconClockPlay}
                        label={
                          <>
                            <Trans>Prepare ahead</Trans>
                            <NewSettingPill id="prepare-ahead" />
                          </>
                        }
                        active={prepare.enabled}
                        value={
                          !prepare.enabled ? (
                            <Trans>Off</Trans>
                          ) : prepared ? (
                            <Plural value={prepared.chapters} one="On, # chapter held" other="On, # chapters held" />
                          ) : (
                            <Trans>On</Trans>
                          )
                        }
                        onClick={() => pages.open('prepare')}
                      />
                    )}
                  </>
                )}
                {pages.page === 'processing' && (
                  <SettingsDrillPage {...jpegXlMark}>
                    <DrillBack ref={pages.backRef} title={<Trans>Processing</Trans>} onBack={pages.back} />
                    <SettingsNumberInput
                      label={t`Threads`}
                      description={t`CPU threads used for one page.`}
                      min={1}
                      max={current.maxThreads ?? saved?.maxThreads}
                      value={current.threads}
                      onChange={(threads) => update({ threads })}
                    />
                    <Switch
                      label={
                        <>
                          {t`JPEG XL for colour pages`}
                          <NewSettingPill id="upscaling-jpeg-xl" />
                        </>
                      }
                      description={
                        saved?.jpegXlAvailable === false
                          ? t`Not available: this build of Maki has no JPEG XL library, so colour pages are WebP.`
                          : t`Smaller files and smooth dark gradients where WebP shows blocks. Browsers that can't show JPEG XL, such as Edge, get WebP. Grey pages are always WebP.`
                      }
                      disabled={saved?.jpegXlAvailable === false}
                      checked={jpegXlOn}
                      onChange={(e) => update({ jpegXl: e.currentTarget.checked })}
                    />
                  </SettingsDrillPage>
                )}
                {pages.page === 'cache' && (
                  <SettingsDrillPage>
                    <DrillBack ref={pages.backRef} title={<Trans>Page cache</Trans>} onBack={pages.back} />
                    <SettingsNumberInput
                      label={t`Cache size (MB)`}
                      description={t`The oldest pages are dropped past this.`}
                      min={128}
                      value={current.cacheMb}
                      onChange={(cacheMb) => update({ cacheMb })}
                    />
                    <CacheUsage
                      bytes={state.cache.bytes - (state.prepared?.bytes ?? 0)}
                      files={state.cache.files}
                      limitMb={saved?.cacheMb ?? current.cacheMb}
                    />
                  </SettingsDrillPage>
                )}
                {pages.page === 'prepare' && (
                  <SettingsDrillPage {...prepareMark}>
                    <DrillBack ref={pages.backRef} title={<Trans>Prepare ahead</Trans>} onBack={pages.back} />
                    <PrepareAhead value={prepare} onChange={(next) => update({ prepare: next })} prepared={prepared} />
                  </SettingsDrillPage>
                )}
              </SettingsDrill>
            </>
          )}
        </Stack>
      )}

      {current && state && (
        <Modal
          opened={modelsOpen}
          onClose={() => setModelsOpen(false)}
          size={MODAL_SIZE.wide}
          keepMounted
          title={
            <ModalHeading
              title={t`Upscaling models`}
              description={t`Saving a ticked model that isn't downloaded yet starts its download.`}
            />
          }
        >
          <Stack gap="md">
            <div className="upscale-models" role="table" aria-label={t`Upscaling models`}>
              <div className="upscale-models-head" role="row">
                <span role="columnheader">
                  <Trans>Model</Trans>
                </span>
                <span role="columnheader">
                  <Trans>Speed</Trans>
                </span>
                <span role="columnheader">
                  <Trans>Quality</Trans>
                </span>
                <span role="columnheader">
                  <Trans>Sample page</Trans>
                </span>
                <span role="columnheader">
                  <Trans>Status</Trans>
                </span>
                <span role="columnheader" />
              </div>
              {state.catalog.map((model) => (
                <ModelRow
                  key={model.id}
                  model={model}
                  checked={current.models.includes(model.id)}
                  onToggle={(on) =>
                    update({
                      models: on ? [...current.models, model.id] : current.models.filter((id) => id !== model.id),
                    })
                  }
                />
              ))}
            </div>
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
          <ModalFooter status={dirty ? <Trans>Save the card to apply your picks.</Trans> : undefined}>
            <Button onClick={() => setModelsOpen(false)}>
              <Trans>Done</Trans>
            </Button>
          </ModalFooter>
        </Modal>
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

/** One model as a two-line table row: name, ratings, speed test, status and its file action, then its details. */
function ModelRow({
  model,
  checked,
  onToggle,
}: {
  model: UpscaleCatalogEntry
  checked: boolean
  onToggle: (on: boolean) => void
}) {
  const { t } = useLingui()
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
  const newId = NEW_MODELS[model.id]
  const deleteLabel = t`Delete ${name}`
  const downloadLabel = model.status === 'failed' ? t`Try downloading ${name} again` : t`Download ${name}`

  return (
    <div className="upscale-model" role="row">
      <Group gap="xs" wrap="nowrap" role="cell" className="upscale-model-name">
        <Checkbox
          label={
            <>
              {name}
              {newId && <NewSettingPill id={newId} />}
            </>
          }
          checked={checked}
          onChange={(e) => onToggle(e.currentTarget.checked)}
        />
        {info?.recommended && (
          <Badge size="sm" variant="light" color="var(--brand)">
            <Trans>Recommended</Trans>
          </Badge>
        )}
      </Group>
      {info?.speed && info.quality ? <Ratings speed={info.speed} quality={info.quality} /> : <><span role="cell" /><span role="cell" /></>}
      <span role="cell">
        <Button
          variant={seconds ? 'subtle' : 'default'}
          size="xs"
          leftSection={<IconGauge size={16} />}
          disabled={!ready}
          loading={benchmark.isPending}
          onClick={() => benchmark.mutate(model.id)}
          aria-label={seconds ? t`${seconds} s per sample page. Test ${name} again` : t`Test speed of ${name}`}
        >
          {seconds ? <Trans>{seconds} s</Trans> : <Trans>Test</Trans>}
        </Button>
      </span>
      <span role="cell">
        <StatusBadge status={model.status} bundled={model.bundled} />
      </span>
      <span role="cell" className="upscale-model-action">
        {model.bundled ? null : ready ? (
          <Tooltip label={deleteLabel}>
            <ActionIcon
              variant="subtle"
              color="var(--danger)"
              aria-label={deleteLabel}
              loading={remove.isPending}
              onClick={() => remove.mutate(model.id)}
            >
              <IconTrash size={16} />
            </ActionIcon>
          </Tooltip>
        ) : (
          <Tooltip label={downloadLabel}>
            <ActionIcon
              variant="subtle"
              aria-label={downloadLabel}
              disabled={model.status === 'downloading'}
              loading={download.isPending}
              onClick={() => download.mutate(model.id)}
            >
              <IconDownload size={16} />
            </ActionIcon>
          </Tooltip>
        )}
      </span>
      <div className="upscale-model-meta">
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
      </div>
    </div>
  )
}

function Ratings({ speed, quality }: { speed: ModelRating; quality: ModelRating }) {
  const { t } = useLingui()
  const speeds = { 1: t`Very slow`, 2: t`Slow`, 3: t`Medium`, 4: t`Fast`, 5: t`Very fast` }
  const qualities = { 1: t`Basic`, 2: t`Fair`, 3: t`Good`, 4: t`High`, 5: t`Best` }
  return (
    <>
      <Rating label={t`Speed`} value={speed} word={speeds[speed]} />
      <Rating label={t`Quality`} value={quality} word={qualities[quality]} />
    </>
  )
}

function Rating({ label, value, word }: { label: string; value: ModelRating; word: string }) {
  return (
    <Group gap={6} wrap="nowrap" aria-label={`${label}: ${word}`} role="cell">
      <Text size="xs" c="var(--ink-3)" aria-hidden className="upscale-model-rating-label">
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
      <Switch
        label={
          <>
            {t`Prepare chapters ahead`}
            <NewSettingPill id="prepare-ahead" />
          </>
        }
        description={t`Upscales the next chapters of series set to Reading while nobody is reading. Only series with Enhance on Always and a server model count; Auto is left out.`}
        checked={value.enabled}
        onChange={(e) => set({ enabled: e.currentTarget.checked })}
      />
      {value.enabled && (
        <>
          <div className="settings-field-grid">
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
          </div>
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

function CacheSummary({ bytes, files, limitMb }: { bytes: number; files: number; limitMb: number }) {
  const used = `${formatNumber(Math.round(bytes / MB))} MB`
  const limit = `${formatNumber(limitMb)} MB`
  return <Plural value={files} one={`${used} of ${limit}, # page`} other={`${used} of ${limit}, # pages`} />
}

function CacheUsage({ bytes, files, limitMb }: { bytes: number; files: number; limitMb: number }) {
  const clear = useClearUpscaleCache()
  return (
    <Group justify="space-between" wrap="wrap" gap="sm">
      <Text size="sm">
        <CacheSummary bytes={bytes} files={files} limitMb={limitMb} />
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
