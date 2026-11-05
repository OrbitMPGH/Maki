// Writes frontend/src/lib/icons.tsx: one component per name below, holding only the Phosphor weights
// the app draws. @phosphor-icons/react ships all six weights inside every icon, which made the icon
// chunk eight times the size of the Tabler one it replaced.
//
// Regular is the default because Phosphor draws it on a 16px pixel grid: at exactly 16px its lines
// land on whole pixels, where Bold's 1.5px lines always leave a soft half-pixel edge. Below 14px
// Regular gets too faint, so icons drawn that small also carry their Bold path and switch to it.
// "That small" means a literal `size` under 14 anywhere in src, plus every icon in SMALL_SOURCES,
// which render icons they're handed at badge size.
//
// Names describe the glyph, not the use, so call sites keep reading the same if the set changes.
// A value is the Phosphor file name, optionally `:fill`. Run `npm run icons` after editing the map
// or adding a small icon.
import { readdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const ICONS = {
  IconActivity: 'pulse',
  IconAdjustments: 'sliders',
  IconAdjustmentsHorizontal: 'sliders-horizontal',
  IconAffiliate: 'share-network',
  IconAlertCircle: 'warning-circle',
  IconAlertOctagon: 'warning-octagon',
  IconAlertTriangle: 'warning',
  IconAperture: 'aperture',
  IconArchive: 'archive',
  IconArchiveOff: 'arrow-counter-clockwise',
  IconArmchair: 'armchair',
  IconArrowAutofitHeight: 'arrows-vertical',
  IconArrowAutofitWidth: 'arrows-horizontal',
  IconArrowBackUp: 'arrow-u-up-left',
  IconArrowBarToDown: 'arrow-line-down',
  IconArrowBarToUp: 'arrow-line-up',
  IconArrowDown: 'arrow-down',
  IconArrowLeft: 'arrow-left',
  IconArrowRight: 'arrow-right',
  IconArrowUp: 'arrow-up',
  IconArrowUpRight: 'arrow-up-right',
  IconArrowsMaximize: 'arrows-out',
  IconArrowsShuffle: 'shuffle',
  IconBan: 'prohibit',
  IconBell: 'bell',
  IconBellCheck: 'bell-ringing',
  IconBellOff: 'bell-slash',
  IconBellPlus: 'bell-simple',
  IconBellRinging: 'bell-ringing',
  IconBook: 'book-open',
  IconBookFilled: 'book-open:fill',
  IconBook2: 'book',
  IconBookmark: 'bookmark-simple',
  IconBookmarkFilled: 'bookmark-simple:fill',
  IconBookmarks: 'bookmarks-simple',
  IconBooks: 'books',
  IconBrandDiscord: 'discord-logo',
  IconBrandSlack: 'slack-logo',
  IconBrandTelegram: 'telegram-logo',
  IconBroadcast: 'broadcast',
  IconBug: 'bug',
  IconCalendar: 'calendar-blank',
  IconCalendarEvent: 'calendar-dot',
  IconCalendarStats: 'calendar-dots',
  IconChartHistogram: 'chart-bar',
  IconChartLine: 'chart-line',
  IconChartPie: 'chart-pie-slice',
  IconCheck: 'check',
  IconChecks: 'checks',
  IconChevronDown: 'caret-down',
  IconChevronLeft: 'caret-left',
  IconChevronRight: 'caret-right',
  IconChevronUp: 'caret-up',
  IconCircleArrowUp: 'arrow-circle-up',
  IconCircleCheck: 'check-circle',
  IconCircleCheckFilled: 'check-circle:fill',
  IconClock: 'clock',
  IconClockPause: 'clock-countdown',
  IconClockPlay: 'clock-clockwise',
  IconCloudDownload: 'cloud-arrow-down',
  IconCloudOff: 'cloud-slash',
  IconColumns: 'columns',
  IconCompass: 'compass',
  IconConfetti: 'confetti',
  IconCopy: 'copy',
  IconDatabase: 'database',
  IconDeviceFloppy: 'floppy-disk',
  IconDeviceMobile: 'device-mobile',
  IconDeviceMobileCheck: 'device-mobile-speaker',
  IconDeviceMobileDown: 'device-mobile-camera',
  IconDeviceMobileMessage: 'chat-teardrop-text',
  IconDeviceMobileOff: 'device-mobile-slash',
  IconDeviceMobileX: 'device-mobile-slash',
  IconDeviceTv: 'television-simple',
  IconDice5: 'dice-five',
  IconDots: 'dots-three',
  IconDotsVertical: 'dots-three-vertical',
  IconDownload: 'download-simple',
  IconExternalLink: 'arrow-square-out',
  IconEye: 'eye',
  IconEyeCheck: 'eyes',
  IconEyeOff: 'eye-slash',
  IconFeather: 'feather',
  IconFileImport: 'file-arrow-down',
  IconFileOff: 'file-x',
  IconFileText: 'file-text',
  IconFileTypePdf: 'file-pdf',
  IconFileUnknown: 'file-dashed',
  IconFileZip: 'file-zip',
  IconFilter: 'funnel-simple',
  IconFlag: 'flag',
  IconFlame: 'fire',
  IconFolder: 'folder-simple',
  IconFolderDown: 'folder-open',
  IconFolderPlus: 'folder-simple-plus',
  IconFolderSearch: 'magnifying-glass',
  IconFolderSymlink: 'arrow-bend-up-right',
  IconFolders: 'folders',
  IconGauge: 'gauge',
  IconGhost: 'ghost',
  IconGripVertical: 'dots-six-vertical',
  IconHeart: 'heart',
  IconHeartFilled: 'heart:fill',
  IconHeartbeat: 'heartbeat',
  IconHelp: 'question',
  IconHelpCircle: 'question',
  IconHelpHexagon: 'seal-question',
  IconHistory: 'clock-counter-clockwise',
  IconHome: 'house',
  IconHourglass: 'hourglass',
  IconHourglassLow: 'hourglass-low',
  IconInbox: 'tray',
  IconInfoCircle: 'info',
  IconIntersect: 'intersect',
  IconKeyboard: 'keyboard',
  IconLanguage: 'translate',
  IconLayoutDashboard: 'squares-four',
  IconLayoutGrid: 'grid-four',
  IconLayoutList: 'rows',
  IconLayoutRows: 'rows',
  IconLayoutSidebarLeftCollapse: 'sidebar-simple',
  IconLayoutSidebarLeftExpand: 'sidebar-simple',
  IconLibrary: 'books',
  IconLightning: 'lightning',
  IconLink: 'link',
  IconLinkOff: 'link-break',
  IconListCheck: 'list-checks',
  IconListDetails: 'list-dashes',
  IconListTree: 'tree-structure',
  IconLoader2: 'circle-notch',
  IconLock: 'lock-simple',
  IconLockOpen: 'lock-simple-open',
  IconLogout: 'sign-out',
  IconMaximize: 'corners-out',
  IconMegaphone: 'megaphone-simple',
  IconMessage2: 'chat-text',
  IconMessages: 'chats',
  IconMinimize: 'corners-in',
  IconMinus: 'minus',
  IconMoon: 'moon',
  IconMoonStars: 'moon-stars',
  IconMountain: 'mountains',
  IconNote: 'note',
  IconPackage: 'package',
  IconPackageImport: 'arrow-square-in',
  IconPackages: 'stack',
  IconPalette: 'palette',
  IconPencil: 'pencil-simple',
  IconPhoto: 'image',
  IconPhotoCheck: 'seal-check',
  IconPhotoCog: 'image-square',
  IconPhotoScan: 'images',
  IconPhotoSearch: 'magnifying-glass-plus',
  IconPlant: 'plant',
  IconPlayerPause: 'pause',
  IconPlayerPauseFilled: 'pause:fill',
  IconPlayerPlay: 'play',
  IconPlayerPlayFilled: 'play:fill',
  IconPlayerTrackNext: 'skip-forward',
  IconPlugConnected: 'plugs-connected',
  IconPlus: 'plus',
  IconPower: 'power',
  IconPuzzle: 'puzzle-piece',
  IconQuestionMark: 'question-mark',
  IconRanking: 'ranking',
  IconRefresh: 'arrow-clockwise',
  IconRefreshDot: 'arrows-clockwise',
  IconRestore: 'arrow-counter-clockwise',
  IconRoute: 'path',
  IconRowRemove: 'list-dashes',
  IconRuler: 'ruler',
  IconScan: 'scan',
  IconSearch: 'magnifying-glass',
  IconSelector: 'caret-up-down',
  IconSend: 'paper-plane-tilt',
  IconServer: 'hard-drives',
  IconSettings: 'gear-six',
  IconShield: 'shield',
  IconShieldLock: 'shield-check',
  IconSitemap: 'tree-structure',
  IconSortAscending: 'sort-ascending',
  IconSortAscendingNumbers: 'sort-ascending',
  IconSortDescending: 'sort-descending',
  IconSortDescendingNumbers: 'sort-descending',
  IconSquare: 'square',
  IconSquareHalf: 'square-half',
  IconStar: 'star',
  IconStarFilled: 'star:fill',
  IconStethoscope: 'stethoscope',
  IconSun: 'sun',
  IconSunrise: 'sun-horizon',
  IconTag: 'tag',
  IconTags: 'tag-chevron',
  IconTarget: 'target',
  IconThumbDown: 'thumbs-down',
  IconThumbUp: 'thumbs-up',
  IconTool: 'wrench',
  IconTrash: 'trash',
  IconTrashX: 'broom',
  IconTrendingDown: 'trend-down',
  IconTrendingUp: 'trend-up',
  IconTrophy: 'trophy',
  IconUpload: 'upload-simple',
  IconUser: 'user',
  IconUserCircle: 'user-circle',
  IconUserQuestion: 'user-circle-dashed',
  IconUsers: 'users',
  IconViewportWide: 'arrows-out-line-horizontal',
  IconWand: 'magic-wand',
  IconWebhook: 'webhooks-logo',
  IconWorld: 'globe',
  IconWorldWww: 'globe-simple',
  IconX: 'x',
  IconZoomIn: 'magnifying-glass-plus',
  IconZoomOut: 'magnifying-glass-minus',
  IconZoomReset: 'magnifying-glass',
}

const SMALL_SOURCES = ['components/ui/status.tsx', 'components/ui/DiscoverRail.tsx', 'components/NotificationsSection.tsx']
const SMALL_BELOW = 14

const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'frontend')
const src = join(root, 'src')
const assets = join(root, 'node_modules', '@phosphor-icons', 'core', 'assets')

