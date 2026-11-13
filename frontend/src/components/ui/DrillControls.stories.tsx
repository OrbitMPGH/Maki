import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor } from 'storybook/test'
import { Paper } from '@mantine/core'
import { IconBook, IconCalendar } from '../../lib/icons'
import {
  DrillFooter,
  DrillGroup,
  DrillMultiChoices,
  DrillNumberRange,
  DrillSearch,
  DrillSliderRange,
  DrillSwitch,
  DrillTriChoices,
} from './DrillControls'
import { DrillRow } from './DrillPanel'

const TIERS = [
  { value: 'small', label: 'Small pages', hint: 'Under 800 pixels wide.' },
  { value: 'colour', label: 'Colour pages' },
  { value: 'credits', label: 'Credit pages' },
]
const GENRES = ['Action', 'Comedy', 'Drama', 'Fantasy', 'Horror', 'Romance'].map((g) => ({ value: g, label: g }))

const onReset = fn()
const onDone = fn()

function Demo() {
  const [tiers, setTiers] = useState<string[]>(['colour'])
  const [include, setInclude] = useState<string[]>(['Action'])
  const [exclude, setExclude] = useState<string[]>(['Horror'])
  const [query, setQuery] = useState('')
  const [from, setFrom] = useState<number | null>(2010)
  const [to, setTo] = useState<number | null>(null)
  const [read, setRead] = useState<[number, number]>([0, 100])
  const [untagged, setUntagged] = useState(false)
  const genres = GENRES.filter((g) => g.label.toLowerCase().includes(query.toLowerCase()))
  return (
    <Paper withBorder radius="lg" p={6} w={320}>
      <DrillGroup heading="Reading">
        <DrillRow icon={IconBook} label="Read" value="Any" onClick={() => {}} />
        <DrillRow icon={IconCalendar} label="Year" value="From 2010" active onClick={() => {}} />
      </DrillGroup>
      <DrillMultiChoices label="Don't enhance" value={tiers} options={TIERS} onChange={setTiers} />
      <DrillSearch label="Search genres" placeholder="Search" value={query} onChange={setQuery} />
      <div id="genres-help">Tap once to include, again to leave out, a third time to clear.</div>
      <DrillTriChoices
        label="Genres"
        describedBy="genres-help"
        include={include}
        exclude={exclude}
        options={genres}
        onChange={(i, e) => {
          setInclude(i)
          setExclude(e)
        }}
      />
      <DrillNumberRange
        icon={IconCalendar}
        title="Year"
        help="Leave both boxes empty to ignore."
        from={{ label: 'Earliest year', placeholder: 'From', value: from, onChange: setFrom }}
        to={{ label: 'Latest year', placeholder: 'To', value: to, onChange: setTo }}
        inputProps={{ min: 0, max: 9999, allowDecimal: false, thousandSeparator: false }}
      />
      <DrillSliderRange
        icon={IconBook}
        title="Read"
        help="Leave at 0 to 100% to ignore."
        min={0}
        max={100}
        step={5}
        value={read}
        onChange={setRead}
        fromLabel="Read from"
        toLabel="Read to"
        format={(n) => `${n}%`}
      />
      <DrillSwitch label="Untagged only" hint="Series with none of your tags" checked={untagged} onChange={setUntagged} />
      <DrillFooter onReset={onReset} status="128 series" onDone={onDone} />
    </Paper>
  )
}

/** The drill pieces for filter-style pages: multi-pick, include and leave out, search, ranges, a switch and the footer. */
const meta = {
  component: Demo,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
} satisfies Meta<typeof Demo>

export default meta
type Story = StoryObj<typeof meta>

export const MultiPick: Story = {
  play: async ({ canvas, userEvent }) => {
    const group = await canvas.findByRole('group', { name: "Don't enhance" })
    await expect(group).toBeVisible()
    const small = await canvas.findByRole('checkbox', { name: /^Small pages/ })
    await userEvent.click(small)
    await waitFor(() => expect(small).toHaveAttribute('aria-checked', 'true'))
    await expect(await canvas.findByRole('checkbox', { name: 'Colour pages' })).toHaveAttribute('aria-checked', 'true')
  },
}

export const IncludeAndLeaveOut: Story = {
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByRole('checkbox', { name: 'Action Included' })).toHaveAttribute('aria-checked', 'true')
    await expect(await canvas.findByRole('checkbox', { name: 'Horror Left out' })).toHaveAttribute('aria-checked', 'false')
    await expect(await canvas.findByRole('group', { name: 'Genres' })).toHaveAccessibleDescription(/Tap once to include/)
    const comedy = await canvas.findByRole('checkbox', { name: 'Comedy' })
    await userEvent.click(comedy)
    await waitFor(() => expect(comedy).toHaveAttribute('aria-checked', 'true'))
    await userEvent.click(comedy)
    await waitFor(() => expect(comedy).toHaveAttribute('aria-checked', 'false'))
    await expect(comedy).toHaveAccessibleName('Comedy Left out')
    await userEvent.click(comedy)
    await waitFor(() => expect(comedy).toHaveAttribute('aria-checked', 'false'))
  },
}

export const SearchNarrowsTheList: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByRole('textbox', { name: 'Search genres' }), 'dra')
    await waitFor(() => expect(canvas.getAllByRole('checkbox', { name: /^(Action|Comedy|Drama|Fantasy|Horror|Romance)/ })).toHaveLength(1))
    await userEvent.click(await canvas.findByRole('button', { name: 'Clear search' }))
    await waitFor(() => expect(canvas.getAllByRole('checkbox', { name: /^(Action|Comedy|Drama|Fantasy|Horror|Romance)/ })).toHaveLength(6))
  },
}

export const Ranges: Story = {
  play: async ({ canvas, userEvent }) => {
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Earliest year' })).toHaveValue('2010'))
    await userEvent.type(await canvas.findByRole('textbox', { name: 'Latest year' }), '2024')
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Latest year' })).toHaveValue('2024'))
    const lo = await canvas.findByRole('slider', { name: 'Read from' })
    lo.focus()
    await userEvent.keyboard('{ArrowRight}')
    await waitFor(() => expect(lo).toHaveAttribute('aria-valuenow', '5'))
  },
}

export const SwitchAndFooter: Story = {
  play: async ({ canvas, userEvent }) => {
    const toggle = await canvas.findByRole('switch', { name: /Untagged only/ })
    await userEvent.click(toggle)
    await waitFor(() => expect(toggle).toBeChecked())
    await expect(await canvas.findByRole('status')).toHaveTextContent('128 series')
    await userEvent.click(await canvas.findByRole('button', { name: 'Reset' }))
    await userEvent.click(await canvas.findByRole('button', { name: 'Done' }))
    await waitFor(() => expect(onReset).toHaveBeenCalled())
    await waitFor(() => expect(onDone).toHaveBeenCalled())
  },
}
