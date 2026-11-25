import { Skeleton } from '@mantine/core'

/**
 * A poster rail's shape while its section's query is loading, shared by Home and Discover so a
 * gated section never renders nothing and pops in once data lands. `title` holds the place of the
 * `SectionHeader` Discover always shows above its rails, with its spacing; Home's rails render their
 * real `SectionHeader` only once there is data, so they leave it off. `engine` matches the wider card
 * the engine rail (`EngineRailRow`) uses, with the footer that holds its "why" line. `flush` drops
 * the gap above it, for a rail placed under a heading of its own (the Next up tray in
 * `ContinueHeroSkeleton`).
 */
export function RailSkeleton({
  count = 12,
  engine = false,
  title = false,
  titleWidth = 190,
  flush = false,
}: {
  count?: number
  engine?: boolean
  title?: boolean
  titleWidth?: number
  flush?: boolean
}) {
  return (
    <div aria-hidden>
      {title && (
        <div className="section-header" style={flush ? { marginTop: 0 } : undefined}>
          <Skeleton h={18} w={titleWidth} />
        </div>
      )}
      <div
        className="discover-rail"
        data-engine={engine || undefined}
        style={title || flush ? undefined : { marginTop: 'var(--mantine-spacing-xl)' }}
      >
        {Array.from({ length: count }, (_, i) => (
          <div key={i} className="discover-rail-item">
            {engine ? (
              <div className="rail-skeleton-engine">
                <Skeleton radius="var(--radius-cover)" style={{ aspectRatio: '2 / 3' }} />
                <div className="rail-skeleton-why">
                  <Skeleton h={10} w="58%" />
                  <Skeleton h={10} w="76%" />
                </div>
              </div>
            ) : (
              <Skeleton radius="var(--radius-cover)" style={{ aspectRatio: '2 / 3' }} />
            )}
          </div>
        ))}
      </div>
    </div>
  )
}
