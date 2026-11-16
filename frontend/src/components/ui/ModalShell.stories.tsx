import type { Meta, StoryObj } from '@storybook/react-vite'
import { Button, Modal, Stack, Text } from '@mantine/core'
import { expect, fn, waitFor, within } from 'storybook/test'
import { ModalFooter, ModalHeading, ModalNote } from './ModalShell'

function ShellDemo({ onClose, rows = 4, note = false }: { onClose: () => void; rows?: number; note?: boolean }) {
  return (
    <Modal
      opened
      onClose={onClose}
      size="lg"
      title={
        <ModalHeading
          title="Rename files"
          description="Matches your naming formats. Only the highlighted parts change."
        />
      }
    >
      <Stack gap="sm">
        {note && <ModalNote>Two chapters want one name. Add the chapter language to the format.</ModalNote>}
        {Array.from({ length: rows }, (_, i) => (
          <Text key={i} size="sm">
            Lanterns Over Kasumi c{String(i + 1).padStart(3, '0')}.cbz
          </Text>
        ))}
      </Stack>
      <ModalFooter status={`${rows} files will be renamed`}>
        <Button variant="default" onClick={onClose}>
          Cancel
        </Button>
        <Button>Rename files</Button>
      </ModalFooter>
    </Modal>
  )
}

const meta = {
  component: ShellDemo,
  tags: ['ai-generated'],
  args: { onClose: fn() },
} satisfies Meta<typeof ShellDemo>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Rename files' })
    await waitFor(() => expect(dialog).toHaveAccessibleName('Rename files'))
    await waitFor(() =>
      expect(dialog).toHaveAccessibleDescription(/Matches your naming formats\. Only the highlighted parts change\./),
    )
    await expect(await within(dialog).findByText('4 files will be renamed')).toBeVisible()
  },
}

export const WithNote: Story = {
  args: { note: true },
}

/** The footer stays pinned while a long body scrolls under it. */
export const LongBody: Story = {
  args: { rows: 60 },
  play: async ({ canvasElement }) => {
    const body = within(canvasElement.ownerDocument.body)
    const dialog = await body.findByRole('dialog', { name: 'Rename files' })
    await expect(await within(dialog).findByRole('button', { name: 'Rename files' })).toBeVisible()
  },
}
