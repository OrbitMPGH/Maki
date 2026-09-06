/**
 * The four layers behind a hero band: the series' own poster filled to the band and lightly
 * blurred, a corner falloff, and two scrims over it rather than one flat wash. The recipe lives in
 * theme.css under `.series-hero-art` and friends; the reason a flat wash reads as a smudge is in
 * the comments there.
 *
 * Extracted so the series page and the Discover detail modal render the same band from one set of
 * elements. Two copies of a four-layer stack drift the first time one of the gradients is tuned.
 */
export function HeroBackdrop({ coverUrl }: { coverUrl: string | null | undefined }) {
  return (
    <>
      {coverUrl && (
        <div
          className="series-hero-art"
          style={{ backgroundImage: `url(${coverUrl})` }}
          aria-hidden
        />
      )}
      <div className="series-hero-falloff" aria-hidden />
      <div className="series-hero-scrim-x" aria-hidden />
      <div className="series-hero-scrim-y" aria-hidden />
    </>
  )
}
