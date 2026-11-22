import type { ComponentProps } from 'react'
import type { Meta, StoryObj } from '@storybook/react-vite'
import { expect, fn, waitFor, within } from 'storybook/test'
import { useUiSettings, type PageSection } from '../../api/hooks'
import type { CustomRail } from '../../api/customRails'
import { cover } from '../../../.storybook/mocks/api'
import { customRails, homePage, homeUi } from '../../../.storybook/mocks/home'
import { HOME_LAYOUT_CONFIG, HOME_SECTION_DEFS } from '../home/homeSectionDefs'
import { LayoutEditLoading, LayoutEditSurface } from './LayoutEditSurface'
import { useLayoutDraft } from './useLayoutDraft'

const layout = homeUi({ rails: [1, 2, 3], off: ['following'] }).homeLayout.sections

type HarnessProps = Omit<ComponentProps<typeof LayoutEditSurface>, 'draft' | 'render' | 'editing' | 'sections'> & {
  initial: PageSection[]
}

/** The page's side of editing: it holds the draft and draws each section, here as a stand-in. */
function Harness({ initial, rails, ...props }: HarnessProps) {
  const { data } = useUiSettings()
  const draft = useLayoutDraft({
    editing: true,
    saved: initial,
    config: HOME_LAYOUT_CONFIG,
    railIds: rails.map((r: CustomRail) => r.id),
  })
  // Saving reads the settings query out of the cache, which the page fills before editing starts.
  if (!data || !draft) return <></>
  return (
    <LayoutEditSurface
      {...props}
      rails={rails}
      editing
      draft={draft}
      sections={initial}
      render={(key) =>
        key === 'popular' ? null : (
          <div style={{ display: 'flex', gap: 10 }}>
            {[1, 5, 9, 3, 10, 2].map((id) => (
              <img key={id} src={cover(id)} alt="" width={84} height={126} style={{ borderRadius: 7 }} />
            ))}
            <button type="button">Show more</button>
          </div>
        )
      }
    />
  )
}

const meta = {
  component: Harness,
  tags: ['ai-generated'],
  args: {
    page: 'home',
    placement: 'home',
    registry: HOME_SECTION_DEFS,
    initial: layout,
    rails: customRails,
    railLimit: 20,
    onExit: fn(),
  },
  parameters: { layout: 'padded', msw: homePage() },
} satisfies Meta<typeof Harness>

export default meta
type Story = StoryObj<typeof meta>

const panel = (canvasElement: HTMLElement, name: string) =>
  within(canvasElement.ownerDocument.body).findByRole('dialog', { name })

export const Default: Story = {
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('Editing layout')).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Move Recently added' })).toBeVisible()
    await expect(await canvas.findByRole('region', { name: 'Unread favourites' })).toBeVisible()
    await expect(await canvas.findByRole('button', { name: 'Show' })).toBeVisible()
    // A section with nothing to show says so through its frame's empty body.
    const popular = await canvas.findByRole('region', {
      name: 'Currently popular',
    })
    await expect(popular.querySelector('.layout-frame-inner')).toHaveAttribute(
      'data-empty',
      'Nothing to show right now',
    )
  },
}

export const SectionsShrinkAndCut: Story = {
  play: async ({ canvas }) => {
    const frame = await canvas.findByRole('region', { name: 'Recently added' })
    const frames = frame.closest('.layout-frames') as HTMLElement
    // The shrink runs on arrival and settles at the edit scale.
    await waitFor(() => expect(frames.style.getPropertyValue('--sc')).toBe('0.85'))
    const body = frame.querySelector<HTMLElement>('.layout-frame-body')
    await waitFor(() => expect(Number.parseFloat(body?.style.height ?? '0')).toBeLessThanOrEqual(220))
  },
}

export const ContentIsInert: Story = {
  play: async ({ canvas }) => {
    const frame = await canvas.findByRole('region', { name: 'Recently added' })
    const body = frame.querySelector('.layout-frame-body')
    await expect(body).toHaveAttribute('inert')
    await expect(body?.querySelector('button')?.closest('[inert]')).toBe(body)
  },
}

