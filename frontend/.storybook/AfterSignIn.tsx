import { useEffect, useState, type ReactNode } from 'react'
import { useAuth } from '../src/auth/AuthProvider'

/**
 * The app mounts its pages behind AuthGate, so they always render after the signed-in user is known.
 * A story mounts at once, which matters twice: a settings deep link is consumed while `me` is still
 * loading, and AuthProvider drops every cached health query when `me` first resolves, orphaning a
 * health fetch that started before it. This holds the story back until one tick past sign-in.
 */
export function AfterSignIn({ children }: { children: ReactNode }) {
  const { me } = useAuth()
  const [ready, setReady] = useState(false)
  useEffect(() => {
    if (me) setReady(true)
  }, [me])
  return ready ? children : null
}
