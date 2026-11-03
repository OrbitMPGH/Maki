import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api } from '../../../.storybook/mocks/api'
import { pageConversionIdle, pageConversionOff, pageConversionRunning } from '../../../.storybook/mocks/settings/data'
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
    await expect(await canvas.findByText('WebP, Balanced, 2 rules')).toBeVisible()
    await expect(canvas.getByText('1 series has its own setting in its menu.')).toBeVisible()
    await expect(canvas.getByText('PNG pages are kept lossless.')).toBeVisible()
    await expect(canvas.getByRole('link', { name: 'Change settings' })).toHaveAttribute(
      'href',
      '/settings/page-conversion?step=2',
    )
    await expect(canvas.queryByRole('radio')).toBeNull()
    await expect(await canvas.findByText(/412 files converted so far/)).toBeVisible()
    await expect(await canvas.findByText(/wait in the upgrade trash for 14 days/)).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Convert library' })).toBeEnabled())
  },
}

const onParallelSave = fn()

export const PagesConvertedAtOnce: Story = {
  parameters: {
    msw: settingsMsw(
      http.put(api('/pageconversion/parallel'), async ({ request }) => {
        const body = (await request.json()) as { limit: number | null }
        onParallelSave(body)
        return HttpResponse.json({ ...pageConversionIdle, parallel: { ...pageConversionIdle.parallel, limit: body.limit } })
      }),
    ),
  },
  beforeEach: () => onParallelSave.mockClear(),
  play: async ({ canvas, canvasElement, userEvent }) => {
    const select = await canvas.findByLabelText('Pages converted at once', { selector: 'input' })
    await waitFor(() => expect(select).toHaveValue('Automatic (4)'))

    await userEvent.click(select)
    const body = within(canvasElement.ownerDocument.body)
    await expect(await body.findByRole('option', { name: '16' })).toBeInTheDocument()
    await userEvent.click(body.getByRole('option', { name: '8' }))
    await waitFor(() => expect(onParallelSave).toHaveBeenCalledWith({ limit: 8 }))
    await waitFor(() => expect(select).toHaveValue('8'))
  },
}

export const Off: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/pageconversion'), () => HttpResponse.json(pageConversionOff))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Off')).toBeVisible()
    await expect(canvas.getByRole('link', { name: 'Set up conversion' })).toHaveAttribute(
      'href',
      '/settings/page-conversion',
    )
    await expect(canvas.queryByRole('link', { name: 'Change settings' })).toBeNull()
    await expect(await canvas.findByText(/Choose a format to convert them/)).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Convert library' })).toBeDisabled())
  },
}

export const Running: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/pageconversion'), () => HttpResponse.json(pageConversionRunning))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('button', { name: 'Pause' })).toBeVisible()
    await expect(await canvas.findByText('Lanterns Over Kasumi (2021)/Lanterns Over Kasumi - c040.cbz')).toBeVisible()
    await expect(await canvas.findByText('AVIF, High')).toBeVisible()
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
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Resume' })).toBeEnabled())
  },
}

export const PausedForSettingsChange: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/pageconversion'), () =>
        HttpResponse.json({
          ...pageConversionRunning,
          pass: { ...pageConversionRunning.pass, state: 'paused', hold: 'settingsChanged', currentFile: null },
        }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Paused because the settings changed\. Restart the pass to use them\./)).toBeVisible()
  },
}
