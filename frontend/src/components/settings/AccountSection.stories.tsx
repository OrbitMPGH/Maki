import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { adminMe, api, failWith, signedInAs } from '../../../.storybook/mocks/api'
import { twoFactorOff, twoFactorOn } from '../../../.storybook/mocks/settings/data'
import { ApiKeysSection, SignInSection } from './AccountSection'

const meta = {
  component: SignInSection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 820 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof SignInSection>

export default meta
type Story = StoryObj<typeof meta>

async function dialogOf(canvasElement: HTMLElement) {
  const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
  await waitFor(() => expect(dialog).toBeVisible())
  return within(dialog)
}

export const SignIn: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('maki')).toBeVisible()
    await expect(await canvas.findByText('Administrator')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Set up' })).toBeVisible()
    await expect(await canvas.findByText(/Not linked yet/)).toBeVisible()
  },
}

export const SignInReader: Story = {
  parameters: { auth: 'reader' },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('reader')).toBeVisible()
    await expect(canvas.queryByText('Administrator')).toBeNull()
  },
}

export const ChangePassword: Story = {
  play: async ({ canvas, userEvent }) => {
    const change = await canvas.findByRole('button', { name: 'Change' })
    await waitFor(() => expect(change).toBeDisabled())
    await userEvent.type(await canvas.findByLabelText('Current'), 'old-password')
    await userEvent.type(await canvas.findByLabelText('New'), 'a-much-longer-password')
    await waitFor(() => expect(change).toBeEnabled())
    await userEvent.click(change)
    await expect(await within(document.body).findByText('Password changed. Other devices have been signed out.')).toBeVisible()
  },
}

export const ChangePasswordTooShort: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByLabelText('Current'), 'old-password')
    await userEvent.type(await canvas.findByLabelText('New'), 'short')
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Change' })).toBeDisabled())
  },
}

export const TwoFactorEnabled: Story = {
  parameters: { msw: settingsMsw(http.get(api('/account/2fa'), () => HttpResponse.json(twoFactorOn))) },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('On')).toBeVisible()
    await expect(await canvas.findByText('8 recovery codes left. A new set replaces the old codes.')).toBeVisible()
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Disable' })).toBeDisabled())
  },
}

export const TwoFactorDisable: Story = {
  parameters: { msw: settingsMsw(http.get(api('/account/2fa'), () => HttpResponse.json(twoFactorOn))) },
  play: async ({ canvas, userEvent }) => {
    await userEvent.type(await canvas.findByLabelText('Confirm your password to turn it off'), 'my-password')
    await userEvent.click(await canvas.findByRole('button', { name: 'Disable' }))
    await expect(await within(document.body).findByText('Two-factor authentication disabled')).toBeVisible()
  },
}

export const TwoFactorSetupModal: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Set up' }))
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByText('JBSW Y3DP EHPK 3PXP')).toBeVisible()
    await expect(await dialog.findByRole('button', { name: 'Verify and enable' })).toBeVisible()
  },
}

export const TwoFactorEnableShowsRecoveryCodes: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Set up' }))
    const dialog = await dialogOf(canvasElement)
    await userEvent.type(await dialog.findByLabelText('Code from your app'), '123456')
    await userEvent.type(await dialog.findByLabelText('Your password'), 'my-password')
    await userEvent.click(await dialog.findByRole('button', { name: 'Verify and enable' }))
    const codes = await within(canvasElement.ownerDocument.body).findByText(/a1b2-c3d4/)
    await waitFor(() => expect(codes).toBeVisible())
    await expect(await within(canvasElement.ownerDocument.body).findByRole('button', { name: 'I have saved them' })).toBeVisible()
  },
}

export const TwoFactorWithoutPasswordLogin: Story = {
  parameters: {
    msw: settingsMsw(
      http.get(api('/account/2fa'), () => HttpResponse.json({ ...twoFactorOff, available: false, hasPassword: false })),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('This account has no password login for two-factor to protect.')).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Set up' })).toBeNull()
  },
}

export const TwoFactorDelegatedToSso: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/account/2fa'), () => HttpResponse.json({ ...twoFactorOff, ssoDelegated: true }))),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/Sign-in relies on your identity provider/)).toBeVisible()
    await expect(canvas.queryByRole('button', { name: 'Set up' })).toBeNull()
  },
}

