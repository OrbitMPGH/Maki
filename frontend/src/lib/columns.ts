/**
 * Splits groups kept in order into two columns, where the taller column comes out shortest.
 * `weights` is each group's height in rows; the result is the index the second column starts at.
 */
export function balancedSplit(weights: number[]): number {
  const total = weights.reduce((sum, w) => sum + w, 0)
  let best = weights.length
  let bestTallest = total
  let left = 0
  for (let i = 0; i <= weights.length; i++) {
    const tallest = Math.max(left, total - left)
    if (tallest < bestTallest) {
      bestTallest = tallest
      best = i
    }
    if (i < weights.length) left += weights[i]
  }
  return best
}

/** The two columns `balancedSplit` picks, leaving out an empty one. */
export function twoColumns<T>(groups: T[], weight: (group: T) => number): T[][] {
  const split = balancedSplit(groups.map(weight))
  return [groups.slice(0, split), groups.slice(split)].filter((column) => column.length > 0)
}
