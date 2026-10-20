import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { SETTINGS_ENTRIES } from '../../pages/settings/registry'
import { SettingsIndex, SettingsJump } from './SettingsIndex'

const entries = SETTINGS_ENTRIES.filter((e) => e.tab === 'library')

// The index tracks real elements by `setting-{id}`, so the stories give it something to track.
const Cards = () => (
  <div style={{ display: 'flex', flexDirection: 'column', gap: 24, minHeight: 600 }}>
    {entries.map((e) => (
      <div key={e.id} id={`setting-${e.id}`} style={{ padding: 24, border: '1px solid var(--line, #444)', borderRadius: 8 }}>
        {e.id}
      </div>
    ))}
  </div>
)

const meta = {
  component: SettingsIndex,
  tags: ['ai-generated'],
  parameters: { layout: 'padded' },
  args: { entries },
  render: (args) => (
    <div style={{ display: 'grid', gridTemplateColumns: '1fr 220px', gap: 24 }}>
      <Cards />
      <SettingsIndex {...args} />
    </div>
  ),
} satisfies Meta<typeof SettingsIndex>

export default meta
type Story = StoryObj<typeof meta>

export const Rail: Story = {
  play: async ({ canvas }) => {
    const nav = within(await canvas.findByRole('navigation', { name: 'Sections on this tab' }))
    await expect(await nav.findByRole('button', { name: 'Root folders' })).toBeVisible()
    await expect(await nav.findByRole('button', { name: 'Recycle bin' })).toBeVisible()
  },
}

export const SingleEntryHidesItself: Story = {
  args: { entries: entries.slice(0, 1) },
  play: async ({ canvas }) => {
    await expect(canvas.queryByRole('navigation', { name: 'Sections on this tab' })).toBeNull()
  },
}

export const JumpMenu: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  render: (args) => (
    <div>
      <SettingsJump {...args} />
      <Cards />
    </div>
  ),
  play: async ({ canvas, canvasElement, userEvent }) => {
    const body = within(canvasElement.ownerDocument.body)
    await userEvent.click(await canvas.findByRole('button', { name: /\S/ }))
    const item = await body.findByRole('menuitem', { name: 'Metadata database' })
    await waitFor(() => expect(item).toBeVisible())
  },
}
