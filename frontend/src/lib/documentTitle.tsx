import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import { msg } from '@lingui/core/macro'
import { useLocation } from 'react-router-dom'
import { useInboxUnread } from '../api/inbox'
import { useLabel } from '../i18n-context'
import { pageTitle } from '../nav'

const APP_NAME = 'Maki'

const DetailValue = createContext<string | null>(null)
const DetailSetter = createContext<((detail: string | null) => void) | null>(null)

export function DocumentTitleProvider({ children }: { children: ReactNode }) {
  const [detail, setDetail] = useState<string | null>(null)
  return (
    <DetailSetter value={setDetail}>
      <DetailValue value={detail}>{children}</DetailValue>
    </DetailSetter>
  )
}

/**
 * Names the browser tab after the data on the page (a series title, a creator) instead of the
 * route's generic name. Null leaves the route name in place; unmounting clears it.
 */
export function useDocumentTitle(detail: string | null | undefined) {
  const setDetail = useContext(DetailSetter)
  const value = detail || null
  useEffect(() => {
    setDetail?.(value)
    return () => setDetail?.(null)
  }, [setDetail, value])
}

function routeName(pathname: string) {
  if (pathname.startsWith('/read/')) return msg`Reader`
  if (pathname.startsWith('/creator/')) return msg`Creator`
  return pageTitle(pathname)
}

/**
 * Writes `document.title`: the page's own name, then the app, behind the unread inbox count when
 * there is one. Reads the same query the bell does, which the inbox push keeps current, so it adds
 * no request of its own.
 */
export function DocumentTitle() {
  const { pathname } = useLocation()
  const label = useLabel()
  const detail = useContext(DetailValue)
  const { data: unread } = useInboxUnread()

  const route = routeName(pathname)
  const name = detail ?? (route ? label(route) : null)
  const count = unread?.count ?? 0
  const badge = count > 0 ? `(${count > 99 ? '99+' : count}) ` : ''
  const title = `${badge}${name ? `${name} | ${APP_NAME}` : APP_NAME}`

  useEffect(() => {
    document.title = title
  }, [title])

  useEffect(
    () => () => {
      document.title = APP_NAME
    },
    [],
  )

  return null
}
