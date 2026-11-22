import {
  Fragment,
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type KeyboardEvent,
  type PointerEvent as ReactPointerEvent,
  type ReactNode,
} from 'react'
import { useReducedMotion } from 'motion/react'
import { Plural, Trans, useLingui } from '@lingui/react/macro'
import { t as now } from '@lingui/core/macro'
import type { MessageDescriptor } from '@lingui/core'
import { ActionIcon, Button, Drawer, Group, Loader, Popover, Text, Tooltip, VisuallyHidden } from '@mantine/core'
import { useMediaQuery } from '@mantine/hooks'
import { notifications } from '../../lib/toast'
import {
  IconCompass,
  IconEye,
  IconEyeOff,
  IconFilter,
  IconGripVertical,
  IconLayoutDashboard,
  IconLibrary,
  IconPlus,
  IconSettings,
  IconX,
  type Icon,
} from '../../lib/icons'
import { isRailKey, railIdOf, useSavePageLayout, type PageSection } from '../../api/hooks'
import {
  RAIL_SOURCE_LABELS,
  useCustomRailItems,
  type CustomRail,
  type CustomRailPlacement,
} from '../../api/customRails'
import { useLabel } from '../../i18n-context'
import { onPageScroll, pageScrollTop, pageScroller, pageViewportHeight, scrollPageTo } from '../../lib/pageScroll'
import { SectionErrorBoundary } from '../SectionErrorBoundary'
import { CustomRailEditor, type CustomRailDraft } from '../rails/CustomRailEditor'
import { DeleteRailDialog } from '../rails/DeleteRailDialog'
import { ModalHeading } from '../ui/ModalShell'
import { sectionVisible, type SectionRegistry } from './pageLayout'
import { SectionSettings } from './SectionSettings'
import type { LayoutDraft } from './useLayoutDraft'

const RAIL_ICONS = {
  library: IconLibrary,
  recommendations: IconCompass,
  catalogue: IconFilter,
}

/** Sections in edit mode are drawn at this scale, laid out wider so they still fill the frame. */
const EDIT_SCALE = 0.85
/** And cut to this height, with a fade at the cut. */
const ROW_CAP = 220
/** The shrink into edit mode and the grow back out of it. */
const SHRINK_MS = 440
/** How far a press has to travel before it picks a section up rather than clicking it. */
const DRAG_THRESHOLD = 6
/** Within this many pixels of the edit bar or the viewport's bottom, a carried section scrolls the page. */
const EDGE = 96
/** A carried section grows by this much, so it reads as picked up. */
const LIFT = 1.012

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v))

/** The bottom of the sticky edit bar in the viewport, so nothing is placed under it. */
function barBottom(): number {
  return Math.max(0, document.querySelector('.layout-edit-bar')?.getBoundingClientRect().bottom ?? 0)
}

function maxScroll(): number {
  const el = pageScroller()
  if (el) return el.scrollHeight - el.clientHeight
  return document.documentElement.scrollHeight - window.innerHeight
}

/** How far the edit view has come in: 0 is the page as it looks normally, 1 is edit mode. */
interface View {
  e: number
  sc: number
}
const FULL: View = { e: 0, sc: 1 }
const EDIT: View = { e: 1, sc: EDIT_SCALE }

interface Slab {
  el: HTMLElement
  key: string
  /** Page coordinates at pickup. */
  top: number
  h: number
  left: number
  dy: number
}

interface Drag {
  me: Slab
  others: Slab[]
  from: number
  /** The slot the section would land in right now, counted among the others. */
  k: number
  /** Each slot's top in page coordinates. */
  s: number[]
  gap: number
  listTop: number
  containerTop: number
  total: number
  /** Where on the section it was grabbed, from its top. */
  grabY: number
  startX: number
  startY: number
  px: number
  py: number
  top: number
  armTop: boolean
  armBottom: boolean
  last: number
  edgeSince: number
  raf: number
  dropping: boolean
}

export interface LayoutEditSurfaceProps {
  page: 'home' | 'discover'
  placement: CustomRailPlacement
  registry: SectionRegistry
  /** Edit mode. The same frames draw the page outside it, so the sections never remount. */
  editing: boolean
  /** The layout being edited; null outside edit mode or while it loads. */
  draft: LayoutDraft | null
  /** What the page shows outside edit mode: the visible sections, in the saved order. */
  sections: PageSection[]
  rails: CustomRail[]
  /** The page's own rail size, so previews read the same cached query the page filled. */
  railLimit: number
  /** The section itself, as the page draws it. Shown inert inside its frame. */
  render: (key: string) => ReactNode
  /** One line beside a section's name, from data the page already holds. Must not fetch. */
  summary?: (key: string) => ReactNode
  /** Cover URLs for a hidden section's strip, from cached data only. */
  preview?: (key: string) => (string | null)[]
  /** Why a section cannot show anything right now, e.g. the local database is off. */
  unavailable?: (key: string) => MessageDescriptor | null
  panelHint?: (sectionKey: string, panelKey: string) => MessageDescriptor | null
  optionHint?: (sectionKey: string, optionKey: string) => MessageDescriptor | null
  onExit: () => void
}

interface Selection {
  key: string
  /** Where the panel opens, relative to the section's frame. */
  x: number
  y: number
  /** Set when it opens from the settings button: the button's box, which the panel hangs from. */
  w?: number
  h?: number
}

const IDLE_DRAFT: LayoutDraft = {
  sections: [],
  baseline: [],
  changes: 0,
  set: () => {},
  update: () => {},
  move: () => {},
}

/**
 * A page's sections, in frames that stay mounted in and out of edit mode so entering it never
 * rebuilds a section. Outside edit mode the frames draw nothing of their own.
 *
 * Layout editing on the page itself. Entering it shrinks every section a little and cuts it shorter,
 * so the whole page is easier to rearrange while each section still looks like itself. Each sits in
 * a frame whose content stops responding; the frame's title strip carries a grip, the section's
 * name, a hide button and a settings button. Clicking a section opens its settings where the pointer
 * is (a bottom sheet on a phone). Dragging lifts the section itself: the others slide out of its way
 * and it drops into the gap, with the page's scroll position untouched.
 *
 * Works on the page's draft: Done saves it in one request and Cancel throws it away. Rails are the
 * exception: creating, editing or deleting one saves the rail itself straight away.
 */
