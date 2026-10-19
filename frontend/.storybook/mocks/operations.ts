import type { HttpHandler } from 'msw'
import { healthHandlers } from './operations/health'
import { importHandlers } from './operations/import'
import { queueHandlers } from './operations/queue'
import { requestsHandlers } from './operations/requests'

export const operationsHandlers: HttpHandler[] = [
  ...queueHandlers,
  ...requestsHandlers,
  ...importHandlers,
  ...healthHandlers,
]
