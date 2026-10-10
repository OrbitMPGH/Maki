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
  Textarea,
  Tooltip,
} from '@mantine/core'
import { notifications } from '@mantine/notifications'
import { SettingsNumberInput } from './SettingsNumberInput'
import {
  IconArrowDown,
  IconArrowUp,
  IconChevronDown,
  IconChevronUp,
  IconLink,
  IconLinkOff,
  IconPlus,
  IconTrash,
} from '@tabler/icons-react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { msg, t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { EmptyState } from '../ui/EmptyState'
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
  useUpgradeSettings,
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
import { useReportUnsaved } from './SaveButton'
import { useLabel } from '../../i18n-context'

/** Highest priority first, matches `UpgradeProfileDefaults.DefaultOrder` on the server. */
const DEFAULT_TIER_ORDER: QualityTierName[] = ['volume', 'official', 'scanlator', 'aggregator', 'unknown']

/** How far a profile upgrades, in words, per cutoff tier. Descriptors, rendered at the call site. */
const CUTOFF_SUMMARIES: Record<QualityTierName, MessageDescriptor> = {
  unknown: msg`Upgrades files Maki knows nothing about, then stops`,
  aggregator: msg`Upgrades until each chapter has any known copy`,
  scanlator: msg`Upgrades until each chapter is from a scanlator or an official source`,
  official: msg`Upgrades until each chapter is from an official source`,
  volume: msg`Upgrades until chapters are covered by a digital volume`,
}

/** Rows split into rank groups, highest first. A grouped row joins the group above it. */
function tierGroups(tiers: ProfileTierDto[]): ProfileTierDto[][] {
  const groups: ProfileTierDto[][] = []
  for (const row of tiers) {
    if (row.grouped && groups.length > 0) groups[groups.length - 1].push(row)
    else groups.push([row])
  }
  return groups
}

/** Every tier in a group ranks the same, so a cutoff means its whole group; named by the group's lowest allowed tier. */
function groupCutoff(tiers: ProfileTierDto[], cutoff: QualityTierName): QualityTierName | null {
  const allowed = tierGroups(tiers).find((group) => group.some((row) => row.tier === cutoff))?.filter((row) => row.allowed) ?? []
  return allowed.length > 0 ? allowed[allowed.length - 1].tier : null
}