export function LayoutEditSurface({
  page,
  placement,
  registry,
  editing,
  draft: maybeDraft,
  sections: viewSections,
  rails,
  railLimit,
  render,
  summary,
  preview,
  unavailable,
  panelHint,
  optionHint,
  onExit,
}: LayoutEditSurfaceProps) {
  const { t } = useLingui()
  const renderLabel = useLabel()
  const reduceMotion = useReducedMotion() ?? false
  const sheet = useMediaQuery('(max-width: 47.9375em)', false, {
    getInitialValueInEffect: false,
  })
  const save = useSavePageLayout()
  const editActive = editing && maybeDraft != null
  const draft = maybeDraft ?? IDLE_DRAFT
  const sections = editActive ? draft.sections : viewSections

  const [selected, setSelected] = useState<Selection | null>(null)
  const [adding, setAdding] = useState<{ at: number | null } | null>(null)
  const [pendingRail, setPendingRail] = useState<{
    id: number
    at: number | null
  } | null>(null)
  const [editingRail, setEditingRail] = useState<CustomRail | null>(null)
  const [deletingRail, setDeletingRail] = useState<CustomRail | null>(null)
  const [created, setCreated] = useState<Set<number>>(() => new Set())
  const [announcement, setAnnouncement] = useState('')
  const [scrollKey, setScrollKey] = useState<string | null>(null)
  const [moving, setMoving] = useState<{
    key: string
    position: number
  } | null>(null)

  const framesRef = useRef<HTMLDivElement>(null)
  const slotRef = useRef<HTMLDivElement>(null)
  const view = useRef<View>(FULL)
  const anim = useRef<{ finish: () => void; cancel: () => void } | null>(null)
  const dragRef = useRef<Drag | null>(null)
  const dragEndedAt = useRef(0)
  const clearAfterCommit = useRef(false)
  const flip = useRef<{ before: Map<string, number>; keep: string; focus: boolean } | null>(null)
  /** Set once Done or Cancel starts growing the sections back, so nothing else starts meanwhile. */
  const leaving = useRef(false)
  /** Cancel puts the saved order back first, then grows back from it. */
  const leaveAfterRender = useRef(false)
  /** Set from Done's click until its request settles, so a second click sends nothing. */
  const saving = useRef(false)
  /** Done or Cancel pressed while a dropped section is still settling: runs once it has. */
  const afterDrop = useRef<'done' | 'cancel' | null>(null)
  const dropTimer = useRef(0)
  /** Removes the window listeners of the press in progress. */
  const dragStop = useRef<(() => void) | null>(null)
  const editRef = useRef(editActive)
  editRef.current = editActive
  const barRef = useRef<HTMLDivElement>(null)
  /** The edit bar's height with its margins, so the page can hold still as it comes and goes. */
  const barSpace = useRef(0)

  const dirty = draft.changes > 0
  useEffect(() => {
    if (!dirty) return
    const warn = (e: BeforeUnloadEvent) => e.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])

  const railOf = (key: string) => (isRailKey(key) ? rails.find((r) => r.id === railIdOf(key)) : undefined)
  const labelOf = (key: string): string => {
    if (isRailKey(key)) return railOf(key)?.name ?? t`Custom rail`
    const def = registry[key]
    return def ? renderLabel(def.label) : key
  }
  const iconOf = (key: string): Icon | undefined => {
    const rail = railOf(key)
    return rail ? RAIL_ICONS[rail.spec.source] : registry[key]?.icon
  }
  const summaryOf = (key: string): ReactNode => {
    const own = summary?.(key)
    if (own) return own
    const rail = railOf(key)
    return rail ? renderLabel(RAIL_SOURCE_LABELS[rail.spec.source]) : null
  }

  // --- Sizes -------------------------------------------------------------------------------------

  const frameEls = () => Array.from(framesRef.current?.querySelectorAll<HTMLElement>('.layout-frame') ?? [])
  const partsOf = (el: HTMLElement) => ({
    body: el.querySelector<HTMLElement>(':scope > .layout-frame-body'),
    inner: el.querySelector<HTMLElement>(':scope > .layout-frame-body > .layout-frame-inner'),
  })
  /**
   * Puts the frames into a view at rest. `--el` drives the edit chrome's layout (strip heights,
   * padding, gaps), `--e` its opacity, `--sc` the sections' scale and `--w` the width they are laid
   * out at, so a scaled section still fills its frame.
   */
  const setLayout = (v: View, width: number) => {
    const c = framesRef.current
    if (!c) return
    c.style.setProperty('--el', String(v.e))
    c.style.setProperty('--e', String(v.e))
    c.style.setProperty('--sc', String(v.sc))
    c.style.setProperty('--w', String(width))
  }
  /** Each section's height at rest: its scaled height, cut at the row height. Reads every height
   *  before writing any, so it costs one layout rather than one per section. */
  const restHeights = () => {
    const sc = view.current.sc
    const rows = frameEls()
      .map(partsOf)
      .filter((p): p is { body: HTMLElement; inner: HTMLElement } => p.body != null && p.inner != null)
    if (sc === 1) {
      for (const { body } of rows) {
        body.style.height = ''
        body.removeAttribute('data-capped')
      }
      return
    }
    const heights = rows.map(({ inner }) => inner.offsetHeight * sc)
    rows.forEach(({ body }, i) => {
      body.style.height = `${Math.min(ROW_CAP, heights[i])}px`
      body.toggleAttribute('data-capped', heights[i] > ROW_CAP + 1)
    })
  }

  /** The point that stays still on screen while every section changes size. */
  const pickAnchor = () => {
    if (pageScrollTop() < 8) return null
    const refY = pageViewportHeight() * 0.4
    let el: HTMLElement | null = null
    let best = Infinity
    for (const f of frameEls()) {
      const r = f.getBoundingClientRect()
      const d = r.top <= refY && r.bottom >= refY ? 0 : Math.min(Math.abs(r.top - refY), Math.abs(r.bottom - refY))
      if (d < best) {
        best = d
        el = f
      }
    }
    if (!el) return null
    const r = el.getBoundingClientRect()
    const y = clamp(refY, r.top, r.bottom)
    return { el, f: r.height ? (y - r.top) / r.height : 0, y }
  }
  const holdAnchor = (a: ReturnType<typeof pickAnchor>) => {
    if (!a) return
    const r = a.el.getBoundingClientRect()
    const d = r.top + a.f * r.height - a.y
    if (Math.abs(d) > 0.5) scrollPageTo(pageScrollTop() + d)
  }

  /** The last frame of growing back out, held until edit mode has ended and the edit bar is gone. */
  const held = useRef<Animation[]>([])
  /** A section on screen as edit mode ends, and where it was, so the page holds it there. */
  const exitHold = useRef<{ el: HTMLElement; top: number } | null>(null)
  const release = () => {
    for (const a of held.current) a.cancel()
    held.current = []
  }

  /**
   * Shrinks the sections into edit mode or grows them back, holding one point still on screen.
   *
   * The page is laid out at the start and the end, and the browser animates between the two: each
   * frame slides from where it was, each section scales while the width it is laid out at grows or
   * shrinks with it, so it fills its frame the whole way, and its visible height eases from the old
   * to the new. Only that width is laid out on the way, inside each section, so the page around it
   * never reflows. The first and last frames match the page at rest exactly: the edit bar, the
   * frame's strip and border, the section's own heading and the fade at the cut all come and go
   * with the motion.
   */
  const shrinkTo = (to: View, done?: () => void, push = 0) => {
    anim.current?.finish()
    release()
    const c = framesRef.current
    const from = view.current
    const frames = frameEls()
    const anchor = pickAnchor()
    // The first thing under a section's own heading, which the frame's strip replaces in edit mode.
    const leadOf = (f: HTMLElement) =>
      f.dataset.ownHeading == null
        ? null
        : (partsOf(f).inner?.querySelector<HTMLElement>(
            ':scope > .section-header:first-child + *, :scope > :first-child > .section-header:first-child + *, :scope > :first-child:not(.section-header) + .section-header + *',
          ) ?? null)
    const headingOf = (f: HTMLElement) =>
      f.dataset.ownHeading == null
        ? null
        : (partsOf(f).inner?.querySelector<HTMLElement>(
            ':scope > .section-header:first-child, :scope > :first-child > .section-header:first-child, :scope > :first-child:not(.section-header) + .section-header',
          ) ?? null)
    const measure = (f: HTMLElement) => {
      const { body, inner } = partsOf(f)
      const fr = f.getBoundingClientRect()
      const r = body?.getBoundingClientRect()
      const lead = leadOf(f)?.getBoundingClientRect()
      return {
        top: fr.top,
        fh: fr.height,
        head: f.querySelector<HTMLElement>(':scope > .layout-frame-head')?.offsetHeight ?? 0,
        body,
        inner,
        bodyTop: r?.top ?? 0,
        left: r?.left ?? 0,
        w: r?.width ?? 0,
        h: r?.height ?? 0,
        capped: body?.hasAttribute('data-capped') ?? false,
        lead: lead && r ? lead.top - r.top : null,
      }
    }
    const before = frames.map(measure)
    // Going in, a section's own heading leaves the layout at once; a copy of it rides out with the
    // content instead. Coming back, the real one slides in from under the strip.
    const headings = frames.map((f) => {
      const el = to.e === 1 ? headingOf(f) : null
      return el ? { el, r: el.getBoundingClientRect() } : null
    })
    if (c) {
      c.toggleAttribute('data-boxed', to.e === 1)
      c.toggleAttribute('data-trim', to.e === 1)
    }
    view.current = to
    setLayout(to, to.sc)
    restHeights()
    holdAnchor(anchor)
    // Growing back out ends with the edit bar gone. How far that moves the page is measured here, in
    // the layout the page ends in, since margins collapse differently around the bar than when it
    // came in. What scrolling can't make up for, the sections rise by on the way, so nothing jumps.
    const bar = barRef.current
    if (to.e === 0 && bar && c) {
      const first = c.querySelector('.layout-frame-inner')?.firstElementChild ?? c
      const withBar = first.getBoundingClientRect().top
      bar.style.display = 'none'
      barSpace.current = withBar - first.getBoundingClientRect().top
      bar.style.display = ''
    }
    const lift = to.e === 0 ? Math.max(0, barSpace.current - pageScrollTop()) : 0

    let settled = false
    const running: Animation[] = []
    const clones: HTMLElement[] = []
    const shrinking: HTMLElement[] = []
    const cleanup = () => {
      for (const n of clones) n.remove()
      for (const f of shrinking) delete f.dataset.shrinking
      if (c) delete c.dataset.animating
      anim.current = null
    }
    const settle = () => {
      if (settled) return
      settled = true
      if (to.e === 0) {
        held.current = running
        // The section nearest the screen's upper middle; out of edit mode there are gaps between them.
        const refY = pageViewportHeight() * 0.4
        let best: { el: HTMLElement; top: number } | null = null
        let bestDist = Infinity
        for (const f of frameEls()) {
          const r = f.getBoundingClientRect()
          const dist =
            r.top <= refY && r.bottom >= refY ? 0 : Math.min(Math.abs(r.top - refY), Math.abs(r.bottom - refY))
          if (dist < bestDist) {
            bestDist = dist
            best = { el: f, top: r.top }
          }
        }
        exitHold.current = best
      } else for (const a of running) a.cancel()
      cleanup()
      if (to.e !== 0) restHeights()
      done?.()
    }
    anim.current = {
      finish: settle,
      cancel: () => {
        settled = true
        for (const a of running) a.cancel()
        cleanup()
      },
    }
    if (reduceMotion || !c) {
      settle()
      return
    }

    c.dataset.animating = 'true'
    const timing: KeyframeAnimationOptions = {
      duration: SHRINK_MS,
      easing: 'cubic-bezier(0.25, 1, 0.5, 1)',
      fill: to.e === 0 ? 'forwards' : 'none',
    }
    const clip = (w: number, h: number) => `polygon(0 0, ${w}px 0, ${w}px ${h}px, 0 ${h}px)`
    // Every position is read before any animation starts: an animation applies its first frame at
    // once, and would otherwise show up in the measurements that follow it.
    const after = frames.map(measure)
    // Only sections on or near the screen are animated; the rest just take their new size, which
    // keeps the GPU from preparing a page's worth of sections nobody can see.
    const vh = pageViewportHeight()
    const near = (top: number, h: number) => top < vh * 1.5 && top + h > -vh * 0.5
    const starts: (() => void)[] = []
    const copies: { copy: HTMLElement; x: number; y: number; at: (typeof after)[number] }[] = []
    frames.forEach((f, i) => {
      const was = before[i]
      const now = after[i]
      if (!near(was.top - push, was.fh) && !near(now.top, now.fh)) return
      f.dataset.shrinking = 'true'
      shrinking.push(f)
      const { body, inner } = now
      const shift = was.lead != null && now.lead != null ? was.lead - (now.lead / to.sc) * from.sc : 0
      const heading = headings[i]
      if (heading && inner) {
        const copy = heading.el.cloneNode(true) as HTMLElement
        copy.setAttribute('aria-hidden', 'true')
        Object.assign(copy.style, {
          position: 'absolute',
          top: '0px',
          left: '0px',
          margin: '0',
          width: `${heading.r.width / from.sc}px`,
          pointerEvents: 'none',
        })
        heading.el.after(copy)
        clones.push(copy)
        // Where the heading was, in the section's own coordinates at the start of the animation.
        copies.push({
          copy,
          x: (heading.r.left - was.left) / from.sc,
          y: (heading.r.top - was.bodyTop - shift) / from.sc,
          at: now,
        })
      }
      if (body && (was.capped || now.capped)) body.setAttribute('data-capped', '')
      // Growing back out, each section is laid out at its old, wider width first, and a section whose
      // height follows its width (a hero) would push the rest down. Its body keeps its final height
      // meanwhile; that stops a margin collapsing through its foot, which the slide makes up below.
      if (to.e === 0 && body) body.style.height = `${now.h}px`
      starts.push(() => {
        const base = laid[i]
        const fromY = was.top - push - base
        const toY = now.top - base - lift
        if (Math.abs(fromY) > 0.5 || Math.abs(toY) > 0.5)
          running.push(
            f.animate(
              [{ transform: `translateY(${fromY}px)` }, { transform: toY ? `translateY(${toY}px)` : 'none' }],
              timing,
            ),
          )
        running.push(
          f.animate(
            [
              { opacity: from.e, bottom: `${now.fh - was.fh}px` },
              { opacity: to.e, bottom: '0px' },
            ],
            {
              ...timing,
              pseudoElement: '::before',
            },
          ),
        )
        const head = f.querySelector<HTMLElement>(':scope > .layout-frame-head')
        if (head)
          running.push(
            head.animate(
              [
                { opacity: from.e, transform: `translate(${was.left - now.left}px, ${(was.head - now.head) / 2}px)` },
                { opacity: to.e, transform: 'none' },
              ],
              timing,
            ),
          )
        if (!body || !inner) return
        const dx = was.left - now.left
        const bd = was.bodyTop - was.top - (now.bodyTop - now.top)
        running.push(
          body.animate(
            [
              { transform: `translate(${dx}px, ${bd}px)`, clipPath: clip(was.w, was.h) },
              { transform: 'none', clipPath: clip(now.w, now.h) },
            ],
            timing,
          ),
        )
        if (was.capped || now.capped)
          running.push(
            body.animate(
              [
                { opacity: was.capped ? 1 : 0, transform: `translateY(${was.h - now.h}px)` },
                { opacity: now.capped ? 1 : 0, transform: 'none' },
              ],
              { ...timing, pseudoElement: '::after' },
            ),
          )
        // The section is laid out at the width that, at its scale, fills the frame at every step.
        running.push(
          inner.animate(
            [
              { transform: `translateY(${shift}px) scale(${from.sc})`, width: `${was.w / from.sc}px` },
              { transform: `translateY(0) scale(${to.sc})`, width: `${now.w / to.sc}px` },
            ],
            timing,
          ),
        )
      })
    })
    // Where each section sits while the animation runs, which is where its slide is measured from.
    const laid = to.e === 0 ? frames.map((f) => f.getBoundingClientRect().top) : after.map((a) => a.top)
    // The heading copies' containing block, read once for all of them, in the layout they animate in.
    const origins = copies.map(({ copy }) => copy.getBoundingClientRect())
    copies.forEach(({ copy, x, y, at }, k) => {
      const o = origins[k]
      copy.style.left = `${x - (o.left - at.left) / to.sc}px`
      copy.style.top = `${y - (o.top - at.bodyTop) / to.sc}px`
      starts.push(() =>
        running.push(copy.animate([{ opacity: 1 }, { opacity: 0, offset: 0.55 }, { opacity: 0 }], timing)),
      )
    })
    if (bar) starts.push(() => running.push(bar.animate([{ opacity: 1 - to.e }, { opacity: to.e }], timing)))
    for (const start of starts) start()
    void Promise.all(running.map((a) => a.finished)).then(settle, () => {})
  }

  // Edit mode comes in: the edit bar has just appeared above the sections, so the page scrolls by
  // its height to stay put, then the sections shrink from exactly how they looked. Going out, the
  // sections have already grown back; only the bar's room is given back.
  const wasEditing = useRef(false)
  useLayoutEffect(() => {
    if (editActive && !wasEditing.current) {
      // How far the bar pushed the sections down, margins collapsed and all. Read off the first
      // section's content: a margin collapsing through the frames can leave their own top edge apart
      // from it.
      const bar = barRef.current
      const c = framesRef.current
      if (bar && c) {
        const first = c.querySelector('.layout-frame-inner')?.firstElementChild ?? c
        const withBar = first.getBoundingClientRect().top
        bar.style.display = 'none'
        barSpace.current = withBar - first.getBoundingClientRect().top
        bar.style.display = ''
      }
      const scrolled = pageScrollTop()
      if (scrolled > 0) scrollPageTo(scrolled + barSpace.current)
      leaving.current = false
      view.current = FULL
      setLayout(FULL, 1)
      restHeights()
      // Where the page could not scroll to make room for the bar, the sections start that much higher.
      shrinkTo(EDIT, undefined, barSpace.current - (pageScrollTop() - scrolled))
    } else if (!editActive && wasEditing.current) {
      anim.current?.cancel()
      release()
      teardownDrag()
      setMoving(null)
      view.current = FULL
      setLayout(FULL, 1)
      framesRef.current?.removeAttribute('data-boxed')
      framesRef.current?.removeAttribute('data-trim')
      framesRef.current?.removeAttribute('inert')
      restHeights()
      // The section that was at the middle of the screen stays there as the bar goes. Measured rather
      // than worked out from the bar's height: margins collapse around the bar in ways that move the
      // page, and the browser's scroll anchoring with it, after any earlier measurement.
      const hold = exitHold.current
      exitHold.current = null
      if (hold && hold.el.isConnected) {
        const d = hold.el.getBoundingClientRect().top - hold.top
        if (Math.abs(d) > 0.5) scrollPageTo(pageScrollTop() + d)
      } else if (pageScrollTop() > 0) scrollPageTo(pageScrollTop() - barSpace.current)
      setSelected(null)
    }
    wasEditing.current = editActive
    // Runs when edit mode starts or ends.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [editActive])
  // Also runs when a kept page is hidden, so a drag never outlives the page it started on.
  useLayoutEffect(
    () => () => {
      anim.current?.cancel()
      release()
      teardownDrag()
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [],
  )

  // Done and Cancel grow the sections back before handing the page back. Started straight from the
  // click rather than through state, so no section re-renders while it runs.
  const leave = () => {
    if (leaving.current) return
    leaving.current = true
    setSelected(null)
    // Nothing on the sections answers while they grow back; set here rather than through state so
    // no section re-renders mid-animation.
    framesRef.current?.setAttribute('inert', '')
    shrinkTo(FULL, onExit)
  }
  useLayoutEffect(() => {
    if (!leaveAfterRender.current) return
    leaveAfterRender.current = false
    leave()
  })

  // Covers and rails arrive after the frame does; keep each cut height in step with its content.
  const observer = useRef<ResizeObserver | null>(null)
  useLayoutEffect(() => {
    observer.current ??= new ResizeObserver(() => {
      if (editRef.current && !anim.current && !dragRef.current) restHeights()
    })
    for (const el of frameEls()) {
      const { inner } = partsOf(el)
      if (inner) observer.current.observe(inner)
    }
    if (editRef.current && !anim.current && !dragRef.current) restHeights()
  })
  useEffect(() => () => observer.current?.disconnect(), [])

  // --- Moving by keyboard and the settings panel: the moved section holds its place on screen and
  // its neighbours slide past it.

  const move = (from: number, to: number, animate = true) => {
    if (to < 0 || to >= sections.length || from === to) return
    if (animate) {
      const els = frameEls()
      flip.current = {
        before: new Map(els.map((el) => [el.dataset.layoutKey ?? '', el.getBoundingClientRect().top])),
        keep: sections[from].key,
        // Moving a focused element in the DOM blurs it; the grip gets focus back after the move.
        focus: els[from]?.contains(document.activeElement) ?? false,
      }
    }
    draft.move(from, to)
    const label = labelOf(sections[from].key)
    const position = to + 1
    const total = sections.length
    setAnnouncement(t`${label} moved to position ${position} of ${total}`)
  }

  const clearDragStyles = () => {
    const root = document.documentElement
    root.dataset.layoutNoAnim = ''
    for (const el of frameEls()) {
      el.style.transform = ''
      el.style.transformOrigin = ''
      el.classList.remove('is-lifted', 'is-dropping')
    }
    if (slotRef.current) slotRef.current.hidden = true
    delete root.dataset.layoutCarry
    void framesRef.current?.offsetHeight
    delete root.dataset.layoutNoAnim
  }

  useLayoutEffect(() => {
    if (clearAfterCommit.current) {
      clearAfterCommit.current = false
      clearDragStyles()
      restHeights()
      runAfterDrop()
    }
    const f = flip.current
    if (!f) return
    flip.current = null
    const keep = frameEls().find((el) => el.dataset.layoutKey === f.keep)
    if (f.focus && keep && !keep.contains(document.activeElement))
      keep.querySelector<HTMLElement>('.layout-frame-grip')?.focus({ preventScroll: true })
    const was = f.before.get(f.keep)
    if (keep && was != null) scrollPageTo(pageScrollTop() + keep.getBoundingClientRect().top - was)
    if (reduceMotion) return
    for (const el of frameEls()) {
      const before = f.before.get(el.dataset.layoutKey ?? '')
      if (before == null) continue
      const d = before - el.getBoundingClientRect().top
      if (Math.abs(d) > 1)
        el.animate([{ transform: `translateY(${d}px)` }, { transform: 'none' }], {
          duration: 280,
          easing: 'cubic-bezier(0.16, 1, 0.3, 1)',
        })
    }
    if (keep) {
      keep.classList.add('is-moved')
      window.setTimeout(() => keep.classList.remove('is-moved'), 320)
    }
    // Runs after the draft reorders.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sections])

  // --- Dragging ----------------------------------------------------------------------------------

  const slotTops = (d: Pick<Drag, 'listTop' | 'others' | 'gap'>) => {
    const s = [d.listTop]
    for (const o of d.others) s.push(s[s.length - 1] + o.h + d.gap)
    return s
  }
  const applyShifts = (d: Drag) => {
    d.others.forEach((o, j) => {
      const top = d.s[j] + (j >= d.k ? d.me.h + d.gap : 0)
      const dy = Math.round(top - o.top)
      if (o.dy !== dy) {
        o.dy = dy
        o.el.style.transform = dy ? `translate3d(0,${dy}px,0)` : ''
      }
    })
    if (slotRef.current) slotRef.current.style.transform = `translate3d(0,${d.s[d.k] - d.containerTop}px,0)`
    setMoving({ key: d.me.key, position: d.k + 1 })
  }
  const updateDrag = () => {
    const d = dragRef.current
    if (!d || d.dropping) return
    const top = clamp(d.py + pageScrollTop() - d.grabY, d.listTop - 28, d.listTop + d.total - d.me.h + 28)
    // The section goes to the slot whose top is nearest its own top, which is also where it lands.
    let best = 0
    let bestDist = Infinity
    d.s.forEach((slot, k) => {
      const dist = Math.abs(top - slot)
      if (dist < bestDist) {
        bestDist = dist
        best = k
      }
    })
    if (best !== d.k && Math.abs(top - d.s[d.k]) - bestDist < 12) best = d.k
    if (best !== d.k) {
      d.k = best
      applyShifts(d)
    }
    d.top = top
    const dx = clamp((d.px - d.startX) * 0.06, -10, 10)
    d.me.el.style.transform = `translate3d(${dx / LIFT}px,${(top - d.me.top) / LIFT}px,0)`
  }
  const dragTick = (time: number) => {
    const d = dragRef.current
    if (!d || d.dropping) return
    const dt = Math.min(48, time - d.last)
    d.last = time
    const topEdge = barBottom() + EDGE
    const bottomEdge = pageViewportHeight() - EDGE
    // A section grabbed inside an edge zone does not scroll until the pointer heads for that edge.
    if (d.py >= topEdge || d.py < d.startY - 24) d.armTop = true
    if (d.py <= bottomEdge || d.py > d.startY + 24) d.armBottom = true
    let into = 0
    if (d.py < topEdge && d.armTop) into = -(topEdge - d.py) / EDGE
    else if (d.py > bottomEdge && d.armBottom) into = (d.py - bottomEdge) / EDGE
    if (into !== 0) {
      if (!d.edgeSince) d.edgeSince = time
      const p = Math.min(1, Math.abs(into))
      // Slow at the lip of the zone, fast at the screen edge, faster still when held there.
      const speed = (240 + 1500 * p * p) * (1 + Math.min(1, (time - d.edgeSince) / 900) * 0.9)
      const next = clamp(pageScrollTop() + (Math.sign(into) * speed * dt) / 1000, 0, maxScroll())
      if (Math.abs(next - pageScrollTop()) >= 0.5) scrollPageTo(next)
    } else d.edgeSince = 0
    updateDrag()
    d.raf = requestAnimationFrame(dragTick)
  }
  const startDrag = (key: string, x: number, y: number, e: PointerEvent) => {
    setSelected(null)
    anim.current?.finish()
    const scroll = pageScrollTop()
    const slabs: Slab[] = frameEls().map((el) => {
      const r = el.getBoundingClientRect()
      return {
        el,
        key: el.dataset.layoutKey ?? '',
        top: r.top + scroll,
        h: r.height,
        left: r.left,
        dy: 0,
      }
    })
    const from = slabs.findIndex((s) => s.key === key)
    if (from < 0) return
    const me = slabs[from]
    const others = slabs.filter((_, i) => i !== from)
    const gap = slabs.length > 1 ? slabs[1].top - slabs[0].top - slabs[0].h : 0
    const containerTop = (framesRef.current?.getBoundingClientRect().top ?? 0) + scroll
    const d: Drag = {
      me,
      others,
      from,
      k: from,
      s: [],
      gap,
      listTop: slabs[0].top,
      containerTop,
      total: slabs.reduce((sum, s) => sum + s.h, 0) + gap * (slabs.length - 1),
      grabY: y + scroll - me.top,
      startX: x,
      startY: y,
      px: e.clientX,
      py: e.clientY,
      top: me.top,
      armTop: false,
      armBottom: false,
      last: performance.now(),
      edgeSince: 0,
      raf: 0,
      dropping: false,
    }
    d.s = slotTops(d)
    if (slotRef.current) {
      slotRef.current.style.height = `${me.h}px`
      slotRef.current.hidden = false
    }
    me.el.style.transformOrigin = `${x - me.left}px ${d.grabY}px`
    me.el.classList.add('is-lifted')
    document.documentElement.dataset.layoutCarry = ''
    dragRef.current = d
    applyShifts(d)
    updateDrag()
    d.raf = requestAnimationFrame(dragTick)
  }
  const endDrag = (commit: boolean) => {
    const d = dragRef.current
    if (!d || d.dropping) return
    d.dropping = true
    cancelAnimationFrame(d.raf)
    if (!commit && d.k !== d.from) {
      d.k = d.from
      applyShifts(d)
    }
    const dest = d.s[d.k]
    const duration = reduceMotion ? 0 : clamp(140 + Math.abs(d.top - dest) * 0.25, 160, 300)
    const el = d.me.el
    el.style.setProperty('--drop', `${duration}ms`)
    el.classList.add('is-dropping')
    el.classList.remove('is-lifted')
    el.style.transform = `translate3d(0,${dest - d.me.top}px,0)`
    dragEndedAt.current = Date.now()
    dropTimer.current = window.setTimeout(
      () => {
        dragRef.current = null
        setMoving(null)
        dragEndedAt.current = Date.now()
        // The transforms already show the new order, so writing it moves nothing on screen and the
        // page's scroll position is untouched.
        if (d.k !== d.from) {
          clearAfterCommit.current = true
          move(d.from, d.k, false)
        } else {
          clearDragStyles()
          restHeights()
          runAfterDrop()
        }
      },
      Math.max(duration, 200) + 20,
    )
  }
  /** Ends a drag at once, wherever it is, with nothing committed. */
  const teardownDrag = () => {
    dragStop.current?.()
    dragStop.current = null
    window.clearTimeout(dropTimer.current)
    afterDrop.current = null
    const d = dragRef.current
    if (d) cancelAnimationFrame(d.raf)
    if (d || document.documentElement.dataset.layoutCarry != null) {
      dragRef.current = null
      clearDragStyles()
    }
  }
  const runAfterDrop = () => {
    const next = afterDrop.current
    afterDrop.current = null
    if (next === 'done') done()
    else if (next === 'cancel') cancel()
  }

  const press = (key: string, event: ReactPointerEvent) => {
    if (!editRef.current || anim.current || dragRef.current || leaving.current) return
    const startX = event.clientX
    const startY = event.clientY
    const id = event.pointerId
    let started = false
    const onMove = (e: PointerEvent) => {
      if (e.pointerId !== id) return
      if (!started) {
        if (Math.hypot(e.clientX - startX, e.clientY - startY) < DRAG_THRESHOLD) return
        started = true
        startDrag(key, startX, startY, e)
        return
      }
      e.preventDefault()
      const d = dragRef.current
      if (!d) return
      d.px = e.clientX
      d.py = e.clientY
      updateDrag()
    }
    const detach = () => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', stop)
      window.removeEventListener('pointercancel', stop)
      dragStop.current = null
    }
    const stop = (e: PointerEvent) => {
      if (e.pointerId !== id) return
      detach()
      if (started) endDrag(e.type === 'pointerup')
    }
    window.addEventListener('pointermove', onMove, { passive: false })
    window.addEventListener('pointerup', stop)
    window.addEventListener('pointercancel', stop)
    dragStop.current = detach
  }

  useEffect(() => {
    if (!moving) return
    const off = onPageScroll(updateDrag)
    const onKey = (e: globalThis.KeyboardEvent) => {
      if (e.key === 'Escape') endDrag(false)
    }
    window.addEventListener('keydown', onKey)
    return () => {
      off()
      window.removeEventListener('keydown', onKey)
    }
    // Bound for the length of one drag.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [moving != null])

  // --- Rails -------------------------------------------------------------------------------------

  // A rail made from "Add a rail here" lands where it was asked for once the rails list has it.
  useEffect(() => {
    if (!pendingRail) return
    const key = `rail:${pendingRail.id}`
    const from = sections.findIndex((s) => s.key === key)
    if (from < 0) return
    setPendingRail(null)
    if (pendingRail.at != null && pendingRail.at !== from)
      draft.move(from, Math.min(pendingRail.at, sections.length - 1))
    setScrollKey(key)
  }, [pendingRail, sections, draft])

  useEffect(() => {
    if (!scrollKey) return
    document.querySelector(`[data-layout-key="${CSS.escape(scrollKey)}"]`)?.scrollIntoView({
      block: 'nearest',
      behavior: reduceMotion ? 'auto' : 'smooth',
    })
    setScrollKey(null)
  }, [scrollKey, reduceMotion])

  const addDraft: CustomRailDraft = {
    placement,
    spec: { source: placement === 'home' ? 'library' : 'recommendations' },
  }
  const onRailCreated = (rail: CustomRail) => {
    setCreated((prev) => new Set(prev).add(rail.id))
    setPendingRail({ id: rail.id, at: adding?.at ?? null })
  }

  // --- Leaving -----------------------------------------------------------------------------------

  // A section dropped a moment ago is still settling into its slot; its move counts, so Done and
  // Cancel wait for it.
  const waitForDrop = (then: 'done' | 'cancel') => {
    const d = dragRef.current
    if (!d) return false
    afterDrop.current = then
    if (!d.dropping) endDrag(then === 'done')
    return true
  }
  const done = () => {
    if (leaving.current || saving.current || waitForDrop('done')) return
    if (!dirty) {
      leave()
      return
    }
    saving.current = true
    setSelected(null)
    save.mutate(
      { page, sections },
      {
        onSuccess: () => {
          notifications.show({
            color: 'var(--ok)',
            message: now`Layout saved`,
          })
          leave()
        },
        onSettled: () => {
          saving.current = false
        },
      },
    )
  }
  // Nothing changes the draft while it is being saved: a change made then would be dropped when the
  // editor closes.
  useLayoutEffect(() => {
    if (!leaving.current) framesRef.current?.toggleAttribute('inert', save.isPending)
  }, [save.isPending])
  const cancel = () => {
    if (leaving.current || saving.current || waitForDrop('cancel')) return
    if (!dirty) {
      leave()
      return
    }
    leaveAfterRender.current = true
    draft.set(draft.baseline)
  }

  const selectedIndex = selected ? sections.findIndex((s) => s.key === selected.key) : -1
  const selectedSection = selectedIndex >= 0 ? sections[selectedIndex] : null
  const settingsFor = (section: PageSection, index: number, asSheet: boolean) => (
    <SectionSettings
      key={section.key}
      section={section}
      index={index}
      total={sections.length}
      def={registry[section.key]}
      rail={railOf(section.key)}
      panelHint={panelHint}
      optionHint={optionHint}
      sheet={asSheet}
      onChange={(patch) => draft.update(section.key, patch)}
      onMove={(to) => move(index, to)}
      onEditRail={() => {
        setSelected(null)
        setEditingRail(railOf(section.key) ?? null)
      }}
      onDeleteRail={() => {
        setSelected(null)
        setDeletingRail(railOf(section.key) ?? null)
      }}
    />
  )

  const movingLabel = moving ? labelOf(moving.key) : ''
  const movingPosition = moving?.position ?? 0
  const total = sections.length

  return (
    <>
      {editActive && (
        <div className="layout-edit-bar" ref={barRef}>
          <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
            <IconLayoutDashboard size={18} />
            <div style={{ minWidth: 0 }}>
              <Text fw={600} size="sm">
                <Trans>Editing layout</Trans>
              </Text>
              <Text size="xs" c="var(--ink-3)" truncate>
                {moving ? (
                  <Trans>
                    Moving {movingLabel}, {movingPosition} of {total}
                  </Trans>
                ) : (
                  <Trans>Drag a section to move it. Select one to change it.</Trans>
                )}
              </Text>
            </div>
          </Group>
          <Group gap="xs" wrap="nowrap">
            {dirty && (
              <Text size="xs" c="var(--ink-3)" className="tnum" visibleFrom="sm">
                <Plural value={draft.changes} one="# change" other="# changes" />
              </Text>
            )}
            <Button
              variant="subtle"
              size="xs"
              leftSection={<IconPlus size={16} />}
              onClick={() => setAdding({ at: null })}
              disabled={save.isPending}
            >
              <Trans>Add a rail</Trans>
            </Button>
            <Button variant="default" size="xs" onClick={cancel} disabled={save.isPending}>
              <Trans>Cancel</Trans>
            </Button>
            <Button size="xs" onClick={done} loading={save.isPending}>
              <Trans>Done</Trans>
            </Button>
          </Group>
        </div>
      )}

      <div className="layout-frames" ref={framesRef} data-editing={editActive || undefined}>
        <div className="layout-drop-slot" ref={slotRef} hidden aria-hidden />
        {sections.map((section, index) => {
          const blocked = unavailable?.(section.key)
          const rail = railOf(section.key)
          const open = selected?.key === section.key
          return (
            <Fragment key={section.key}>
              {editActive && index > 0 && <RailInserter onAdd={() => setAdding({ at: index })} />}
              <LayoutFrame
                editing={editActive}
                section={section}
                index={index}
                total={sections.length}
                label={labelOf(section.key)}
                icon={iconOf(section.key)}
                summary={summaryOf(section.key)}
                rail={rail}
                railLimit={railLimit}
                preview={preview}
                keepHeadings={registry[section.key]?.keepHeadings ?? false}
                isNew={rail != null && created.has(rail.id)}
                unavailable={blocked ? renderLabel(blocked) : null}
                anchor={open && !sheet ? selected : null}
                settings={open && !sheet && selectedSection ? settingsFor(selectedSection, selectedIndex, false) : null}
                onOpen={(x, y, w, h) => {
                  if (Date.now() - dragEndedAt.current < 350) return
                  setSelected({ key: section.key, x, y, w, h })
                }}
                onClose={() => setSelected(null)}
                onToggle={() => draft.update(section.key, { enabled: !section.enabled })}
                onStep={(delta) => move(index, index + delta)}
                onPress={(event) => press(section.key, event)}
              >
                {render(section.key)}
              </LayoutFrame>
            </Fragment>
          )
        })}
      </div>
      {editActive && (
        <button
          type="button"
          className="layout-edit-add"
          onClick={() => setAdding({ at: null })}
          disabled={save.isPending}
        >
          <IconPlus size={16} />
          <Trans>Add a rail</Trans>
        </button>
      )}

      <VisuallyHidden aria-live="polite">{announcement}</VisuallyHidden>

      <Drawer
        opened={sheet && selectedSection != null}
        onClose={() => setSelected(null)}
        position="bottom"
        size="auto"
        title={selectedSection ? <ModalHeading title={labelOf(selectedSection.key)} /> : null}
        classNames={{ content: 'layout-settings-sheet' }}
        closeButtonProps={{ 'aria-label': t`Close` }}
        styles={{
          content: { height: 'auto', maxHeight: '85dvh', flex: '0 0 auto' },
        }}
      >
        {sheet && selectedSection && settingsFor(selectedSection, selectedIndex, true)}
      </Drawer>

      {adding && <CustomRailEditor draft={addDraft} onSaved={onRailCreated} onClose={() => setAdding(null)} />}
      {editingRail && <CustomRailEditor rail={editingRail} onClose={() => setEditingRail(null)} />}
      <DeleteRailDialog
        opened={deletingRail != null}
        onClose={() => setDeletingRail(null)}
        railId={deletingRail?.id}
        railName={deletingRail?.name ?? ''}
        onDeleted={() => setSelected(null)}
      />
    </>
  )
}

function useCovers(
  key: string,
  rail: CustomRail | undefined,
  railLimit: number,
  preview: ((key: string) => (string | null)[]) | undefined,
): string[] {
  // Cache only: a hidden section's strip never costs a request.
  const { data: railItems } = useCustomRailItems(rail?.id ?? 0, railLimit, false)
  return (rail ? (railItems?.items ?? []).map((i) => i.thumbUrl ?? i.coverUrl) : (preview?.(key) ?? []))
    .filter((u): u is string => !!u)
    .slice(0, 8)
}

function Covers({ urls }: { urls: string[] }) {
  if (urls.length === 0) return null
  return (
    <span className="layout-covers" aria-hidden>
      {urls.map((src, i) => (
        <img key={i} src={src} alt="" loading="lazy" />
      ))}
    </span>
  )
}

function Badges({
  section,
  isNew,
  unavailable,
}: {
  section: PageSection
  isNew?: boolean
  unavailable?: string | null
}) {
  const panelsOn = section.panels?.filter((p) => p.enabled).length
  const panelsTotal = section.panels?.length
  return (
    <>
      {!sectionVisible(section) && (
        <span className="layout-edit-badge">
          <Trans>Hidden</Trans>
        </span>
      )}
      {section.hero && (
        <span className="layout-edit-badge" data-tone="brand">
          <Trans>Hero</Trans>
        </span>
      )}
      {panelsTotal != null && panelsOn !== panelsTotal && (
        <span className="layout-edit-badge">
          <Trans>
            {panelsOn} of {panelsTotal} panels
          </Trans>
        </span>
      )}
      {unavailable && (
        <span className="layout-edit-badge" data-tone="warn">
          {unavailable}
        </span>
      )}
      {isNew && (
        <span className="layout-edit-badge" data-tone="brand">
          <Trans>New</Trans>
        </span>
      )}
    </>
  )
}

/** A section on the page. In edit mode: its title strip, then the section itself, inert. */
function LayoutFrame({
  editing,
  section,
  index,
  total,
  label,
  icon: SectionIcon,
  summary,
  rail,
  railLimit,
  preview,
  keepHeadings,
  isNew,
  unavailable,
  anchor,
  settings,
  onOpen,
  onClose,
  onToggle,
  onStep,
  onPress,
  children,
}: {
  editing: boolean
  section: PageSection
  index: number
  total: number
  label: string
  icon: Icon | undefined
  summary: ReactNode
  rail: CustomRail | undefined
  railLimit: number
  preview: ((key: string) => (string | null)[]) | undefined
  keepHeadings: boolean
  isNew: boolean
  unavailable: string | null
  anchor: Selection | null
  settings: ReactNode
  onOpen: (x: number, y: number, w?: number, h?: number) => void
  onClose: () => void
  onToggle: () => void
  onStep: (delta: -1 | 1) => void
  onPress: (event: ReactPointerEvent) => void
  children: ReactNode
}) {
  const { t } = useLingui()
  const ref = useRef<HTMLElement>(null)
  const settingsRef = useRef<HTMLButtonElement>(null)
  const titleId = useId()
  const visible = sectionVisible(section)
  const covers = useCovers(section.key, rail, railLimit, visible ? undefined : preview)

  const openAt = (clientX: number, clientY: number) => {
    const box = ref.current?.getBoundingClientRect()
    if (!box) return
    onOpen(clientX - box.left, clientY - box.top)
  }
  // The panel hangs from the button itself, so it flips above the button rather than over it.
  const openFromButton = () => {
    const button = settingsRef.current?.getBoundingClientRect()
    const box = ref.current?.getBoundingClientRect()
    if (!button || !box) return
    onOpen(button.left - box.left, button.top - box.top, button.width, button.height)
  }
  // Closing from inside the panel puts focus back on the settings button; a click elsewhere leaves
  // focus where it went.
  const closeToButton = () => {
    onClose()
    settingsRef.current?.focus({ preventScroll: true })
  }
  // Hiding swaps the hide button for a Show button; focus moves across with it.
  const toggleFocus = useRef(false)
  const toggle = () => {
    toggleFocus.current = ref.current?.contains(document.activeElement) ?? false
    onToggle()
  }
  useLayoutEffect(() => {
    if (!toggleFocus.current) return
    toggleFocus.current = false
    ref.current?.querySelector<HTMLElement>('[data-layout-toggle]')?.focus({ preventScroll: true })
  }, [visible])

  const onGripKey = (e: KeyboardEvent) => {
    if (e.key === 'ArrowUp' && index > 0) {
      e.preventDefault()
      onStep(-1)
    } else if (e.key === 'ArrowDown' && index < total - 1) {
      e.preventDefault()
      onStep(1)
    }
  }

  return (
    <section
      ref={ref}
      className="layout-frame"
      data-layout-key={section.key}
      data-hidden={!visible || undefined}
      data-selected={anchor != null || undefined}
      data-own-heading={!keepHeadings || undefined}
      aria-label={editing ? label : undefined}
      onPointerDown={(e) => {
        // A portal's events bubble through React: ignore the settings panel's own presses.
        if (!editing || e.button !== 0 || !e.currentTarget.contains(e.target as Node)) return
        const target = e.target as HTMLElement
        const onGrip = target.closest('.layout-frame-grip') != null
        if (!onGrip && target.closest('button, a, input')) return
        // On touch the section scrolls the page; only the grip picks it up.
        if (e.pointerType !== 'mouse' && !onGrip) return
        if (e.pointerType === 'mouse') e.preventDefault()
        onPress(e)
      }}
      onClick={(e) => {
        if (!editing || !e.currentTarget.contains(e.target as Node)) return
        if ((e.target as HTMLElement).closest('button, a, input')) return
        openAt(e.clientX, e.clientY)
      }}
    >
      {editing && (
        <div className="layout-frame-head">
          <button
            type="button"
            className="layout-frame-grip"
            aria-label={t`Move ${label}`}
            aria-keyshortcuts="ArrowUp ArrowDown"
            onKeyDown={onGripKey}
          >
            <IconGripVertical size={16} />
          </button>
          {SectionIcon && (
            <span className="layout-frame-icon" aria-hidden>
              <SectionIcon size={16} />
            </span>
          )}
          <span className="layout-frame-name">{label}</span>
          <Badges section={section} isNew={isNew} unavailable={unavailable} />
          {summary && <span className="layout-frame-summary">{summary}</span>}
          <span className="layout-frame-end">
            {!visible && <Covers urls={covers} />}
            {visible ? (
              <Tooltip label={t`Hide`} withArrow>
                <ActionIcon
                  variant="subtle"
                  color="var(--ink-2)"
                  aria-label={t`Hide ${label}`}
                  onClick={toggle}
                  data-layout-toggle
                >
                  <IconEyeOff size={16} />
                </ActionIcon>
              </Tooltip>
            ) : (
              <Button
                size="xs"
                variant="default"
                leftSection={<IconEye size={16} />}
                onClick={toggle}
                aria-label={t`Show ${label}`}
                data-layout-toggle
              >
                <Trans>Show</Trans>
              </Button>
            )}
            <Tooltip label={t`Settings`} withArrow>
              <ActionIcon
                ref={settingsRef}
                variant="default"
                aria-label={t`Settings for ${label}`}
                aria-expanded={anchor != null}
                onClick={openFromButton}
              >
                <IconSettings size={16} />
              </ActionIcon>
            </Tooltip>
          </span>
        </div>
      )}

      {visible && (
        <div className="layout-frame-body" inert={editing}>
          <div className="layout-frame-inner" data-empty={t`Nothing to show right now`}>
            <SectionErrorBoundary>{children}</SectionErrorBoundary>
          </div>
        </div>
      )}

      {anchor && (
        <Popover
          opened
          onChange={(opened) => {
            if (!opened) onClose()
          }}
          closeOnEscape={false}
          position={anchor.w != null ? 'bottom-end' : 'bottom-start'}
          offset={6}
          width={320}
          shadow="md"
          trapFocus
          withinPortal
          middlewares={{ flip: true, shift: { padding: 8 } }}
        >
          <Popover.Target>
            <span
              className="layout-frame-anchor"
              style={{ left: Math.max(0, anchor.x), top: anchor.y, width: anchor.w, height: anchor.h }}
            />
          </Popover.Target>
          <Popover.Dropdown
            p={6}
            aria-labelledby={titleId}
            className="layout-settings-pop"
            onKeyDownCapture={(e) => {
              if (e.key !== 'Escape') return
              e.stopPropagation()
              closeToButton()
            }}
          >
            <div className="layout-settings-head">
              {SectionIcon && (
                <span className="layout-frame-icon" aria-hidden>
                  <SectionIcon size={16} />
                </span>
              )}
              <span id={titleId} className="layout-settings-title" tabIndex={-1} data-autofocus>
                {label}
              </span>
              <ActionIcon variant="subtle" color="var(--ink-2)" aria-label={t`Close`} onClick={closeToButton}>
                <IconX size={16} />
              </ActionIcon>
            </div>
            {settings}
          </Popover.Dropdown>
        </Popover>
      )}
    </section>
  )
}

/** "Add a rail here", in the gap between two sections. Shows while the pointer is in the gap. */
function RailInserter({ onAdd }: { onAdd: () => void }) {
  return (
    <div className="layout-insert">
      <button type="button" className="layout-insert-button" onClick={onAdd}>
        <IconPlus size={16} />
        <Trans>Add a rail here</Trans>
      </button>
    </div>
  )
}

/** Shown while the saved layout is still loading, so editing never starts from a guess. */
export function LayoutEditLoading({ failed, onExit }: { failed?: boolean; onExit: () => void }) {
  if (failed) {
    return (
      <Group justify="center" py="xl" gap="sm">
        <Text size="sm" c="dimmed">
          <Trans>The layout could not be loaded.</Trans>
        </Text>
        <Button variant="default" size="xs" onClick={onExit}>
          <Trans>Exit editing</Trans>
        </Button>
      </Group>
    )
  }
  return (
    <Group justify="center" py="xl">
      <Loader size="sm" />
    </Group>
  )
}
