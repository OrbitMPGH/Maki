import { useState } from 'react'
import {
  ActionIcon,
  Badge,
  Button,
  Card,
  Group,
  MultiSelect,
  NumberInput,
  Select,
  Stack,
  Switch,
  Table,
  Text,
  TextInput,
  Tooltip,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import {
  IconArrowDown,
  IconArrowUp,
  IconChevronDown,
  IconChevronUp,
  IconPlus,
  IconTrash,
} from '@tabler/icons-react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import {
  FORMAT_CONDITION_TYPE_LABELS,
  FORMAT_CONDITION_TYPES,
  IMAGE_FORMATS,
  QUALITY_TIER_LABELS,
  SOURCE_KINDS,
  SOURCE_KIND_LABELS,
  useCreateQualityFormat,
  useCreateUpgradeProfile,
  useDeleteQualityFormat,
  useDeleteUpgradeProfile,
  useQualityFormats,
  useUpdateQualityFormat,
  useUpdateUpgradeProfile,
  useUpgradeProfiles,
  type FormatConditionDto,
  type FormatConditionType,
  type FormatScoreDto,
  type ProfileTierDto,
  type QualityFormatDto,
  type QualityFormatInput,
  type QualityTierName,
  type UpgradeProfileDto,
  type UpgradeProfileInput,
} from '../../api/upgrades'
import { useSources } from '../../api/hooks'
import { ConfirmDialog } from '../ui/ConfirmDialog'
import { SettingsSection } from '../../pages/settings/SettingsSection'
import { SettingsHelp } from './SettingsHelp'
import { useLabel } from '../../i18n-context'

/** Highest priority first, matches `UpgradeProfileDefaults.DefaultOrder` on the server. */
const DEFAULT_TIER_ORDER: QualityTierName[] = ['volume', 'official', 'scanlator', 'aggregator', 'unknown']

const DEFAULT_PROFILE: UpgradeProfileInput = {
  name: '',
  tiers: DEFAULT_TIER_ORDER.map((tier) => ({ tier, allowed: true })),
  cutoff: 'aggregator',
  upgradesEnabled: false,
  minScoreDelta: 1,
  upgradeUntilScore: 0,
  formatScores: [],
  resolutionWeight: 10,
  compressionWeight: 10,
  pageTolerancePercent: 10,
  allowReplacingUnknown: true,
}

function moveItem<T>(items: T[], index: number, direction: -1 | 1): T[] {
  const target = index + direction
  if (target < 0 || target >= items.length) return items
  const next = [...items]
  ;[next[index], next[target]] = [next[target], next[index]]
  return next
}

function scoreOf(formatScores: FormatScoreDto[], formatId: number): number {
  return formatScores.find((f) => f.formatId === formatId)?.score ?? 0
}

function withScore(formatScores: FormatScoreDto[], formatId: number, score: number): FormatScoreDto[] {
  const rest = formatScores.filter((f) => f.formatId !== formatId)
  return score === 0 ? rest : [...rest, { formatId, score }]
}

/**
 * Which release tiers an upgrade profile prefers, in what order, and how much of a score gain it
 * takes to replace what's already on disk. Sits above the format score table, which is scored per
 * profile but shared across them, which is why formats get their own section below.
 */
export function UpgradeProfilesSection() {
  const { t } = useLingui()
  const { data: profiles } = useUpgradeProfiles()
  const { data: formats } = useQualityFormats()
  const create = useCreateUpgradeProfile()
  const [creating, setCreating] = useState(false)

  return (
    <SettingsSection
      id="profiles"
      title={<Trans>Quality profiles</Trans>}
      description={
        <Trans>
          A profile picks which release tier Maki prefers for a series, and how far it goes to
          replace what's already downloaded. Pin one to a series from its Quality profile menu, or
          set a default for every series below under Downloads.
        </Trans>
      }
      actions={
        <Button
          size="xs"
          variant="light"
          leftSection={<IconPlus size={14} />}
          onClick={() => setCreating((open) => !open)}
        >
          <Trans>New profile</Trans>
        </Button>
      }
    >

      {creating && (
        <ProfileEditor
          key="new"
          initial={DEFAULT_PROFILE}
          formats={formats ?? []}
          submitLabel={t`Create`}
          busy={create.isPending}
          onCancel={() => setCreating(false)}
          onSubmit={(input) =>
            create.mutate(input, {
              onSuccess: () => {
                setCreating(false)
                notifications.show({ message: now`Profile created`, color: 'var(--ok)' })
              },
            })
          }
        />
      )}

      <Stack gap="xs" mt={creating ? 'md' : undefined}>
        {(profiles ?? []).map((profile) => (
          <ProfileRow key={profile.id} profile={profile} formats={formats ?? []} />
        ))}
        {profiles && profiles.length === 0 && !creating && (
          <Text size="sm" c="var(--ink-3)">
            <Trans>No quality profiles yet. Series without one use whatever the instance default is set to.</Trans>
          </Text>
        )}
      </Stack>
    </SettingsSection>
  )
}

