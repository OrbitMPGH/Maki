import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import type { UpgradeScanResultDto } from '../../api/upgrades'
import { seriesMsw } from '../../../.storybook/mocks/series'
import { UpgradeNowResultModal } from './UpgradeNowResultModal'

const won: UpgradeScanResultDto = {
  seriesScanned: 1,
  chaptersChecked: 1,
  candidatesProbed: 3,
  enqueued: 1,
  skipped: {},
  queuedFromMappingId: 11,
  candidates: [
    { mappingId: 11, sourceName: 'mangadex', sourceChapterId: 'md-1', reason: 'enqueued', probed: true, pageCount: 26, medianWidth: 1600, score: 94 },
    { mappingId: 12, sourceName: 'comick', sourceChapterId: 'ck-1', reason: 'score_not_higher', probed: true, pageCount: 24, medianWidth: 1100, score: 58 },
    { mappingId: 13, sourceName: 'weebcentral', sourceChapterId: 'wc-1', reason: 'source_cooldown', probed: false, pageCount: null, medianWidth: null, score: null },
  ],
}

const meta = {
  component: UpgradeNowResultModal,
  tags: ['ai-generated'],
  args: { opened: true, onClose: fn(), result: won },
  parameters: { msw: seriesMsw() },
} satisfies Meta<typeof UpgradeNowResultModal>

export default meta
type Story = StoryObj<typeof meta>

export const Queued: Story = {
  play: async ({ canvasElement }) => {
    const dialog = await within(canvasElement.ownerDocument.body).findByRole('dialog')
    await waitFor(() => expect(dialog).toHaveTextContent(/Queued upgrade from/))
    await waitFor(() => expect(dialog).toHaveTextContent('1600px'))
  },
}

export const NothingBetter: Story = {
  args: {
    result: {
      ...won,
      enqueued: 0,
      queuedFromMappingId: null,
      candidates: won.candidates.slice(1).map((c) => ({ ...c, reason: 'score_not_higher' })),
    },
  },
  play: async ({ canvasElement }) => {
    await expect(await within(canvasElement.ownerDocument.body).findByText('No better copy found')).toBeVisible()
  },
}

export const ChapterLevelSkip: Story = {
  args: { result: { ...won, enqueued: 0, queuedFromMappingId: null, candidates: [], skipped: { trusted: 1 } } },
}

export const NoSources: Story = {
  args: { result: { ...won, enqueued: 0, queuedFromMappingId: null, candidates: [], skipped: {} } },
}
