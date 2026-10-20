import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { adminMe, api, failWith, neverResolve } from '../../.storybook/mocks/api'
import { SetupAccountPage } from './SetupAccountPage'

const meta = {
  component: SetupAccountPage,
  tags: ['ai-generated'],
  // Nobody is signed in before the administrator is claimed.
  parameters: { layout: 'fullscreen', auth: 'signedOut' },
} satisfies Meta<typeof SetupAccountPage>

export default meta
type Story = StoryObj<typeof meta>

const PASSWORD = 'correct horse battery'

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { name: 'Create your account' })).toBeVisible()
    await waitFor(() => expect(canvas.getByLabelText(/^username/i)).toHaveValue('admin'))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Create account' })).toBeDisabled())
    await expect(await canvas.findByText('Before exposing Maki to the internet')).toBeVisible()
  },
}

export const PasswordTooShort: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByLabelText(/^password/i, { selector: 'input' }), 'short')
    await expect(await canvas.findByText('Use at least 10 characters')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Create account' })).toBeDisabled())
  },
}

export const PasswordsDoNotMatch: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByLabelText(/^password/i, { selector: 'input' }), PASSWORD)
    await userEvent.type(await canvas.findByLabelText(/^confirm password/i), 'correct horse batter')
    await expect(await canvas.findByText('Passwords do not match')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Create account' })).toBeDisabled())
  },
}

export const ReadyToSubmit: Story = {
  play: async ({ canvas, userEvent }) => {
    const username = await canvas.findByLabelText(/^username/i)
    await userEvent.clear(username)
    await userEvent.type(username, 'maki')
    await userEvent.type(await canvas.findByLabelText(/^password/i, { selector: 'input' }), PASSWORD)
    await userEvent.type(await canvas.findByLabelText(/^confirm password/i), PASSWORD)
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Create account' })).toBeEnabled())
  },
}

export const Submitting: Story = {
  parameters: { msw: [http.post(api('/auth/setup'), () => neverResolve())] },
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByLabelText(/^password/i, { selector: 'input' }), PASSWORD)
    await userEvent.type(await canvas.findByLabelText(/^confirm password/i), PASSWORD)
    await userEvent.click(await canvas.findByRole('button', { name: 'Create account' }))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Create account' })).toHaveAttribute('data-loading', 'true'))
  },
}

export const CreatesTheAccount: Story = {
  parameters: { msw: [http.post(api('/auth/setup'), () => HttpResponse.json(adminMe))] },
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByLabelText(/^password/i, { selector: 'input' }), PASSWORD)
    await userEvent.type(await canvas.findByLabelText(/^confirm password/i), PASSWORD)
    await userEvent.click(await canvas.findByRole('button', { name: 'Create account' }))
    await expect(canvas.queryByRole('alert')).not.toBeInTheDocument()
  },
}

export const ServerRefuses: Story = {
  parameters: {
    msw: [http.post(api('/auth/setup'), () => failWith(409, 'This instance already has an administrator.', 'error.auth.alreadySetUp'))],
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByLabelText(/^password/i, { selector: 'input' }), PASSWORD)
    await userEvent.type(await canvas.findByLabelText(/^confirm password/i), PASSWORD)
    await userEvent.click(await canvas.findByRole('button', { name: 'Create account' }))
    await waitFor(() => expect(canvas.getByRole('alert')).toHaveTextContent('This instance already has an administrator.'))
  },
}

export const Mobile: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
}
