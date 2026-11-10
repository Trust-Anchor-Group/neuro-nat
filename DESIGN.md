---
name: Neuro.AA
description: A calm, dense operator console for running many Neurons at once, in designed light and dark.
colors:
  accent: "#3358D4"
  accent-hover: "#2A4BBE"
  accent-subtle: "#EAEFFC"
  accent-ink: "#2440A8"
  accent-dark: "#7B9BFF"
  accent-hover-dark: "#94AEFF"
  accent-subtle-dark: "#1C2541"
  accent-ink-dark: "#B4C6FF"
  surface-ground: "#F5F6F8"
  surface-panel: "#FFFFFF"
  surface-hover: "#F0F2F5"
  surface-pressed: "#E7EAEE"
  surface-rail: "#ECEEF2"
  surface-code: "#FAFBFC"
  surface-ground-dark: "#101215"
  surface-panel-dark: "#16191D"
  surface-hover-dark: "#1D2126"
  surface-pressed-dark: "#242930"
  surface-rail-dark: "#0C0E11"
  surface-code-dark: "#121519"
  line: "#E2E5EA"
  line-strong: "#CDD2D9"
  line-dark: "#252A31"
  line-strong-dark: "#353C46"
  ink-primary: "#12161C"
  ink-secondary: "#545D6B"
  ink-tertiary: "#6B7480"
  ink-on-accent: "#FFFFFF"
  ink-primary-dark: "#E6E9ED"
  ink-secondary-dark: "#A3ABB6"
  ink-tertiary-dark: "#838B96"
  ink-on-accent-dark: "#0B0D10"
  state-ok: "#1E8A4C"
  state-busy: "#B26B00"
  state-bad: "#C8312B"
  state-bad-subtle: "#FCEBEA"
  state-idle: "#8A929E"
  state-ok-dark: "#45C27D"
  state-busy-dark: "#E5A63F"
  state-bad-dark: "#F26D64"
  state-bad-subtle-dark: "#2E1A1A"
  state-idle-dark: "#6E7682"
  severity-debug: "#8A929E"
  severity-informational: "#545D6B"
  severity-notice: "#3358D4"
  severity-warning: "#B26B00"
  severity-error: "#C8312B"
  severity-critical: "#A11A4B"
  severity-alert: "#8E1B6E"
  severity-emergency: "#6B1D8F"
  severity-debug-dark: "#6E7682"
  severity-informational-dark: "#A3ABB6"
  severity-notice-dark: "#7B9BFF"
  severity-warning-dark: "#E5A63F"
  severity-error-dark: "#F26D64"
  severity-critical-dark: "#F0608F"
  severity-alert-dark: "#D06BD0"
  severity-emergency-dark: "#B488FF"
  account-hue0: "#3B6FE0"
  account-hue1: "#0F9488"
  account-hue2: "#7C4DDB"
  account-hue3: "#C27C0E"
  account-hue4: "#D23F6C"
  account-hue5: "#3A8F3A"
  account-hue6: "#0C86B5"
  account-hue7: "#D2601A"
  account-hue0-dark: "#6E98FF"
  account-hue1-dark: "#36C2B4"
  account-hue2-dark: "#A887FF"
  account-hue3-dark: "#E7A93C"
  account-hue4-dark: "#F07399"
  account-hue5-dark: "#6CC46C"
  account-hue6-dark: "#4BB8E6"
  account-hue7-dark: "#F08A4B"
typography:
  title:
    fontFamily: "Inter, sans-serif"
    fontSize: "18px"
    fontWeight: 600
    letterSpacing: "-0.2px"
  section:
    fontFamily: "Inter, sans-serif"
    fontSize: "15px"
    fontWeight: 600
  body:
    fontFamily: "Inter, sans-serif"
    fontSize: "13px"
    fontWeight: 400
  body-strong:
    fontFamily: "Inter, sans-serif"
    fontSize: "13px"
    fontWeight: 600
  control:
    fontFamily: "Inter, sans-serif"
    fontSize: "13px"
    fontWeight: 500
  meta:
    fontFamily: "Inter, sans-serif"
    fontSize: "12px"
    fontWeight: 400
  label:
    fontFamily: "Inter, sans-serif"
    fontSize: "12px"
    fontWeight: 500
  caption:
    fontFamily: "Inter, sans-serif"
    fontSize: "11px"
    fontWeight: 500
  mono:
    fontFamily: "Cascadia Mono, Consolas, SF Mono, Menlo, JetBrains Mono, DejaVu Sans Mono, monospace"
    fontSize: "12px"
    fontWeight: 400
  mono-tag:
    fontFamily: "Cascadia Mono, Consolas, SF Mono, Menlo, JetBrains Mono, DejaVu Sans Mono, monospace"
    fontSize: "11px"
    fontWeight: 700
