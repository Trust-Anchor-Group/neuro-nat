---
name: NAT
description: A calm, dense desktop app for running many Neurons at once, in designed light and dark, at any window size.
colors:
  accent: "#3358D4"
  accent-hover: "#2A4BBE"
  accent-pressed: "#223FA3"
  accent-subtle: "#EAEFFC"
  accent-ink: "#2440A8"
  accent-dark: "#7B9BFF"
  accent-hover-dark: "#94AEFF"
  accent-pressed-dark: "#6A8BF0"
  accent-subtle-dark: "#1C2541"
  accent-ink-dark: "#B4C6FF"
  surface-ground: "#F5F6F8"
  surface-panel: "#FFFFFF"
  surface-hover: "#F0F2F5"
  surface-pressed: "#E7EAEE"
  surface-rail: "#ECEEF2"
  surface-code: "#FAFBFC"
  surface-raised: "#FFFFFF"
  surface-header: "#F7F8FA"
  surface-ground-dark: "#101215"
  surface-panel-dark: "#16191D"
  surface-hover-dark: "#1D2126"
  surface-pressed-dark: "#242930"
  surface-rail-dark: "#0C0E11"
  surface-code-dark: "#121519"
  surface-raised-dark: "#1C2026"
  surface-header-dark: "#13161A"
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
  state-ok-subtle: "#E6F4EC"
  state-busy-subtle: "#FBF1DF"
  state-idle: "#8A929E"
  state-ok-dark: "#45C27D"
  state-busy-dark: "#E5A63F"
  state-bad-dark: "#F26D64"
  state-bad-subtle-dark: "#2E1A1A"
  state-ok-subtle-dark: "#142A1E"
  state-busy-subtle-dark: "#2E2412"
  state-idle-dark: "#6E7682"
  chart-1: "#2A78D6"
  chart-2: "#EB6834"
  chart-3: "#1BAF7A"
  chart-4: "#EDA100"
  chart-5: "#E87BA4"
  chart-6: "#6250D6"
  chart-neutral: "#B4BAC3"
  chart-1-dark: "#3987E5"
  chart-2-dark: "#D95926"
  chart-3-dark: "#199E70"
  chart-4-dark: "#C98500"
  chart-5-dark: "#D55181"
  chart-6-dark: "#9085E9"
  chart-neutral-dark: "#4C5460"
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
  number:
    fontFamily: "Inter, sans-serif"
    fontSize: "20px"
    fontWeight: 600
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
  small: "4px"
  base: "6px"
  float: "8px"
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

# Design System: NAT

This file records the app as it is built. The XAML is the source of truth: colours, type and shapes are resources in `src/Neuro.Nat.App/Themes/Tokens.axaml`, the restyled controls are in `Themes/Controls.axaml`, motion is in `Themes/Motion.axaml`, and the shared behaviour (right-click menus, the selection bar, panes, the note at the bottom) is in `Controls/` and `ViewModels/Ui.cs`. When this file and the XAML disagree, the XAML is right and this file needs an edit.

## Overview

**North star: "The Quiet Console".** NAT (Neuro Administration Tool) is a native Avalonia 12 desktop app for Windows, macOS and Linux. It replaces IoTClient and Lil'Sis for the people who run Neurons. The look is the calm, dense layout operators already know, built with care: hierarchy from type weight, spacing and 1 px lines, and colour only where it means something. The app stays open for hours, so its resting state is quiet.

**What it is like to use:**
- Five places in the rail, in the order of a working day: Inbox, Neurons, Events, Chat, Console. Accounts and Settings sit at the bottom.
- One Neuron can be picked at the top, and every screen then shows only that Neuron. A Neuron also has a page of its own with tabs.
- Everything that can be done to a row is on its right-click menu, with its shortcut beside it. Several rows can be selected and acted on at once.
- Nothing blocks. There are no dialog windows. Questions are asked in place, and what was just done can be taken back.
- It works in a window 760 px wide, on a laptop, and on a very wide monitor. Text size can be changed and the whole window follows.

**Key characteristics:**
- Flat surfaces with 1 px lines. One shadow, only under things that float over the window.
- One cobalt accent for selection, the main action and keyboard focus.
- Eight account hues that only say which account something belongs to.
- Inter for everything a person reads. A monospace font for what a machine produced.
- Line icons on a 24-unit grid that take the colour of the text beside them.
- Light and dark are both designed. Neither is the other one inverted.

