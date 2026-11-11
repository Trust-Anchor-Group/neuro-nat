# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Stack

Avalonia 12 on .NET 10, all C#, one desktop app for Windows, macOS and Linux (decided by Mille, 2026-11-09). The Platform value above means one custom design language rendered the same on every OS. It is not a website. Small per-OS conventions apply: shortcut modifiers, the macOS menu bar, window controls, and following the OS light/dark setting.

## Users

Neuron administrators and operators inside the company. No external users. They run and support Neurons (TAG Neuron® / IoT Broker instances) and today switch between IoTClient (for sources and nodes) and Lil'Sis (for chat).

Typical situations, inferred from the brief and still to be confirmed:
- Routine checks across the servers they are responsible for.
- Investigating an incident: something is throwing exceptions, a server is unreachable, clients drop.
- Day-to-day administration: editing sources and nodes, accounts, users, software packages.
- Talking to coworkers about all of the above, securely.

## Product Purpose

One workspace to operate many Neurons at once. You sign in with several XMPP accounts and get live and historical events and exceptions across all servers, full administration of concentrator sources and nodes, chat admin, roster and chat, end-to-end encrypted chat with coworkers, digital identities, and a topology view of the federation.

Success: operators stop using IoTClient and Lil'Sis for daily work, and finding out "what is wrong, where, since when" takes seconds instead of scrolling a chat window.

## Positioning

It speaks the Neuron's own protocols with the Neuron's own libraries, so everything the Neuron exposes to an administrator is available, across many servers at once, without installing anything on the servers or changing their source. Monitoring is a first-class workspace, not a chat log.

## Operating Context

- Operators connect with accounts that are administrators on the Neuron (notification addresses, or users with the Admin privilege). Without that, event search and server reports are refused, and the app must say so.
- Live events come from sniffing the broker's `EventsRoot` data source; history comes from the XEP-0337 event log search. No event sink nodes are added to the Neurons (Mille, 2026-11-09).
- Chat admin commands and their GraphViz outputs are part of daily work; several Neurons can share a MUC room and answer one command together.
- Development and testing use local Neurons. Live Neurons are tested by Mille.
- Often used on large or multiple monitors during incidents; also on laptops.

## Capabilities and Constraints

- Never modify Neuron, IoTGateway or iotbroker source.
- Must do everything IoTClient does (see PLAN.md section 2.1), and more.
- Runs on Windows, macOS and Linux.
- Event volume can be high; lists must stay fast with hundreds of thousands of rows.
- Destructive server actions (reboot, stop, repair, upgrade, delete node) must always be explicit about which server they hit.

Decided:
- The app opens on the screen it was closed on, with its windows where they were. The first time, with an account set up, it opens on the Neurons list.

Undecided:
- Whether contracts (browse and sign) are in scope, or only identities.
- What "Neuro.AA" stands for and whether the app carries Neuro / TAG branding.

## Brand Commitments

- Look (Mille, 2026-11-09): modern and clean, responsive and clear, with designed light and dark modes. "No need to make crazy designs." Of the six sketched directions the standard dark ops console read best, "but we can do better". So the category standard is the commitment, executed at the craft level of the best tools in the category (proposed bar: Linear, Sentry, Grafana; Mille may name others), not a themed world.
- No name meaning or logo yet ("Neuro.AA" is the working name).

## Evidence on Hand

- Reference implementations: `C:\My Projects\IoTGateway\Clients\Waher.Client.WPF` (IoTClient), `C:\Program Files (x86)\LilSis\` (Lil'Sis chat), `C:\My Projects\iotbroker` (broker components and data sources).
- Chat admin command reference: `iotbroker\Waher.Service.IoTBroker.Documentation\Root\Documentation\Neuron\Administration\ChatAdmin.md`.
- No screenshots, user research or metrics have been gathered. Do not invent any.

## Product Principles

1. Find the problem first. Monitoring and triage come before everything else on screen.
2. Always say which server and which account an action targets.
3. Many servers, one workspace: every view can span the fleet or narrow to one Neuron.
4. Use only what the Neuron already exposes. No server changes, no hidden agents.
5. Keyboard first, dense and calm. Operators live in this tool for hours.
