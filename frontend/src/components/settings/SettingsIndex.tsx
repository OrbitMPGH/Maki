import { useEffect, useState } from 'react'
import { UnstyledButton } from '@mantine/core'
import { useLingui } from '@lingui/react/macro'
import { useLabel } from '../../i18n-context'
import type { SettingsEntry } from '../../pages/settings/registry'

/**
 * The cards on one settings tab, as a sticky list beside them. Highlights whichever card is in the
 * upper part of the viewport. Hidden below the width where it fits beside the content.
 */
export function SettingsIndex({ entries }: { entries: SettingsEntry[] }) {
  const { t } = useLingui()
  const renderLabel = useLabel()
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
        if (first) setActive(first.id)
      },
      // A band across the upper part of the screen: the card crossing it is the one being read.
      { rootMargin: '-80px 0px -55% 0px' },
    )
    elements.forEach((el) => observer.observe(el))
    return () => observer.disconnect()
  }, [entries])

  if (entries.length < 2) return null

  return (
    <nav className="settings-index" aria-label={t`Sections on this tab`}>
      {entries.map((e) => (
        <UnstyledButton
          key={e.id}
          className="settings-index-link"
          data-active={active === e.id || undefined}
          onClick={() => {
            document.getElementById(`setting-${e.id}`)?.scrollIntoView({ block: 'start', behavior: 'smooth' })
            setActive(e.id)
          }}
        >
          {renderLabel(e.title)}
        </UnstyledButton>
      ))}
    </nav>
  )
}
