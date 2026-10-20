import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { api, failWith } from '../../.storybook/mocks/api'
import { LoginPage } from './LoginPage'

const meta = {
  component: LoginPage,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', auth: 'signedOut', router: { initialEntries: ['/'] } },
} satisfies Meta<typeof LoginPage>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    // The SSO button only appears once /initialize.json (MSW) has answered.
    await expect(await canvas.findByRole('link', { name: /continue with authentik/i })).toBeVisible()
  },
}

export const WrongPassword: Story = {
  parameters: {
    msw: [http.post(api('/auth/login'), () => failWith(401, 'Invalid username or password', 'error.auth.invalidCredentials'))],
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByLabelText(/username/i), 'reader')
    await userEvent.type(await canvas.findByLabelText(/password/i, { selector: 'input' }), 'not-it')
    await userEvent.click(await canvas.findByRole('button', { name: /^sign in$/i }))
    await waitFor(() => expect(canvas.getByRole('alert')).toHaveTextContent('Invalid username or password'))
  },
}

export const TwoFactor: Story = {
  parameters: {
    msw: [http.post(api('/auth/login'), () => HttpResponse.json({ requiresTwoFactor: true }))],
  },
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByLabelText(/username/i), 'reader')
    await userEvent.type(await canvas.findByLabelText(/password/i, { selector: 'input' }), 'correct')
    await userEvent.click(await canvas.findByRole('button', { name: /^sign in$/i }))
    await expect(await canvas.findByRole('heading', { name: /two-factor code/i })).toBeVisible()
  },
}

export const SsoFailed: Story = {
  parameters: { router: { initialEntries: ['/?ssoError=access_denied'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('alert')).toBeVisible()
  },
}
