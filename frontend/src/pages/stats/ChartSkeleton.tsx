import { Skeleton } from '@mantine/core'

const HEIGHTS = [34, 52, 41, 66, 48, 30, 58, 72, 45, 38, 61, 80, 54, 36, 47, 69, 57, 42, 63, 50, 33, 59, 74, 46]

/** Bars at rest in place of a chart that has not loaded, lighter on the page than one grey slab. */
export function ChartSkeleton({ h }: { h: number }) {
  return (
    <div className="chart-skeleton" style={{ height: h }} aria-hidden>
      {HEIGHTS.map((pct, i) => (
        <Skeleton key={i} h={`${pct}%`} radius="sm" />
      ))}
    </div>
  )
}