export const SsoLinked: Story = {
  parameters: {
    msw: settingsMsw(signedInAs({ ...adminMe, oidcLinked: true, oidcUserName: 'maki@sso.example.test' })),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Linked')).toBeVisible()
    await expect(await canvas.findByText('maki@sso.example.test')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Remove' })).toBeVisible()
  },
}

export const SsoLinkedJustNow: Story = {
  parameters: { router: { initialEntries: ['/settings?oidcLinked=1'] } },
  play: async ({ canvas }) => {
    await expect(await within(document.body).findByText('Single sign-on linked to your account')).toBeVisible()
    await expect(await canvas.findByText(/Not linked yet/)).toBeVisible()
  },
}

export const SsoLinkDenied: Story = {
  parameters: { router: { initialEntries: ['/settings?oidcLinkError=access_denied'] } },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Single sign-on')).toBeVisible()
    await waitFor(() => expect(within(document.body).queryAllByRole('alert').length).toBeGreaterThan(0))
  },
}

export const SignOutOtherSessions: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Sign out everywhere else' }))
    await expect(await within(document.body).findByText('Other sessions signed out')).toBeVisible()
  },
}

export const ApiKeysList: Story = {
  render: () => <ApiKeysSection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Home automation')).toBeVisible()
    await expect(await canvas.findByText('Old laptop')).toBeVisible()
    await expect(await canvas.findByText('revoked')).toBeVisible()
    // The OPDS feed key is managed on the OPDS card, not listed here.
    await expect(canvas.queryByText('Reading app')).toBeNull()
  },
}

export const ApiKeysEmpty: Story = {
  render: () => <ApiKeysSection />,
  parameters: { msw: settingsMsw(http.get(api('/account/apikeys'), () => HttpResponse.json([]))) },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Create' })).toBeDisabled())
    await expect(canvas.queryByRole('table')).toBeNull()
  },
}

export const ApiKeyCreateShowsSecretOnce: Story = {
  render: () => <ApiKeysSection />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.type(await canvas.findByLabelText('Name'), 'Phone reader')
    await userEvent.type(await canvas.findByLabelText('Your password'), 'my-password')
    await userEvent.click(await canvas.findByRole('button', { name: 'Create' }))
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByText('mk_9z8y_example-secret-shown-once')).toBeVisible()
    await userEvent.click(await dialog.findByRole('button', { name: 'I have copied it' }))
    await waitFor(() => expect(within(canvasElement.ownerDocument.body).queryByRole('dialog')).toBeNull())
  },
}

export const ApiKeyCreateWithoutPasswordField: Story = {
  render: () => <ApiKeysSection />,
  parameters: {
    msw: settingsMsw(http.get(api('/account/2fa'), () => HttpResponse.json({ ...twoFactorOff, hasPassword: false, available: false }))),
  },
  play: async ({ canvas }) => {
    await canvas.findByLabelText('Name')
    await waitFor(() => expect(canvas.queryByLabelText('Your password')).toBeNull())
  },
}

export const ApiKeyRevokeConfirm: Story = {
  render: () => <ApiKeysSection />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Revoke' }))
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByText('Revoke Home automation?')).toBeVisible()
    await userEvent.click(await dialog.findByRole('button', { name: 'Revoke' }))
    await waitFor(() => expect(within(canvasElement.ownerDocument.body).queryByRole('dialog')).toBeNull())
  },
}

export const ApiKeyCreateRejected: Story = {
  render: () => <ApiKeysSection />,
  parameters: {
    msw: settingsMsw(http.post(api('/account/apikeys'), () => failWith(403, 'That password is not right.'))),
  },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.type(await canvas.findByLabelText('Name'), 'Phone reader')
    await userEvent.type(await canvas.findByLabelText('Your password'), 'wrong')
    await userEvent.click(await canvas.findByRole('button', { name: 'Create' }))
    await waitFor(() => expect(canvas.getByRole('button', { name: 'Create' })).toBeEnabled())
    await expect(within(canvasElement.ownerDocument.body).queryByRole('dialog')).toBeNull()
  },
}
