# Import words and snippets from another app

In the Windows app, open **Dictionary** or **Snippets** from Quick Launch, then
choose **Import → From another app…**.

The setup wizard also offers **Import from another app** on its welcome page.
It detects data files in the default locations and offers separate word and snippet
imports using the same review. Detection does not read entries or import anything.
Choose **Continue** to proceed without importing; the option remains available later
in Dictionary and Snippets.

| Source | Supported content | Default Windows location |
| --- | --- | --- |
| Wispr Flow | Words, alternate spellings as corrections, and snippets | `%APPDATA%\Wispr Flow\flow.sqlite` |
| Handy | Custom words | `%APPDATA%\com.pais.handy\settings_store.json` |

The app checks the selected source's default location. Use **Choose a file instead**
for a copied database or a portable Handy installation. Import words and corrections
from the Words or Corrections tab; import snippets separately from the Snippets tab.
The existing TypeWhisper JSON import remains under **Import → TypeWhisper JSON…**.

Review the entries before choosing **Add**. Select a row to inspect its full content.
Only new entries are added. Duplicate entries are skipped, and conflicting entries
keep the existing TypeWhisper spelling or snippet, including its enabled state.
Term packs remain unchanged. Canceling the review changes nothing. If the destination
list changes during review, the import stops; start it again to review the current list.

Wispr Flow's database and write-ahead log are copied to a temporary folder. Source
hashes before and after copying must match the copies' SHA-256 hashes before SQLite
opens the copy. The original database is never opened with SQLite or written to.
Closing the app cancels ongoing copy and hash operations. If a temporary copy cannot
be removed immediately, abandoned copies older than a day are cleaned up on subsequent
launches and imports; active imports are protected by an exclusive lease.
Handy settings must produce two consecutive identical reads. Quitting the source app
and retrying can help when its data cannot be read consistently.

Deleted Wispr entries, entries belonging to the other import section, blank or oversized
entries, and incompatible text are excluded. Wispr corrections also contribute the
canonical spelling as a word. Handy filler words are not imported.

Foreign snippets containing TypeWhisper placeholders such as `{clipboard}` or `{date}`
are excluded because importing them would change their literal meaning. Corrections
containing TypeWhisper formatting escapes such as `\n` are excluded for the same reason.
Review these entries manually if you want to use TypeWhisper's dynamic formatting.

Each source is limited to 10,000 rows, including deleted and filtered entries.
Database copies are limited to 2 GB, source dictionary text to five million characters,
and Handy settings to 8 MB. Resulting catalogs must
fit the existing TypeWhisper transfer limits: 10,000 entries, 160 characters per phrase,
10,000 characters per expansion, and five million JSON characters.

This import is available on Windows. It does not add a macOS importer or CSV import.
