import type { HttpHandler } from 'msw'

interface MswContext {
  msw: { use: (...handlers: HttpHandler[]) => void }
}

/**
 * A `beforeEach` that layers fresh handlers over the story's own. The factory runs once per story
 * run, so handlers that keep state (an inbox, the wizard's settings) never carry it into the next
 * run. Handlers added this way win over `parameters.msw`, which the loader applies first.
 */
export function layerHandlers(factory: () => HttpHandler[]) {
  return (context: unknown) => {
    ;(context as MswContext).msw.use(...factory())
  }
}
