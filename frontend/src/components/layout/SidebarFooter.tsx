import { useState, useSyncExternalStore, type ReactNode } from 'react'
import { Box, Button, Menu, Text, Tooltip } from '@mantine/core'
import {
  IconArrowUpRight,
  IconBook,
  IconBug,
  IconChevronUp,
  IconHelpCircle,
  IconMessages,
  IconRocket,
  IconSparkles,
  IconStarFilled,
  IconX,
} from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { useAppVersion, useUpdateStatus } from '../../api/hooks'
import { getSkippedVersion, setSkippedVersion, subscribeSkippedVersion } from '../../lib/updateSkip'
import { openCommandPalette } from '../../lib/commandPalette'

const REPO_URL = 'https://github.com/OrbitMPGH/Maki'
const STAR_DISMISSED_KEY = 'star-nudge-dismissed'

function readStarDismissed(): boolean {
  try {
    return localStorage.getItem(STAR_DISMISSED_KEY) === '1'
  } catch {
    return false
  }
}

function isMac(): boolean {
  if (typeof navigator === 'undefined') return false
  return /Mac|iPhone|iPad|iPod/i.test(navigator.platform || navigator.userAgent)
}

export default function SidebarFooter() {
  const { t } = useLingui()
  const { data: version } = useAppVersion()
  const { data: update } = useUpdateStatus()
  const skipped = useSyncExternalStore(subscribeSkippedVersion, getSkippedVersion)
  const [starDismissed, setStarDismissed] = useState(readStarDismissed)

  const latestVersion = update?.latestVersion ?? null
  const updateAvailable = !!update?.updateAvailable && !!latestVersion
  const isSkipped = updateAvailable && skipped === latestVersion
  const showUpdateCard = updateAvailable && !isSkipped
  const showStarCard = !showUpdateCard && !starDismissed

  const dismissStar = () => {
    setStarDismissed(true)
    try {
      localStorage.setItem(STAR_DISMISSED_KEY, '1')
    } catch {
      /* private mode: dismissed for this visit only */
    }
  }

  const unofficial = version ? /-(dev|nightly)/.test(version) : false
  const tagged = !!version && !unofficial

  return (
    <div className="nav-footer">
      <Box visibleFrom="sm">
        {showUpdateCard && update && latestVersion ? (
          <UpdateCard
            latestVersion={latestVersion}
            currentVersion={update.currentVersion}
            releaseUrl={update.releaseUrl}
          />
        ) : showStarCard ? (
          <div className="nav-card-wrap">
            <a
              href={REPO_URL}
              target="_blank"
              rel="noreferrer"
              className="nav-card nav-card-star"
              onClick={dismissStar}
            >
              <IconStarFilled size={16} className="nav-card-icon" />
              <div className="nav-card-body">
                <div className="nav-card-title">
                  <Trans>Enjoying Maki?</Trans>
                </div>
                <div className="nav-card-sub">
                  <Trans>A star on GitHub helps others find it.</Trans>
                </div>
              </div>
            </a>
            <button
              type="button"
              className="nav-card-dismiss"
              aria-label={t`Dismiss`}
              onClick={(e) => {
                e.preventDefault()
                e.stopPropagation()
                dismissStar()
              }}
            >
              <IconX size={12} stroke={2} />
            </button>
          </div>
        ) : null}
      </Box>

      <HelpMenu version={tagged ? version : null} />

      <div className="nav-footer-row">
        {version ? (
          <VersionLabel
            version={version}
            unofficial={unofficial}
            latestVersion={updateAvailable ? latestVersion : null}
            isSkipped={isSkipped}
          />
        ) : (
          <span />
        )}
        <Tooltip label={t`Command palette`} withArrow>
          <button
            type="button"
            className="nav-footer-kbd"
            onClick={openCommandPalette}
            aria-label={t`Command palette`}
          >
            {isMac() ? '⌘ K' : 'Ctrl K'}
          </button>
        </Tooltip>
      </div>
    </div>
  )
}

