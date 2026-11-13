import { Button, Progress, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { Link } from 'react-router-dom'
import { useLogout } from '../api/auth'
import { useAuth } from '../auth/AuthProvider'
import { useDumpProgress, useHomeReading, useMetadataSettings, type DumpProgress } from '../api/hooks'
import { AuthFrame } from '../components/auth/AuthFrame'
import { FigureStrip } from '../components/ui/FigureStrip'
import { ModalNote } from '../components/ui/ModalShell'
import { StatusDot } from '../components/ui/StatusDot'
import { IconClock } from '../lib/icons'
import { formatBytes, formatPercent, formatReadingTime } from '../format'
import { useLabel } from '../i18n-context'
import { Thumb } from '../components/ui/Thumb'
import MetadataDumpProgress, { MetadataDumpStatus, dumpPercent, dumpPhaseLabel } from '../components/MetadataDumpProgress'

/**
 * Stands in front of the SPA while the local MangaBaka database is missing: search, adding series,
 * imports and Discover all read from it, and there is no API fallback. The reader is not behind it,
 * and neither are reading apps on OPDS, Komga or KOReader, since serving files needs no metadata.
 *
 * An admin sees the live download, can retry it, and can still open Settings and Health to find
 * out why it fails. Everyone else gets a waiting screen, or word that the download failed and is
 * retried on its own; the metadata query polls while the dump is missing, so the app opens by
 * itself once it lands.
 */
export default function MetadataGatePage() {
  const { t } = useLingui()
  const { can } = useAuth()
  const isAdmin = can('Admin')
  const { data: metadata } = useMetadataSettings()
  const { data: progress } = useDumpProgress(isAdmin)
  const logout = useLogout()

  return (
    <AuthFrame
      title={t`Getting the metadata database ready`}
      subtitle={<Trans>About a 350 MB download. Maki opens as soon as it is installed.</Trans>}
      mood="asleep"
      cardFooter={
        <div className="auth-card-actions">
          {isAdmin && (
            <>
              <Button component={Link} to="/settings?tab=library&s=metadata" variant="default" size="xs">
                <Trans>Open settings</Trans>
              </Button>
              <Button component={Link} to="/health" variant="default" size="xs">
                <Trans>Open health</Trans>
              </Button>
            </>
          )}
          <Button
            className="auth-card-actions-end"
            variant="subtle"
            color="var(--neutral)"
            size="xs"
            loading={logout.isPending}
            onClick={() => logout.mutate()}
          >
            <Trans>Sign out</Trans>
          </Button>
        </div>
      }
    >
      <div style={{ display: 'grid', gap: 16 }}>
        {isAdmin ? (
          <>
            <MetadataDumpProgress quiet />
            <GateProgress metadata={metadata} progress={progress} />
          </>
        ) : metadata?.dumpDownloadFailed ? (
          <ModalNote tone="warn">
            <Trans>
              The download failed. Maki tries again by itself, and an admin can check what went wrong.
              This page opens Maki by itself when it is done.
            </Trans>
          </ModalNote>
        ) : (
          <Text size="sm" className="auth-retry-line">
            <StatusDot tone="info" live />
            <Trans>The server is downloading it now. This page opens Maki by itself when it is done.</Trans>
          </Text>
        )}
        <GateWaitingList />
        <GateReadingList />
      </div>
    </AuthFrame>
  )
}

/** The bar and its numbers while a download is running; the shared status (ready, failed, retry) otherwise. */
function GateProgress({
  metadata,
  progress,
}: {
  metadata: Parameters<typeof MetadataDumpStatus>[0]['metadata']
  progress: DumpProgress | undefined
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  if (metadata?.dumpPresent || !progress?.running) {
    return <MetadataDumpStatus metadata={metadata} progress={progress} />
  }
  const percent = dumpPercent(progress)
  const phaseLabel = renderLabel(dumpPhaseLabel(progress.phase))
  const downloading = progress.phase === 'downloading'
  const total = progress.totalBytes ? formatBytes(progress.totalBytes) : null
  return (
    <div>
      <div className="auth-wait-row" style={{ padding: 0, minHeight: 0, marginBottom: 6 }}>
        <Text size="sm" fw={600} c="var(--ink-hi)">
          {phaseLabel}
        </Text>
        {percent !== null && (
          <Text size="sm" c="var(--ink-2)" className="tnum">
            {formatPercent(percent / 100)}
          </Text>
        )}
      </div>
      <Progress
        value={percent ?? 100}
        animated={percent === null}
        size="md"
        aria-label={phaseLabel}
      />
      {downloading && (
        <div style={{ marginTop: 12 }}>
          <FigureStrip
            className="auth-gate-figures"
            figures={[
              {
                label: t`Downloaded`,
                value: formatBytes(progress.downloadedBytes),
                hint: total ? t`of ${total}` : undefined,
                zero: false,
              },
              ...(progress.bytesPerSecond
                ? [{ label: t`Speed`, value: `${formatBytes(progress.bytesPerSecond)}/s`, zero: false }]
                : []),
              ...(progress.estimatedSecondsRemaining !== null
                ? [{ label: t`Left`, value: formatReadingTime(progress.estimatedSecondsRemaining), zero: false }]
                : []),
            ]}
          />
        </div>
      )}
    </div>
  )
}

function GateWaitingList() {
  return (
    <div>
      <div className="auth-label">
        <Trans>What is waiting for it</Trans>
      </div>
      <div className="auth-wait-list">
        <div className="auth-wait-row">
          <span>
            <Trans>Search, adding series, imports and Discover</Trans>
          </span>
          <span className="auth-wait-state">
            <IconClock size={14} aria-hidden />
            <Trans>Waiting</Trans>
          </span>
        </div>
        <div className="auth-wait-row">
          <span>
            <Trans>Reader, OPDS, Komga and KOReader</Trans>
          </span>
          <StatusDot tone="ok">
            <Trans>Working</Trans>
          </StatusDot>
        </div>
      </div>
    </div>
  )
}

const GATE_READING_LIMIT = 6

/**
 * Chapters to pick up while the database downloads, since the reader is the one page everyone can
 * still open and nothing else leads to it. Home's reading list needs no metadata, only progress
 * rows and files on disk. Renders nothing when there is nothing to resume.
 */
function GateReadingList() {
  const { t } = useLingui()
  const { data } = useHomeReading(GATE_READING_LIMIT)
  const items = data?.continueReading.slice(0, GATE_READING_LIMIT) ?? []
  if (items.length === 0) return null

  return (
    <section aria-label={t`Continue reading`}>
      <div className="auth-label">
        <Trans>Continue reading</Trans>
      </div>
      {items.map(({ chapterId, coverUrl, seriesTitle, chapterLabel }) => (
        <Link key={chapterId} to={`/read/${chapterId}`} className="next-up-row">
          <Thumb src={coverUrl} size="sm" />
          <span className="next-up-copy">
            <span className="next-up-name series-title">{seriesTitle}</span>
            <span className="next-up-meta tnum">{chapterLabel}</span>
          </span>
        </Link>
      ))}
    </section>
  )
}
