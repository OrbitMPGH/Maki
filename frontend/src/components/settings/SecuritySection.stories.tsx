import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor, within } from 'storybook/test'
import { http, HttpResponse } from 'msw'
import { settingsMsw } from '../../../.storybook/mocks/settings'
import { api, failWith } from '../../../.storybook/mocks/api'
import { oidcSettings, securitySettings } from '../../../.storybook/mocks/settings/data'
import { NetworkSection, OidcSection, SecuritySection, SignInPageSection } from './SecuritySection'

const meta = {
  component: SecuritySection,
  tags: ['ai-generated'],
  parameters: { layout: 'padded', msw: settingsMsw() },
  decorators: [
    (Story) => (
      <div style={{ maxWidth: 820 }}>
        <Story />
      </div>
    ),
  ],
} satisfies Meta<typeof SecuritySection>

export default meta
type Story = StoryObj<typeof meta>

async function dialogOf(canvasElement: HTMLElement) {
  const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
  await waitFor(() => expect(dialog).toBeVisible())
  return within(dialog)
}

export const SignInPolicy: Story = {
  play: async ({ canvas }) => {
    const attempts = await canvas.findByRole('textbox', { name: /Failed sign-ins before lockout/ })
    await waitFor(() => expect(attempts).toHaveValue('5'))
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Lockout duration (minutes)' })).toHaveValue('15'))
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Session lifetime (days)' })).toHaveValue('30'))
  },
}

export const SignInPolicyEditSaveDiscard: Story = {
  play: async ({ canvas, userEvent }) => {
    const attempts = await canvas.findByRole('textbox', { name: /Failed sign-ins before lockout/ })
    await waitFor(() => expect(attempts).toHaveValue('5'))
    await userEvent.clear(attempts)
    await userEvent.type(attempts, '8')
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Discard' }))
    await waitFor(() => expect(attempts).toHaveValue('5'))
  },
}

export const SignInPolicyLockoutOff: Story = {
  parameters: {
    msw: settingsMsw(http.get(api('/settings/security'), () => HttpResponse.json({ ...securitySettings, lockoutMaxAttempts: 0 }))),
  },
  play: async ({ canvas }) => {
    const attempts = await canvas.findByRole('textbox', { name: /Failed sign-ins before lockout/ })
    await waitFor(() => expect(attempts).toHaveValue('0'))
  },
}

export const SignInPolicyLoadFailed: Story = {
  parameters: { msw: settingsMsw(http.get(api('/settings/security'), () => failWith(500))) },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.queryByRole('textbox', { name: /Failed sign-ins/ })).toBeNull())
  },
}

export const Network: Story = {
  render: () => <NetworkSection />,
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('switch', { name: /^Require HTTPS/ })).not.toBeChecked())
    await waitFor(() => expect(canvas.getByRole('textbox', { name: /^Trusted proxies/ })).toHaveValue('172.18.0.0/16'))
  },
}

export const NetworkRequireHttpsOn: Story = {
  render: () => <NetworkSection />,
  parameters: {
    msw: settingsMsw(http.get(api('/settings/security'), () => HttpResponse.json({ ...securitySettings, requireHttps: true }))),
  },
  play: async ({ canvas }) => {
    const toggle = await canvas.findByRole('switch', { name: /^Require HTTPS/ })
    await waitFor(() => expect(toggle).toBeChecked())
  },
}

export const NetworkEditShowsSave: Story = {
  render: () => <NetworkSection />,
  play: async ({ canvas, userEvent }) => {
    const proxies = await canvas.findByRole('textbox', { name: /^Trusted proxies/ })
    await waitFor(() => expect(proxies).toHaveValue('172.18.0.0/16'))
    await userEvent.type(proxies, ', 10.0.0.5')
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
  },
}

export const SignInPage: Story = {
  render: () => <SignInPageSection />,
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('switch', { name: /^Show covers from the library/ })).not.toBeChecked())
  },
}

export const SignInPageTurnOnCovers: Story = {
  render: () => <SignInPageSection />,
  play: async ({ canvas, userEvent }) => {
    const toggle = await canvas.findByRole('switch', { name: /^Show covers from the library/ })
    await waitFor(() => expect(toggle).not.toBeChecked())
    await userEvent.click(toggle)
    await expect(await canvas.findByText('Unsaved')).toBeVisible()
  },
}

export const SingleSignOn: Story = {
  render: () => <OidcSection />,
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Issuer URL' })).toHaveValue('https://sso.example.test/application/o/maki/'))
    await waitFor(() => expect(canvas.getByRole('switch', { name: /^Enable single sign-on/ })).toBeChecked())
    await expect(await canvas.findByText(/\/signin-oidc$/)).toBeVisible()
  },
}

export const SingleSignOnClaimsOverridePermissions: Story = {
  render: () => <OidcSection />,
  play: async ({ canvas }) => {
    await expect(await canvas.findByText(/your provider is the authority on permissions/)).toBeVisible()
  },
}

export const SingleSignOnBreakGlass: Story = {
  render: () => <OidcSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/oidc'), () => HttpResponse.json({ ...oidcSettings, oidcOnly: true, breakGlassActive: true, adminClaim: '' })),
    ),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('MAKI_ALLOW_LOCAL_LOGIN')).toBeVisible()
    await expect(canvas.queryByText(/your provider is the authority on permissions/)).toBeNull()
  },
}

export const SingleSignOnNotConfigured: Story = {
  render: () => <OidcSection />,
  parameters: {
    msw: settingsMsw(
      http.get(api('/settings/oidc'), () =>
        HttpResponse.json({ ...oidcSettings, enabled: false, authority: '', clientId: '', displayName: '', adminClaim: '' }),
      ),
    ),
  },
  play: async ({ canvas }) => {
    const toggle = await canvas.findByRole('switch', { name: /^Enable single sign-on/ })
    await waitFor(() => expect(toggle).not.toBeChecked())
    await waitFor(() => expect(canvas.getByRole('textbox', { name: 'Issuer URL' })).toHaveValue(''))
  },
}

export const SingleSignOnIssuerChangeAsksFirst: Story = {
  render: () => <OidcSection />,
  play: async ({ canvas, canvasElement, userEvent }) => {
    const issuer = await canvas.findByRole('textbox', { name: 'Issuer URL' })
    await waitFor(() => expect(issuer).toHaveValue('https://sso.example.test/application/o/maki/'))
    await userEvent.clear(issuer)
    await userEvent.type(issuer, 'https://login.other.test/')
    await userEvent.click(await canvas.findByRole('button', { name: 'Save' }))
    const dialog = await dialogOf(canvasElement)
    await expect(await dialog.findByRole('button', { name: 'Save anyway' })).toBeVisible()
    await userEvent.click(await dialog.findByRole('button', { name: 'Save anyway' }))
    await expect(await within(document.body).findByText('Single sign-on saved. Restart Maki to apply it.')).toBeVisible()
  },
}

export const SingleSignOnSaveWithoutIssuerChange: Story = {
  render: () => <OidcSection />,
  play: async ({ canvas, userEvent }) => {
    const name = await canvas.findByRole('textbox', { name: 'Button label' })
    await waitFor(() => expect(name).toHaveValue('Authentik'))
    await userEvent.type(name, ' SSO')
    await userEvent.click(await canvas.findByRole('button', { name: 'Save' }))
    await expect(await within(document.body).findByText('Single sign-on saved. Restart Maki to apply it.')).toBeVisible()
  },
}
