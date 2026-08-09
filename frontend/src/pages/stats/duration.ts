/**
 * Reading time as a person would say it: "4h 20m", "35m", "48s". Minutes are dropped once the
 * figure is whole hours, because "12h 0m" reads like a stopwatch rather than an answer.
 */
export function formatReadingTime(seconds: number): string {
  if (seconds < 60) return `${Math.max(0, Math.round(seconds))}s`

  const hours = Math.floor(seconds / 3600)
  const minutes = Math.round((seconds % 3600) / 60)
  if (hours === 0) return `${minutes}m`
  // 59m30s rounds to 60 minutes; carry it rather than printing "3h 60m".
  if (minutes === 60) return `${hours + 1}h`
  return minutes === 0 ? `${hours}h` : `${hours}h ${minutes}m`
}
