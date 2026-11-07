import { Component, type ReactNode } from 'react'
import { useLingui } from '@lingui/react/macro'
import { EmptyState } from './ui/EmptyState'

/**
 * Catches a render error in one section of a page (a Home rail) so a malformed payload takes down
 * that section alone, with a small inline error in its place, rather than the whole page. Retry
 * remounts the section, which refetches anything it owns.
 */
export class SectionErrorBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false }

  static getDerivedStateFromError() {
    return { failed: true }
  }

  componentDidCatch(error: unknown) {
    console.error('Section render failed', error)
  }

  render() {
    return this.state.failed ? (
      <SectionErrorNotice onRetry={() => this.setState({ failed: false })} />
    ) : (
      this.props.children
    )
  }
}

function SectionErrorNotice({ onRetry }: { onRetry: () => void }) {
  const { t } = useLingui()
  return <EmptyState compact title={t`Couldn't load this section`} actionLabel={t`Retry`} onAction={onRetry} />
}
