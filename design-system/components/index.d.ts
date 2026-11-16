// Prop types of Maki's shared UI primitives (frontend/src/components/ui), as documentation.
// Button and Badge are Mantine components with theme defaults and are not re-declared here.
import type { ReactNode, CSSProperties, MouseEventHandler, ComponentPropsWithRef, ComponentType, SVGProps } from 'react'
import type { PaperProps } from '@mantine/core'

/** An icon from frontend/src/lib/icons.tsx (Phosphor, Regular; Bold below 14px). */
type Icon = ComponentType<SVGProps<SVGSVGElement> & { size?: number | string }>

/** Page title row: title and description left, actions right. */
export declare function PageHeader(props: {
  title: ReactNode
  description?: ReactNode
  actions?: ReactNode
  /** Smaller title for operational pages. */
  compact?: boolean
  className?: string
}): JSX.Element

/** The house panel: bordered Paper, radius lg, padding lg, 1px card shadow. */
export interface PanelProps extends PaperProps {
  children?: ReactNode
}
export declare const Panel: (props: PanelProps) => JSX.Element

/** Heading above a rail or grid, with a rule running to an optional action. */
export declare function SectionHeader(props: {
  icon: Icon
  title: string
  count?: number
  action?: ReactNode
  /** Trailing chevron after a single text action that opens a bigger view. */
  chevron?: boolean
}): JSX.Element

/** Title inside a panel or card, one tier under a SectionHeader. */
export declare function PanelTitle(props: {
  icon?: Icon
  /** Heading level, 3 by default. */
  order?: 2 | 3 | 4
  className?: string
  children: ReactNode
}): JSX.Element

export interface Figure {
  label: string
  /** A count, formatted by the strip, or a value the caller already formatted. */
  value: number | ReactNode
  /** Only coloured while non-zero. */
  tone?: 'ok' | 'warn' | 'danger' | 'info'
  /** Whether a formatted value stands for nothing, so it greys out like a zero count. */
  zero?: boolean
  /** Fractional change vs the previous period (0.12 is +12%), as a chip beside the value. Hidden on a zero. */
  delta?: number
  /** A dimmed line under the label. */
  hint?: ReactNode
}

/** Every row of figures in the app: equal cells in one ruled strip. */
export declare function FigureStrip(props: {
  figures: Figure[]
  /** Drops the strip's own border and surface inside a panel. */
  flush?: boolean
  /** Keeps the labels and blanks the numbers. */
  loading?: boolean
  /** Shrinks to one short line when every figure is zero. */
  quietWhenZero?: boolean
  /** What the deltas compare against; the chips' tooltip. */
  deltaLabel?: string
  className?: string
}): JSX.Element

export interface DisclosureProps {
  title: ReactNode
  /** Badges how many things inside are set. Hidden at 0. */
  count?: number
  /** What a screen reader hears for `count`, translated. */
  countLabel?: string
  /** Up to two lines under the title, shown only while closed. */
  summary?: ReactNode
  opened: boolean
  onToggle: () => void
  children: ReactNode
}
export declare function Disclosure(props: DisclosureProps): JSX.Element

