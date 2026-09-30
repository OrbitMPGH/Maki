import { useState, useSyncExternalStore, type ReactNode } from 'react'
import { Menu, Text, Tooltip } from '@mantine/core'
import {
  IconArrowUpRight,
  IconBook,
  IconBug,
  IconChevronUp,
  IconHelpCircle,
  IconMessages,
  IconSparkles,
  IconStarFilled,
  IconX,
} from '@tabler/icons-react'
import { Trans, useLingui } from '@lingui/react/macro'
import { useAppVersion, useUpdateStatus } from '../../api/hooks'
import { getSkippedVersion, setSkippedVersion, subscribeSkippedVersion } from '../../lib/updateSkip'
import { UserMenu } from '../UserMenu'

const REPO_URL = 'https://github.com/OrbitMPGH/Maki'
const STAR_DISMISSED_KEY = 'star-nudge-dismissed'

function readStarDismissed(): boolean {
  try {
    return localStorage.getItem(STAR_DISMISSED_KEY) === '1'
  } catch {
    return false
  }
}

export default function SidebarFooter({ onNavigate }: { onNavigate?: () => void }) {
  const { t } = useLingui()
  const { data: version } = useAppVersion()
  const { data: update } = useUpdateStatus()
  const skipped = useSyncExternalStore(subscribeSkippedVersion, getSkippedVersion)
  const [starDismissed, setStarDismissed] = useState(readStarDismissed)

  const latestVersion = update?.latestVersion ?? null
  const updateAvailable = !!update?.updateAvailable && !!latestVersion
  const isSkipped = updateAvailable && skipped === latestVersion
  const showUpdate = updateAvailable && !isSkipped
  const showStar = !showUpdate && !starDismissed

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
      <UserMenu onNavigate={onNavigate} />

      {showUpdate && latestVersion ? (
        <div className="nav-footer-notice">
          <a
            href={update?.releaseUrl ?? `${REPO_URL}/releases`}
            target="_blank"
            rel="noreferrer"
            className="nav-footer-link"
          >
            <Trans>Update {latestVersion} available</Trans>
            <IconArrowUpRight size={14} stroke={1.8} />
          </a>
          <button
            type="button"
            className="nav-footer-dismiss"
            aria-label={t`Skip ${latestVersion}`}
            title={t`Skip ${latestVersion}`}
            onClick={() => setSkippedVersion(latestVersion)}
          >
            <IconX size={12} stroke={2} />
          </button>
        </div>
      ) : showStar ? (
        <div className="nav-footer-notice">
          <a
            href={REPO_URL}
            target="_blank"
            rel="noreferrer"
            className="nav-footer-link"
            onClick={dismissStar}
          >
            <IconStarFilled size={13} className="nav-footer-star" />
            <Trans>Star on GitHub</Trans>
            <IconArrowUpRight size={14} stroke={1.8} />
          </a>
          <button
            type="button"
            className="nav-footer-dismiss"
            aria-label={t`Dismiss`}
            title={t`Dismiss`}
            onClick={dismissStar}
          >
            <IconX size={12} stroke={2} />
          </button>
        </div>
      ) : null}

      <HelpMenu version={tagged ? version : null} />

      {version && (
        <div className="nav-footer-row">
          <VersionLabel
            version={version}
            unofficial={unofficial}
            latestVersion={updateAvailable ? latestVersion : null}
            isSkipped={isSkipped}
          />
        </div>
      )}
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
          <IconHelpCircle size={18} stroke={1.7} className="nav-icon" />
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
