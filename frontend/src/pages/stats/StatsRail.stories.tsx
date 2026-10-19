import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { RangeControl } from './RangeControl'
import { StatsRail } from './StatsRail'
import { StatsSection } from './StatsSection'
import { STATS_SECTIONS } from './sectionList'
import type { RangePreset } from './StatsRange'

function Window() {
  const [preset, setPreset] = useState<RangePreset>('30d')
  return (
    <RangeControl
      preset={preset}
      onPresetChange={setPreset}
      year={2026}
      onYearChange={() => {}}
      month={null}
      onMonthChange={() => {}}
      yearOptions={['2026']}
      size="xs"
    />
  )
}

const meta = {
  component: StatsRail,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', router: { initialEntries: ['/stats'] } },
  // The rail looks sections up by DOM id, so give it the real ids to scroll to.
  render: (args) => (
    <div className="stats-layout" style={{ padding: 16 }}>
      <StatsRail {...args} />
      {STATS_SECTIONS.map((s) => (
        <StatsSection key={s.key} sectionKey={s.key} icon={s.icon} title={s.key}>
          <div style={{ height: 320 }} />
        </StatsSection>
      ))}
    </div>
  ),
} satisfies Meta<typeof StatsRail>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('navigation', { name: 'Stats sections' })).toBeVisible()
    await expect(canvas.getByRole('button', { name: /Reading/ })).toHaveAttribute('aria-current', 'location')
  },
}

export const WithWindowPicker: Story = {
  args: { end: <Window /> },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(canvas.getByRole('button', { name: /Taste/ }))
    await expect(canvas.getByRole('button', { name: /Taste/ })).toHaveAttribute('aria-current', 'location')
    await expect(canvas.getByRole('radio', { name: '30 days' })).toBeChecked()
  },
}

/** The Library link carries a chip because that section covers everyone, not the selected reader. */
export const DeepLinked: Story = {
  parameters: { router: { initialEntries: ['/stats?section=library'] } },
  play: async ({ canvas }) => {
    await expect(canvas.getByRole('button', { name: /Library/ })).toHaveAttribute('aria-current', 'location')
    await expect(canvas.getByText('everyone')).toBeVisible()
  },
}
