import { useState, type FormEvent } from 'react'
import {
  Badge,
  Button,
  Group,
  Modal,
  Popover,
  SegmentedControl,
  Skeleton,
  Stack,
  Switch,
  Text,
  TextInput,
  UnstyledButton,
} from '@mantine/core'
import { useMediaQuery } from '@mantine/hooks'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { msg, plural } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import {
  IconBrandDiscord,
  IconChevronRight,
  IconEye,
  IconEyeOff,
  IconMegaphone,
  IconPencil,
  IconPhoto,
  IconUsers,
} from '../../lib/icons'
import { notifications } from '../../lib/toast'
import { useLabel } from '../../i18n-context'
import { formatNumber } from '../../format'
import {
  creditNoticeImageUrl,
  useApplyCreditGroups,
  useCreditGroupDetail,
  useCreditGroups,
  useNameCreditNotices,
  useUpdateCreditNotice,
  type ApplyCreditGroupsResult,
  type CreditGroup,
  type CreditGroupSeries,
  type CreditNameSource,
  type CreditNotice,
  type CreditNoticeOverride,
} from '../../api/creditGroups'
import { DrillBack } from '../ui/DrillPanel'
import { EmptyState } from '../ui/EmptyState'
import { MetaList } from '../ui/MetaList'
import { ModalFooter, ModalHeading, ModalNote } from '../ui/ModalShell'
import { MODAL_SIZE } from '../ui/modalSizes'

const PHONE = '(max-width: 47.9375em)'
/** Chapters listed per series before "Show all". */
const CHAPTERS_SHOWN = 8

const NAME_SOURCES: Record<Exclude<CreditNameSource, 'none'>, { label: MessageDescriptor; hint: MessageDescriptor; color: string }> = {
  confirmed: { label: msg`Confirmed`, hint: msg`Set by you.`, color: 'var(--brand)' },
  propagated: {
    label: msg`From chapters`,
    hint: msg`Taken from chapters that already carry this group.`,
    color: 'var(--ok)',
  },
  suggested: {
    label: msg`Suggested`,
    hint: msg`Read off the image text, which gets the name right about two times in three. Confirm or correct it before giving it to chapters.`,
    color: 'var(--warn)',
  },
}

const OVERRIDE_TO_WIRE = { none: 'None', hide: 'Hide', keep: 'Keep' } as const

function useGroupTitle() {
  const { t } = useLingui()
  return (group: CreditGroup) =>
    group.kind === 'site' ? t`Site ads` : (group.name ?? t`No group name`)
}

/** Whether giving this group to chapters can run now, and if not, why. */
function applyState(group: CreditGroup): 'ready' | 'confirm' | 'none' | 'never' {
  if (group.kind === 'site' || !group.name) return 'never'
  if (group.nameSource === 'suggested' || group.kind !== 'group') return 'confirm'
  return group.chaptersWithoutGroup > 0 ? 'ready' : 'none'
}

/**
 * The release groups behind hidden credit pages and ads, opened from Settings > Library > Credit
 * pages. Groups on one side, the chosen group's notices, name and chapters on the other; on a phone
 * the two are drill-in pages.
 */
