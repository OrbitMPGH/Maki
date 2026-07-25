import { useEffect, useRef } from 'react'
import { flushProgress, saveProgress } from '../../api/reader'

const DEBOUNCE_MS = 1500

/**
 * Reports the reader's position, debounced, and flushes on tab hide / unmount so closing the
 * browser mid-chapter still resumes in the right place. Writes are absolute page indices and
 * failures are swallowed: losing a position must never interrupt reading.
 */
export function useReaderProgress(chapterId: number | undefined, page: number, enabled: boolean) {
  const latest = useRef({ chapterId, page })
  const pending = useRef(false)

  latest.current = { chapterId, page }

  useEffect(() => {
    if (!enabled || !chapterId) return

    pending.current = true
    const timer = setTimeout(() => {
      pending.current = false
      void saveProgress(chapterId, page).catch(() => {})
    }, DEBOUNCE_MS)

    return () => clearTimeout(timer)
  }, [chapterId, page, enabled])

  useEffect(() => {
    if (!enabled) return

    const flush = () => {
      const { chapterId: id, page: at } = latest.current
      if (pending.current && id) {
        pending.current = false
        void flushProgress(id, at).catch(() => {})
      }
    }

    const onVisibility = () => {
      if (document.visibilityState === 'hidden') flush()
    }

    document.addEventListener('visibilitychange', onVisibility)
    window.addEventListener('pagehide', flush)
    return () => {
      document.removeEventListener('visibilitychange', onVisibility)
      window.removeEventListener('pagehide', flush)
      flush()
    }
  }, [enabled])
}
