import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'

/**
 * What the top bar says. Nothing by default: every page already names itself in its own header,
 * and the bar repeating it read as a stutter. A page sets a title only when it adds something, like
 * the series name once the series page's own heading has scrolled away.
 *
 * Two contexts so that setting the title does not re-render the page that set it.
 */
const TitleValue = createContext<string | null>(null)
const TitleSetter = createContext<((title: string | null) => void) | null>(null)

export function ShellTitleProvider({ children }: { children: ReactNode }) {
  const [title, setTitle] = useState<string | null>(null)
  return (
    <TitleSetter value={setTitle}>
      <TitleValue value={title}>{children}</TitleValue>
    </TitleSetter>
  )
}

export function useShellTitleValue(): string | null {
  return useContext(TitleValue)
}

/** Shows `title` in the top bar while the calling component is mounted; null clears it. */
export function useShellTitle(title: string | null) {
  const setTitle = useContext(TitleSetter)
  useEffect(() => {
    setTitle?.(title)
    return () => setTitle?.(null)
  }, [setTitle, title])
}
