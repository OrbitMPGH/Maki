/** `smooth` unless the user asked for reduced motion, which the CSS rule cannot enforce for scripted scrolling. */
export function scrollBehavior(): ScrollBehavior {
  return typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches
    ? 'auto'
    : 'smooth'
}
