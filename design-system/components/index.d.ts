// Prop types of Maki's shared UI primitives (frontend/src/components/ui), as documentation.
// Button and Badge are Mantine components with theme defaults and are not re-declared here.
import type { ReactNode, CSSProperties, MouseEventHandler, ComponentPropsWithRef } from 'react'
import type { PaperProps } from '@mantine/core'
import type { Icon } from '@tabler/icons-react'

/** Page title row: title and description left, actions right. */
export declare function PageHeader(props: {
  title: ReactNode
  description?: ReactNode
  actions?: ReactNode
  /** Smaller title for operational pages. */
  compact?: boolean
}): JSX.Element

export type PanelEdge = 'brand' | 'info' | 'ok' | 'warn' | 'danger' | 'strong'

/** The house panel: bordered Paper, radius lg, padding lg, no shadow. */
export interface PanelProps extends PaperProps {
  /** Draws the 2px accent rule. `strong` is the neutral border-strong variant. */
  edge?: PanelEdge
  /** Left drops the right border, top keeps all four. Default 'top'. */
  edgeSide?: 'top' | 'left'
  children?: ReactNode
}
export declare const Panel: (props: PanelProps) => JSX.Element

/** Heading above a rail or grid, with a rule running to an optional action. */
export declare function SectionHeader(props: {
  icon: Icon
  title: string
  count?: number
  action?: ReactNode
}): JSX.Element

/** Compact metric tile with a left accent and an icon. */
export declare function StatTile(props: {
  label: string
  value: ReactNode
  icon: Icon
  accent?: 'brand' | 'ok' | 'warn' | 'info' | 'danger' | 'gray'
  /** Fractional change vs the previous period. null means the baseline was zero. */
  delta?: number | null
  deltaLabel?: string
  /** For metrics where up is bad. */
  invertDelta?: boolean
  hint?: string
  loading?: boolean
}): JSX.Element

export interface Figure {
  label: string
  value: number
  /** Only coloured while non-zero. */
  tone?: 'ok' | 'warn' | 'danger' | 'info'
}

/** A row of labelled counts in one ruled strip. */
export declare function FigureStrip(props: {
  figures: Figure[]
  flush?: boolean
  loading?: boolean
  className?: string
}): JSX.Element

/** A status as a coloured dot and a word. `tone` is a token stem. */
export declare function StatusDot(
  props: ComponentPropsWithRef<'span'> & { tone: string; live?: boolean },
): JSX.Element

export interface TagChipProps {
  children: ReactNode
  /** Any CSS colour. Present means the chip shows its 6px bucket dot. */
  dot?: string
  active?: boolean
  onClick?: MouseEventHandler<HTMLButtonElement>
  href?: string
  size?: 'sm' | 'md'
  className?: string
  title?: string
  style?: CSSProperties
}
/** Bordered tag pill; a button or link when given onClick or href. */
export declare function TagChip(props: TagChipProps): JSX.Element
/** The flex-wrap row chips live in. */
export declare function TagChips(props: { children: ReactNode; className?: string; style?: CSSProperties }): JSX.Element

/** What a section says when it has nothing to show. */
export declare function EmptyState(props: {
  title: string
  description?: ReactNode
  actionLabel?: string
  actionTo?: string
  onAction?: () => void
  compact?: boolean
}): JSX.Element

/** Poster card for the library grid. `series` is the API's SeriesDto. */
export declare const CoverCard: (props: {
  series: unknown
  selectMode: boolean
  selected: boolean
  readTracking: boolean
  onToggle: (id: number) => void
}) => JSX.Element

/** A catalogue item from MangaBaka (RecommendationItem in api/hooks). */
export type RecommendationItem = unknown

/** One horizontal rail of catalogue poster cards. */
export declare function DiscoverRailRow(props: {
  items: RecommendationItem[]
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: (item: RecommendationItem) => void
  /** Show each card's reason line. Off by default: the rail heading already says why. */
  showReason?: boolean
}): JSX.Element

/** Catalogue poster card. The corner shows a check when owned, an add control otherwise. */
export declare const RecommendationCard: (props: {
  item: RecommendationItem
  /** Library series id if already owned; null otherwise. */
  inLibrarySeriesId: number | null
  onOpen: (item: RecommendationItem) => void
  /** A string replaces the reason line, null hides it. */
  reasonOverride?: string | null
}) => JSX.Element

/** List-view row for a catalogue item, on the SeriesRow classes. */
export declare const RecommendationRow: (props: {
  item: RecommendationItem
  inLibrarySeriesId: number | null
  density: 'compact' | 'default' | 'comfortable'
  onOpen: (item: RecommendationItem) => void
  reasonOverride?: string | null
}) => JSX.Element

/** A rail of EngineCards, for rows the recommender produced. */
export declare function EngineRailRow(props: {
  items: RecommendationItem[]
  seriesIdFor: (item: RecommendationItem) => number | null
  onOpen: (item: RecommendationItem) => void
}): JSX.Element

/** Wider poster card with a footer naming why the pick is here. */
export declare const EngineCard: (props: {
  item: RecommendationItem
  inLibrarySeriesId: number | null
  onOpen: (item: RecommendationItem) => void
}) => JSX.Element

/** List-view card for an owned series. */
export declare const SeriesRow: (props: {
  series: unknown
  selectMode: boolean
  selected: boolean
  readTracking: boolean
  density: 'compact' | 'default' | 'comfortable'
  onToggle: (id: number) => void
}) => JSX.Element

/** The confirmation before an irreversible action. */
export declare function ConfirmDialog(props: {
  opened: boolean
  onClose: () => void
  title: ReactNode
  /** What follows from the action. */
  children: ReactNode
  confirmLabel: ReactNode
  onConfirm: () => void
  loading?: boolean
}): JSX.Element

/** The app-wide delegated tooltip. Mount once; any element with data-tip gets one. */
export declare function TipLayer(): JSX.Element

export type SurfaceWidth = 'full' | 'wide'
export type SurfacePageStyle = 'standard' | 'editorial' | 'operational'

/** Outer wrapper of every route: width and page style. */
export declare function SurfaceFrame(props: ComponentPropsWithRef<'div'> & {
  children?: ReactNode
  /** Default 'wide' (content-wide). */
  width?: SurfaceWidth
  /** Default 'standard'. */
  pageStyle?: SurfacePageStyle
}): JSX.Element

/** A panel-table outline while rows load. */
export declare function TableSkeleton(props: { columns: number; rows?: number }): JSX.Element
