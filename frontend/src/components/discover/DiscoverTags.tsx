import { Paper, Title } from '@mantine/core'
import type { MangaBakaTag } from '../../api/hooks'
import { TagBuckets } from '../TagBuckets'

/** The Discover detail card's Tags panel: a titled card around the shared bucket list. */
export function DiscoverTags({ tags }: { tags: MangaBakaTag[] }) {
  return (
    <Paper withBorder radius="lg" p="lg">
      <Title order={3} fz={17}>
        Tags
      </Title>
      <TagBuckets tags={tags} />
    </Paper>
  )
}