function UpdateCard({
  latestVersion,
  currentVersion,
  releaseUrl,
}: {
  latestVersion: string
  currentVersion: string
  releaseUrl: string | null | undefined
}) {
  return (
    <div className="nav-card nav-card-update">
      <IconRocket size={16} stroke={1.8} className="nav-card-icon" />
      <div className="nav-card-body">
        <div className="nav-card-title">
          <Trans>Maki {latestVersion} is out</Trans>
        </div>
        <div className="nav-card-sub">
          <Trans>You're on {currentVersion}</Trans>
        </div>
        <div className="nav-card-actions">
          {releaseUrl && (
            <Button
              component="a"
              href={releaseUrl}
              target="_blank"
              rel="noreferrer"
              size="compact-xs"
              color="brand"
              variant="filled"
              rightSection={<IconArrowUpRight size={12} stroke={2} />}
            >
              <Trans>View release</Trans>
            </Button>
          )}
          <button
            type="button"
            className="nav-card-link"
            onClick={() => setSkippedVersion(latestVersion)}
          >
            <Trans>Skip {latestVersion}</Trans>
          </button>
        </div>
      </div>
    </div>
  )
}

function HelpMenu({ version }: { version: string | null }) {
  const menuItem = (
    href: string,
    icon: ReactNode,
    label: ReactNode,
    description?: ReactNode,
  ) => (
    <Menu.Item component="a" href={href} target="_blank" rel="noreferrer" leftSection={icon}>
      <Text fz="sm" lh={1.3}>
        {label}
      </Text>
      {description && (
        <Text fz="xs" c="dimmed" lh={1.3}>
          {description}
        </Text>
      )}
    </Menu.Item>
  )

  return (
    <Menu position="top-start" withArrow offset={6} width={228}>
      <Menu.Target>
        <button type="button" className="nav-link nav-footer-help">
          <IconHelpCircle size={16} stroke={1.7} className="nav-icon" />
          <Trans>Help & feedback</Trans>
          <IconChevronUp size={12} stroke={1.8} className="nav-footer-chevron" />
        </button>
      </Menu.Target>
      <Menu.Dropdown>
        {menuItem(
          `${REPO_URL}/issues/new/choose`,
          <IconBug size={16} stroke={1.7} />,
          <Trans>Report a bug</Trans>,
          <Trans>Opens a GitHub issue with the template.</Trans>,
        )}
        {menuItem(
          `${REPO_URL}/discussions`,
          <IconMessages size={16} stroke={1.7} />,
          <Trans>Ask or suggest</Trans>,
          <Trans>GitHub Discussions.</Trans>,
        )}
        {menuItem(`${REPO_URL}#readme`, <IconBook size={16} stroke={1.7} />, <Trans>Documentation</Trans>)}
        <Menu.Divider />
        {menuItem(
          REPO_URL,
          <IconStarFilled size={16} style={{ color: 'var(--rating)' }} />,
          <Trans>Star on GitHub</Trans>,
        )}
        {version
          ? menuItem(
              `${REPO_URL}/releases/tag/v${version}`,
              <IconSparkles size={16} stroke={1.7} />,
              <Trans>What's new in v{version}</Trans>,
            )
          : menuItem(
              `${REPO_URL}/releases`,
              <IconSparkles size={16} stroke={1.7} />,
              <Trans>Release notes</Trans>,
            )}
      </Menu.Dropdown>
    </Menu>
  )
}

function VersionLabel({
  version,
  unofficial,
  latestVersion,
  isSkipped,
}: {
  version: string
  unofficial: boolean
  latestVersion: string | null
  isSkipped: boolean
}) {
  const { t } = useLingui()
  const tooltip = unofficial
    ? t`Unofficial build (not a tagged release)`
    : latestVersion && isSkipped
      ? t`${latestVersion} available, skipped`
      : latestVersion
        ? t`Maki ${latestVersion} available`
        : t`Maki ${version}`

  return (
    <Tooltip label={tooltip} withArrow>
      {latestVersion ? (
        <button
          type="button"
          className="nav-footer-version nav-footer-version-button"
          onClick={() => setSkippedVersion(null)}
        >
          <span className="nav-footer-dot" />v{version}
        </button>
      ) : (
        <span className="nav-footer-version">v{version}</span>
      )}
    </Tooltip>
  )
}
