# Neuro.AA — plan for an internal Neuron admin app

Status: reviewed by Mille 2026-11-09, decisions in section 10. Phases 0 to 5 built by 2026-11-10 (remote desktop left out: nothing to connect to yet); Phase 6 (bold ideas) is next.
Date: 2026-11-09
References studied (read-only): `IoTGateway` (especially `Clients/Waher.Client.WPF`, i.e. IoTClient), `iotbroker` (`Waher.Service.IoTBroker`), `C:\Program Files (x86)\LilSis\`

Hard rule: Neuro.AA never changes Neuron, IoTGateway or iotbroker source. Everything below uses protocols and administrative surfaces the Neuron already exposes. Where a feature needs the Neuron to be *configured* differently (for example adding an event sink node), that is called out explicitly and is always optional.

---

## 1. What we are building, in one paragraph

A single desktop workspace for the people who run Neurons. You sign in with several XMPP accounts at once (on several Neurons), and the app gives you one place to (a) see what every server is doing right now, especially events and exceptions, (b) administer concentrator sources and nodes with at least everything IoTClient can do, (c) talk to servers through chat admin and to coworkers through end-to-end encrypted chat, (d) manage your digital identities, and (e) see the federation as a live topology. It replaces the current mix of IoTClient (sources/nodes) and Lil'Sis (chat), and it fixes what both are bad at: monitoring.

## 2. What the references taught us

### 2.1 IoTClient (Waher.Client.WPF) — the parity baseline

A WPF tree of connections, each `XmppAccountNode` carrying many protocol clients. Features found in source:

| Area | IoTClient capability | Source |
|---|---|---|
| Accounts | Multiple XMPP accounts, connect form, change password, saved connection files (`.xml`) | `ConnectToForm`, `XmppAccountNode`, File > New/Open/Save |
| Roster | Contacts, groups, presence, subscribe/unsubscribe/accept, add contact, copy/paste JIDs | `XmppContact`, `AddContactForm` |
| Chat | 1:1 chat with Markdown | `ChatView` |
| Raw XMPP | Send custom message / IQ, view IQ result | `Dialogs/Xmpp/*` |
| Discovery | Finds components on the server (concentrator, MUC, PubSub, legal, provisioning, thing registry, events) | `SearchComponents` |
| Concentrator | Data sources, node tree, add/edit/delete, move up/down, copy/paste, configure parameters (data forms), node commands with results, search nodes | `Model/Concentrator/*`, `ParameterDialog` |
| Sensors / control | Read momentary / detailed values, subscribe to momentary, control parameters | `SensorDataView`, `XmppSensor`, `XmppActuator` |
| Queries / reports | Node queries rendered as reports (tables, objects, sections, events) | `QueryResultView`, `Controls/Report/*` |
| Sniffing | Sniff connections and nodes | `SnifferView` |
| Events | Receive XEP-0337 event messages, event log view | `LogView`, `EventReceptor_OnEvent` |
| Provisioning | Thing registry, claim device, search for things, provisioning questions (can read / can control / is friend) | `ThingRegistry`, `QuestionView`, `Controls/Questions/*` |
| MUC | Enter/destroy rooms, occupants, privileges, ban, subject, invitations | `Model/Muc/*`, `Dialogs/Muc/*` |
| PubSub / PEP | Nodes, items, affiliations; PEP mood, activity, tune, location, avatar, sensor data | `Model/PubSub/*` |
| Legal | Legal identity form / service | `LegalService`, `LegalIdentityForm` |
| P2P / E2E | Serverless messaging, peer connections | `ServerlessMessaging_*` |
| Remote desktop | Remote desktop view | `RemoteDesktopView` |
| Script | Script console | `ScriptView` |

Its weak points are the ones to beat: modal dialogs for everything, one tree for everything, a plain event list without filtering, grouping or history, and nothing that spans more than one server.

### 2.2 Lil'Sis — the chat reference

A modified Neuron serving Markdown web pages (`Chat.md`, `Contacts.md`, `Rooms.md`, `E2E.md`, `Events.js`, `Credentials.md`, `ProposeContract.md`, `RDP.md`, ...). Good ideas to keep: the E2E lock and P2P lightning/snail indicators per contact, rooms, contract proposals from chat, Markdown everywhere. Weak: it is a web server you run locally, monitoring is an afterthought, and the UI is page-based.

### 2.3 What the Neuron already offers that we can use without touching source

This is the important finding: the broker already exposes far more admin surface than IoTClient uses.

1. **Event log component (XEP-0337)** — `iotbroker/.../Events/EventLogComponent.cs`. Accepts `log` messages and supports `jabber:iq:search` over the *persisted* event log, filterable by From/To time, Type, Level, Message, Object, Actor, EventId, Facility, Module, StackTrace (wildcards allowed). Admin only. This gives us **history and backfill**.
2. **Broker data sources over the concentrator** — `iotbroker/.../Sources/`:
   - `Reports/Events`: `EventsRoot` (sniffable; `EventSinkToSniffer` forwards every logged event to a sniffer, which gives us a **live event stream**), `SearchEventsCommand`, `EventStatisticsCommand`.
   - `Reports/Performance`: CPU, allocated/free memory, free disk, locked objects, sample statistics.
   - `Reports/Protocols`: HTTP and XMPP statistics.
   - `Reports`: server admin reports (`ServerAdminReportNode`).
   - `Connections`: connected clients and servers.
   - `GatewayConfig`: ports, web server, login auditor, domains, and **event sinks** (`XmppEventSinkNode` with a Recipient JID, plus filter, MQTT, syslog, webhook, file sinks).
   - `ProgramData`: files and folders on the server.
3. **Admin rights** — `XmppServerModule.IsAdmin`: a JID is admin if it is a notification address or a user with the `Admin` privilege. Our accounts need one of those.
4. **Notifications** — `Gateway.SendNotification` pushes Markdown chat messages to notification addresses (backups, legal ID expiry, roster changes, startup, etc.). Today they drown in a chat window; we will parse and classify them.
5. **Chat admin** — command language over chat (`help`, `Clients`, `Servers`, `Versions`, `Packages`, `Backup`, `Analyze`, `ClockDiff`, `users`, `roles`, `Reboot`, ...) plus any input executed as script. Topology commands return GraphViz: `ParentGraph`, `S2SGraph [SCRIPT]`, `GroupGraph [SCRIPT]`, `TopologyQuery [SCRIPT]`. When many Neurons sit in one MUC room, one command fans out and **results are consolidated** into one graph or table.
6. **MCP servers on the Neuron** — `IoTGateway/Mcp`: `Waher.Mcp.Events`, `Waher.Mcp.Identity`, `Waher.Mcp.Xmpp`, `Waher.Mcp.Files`, `Waher.Mcp.Content`. These open the door to an AI assistant later (bold ideas).

## 3. Feature scope

### 3.1 Fleet and monitoring (the reason this app exists)

- **Fleet overview**: one row/card per Neuron (domain, build time, versions, uptime, clients/servers, CPU/memory/disk, error rate last hour, last exception). Sources: performance and protocol nodes, chat admin `Versions`/`Runtime`/`BuildTime`.
- **Live event stream across all servers**: subscribed through the `EventsRoot` sniffer on each broker. We do not add event sink nodes to the Neurons (decided 2026-11-09: no need right now). The app is read-only towards Neuron configuration unless the operator explicitly edits a node.
- **Backfill and history**: on connect, query the event log component for the window since we were last online; dedupe against the local store.
- **Exceptions as issues**: group events by fingerprint (normalized stack trace + facility + module + event ID), with first seen, last seen, count, sparkline, affected servers. Mark as acknowledged, muted, or resolved, with a note. Re-opened automatically if it returns after resolve.
- **Fast filtering**: facets for server, type (Debug → Emergency), level (Minor/Medium/Major), facility, module, actor, object, event ID, tags; free text; saved views; time range brush.
- **Event detail**: full message, tags, stack trace with collapsing of framework frames, "events around this one" ±N seconds on the same server, copy as Markdown, share to a coworker chat.
- **Alerts**: local rules (for example "any Critical+ on prod", "more than 20 errors in 5 min on X"), a native OS notification + a badge, quiet hours.
- **Notification parsing**: the Neuron's Markdown notifications become typed items (backup done/failed, legal ID expiring, software package available, new roster request) in an Attention inbox, not chat noise.

### 3.2 Sources and nodes (IoTClient parity, done better)

- Data sources and node tree per server, with search, lazy loading and virtualization for large trees.
- Inspector side panel instead of modal dialogs: properties, parameters (rendered XEP-0004 data forms with validation, dynamic forms and post-back), node commands, queries.
- Add/edit/delete/move/copy/paste nodes, with a diff preview for edits and undo where the protocol allows it.
- Sensor readout (momentary/detailed/all) as a table plus a live chart; subscriptions with field rules; control parameters with confirmation.
- Query results rendered as real reports (sections, tables, objects, events, graphs), exportable to CSV/Markdown.
- Sniffer panel docked under any node or connection, with XML pretty-print, filtering and pause.
- Compare a node or config subtree between two servers (bold idea, but cheap once the tree exists).

### 3.3 Accounts, roster and chat

- Many accounts at once, each with a colour/initials chip; a global account lens to scope every view to one, several or all accounts.
- Roster: contacts, groups, presence, subscription requests (accept/deny in bulk), add/remove, rename, group editing.
- Unified inbox across accounts, Markdown messages (parsed with the Waher Markdown engine and rendered by our own Avalonia renderer, see section 5), message corrections (the Neuron uses message updates for notifications), threads, file transfer via HTTP upload.
- MUC: rooms, occupants, roles, invitations, subjects, ban, destroy.
- Raw XMPP console: send message/IQ, see results, for debugging.

### 3.4 End-to-end encrypted coworker chat

- Uses the Waher E2E stack (`Waher.Networking.XMPP.P2P` endpoint security, the same one Lil'Sis and Neuro-Access use) so it is interoperable with Neuro-Access and Lil'Sis users.
- Clear trust signals: lock (E2E established), P2P vs via broker, key fingerprint, and a warning if a contact's key changes.
- Rich shares: drop an event, an issue, a node, or a topology snapshot into a chat as a card that the coworker can open in their own Neuro.AA.
- Team room: an E2E-capable MUC for the ops team.

### 3.5 Chat admin console

- Chat admin as a proper console, not a chat bubble list: command autocomplete built from the server's own `help` output, history, multi-line script mode, result rendering (Markdown tables → sortable tables, GraphViz → interactive graph).
- **Fan-out**: pick several Neurons (or a MUC they share), run a command once, see per-server results side by side and the consolidated result.
- Destructive commands (`Reboot`, `Stop`, `Repair`, `upgrade`, `disable account`) need a typed confirmation that names the target server.

### 3.6 Topology

- Render `ParentGraph`, `S2SGraph`, `GroupGraph` and `TopologyQuery` results (fanned out across the fleet) as one interactive map: zoom, pan, search, click a Neuron to open it.
- Health overlay from the fleet data (error rate, clients, last seen).
- `TopologyQuery SCRIPT` presets ("clients per Neuron", "version per Neuron") shown as node labels or heat colour.

### 3.7 Digital identities

- Per account: list legal identities and their states, attributes, attachments, validity and expiry; apply for a new one; obsolete/compromise; see and respond to petitions; sign/review as a peer when allowed.
- Server-side view (admin): pending identity applications and the Neuron's own legal ID (`LegalId` command), with expiry warnings.
- Contracts: browse, view, sign in a later phase (scope to confirm).

### 3.8 The rest of IoTClient

Thing registry and provisioning (claim, search, owner questions), PubSub/PEP, remote desktop, script console. These land in the parity sweep phase.

## 4. Architecture

```
┌────────────────────────── Neuro.AA (one desktop app) ──────────────────────────┐
│                                                                                  │
│  UI  (Avalonia, MVVM; Windows, macOS, Linux)                                     │
│   Fleet · Events · Sources · Chat · Admin console · Topology · Identities       │
│        ▲  view models bind to engine services and live observable collections    │
│        ▼                                                                          │
│  Engine  (.NET 10, Waher libraries)                                              │
│   AccountManager ── per account: XmppClient + Concentrator, Sensor, Control,     │
│                     Contracts, MUC, PubSub/PEP, E2E/P2P, Provisioning,           │
│                     ThingRegistry, HTTP upload, Software, EventLog clients       │
│   EventIngest ───── sniffer stream + XEP-0337 search backfill + notifications    │
│                     → normalize → fingerprint → local store → alert rules        │
│   FleetService ──── periodic perf/protocol reads, chat-admin probes              │
│   CommandBus ────── audited admin actions, confirmations, fan-out                │
│   Vault ─────────── credentials and keys in the OS keychain                      │
│   LocalStore ────── Waher.Persistence.Files (encrypted), full-text index         │
│                                                                                  │
└──────────────┬──────────────────────────┬──────────────────────────┬────────────┘
               │ XMPP (c2s)               │ XMPP (c2s)               │ ...
          Neuron A                    Neuron B                   Neuron N
```

Key decisions:

- **The engine uses the Waher libraries directly**, the same NuGet versions the Neurons run. This gives protocol parity with IoTClient for free and keeps us honest: we only use what the Neuron exposes.
- **The UI never speaks XMPP.** The engine is a plain .NET library with no UI dependency. View models talk to engine service interfaces, so the engine can be tested headless and could later run as a separate tray process.
- **Everything live is a stream with backpressure**: events, sniffers, presence, chat. Streams are batched onto the UI thread (no per-event dispatch), and every long list is virtualized.
- **Local-first**: events, issues, chat history and fleet samples are stored locally, so the app opens instantly and works for analysis even when a Neuron is unreachable.
- **One process for now.** UI and engine share a process. If the watch agent (bold idea 10) is built, the engine moves behind a local IPC boundary; the service interfaces are designed so that is a mechanical change.
- **Platform code stays at the edges**: keychain, notifications, tray, deep links and file locations sit behind small interfaces with one implementation per OS.

Solution layout:

```
Neuro.AA/
  Neuro.AA.slnx
  src/Neuro.AA.Engine/             .NET 10 library: accounts, ingest, store, services (no UI)
  src/Neuro.AA.Markdown.Avalonia/  Avalonia renderer for Waher Markdown documents
  src/Neuro.AA.App/                Avalonia app: views, view models, design system
  src/Neuro.AA.Desktop/            Entry point and per-OS integration (keychain, tray, notifications)
  tests/Neuro.AA.Engine.Tests/     unit tests + integration tests against a local Neuron
  tests/Neuro.AA.App.Tests/        Avalonia.Headless UI tests
  docs/                            PRODUCT.md, DESIGN.md, ADRs
```

## 5. Tech stack

Decided 2026-11-09: **Avalonia**, all C#, running on **Windows, macOS and Linux**. Versions checked on nuget.org the same day.

| Layer | Choice | Notes |
|---|---|---|
| Runtime | .NET 10 | SDK 10.0.403 installed |
| Protocols | Waher.* NuGet packages (XMPP 4.4.0, Concentrator 4.4.0, Contracts 5.5.0, P2P 5.0.0, Markdown 6.4.1, ...) | netstandard2.1, so they run on all three OSes; avoid Windows-only Waher packages such as `Waher.Events.WindowsEventLog` |
| UI framework | Avalonia 12.1.6 (MIT), Fluent theme as the base, fully re-tokened | One codebase, native windows on each OS |
| MVVM and live data | CommunityToolkit.Mvvm 8.4.2, DynamicData 9.5.0 | Source-generated view models; DynamicData for filtered, sorted, batched live collections (event stream, roster, node tree) |
| Lists and trees | Our own virtualized list and flattened tree controls | `Avalonia.Controls.TreeDataGrid` 12.x now requires a commercial AvaloniaUI licence, so we avoid it |
| Text and code | AvaloniaEdit 12.0.0 (MIT) | Script console, XML sniffer, stack traces, Markdown source |
| Charts | ScottPlot.Avalonia 5.1.59 (MIT) for full charts; custom-drawn sparklines | Sparklines are drawn directly for speed |
| Graphs | Microsoft.Msagl 1.1.6 (MIT) layout + our own Avalonia rendering, with a small DOT parser | The Neuron returns GraphViz DOT; no dependency on a Graphviz install on the operator's machine |
| Markdown | Waher.Content.Markdown parser + our own Avalonia renderer (`Neuro.AA.Markdown.Avalonia`) | Same parser as the Neuron, so tables, code, dot/graph blocks and notifications render with full fidelity |
| Storage | Waher.Persistence.Files + full-text search | Same persistence as the Neuron. Verify encryption works on macOS and Linux in Phase 0; fall back to SQLite if not |
| Secrets | OS keychain: Windows Credential Manager, macOS Keychain, Linux Secret Service (via Devlooped.CredentialManager 2.7.0) | Encrypted-file fallback with a master password when no keychain exists (headless Linux) |
| Tray and notifications | Avalonia `TrayIcon`; native notifications through small per-OS adapters | Existing notification packages for Avalonia are stale (0.10 era), so we write thin adapters |
| Packaging and updates | Velopack 1.2.x | Windows installer, macOS .app (signing and notarization needed), Linux AppImage; built-in updates |
| Tests | xUnit, Avalonia.Headless.XUnit 12.1.6, integration tests against a local Neuron | Live Neurons are tested by Mille later |
| CI | Build and test on Windows, macOS and Linux runners | Hosting to confirm |

## 6. UX and design direction

Mode: **Operate**. Success means an operator finds the problem and acts on it quickly; the interface should disappear into the task. Ambition shows as precision, density and calm, not decoration.

Layout:
- A slim left rail with the workspaces: **Fleet, Events, Sources, Chat, Console, Topology, Identities**. An **Attention** count at the top of the rail collects everything that needs a human.
- A top bar with the **account lens** (chips for each connected account; click to scope, shift-click to add) and a **command palette** (Ctrl+K) that reaches every server, node, contact, command and saved view.
- Content uses list + inspector panes, not modal dialogs. Detail opens in a right-hand inspector or a split, so context stays visible.
- Any panel can be popped out to its own window (event stream on a second monitor is a real use case).

Visual principles:
- Restrained colour. Neutral surfaces with a slightly different second layer for rails and panels. Colour is reserved for state: the eight event types (Debug, Informational, Notice, Warning, Error, Critical, Alert, Emergency) get one consistent severity ramp used everywhere, from a row marker to a sparkline.
- One well-tuned sans for UI and a monospace for JIDs, IDs, stack traces and XML. Tabular numerals in all metrics.
- Dark and light themes from day one, both designed, not inverted.
- Motion only to convey state (new event arrives, connection drops, panel opens), 150–200 ms.
- Keyboard first: j/k through lists, Enter to open, e to acknowledge, m to mute, / to filter, g then a letter to jump to a workspace.
- Empty states teach (for example the Events workspace without admin rights explains what is missing and how to get it).
- Every interactive component ships with all states: default, hover, focus, active, disabled, loading, error. Skeletons, not spinners.

Per platform:
- Shortcuts follow the OS (Cmd on macOS, Ctrl elsewhere); the palette shows the right glyphs.
- macOS gets a native menu bar (`NativeMenu`), traffic-light window controls and the system font; Windows and Linux get an integrated title bar.
- Follow the OS light/dark setting by default, with a manual override.
- Fonts: the UI font ships with the app, so it looks the same everywhere. Same for the monospace font.

Trust and safety in the UI:
- Every view shows which account and server it acts on. Destructive actions name the target and need typed confirmation.
- A per-account **read-only** switch for production accounts.
- A local audit log of every admin action we send.

Before building, the impeccable workflow wants a PRODUCT.md from a short interview (who uses it, in what situations, what success looks like). The questions are in section 10. The visual world (DESIGN.md) is then chosen with two or three concept directions to pick from.

## 7. Security

- Credentials and private keys (E2E, legal identity) are stored in the OS keychain of the signed-in user; nothing secret in config files or logs.
- Prefer password hashes / SASL mechanisms the Neuron supports; never log stanzas containing credentials (sniffer redaction).
- E2E key changes are surfaced, never silently accepted.
- Admin actions are audited locally with account, server, command and result.
- No outbound network except to the configured Neurons (and nothing else unless an optional feature is enabled).

## 8. Roadmap

Each phase ends in something usable. Estimates assume one developer with Claude and are rough.

| Phase | Goal | Contents | Rough size |
|---|---|---|---|
| 0. Foundations | A shell that connects | Repo and solution, engine library, Avalonia shell on all three OSes, multi-account connect with keychain vault, connection health, sniffer panel, design tokens, PRODUCT.md and DESIGN.md, local test Neuron setup | 2 weeks |
| 1. Monitor | Replace "hopeless" event monitoring | Live event stream via `EventsRoot` sniffer, history backfill via event log search, local store, filters and saved views, issue grouping, event detail, alerts and toasts, Fleet overview with performance and protocol stats, notification parsing into Attention | 3–4 weeks |
| 2. Sources and nodes | Retire IoTClient for daily admin | Data sources and node tree, inspector, data forms, add/edit/delete/move/copy/paste, commands, queries as reports, sensor readout and subscriptions, control, node search | 3–4 weeks |
| 3. Communication | Retire Lil'Sis for chat | Roster, presence, subscriptions, unified inbox, Markdown, MUC, E2E coworker chat with trust indicators, rich shares, chat admin console with autocomplete and fan-out | 3 weeks |
| 4. Identity and topology | The fleet as one picture | Legal identities and petitions, Neuron legal ID status, topology map from consolidated graphs with health overlay | 2–3 weeks |
| 5. Parity sweep | Nothing left in IoTClient | Thing registry and provisioning, PubSub/PEP, remote desktop, script console, raw XMPP console, software packages and update control | 2–3 weeks |
| 6. Bold ideas | Better than anything we have | Pick from section 9 | open |

Phase 1 comes before parity on purpose: monitoring is the biggest pain today and the strongest reason for the team to switch.

## 9. Bold ideas

1. **Fleet room**: Neuro.AA creates a private MUC and asks each Neuron to `join` it. From then on any chat admin command fans out to the whole fleet and the app shows the Neuron's own consolidated result (graphs and tables), using the mechanism the Neuron already implements.
2. **Release radar**: correlate new exception fingerprints with `BuildTime`, `Versions` and package installs. "This exception first appeared 4 minutes after package X 1.2.3 was installed on 3 servers."
3. **Timeline scrubber**: one time axis across all servers combining events, CPU/memory, client counts and notifications. Drag to a moment and see the state of the fleet then.
4. **Incident mode**: one click opens an E2E incident room with the relevant coworkers, pins the issue, streams new matching events into it, and produces a Markdown post-mortem draft at the end.
5. **Runbooks**: saved, parameterized sequences of chat admin commands and scripts, with dry run, per-step results and optional four-eyes approval for destructive steps.
6. **Config diff**: compare `GatewayConfig` subtrees (ports, domains, event sinks, login auditor) between two Neurons, or the same Neuron over time.
7. **Ask the fleet**: an assistant panel that uses the Neuron's own MCP servers (events, identity, XMPP) plus the local store, so you can ask "what changed on prod before the 14:02 errors?" and get an answer with links into the app. Off by default, explicit opt-in, no data leaves without it.
8. **Topology heat**: run a `TopologyQuery` script on every Neuron (for example client count or error rate) and paint the federation map with it, live.
9. **Shareable links**: `neuroaa://` deep links for an event, issue, node or view; paste one in chat and a coworker lands on the same thing.
10. **Watch agent**: the engine keeps running headless in the tray and alerts even when the window is closed.

## 10. Decisions and open questions

Decided by Mille on 2026-11-09:
1. UI stack: **Avalonia** (all C#).
2. Platforms: **Windows, macOS and Linux**.
3. Event sink nodes on the Neurons: **no, not now**. Live events come from sniffing `EventsRoot`, history from event log search.
4. Development and testing happen against **local Neurons**, which Claude may start and run. Mille tests against live Neurons later.
5. Users: **Neuron administrators and operators, company internal**. No external users.

Still open (not blocking Phase 0):
6. Contracts: browse and sign in this app, or only identities for now?
7. What is the first thing an operator should see when the app opens?
8. What does "Neuro.AA" stand for, and should it carry Neuro / TAG branding or stay a neutral internal tool?

## 11. Risks

- **Event volume**: busy Neurons can log a lot. Mitigation: server-side filtering where possible (event filter nodes), batching, and a capped local store with retention rules.
- **Sniffer sessions are ephemeral**: if the app disconnects we miss live events. Mitigation: backfill from the event log search on reconnect; the optional event sink node.
- **Rights**: many features need admin. The app must degrade gracefully and say what is missing.
- **Protocol coverage**: some IoTClient features are rarely used; we confirm with the team before spending time on them in phase 5.
- **Cross-platform gaps**: encrypted Waher persistence, keychains on headless Linux, tray support on some Linux desktops, and macOS signing/notarization. Each gets checked in Phase 0 with a fallback ready.
- **Remote desktop** in IoTClient is Windows-oriented; on macOS and Linux it may be view-only or come later.
