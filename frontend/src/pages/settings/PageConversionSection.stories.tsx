import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api } from '../../../.storybook/mocks/api'
import { pageConversionOff, pageConversionRunning } from '../../../.storybook/mocks/settings/data'
import { PageConversionSection } from './PageConversionSection'

const meta = {
  component: PageConversionSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 860 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof PageConversionSection>

export default meta
type Story = StoryObj<typeof meta>

export const AfterAPass: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('radio', { name: 'WebP' })).toBeChecked()
    await expect(canvas.getByRole('textbox', { name: 'WebP quality' })).toHaveValue('80')
    await expect(canvas.getByText(/412 files converted so far/)).toBeVisible()
    await expect(canvas.getByText(/wait in the upgrade trash for 14 days/)).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Convert library' })).toBeEnabled()
  },
}

export const Off: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/pageconversion'), () => HttpResponse.json(pageConversionOff))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('radio', { name: 'Off' })).toBeChecked()
    await expect(canvas.queryByRole('textbox', { name: /quality/ })).toBeNull()
    await expect(canvas.getByText(/Choose a format to convert them/)).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Convert library' })).toBeDisabled()
  },
}

export const Running: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/pageconversion'), () => HttpResponse.json(pageConversionRunning))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Pause' })).toBeVisible()
    await expect(canvas.getByText('Lanterns Over Kasumi (2021)/Lanterns Over Kasumi - c040.cbz')).toBeVisible()
    await expect(canvas.getByRole('textbox', { name: 'AVIF quality' })).toHaveValue('55')
  },
}

export const PausedForDisk: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/pageconversion'), () =>
        HttpResponse.json({
          ...pageConversionRunning,
          pass: {
            ...pageConversionRunning.pass,
            state: 'paused',
            hold: 'lowDisk',
            currentFile: null,
          },
        }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/less than 1 GB free/)).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Resume' })).toBeEnabled()
  },
}

export const SwitchingFormatMarksUnsaved: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('radio', { name: 'AVIF' }))
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
    await expect(canvas.getByRole('textbox', { name: 'AVIF quality' })).toHaveValue('55')
  },
}
