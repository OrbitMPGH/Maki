import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api } from '../../../.storybook/mocks/api'
import { upscalingFailed, upscalingNoJpegXl, upscalingOff, upscalingPrepare } from '../../../.storybook/mocks/settings/data'
import { UpscalingSection } from './UpscalingSection'
import { SETTINGS_TARGET_EVENT } from '../../components/settings/SettingsDrill'

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

type Canvas = ReturnType<typeof within>
type User = { click: (element: Element) => Promise<void> }

// The model list opens in a modal from the card.
async function openModels(canvas: Canvas, userEvent: User) {
  await userEvent.click(await canvas.findByRole('button', { name: 'Manage models' }))
  return within(await within(document.body).findByRole('dialog', { name: 'Upscaling models' }))
}

// Processing, the page cache and prepare-ahead are drill rows that open a page in the card.
async function openPage(canvas: Canvas, userEvent: User, row: RegExp) {
  await userEvent.click(await canvas.findByRole('button', { name: row }))
  await canvas.findByRole('button', { name: /^Back/ })
}

export const ReadyAndDownloading: Story = {
  play: async ({ canvas, userEvent }) => {
    await waitFor(() => expect(canvas.getByRole('switch', { name: /Enable server upscaling/ })).toBeChecked())
    await expect(await canvas.findByText(/^Maki 2x, SPAN-S,? and Real-ESRGAN$/)).toBeVisible()
    await expect(canvas.getByRole('button', { name: /^Processing.*2 threads, JPEG XL on/ })).toBeVisible()
    await expect(canvas.getByRole('button', { name: /^Page cache.*312 MB of 1,024 MB, 280 pages/ })).toBeVisible()

    const models = await openModels(canvas, userEvent)
    await expect(await models.findByRole('checkbox', { name: 'Maki 2x' })).toBeChecked()
    await expect(models.getByRole('checkbox', { name: 'SPAN-S' })).toBeChecked()
    await expect(models.getByRole('checkbox', { name: 'Real-ESRGAN' })).toBeChecked()
    await expect(models.getByRole('checkbox', { name: 'Maki 2x HQ' })).not.toBeChecked()
    await expect(models.getByText('Recommended')).toBeVisible()
    await expect(models.getByRole('img', { name: 'Speed: Very fast' })).toBeVisible()
    await waitFor(() => expect(models.getAllByRole('img', { name: 'Quality: Best' })).toHaveLength(2))
    await waitFor(() => expect(models.getAllByText('Ready')).toHaveLength(1))
    await waitFor(() => expect(models.getAllByText('Included')).toHaveLength(2))
    await expect(models.getAllByRole('button', { name: /^Delete / })).toHaveLength(1)
    await expect(models.getByText('Downloading')).toBeVisible()
    await waitFor(() => expect(models.getByRole('button', { name: 'Delete SPAN-S' })).toBeEnabled())
    await expect(models.getByRole('button', { name: 'Test speed of Maki 2x' })).toBeEnabled()
    await expect(models.getByRole('link', { name: 'Third-party notices' })).toHaveAttribute(
      'href',
      'https://github.com/OrbitMPGH/Maki/blob/main/THIRD_PARTY_NOTICES.md',
    )
  },
}

export const ProcessingPage: Story = {
  play: async ({ canvas, userEvent }) => {
    await openPage(canvas, userEvent, /^Processing/)
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Threads' })).toHaveValue('2'))
    await userEvent.click(canvas.getByRole('button', { name: /^Back/ }))
    await expect(await canvas.findByRole('button', { name: /^Page cache/ })).toBeVisible()
  },
}

/** A deep link to a control on the first page brings the drill back there from another page. */
export const DeepLinkLeavesAnOpenPage: Story = {
  play: async ({ canvas, userEvent }) => {
    await openPage(canvas, userEvent, /^Page cache/)
    document.dispatchEvent(new CustomEvent(SETTINGS_TARGET_EVENT, { detail: 'upscaling-jpeg-xl' }))
    await waitFor(() => expect(canvas.getByRole('button', { name: /^Processing/ })).toBeVisible())
    await waitFor(() => expect(canvas.queryByRole('button', { name: /^Back/ })).toBeNull())
  },
}

