import { Component, type ReactNode } from 'react'
import { useLocation } from 'react-router-dom'
import { useLingui } from '@lingui/react/macro'
import { IconArrowLeft, IconRefresh } from '@tabler/icons-react'
import { EmptyState } from './ui/EmptyState'

/**
 * Catches a render error in one route so the shell, nav and the rest of the app stay up. Without
 * it React unmounts the whole root on any throw, and the reader saw a blank page with nothing to
 * click: one bad field on one series page took the app down with it. The error state clears when
 * the path changes after a failure, so navigating somewhere else leaves the broken page behind
 * without a reload. Children are not remounted on ordinary path changes, which the reader relies
 * on when it swaps chapters in place.
 */
export function RouteErrorBoundary({ children }: { children: ReactNode }) {
  const { pathname } = useLocation()
  return (
    <Catcher pathname={pathname} fallback={<RouteErrorFallback />}>
      {children}
    </Catcher>
  )
}

function RouteErrorFallback() {
  const { t } = useLingui()
  return (
    <EmptyState
      art="missing"
      headingOrder={1}
      title={t`This page hit an error`}
      description={t`Something went wrong while drawing it. Reloading usually clears it; if it keeps happening, the log has the details.`}
      actionLabel={t`Reload`}
      onAction={() => window.location.reload()}
      actionIcon={<IconRefresh size={16} />}
      secondaryActionLabel={t`Go to start page`}
      secondaryActionTo="/"
      secondaryActionIcon={<IconArrowLeft size={16} />}
    />
  )
}

interface CatcherProps {
  children: ReactNode
  fallback: ReactNode
  pathname: string
}

interface CatcherState {
  failed: boolean
  failedAt: string | null
}

class Catcher extends Component<CatcherProps, CatcherState> {
  state: CatcherState = { failed: false, failedAt: null }

  static getDerivedStateFromError() {
    return { failed: true }
  }

  static getDerivedStateFromProps(props: CatcherProps, state: CatcherState) {
    if (!state.failed) return null
    if (state.failedAt === null) return { failedAt: props.pathname }
    if (state.failedAt !== props.pathname) return { failed: false, failedAt: null }
    return null
  }

  componentDidCatch(error: unknown) {
    console.error('Route render failed', error)
  }

  render() {
    return this.state.failed ? this.props.fallback : this.props.children
  }
}
