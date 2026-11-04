import { useMediaQuery } from '@mantine/hooks'

/** Mantine's `xs` breakpoint, the width under which the reader drops its page counter. */
export const PHONE_QUERY = '(max-width: 36em)'

/** Whether the notes panel is a bottom sheet that covers the page, rather than a panel beside it. */
export function usePhoneSheet(): boolean {
  return useMediaQuery(PHONE_QUERY, false, { getInitialValueInEffect: false }) ?? false
}
