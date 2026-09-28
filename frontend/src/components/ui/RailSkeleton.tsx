import { Skeleton } from '@mantine/core'

/**
 * A poster rail's shape while its section's query is loading, shared by Home and Discover so a
 * gated section never renders nothing and pops in once data lands. `title` reproduces the heading
 * skeleton Discover always shows above its rails; Home's rails render their real `SectionHeader`
 * only once there is data, so they leave it off. `engine` matches the wider card the engine rail
 * (`EngineRailRow`) uses for its "why" line.
 */
export function RailSkeleton({
  count = 12,
  engine = false,
  title = false,
  titleWidth = 190,
}: {
  count?: number
  engine?: boolean
  title?: boolean
  titleWidth?: number
}) {
  return (
    <div aria-hidden>
      {title && <Skeleton h={18} w={titleWidth} mt="xl" mb="sm" />}
      <div
        className="discover-rail"
        data-engine={engine || undefined}
        style={title ? undefined : { marginTop: 'var(--mantine-spacing-xl)' }}
      >
        {Array.from({ length: count }, (_, i) => (
          <div key={i} className="discover-rail-item">
            <Skeleton radius="lg" style={{ aspectRatio: '2 / 3' }} />
          </div>
        ))}
      </div>
    </div>
  )
}
