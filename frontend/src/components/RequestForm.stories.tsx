import { useState } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn } from 'storybook/test'
import { RequestForm } from './RequestForm'

const meta = {
  component: RequestForm,
  tags: ['ai-generated'],
  args: {
    chapterStart: '',
    chapterEnd: '',
    note: '',
    pending: false,
    onChapterStart: fn(),
    onChapterEnd: fn(),
    onNote: fn(),
    onSubmit: fn(),
  },
  render: function Render(args) {
    const [start, setStart] = useState(args.chapterStart)
    const [end, setEnd] = useState(args.chapterEnd)
    const [note, setNote] = useState(args.note)
    return (
      <div style={{ maxWidth: 420 }}>
        <RequestForm
          {...args}
          chapterStart={start}
          chapterEnd={end}
          note={note}
          onChapterStart={(v) => {
            setStart(v)
            args.onChapterStart(v)
          }}
          onChapterEnd={(v) => {
            setEnd(v)
            args.onChapterEnd(v)
          }}
          onNote={(v) => {
            setNote(v)
            args.onNote(v)
          }}
        />
      </div>
    )
  },
} satisfies Meta<typeof RequestForm>

export default meta
type Story = StoryObj<typeof meta>

export const Default: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.type(canvas.getByLabelText('From'), '12')
    await userEvent.type(canvas.getByLabelText('To'), '40')
    await userEvent.type(canvas.getByLabelText('Note (optional)'), 'The scanlation group is the one I follow')
    await userEvent.click(canvas.getByRole('button', { name: 'Request series' }))
    await expect(args.onSubmit).toHaveBeenCalledOnce()
    await expect(args.onChapterStart).toHaveBeenLastCalledWith(12)
  },
}

export const Prefilled: Story = {
  args: { chapterStart: 5, chapterEnd: 5, note: 'Just this one chapter, please.', label: 'Request chapter' },
}

export const Dense: Story = {
  args: { dense: true },
  render: (args) => (
    <div style={{ width: 215 }}>
      <RequestForm {...args} />
    </div>
  ),
}

export const Pending: Story = {
  args: { pending: true, chapterStart: 1, chapterEnd: 10 },
}
