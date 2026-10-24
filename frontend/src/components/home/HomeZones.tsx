import { useId } from 'react'
import { Link } from 'react-router-dom'
import { Button, Title } from '@mantine/core'
import { Trans } from '@lingui/react/macro'
import { IconArrowRight, IconCompass } from '@tabler/icons-react'

/**
 * How Home tells what you can read now from what you would have to add first. Draft: compared in
 * Storybook, not wired to a setting yet.
 *
 * `break` and `well` gather every catalogue section after the library ones under one heading;
 * `tagged` keeps the user's order and marks each catalogue section's header instead.
 */
export type HomeSeparation = 'none' | 'break' | 'well' | 'tagged'

export function DiscoverZone({
  variant,
  discoverLink,
  children,
}: {
  variant: 'break' | 'well'
  discoverLink: boolean
  children: React.ReactNode
}) {
  const titleId = useId()
  return (
    <section className="home-zone" data-variant={variant} aria-labelledby={titleId}>
      <div className="home-zone-head">
        <span className="home-zone-icon" aria-hidden>
          <IconCompass size={22} stroke={1.9} />
        </span>
        <div className="home-zone-text">
          <Title order={2} id={titleId} className="home-zone-title">
            <Trans>Not in your library yet</Trans>
          </Title>
          <p className="home-zone-sub">
            <Trans>Picks from the catalogue. Open one to read more about it or add it.</Trans>
          </p>
        </div>
        {discoverLink && (
          <Button
            component={Link}
            to="/discover"
            variant="light"
            size="compact-sm"
            rightSection={<IconArrowRight size={14} />}
            className="home-zone-action"
          >
            <Trans>Open Discover</Trans>
          </Button>
        )}
      </div>
      {children}
    </section>
  )
}