export const PrepareAhead: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/upscaling'), () => HttpResponse.json(upscalingPrepare))),
  },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByRole('button', { name: /^Prepare ahead.*On, 9 chapters held/ })).toBeVisible()
    await expect(canvas.getByRole('button', { name: /^Page cache.*312 MB of 1,024 MB, 912 pages/ })).toBeVisible()
    await openPage(canvas, userEvent, /^Prepare ahead/)
    await waitFor(() => expect(canvas.getByRole('switch', { name: /Prepare chapters ahead/ })).toBeChecked())
    await expect(await canvas.findByText(/9 chapters held, 812 MB of 4,096 MB/)).toBeVisible()
    await expect(canvas.getByText(/38 pages still to make/)).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Series per reader' })).toHaveValue('8'))
  },
}

export const TierOff: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/upscaling'), () => HttpResponse.json(upscalingOff))),
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('switch', { name: /Enable server upscaling/ })).not.toBeChecked())
    await expect(canvas.queryByRole('button', { name: 'Manage models' })).toBeNull()
    await expect(canvas.queryByRole('button', { name: /^Processing/ })).toBeNull()
  },
}

export const SwitchingOnMarksUnsaved: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/upscaling'), () => HttpResponse.json(upscalingOff))),
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('switch', { name: /Enable server upscaling/ }))
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
    await expect(canvas.getByRole('button', { name: 'Save' })).toBeEnabled()
    await expect(await canvas.findByText('None ticked')).toBeVisible()
    const models = await openModels(canvas, userEvent)
    await expect(await models.findByRole('checkbox', { name: 'SPAN-S' })).not.toBeChecked()
    // A model with no size until its manifest is read goes straight to the licence.
    const unsized = (await models.findByRole('link', { name: 'xinntao (Real-ESRGAN)' })).closest('.meta-list')!
    await waitFor(() => {
      expect(unsized).toHaveTextContent('BSD-3-Clause')
      expect([...unsized.children].every((item) => item.textContent?.trim())).toBe(true)
    })
  },
}

/** Ticking a model in the modal is part of the card's draft: Save is on the card. */
export const TickingAModelMarksUnsaved: Story = {
  play: async ({ canvas, userEvent }) => {
    const models = await openModels(canvas, userEvent)
    const hq = await models.findByRole('checkbox', { name: 'Maki 2x HQ' })
    await waitFor(() => expect(hq).not.toBeChecked())
    await userEvent.click(hq)
    await expect(await models.findByText('Save the card to apply your picks.')).toBeVisible()
    await userEvent.click(models.getByRole('button', { name: 'Done' }))
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
    await expect(await canvas.findByText(/Maki 2x HQ/)).toBeVisible()
  },
}

export const FailedDownload: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/upscaling'), () => HttpResponse.json(upscalingFailed))),
  },
  play: async ({ canvas, userEvent }) => {
    const models = await openModels(canvas, userEvent)
    await expect(await models.findByText('Failed')).toBeVisible()
    await expect(models.getByText('The download did not match its checksum and was removed.')).toBeVisible()
    await expect(models.getByRole('button', { name: 'Try downloading Real-ESRGAN again' })).toBeEnabled()
  },
}

export const TestSpeed: Story = {
  play: async ({ canvas, userEvent }) => {
    const models = await openModels(canvas, userEvent)
    const test = await models.findByRole('button', { name: 'Test speed of Maki 2x' })
    await waitFor(() => expect(test).toBeEnabled())
    await userEvent.click(test)
    await expect(await models.findByRole('button', { name: '5.5 s per sample page. Test Maki 2x again' })).toBeVisible()
  },
}

export const JpegXlOffMarksUnsaved: Story = {
  play: async ({ canvas, userEvent }) => {
    await openPage(canvas, userEvent, /^Processing/)
    const toggle = await canvas.findByRole('switch', { name: /JPEG XL for colour pages/ })
    await waitFor(() => expect(toggle).toBeChecked())
    await userEvent.click(toggle)
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
    await waitFor(() => expect(toggle).not.toBeChecked())
  },
}

export const JpegXlUnavailable: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/upscaling'), () => HttpResponse.json(upscalingNoJpegXl))),
  },
  play: async ({ canvas, userEvent }) => {
    await expect(await canvas.findByRole('button', { name: /^Processing.*2 threads$/ })).toBeVisible()
    await openPage(canvas, userEvent, /^Processing/)
    const toggle = await canvas.findByRole('switch', { name: /JPEG XL for colour pages/ })
    await waitFor(() => {
      expect(toggle).toBeDisabled()
      expect(toggle).not.toBeChecked()
    })
    await expect(canvas.getByText(/this build of Maki has no JPEG XL library/)).toBeVisible()
  },
}