const DEFAULT_PROFILE: UpgradeProfileInput = {
  name: '',
  description: null,
  tiers: DEFAULT_TIER_ORDER.map((tier) => ({ tier, allowed: true, grouped: false })),
  cutoff: 'aggregator',
  upgradesEnabled: false,
  minScoreDelta: 1,
  maxTierScoreDrop: 10,
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

/** Moves the tier, not the grouping: each position keeps its link to the row above. */
function moveTier(tiers: ProfileTierDto[], index: number, direction: -1 | 1): ProfileTierDto[] {
  return moveItem(tiers, index, direction).map((row, i) => ({ ...row, grouped: tiers[i].grouped }))
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
          A profile picks which release tier Maki prefers for a series and how far it goes to
          replace what's already downloaded. Pin one to a series from its Quality profile menu.
          The default for every other series is set under Upgrade scan below.
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
  const { data: upgradeSettings } = useUpgradeSettings()
  const [confirming, setConfirming] = useState(false)
  const { name, seriesCount, upgradeUntilScore } = profile
  const deleteBlocker =
    seriesCount > 0
      ? t`Unpin it from every series first`
      : upgradeSettings?.defaultProfileId === profile.id
        ? t`Pick another default profile first`
        : null

  return (
    <Card withBorder radius="sm" padding="xs">
      <Group justify="space-between" wrap="nowrap" align="flex-start">
        <div style={{ minWidth: 0 }}>
          <Text fw={600} fz="sm" truncate>
            {name}
          </Text>
          <Text fz="xs" fw={500} c={profile.upgradesEnabled ? 'var(--ok)' : 'var(--ink-3)'}>
            {!profile.upgradesEnabled ? (
              <Trans>Never upgrades</Trans>
            ) : upgradeUntilScore > 0 ? (
              <Trans>Keeps taking better scoring copies until one scores {upgradeUntilScore}</Trans>
            ) : (
              renderLabel(CUTOFF_SUMMARIES[groupCutoff(profile.tiers, profile.cutoff) ?? profile.cutoff])
            )}
          </Text>
          {profile.description && (
            <Text fz="xs" c="var(--ink-2)" mt={2}>
              {profile.description}
            </Text>
          )}
          <Text fz="xs" c="var(--ink-3)" mt={2}>
            <Plural value={seriesCount} one="# series" other="# series" />
          </Text>
        </div>
        <Group gap={4} wrap="nowrap">
          <Tooltip label={deleteBlocker ?? t`Delete profile`} withArrow>
            <span>
              <ActionIcon
                variant="subtle"
                color="var(--danger)"
                loading={remove.isPending}
                disabled={deleteBlocker !== null}
                onClick={() => setConfirming(true)}
                aria-label={t`Delete profile`}
              >
                <IconTrash size={16} />
              </ActionIcon>
            </span>
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
        <Trans>The profile is removed. This can't be undone.</Trans>
      </ConfirmDialog>
    </Card>
  )
}

function profileFingerprint(profile: UpgradeProfileInput): string {
  return JSON.stringify({
    name: profile.name.trim(),
    description: profile.description?.trim() || null,
    tiers: profile.tiers.map((t) => [t.tier, t.allowed, t.grouped]),
    cutoff: groupCutoff(profile.tiers, profile.cutoff) ?? profile.cutoff,
    upgradesEnabled: profile.upgradesEnabled,
    minScoreDelta: profile.minScoreDelta,
    maxTierScoreDrop: profile.maxTierScoreDrop,
    upgradeUntilScore: profile.upgradeUntilScore,
    formatScores: profile.formatScores
      .filter((f) => f.score !== 0)
      .map((f) => [f.formatId, f.score])
      .sort((x, y) => x[0] - y[0]),
    resolutionWeight: profile.resolutionWeight,
    compressionWeight: profile.compressionWeight,
    pageTolerancePercent: profile.pageTolerancePercent,
    allowReplacingUnknown: profile.allowReplacingUnknown,
  })
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
  const [description, setDescription] = useState(initial.description ?? '')
  const [tiers, setTiers] = useState<ProfileTierDto[]>(initial.tiers)
  const [cutoff, setCutoff] = useState<QualityTierName>(initial.cutoff)
  const [upgradesEnabled, setUpgradesEnabled] = useState(initial.upgradesEnabled)
  const [minScoreDelta, setMinScoreDelta] = useState<number>(initial.minScoreDelta)
  const [maxTierScoreDrop, setMaxTierScoreDrop] = useState<number | string>(initial.maxTierScoreDrop ?? '')
  const [upgradeUntilScore, setUpgradeUntilScore] = useState<number>(initial.upgradeUntilScore)
  const [formatScores, setFormatScores] = useState<FormatScoreDto[]>(initial.formatScores)
  const [resolutionWeight, setResolutionWeight] = useState<number>(initial.resolutionWeight)
  const [compressionWeight, setCompressionWeight] = useState<number>(initial.compressionWeight)
  const [pageTolerancePercent, setPageTolerancePercent] = useState<number>(initial.pageTolerancePercent)
  const [allowReplacingUnknown, setAllowReplacingUnknown] = useState(initial.allowReplacingUnknown)

  const groups = tierGroups(tiers)
  const cutoffData = groups
    .map((group) => group.filter((row) => row.allowed))
    .filter((allowed) => allowed.length > 0)
    .map((allowed) => ({
      value: allowed[allowed.length - 1].tier,
      label: allowed.map((row) => renderLabel(QUALITY_TIER_LABELS[row.tier])).join(' + '),
    }))
  const effectiveCutoff = groupCutoff(tiers, cutoff)
  const draft: UpgradeProfileInput = {
    name: name.trim(),
    description: description.trim() || null,
    tiers,
    cutoff: effectiveCutoff ?? cutoff,
    upgradesEnabled,
    minScoreDelta,
    maxTierScoreDrop: maxTierScoreDrop === '' ? null : Number(maxTierScoreDrop) || 0,
    upgradeUntilScore,
    formatScores,
    resolutionWeight,
    compressionWeight,
    pageTolerancePercent,
    allowReplacingUnknown,
  }
  useReportUnsaved(profileFingerprint(draft) !== profileFingerprint(initial))

  const setTierAllowed = (tier: QualityTierName, allowed: boolean) =>
    setTiers((current) => current.map((row) => (row.tier === tier ? { ...row, allowed } : row)))
  const toggleGrouped = (index: number) =>
    setTiers((current) => current.map((row, i) => (i === index ? { ...row, grouped: !row.grouped } : row)))

  const tierRow = (row: ProfileTierDto, index: number) => {
    const tierLabel = renderLabel(QUALITY_TIER_LABELS[row.tier])
    return (
      <Group key={row.tier} gap="xs" wrap="nowrap" justify="space-between">
        <Group gap="xs" wrap="nowrap">
          <Group gap={2} wrap="nowrap">
            <ActionIcon
              size="sm"
              variant="subtle"
              color="var(--neutral)"
              disabled={index === 0}
              onClick={() => setTiers((current) => moveTier(current, index, -1))}
              aria-label={t`Move ${tierLabel} up`}
            >
              <IconArrowUp size={14} />
            </ActionIcon>
            <ActionIcon
              size="sm"
              variant="subtle"
              color="var(--neutral)"
              disabled={index === tiers.length - 1}
              onClick={() => setTiers((current) => moveTier(current, index, 1))}
              aria-label={t`Move ${tierLabel} down`}
            >
              <IconArrowDown size={14} />
            </ActionIcon>
          </Group>
          <Text size="sm" fw={500} w={100}>
            {renderLabel(QUALITY_TIER_LABELS[row.tier])}
          </Text>
          {index > 0 && (
            <Tooltip label={row.grouped ? t`Split from the tier above` : t`Group with the tier above`}>
              <ActionIcon
                size="sm"
                variant={row.grouped ? 'light' : 'subtle'}
                color={row.grouped ? undefined : 'var(--neutral)'}
                onClick={() => toggleGrouped(index)}
                aria-label={row.grouped ? t`Split from the tier above` : t`Group with the tier above`}
                aria-pressed={row.grouped}
              >
                {row.grouped ? <IconLink size={14} /> : <IconLinkOff size={14} />}
              </ActionIcon>
            </Tooltip>
          )}
        </Group>
        <Switch
          size="sm"
          label={t`Allowed`}
          checked={row.allowed}
          onChange={(e) => setTierAllowed(row.tier, e.currentTarget.checked)}
        />
      </Group>
    )
  }

  return (
    <Stack gap="sm" mt="sm">
      <TextInput
        label={t`Name`}
        value={name}
        maxLength={60}
        onChange={(e) => setName(e.currentTarget.value)}
      />
      <Textarea
        label={t`Description`}
        description={t`What picking this profile does, shown in the profile list.`}
        value={description}
        maxLength={300}
        autosize
        minRows={2}
        onChange={(e) => setDescription(e.currentTarget.value)}
      />

      <div>
        <Text size="sm" fw={500} mb={4}>
          <Trans>Tier order</Trans>
        </Text>
        <SettingsHelp mb="xs">
          <Trans>
            Highest priority first. Switch a tier off to never prefer or accept it. Grouped tiers rank the same, so
            the better scoring copy wins between them. Volume is a digital volume release, Official the
            publisher's own site, Scanlator a translation group, Aggregator a site that reposts others'
            releases.
          </Trans>
        </SettingsHelp>
        <Stack gap={4}>
          {groups.map((group) => {
            const first = tiers.indexOf(group[0])
            const rows = group.map((row, i) => tierRow(row, first + i))
            return group.length > 1 ? (
              <Stack
                key={group[0].tier}
                gap={4}
                p={4}
                style={{ border: '1px solid var(--hairline)', borderRadius: 'var(--mantine-radius-sm)' }}
              >
                {rows}
              </Stack>
            ) : (
              rows
            )
          })}
        </Stack>
      </div>

      <Select
        label={t`Cutoff`}
        description={t`Once a file's tier reaches this, upgrading stops (unless upgrade until score says otherwise). A grouped tier's cutoff is its whole group.`}
        value={effectiveCutoff}
        onChange={(value) => value && setCutoff(value as QualityTierName)}
        data={cutoffData}
        w={260}
      />

      <Switch
        label={t`Let the daily scan upgrade this profile's series`}
        description={t`When the instance switch is on, the daily scan may replace files of series on this profile with better copies.`}
        checked={upgradesEnabled}
        onChange={(e) => setUpgradesEnabled(e.currentTarget.checked)}
      />

      <Group grow align="flex-start">
        <SettingsNumberInput
          label={t`Minimum score gain`}
          description={t`Within the same tier, a candidate must beat the current file's score by at least this much. A higher tier is governed by the next field.`}
          min={0}
          value={minScoreDelta}
          onChange={setMinScoreDelta}
        />
        <NumberInput
          label={t`Maximum score loss for a higher tier`}
          description={t`A higher tier still replaces the current file when its score is at most this much lower. Leave empty to let a higher tier always win.`}
          min={0}
          allowDecimal={false}
          value={maxTierScoreDrop}
          onChange={setMaxTierScoreDrop}
        />
      </Group>
      <Group grow align="flex-start">
        <SettingsNumberInput
          label={t`Upgrade until score`}
          description={t`Keep taking better scoring copies once the cutoff is reached, until a file scores this much. 0 stops at the cutoff. Set it very high to never stop.`}
          min={0}
          value={upgradeUntilScore}
          onChange={setUpgradeUntilScore}
        />
        <SettingsNumberInput
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
          <SettingsNumberInput
            label={t`Resolution weight`}
            description={t`Scored on median page width, from 500 to 2000 pixels.`}
            min={0}
            max={50}
            value={resolutionWeight}
            onChange={setResolutionWeight}
          />
          <SettingsNumberInput
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
                      aria-label={t`Score for ${format.name}`}
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
          onClick={() => onSubmit(draft)}
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
          A format matches a file by its source, kind, release group or name, resolution, image
          codec, size per page, page count or language. Every required condition must match; when a
          format also has optional conditions, at least one of those must match too. Scores for a
          format are set inside each profile.
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
          <EmptyState compact mood="asleep" title={t`No quality formats yet.`} />
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

function formatFingerprint(format: QualityFormatInput): string {
  const conditions = format.conditions.map((c) => ({ type: c.type, value: c.value, required: c.required, negate: c.negate }))
  return JSON.stringify({ name: format.name.trim(), conditions })
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
  useReportUnsaved(formatFingerprint({ name, conditions }) !== formatFingerprint(initial))

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