## Words

The app uses the words its users use. These replaced older ones, and the old ones must not come back in anything a person reads (class and property names in code may keep them).

| Say | Not |
|---|---|
| Inbox | Attention |
| Neurons | Fleet |
| Map | Topology |
| Problems | Issues |
| Mark fixed, Fixed | Resolve, Resolved |
| Listening | Live |
| No access | Not admin |
| Waiting for you | Needs you |
| N reporting | N live |
| Earlier events | Past events, history |
| Traffic | Sniffer |
| Software | Updates |
| Storage | Publish/subscribe, as the name of the tab |

Rules for new words: say what the thing does for the person, in a full short phrase. A button says what happens ("Mark fixed", "Put the layout back"). A count says what it counts ("3 problems", "4 of 5 Neurons reporting"). An empty screen says what is missing, in one short line. An error says what went wrong and, beside it, what fixes it. `FinishTests.EveryScreenUsesThePlansWordsAtEverySize` fails when an old word shows on a screen.

**No middle dots.** Facts that sit side by side are separate pieces with space between them in the layout, or one short phrase ("5 events on Lab since 10:42, latest 4 min ago"). Never join them with a middle dot, and never swap it for another separator character. Window titles use a plain hyphen ("Events - NAT"). `TextPassTests.NoMiddleDotInWhatTheViewsShow` fails when a view or a string in the code has one.

**A Neuron is named by its domain.** It goes by the roster name the user gave it, else by its domain (the part after "@"). When two Neurons would get the same name, those two show their full addresses. The full address stays in the details, in a tooltip on the name, and behind "Copy address". One function gives the name everywhere: `Engine/Events/NeuronNames.cs` (`NeuronNameConverter` in the views).

**No explaining text.** Say what is there, not how it works. One short line only where an action would otherwise be unclear, and one short line in an empty state. A question before something destructive stays, kept short. A tooltip is the short name of what a button does, not a sentence. The chat header says "End-to-end encrypted" or "Not encrypted" and nothing more; the key is in the contact's details.

**The account only when there are several.** With one account, no screen says which account something comes through. With several, the account goes by its short label (its display name, else its user name), never its full address, and only where it tells things apart.

## Colors

Cool neutrals, one cobalt accent, a state set, a severity ramp and the account hues. Each set has one job. Every token has a light and a dark value (frontmatter); views use the token, never a hex.

- **Accent** (`Accent`, `Accent.Hover`, `Accent.Pressed`, `Accent.Subtle`, `Accent.Ink`): the selected row and the checked choice (`Accent.Subtle` with `Accent.Ink`), the current rail item, primary buttons, text selection, the focused input's border and the 2 px focus ring. On the map it also marks links between federated servers.
- **Account hues** (`Account.Hue0` to `Account.Hue7`): the small square with the account's initial, wherever an account is named. Chosen by the account's index through `AccountHueConverter`.
- **State** (`State.Ok`, `State.Busy`, `State.Bad`, `State.Idle`, and `.Subtle` tints of the first three): status dots, counts that need attention, error text, the destructive button, and the tinted bands behind a failure, a warning or a success. `ToneBrushConverter` maps the tones `ok`, `busy`, `bad` and `idle` to them.
- **Severity** (`Severity.Debug` to `Severity.Emergency`): the level tag on an event and on a problem. Warning and Error share the state colours on purpose.
- **Charts** (`Chart.1` to `Chart.6`, `Chart.Neutral`, `Chart.Hatch`, `Chart.Settling`): the series of a chart, in that order and never cycled; the neutral one for undecided and other; the hatch over days whose numbers can still change. A series keeps its colour when others come and go (Android is always `Chart.1`, iOS `Chart.2`). Light and dark are each checked for colour blindness; three light ones are below 3:1 on white, so every chart can be shown as a table.
- **Surfaces** (`Surface.Ground`, `.Panel`, `.Rail`, `.Header`, `.Code`, `.Raised`, `.Hover`, `.Pressed`): the window, panels and inputs, the rail, table headers, streams of machine text, things that float, and the two interaction fills.
- **Lines** (`Line`, `Line.Strong`): `Line` for panel borders, dividers and loading placeholders; `Line.Strong` for the borders of buttons and inputs and for scrollbars.
- **Ink** (`Ink.Primary`, `.Secondary`, `.Tertiary`, `.OnAccent`): content, supporting text, and times and placeholders.