export function CreditGroupsModal({ opened, onClose }: { opened: boolean; onClose: () => void }) {
  const { t } = useLingui()
  const phone = useMediaQuery(PHONE) ?? false
  const [all, setAll] = useState(false)
  const [selected, setSelected] = useState<{ key: string; ids: number[] } | null>(null)
  const [applying, setApplying] = useState<CreditGroup | null>(null)
  const { data: groups, isLoading, isError, refetch } = useCreditGroups(all, opened)
  const titleOf = useGroupTitle()

  // A rename moves a group to a new key, so fall back to whichever group now holds its notices.
  const resolved =
    (selected &&
      (groups?.find((g) => g.key === selected.key) ??
        groups?.find((g) => g.noticeIds.some((id) => selected.ids.includes(id))))) ||
    null
  const current = resolved ?? (phone ? null : (groups?.[0] ?? null))
  const select = (group: CreditGroup | null) => setSelected(group && { key: group.key, ids: group.noticeIds })

  const close = () => {
    setSelected(null)
    setApplying(null)
    onClose()
  }

  const noticeTotal = (groups ?? []).reduce((sum, g) => sum + g.notices, 0)
  const groupCount = (groups ?? []).filter((g) => g.name && g.kind !== 'site').length
  const showDetail = current != null && (!phone || resolved != null)
  const currentMissing = current?.chaptersWithoutGroup ?? 0

  return (
    <Modal
      opened={opened}
      onClose={close}
      size={MODAL_SIZE.wide}
      title={
        <ModalHeading
          title={t`Release groups`}
          description={
            <Trans>The credit pages and ads Maki found, grouped by the release group they name.</Trans>
          }
        />
      }
    >
      {!(phone && resolved != null) && (
        <div className="credit-groups-rail">
          <SegmentedControl
            size="xs"
            aria-label={t`Notices to list`}
            value={all ? 'all' : 'hidden'}
            onChange={(value) => setAll(value === 'all')}
            data={[
              { value: 'hidden', label: t`Hidden and named` },
              { value: 'all', label: t`Every notice` },
            ]}
          />
        </div>
      )}

      {isLoading ? (
        <GroupsSkeleton />
      ) : isError ? (
        <EmptyState compact title={t`Couldn't load the release groups`} actionLabel={t`Retry`} onAction={() => void refetch()} />
      ) : (groups ?? []).length === 0 ? (
        <EmptyState
          compact
          mood="asleep"
          title={all ? t`No notices yet` : t`No hidden notices yet`}
          description={
            <Trans>
              A notice appears once Maki finds the same credit page or ad in several chapters, in the background search
              or when a chapter is opened.
            </Trans>
          }
        />
      ) : (
        <div className="credit-groups-split" data-detail={showDetail || undefined}>
          {(!phone || !showDetail) && (
            <GroupList groups={groups ?? []} current={current} onSelect={select} />
          )}
          {showDetail && current && (
            <div className="credit-groups-detail">
              {phone && (
                <DrillBack title={<Trans>Release groups</Trans>} onBack={() => select(null)} className="credit-groups-back" />
              )}
              <GroupDetail key={current.key} group={current} groups={groups ?? []} />
            </div>
          )}
        </div>
      )}

      {(groups ?? []).length > 0 && (
        <ModalFooter
          status={
            showDetail && current ? (
              <ApplyStatus group={current} />
            ) : (
              <MetaList
                inline
                items={[
                  <Plural key="groups" value={groupCount} one="# group" other="# groups" />,
                  <Plural key="notices" value={noticeTotal} one="# notice" other="# notices" />,
                ]}
              />
            )
          }
        >
          {showDetail && current && applyState(current) !== 'never' && (
            <Button
              leftSection={<IconUsers size={16} />}
              disabled={applyState(current) !== 'ready'}
              onClick={() => setApplying(current)}
            >
              <Plural
                value={currentMissing}
                _0="Give to chapters"
                one="Give to # chapter"
                other="Give to # chapters"
              />
            </Button>
          )}
        </ModalFooter>
      )}

      <ApplyDialog group={applying} title={applying ? titleOf(applying) : ''} onClose={() => setApplying(null)} />
    </Modal>
  )
}

function ApplyStatus({ group }: { group: CreditGroup }) {
  const missing = group.chaptersWithoutGroup
  switch (applyState(group)) {
    case 'confirm':
      return <Trans>Confirm the name before giving it to chapters.</Trans>
    case 'none':
      return <Trans>Every chapter here already has a group.</Trans>
    case 'ready':
      return <Plural value={missing} one="# chapter has no group yet" other="# chapters have no group yet" />
    default:
      return group.kind === 'site' ? (
        <Trans>Site ads never give a group to chapters.</Trans>
      ) : (
        <Trans>Name a notice to give its group to chapters.</Trans>
      )
  }
}