function ProfileRow({ profile, formats }: { profile: UpgradeProfileDto; formats: QualityFormatDto[] }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [open, setOpen] = useState(false)
  const update = useUpdateUpgradeProfile()
  const remove = useDeleteUpgradeProfile()
  const [confirming, setConfirming] = useState(false)
  const { name, seriesCount } = profile
  const cutoffLabel = renderLabel(QUALITY_TIER_LABELS[profile.cutoff])

  return (
    <Card withBorder radius="sm" padding="xs">
      <Group justify="space-between" wrap="nowrap">
        <div style={{ minWidth: 0 }}>
          <Group gap="xs" wrap="nowrap">
            <Text fw={600} fz="sm" truncate>
              {name}
            </Text>
            <Badge size="xs" variant="light">
              <Trans>Cutoff: {cutoffLabel}</Trans>
            </Badge>
            <Badge size="xs" variant="outline" color={profile.upgradesEnabled ? 'var(--ok)' : 'var(--neutral)'}>
              {profile.upgradesEnabled ? <Trans>Upgrades on</Trans> : <Trans>Upgrades off</Trans>}
            </Badge>
          </Group>
          <Text fz="xs" c="var(--ink-3)">
            <Plural value={seriesCount} one="# series" other="# series" />
          </Text>
        </div>
        <Group gap={4} wrap="nowrap">
          <Tooltip label={t`Delete profile`} withArrow>
            <ActionIcon
              variant="subtle"
              color="var(--danger)"
              loading={remove.isPending}
              onClick={() => setConfirming(true)}
              aria-label={t`Delete profile`}
            >
              <IconTrash size={16} />
            </ActionIcon>
          </Tooltip>
          <ActionIcon
            variant="subtle"
            color="var(--neutral)"
            onClick={() => setOpen((value) => !value)}
            aria-label={open ? t`Collapse` : t`Edit profile`}
          >
            {open ? <IconChevronUp size={16} /> : <IconChevronDown size={16} />}
          </ActionIcon>
        </Group>
      </Group>

      {open && (
        <ProfileEditor
          initial={profile}
          formats={formats}
          submitLabel={t`Save`}
          busy={update.isPending}
          onCancel={() => setOpen(false)}
          onSubmit={(input) =>
            update.mutate(
              { id: profile.id, ...input },
              { onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }) },
            )
          }
        />
      )}

      <ConfirmDialog
        opened={confirming}
        onClose={() => setConfirming(false)}
        title={<Trans>Delete {name}?</Trans>}
        confirmLabel={<Trans>Delete profile</Trans>}
        loading={remove.isPending}
        onConfirm={() =>
          remove.mutate(profile.id, {
            onSuccess: () => {
              setConfirming(false)
              notifications.show({ message: now`Deleted "${name}"`, color: 'var(--ok)' })
            },
          })
        }
      >
        <Trans>
          Series pinned to this profile fall back to the instance default. This can't be undone.
        </Trans>
      </ConfirmDialog>
    </Card>
  )
}