rounded:
  base: "6px"
  pill: "999px"
spacing:
  hair: "2px"
  tight: "4px"
  inline: "6px"
  control-gap: "8px"
  stack: "10px"
  list-inset: "12px"
  panel-pad: "14px"
  section-gap: "16px"
  gutter: "20px"
components:
  button:
    backgroundColor: "{colors.surface-panel}"
    textColor: "{colors.ink-primary}"
    typography: "{typography.control}"
    rounded: "{rounded.base}"
    padding: "5px 10px"
    height: "30px"
  button-hover:
    backgroundColor: "{colors.surface-hover}"
  button-pressed:
    backgroundColor: "{colors.surface-pressed}"
  button-disabled:
    backgroundColor: "{colors.surface-panel}"
    textColor: "{colors.ink-tertiary}"
  button-primary:
    backgroundColor: "{colors.accent}"
    textColor: "{colors.ink-on-accent}"
    typography: "{typography.control}"
    rounded: "{rounded.base}"
    padding: "5px 10px"
    height: "30px"
  button-primary-hover:
    backgroundColor: "{colors.accent-hover}"
    textColor: "{colors.ink-on-accent}"
  button-primary-disabled:
    backgroundColor: "{colors.surface-pressed}"
    textColor: "{colors.ink-tertiary}"
  button-ghost:
    textColor: "{colors.ink-secondary}"
    typography: "{typography.control}"
    rounded: "{rounded.base}"
    padding: "5px 8px"
  button-ghost-hover:
    backgroundColor: "{colors.surface-hover}"
    textColor: "{colors.ink-primary}"
  button-danger:
    textColor: "{colors.state-bad}"
  button-danger-hover:
    backgroundColor: "{colors.state-bad-subtle}"
    textColor: "{colors.state-bad}"
  button-danger-armed:
    backgroundColor: "{colors.state-bad-subtle}"
    textColor: "{colors.state-bad}"
  toggle-checked:
    backgroundColor: "{colors.accent-subtle}"
    textColor: "{colors.accent-ink}"
  input:
    backgroundColor: "{colors.surface-panel}"
    textColor: "{colors.ink-primary}"
    typography: "{typography.body}"
    rounded: "{rounded.base}"
    padding: "6px 10px"
    height: "32px"
  list-row:
    rounded: "{rounded.base}"
    padding: "8px 10px"
  list-row-hover:
    backgroundColor: "{colors.surface-hover}"
  list-row-selected:
    backgroundColor: "{colors.accent-subtle}"
  stream-row:
    typography: "{typography.mono}"
    padding: "3px 14px"
  panel:
    backgroundColor: "{colors.surface-panel}"
    rounded: "{rounded.base}"
  rail-item:
    textColor: "{colors.ink-secondary}"
    typography: "{typography.caption}"
    rounded: "{rounded.base}"
    width: "60px"
    padding: "8px 0 7px"
  rail-item-hover:
    backgroundColor: "{colors.surface-hover}"
    textColor: "{colors.ink-primary}"
  rail-item-current:
    backgroundColor: "{colors.accent-subtle}"
    textColor: "{colors.accent-ink}"
  lens-chip:
    backgroundColor: "{colors.surface-ground}"
    textColor: "{colors.ink-primary}"
    typography: "{typography.label}"
    rounded: "{rounded.pill}"
    padding: "4px 10px 4px 4px"
  avatar:
    rounded: "{rounded.base}"
    size: "30px"
  status-dot:
    backgroundColor: "{colors.state-idle}"
    rounded: "{rounded.pill}"
    size: "8px"
  failure-banner:
    backgroundColor: "{colors.state-bad-subtle}"
    textColor: "{colors.ink-primary}"
    rounded: "{rounded.base}"
    padding: "10px 12px"