const small = new Set()
const walk = (dir) => {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name)
    if (entry.isDirectory()) walk(full)
    else if (/\.tsx$/.test(entry.name)) {
      for (const m of readFileSync(full, 'utf8').matchAll(/<(Icon[A-Z]\w*)\b[^<>]*?\bsize=\{(\d+)\}/g)) {
        if (Number(m[2]) < SMALL_BELOW) small.add(m[1])
      }
    }
  }
}
walk(src)
for (const file of SMALL_SOURCES) {
  for (const m of readFileSync(join(src, file), 'utf8').matchAll(/\bIcon[A-Z]\w*/g)) small.add(m[0])
}

function path(file, weight) {
  const svg = readFileSync(join(assets, weight, weight === 'regular' ? `${file}.svg` : `${file}-${weight}.svg`), 'utf8')
  const paths = [...svg.matchAll(/<path d="([^"]+)"\/>/g)].map((m) => m[1])
  if (paths.length !== 1 || svg.replace(/<path d="[^"]+"\/>/, '').includes('<path')) throw new Error(`${file}-${weight}: expected exactly one path`)
  return paths[0]
}

const lines = []
let withSmall = 0
for (const [name, spec] of Object.entries(ICONS)) {
  const [file, fixed] = spec.split(':')
  const args = [`'${name}'`, `'${path(file, fixed ?? 'regular')}'`]
  if (!fixed && small.has(name)) {
    args.push(`'${path(file, 'bold')}'`)
    withSmall++
  }
  lines.push(`export const ${name} = /*#__PURE__*/ make(${args.join(', ')})`)
}

const out = `// Generated by scripts/frontend/generate-icons.mjs from @phosphor-icons/core (MIT). Edit the map there
// and run \`npm run icons\`; never edit this file by hand.
import { forwardRef, type ForwardRefExoticComponent, type RefAttributes, type SVGProps } from 'react'

export type IconProps = Omit<SVGProps<SVGSVGElement>, 'ref'> & { size?: number | string }
export type Icon = ForwardRefExoticComponent<IconProps & RefAttributes<SVGSVGElement>>

function make(name: string, regular: string, bold?: string): Icon {
  const C = forwardRef<SVGSVGElement, IconProps>(({ size = 24, ...rest }, ref) => (
    <svg ref={ref} xmlns="http://www.w3.org/2000/svg" width={size} height={size} viewBox="0 0 256 256" fill="currentColor" {...rest}>
      <path d={bold && Number(size) < ${SMALL_BELOW} ? bold : regular} />
    </svg>
  ))
  C.displayName = name
  return C
}

${lines.join('\n')}
`
writeFileSync(join(src, 'lib', 'icons.tsx'), out)
console.log(`Wrote ${lines.length} icons, ${withSmall} with a Bold path for sizes under ${SMALL_BELOW}px.`)
