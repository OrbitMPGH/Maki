import { Trans } from '@lingui/react/macro'
import type { MangaBakaTag } from '../../api/hooks'
import { IconTag } from '../../lib/icons'
import { TagBuckets } from '../TagBuckets'
import { Panel } from '../ui/Panel'
import { PanelTitle } from '../ui/PanelTitle'

/** The Discover detail card's Tags panel: a titled card around the shared bucket list. */
export function DiscoverTags({ tags }: { tags: MangaBakaTag[] }) {
  return (
    <Panel>
      <PanelTitle order={2} icon={IconTag}>
        <Trans>Tags</Trans>
      </PanelTitle>
      <TagBuckets tags={tags} />
    </Panel>
  )
}
