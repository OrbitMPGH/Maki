import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { AppearancePicker } from './AppearancePicker'

const meta = {
  component: AppearancePicker,
  tags: ['ai-generated'],
} satisfies Meta<typeof AppearancePicker>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas, userEvent }) => {
    const accents = within(await canvas.findByRole('radiogroup', { name: 'Accent' }))
    const next = (await accents.findAllByRole('radio')).find((r) => r.getAttribute('aria-checked') === 'false')!
    await userEvent.click(next)
    await waitFor(() => expect(next).toHaveAttribute('aria-checked', 'true'))
  },
}

// Arrow keys rove across the Dark and Light groups as one radiogroup.
export const KeyboardAcrossGroups: Story = {
  globals: { background: 'oled' },
  play: async ({ canvas, userEvent }) => {
    const group = within(await canvas.findByRole('radiogroup', { name: 'Background' }))
    await expect(await group.findByText('Dark')).toBeVisible()
    const oled = await group.findByRole('radio', { name: 'OLED black' })
    await waitFor(() => expect(group.getAllByRole('radio')).toHaveLength(9))
    oled.focus()
    await userEvent.keyboard('{ArrowRight}')
    const light = await group.findByRole('radio', { name: 'Light' })
    await waitFor(() => expect(light).toHaveAttribute('aria-checked', 'true'))
    await waitFor(() => expect(light).toHaveFocus())
  },
}

export const LightGroundSelected: Story = {
  globals: { background: 'paper' },
  play: async ({ canvas }) => {
    const group = within(await canvas.findByRole('radiogroup', { name: 'Background' }))
    await waitFor(() => expect(group.getByRole('radio', { name: 'Paper' })).toHaveAttribute('aria-checked', 'true'))
  },
}

export const WithBackgroundHints: Story = {
  args: { backgroundHints: { tinted: { label: 'Default' }, night: { label: 'Dim', muted: true } } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Dim')).toBeVisible()
  },
}
