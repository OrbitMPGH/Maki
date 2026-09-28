import { msg } from '@lingui/core/macro'
import {
  IconCompass,
  IconFlame,
  IconHeartFilled,
  IconLayoutGrid,
  IconLibrary,
  IconStar,
  IconUsers,
  IconWand,
} from '@tabler/icons-react'
import { DISCOVER_SECTIONS, DISCOVER_SECTION_LABELS } from '../../api/hooks'
import type { LayoutConfig, SectionRegistry } from '../layout/pageLayout'

/** Discover's rules for the layout editor. Mirrors `DiscoverLayoutSpec.Definition` on the server. */
export const DISCOVER_LAYOUT_CONFIG: LayoutConfig = {
  canonical: DISCOVER_SECTIONS,
  heroDefaults: {},
  panels: {},
  railAnchor: 'trending',
}

export const DISCOVER_SECTION_DEFS: SectionRegistry = {
  hero: {
    icon: IconStar,
    label: DISCOVER_SECTION_LABELS.hero,
    description: msg`A few large picks at the top, from your recent reading or what is trending.`,
  },
  taste: {
    icon: IconHeartFilled,
    label: DISCOVER_SECTION_LABELS.taste,
    description: msg`A short strip of what your library leans towards.`,
  },
  recent: {
    icon: IconLibrary,
    label: DISCOVER_SECTION_LABELS.recent,
    description: msg`Picks based on the series you read most recently.`,
  },
  sideinterests: {
    icon: IconWand,
    label: DISCOVER_SECTION_LABELS.sideinterests,
    description: msg`A row for each smaller theme on your shelf.`,
  },
  cohort: {
    icon: IconUsers,
    label: DISCOVER_SECTION_LABELS.cohort,
    description: msg`What readers who finished the same series as you went on to finish.`,
  },
  trending: {
    icon: IconFlame,
    label: DISCOVER_SECTION_LABELS.trending,
    description: msg`The titles climbing fastest in popularity right now.`,
  },
  catalogue: {
    icon: IconCompass,
    label: DISCOVER_SECTION_LABELS.catalogue,
    description: msg`Popular, new and top rated titles, with the catalogue filter.`,
  },
  genres: {
    icon: IconLayoutGrid,
    label: DISCOVER_SECTION_LABELS.genres,
    description: msg`One tile per genre, each opening its most popular titles.`,
  },
}
