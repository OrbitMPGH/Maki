# TipLayer

One delegated tooltip for the whole app: any element carrying `data-tip="..."` gets it on hover or keyboard focus.

Mounted once, in App. A library grid renders several hundred cards, and giving each its own Mantine `<Tooltip>` meant hundreds of floating-ui instances sitting idle. Delegating costs one listener and one node however many targets are on screen.

**Look:** Mantine's tooltip, `radius-md`, 5px 10px padding, `body` size, max 260px wide, with an 8px arrow that keeps pointing at the target even when the bubble is clamped 8px off a viewport edge. Dark themes draw Mantine gray-2 with black text; Light draws gray-9 with white. It sits above the target and flips below when there is less than 44px above. It fades in over 100ms and fades out still showing its old text.

**It dismisses** on pointer down, focus out, window blur and any scroll, including inside nested scrollers.

**The consumer provides:** nothing but a translated `data-tip` string on the element. Use it for icon-only badges ("Monitored", "Notifications muted"), counts that need their unit ("4 unread"), and short explanations. Mantine's `Tooltip` is still fine for a one-off control outside a big list.
