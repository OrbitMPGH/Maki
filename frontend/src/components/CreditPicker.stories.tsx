import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, within } from 'storybook/test'
import { discoverPage } from '../../.storybook/mocks/discover'
import type { CatalogueCredit } from '../api/hooks'
import { CreditPicker } from './CreditPicker'

const meta = {
  component: CreditPicker,
  tags: ['ai-generated'],
  args: { value: [], onChange: fn() },
  render: function Render(args) {
    const [value, setValue] = useState<CatalogueCredit[]>(args.value)
    return (
      <div style={{ maxWidth: 440 }}>
        <CreditPicker
          value={value}
          onChange={(next) => {
            setValue(next)
            args.onChange(next)
          }}
        />
      </div>
    )
  },
  parameters: { layout: 'padded', msw: discoverPage() },
} satisfies Meta<typeof CreditPicker>

export default meta
type Story = StoryObj<typeof meta>

export const Empty: Story = {}

export const SearchAndPick: Story = {
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.type(await canvas.findByLabelText('Creators and studios'), 'Aoi')
    const option = await within(canvasElement.ownerDocument.body).findByRole('option', { name: /Aoi/ }, { timeout: 4000 })
    await userEvent.click(option)
    await expect(args.onChange).toHaveBeenCalledWith([expect.objectContaining({ name: expect.stringContaining('Aoi') })])
  },
}

export const WithPickedCredits: Story = {
  args: {
    value: [
      { name: 'Studio Lark', role: 'studio' },
      { name: 'Mio Tachibana' },
      { name: 'Aoi Mizuhara', role: 'author' },
    ],
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Studio Lark (Studio)')).toBeVisible()
    await expect(canvas.getByText('Mio Tachibana')).toBeVisible()
    await expect(canvas.getByText('Aoi Mizuhara (Story)')).toBeVisible()
  },
}
