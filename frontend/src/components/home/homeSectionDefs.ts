import { msg } from '@lingui/core/macro'
import {
  IconBell,
  IconBook,
  IconBookmarks,
  IconCalendarStats,
  IconDeviceTv,
  IconDownload,
  IconFlame,
  IconPlayerPlay,
  IconSparkles,
} from '@tabler/icons-react'
import {
  HOME_HERO_DEFAULTS,
  HOME_SECTIONS,
  HOME_SECTION_LABELS,
} from '../../api/hooks'
import type { LayoutConfig, SectionRegistry } from '../layout/pageLayout'

/** Home's rules for the layout editor. Mirrors `HomeLayoutSpec.Definition` on the server. */
export const HOME_LAYOUT_CONFIG: LayoutConfig = {
  canonical: HOME_SECTIONS,
  heroDefaults: HOME_HERO_DEFAULTS,
  panels: {},
  railAnchor: null,
}

export const HOME_SECTION_DEFS: SectionRegistry = {
  glance: {
    icon: IconCalendarStats,
    label: HOME_SECTION_LABELS.glance,
    description: msg`Your last five weeks of reading: the days you read, your streak, chapters and time read.`,
  },
  downloading: {
    icon: IconDownload,
    label: HOME_SECTION_LABELS.downloading,
    description: msg`Chapters downloading right now. Only shows while something is.`,
  },
  continue: {
    icon: IconPlayerPlay,
    label: HOME_SECTION_LABELS.continue,
    description: msg`Series with a chapter part-way through.`,
    hero: true,
  },
  jumpback: {
    icon: IconBook,
    label: HOME_SECTION_LABELS.jumpback,
    description: msg`Series you finished a chapter of that still have more to read.`,
    hero: true,
  },
  fromanime: {
    icon: IconDeviceTv,
    label: HOME_SECTION_LABELS.fromanime,
    description: msg`Series whose anime you finished, starting after the last adapted chapter.`,
  },
  recent: {
    icon: IconBookmarks,
    label: HOME_SECTION_LABELS.recent,
    description: msg`Series with newly added chapters.`,
  },
  following: {
    icon: IconBell,
    label: HOME_SECTION_LABELS.following,
    description: msg`The newest titles from the creators and studios you follow. Empty until you follow someone.`,
  },
  recommended: {
    icon: IconSparkles,
    label: HOME_SECTION_LABELS.recommended,
    description: msg`Picks from the recommender, based on your whole library.`,
  },
  popular: {
    icon: IconFlame,
    label: HOME_SECTION_LABELS.popular,
    description: msg`The most popular titles in the MangaBaka catalogue.`,
  },
}
