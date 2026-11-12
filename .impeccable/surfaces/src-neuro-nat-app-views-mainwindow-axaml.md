---
version: 1
slug: "src-neuro-nat-app-views-mainwindow-axaml"
primary_target: "src/Neuro.Nat.App/Views/MainWindow.axaml"
related_targets: ["src/Neuro.Nat.App/App.axaml"]
---

# App shell and Accounts workspace

Scope: the desktop shell (rail, top bar, account lens, workspace area) and the Phase 0 Accounts workspace (account list, inline add form, account detail with live sniffer). Mode: Operate. Every later workspace (Events, Fleet, Sources, Chat, Console, Topology, Identities) inherits this shell.

Audience and job: internal Neuron administrators and operators, on Windows, macOS and Linux, often on large or multiple monitors, sometimes on a laptop. Job here: add accounts, see at a glance which are online, read why one is not, and watch its traffic.

Constraints: Avalonia 12, Fluent controls restyled through tokens, light and dark both designed, responsive from about 720 px wide to 2560 px. Destructive actions isolated and confirmed. Keyboard focus visible everywhere.

## Direction contract

THESIS: The calm, dense operator console the category already knows, executed at the craft level of the category's best tools (Linear, Sentry, Grafana). It refuses both the dashboard-of-cards and the neon-on-black "ops" costume: hierarchy comes from type, spacing and hairlines, colour only marks state, selection and which account something belongs to.

OWN-WORLD: Light ground cool near-white with white panels, dark ground graphite with one-step-lighter panels; 1 px hairlines instead of shadows; one cobalt accent reserved for selection, primary action and focus; one severity ramp (debug to emergency) and eight account hues used only on account dots and chips. Inter for all UI text, a system monospace only for JIDs, XML and timestamps, tabular numerals. Radius 6. Line icons drawn on a 24 grid at 1.6 stroke.

STORY: The operator sees every account and its state immediately, opens one, reads exactly why it is offline or what it is sending, and acts without leaving the screen.

FIRST VIEWPORT: Left, a 72 px rail with icon-over-label workspaces, the current one marked with an accent tint; later workspaces visible but quiet. Top, a 48 px bar: workspace title, the account lens (one chip per account: hue dot, name, status), and a search/command field on the right. Below, a 340 px account list with status lines and an inline Add account form at its top, beside the selected account: header with endpoint and status, actions on the right with Remove set apart, then the sniffer as a full-height monospace stream with direction markers. Primary action: Add account (accent) in the list header.

FORM: Category standard (the canon card, chosen by Mille), executed at full fidelity; position: standing exit; seed:e7f5fe6aaa9ef32e.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

## Open

- Search / command field (FIRST VIEWPORT, right of the top bar): deferred to Phase 1, when there are servers, nodes, events and commands to search. Phase 0 has only a handful of accounts, already visible in the lens and the list; a field with nothing behind it would be a dead control.
- Reference products: Linear, Sentry and Grafana proposed as the craft bar; Mille may name others.
- Signature interaction: the account lens (click to scope, shift-click to add) becomes the shared filter for every workspace from Phase 1.