**The One Accent Rule.** Cobalt means selected, primary or focused. It is never decoration.

**The Hue Belongs To The Account Rule.** Account hues never mark state or severity. State is always also said in words.

**The Token-Only Rule.** `DynamicResource` for brushes, so the look changes live. `StaticResource` for sizes, radii, fonts and icons. No hex in a view.

## Typography

**UI font:** Inter, bundled, so every system draws the same face. **Mono font:** Cascadia Mono, then Consolas, SF Mono, Menlo, JetBrains Mono, DejaVu Sans Mono.

- **Title** (600, 18 px; class `title`): the name of the place in the top bar.
- **Number** (600, 20 px; `Type.Number`): the large figure on a Neuron's overview cards.
- **Section** (600, 15 px; class `section`): the heading of a list, a card or a pane.
- **Body** (400, 13 px): the window default. Row names are body at 600, button text at 500.
- **Meta** (400, 12 px; class `meta`): status lines, hints, supporting text, in `Ink.Secondary`.
- **Label** (500, 12 px; class `label`): the label over a field.
- **Caption** (500 to 600, 11 px; class `caption`): rail labels, table headers, small tags.
- **Mono** (400, 12 px; class `mono`): addresses, XML, timestamps, commands and their answers.

**The Machine Text Is Mono Rule.** Text that came from a protocol is mono. Everything else is Inter.

**The Sizes Rule.** 11, 12, 13, 15, 18, and 20 for the one large figure. Hierarchy comes from weight and ink before a new size.

**Text size is the person's choice.** Settings offers Small, Normal, Large and Larger (90, 100, 115, 130 percent); Ctrl+plus, Ctrl+minus and Ctrl+0 change it from anywhere. The whole window scales through one `LayoutTransformControl` (`Zoom` in `MainWindow`), so nothing is cut off, and the width steps below count the scaled width. Never set a size that fights this (no fixed pixel windows inside the window).

## Layout

**The shell** (`Views/MainWindow.axaml`): the rail on the left; a 48 px top bar with back and forward, the name of the place, its tabs, the Neuron picker, a button for a new window, and the search box (Ctrl+K); the screen; and a status bar with how many Neurons are reporting, the last event, the accounts and your status. The note that confirms an action shows in the status bar.

