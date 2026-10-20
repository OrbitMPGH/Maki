import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { Button, List, Stack, Text, TextInput } from '@mantine/core'
import { AuthError, AuthFrame } from './AuthFrame'

const meta = {
  component: AuthFrame,
  tags: ['ai-generated'],
  parameters: { layout: 'fullscreen', auth: 'signedOut' },
  args: {
    title: 'Sign in',
    children: (
      <Stack>
        <TextInput label="Username" />
        <TextInput label="Password" type="password" />
        <Button fullWidth>Sign in</Button>
      </Stack>
    ),
  },
} satisfies Meta<typeof AuthFrame>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByRole('heading', { level: 1, name: 'Maki' })).toBeVisible()
    await expect(await canvas.findByText('Self-hosted manga library and downloader.')).toBeVisible()
    await expect(await canvas.findByRole('heading', { level: 2, name: 'Sign in' })).toBeVisible()
  },
}

export const WithSubtitle: Story = {
  args: {
    title: 'Create your account',
    subtitle: 'Create the administrator account. Your library and reading history are already attached to it.',
  },
}

export const WithFooter: Story = {
  args: {
    title: 'Create your account',
    footer: (
      <div className="auth-note">
        <Text fz="sm" fw={600} mb={6}>
          Before exposing Maki to the internet
        </Text>
        <List fz="sm" c="var(--ink-3)" spacing={4}>
          <List.Item>Put it behind HTTPS, then turn on Require HTTPS.</List.Item>
          <List.Item>Name your reverse proxy under Trusted proxies.</List.Item>
        </List>
      </div>
    ),
  },
}

export const WithError: Story = {
  args: {
    children: (
      <Stack>
        <TextInput label="Username" defaultValue="reader" />
        <TextInput label="Password" type="password" defaultValue="wrong" />
        <AuthError>Invalid username or password</AuthError>
        <Button fullWidth>Sign in</Button>
      </Stack>
    ),
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getByRole('alert')).toHaveTextContent('Invalid username or password'))
  },
}

export const Mobile: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
}