/** The count on an icon button. Hidden at zero, `99+` past 99; the child is the ActionIcon. */
export declare function CountIndicator(props: {
  count: number
  tone?: 'brand' | 'warn' | 'danger'
  children: ReactNode
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
  /** Sits in a dashed outline of the table it stands in for. */
  framed?: boolean
  /** The whole page is empty: centred, over empty cover slots or a lone blank cover. */
  art?: 'shelf' | 'missing'
  /** The mascot beside the text of the inline form. */
  mood?: 'asleep' | 'asking' | 'pleased'
  code?: string
  headingOrder?: 1 | 2
  actionIcon?: ReactNode
  secondaryActionLabel?: string
  secondaryActionTo?: string
  secondaryActionIcon?: ReactNode
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
  /** Cover of the library series this pick was seeded from. */
  seedCoverUrl?: string | null
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

/** A utility modal's title with one line saying what the dialog is for. Pass as the Modal's `title`. */
export declare function ModalHeading(props: { title: ReactNode; description?: ReactNode }): JSX.Element

/** The pinned bar at a utility modal's foot: a status line left, the actions right. The body's last child. */
export declare function ModalFooter(props: { status?: ReactNode; children?: ReactNode }): JSX.Element

/** One line inside a dialog that needs attention: a tone icon, then the words. */
export declare function ModalNote(props: { tone?: 'warn' | 'info'; children: ReactNode }): JSX.Element

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

/** Short facts in a row split by a hairline, not a centre dot. Falsy items are dropped. */
export declare function MetaList(props: {
  items: ReactNode[]
  /** Spans instead of a list, for a parent that truncates or line-clamps. */
  inline?: boolean
  className?: string
  style?: CSSProperties
}): JSX.Element | null

/**
 * A file name, path or id on one line. Too long for its column, it loses its middle, keeping the
 * start and the extension; the full value is in a tooltip and, with `copy`, behind a copy button.
 */
export declare function MonoValue(props: {
  value: string
  /** Tooltip and copy value when the label is shorter, e.g. a file name standing for its path. */
  full?: string
  href?: string
  copy?: boolean
  /** Stops a table column collapsing to the tail. */
  minWidth?: CSSProperties['minWidth']
  fz?: CSSProperties['fontSize']
  c?: CSSProperties['color']
}): JSX.Element

/** A drill panel's row: the setting's icon in the accent, its name, its current value, a chevron. Opens a sub-page. */
export declare function DrillRow(props: {
  icon: ComponentType<{ size?: number; className?: string }>
  label: ReactNode
  value?: ReactNode
  /** A value the user set reads in the accent. */
  active?: boolean
  /** Off for a row that runs an action rather than opening a page. */
  chevron?: boolean
  onClick: () => void
}): JSX.Element

/** A drill panel sub-page's header: back, and the page's name. */
export declare function DrillBack(props: {
  title: ReactNode
  onBack: () => void
  /** Replaces the panel spacing, for a panel whose content is not inset like rows are. */
  className?: string
}): JSX.Element

/** One choice from a list, each with a hint under it; the current one carries the check. */
export declare function DrillChoices(props: {
  /** Names the group for a screen reader. */
  label: string
  /** A visible name over the list, for a page that splits its choices into groups. */
  heading?: ReactNode
  value: string
  options: { value: string; label: ReactNode; hint?: ReactNode; disabled?: boolean }[]
  onPick: (value: string) => void
}): JSX.Element

/** A setting changed in place: icon and name over the control, one line under it on the current pick. */
export declare function DrillField(props: {
  icon: ComponentType<{ size?: number }>
  title: ReactNode
  help?: ReactNode
  children: ReactNode
}): JSX.Element

/** Rows of a drill panel's first page under a plain heading. */
export declare function DrillGroup(props: { heading: ReactNode; children: ReactNode }): JSX.Element

/** Any number of choices from a list; each is a checkbox drawn as a box in the check slot. */
export declare function DrillMultiChoices(props: {
  label: string
  heading?: ReactNode
  value: string[]
  options: { value: string; label: ReactNode; hint?: ReactNode; disabled?: boolean }[]
  onChange: (value: string[]) => void
  disabled?: boolean
}): JSX.Element

/** Choices to include or leave out: one tap includes, a second leaves out, a third clears. */
export declare function DrillTriChoices(props: {
  label: string
  include: string[]
  exclude: string[]
  options: { value: string; label: ReactNode; hint?: ReactNode; disabled?: boolean }[]
  onChange: (include: string[], exclude: string[]) => void
  disabled?: boolean
}): JSX.Element

/** The search box over a long list of choices. */
export declare function DrillSearch(props: { label: string; placeholder?: string; value: string; onChange: (value: string) => void }): JSX.Element

/** Two number boxes in a field; an empty box leaves that end open. */
export declare function DrillNumberRange(props: {
  icon: ComponentType<{ size?: number }>
  title: ReactNode
  help?: ReactNode
  from: { label: string; placeholder: string; value: number | null; onChange: (value: number | null) => void }
  to: { label: string; placeholder: string; value: number | null; onChange: (value: number | null) => void }
  children?: ReactNode
}): JSX.Element

/** A range on two single sliders, From over To, each with its value beside it. `DrillSliderLines` is the same without the field. */
export declare function DrillSliderRange(props: {
  icon: ComponentType<{ size?: number }>
  title: ReactNode
  help?: ReactNode
  min: number
  max: number
  step: number
  value: [number, number]
  onChange: (value: [number, number]) => void
  fromLabel: string
  toLabel: string
  format: (n: number) => string
}): JSX.Element

/** An on or off setting with its hint, the switch at the end of the row. */
export declare function DrillSwitch(props: { label: ReactNode; hint?: ReactNode; checked: boolean; onChange: (checked: boolean) => void }): JSX.Element

/** Pinned under a drill panel that fills a drawer: Reset on the left, a live status and Done on the right. */
export declare function DrillFooter(props: { onReset: () => void; resetDisabled?: boolean; status?: ReactNode; onDone: () => void }): JSX.Element

/** Slides between a drill panel's pages and eases its height. Keyed on `page`; `depth` picks the direction. */
export declare function DrillPanes(props: { page: string; depth: number; bleed?: number; easeHeight?: boolean; children: ReactNode }): JSX.Element

/** The current page, its depth, `open`/`back`, and the refs that put focus back where it was. */
export declare function useDrillPages<P extends string>(
  root: P,
  parents: Record<P, P>,
): {
  page: P
  depth: number
  open: (to: P) => void
  back: () => void
  /** Straight back to the first page without moving focus, for a panel that is reopening. */
  reset: () => void
  backRef: { current: HTMLButtonElement | null }
  rowRef: (p: P) => (node: HTMLButtonElement | null) => void
}