function NameSourcePill({ source }: { source: CreditNameSource }) {
  const renderLabel = useLabel()
  if (source === 'none') return null
  const visual = NAME_SOURCES[source]
  return (
    <Badge size="xs" variant="light" color={visual.color} className="credit-pill">
      {renderLabel(visual.label)}
    </Badge>
  )
}

function GroupList({
  groups,
  current,
  onSelect,
}: {
  groups: CreditGroup[]
  current: CreditGroup | null
  onSelect: (group: CreditGroup) => void
}) {
  const { t } = useLingui()
  const titleOf = useGroupTitle()
  return (
    <nav className="credit-groups-list" aria-label={t`Release groups`}>
      <div className="modal-list">
        {groups.map((group) => {
          const active = current?.key === group.key
          const { chapters, chaptersWithoutGroup: missing } = group
          return (
            <UnstyledButton
              key={group.key}
              className="modal-list-row credit-group-row"
              data-active={active || undefined}
              aria-current={active || undefined}
              onClick={() => onSelect(group)}
            >
              <span className="credit-group-row-text">
                <span className="credit-group-row-line">
                  {group.kind === 'site' && <IconMegaphone size={14} className="credit-group-row-icon" aria-hidden />}
                  <Text component="span" size="sm" fw={600} truncate c={group.name || group.kind === 'site' ? undefined : 'var(--ink-3)'}>
                    {titleOf(group)}
                  </Text>
                  <NameSourcePill source={group.nameSource} />
                </span>
                <Text component="span" size="xs" c="var(--ink-3)">
                  <MetaList
                    inline
                    items={[
                      plural(chapters, { one: '# chapter', other: '# chapters' }),
                      group.name && group.kind !== 'site' && missing > 0 && (
                        <span key="missing" className="credit-group-missing">
                          {plural(missing, { one: '# without a group', other: '# without a group' })}
                        </span>
                      ),
                    ]}
                  />
                </Text>
              </span>
              <IconChevronRight size={16} className="credit-group-row-chevron" aria-hidden />
            </UnstyledButton>
          )
        })}
      </div>
    </nav>
  )
}

function GroupDetail({ group, groups }: { group: CreditGroup; groups: CreditGroup[] }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const titleOf = useGroupTitle()
  const { data, isLoading, isError, refetch } = useCreditGroupDetail(group.noticeIds)
  const knownNames = groups.filter((g) => g.kind === 'group' && g.name && g.key !== group.key).map((g) => g.name!)
  const groupTitle = titleOf(group)
  const { notices, series, chapters } = group

  return (
    <Stack gap="lg">
      <section className="credit-group-head" aria-label={groupTitle}>
        <Group gap="xs" wrap="wrap">
          {group.kind === 'site' && <IconMegaphone size={18} className="credit-group-head-icon" aria-hidden />}
          <Text fw={600} size="lg" className="credit-group-title">
            {groupTitle}
          </Text>
          <NameSourcePill source={group.nameSource} />
        </Group>
        <Text size="xs" c="var(--ink-3)">
          <MetaList
            inline
            items={[
              plural(notices, { one: '# notice', other: '# notices' }),
              plural(series, { one: '# series', other: '# series' }),
              plural(chapters, { one: '# chapter', other: '# chapters' }),
            ]}
          />
        </Text>
        {group.kind === 'site' ? (
          <ModalNote tone="neutral">
            <Trans>Ads an aggregator site pastes into chapters. They never name a group, so they never give one to chapters.</Trans>
          </ModalNote>
        ) : group.key === 'unnamed' ? (
          <ModalNote tone="neutral">
            <Trans>Maki could not read a group name off these. Name each notice below, or mark it as a site ad.</Trans>
          </ModalNote>
        ) : (
          <>
            {group.nameSource !== 'none' && (
              <Text size="sm" c="var(--ink-2)">
                {renderLabel(NAME_SOURCES[group.nameSource].hint)}
              </Text>
            )}
            <GroupNameForm key={group.name ?? ''} group={group} />
          </>
        )}
      </section>

      {isLoading ? (
        <Stack gap="sm" aria-hidden>
          <Skeleton h={140} radius="md" />
          <Skeleton h={140} radius="md" />
        </Stack>
      ) : isError || !data ? (
        <EmptyState compact title={t`Couldn't load these notices`} actionLabel={t`Retry`} onAction={() => void refetch()} />
      ) : (
        <>
          <section aria-label={t`Notices`}>
            <Text size="sm" fw={500} mb="xs">
              <Trans>Notices</Trans>
            </Text>
            <Stack gap="sm">
              {data.notices.map((notice) => (
                <NoticeCard key={notice.id} notice={notice} knownNames={knownNames} />
              ))}
            </Stack>
          </section>
          <section aria-label={t`Where it appears`}>
            <Text size="sm" fw={500} mb="xs">
              <Trans>Where it appears</Trans>
            </Text>
            {data.series.length === 0 ? (
              <Text size="sm" c="var(--ink-3)">
                <Trans>No chapter in the library carries these any more.</Trans>
              </Text>
            ) : (
              <Stack gap="md">
                {data.series.map((series) => (
                  <SeriesChapters key={series.seriesId} series={series} />
                ))}
              </Stack>
            )}
          </section>
        </>
      )}
    </Stack>
  )
}

