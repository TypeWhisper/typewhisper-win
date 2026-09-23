# File Memory acceptance

## Scope

Local fact storage with explicit workflow output actions and opt-in memory context for LLM workflows. The package remains independent of other provider migrations. No account or paid API is needed for storage or lexical retrieval.

## Automated evidence

- 24 plugin tests: atomic saves, edits, duplicate rejection, persisted reload, corrupt-file preservation, cancellation, ranked retrieval and package install/update/restart/uninstall.
- 1,321 presentation tests, including seven new memory workflow cases. The seven cases were rerun after the final continuation-context change.
- 283 portable SDK/host tests.
- Real runtime test stores a fact through `IActionPlugin`, retrieves it through the memory lease, then verifies old handles are rejected after disable/re-enable.
- Canonical Windows development build succeeded with File Memory 1.4.0 installed into the isolated development profile. Unrelated installation receipts were preserved.

## Manual acceptance

Settings entry creation, save, restart persistence, matching list search and empty search results were exercised in the running development host. The settings screenshot is in `docs/screenshots/file-memory/settings.png`. Two manual sample workflows were added without replacing existing workflows. The configured LM Studio request did not produce a completed result in the first UI attempt; live model output still needs acceptance. A provider refresh was found to overwrite run errors, which is now fixed so the diagnostic stays visible. Fake-model tests verify context delivery, not model quality.

Suggested test:

1. Open **File Memory – Merken** in Workflows and run it on `Project Aurora uses British English.`
2. Confirm the saved entry in File Memory settings. Edit it, save, and filter the list by `Aurora`.
3. Open **File Memory – Mit Kontext schreiben**. Ensure a working default LLM or an explicit provider/model is selected.
4. Run `Which language convention does Aurora use?` and verify the answer uses the saved fact.
5. Turn **Memory context** off and verify the workflow no longer receives the stored entry.

## Limitations

Retrieval matches words and phrases, not semantic embeddings. At most five facts, each limited to 1,600 characters, are included. No background fact extraction occurs. Only workflows explicitly configured to use memory transmit matches to their selected LLM. Existing saved entries are not automatically opted into any workflow.
