# Where your data is kept

NAT keeps its data in `%AppData%\Neuro.Nat`. Everything it remembers is in one encrypted database
(`database\`): accounts, sign-ins, chat history and room passwords, Inbox items, settings, and the
signing keys of legal identities. Only three things are outside it:

- `secrets\database.key`, the key to the database, protected by Windows for the signed-in user;
- `logs\nat.log`, the log, which never holds message texts, passwords, tokens or sign-ins;
- `update-source.txt`, read by the updater before the app has started.

Older versions kept accounts, chats, the Inbox and settings in plain JSON files and each sign-in in
a file of its own. At start NAT moves what it finds of those into the database, checks that all of
it came across, and only then deletes the old file; what fails keeps its file and is tried again at
the next start. If the database cannot be opened, NAT says so and offers Try again, Start fresh (the
database is moved aside into a dated folder; nothing is deleted) and Quit.

On macOS and Linux there is no vault for the key yet, so there is no database: nothing is kept
between runs, and Settings says so.

The data folder of the version before the rename moves to `%AppData%\Neuro.Nat` once, at the first
start. `NAT_DATA_FOLDER` names another folder to use instead, for example a copy to try the app
against; nothing is moved then.