function GroupNameForm({ group }: { group: CreditGroup }) {
  const { t } = useLingui()
  const name = useNameCreditNotices()
  const [draft, setDraft] = useState(group.name ?? '')

  const typed = draft.trim()
  const changed = typed !== (group.name ?? '')
  const confirmed = group.nameSource === 'confirmed'

  const submit = (e: FormEvent) => {
    e.preventDefault()
    if (!typed || (!changed && confirmed)) return
    name.mutate(
      { noticeIds: group.noticeIds, name: typed },
      {
        onSuccess: ({ named }) =>
          notifications.show({
            message: plural(named, { one: `Named # notice ${typed}`, other: `Named # notices ${typed}` }),
            color: 'var(--ok)',
          }),
        onError: (error) => notifications.show({ message: error.message, color: 'var(--danger)' }),
      },
    )
  }

  return (
    <Group component="form" onSubmit={submit} gap="xs" align="flex-end" wrap="wrap" className="credit-group-name-form">
      <TextInput
        label={t`Group name`}
        value={draft}
        maxLength={100}
        onChange={(e) => setDraft(e.currentTarget.value)}
        style={{ flex: '1 1 14rem' }}
      />
      {(changed || !confirmed) && (
        <Button type="submit" variant={changed ? 'filled' : 'default'} disabled={!typed} loading={name.isPending}>
          {changed ? <Trans>Save name</Trans> : <Trans>Confirm name</Trans>}
        </Button>
      )}
    </Group>
  )
}

function NoticeImage({ notice }: { notice: CreditNotice }) {
  const { t } = useLingui()
  const [failed, setFailed] = useState(false)
  if (failed) {
    return (
      <div className="credit-notice-image" data-empty>
        <IconPhoto size={20} aria-hidden />
        <Text size="xs" c="var(--ink-3)">
          <Trans>No chapter that carries it can be read right now.</Trans>
        </Text>
      </div>
    )
  }
  const url = creditNoticeImageUrl(notice.id)
  return (
    <a className="credit-notice-image" href={url} target="_blank" rel="noreferrer" aria-label={t`Open the image`}>
      <img src={url} alt="" loading="lazy" decoding="async" onError={() => setFailed(true)} />
    </a>
  )
}

