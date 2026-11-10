import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, waitFor } from 'storybook/test'
import { Paper, SegmentedControl } from '@mantine/core'
import { IconEye, IconEyeOff, IconFolder } from '../../lib/icons'
import { DrillPanes } from './DrillPanes'
import { DrillBack, DrillChoices, DrillField, DrillRow, useDrillPages } from './DrillPanel'

type Page = 'main' | 'folder' | 'privacy' | 'automation'
const PARENTS: Record<Page, Page> = { main: 'main', folder: 'main', privacy: 'main', automation: 'main' }

const FOLDERS = [
  { value: '1', label: '/library/manga', hint: '412 GB free' },
  { value: '2', label: '/library/manhwa', hint: '96 GB free' },
]
const PRIVACY = [
  { value: 'Off', label: 'Off', hint: 'Reading counts in your stats and goes to your trackers.' },
  { value: 'ScrobbleOnly', label: 'No scrobble', hint: "Reading counts in your stats but isn't sent to your trackers." },
  { value: 'Full', label: 'Full', hint: 'Kept out of your trackers, Rewind, stats and reading history.' },
]
const MONITOR_HELP: Record<string, string> = {
  All: 'Every chapter released from now on is wanted.',
  Smart: "Chapters released from now on are wanted and queued a batch at a time, ahead of where you're reading.",
  None: "Chapters released from now on aren't wanted.",
}

function Demo() {
  const pages = useDrillPages<Page>('main', PARENTS)
  const [folder, setFolder] = useState('1')
  const [privacy, setPrivacy] = useState('Off')
  const [monitor, setMonitor] = useState('Smart')
  const pick = (set: (v: string) => void) => (v: string) => {
    set(v)
    pages.back()
  }
  return (
    <Paper withBorder radius="lg" p={6} w={300}>
      <DrillPanes page={pages.page} depth={pages.depth} bleed={6}>
        {pages.page === 'main' && (
          <>
            <DrillRow ref={pages.rowRef('folder')} icon={IconFolder} label="Folder" value={FOLDERS.find((f) => f.value === folder)?.label} onClick={() => pages.open('folder')} />
            <DrillRow ref={pages.rowRef('privacy')} icon={IconEyeOff} label="Incognito" value={PRIVACY.find((p) => p.value === privacy)?.label} onClick={() => pages.open('privacy')} />
            <DrillRow ref={pages.rowRef('automation')} icon={IconEye} label="Automation" value={monitor} onClick={() => pages.open('automation')} />
          </>
        )}
        {pages.page === 'folder' && (
          <>
            <DrillBack ref={pages.backRef} title="Folder" onBack={pages.back} />
            <DrillChoices label="Folder" value={folder} options={FOLDERS} onPick={pick(setFolder)} />
          </>
        )}
        {pages.page === 'privacy' && (
          <>
            <DrillBack ref={pages.backRef} title="Incognito" onBack={pages.back} />
            <DrillChoices label="Incognito" value={privacy} options={PRIVACY} onPick={pick(setPrivacy)} />
          </>
        )}
        {pages.page === 'automation' && (
          <>
            <DrillBack ref={pages.backRef} title="Automation" onBack={pages.back} />
            <DrillField icon={IconEye} title="Monitor" help={MONITOR_HELP[monitor]}>
              <SegmentedControl aria-label="Monitor" fullWidth size="xs" value={monitor} onChange={setMonitor} data={['All', 'Smart', 'None']} />
            </DrillField>
          </>
        )}
      </DrillPanes>
    </Paper>
  )
}

/** The grammar the reader settings, the series menu and the Discover add card share. */
const meta = {
  component: DrillRow,
  tags: ['ai-generated'],
  render: () => <Demo />,
  args: { icon: IconFolder, label: 'Folder', onClick: () => {} },
  parameters: { layout: 'padded' },
} satisfies Meta<typeof DrillRow>

export default meta
type Story = StoryObj<typeof meta>

export const Panel: Story = {}

/** A row opens its page, a pick goes back, and focus returns to the row that opened it. */
export const PickAndReturn: Story = {
  play: async ({ canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: /^Incognito/ }))
    await userEvent.click(await canvas.findByRole('radio', { name: /^Full/ }))
    const row = await canvas.findByRole('button', { name: /^Incognito.*Full/ })
    await waitFor(() => expect(row).toHaveFocus())
  },
}
