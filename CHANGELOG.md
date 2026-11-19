# NAT changelog

## 0.8.0

- **Browse query results in Chat admin and runbooks.** Select a row to read every field and copy individual values, a row, or the whole result as CSV. Use **Show original** to inspect the script output.
- **Explore nested script results.** Expand objects by field name and lists by index. Copy a single value or an entire branch, including its collapsed children.

## 0.7.0

- **Choose the reporting period in App telemetry.** Select today, yesterday, the last seven days, last calendar week, the last 30 days, the last 12 full months, or custom UTC dates spanning multiple years. The dashboard shows the latest day the collector has published.
- **Explore and share a telemetry report.** The overview puts application decisions and NFC read success above the findings. Select a headline metric to inspect its chart and table. Copied links reopen the same collector, metric or page, and dates.
- **Approve package uploads with Legal ID.** Review the request in the NAT Inbox or your identity app. NAT reuses the session for later uploads to the same broker from the same NAT account. Web username and password sign-in is also available.
- **Install a downloaded package.** In a Neuron’s **Software → Downloaded packages**, select **Install…**, enter the package’s installation key, and choose whether to back up first. An installed package offers **Upgrade…** on the same row.

## 0.6.0

- **Check for NAT updates.** Open Settings and scroll to the version, or open the tray menu. Check for an update there and restart into it once the download finishes.
- **Choose a telemetry collector.** App telemetry shows the selected Neuron’s address. Switch between discovered collectors in the sidebar dashboard, or open **Choose collector…** from a Neuron’s telemetry tab.
- **Upload and upgrade packages from Software.** Upload a package to a broker with an optional signature, or let the broker sign it. To upgrade one installed package, select **Upgrade…** beside its name and choose whether to back up first. The confirmation shows the chat admin command that will run.

## 0.5.0

This release makes it easier to clear a noisy Inbox, spot recurring issues, and read results from several Neurons.

- **Manage recurring alerts.** Right-click an Inbox alert to mark all current matches across your Neurons as read. For future occurrences, you can mute pop-ups and sounds, hide the alert for an hour or until restored, or track each recurrence. In **All events**, right-click an event ID to hide it temporarily while scanning the rest.
- **Catch up after time away.** Reconnecting no longer floods you with pop-ups for historical events. **Mark all read** clears messages and notifications from the active Inbox. Requests that still need your decision stay visible.
- **Read and copy query results.** Chat admin and runbooks give each Neuron’s answer the full width. Long records wrap, and you can copy a selection or the whole answer.
- **Check performance and telemetry.** Read current CPU usage alongside the recent average and peak. If an existing connection is missing its telemetry dashboard, open the Neuron’s **Software** tab and select **Check app telemetry** to discover it or see what needs attention.
- **Navigate the fleet.** Back and Forward skip repeated destinations, and **Other servers** uses a more compact layout on the map.