function NoticeCard({ notice, knownNames }: { notice: CreditNotice; knownNames: string[] }) {
  const { t } = useLingui()
  const update = useUpdateCreditNotice()
  const pending = update.isPending ? update.variables?.change : undefined
  const override: CreditNoticeOverride =
    pending?.override != null ? (pending.override.toLowerCase() as CreditNoticeOverride) : notice.override
  const site = pending?.kind != null ? pending.kind === 'Site' : notice.kind === 'site'
  // A strip from the top or bottom of a page sits over the text; a whole or most of a page beside it.
  const shape = notice.whole || notice.share > 0.5 ? 'page' : 'strip'
  const percent = formatNumber(Math.round(notice.share * 100))
  const chapters = notice.chapters
  const series = notice.series
  const { invite, excerpt, id: noticeId } = notice

  const change = (body: Parameters<typeof update.mutate>[0]['change']) =>
    update.mutate(
      { id: notice.id, change: body },
      { onError: (error) => notifications.show({ message: error.message, color: 'var(--danger)' }) },
    )

  return (
    <article className="credit-notice" data-shape={shape} aria-label={t`Notice ${noticeId}`}>
      <NoticeImage notice={notice} />
      <div className="credit-notice-body">
        <Group gap={6} wrap="wrap">
          <Badge
            size="sm"
            variant="light"
            color={notice.override === 'none' ? 'var(--neutral)' : 'var(--brand)'}
            leftSection={notice.hidden ? <IconEyeOff size={12} /> : <IconEye size={12} />}
            className="credit-pill"
          >
            {notice.hidden ? <Trans>Hidden</Trans> : <Trans>Kept</Trans>}
          </Badge>
          {notice.kind === 'site' && (
            <Badge size="sm" variant="light" color="var(--q-aggregator)" leftSection={<IconMegaphone size={12} />} className="credit-pill">
              <Trans>Site ad</Trans>
            </Badge>
          )}
          <Text component="span" size="xs" c="var(--ink-3)">
            <MetaList
              inline
              items={[
                notice.whole ? t`Whole page` : notice.tail ? t`Bottom ${percent}% of a page` : t`Top ${percent}% of a page`,
                <Plural
                  key="where"
                  value={chapters}
                  one={`# chapter in ${series} series`}
                  other={`# chapters in ${series} series`}
                />,
              ]}
            />
          </Text>
        </Group>

        {excerpt && (
          <Text size="xs" className="credit-notice-excerpt" lineClamp={3} title={excerpt}>
            {excerpt}
          </Text>
        )}
        {invite && (
          <Text size="xs" c="var(--ink-3)" className="credit-notice-invite">
            <IconBrandDiscord size={14} aria-hidden /> discord.gg/{invite}
          </Text>
        )}

        <NoticeName notice={notice} knownNames={knownNames} onSave={(groupName) => change({ groupName })} busy={update.isPending} />

        <div className="credit-notice-controls">
          <div>
            <Text size="xs" fw={500} mb={4} id={`credit-notice-${notice.id}-readers`}>
              <Trans>Readers get it</Trans>
            </Text>
            <SegmentedControl
              size="xs"
              aria-labelledby={`credit-notice-${notice.id}-readers`}
              value={override}
              onChange={(value) => change({ override: OVERRIDE_TO_WIRE[value as CreditNoticeOverride] })}
              data={[
                { value: 'none', label: notice.accepted ? t`Automatic (hidden)` : t`Automatic (kept)` },
                { value: 'hide', label: t`Hidden` },
                { value: 'keep', label: t`Kept` },
              ]}
            />
          </div>
          <Switch
            size="sm"
            label={t`Site ad`}
            checked={site}
            onChange={(e) => change({ kind: e.currentTarget.checked ? 'Site' : 'Unknown' })}
          />
        </div>
        <Text size="xs" c="var(--ink-3)">
          <Trans>A change reaches readers once Maki has searched its chapters again in the background.</Trans>
        </Text>
      </div>
    </article>
  )
}