---

# Design System: Neuro.AA

## Overview

**Creative North Star: "The Quiet Console"**

Neuro.AA is a native Avalonia 12 desktop app for Windows, macOS and Linux, not a website. Its design system lives in XAML, not CSS: colour, type and shape tokens are resources in `src/Neuro.AA.App/Themes/Tokens.axaml`, and the restyled controls are in `src/Neuro.AA.App/Themes/Controls.axaml`, layered over Avalonia's FluentTheme, whose accent palette is pinned to the same cobalt. Views reference token keys only (`{DynamicResource Ink.Primary}`, `{StaticResource Radius}`), never raw colours. Every later workspace (Events, Fleet, Sources, Chat, Console, Topology, Identities) reuses these tokens and control styles and sits inside the shell recorded here. Hex values and px sizes in this file mirror the XAML. The XAML resource is the source of truth and this file records it.

The world is the category-standard operator console, chosen deliberately: the calm, dense layout the audience already knows, built to the standard of the category's best tools. Hierarchy comes from type weight, spacing and 1 px hairlines. Colour carries meaning only: connection state, selection and primary action, and which account something belongs to. Light mode is a cool near-white ground with white panels. Dark mode is graphite with panels one step lighter. Both are designed palettes; neither is an inversion of the other. The theme follows the OS by default (`RequestedThemeVariant="Default"`) and a rail button toggles it.

Density is high but never cramped. Body text is 13 px, controls are 30 to 32 px tall, list rows are tight, and the sniffer stream is a full-height monospace log. Operators keep this tool open for hours, so the resting state is quiet. The dashboard-of-cards layout and the neon-on-black "ops" costume are both rejected.

**Key Characteristics:**
- Flat, hairline-structured surfaces; no shadows anywhere.
- One cobalt accent, reserved for selection, primary action and keyboard focus.
- Eight account hues, used only to say which account something belongs to.
- Inter for all UI text; a system monospace only for JIDs, XML, endpoints and timestamps.
- One corner radius (6 px) on every rectangle; pills only for lens chips and status dots.
- Line icons drawn on a 24-unit grid with round caps, taking the surrounding text colour.
- Designed light and dark palettes, switched per theme through Avalonia ThemeDictionaries.

## Colors

The palette is cool neutrals plus one cobalt accent, with a separate state set, severity ramp and account-hue set, each with a single job. Every token has a light value and a `-dark` value. They are listed together below; use the one for the active theme variant.

### Primary
- **Signal Cobalt** (`Accent`, light/dark in frontmatter): the selected list row (as `Accent.Subtle`), the current rail item, the checked toggle, primary buttons, text-selection highlight, the focused input border and the 2 px keyboard focus ring. `Accent.Hover` is the hover fill for primary buttons. `Accent.Ink` is cobalt text on an `Accent.Subtle` tint (current rail item, checked toggle).

