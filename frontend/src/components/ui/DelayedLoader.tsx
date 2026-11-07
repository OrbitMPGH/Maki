import { Center, Loader } from '@mantine/core'

/**
 * The centred loader for a chunk or setting in flight. It waits out a fast load before it shows and
 * then fades in, so a warm cache never flashes a spinner ahead of the page's own skeleton.
 */
export function DelayedLoader({ py = 80 }: { py?: number }) {
  return (
    <Center py={py} className="delayed-fallback">
      <Loader />
    </Center>
  )
}