/** The notice's own group: what it is known as, and a way to move it to another name. */
function NoticeName({
  notice,
  knownNames,
  onSave,
  busy,
}: {
  notice: CreditNotice
  knownNames: string[]
  onSave: (groupName: string) => void
  busy: boolean
}) {
  const { t } = useLingui()
  const [opened, setOpened] = useState(false)
  const [draft, setDraft] = useState('')
  if (notice.kind === 'site') return null

  const groupName = notice.groupName
  const suggested = notice.suggestedName
  const open = () => {
    setDraft(groupName ?? suggested ?? '')
    setOpened(true)
  }
  const save = (value: string) => {
    onSave(value)
    setOpened(false)
  }
  const picks = knownNames.filter((n) => n !== groupName).slice(0, 6)

  return (
    <Group gap="xs" wrap="wrap" className="credit-notice-name">
      <Text size="sm">
        {groupName ? (
          notice.groupSource === 'confirmed' ? (
            <Trans>Group: {groupName}, set by you</Trans>
          ) : (
            <Trans>Group: {groupName}, from chapters</Trans>
          )
        ) : suggested ? (
          <Trans>Suggested name: {suggested}</Trans>
        ) : (
          <span className="credit-muted">
            <Trans>No group name</Trans>
          </span>
        )}
      </Text>
      <Popover opened={opened} onChange={setOpened} position="bottom-start" withinPortal trapFocus>
        <Popover.Target>
          <Button size="compact-xs" variant="subtle" leftSection={<IconPencil size={14} />} onClick={open}>
            {groupName || suggested ? <Trans>Change group</Trans> : <Trans>Name group</Trans>}
          </Button>
        </Popover.Target>
        <Popover.Dropdown className="credit-name-popover">
          <form
            onSubmit={(e) => {
              e.preventDefault()
              if (draft.trim()) save(draft.trim())
            }}
          >
            <Stack gap="xs">
              <TextInput
                label={t`Group name for this notice`}
                value={draft}
                maxLength={100}
                data-autofocus
                onChange={(e) => setDraft(e.currentTarget.value)}
              />
              {picks.length > 0 && (
                <Group gap={4} wrap="wrap" aria-label={t`Other groups`} role="group">
                  {picks.map((name) => (
                    <Button key={name} size="compact-xs" variant="default" onClick={() => setDraft(name)}>
                      {name}
                    </Button>
                  ))}
                </Group>
              )}
              <Group gap="xs" justify="space-between">
                {groupName ? (
                  <Button size="xs" variant="subtle" color="var(--ink-3)" onClick={() => save('')}>
                    <Trans>Remove name</Trans>
                  </Button>
                ) : (
                  <span />
                )}
                <Button size="xs" type="submit" disabled={!draft.trim()} loading={busy}>
                  <Trans>Save</Trans>
                </Button>
              </Group>
            </Stack>
          </form>
        </Popover.Dropdown>
      </Popover>
    </Group>
  )
}

function SeriesChapters({ series }: { series: CreditGroupSeries }) {
  const [expanded, setExpanded] = useState(false)
  const total = series.chapters.length
  const missing = series.chapters.filter((c) => !c.group).length
  const shown = expanded ? series.chapters : series.chapters.slice(0, CHAPTERS_SHOWN)
  const hiddenCount = total - shown.length

  return (
    <div className="credit-series">
      <div className="credit-series-head">
        <Text size="sm" fw={600} className="credit-series-title">
          {series.title}
        </Text>
        <Text size="xs" c="var(--ink-3)">
          <MetaList
            inline
            items={[
              plural(total, { one: '# chapter', other: '# chapters' }),
              missing > 0 && (
                <span key="missing" className="credit-group-missing">
                  {plural(missing, { one: '# without a group', other: '# without a group' })}
                </span>
              ),
            ]}
          />
        </Text>
      </div>
      <div className="modal-list">
        {shown.map((chapter) => (
          <div key={chapter.chapterId} className="modal-list-row credit-chapter-row">
            <Text size="sm" className="credit-chapter-label">
              {chapter.label}
              {chapter.title && (
                <Text component="span" size="sm" c="var(--ink-3)">
                  {' '}
                  {chapter.title}
                </Text>
              )}
            </Text>
            {chapter.group ? (
              <Badge size="sm" variant="light" color="var(--neutral)" className="credit-pill credit-chapter-group">
                {chapter.group}
              </Badge>
            ) : (
              <Text size="xs" className="credit-group-missing">
                <Trans>No group</Trans>
              </Text>
            )}
          </div>
        ))}
      </div>
      {hiddenCount > 0 && (
        <Button size="compact-xs" variant="subtle" mt={4} onClick={() => setExpanded(true)}>
          <Plural value={total} one="Show all # chapter" other="Show all # chapters" />
        </Button>
      )}
    </div>
  )
}