### Secondary
- **Account Hues** (`Account.Hue0` to `Account.Hue7`): blue, teal, violet, ochre, rose, green, sky, orange. An account's index picks its hue (wrapping modulo 8) through `AccountHueConverter`. The hue shows as the initial's text colour on a 16% tint of the same hue behind it (the converter's `subtle` parameter). This applies to list avatars (30 px), the detail header avatar (36 px) and the 20 px avatar inside each lens chip.

### Tertiary
- **Connection State** (`State.Ok` green, `State.Busy` amber, `State.Bad` red, `State.Idle` grey): status dots, error text and the destructive button. `State.Bad.Subtle` is the pale red behind the failure banner, the danger-hover fill and the armed Remove button.
- **Severity Ramp** (`Severity.Debug` through `Severity.Emergency`, eight steps): defined for the event workspaces from Phase 1. Nothing renders it yet. It runs from grey (debug) through ink, cobalt, amber and red into crimson, magenta and violet for the most severe levels. Notice shares the accent hex, and Warning and Error share the state hexes on purpose.

### Neutral
- **Ground** (`Surface.Ground`): the window background and the lens chip fill.
- **Panel** (`Surface.Panel`): the top bar, the detail header, bordered panels, buttons and inputs.
- **Rail** (`Surface.Rail`): the workspace rail. A step darker than ground in light mode and the darkest surface in dark mode.
- **Code** (`Surface.Code`): the inside of the sniffer stream panel.
- **Hover / Pressed** (`Surface.Hover`, `Surface.Pressed`): interaction fills for rows, buttons and rail items. `Surface.Pressed` is also the disabled primary fill.
- **Hairline / Strong Hairline** (`Line`, `Line.Strong`): `Line` for panel borders and dividers; `Line.Strong` for button and input borders and the scrollbar thumb.
- **Ink** (`Ink.Primary`, `Ink.Secondary`, `Ink.Tertiary`, `Ink.OnAccent`): primary for content, secondary for meta, labels and ghost buttons, tertiary for timestamps, endpoints, placeholders and disabled text. `Ink.OnAccent` is the text on primary buttons and selections; it is dark in dark mode because the dark accent is light.

### Named Rules
**The One Accent Rule.** Cobalt means "selected, primary or focused" and nothing else. Never use it for decoration, headings or links that are not the primary action.

**The Hue Belongs To The Account Rule.** Account hues appear only on account avatars and lens chips. They never mark state, severity or workspace identity. State is said by the state set and by words, never by hue alone.

**The Token-Only Rule.** Views reference token keys through `DynamicResource` (colours, so theme switches apply live) or `StaticResource` (type sizes, radius, fonts, icons). No hex appears in a view.

## Typography

**UI Font:** Inter (bundled through `Avalonia.Fonts.Inter`, so every OS renders the same face)
**Mono Font:** Cascadia Mono, then Consolas, SF Mono, Menlo, JetBrains Mono, DejaVu Sans Mono, monospace (`Font.Mono`)

**Character:** Inter handles everything a person reads as interface. The platform monospace handles everything a machine produced: JIDs in the endpoint line, XML stanzas, timestamps and direction tags. Monospace timestamps align by construction. The contract's tabular-numerals feature is not applied anywhere yet.

### Hierarchy
- **Title** (600, 18 px, -0.2 letter spacing; `Type.Title`, class `title`): the workspace title in the top bar and the first-run heading.
- **Section** (600, 15 px; `Type.Section`, class `section`): list summary ("3 accounts · 0 online"), "Traffic", form headings, the account name in the detail header.
- **Body** (400, 13 px; `Type.Body`): window default. Row names use body at 600; button text uses body at 500.
- **Meta** (400, 12 px; `Type.Meta`, class `meta`): status lines, hints, explanatory notes, in `Ink.Secondary`.
- **Label** (500, 12 px, class `label`): field labels above inputs, in `Ink.Secondary`, with 4 px space below.
- **Caption** (500 to 600, 11 px; `Type.Caption`): rail labels and the sniffer direction tags (mono, 700).
- **Mono** (400, 12 px, class `mono`): stanzas, endpoints, timestamps.

### Named Rules
**The Machine Text Is Mono Rule.** If the text came from a protocol (JID, XML, host:port, timestamp), it is set in `Font.Mono`. Otherwise it is set in Inter. Never mix the two within a single phrase.

**The Five Sizes Rule.** The ramp is 11 / 12 / 13 / 15 / 18. Hierarchy within it comes from weight (400, 500, 600) and ink step, not from new sizes.

## Layout

The shell is a fixed grid: a full-height **rail** on the left, a 48 px **top bar** across the rest, and the **workspace** below it. The Accounts workspace is master and detail: a fixed-width **list column** separated by a hairline, beside a flexible **detail column** (header strip on a panel surface, an optional failure banner, a section header, then a full-height bordered stream panel).

- **Rail:** 76 px wide, `Surface.Rail`, hairline on its right. 60 px items with icon over label, 2 px apart. The theme toggle is pinned to the bottom. Workspaces that have not shipped yet are visible but disabled, with a tooltip naming the phase they arrive in.
- **Top bar:** `Surface.Panel` with a bottom hairline, 20 px left inset. Workspace title, then the account lens, which scrolls horizontally without a visible scrollbar. The contract's search/command field is deferred until there is something to search.
- **List column:** 340 px, inset 12 px, header row 10 px above the list.
- **Detail column:** 20 px gutters, 14 to 16 px between blocks.
- **Spacing rhythm:** steps of 2 / 4 / 6 / 8 / 10 / 12 / 14 / 16 / 20 px (frontmatter `spacing`): 6 between icon and label, 8 between sibling controls, 10 within a form stack, 14 panel padding, 20 page gutter. Destructive actions sit 20 px away from their siblings.

**Responsive behaviour** uses Avalonia `ContainerQuery` on the root grid (window minimum 720 x 480):
- **≤ 1180 px:** list column 300 px.
- **≤ 960 px:** rail collapses to 56 px with 44 px icon-only items; lens chips drop to avatar plus dot (a failed account still shows its short status in red); list column 260 px; the Add button loses its label; the endpoint is hidden.
- **≤ 780 px:** all button labels hide except the armed Remove, which keeps its words so the confirmation stays readable. List column 220 px; the traffic hint is hidden; timestamps shorten to `HH:mm:ss`; the list summary switches to its short form ("0/3 online").

Icon-only states always keep a tooltip and an automation name.

## Elevation & Depth

The system is flat. No element in the build has a shadow. Depth comes from tonal surface steps (rail, ground, panel, code) and 1 px hairlines (`Line` on structure, `Line.Strong` on interactive edges). State is shown with fills (`Surface.Hover`, `Surface.Pressed`, `Accent.Subtle`, `State.Bad.Subtle`), never with lift. The only overlay is the platform tooltip.

### Named Rules
**The Hairline, Not Shadow Rule.** Separate regions with a 1 px `Line` border or a surface step. If something seems to need a shadow, it needs a surface step instead.

## Shapes

Every rectangle (button, input, panel, list row, avatar, failure banner, focus ring) uses one gently rounded corner (`Radius`, 6 px). The fully rounded pill (`Radius.Pill`) is used only for account lens chips, and status dots are circles: 8 px, or 10 px with a 2 px `Surface.Panel` ring when overlaid on an avatar's bottom-right corner. Icons are strokes with round caps and joins, drawn on a 24-unit grid and scaled to 15 px in buttons, 16 px inline, 20 px in the rail and 24 px in empty states. Borders are always 1 px, except the 2 px focus ring.

## Components

### Buttons
Restrained and legible. Every button is a 6 px rectangle, 30 px minimum height, 13 px Medium text. An optional 15 px line icon sits 6 px before the label.
- **Default (secondary):** panel fill, `Line.Strong` border, primary ink. Hover `Surface.Hover`, pressed `Surface.Pressed`, disabled tertiary ink on a `Line` border.
- **Primary:** cobalt fill and border, `Ink.OnAccent` text. Hover `Accent.Hover`. Disabled falls back to `Surface.Pressed`. Use at most one per region: Add account, Connect, Add and connect, Save and connect.
- **Ghost:** transparent with no border, secondary ink, 8 px horizontal padding. Hover gets the hover fill and primary ink. Used for Cancel and for in-panel tools (Pause, Clear).
- **Danger:** red text (ghost-styled in the header), red-tint fill on hover, set 20 px apart from the other actions. **Armed** (after the first click): red-tint fill, red border, and text that asks for confirmation. Destruction always takes two clicks.
- **Toggle checked:** `Accent.Subtle` fill, cobalt border, `Accent.Ink` text (ghost toggles drop the border).
- **Focus:** a 2 px cobalt ring, 6 px radius, offset 2 px outside the control, on every focusable control.

### Chips (account lens)
- **Style:** pill, `Surface.Ground` fill, 1 px `Line` border, padding 4 px on three sides and 10 px on the right. Contents are a 20 px hue avatar with a ringed status dot, the account label (12 px Medium) and, only when failed, a short status in `State.Bad`.
- **State:** the tooltip gives the label and full status. Below 960 px the chip collapses to the avatar alone. Click-to-scope filtering is the planned signature behaviour from Phase 1 and is not built yet.

### Cards / Containers
- **Corner Style:** 6 px.
- **Background:** `Surface.Panel`, or `Surface.Code` for streams.
- **Shadow Strategy:** none (see Elevation & Depth).
- **Border:** 1 px `Line`.
- **Internal Padding:** 14 px for forms; streams run edge to edge with 6 px vertical inset.

### Inputs / Fields
- **Style:** panel fill, 1 px `Line.Strong` border, 6 px radius, 32 px minimum height, padding 6 px vertical by 10 px horizontal, 13 px text. Placeholder in tertiary ink. Label above in the `label` style.
- **Hover / Focus:** hover darkens the border to `Ink.Tertiary`. Focus turns the border cobalt; inputs show focus through their own border and suppress the outer ring. Text selection is cobalt with `Ink.OnAccent`.
- **Error:** form errors are 12 px `State.Bad` text, wrapped, directly above the form's actions.

### Lists
- **Rows (`rows`):** 6 px radius, padding 8 px vertical by 10 px horizontal, 1 px vertical gap. Hover `Surface.Hover`; selected `Accent.Subtle` (no border, no accent bar). Account rows show a hue avatar with a ringed status dot, the name at 13 px SemiBold and the status line in meta.
- **Stream (`stream`):** for high-volume logs. Virtualised, padding 3 px vertical by 14 px horizontal, no minimum height. Columns are a mono tertiary timestamp, a 36 px mono direction tag and wrapped selectable mono text. Tags are TX, RX, INF, WRN, ERR, EXC: RX in primary ink, WRN in `State.Busy`, ERR/EXC in `State.Bad`, INF in secondary ink (TX: see Do's and Don'ts).

### Navigation (rail)
- **Style:** 60 px items, transparent, 20 px icon over an 11 px Medium label, secondary ink.
- **States:** hover gets the hover fill and primary ink. The current item gets an `Accent.Subtle` fill with `Accent.Ink`. Disabled (future workspaces) uses tertiary ink at 0.7 opacity and keeps its tooltip.
- **Narrow:** at 960 px and below, items are 44 px and icon-only.

### Status and Failure
- **Status dot:** an 8 px circle; idle grey, ok green, busy amber, bad red. Always paired with a status word nearby (meta text or tooltip).
- **Failure banner:** `State.Bad.Subtle` fill, 6 px radius, padding 10 px vertical by 12 px horizontal. A 16 px red alert icon, the selectable error text, and the action that fixes it (such as "Enter password"). That action expands an inline field in place, with Cancel (ghost) and Save and connect (primary). Recovery happens where the error is shown, without a dialog.
- **Empty state:** centred, max 360 to 440 px wide. A 24 px tertiary line icon, a SemiBold line saying what is missing, and one meta sentence saying how to get it, adapted to the current error.

### Line Icon (signature primitive)
`LineIcon` draws a `StreamGeometry` from the `Icon.*` tokens on a 24-unit grid with a 1.7-unit stroke, round caps and joins, no fill. The stroke inherits the text foreground, so hover, selected and disabled states colour icons without extra styles. New icons are added as `Icon.*` geometry tokens in the same grid and stroke style. Never use font glyphs or bitmaps.

## Do's and Don'ts

### Do:
- **Do** reference only token keys from `Tokens.axaml`, through `DynamicResource` for brushes so light and dark switch live.
- **Do** reuse the `Controls.axaml` classes (`primary`, `ghost`, `danger`, `armed`, `rows`, `stream`, `panel`, `dot`, `meta`, `label`, `section`, `title`, `mono`) in every new workspace before writing a local style.
- **Do** set protocol text (JIDs, XML, host:port, timestamps) in `Font.Mono` at 12 px.
- **Do** pair every status dot and hue with words: a status line, a tooltip, or an automation name.
- **Do** set destructive actions 20 px apart from their siblings and require a second, armed click whose label stays visible at every width.
- **Do** put the fix for a failure next to the failure, inline, in a `State.Bad.Subtle` banner.
- **Do** add responsive rules as `ContainerQuery` steps at the existing 1180 / 960 / 780 px breakpoints, and keep a tooltip on anything that becomes icon-only.
- **Do** design both theme variants for every new token. Never derive dark by inverting light.

### Don't:
- **Don't** use cobalt for anything except selection, primary action and focus.
- **Don't** use account hues for state, severity or decoration.
- **Don't** add shadows or glows; use a surface step or a 1 px hairline.
- **Don't** introduce a corner radius other than 6 px or the pill.
- **Don't** add type sizes outside 11 / 12 / 13 / 15 / 18.
- **Don't** build dashboards out of cards, or style the console as neon on black.
- **Don't** treat the sniffer's TX tag colour (currently the accent) as precedent for accent-coloured content; it is an open exception to the One Accent Rule.