**The places:**
- **Inbox**: everything that waits for you in one list (messages, requests, notifications from Neurons), with the thing open beside it so it can be answered there.
- **Neurons**: List, Map and Big screen. A Neuron opens as a page with Overview, Events, Sources, Console, Software and Identity.
- **Compare** (under Neurons): two Neurons side by side, or a Neuron beside a kept copy of how it was, in Software, Settings and State now (`Views/CompareView.axaml`, `Engine/Compare/`). It starts from two Neurons selected in the list, "Compare with" on a Neuron, or "compare" and two names in the search box. "Only differences" is on from the start, with the count; State now is shown and never counted. Passwords and keys only say Same or Different. Every comparison keeps a copy of what it read; "Keep a copy now" and "Compare with earlier" are on every Neuron's menu.
- **Events**: problems on the left (events grouped by what went wrong), all events on the right.
- **Chat**: people and rooms, the conversation, and the person's details as a third column when there is room.
- **Console**: tick one or several Neurons, run a command, read the answers side by side.
- **Accounts**: each account with Connection (and its traffic), Devices, Identities and Storage.
- **App telemetry** (only while a Neuron with the Neuro.App.Telemetry package is found, as a sixth place in the rail and as a tab on that Neuron's page): Overview, Applications, NFC reading, Troubleshooting, Neurons and All metrics, from the feature project `Neuro.Nat.Features.Telemetry`. With several such Neurons, a choice of one at the top; their numbers are never mixed. Pages and charts an account may not read are left out.
- **Settings**: your status, pop-ups and sounds, look, text size, the tray, and every keyboard shortcut. At the bottom, the app's long name and version, and, where nothing is kept between runs (no vault for the database key yet), one plain line saying so.
- **When what NAT saved cannot be opened**, a small window says so before anything else ("NAT cannot open what it has saved on this computer.") with Try again (primary), Start fresh and Quit (`Views/DatabaseProblemWindow.axaml`). NAT never starts empty without a word.

**List and detail.** Most screens are a list pane beside what is open. The list pane has a width of its own per screen, the line between the panes can be dragged (`c:Pane.Key`; double-click puts it back) and the width is remembered. "Put the layout back" in Settings clears all of them.

**Sizes.** `MainWindow.axaml.cs` puts a class on the root for each width step the window is at or below: `maxw1280`, `maxw1180`, `maxw1100`, `maxw1000`, `maxw960`, `maxw900`, `maxw860`, `maxw780`. From 1800 px it adds `wide`. Views style themselves with `.maxwN Selector`. (Container queries did the same but cost every list row a subscription and made scrolling slow. Do not bring them back.)
- **Wide (1800 px and up):** list panes get some of the room, tables show every column, Chat keeps the person's details open as a third column.
- **Normal (about 1440):** the reference size. Everything shows with its words.
- **Narrow (1280 down to 960):** list panes get slimmer, secondary columns and long labels drop, filter boxes shrink.
- **Small (860 and below, to the 720 px minimum):** the rail is icons only, the Neuron picker scrolls, tables keep the columns that answer "is it well", toolbars keep what is used most and leave the rest on the right-click menu.

**A width set in a view beats a style.** A local `Width="260"` on a control wins over any `.maxwN` style, and the control then overflows in a small window. Give such widths in a style, with the narrower steps after it. Things in a bar are either whole or not there; nothing is shown half cut. `FinishTests.TheBarOverTheEventsGivesWayInsteadOfBeingCutOff` checks this for the busiest bar.

**More than one window.** Ctrl+Shift+N opens the screen in front in a window of its own; a Neuron and a conversation have "Open in a new window" on their menus. Windows come back where they were, on a screen that still exists, showing what they showed. The app itself opens on the screen it was closed on.

**The big screen** (F11, `Views/BigScreenView.axaml`): for a screen across the room, one page at a time in large text with no controls. Neurons (the Neurons as large tiles and the latest problems) is always there; Problems (each open problem: what, where, since when, how many times) only while one is open; after them come the pages features add through `IBigScreenPage` in `BigScreenPages.Added` (App numbers, each found telemetry Neuron's key numbers, is one). It turns by itself, 20 seconds a page, and skips pages with nothing to show; small dots at the bottom say which page is showing. When a Neuron goes down or a new problem opens, it turns to that page and stays half as long again; a new problem is marked New. Left and Right turn, Space holds, Esc or F11 leaves. Settings choose the pages and the time. Pages slide in as they turn, and simply change with less motion. It is drawn for 1280 px and scaled to the window, so it reads the same at 3840. It never scrolls and never shrinks below what reads from across a room (`BigScreenTileLayout`): up to six Neurons get full tiles; more get small tiles (name, state, CPU, errors), up to six side by side as the width allows, never under 0.85 of their drawn size, and when they do not fit at that the Neurons page becomes several pages, in name order, each with its own dot. Problems turn five to a page. A Neuron going down turns to the page that holds it.

**Spacing:** steps of 2, 4, 6, 8, 10, 12, 14, 16, 20 px. 6 between an icon and its label, 8 between sibling controls, 14 inside a panel, 20 at the edge of a screen. `compact` on the root (Settings, "Compact rows") makes list rows less tall.

## Elevation and depth

The window is flat: regions are separated by a surface step or a 1 px `Line`. One thing has a shadow: what floats over the window (class `float`: the search results and the pop-up cards; menus are styled the same way) uses `Surface.Raised`, a `Line.Strong` border, the 8 px `Radius.Float` and `Shadow.Float`. The selection bar is an `Accent.Subtle` band inside its list, and the note is a dark pill in the status bar; neither has a shadow. State is shown with fills (`Surface.Hover`, `Surface.Pressed`, `Accent.Subtle`, the state tints), never with lift.

**The Line, Not Shadow Rule.** If something in the window seems to need a shadow, it needs a surface step. Shadows are for what floats.

## Shapes

`Radius` 6 px on buttons, inputs, panels, rows and avatars. `Radius.Small` 4 px on small tags and key caps. `Radius.Float` 8 px on what floats and on Settings cards. `Radius.Pill` on the Neuron picker's chips and on counts. Status dots are circles. Borders are 1 px; the focus ring is 2 px.

## Components

Classes are in `Themes/Controls.axaml`. Use them before writing a local style.

### Buttons
30 px tall, 6 px corners, 13 px medium text, an optional 15 px icon 6 px before the label.
- **Default:** panel fill, `Line.Strong` border.
- **`primary`:** cobalt. At most one per region.
- **`ghost`:** no border; for tools in a bar. **`small`** for buttons inside rows. **`link`** looks like text until the pointer is on it, and opens what it names.
- **`danger`:** red text, set apart from its siblings. Anything that restarts, stops, deletes or upgrades asks once more in place, and says which Neuron it will hit.
- **`tab`** (the tabs of a place), **`seg`** with `first` and `last` (a choice of a few in a row), **`chip`** (the Neuron picker), **`choice`** (a large choice with an icon in Settings).
- Hover, pressed, disabled and keyboard-focus states exist for every one of them. Focus is a 2 px cobalt ring; inputs show it on their own border.

### Fields
32 px tall, `Line.Strong` border that turns cobalt with focus, placeholder in `Ink.Tertiary`, label above in `label`. **`find`** is a filter box: every list has one, and Ctrl+F puts the keyboard in the one on the screen in front. A list never shows without a way to narrow it.

### Lists
- **`rows`:** 6 px corners, hover and selected fills, no accent bar. A row shows who or what, one line of state, and at most one or two buttons for what is done most.
- **`stream`:** virtualised rows of machine text for events and traffic. Column names sit in a `thead` of `th` texts that share the rows' widths.
- **Tables** (the Neurons list): click a column name to sort; the choice is remembered.
- Lists stay fast with very many rows: rows are virtualised, and a row takes no subscription of its own to the window's size.

### Right-click menus and the selection bar
Every list implements `IActionSource.ActionsFor(items)` and returns an `ActionList` (`ViewModels/ItemActions.cs`): Open, Copy, Save, Filter, Go to, Do, and Window, in that order of groups. `c:ActionMenu` shows it on right-click with the shortcut beside each action. When several rows are selected, `c:ActionBar` rises at the bottom of the list with the count, the main actions, the rest under "More", and a way out (Escape). The same list of actions feeds the menu, the bar and the keyboard, so they cannot disagree. An action never depends on a selection the person cannot see.

Copy is always there: Ctrl+C copies what is selected as text, Ctrl+Shift+C the other way (with times, or as Markdown), Ctrl+S saves it to a file. In traffic, dragging over lines selects them.

### Links
Anything you can point at has a link that reads plainly: `neuro-nat://neuron/<domain>/<tab>`, `problem/<domain>/<fingerprint>`, `event/<domain>/<id>?at=<UTC time>`, `node/<domain>/<source>/<node>/…`, `chat/<address>`, `room/<address>`, `compare/<domain>/<domain>` and `compare/<domain>/kept/<copy id>` (`Engine/Links/NatLink.cs`). A Neuron is named by its domain; the account is never in a link. "Copy link" is added once, in `Links.WithLink` (`ViewModels/Links.cs`), so every menu and selection bar has it. In a chat message a link shows as a small card with the name of the thing and its state now; pasted into the search box it opens. A link only opens a screen. What cannot be followed gets one note.

### The note, and Undo
`Ui.Say("Copied 3 events")` shows a short note in the status bar for 2.4 seconds (class `flash`), in the window in front. `Ui.Say(note, undo)` adds an Undo button and keeps the note for 7 seconds; Ctrl+Z does the same while it shows. Use the second form for anything that takes one click and is easy to do to the wrong row: acknowledge, mute, mark fixed, reopen. What cannot be taken back asks first instead.

### Pop-ups
A message or an alert shows as a card in the bottom right corner of the window, or of the screen when the window is behind others or in the tray (`Views/NoticeCard.axaml`). It can be answered or dismissed from the card. Settings start with four plain choices (What needs me, Everything, Nothing, My own choice); the table of switches for each kind only shows for "My own choice". Settings never open on a long list of tick boxes.

### Charts
Drawn by NAT itself (`Neuro.Nat.Features.Telemetry/Charts`): lines and bars over time (`TimeChart`, also stacked), one bar per group across (`AcrossChart`), a large number with its trend (`TrendLine` beside it), and the same numbers as a table (`TableChart`). Every chart sits in a `panel` with its name in `section` and at most one `meta` line under it, a legend when it has more than one series, and "Show table". The pointer on a chart shows its values in a floating box; days that can still change are hatched and marked "May change". Grid lines are `Line`, axis text `Ink.Tertiary` at 11 px, lines 2 px, bars with a 2 px gap.

### Empty and loading
- **Empty:** a 24 px icon in `Ink.Tertiary` and one short semi-bold line saying what is missing. A `meta` line under it only when the way to fill it is not plain from the screen. Centred, at most about 380 px wide, with a side margin so it wraps in a narrow pane.
- **Loading:** `c:Placeholder` draws grey bars where the rows will be (`Rows`, `RowHeight`), under one line saying what is being read. The screen does not jump when the rows arrive.
- **Failure:** a `State.Bad.Subtle` band with what went wrong and the action that fixes it, in place.

### The map
`Controls/TopologyMap.cs`, laid out by `Engine/Topology/TopologyLayout.cs`. A part of the network where something has a parent is a tree: the parent on top, what connects to it in a row under it, lines that leave the parent downwards and turn with rounded corners. Seven or more children with none of their own hang in columns under their parent, each entered from its left. Points that are only linked some other way go in rows under the tree, as near as they can be to what they link to. A part with no parents is spread by a force layout. A Neuron's card shows its health dot, its name (and its domain when the name is another one), and CPU, memory and disk. Grey line: up to the parent. Blue line: the parent, and federated with it. Blue dashes: federated with each other. The panel beside the map shows this legend until something is selected.

## Motion

Short, and only where it says what changed. All of it is in `Themes/Motion.axaml`, which is part of the app's styles only while motion is on (`Controls/Motion.cs`), so with it off nothing can move.
- A screen that comes to the front fades in (140 ms).
- The note, the search results and the selection bar rise 6 px into place (160 ms).
- A pop-up comes in from the right (200 ms). Menus fade in (90 ms).
- A row that arrives while you watch fades in (`c:Motion.NewRows` on its list). Rows that were there when the list was filled do not.
- A status dot changes colour softly. The loading bars breathe slowly.

Motion is off when the system asks for it (on Windows: "Animation effects"), when Settings says "Less motion", and in tests. Nothing loops except the loading bars, nothing moves more than a few pixels, and nothing waits for an animation to finish before it can be used.

## Never again

These are the habits of the tools this app replaces. Check every new screen against them.
1. **No blocking windows.** No dialogs. Ask in place, or let it be undone.
2. **No action that depends on a hidden selection.** What an action will hit is visible and named.
3. **No piles of tabs.** A place has a few tabs at most, and a thing opens in place, not in a tab of its own.
4. **No flat lists.** Every list can be filtered, and Ctrl+K searches everything.
5. **No page per thing** when a pane beside the list will do.
6. **No long lists of tick boxes.** A few plain choices first, the detail behind one of them.

## Do and don't

**Do**
- Use the tokens, and the classes in `Controls.axaml`, before anything local.
- Give every row a right-click menu through `IActionSource`, and every list a `find` box.
- Say which Neuron an action is for, by its name. Name the account too when there are several.
- Pair every dot and every hue with words.
- Confirm with `Ui.Say`, and pass an undo where one click could go wrong.
- Add width rules as `.maxwN` styles, and check the screen at 760, 1440 and 2400 px in both looks. `FinishTests` renders all of them to `.impeccable/review/`.
- Put motion in `Motion.axaml` only.
- Design both looks for a new token.

**Don't**
- Don't use cobalt for anything but selection, the main action, focus and federation links on the map.
- Don't use account hues for state.
- Don't add a shadow to anything that does not float.
- Don't add a corner radius or a type size outside the ones above.
- Don't set a width on a control that a small window needs to shrink.
- Don't open a dialog window.
- Don't use a word from the "Not" column.
- Don't put a middle dot between facts, or a full address where a name will do.
- Don't explain what nobody asked about.
- Don't build dashboards out of cards, or dress the app as neon on black.