export const ClickOpensSettings: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('region', { name: 'Continue reading' }))
    const dialog = await panel(canvasElement, 'Continue reading')
    await waitFor(() => expect(within(dialog).getByRole('switch', { name: 'Show this section' })).toBeChecked())
    await expect(within(dialog).getByRole('radio', { name: 'Under the hero' })).toBeChecked()
  },
}

export const SettingsButton: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(
      await canvas.findByRole('button', {
        name: 'Settings for Recently added',
      }),
    )
    const dialog = await panel(canvasElement, 'Recently added')
    await userEvent.click(within(dialog).getByRole('button', { name: /Move/ }))
    await userEvent.click(await within(dialog).findByRole('button', { name: 'Top' }))
    await waitFor(() => expect(canvas.getByText('Recently added moved to position 1 of 9')).toBeInTheDocument())
  },
}

export const KeyboardReorder: Story = {
  play: async ({ canvas, userEvent }) => {
    const grip = await canvas.findByRole('button', {
      name: 'Move Recently added',
    })
    grip.focus()
    await userEvent.keyboard('{ArrowDown}')
    await waitFor(() => expect(canvas.getByText('Recently added moved to position 3 of 9')).toBeInTheDocument())
  },
}

export const HideThenDone: Story = {
  play: async ({ args, canvas, canvasElement, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Hide Currently popular' }))
    await expect(await canvas.findByText('1 change')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Done' }))
    await waitFor(() => expect(args.onExit).toHaveBeenCalledOnce())
    await expect(await within(canvasElement.ownerDocument.body).findByText('Layout saved')).toBeVisible()
  },
}

export const DoneWithoutChanges: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Done' }))
    await expect(args.onExit).toHaveBeenCalledOnce()
  },
}

export const Cancel: Story = {
  play: async ({ args, canvas, userEvent }) => {
    await userEvent.click(await canvas.findByRole('button', { name: 'Cancel' }))
    await expect(args.onExit).toHaveBeenCalledOnce()
  },
}

export const AddRailOpensEditor: Story = {
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click((await canvas.findAllByRole('button', { name: 'Add a rail' }))[0])
    await waitFor(async () =>
      expect(
        await within(canvasElement.ownerDocument.body).findByRole('dialog', {
          name: 'New rail',
        }),
      ).toBeVisible(),
    )
  },
}

export const UnavailableSections: Story = {
  args: {
    unavailable: (key: string) =>
      key === 'recommended' || key === 'popular'
        ? {
            id: 'story.unavailable',
            message: 'Needs the local MangaBaka database',
          }
        : null,
  },
  play: async ({ canvas }) => {
    await waitFor(() => expect(canvas.getAllByText('Needs the local MangaBaka database')).toHaveLength(2))
  },
}

export const WithSummariesAndPreviews: Story = {
  args: {
    summary: (key: string) => (key === 'recent' ? '7 series with new chapters' : null),
    preview: (key: string) => (key === 'following' ? [1, 5, 9, 3, 10].map((id) => cover(id)) : []),
  },
  play: async ({ canvas }) => {
    await expect(await canvas.findByText('7 series with new chapters')).toBeVisible()
  },
}

export const Phone: Story = {
  parameters: { viewport: { defaultViewport: 'mobile1' } },
  globals: { viewport: { value: 'mobile1', isRotated: false } },
  play: async ({ canvas, canvasElement, userEvent }) => {
    await userEvent.click(
      await canvas.findByRole('button', {
        name: 'Settings for Continue reading',
      }),
    )
    const sheet = await panel(canvasElement, 'Continue reading')
    await expect(await within(sheet).findByRole('button', { name: 'Move down' })).toBeVisible()
  },
}

export const WithoutRails: Story = {
  args: { initial: homeUi().homeLayout.sections, rails: [] },
}

export const Loading: Story = {
  render: () => <LayoutEditLoading onExit={fn()} />,
}

export const LoadFailed: Story = {
  render: (args) => <LayoutEditLoading failed onExit={args.onExit} />,
  play: async ({ args, canvas, userEvent }) => {
    await expect(await canvas.findByText('The layout could not be loaded.')).toBeVisible()
    await userEvent.click(await canvas.findByRole('button', { name: 'Exit editing' }))
    await expect(args.onExit).toHaveBeenCalledOnce()
  },
}
