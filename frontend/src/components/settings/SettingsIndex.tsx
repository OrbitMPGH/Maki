import { useEffect, useState } from 'react'
import { Menu, UnstyledButton } from '@mantine/core'
import { IconCheck, IconChevronDown, IconListDetails } from '@tabler/icons-react'
import { useLingui } from '@lingui/react/macro'
import { useLabel } from '../../i18n-context'
import type { SettingsEntry } from '../../pages/settings/registry'
import { scrollBehavior } from '../../lib/scrollBehavior'

/**
 * Whichever card crosses a band of the viewport from `top` px down, `bottom` short of the end. With
 * none crossing it (a gap between cards, or the page above the first), the last card to start above
 * the band's top, or else the first card.
 */
function useActiveEntry(entries: SettingsEntry[], top: number, bottom: () => string) {
  const [active, setActive] = useState(entries[0]?.id)

  useEffect(() => {
    const elements = entries
      .map((e) => document.getElementById(`setting-${e.id}`))
      .filter((el): el is HTMLElement => el !== null)
    const visible = new Set<string>()
    const observer = new IntersectionObserver(
      (records) => {
        for (const r of records) {
          const id = r.target.id.slice('setting-'.length)
          if (r.isIntersecting) visible.add(id)
          else visible.delete(id)
        }
        const first = entries.find((e) => visible.has(e.id))
        if (first) return setActive(first.id)
        const above = elements.filter((el) => el.getBoundingClientRect().top < top).at(-1)
        setActive(above ? above.id.slice('setting-'.length) : entries[0]?.id)
      },
      { rootMargin: `-${top}px 0px ${bottom()} 0px` },
    )
    elements.forEach((el) => observer.observe(el))
    return () => observer.disconnect()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [entries])

  return [active, setActive] as const
}

function jumpTo(entry: SettingsEntry) {
  document.getElementById(`setting-${entry.id}`)?.scrollIntoView({ block: 'start', behavior: scrollBehavior() })
}

/**
 * The cards on one settings tab, as a sticky list beside them. Highlights whichever card is in the
 * upper part of the viewport. Hidden below the width where it fits beside the content, where
 * `SettingsJump` takes over.
 */
export function SettingsIndex({ entries }: { entries: SettingsEntry[] }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  // A band across the upper part of the screen: the card crossing it is the one being read.
  const [active, setActive] = useActiveEntry(entries, 80, () => '-55%')

  if (entries.length < 2) return null

  return (
    <nav className="settings-index" aria-label={t`Sections on this tab`}>
      {entries.map((e) => (
        <UnstyledButton
          key={e.id}
          className="settings-index-link"
          data-active={active === e.id || undefined}
          onClick={() => {
            jumpTo(e)
            setActive(e.id)
          }}
        >
          {renderLabel(e.title)}
        </UnstyledButton>
      ))}
    </nav>
  )
}

/**
 * The same index for a screen with no room beside the cards: a bar pinned under the header that
 * names the card being read and opens the list. Shown only where `SettingsIndex` is hidden.
 */
export function SettingsJump({ entries }: { entries: SettingsEntry[] }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  // A thin line just under the header and this bar: a tall band named the card above the one being
  // read for as long as its last few lines were still on screen.
  const [active, setActive] = useActiveEntry(entries, 130, () => `-${Math.max(0, window.innerHeight - 150)}px`)

  if (entries.length < 2) return null
  const current = entries.find((e) => e.id === active) ?? entries[0]

  return (
    <nav className="settings-jump" aria-label={t`Sections on this tab`}>
      <Menu position="bottom-start" width="target" withinPortal>
        <Menu.Target>
          <UnstyledButton className="settings-jump-button">
            <IconListDetails size={16} className="settings-jump-icon" aria-hidden />
            <span className="settings-jump-current">{renderLabel(current.title)}</span>
            <IconChevronDown size={16} className="settings-jump-chevron" aria-hidden />
          </UnstyledButton>
        </Menu.Target>
        <Menu.Dropdown>
          {entries.map((e) => (
            <Menu.Item
              key={e.id}
              aria-current={e.id === current.id ? 'location' : undefined}
              rightSection={e.id === current.id ? <IconCheck size={14} aria-hidden /> : undefined}
              onClick={() => {
                jumpTo(e)
                setActive(e.id)
              }}
            >
              {renderLabel(e.title)}
            </Menu.Item>
          ))}
        </Menu.Dropdown>
      </Menu>
    </nav>
  )
}
