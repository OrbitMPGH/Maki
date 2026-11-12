import { Group, SegmentedControl, Slider, Stack, Switch, Text } from '@mantine/core'
import { Trans, useLingui } from '@lingui/react/macro'
import { useLabel } from '../../i18n-context'
import type { ReaderPrefs } from './prefs'
import { TAP_LAYOUTS, TAP_WIDTH_MAX, TAP_WIDTH_MIN, tapZoneCells, type TapLayout } from './tapZones'

type TapPrefs = Pick<ReaderPrefs, 'tapLayout' | 'tapWidth' | 'tapInvert' | 'direction'>

const PREVIEW_W = 60
const PREVIEW_H = 84

/** A thumbnail of the zones, for the profile editor, where no page is open to draw them on. */
export function TapZonePreview({ prefs, vertical }: { prefs: TapPrefs; vertical: boolean }) {
  const cells = tapZoneCells(prefs, vertical)
  const rtl = prefs.direction === 'rtl'
  const forward = vertical ? '↓' : rtl ? '←' : '→'
  const back = vertical ? '↑' : rtl ? '→' : '←'
  return (
    <svg
      className="reader-tap-preview"
      width={PREVIEW_W}
      height={PREVIEW_H}
      viewBox={`0 0 ${PREVIEW_W} ${PREVIEW_H}`}
      aria-hidden
    >
      {cells.map((cell) => (
        <rect
          key={cell.key}
          data-action={cell.action}
          x={cell.x * PREVIEW_W}
          y={cell.y * PREVIEW_H}
          width={cell.width * PREVIEW_W}
          height={cell.height * PREVIEW_H}
        />
      ))}
      {cells
        .filter((cell) => cell.action !== 'menu')
        .map((cell) => (
          <text
            key={`label-${cell.key}`}
            x={(cell.x + cell.width / 2) * PREVIEW_W}
            y={(cell.y + cell.height / 2) * PREVIEW_H}
            textAnchor="middle"
            dominantBaseline="central"
          >
            {cell.action === 'next' ? forward : back}
          </text>
        ))}
      <rect className="reader-tap-preview-edge" x={0.5} y={0.5} width={PREVIEW_W - 1} height={PREVIEW_H - 1} rx={3} />
    </svg>
  )
}

/**
 * Tap zone layout, zone size and swap. In the reader the zones are drawn on the page while this is
 * open (TapZoneOverlay), so only the profile editor asks for the thumbnail. Continuous has no
 * layouts: its bands are top and bottom, and the zone size sets their height.
 */
export default function TapZoneSettings({
  prefs,
  onPrefs,
  vertical,
  preview = false,
  hintClassName,
}: {
  prefs: TapPrefs
  onPrefs: (patch: Partial<ReaderPrefs>) => void
  vertical: boolean
  preview?: boolean
  hintClassName?: string
}) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const layout = TAP_LAYOUTS.find((entry) => entry.value === prefs.tapLayout) ?? TAP_LAYOUTS[0]
  const width = prefs.tapWidth
  const hint = vertical
    ? t`In Continuous a band at the top goes back and one at the bottom goes forward. The layouts apply to Single and Double.`
    : renderLabel(layout.hint)
  return (
    <Stack gap={10}>
      <div>
        <Text fz="xs" fw={600} c="var(--ink-3)" mb={6}>
          <Trans>Tap zone layout</Trans>
        </Text>
        <SegmentedControl
          aria-label={t`Tap zone layout`}
          fullWidth
          size="xs"
          disabled={vertical}
          value={prefs.tapLayout}
          onChange={(value) => onPrefs({ tapLayout: value as TapLayout })}
          data={TAP_LAYOUTS.map((entry) => ({ value: entry.value, label: renderLabel(entry.label) }))}
        />
      </div>
      <Group gap="md" wrap="nowrap" align="flex-start">
        {preview && <TapZonePreview prefs={prefs} vertical={vertical} />}
        <Stack gap={8} style={{ flex: 1, minWidth: 0 }}>
          <Text fz="xs" c="var(--ink-3)" className={hintClassName}>
            {hint}
          </Text>
          <div>
            <Text fz="xs" fw={600} c="var(--ink-3)" mb={4}>
              <Trans>Zone size ({width}%)</Trans>
            </Text>
            <Slider
              thumbLabel={t`Zone size`}
              size="xs"
              min={TAP_WIDTH_MIN}
              max={TAP_WIDTH_MAX}
              step={1}
              label={(value) => `${value}%`}
              value={prefs.tapWidth}
              onChange={(value) => onPrefs({ tapWidth: value })}
            />
          </div>
        </Stack>
      </Group>
      <Switch
        size="xs"
        labelPosition="left"
        label={t`Swap back and forward`}
        description={t`Puts the back zones where the forward ones were, for the other hand.`}
        checked={prefs.tapInvert}
        onChange={(event) => onPrefs({ tapInvert: event.currentTarget.checked })}
        classNames={{ body: 'reader-settings-switch', description: hintClassName }}
      />
    </Stack>
  )
}
