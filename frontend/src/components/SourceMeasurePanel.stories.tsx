import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn } from 'storybook/test'
import { hoursAgo } from '../../.storybook/mocks/api'
import { sourceOrderFor } from '../../.storybook/mocks/series'
import type { ScoutSnapshot, SourceOrderDto } from '../api/upgrades'
import { MeasureProgress, MeasureResult, ScoutCell } from './SourceMeasurePanel'

const base = sourceOrderFor(1)
const [first, second] = base.order

const scout = (overrides: Partial<ScoutSnapshot>): ScoutSnapshot => ({
  running: false,
  probes: 9,
  done: 9,
  measured: 6,
  startedAtUtc: hoursAgo(1),
  finishedAtUtc: hoursAgo(1),
  chapters: ['Ch. 12', 'Ch. 60', 'Ch. 131'],
  sources: [
    { mappingId: first, state: 'done', planned: 3, done: 3, measured: 3, problem: null },
    { mappingId: second, state: 'done', planned: 3, done: 3, measured: 3, problem: null },
    { mappingId: 13, state: 'skipped', planned: 0, done: 0, measured: 0, problem: null },
  ],
  ...overrides,
})

const orderWith = (overrides: Partial<SourceOrderDto>): SourceOrderDto => ({ ...base, ...overrides })

const labelOf = (mappingId: number) => (mappingId === first ? 'MangaDex' : mappingId === second ? 'Comick' : 'Weeb Central')

const meta = {
  component: MeasureResult,
  tags: ['ai-generated'],
  args: {
    order: orderWith({ mode: 'manual', order: [second, first], qualityOrder: [first, second], scout: scout({}) }),
    label: labelOf,
    needsFlareSolverr: (id: number) => id < 0,
    busy: null,
    onUseQuality: fn(),
    onReorder: fn(),
    onDismiss: fn(),
  },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 640 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof MeasureResult>

export default meta
type Story = StoryObj<typeof meta>

export const ResultSuggestsReorder: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByText(/By measured quality, downloads would try MangaDex first/)).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Use best quality first' }))
    await expect(args.onUseQuality).toHaveBeenCalledOnce()
    await userEvent.click(await canvas.findByRole('button', { name: 'Reorder priority to match' }))
    await expect(args.onReorder).toHaveBeenCalledOnce()
  },
}

export const ResultAlreadyBest: Story = {
  args: { order: orderWith({ mode: 'manual', order: [first, second], qualityOrder: [first, second], scout: scout({}) }) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/already tried first/)).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Use best quality first' })).toBeNull()
  },
}

export const ResultInQualityMode: Story = {
  args: { order: orderWith({ mode: 'quality', qualityOrder: [first, second], scout: scout({}) }) },
}

export const ResultNothingMeasured: Story = {
  args: {
    order: orderWith({
      scout: scout({
        measured: 0,
        sources: [
          { mappingId: first, state: 'failed', planned: 3, done: 3, measured: 0, problem: 'failed' },
          { mappingId: second, state: 'failed', planned: 3, done: 3, measured: 0, problem: 'cooldown' },
        ],
      }),
    }),
    needsFlareSolverr: (id) => id === second,
  },
}

export const ResultAdoptRunning: Story = {
  args: { busy: 'quality' },
}

export const Measuring: StoryObj<typeof MeasureProgress> = {
  render: () => (
    <div style={{ maxWidth: 640 }}>
      <MeasureProgress
        order={orderWith({
          scout: scout({
            running: true,
            done: 4,
            finishedAtUtc: null,
            sources: [
              { mappingId: first, state: 'measuring', planned: 3, done: 1, measured: 1, problem: null },
              { mappingId: second, state: 'waiting', planned: 3, done: 0, measured: 0, problem: null },
            ],
          }),
        })}
      />
    </div>
  ),
}

export const ScoutCells: StoryObj<typeof ScoutCell> = {
  render: () => (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <ScoutCell progress={{ mappingId: 1, state: 'waiting', planned: 3, done: 0, measured: 0, problem: null }} needsFlareSolverr={false} />
      <ScoutCell progress={{ mappingId: 1, state: 'measuring', planned: 3, done: 2, measured: 2, problem: null }} needsFlareSolverr={false} />
      <ScoutCell progress={{ mappingId: 1, state: 'failed', planned: 3, done: 3, measured: 0, problem: 'cooldown' }} needsFlareSolverr={false} />
      <ScoutCell progress={{ mappingId: 1, state: 'failed', planned: 3, done: 3, measured: 0, problem: 'failed' }} needsFlareSolverr />
      <ScoutCell progress={{ mappingId: 1, state: 'skipped', planned: 0, done: 0, measured: 0, problem: null }} needsFlareSolverr={false} />
    </div>
  ),
}
