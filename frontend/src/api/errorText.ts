/**
 * The text of a failure for display. `String(error)` renders an `Error` as "ApiError: ...", and
 * the server's message is already localized, so show just that.
 */
export function errorText(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}
