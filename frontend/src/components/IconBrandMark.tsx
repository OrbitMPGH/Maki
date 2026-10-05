export type BrandMood = 'awake' | 'asleep' | 'asking' | 'pleased'

const INK = '#1a1a1a'

/**
 * The mascot. Only the eyes and mouth change with `mood`; the body, bands and cheeks keep the fixed
 * mark colours in every theme and accent.
 *
 * Lives in its own module because the login and setup screens render it too, and those are outside
 * the AppShell: importing it from App.tsx would make the pre-authentication bundle pull in the whole
 * shell it exists to avoid.
 */
export function IconBrandMark({ mood = 'awake', size = 30 }: { mood?: BrandMood; size?: number }) {
  const line = { fill: 'none', stroke: INK, strokeLinecap: 'round' } as const
  return (
    <svg width={size} height={size} viewBox="0 0 96 96" fill="none" aria-hidden>
      <g stroke={INK} strokeWidth="3.5" strokeLinejoin="round">
        <rect x="22" y="20" width="52" height="56" rx="15" fill="#f4ecd8" />
        <path d="M22 35 a15 15 0 0 1 15 -15 h22 a15 15 0 0 1 15 15 v2 h-52 z" fill="#20301f" />
        <path d="M22 61 h52 a15 15 0 0 1 -15 15 h-22 a15 15 0 0 1 -15 -15 z" fill="#20301f" />
      </g>
      <circle cx="31" cy="52" r="2.8" fill="#f7a8bf" />
      <circle cx="65" cy="52" r="2.8" fill="#f7a8bf" />
      {(mood === 'awake' || mood === 'asking') && (
        <>
          <g fill={INK}>
            <circle cx="38" cy="46" r="4" />
            <circle cx="58" cy="46" r="4" />
          </g>
          <circle cx="39.3" cy="44.6" r="1.3" fill="#fff" />
          <circle cx="59.3" cy="44.6" r="1.3" fill="#fff" />
        </>
      )}
      {mood === 'awake' && <path d="M43 53 q5 4 10 0" strokeWidth="2.4" {...line} />}
      {mood === 'asking' && (
        <>
          <path d="M53 37 q5 -4 10 -1" strokeWidth="2.4" {...line} />
          <circle cx="48" cy="55" r="2.4" strokeWidth="2.2" {...line} />
        </>
      )}
      {mood === 'asleep' && (
        <>
          <path d="M34 47 q4 3 8 0 M54 47 q4 3 8 0" strokeWidth="2.6" {...line} />
          <path d="M45 54 q3 2 6 0" strokeWidth="2.2" {...line} />
          <path
            d="M72 20 h8 l-8 9 h8 M83 8 h6 l-6 7 h6"
            fill="none"
            strokeWidth="2.2"
            strokeLinecap="round"
            strokeLinejoin="round"
            style={{ stroke: 'var(--ink-3)' }}
          />
        </>
      )}
      {mood === 'pleased' && (
        <>
          <path d="M33 48 q5 -6 10 0 M53 48 q5 -6 10 0" strokeWidth="2.8" {...line} />
          <path d="M41 52 h14 q-1 7 -7 7 q-6 0 -7 -7 z" fill={INK} stroke={INK} strokeWidth="1.6" strokeLinejoin="round" />
        </>
      )}
    </svg>
  )
}