function ProfileEditor({
  initial,
  formats,
  submitLabel,
  busy,
  onSubmit,
  onCancel,
}: {
  initial: UpgradeProfileInput
  formats: QualityFormatDto[]
  submitLabel: string
  busy: boolean
  onSubmit: (input: UpgradeProfileInput) => void
  onCancel: () => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const [name, setName] = useState(initial.name)
  const [tiers, setTiers] = useState<ProfileTierDto[]>(initial.tiers)
  const [cutoff, setCutoff] = useState<QualityTierName>(initial.cutoff)
  const [upgradesEnabled, setUpgradesEnabled] = useState(initial.upgradesEnabled)
  const [minScoreDelta, setMinScoreDelta] = useState<number | string>(initial.minScoreDelta)
  const [upgradeUntilScore, setUpgradeUntilScore] = useState<number | string>(initial.upgradeUntilScore)
  const [formatScores, setFormatScores] = useState<FormatScoreDto[]>(initial.formatScores)
  const [resolutionWeight, setResolutionWeight] = useState<number | string>(initial.resolutionWeight)
  const [compressionWeight, setCompressionWeight] = useState<number | string>(initial.compressionWeight)
  const [pageTolerancePercent, setPageTolerancePercent] = useState<number | string>(initial.pageTolerancePercent)
  const [allowReplacingUnknown, setAllowReplacingUnknown] = useState(initial.allowReplacingUnknown)

  const allowedTiers = tiers.filter((tier) => tier.allowed).map((tier) => tier.tier)
  const cutoffData = tiers
    .filter((tier) => tier.allowed)
    .map((tier) => ({ value: tier.tier, label: renderLabel(QUALITY_TIER_LABELS[tier.tier]) }))

  const setTierAllowed = (tier: QualityTierName, allowed: boolean) =>
    setTiers((current) => current.map((row) => (row.tier === tier ? { ...row, allowed } : row)))

  return (
    <Stack gap="sm" mt="sm">
      <TextInput
        label={t`Name`}
        value={name}
        maxLength={60}
        onChange={(e) => setName(e.currentTarget.value)}
      />

      <div>
        <Text size="sm" fw={500} mb={4}>
          <Trans>Tier order</Trans>
        </Text>
        <SettingsHelp mb="xs">
          <Trans>Highest priority first. Switch a tier off to never prefer or accept it.</Trans>
        </SettingsHelp>
        <Stack gap={4}>
          {tiers.map((row, index) => (
            <Group key={row.tier} gap="xs" wrap="nowrap" justify="space-between">
              <Group gap="xs" wrap="nowrap">
                <Group gap={2} wrap="nowrap">
                  <ActionIcon
                    size="sm"
                    variant="subtle"
                    color="var(--neutral)"
                    disabled={index === 0}
                    onClick={() => setTiers((current) => moveItem(current, index, -1))}
                    aria-label={t`Move up`}
                  >
                    <IconArrowUp size={14} />
                  </ActionIcon>
                  <ActionIcon
                    size="sm"
                    variant="subtle"
                    color="var(--neutral)"
                    disabled={index === tiers.length - 1}
                    onClick={() => setTiers((current) => moveItem(current, index, 1))}
                    aria-label={t`Move down`}
                  >
                    <IconArrowDown size={14} />
                  </ActionIcon>
                </Group>
                <Text size="sm" fw={500} w={100}>
                  {renderLabel(QUALITY_TIER_LABELS[row.tier])}
                </Text>
              </Group>
              <Switch
                size="sm"
                label={t`Allowed`}
                checked={row.allowed}
                onChange={(e) => setTierAllowed(row.tier, e.currentTarget.checked)}
              />
            </Group>
          ))}
        </Stack>
      </div>

      <Select
        label={t`Cutoff`}
        description={t`Once a file's tier reaches this, upgrading stops (unless upgrade until score says otherwise).`}
        value={allowedTiers.includes(cutoff) ? cutoff : null}
        onChange={(value) => value && setCutoff(value as QualityTierName)}
        data={cutoffData}
        w={260}
      />

      <Switch
        label={t`Upgrades enabled`}
        description={t`When the instance switch is on, the daily scan may replace files of series on this profile with better copies.`}
        checked={upgradesEnabled}
        onChange={(e) => setUpgradesEnabled(e.currentTarget.checked)}
      />

      <Group grow align="flex-start">
        <NumberInput
          label={t`Minimum score gain`}
          description={t`A candidate must beat the current file's score by at least this much.`}
          min={0}
          value={minScoreDelta}
          onChange={setMinScoreDelta}
        />
        <NumberInput
          label={t`Upgrade until score`}
          description={t`Keep upgrading past the cutoff tier until this score is reached. 0 ignores score entirely.`}
          min={0}
          value={upgradeUntilScore}
          onChange={setUpgradeUntilScore}
        />
        <NumberInput
          label={t`Page tolerance %`}
          description={t`How much shorter a candidate's page count may be and still count as an upgrade.`}
          min={0}
          max={100}
          value={pageTolerancePercent}
          onChange={setPageTolerancePercent}
        />
      </Group>

      <div>
        <Text size="sm" fw={500} mb={4}>
          <Trans>Measured quality</Trans>
        </Text>
        <SettingsHelp mb="xs">
          <Trans>
            Points from the pages themselves, so a somewhat sharper or less compressed copy scores somewhat higher. Each
            weight is the points for doubling that measurement. 0 turns it off.
          </Trans>
        </SettingsHelp>
        <Group grow align="flex-start">
          <NumberInput
            label={t`Resolution weight`}
            description={t`Scored on median page width, from 500 to 2000 pixels.`}
            min={0}
            max={50}
            value={resolutionWeight}
            onChange={setResolutionWeight}
          />
          <NumberInput
            label={t`Compression weight`}
            description={t`Scored on image data per pixel, adjusted so PNG and WebP compare fairly with JPG.`}
            min={0}
            max={50}
            value={compressionWeight}
            onChange={setCompressionWeight}
          />
        </Group>
      </div>

      <Switch
        label={t`Allow replacing unknown-tier files`}
        checked={allowReplacingUnknown}
        onChange={(e) => setAllowReplacingUnknown(e.currentTarget.checked)}
      />

      <div>
        <Text size="sm" fw={500} mb={4}>
          <Trans>Format scores</Trans>
        </Text>
        <SettingsHelp mb="xs">
          <Trans>Points added to a file's score for each quality format it matches. 0 means the format isn't scored by this profile.</Trans>
        </SettingsHelp>
        {formats.length === 0 ? (
          <Text size="sm" c="var(--ink-3)">
            <Trans>No quality formats defined yet. Add one below to score files by it.</Trans>
          </Text>
        ) : (
          <Table withRowBorders={false}>
            <Table.Tbody>
              {formats.map((format) => (
                <Table.Tr key={format.id}>
                  <Table.Td>
                    <Text size="sm">{format.name}</Text>
                  </Table.Td>
                  <Table.Td w={120}>
                    <NumberInput
                      size="xs"
                      value={scoreOf(formatScores, format.id)}
                      onChange={(value) =>
                        setFormatScores((current) => withScore(current, format.id, Number(value) || 0))
                      }
                    />
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
      </div>

      <Group justify="flex-end" gap="xs">
        <Button size="xs" variant="subtle" color="var(--neutral)" onClick={onCancel}>
          <Trans>Cancel</Trans>
        </Button>
        <Button
          size="xs"
          loading={busy}
          disabled={name.trim().length === 0}
          onClick={() =>
            onSubmit({
              name: name.trim(),
              tiers,
              cutoff,
              upgradesEnabled,
              minScoreDelta: Number(minScoreDelta) || 0,
              upgradeUntilScore: Number(upgradeUntilScore) || 0,
              formatScores,
              resolutionWeight: Number(resolutionWeight) || 0,
              compressionWeight: Number(compressionWeight) || 0,
              pageTolerancePercent: Number(pageTolerancePercent) || 0,
              allowReplacingUnknown,
            })
          }
        >
          {submitLabel}
        </Button>
      </Group>
    </Stack>
  )
}

const DEFAULT_FORMAT: QualityFormatInput = {
  name: '',
  conditions: [{ type: 'minWidth', value: '', required: true, negate: false }],
}

/**
 * Named bundles of conditions a chapter file can match (a release group, a minimum resolution, an
 * image codec). Formats themselves carry no score; each profile decides what a match is worth,
 * which is why the score table lives on the profile editor above and this section only shapes what
 * "matches" means.
 */
export function QualityFormatsSection() {
  const { t } = useLingui()
  const { data: formats } = useQualityFormats()
  const create = useCreateQualityFormat()
  const [creating, setCreating] = useState(false)

  return (
    <SettingsSection
      id="formats"
      title={<Trans>Quality formats</Trans>}
      description={
        <Trans>
          A format matches a file by its source, its group or release name, its resolution, or its
          image codec. Every required condition has to match, plus at least one condition that isn't
          marked required.
        </Trans>
      }
      actions={
        <Button
          size="xs"
          variant="light"
          leftSection={<IconPlus size={14} />}
          onClick={() => setCreating((open) => !open)}
        >
          <Trans>New format</Trans>
        </Button>
      }
    >

      {creating && (
        <FormatEditor
          key="new"
          initial={DEFAULT_FORMAT}
          submitLabel={t`Create`}
          busy={create.isPending}
          onCancel={() => setCreating(false)}
          onSubmit={(input) =>
            create.mutate(input, {
              onSuccess: () => {
                setCreating(false)
                notifications.show({ message: now`Format created`, color: 'var(--ok)' })
              },
            })
          }
        />
      )}

      <Stack gap="xs" mt={creating ? 'md' : undefined}>
        {(formats ?? []).map((format) => (
          <FormatRow key={format.id} format={format} />
        ))}
        {formats && formats.length === 0 && !creating && (
          <Text size="sm" c="var(--ink-3)">
            <Trans>No quality formats yet.</Trans>
          </Text>
        )}
      </Stack>
    </SettingsSection>
  )
}

function FormatRow({ format }: { format: QualityFormatDto }) {
  const { t } = useLingui()
  const [open, setOpen] = useState(false)
  const update = useUpdateQualityFormat()
  const remove = useDeleteQualityFormat()
  const [confirming, setConfirming] = useState(false)
  const { name, profileCount } = format
  const conditionCount = format.conditions.length

  return (
    <Card withBorder radius="sm" padding="xs">
      <Group justify="space-between" wrap="nowrap">
        <div style={{ minWidth: 0 }}>
          <Group gap="xs" wrap="nowrap">
            <Text fw={600} fz="sm" truncate>
              {name}
            </Text>
            <Badge size="xs" variant="light">
              <Plural value={conditionCount} one="# condition" other="# conditions" />
            </Badge>
          </Group>
          <Text fz="xs" c="var(--ink-3)">
            <Plural value={profileCount} one="Scored in # profile" other="Scored in # profiles" />
          </Text>
        </div>
        <Group gap={4} wrap="nowrap">
          <Tooltip label={t`Delete format`} withArrow>
            <ActionIcon
              variant="subtle"
              color="var(--danger)"
              loading={remove.isPending}
              onClick={() => setConfirming(true)}
              aria-label={t`Delete format`}
            >
              <IconTrash size={16} />
            </ActionIcon>
          </Tooltip>
          <ActionIcon
            variant="subtle"
            color="var(--neutral)"
            onClick={() => setOpen((value) => !value)}
            aria-label={open ? t`Collapse` : t`Edit format`}
          >
            {open ? <IconChevronUp size={16} /> : <IconChevronDown size={16} />}
          </ActionIcon>
        </Group>
      </Group>

      {open && (
        <FormatEditor
          initial={format}
          submitLabel={t`Save`}
          busy={update.isPending}
          onCancel={() => setOpen(false)}
          onSubmit={(input) =>
            update.mutate(
              { id: format.id, ...input },
              { onSuccess: () => notifications.show({ message: now`Saved`, color: 'var(--ok)' }) },
            )
          }
        />
      )}

      <ConfirmDialog
        opened={confirming}
        onClose={() => setConfirming(false)}
        title={<Trans>Delete {name}?</Trans>}
        confirmLabel={<Trans>Delete format</Trans>}
        loading={remove.isPending}
        onConfirm={() =>
          remove.mutate(format.id, {
            onSuccess: () => {
              setConfirming(false)
              notifications.show({ message: now`Deleted "${name}"`, color: 'var(--ok)' })
            },
          })
        }
      >
        <Trans>Removed from every profile that scores it. This can't be undone.</Trans>
      </ConfirmDialog>
    </Card>
  )
}

function isNumericType(type: FormatConditionType): boolean {
  return type === 'minWidth' || type === 'minBytesPerPage' || type === 'minPages'
}

function splitValue(value: string): string[] {
  return value
    .split(',')
    .map((v) => v.trim())
    .filter(Boolean)
}

function FormatEditor({
  initial,
  submitLabel,
  busy,
  onSubmit,
  onCancel,
}: {
  initial: QualityFormatInput
  submitLabel: string
  busy: boolean
  onSubmit: (input: QualityFormatInput) => void
  onCancel: () => void
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const { data: sources } = useSources()
  const [name, setName] = useState(initial.name)
  const [conditions, setConditions] = useState<FormatConditionDto[]>(initial.conditions)

  const sourceOptions = (sources ?? []).map((s) => ({ value: s.name, label: s.displayName }))
  const sourceKindOptions = SOURCE_KINDS.map((kind) => ({ value: kind, label: renderLabel(SOURCE_KIND_LABELS[kind]) }))
  const imageFormatOptions = IMAGE_FORMATS.map((format) => ({ value: format, label: format.toUpperCase() }))

  const updateCondition = (index: number, patch: Partial<FormatConditionDto>) =>
    setConditions((current) => current.map((c, i) => (i === index ? { ...c, ...patch } : c)))

  const removeCondition = (index: number) =>
    setConditions((current) => current.filter((_, i) => i !== index))

  const addCondition = () =>
    setConditions((current) => [...current, { type: 'minWidth', value: '', required: true, negate: false }])

  return (
    <Stack gap="sm" mt="sm">
      <TextInput
        label={t`Name`}
        value={name}
        maxLength={60}
        onChange={(e) => setName(e.currentTarget.value)}
      />

      <div>
        <Text size="sm" fw={500} mb={4}>
          <Trans>Conditions</Trans>
        </Text>
        <Stack gap="xs">
          {conditions.map((condition, index) => (
            <Card key={index} withBorder radius="sm" padding="xs">
              <Group align="flex-end" gap="xs" wrap="wrap">
                <Select
                  label={t`Type`}
                  value={condition.type}
                  onChange={(value) =>
                    value && updateCondition(index, { type: value as FormatConditionType, value: '' })
                  }
                  data={FORMAT_CONDITION_TYPES.map((type) => ({
                    value: type,
                    label: renderLabel(FORMAT_CONDITION_TYPE_LABELS[type]),
                  }))}
                  w={220}
                />

                {condition.type === 'sourceIs' && (
                  <MultiSelect
                    label={t`Sources`}
                    value={splitValue(condition.value)}
                    onChange={(values) => updateCondition(index, { value: values.join(',') })}
                    data={sourceOptions}
                    searchable
                    w={260}
                  />
                )}
                {condition.type === 'sourceKindIs' && (
                  <MultiSelect
                    label={t`Kinds`}
                    value={splitValue(condition.value)}
                    onChange={(values) => updateCondition(index, { value: values.join(',') })}
                    data={sourceKindOptions}
                    w={260}
                  />
                )}
                {condition.type === 'imageFormatIs' && (
                  <MultiSelect
                    label={t`Image formats`}
                    value={splitValue(condition.value)}
                    onChange={(values) => updateCondition(index, { value: values.join(',') })}
                    data={imageFormatOptions}
                    w={260}
                  />
                )}
                {(condition.type === 'groupMatches' || condition.type === 'releaseNameMatches') && (
                  <TextInput
                    label={t`Regex`}
                    value={condition.value}
                    onChange={(e) => updateCondition(index, { value: e.currentTarget.value })}
                    w={260}
                  />
                )}
                {condition.type === 'languageIs' && (
                  <TextInput
                    label={t`Language codes`}
                    placeholder="en, ja"
                    value={condition.value}
                    onChange={(e) => updateCondition(index, { value: e.currentTarget.value })}
                    w={260}
                  />
                )}
                {isNumericType(condition.type) && (
                  <NumberInput
                    label={t`Value`}
                    min={0}
                    allowDecimal={false}
                    allowNegative={false}
                    value={condition.value === '' ? '' : Number(condition.value)}
                    onChange={(value) => updateCondition(index, { value: value === '' ? '' : String(value) })}
                    w={140}
                  />
                )}

                <Switch
                  label={t`Required`}
                  checked={condition.required}
                  onChange={(e) => updateCondition(index, { required: e.currentTarget.checked })}
                />
                <Switch
                  label={t`Negate`}
                  checked={condition.negate}
                  onChange={(e) => updateCondition(index, { negate: e.currentTarget.checked })}
                />

                <ActionIcon
                  variant="subtle"
                  color="var(--danger)"
                  onClick={() => removeCondition(index)}
                  disabled={conditions.length <= 1}
                  aria-label={t`Remove condition`}
                >
                  <IconTrash size={16} />
                </ActionIcon>
              </Group>
            </Card>
          ))}
        </Stack>
        <Button
          size="xs"
          variant="subtle"
          mt="xs"
          leftSection={<IconPlus size={14} />}
          onClick={addCondition}
        >
          <Trans>Add condition</Trans>
        </Button>
      </div>

      <Group justify="flex-end" gap="xs">
        <Button size="xs" variant="subtle" color="var(--neutral)" onClick={onCancel}>
          <Trans>Cancel</Trans>
        </Button>
        <Button
          size="xs"
          loading={busy}
          disabled={name.trim().length === 0 || conditions.length === 0}
          onClick={() => onSubmit({ name: name.trim(), conditions })}
        >
          {submitLabel}
        </Button>
      </Group>
    </Stack>
  )
}
