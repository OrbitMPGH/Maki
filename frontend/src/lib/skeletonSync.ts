const SKELETON = '.mantine-Skeleton-root, .reader-page-skeleton'

/**
 * A CSS animation's clock starts when its element mounts, so skeletons added later (a grid that
 * gains columns on resize, a list that grows) pulse out of step with the ones already there.
 * Pinning every skeleton pulse to the document timeline's origin puts them all on one phase.
 */
export function syncSkeletonPulses() {
  if (typeof CSSAnimation === 'undefined' || typeof Element.prototype.getAnimations !== 'function') return
  document.addEventListener('animationstart', (e) => {
    const el = e.target
    if (!(el instanceof Element) || !el.matches(SKELETON)) return
    for (const animation of el.getAnimations({ subtree: true })) {
      if (animation instanceof CSSAnimation && animation.animationName === e.animationName) animation.startTime = 0
    }
  })
}
