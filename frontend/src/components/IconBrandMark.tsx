/**
 * Small book glyph used inside the gradient brand tile.
 *
 * Lives in its own module because the login and setup screens render it too, and those are outside
 * the AppShell — importing it from App.tsx would make the pre-authentication bundle pull in the whole
 * shell it exists to avoid.
 */
export function IconBrandMark() {
  return (
    <svg width="30" height="30" viewBox="0 0 96 96" fill="none" aria-hidden>
      <g stroke="#1a1a1a" strokeWidth="3.5" strokeLinejoin="round">
        <rect x="22" y="20" width="52" height="56" rx="15" fill="#f4ecd8" />
        <path d="M22 35 a15 15 0 0 1 15 -15 h22 a15 15 0 0 1 15 15 v2 h-52 z" fill="#20301f" />
        <path d="M22 61 h52 a15 15 0 0 1 -15 15 h-22 a15 15 0 0 1 -15 -15 z" fill="#20301f" />
      </g>
      <g fill="#1a1a1a">
        <circle cx="38" cy="46" r="4" />
        <circle cx="58" cy="46" r="4" />
      </g>
      <circle cx="39.3" cy="44.6" r="1.3" fill="#fff" />
      <circle cx="59.3" cy="44.6" r="1.3" fill="#fff" />
      <circle cx="31" cy="52" r="2.8" fill="#f7a8bf" />
      <circle cx="65" cy="52" r="2.8" fill="#f7a8bf" />
      <path d="M43 53 q5 4 10 0" fill="none" stroke="#1a1a1a" strokeWidth="2.4" strokeLinecap="round" />
    </svg>
  )
}
