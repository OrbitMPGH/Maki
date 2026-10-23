import { msg } from '@lingui/core/macro'
import {
  IconBell,
  IconBook,
  IconBookmarks,
  IconDeviceTv,
  IconFlame,
  IconPlayerPlay,
  IconSparkles,
} from '@tabler/icons-react'
import {
  HOME_HERO_DEFAULTS,
  HOME_SECTIONS,
  HOME_SECTION_LABELS,
  HOME_SECTION_OPTIONS,
} from '../../api/hooks'
import type { LayoutConfig, SectionOptionDef, SectionRegistry } from '../layout/pageLayout'

/** Home's rules for the layout editor. Mirrors `HomeLayoutSpec.Definition` on the server. */
export const HOME_LAYOUT_CONFIG: LayoutConfig = {
  canonical: HOME_SECTIONS,
  heroDefaults: HOME_HERO_DEFAULTS,
  panels: {},
  options: HOME_SECTION_OPTIONS,
  railAnchor: null,
}

/** Next up as a list beside the hero. Shared by both hero sections. */
const nextUpBeside: SectionOptionDef = {
  key: 'nextUpBeside',
  label: msg`Next up beside the hero`,
  description: msg`The rest of the section as a list beside the hero, instead of a tray of posters under it.`,
  hero: true,
}

export const HOME_SECTION_DEFS: SectionRegistry = {
  continue: {
    icon: IconPlayerPlay,
    label: HOME_SECTION_LABELS.continue,
    description: msg`Series with a chapter part-way through.`,
    hero: true,
    options: [
      {
        key: 'glance',
        label: msg`Your last five weeks`,
        description: msg`The days you read, your streak, chapters and time read, as a card in the hero.`,
      },
      nextUpBeside,
    ],
  },
  jumpback: {
    icon: IconBook,
    label: HOME_SECTION_LABELS.jumpback,
    description: msg`Series you finished a chapter of that still have more to read.`,
    hero: true,
    options: [nextUpBeside],
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
