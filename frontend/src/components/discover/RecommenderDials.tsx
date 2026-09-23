import { Trans, useLingui } from '@lingui/react/macro'
import { Slider, Text } from '@mantine/core'

/**
 * The recommender's two dials, obscurity and variety. Two grid cells rather than one block so the
 * caller's grid lays them out beside its own sliders.
 */
export function RecommenderDials({
  obscurity,
  setObscurity,
  diversity,
  setDiversity,
}: {
  obscurity: number
  setObscurity: (value: number) => void
  diversity: number
  setDiversity: (value: number) => void
}) {
  const { t } = useLingui()
  const obscurityDisplay = obscurity.toFixed(2)
  const diversityDisplay = diversity.toFixed(2)

  return (
    <>
      <div>
        <Text size="sm" fw={500} mb={4}>
          {obscurity === 0 ? (
            <Trans>Obscurity: balanced</Trans>
          ) : obscurity > 0 ? (
            <Trans>Obscurity: hidden gems (+{obscurityDisplay})</Trans>
          ) : (
            <Trans>Obscurity: mainstream ({obscurityDisplay})</Trans>
          )}
        </Text>
        <Slider
          min={-1}
          max={1}
          step={0.25}
          value={obscurity}
          onChange={setObscurity}
          label={(v) => (v === 0 ? t`balanced` : v > 0 ? t`obscure` : t`popular`)}
          marks={[
            { value: -1, label: t`popular` },
            { value: 0, label: '·' },
            { value: 1, label: t`gems` },
          ]}
          color={obscurity >= 0 ? 'grape' : 'blue'}
        />
      </div>
      <div>
        <Text size="sm" fw={500} mb={4}>
          {diversity === 0 ? (
            <Trans>Variety: closest matches</Trans>
          ) : (
            <Trans>Variety: spread out ({diversityDisplay})</Trans>
          )}
        </Text>
        <Slider
          min={0}
          max={1}
          step={0.1}
          value={diversity}
          onChange={setDiversity}
          label={(v) => (v === 0 ? t`closest` : v.toFixed(1))}
          marks={[
            { value: 0, label: t`closest` },
            { value: 0.5, label: '·' },
            { value: 1, label: t`varied` },
          ]}
          color="var(--ok)"
        />
        {/* Mark labels are absolutely positioned, so they take no layout space — this has
            to clear them by hand or the caption lands on top of "closest"/"varied". */}
        <Text size="xs" c="var(--ink-3)" mt={26}>
          <Trans>Trades a little similarity for picks that aren't near-copies of each other.</Trans>
        </Text>
      </div>
    </>
  )
}