/** Asks before giving the group to chapters, since that renames files, then shows what it did. */
function ApplyDialog({ group, title, onClose }: { group: CreditGroup | null; title: string; onClose: () => void }) {
  const { t } = useLingui()
  const apply = useApplyCreditGroups()
  const [result, setResult] = useState<ApplyCreditGroupsResult | null>(null)
  const count = group?.chaptersWithoutGroup ?? 0
  const conflicts = result?.conflicts ?? 0

  const close = () => {
    setResult(null)
    apply.reset()
    onClose()
  }

  return (
    <Modal
      opened={group != null}
      onClose={close}
      centered
      size={MODAL_SIZE.narrow}
      title={
        <ModalHeading
          title={result ? t`Given to chapters` : t`Give ${title} to chapters`}
          description={
            result ? undefined : (
              <Plural
                value={count}
                one={`# chapter carries these notices and has no group yet. It gets ${title}.`}
                other={`# chapters carry these notices and have no group yet. They get ${title}.`}
              />
            )
          }
        />
      }
    >
      {result ? (
        <Stack gap="sm">
          <dl className="credit-apply-result">
            <div>
              <dt>
                <Trans>Chapters given the group</Trans>
              </dt>
              <dd className="tnum">{formatNumber(result.chaptersUpdated)}</dd>
            </div>
            <div>
              <dt>
                <Trans>Files renamed</Trans>
              </dt>
              <dd className="tnum">{formatNumber(result.filesRenamed)}</dd>
            </div>
            <div>
              <dt>
                <Trans>Left alone</Trans>
              </dt>
              <dd className="tnum">{formatNumber(result.conflicts)}</dd>
            </div>
          </dl>
          {conflicts > 0 && (
            <ModalNote tone="info">
              <Plural
                value={conflicts}
                one="# file carries notices that name different groups, so it was left without one."
                other="# files carry notices that name different groups, so they were left without one."
              />
            </ModalNote>
          )}
          {result.warnings.map((warning) => (
            <ModalNote key={warning} tone="warn">
              {warning}
            </ModalNote>
          ))}
          <ModalFooter>
            <Button onClick={close}>
              <Trans>Done</Trans>
            </Button>
          </ModalFooter>
        </Stack>
      ) : (
        <Stack gap="sm">
          <Text size="sm">
            <Trans>Where your file naming format uses the group, Maki renames these files on disk.</Trans>
          </Text>
          <ModalFooter>
            <Button variant="default" onClick={close}>
              <Trans>Cancel</Trans>
            </Button>
            <Button
              loading={apply.isPending}
              onClick={() =>
                group &&
                apply.mutate(group.noticeIds, {
                  onSuccess: setResult,
                  onError: (error) => notifications.show({ message: error.message, color: 'var(--danger)' }),
                })
              }
            >
              <Plural value={count} one="Give to # chapter" other="Give to # chapters" />
            </Button>
          </ModalFooter>
        </Stack>
      )}
    </Modal>
  )
}

function GroupsSkeleton() {
  return (
    <div className="credit-groups-split" aria-hidden>
      <div className="credit-groups-list">
        <div className="modal-list">
          {[64, 48, 56, 40].map((width, i) => (
            <div key={i} className="modal-list-row">
              <Stack gap={6} style={{ flex: 1 }}>
                <Skeleton h={12} w={`${width}%`} />
                <Skeleton h={10} w={`${width - 12}%`} />
              </Stack>
            </div>
          ))}
        </div>
      </div>
      <div className="credit-groups-detail">
        <Stack gap="sm">
          <Skeleton h={20} w="40%" />
          <Skeleton h={140} radius="md" />
        </Stack>
      </div>
    </div>
  )
}

