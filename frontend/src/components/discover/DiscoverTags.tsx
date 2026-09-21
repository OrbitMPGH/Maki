import { Paper, Title } from '@mantine/core'
import { Trans } from '@lingui/react/macro'
import type { MangaBakaTag } from '../../api/hooks'
import { TagBuckets } from '../TagBuckets'

/** The Discover detail card's Tags panel: a titled card around the shared bucket list. */
export function DiscoverTags({ tags }: { tags: MangaBakaTag[] }) {
  return (
    <Paper withBorder radius="lg" p="lg">
      <Title order={3} fz={17}>
        <Trans>Tags</Trans>
      </Title>
      <TagBuckets tags={tags} />
    </Paper>
  )
}
