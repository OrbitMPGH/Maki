import { SegmentedControl, Select } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { useLabel } from '../../i18n-context'
import { monthName } from '../../format'
import { RANGE_OPTIONS, type RangePreset } from './StatsRange'

/** The window picker: presets, and the year and month drill-down when Year is picked. */
export function RangeControl({
  preset,
  onPresetChange,
  year,
  onYearChange,
  month,
  onMonthChange,
  yearOptions,
  size = 'sm',
}: {
  preset: RangePreset
  onPresetChange: (preset: RangePreset) => void
  year: number
  onYearChange: (year: number) => void
  month: number | null
  onMonthChange: (month: number | null) => void
  yearOptions: string[]
  size?: 'xs' | 'sm'
}) {
  const { t } = useLingui()
  const label = useLabel()

  return (
    <div className="stats-range-group">
      <div className="stats-range">
        <SegmentedControl
          size={size}
          value={preset}
          onChange={(v) => onPresetChange(v as RangePreset)}
          data={RANGE_OPTIONS.map((option) => ({ value: option.value, label: label(option.label) }))}
          aria-label={t`Time range`}
        />
      </div>
      {preset === 'year' && (
        <>
          <Select
            data={yearOptions}
            value={String(year)}
            onChange={(v) => v && onYearChange(Number(v))}
            w={100}
            size={size}
            aria-label={t`Year`}
          />
          <Select
            data={[
              { value: 'all', label: t`Whole year` },
              ...Array.from({ length: 12 }, (_, i) => ({ value: String(i + 1), label: monthName(i + 1) })),
            ]}
            value={month === null ? 'all' : String(month)}
            onChange={(v) => onMonthChange(v === null || v === 'all' ? null : Number(v))}
            w={140}
            size={size}
            aria-label={t`Month`}
          />
        </>
      )}
    </div>
  )
}
