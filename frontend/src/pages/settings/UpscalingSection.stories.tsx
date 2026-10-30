import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api } from '../../../.storybook/mocks/api'
import { upscalingFailed, upscalingOff } from '../../../.storybook/mocks/settings/data'
import { UpscalingSection } from './UpscalingSection'

const meta = {
  component: UpscalingSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 860 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof UpscalingSection>

export default meta
type Story = StoryObj<typeof meta>

export const ReadyAndDownloading: Story = {
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('switch', { name: /Enable server upscaling/ })).toBeChecked())
    await expect(await canvas.findByRole('checkbox', { name: 'Sharp (server)' })).toBeChecked()
    await expect(canvas.getByRole('checkbox', { name: 'Crisp line art (server)' })).toBeChecked()
    await expect(canvas.getByText('Ready')).toBeVisible()
    await expect(canvas.getByText('Downloading')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Delete' })).toBeEnabled()
    await expect(canvas.getAllByRole('button', { name: 'Test speed' })[0]).toBeEnabled()
    await expect(await canvas.findByText('312 MB of 1,024 MB, 280 pages')).toBeVisible()
    await expect(canvas.getByRole('textbox', { name: 'Threads' })).toHaveValue('2')
    await expect(canvas.getByRole('link', { name: 'Third-party notices' })).toHaveAttribute(
      'href',
      'https://github.com/OrbitMPGH/Maki/blob/main/THIRD_PARTY_NOTICES.md',
    )
  },
}

export const TierOff: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/upscaling'), () => HttpResponse.json(upscalingOff))),
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('switch', { name: /Enable server upscaling/ })).not.toBeChecked())
    await expect(canvas.queryByRole('checkbox', { name: 'Sharp (server)' })).toBeNull()
    await expect(canvas.queryByRole('textbox', { name: 'Threads' })).toBeNull()
  },
}

export const SwitchingOnMarksUnsaved: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/upscaling'), () => HttpResponse.json(upscalingOff))),
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('switch', { name: /Enable server upscaling/ }))
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
    await expect(await canvas.findByRole('checkbox', { name: 'Sharp (server)' })).not.toBeChecked()
    await expect(canvas.getByRole('button', { name: 'Save' })).toBeEnabled()
    // A model with no size until its manifest is read goes straight to the licence.
    const unsized = (await canvas.findByRole('link', { name: 'xinntao (Real-ESRGAN)' })).closest('.meta-list')!
    await waitFor(() => {
      expect(unsized).toHaveTextContent('BSD-3-Clause')
      expect([...unsized.children].every((item) => item.textContent?.trim())).toBe(true)
    })
  },
}

export const FailedDownload: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/upscaling'), () => HttpResponse.json(upscalingFailed))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Failed')).toBeVisible()
    await expect(canvas.getByText('The download did not match its checksum and was removed.')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Try again' })).toBeEnabled()
  },
}

export const TestSpeed: Story = {
  play: async ({ canvas, userEvent }) => {
    const buttons = await canvas.findAllByRole('button', { name: 'Test speed' })
    await waitFor(() => expect(buttons[0]).toBeEnabled())
    await userEvent.click(buttons[0])
    await expect(await canvas.findByText('5.5 s per sample page')).toBeVisible()
  },
}
