# Dictation results

The normal dictation path transcribes speech, applies configured processing and
attempts automatic insertion. A result window appears when the text needs a
manual next step. The window does not require an approval to finish transcription.

| Heading | Why it appears | Next step |
| --- | --- | --- |
| Your dictation | **After recording** is set to **Review first**, or automatic insertion was turned off while dictating. | Copy the text and paste it into the desired field. |
| Text could not be inserted | Automatic insertion could not complete, for example because the target could not be restored or the clipboard was unavailable. | Check the target field, copy the text and paste any missing text. |
| Text processing did not finish | Speech recognition completed, but a workflow or text processor failed. | Check the retained text before copying it; it may lack the intended transformation. |
| Workflow action needs attention | The configured destination did not confirm successful delivery. | Read the reported outcome and check the destination before trying again. |

The window does not infer the exact native insertion failure from a generic
failure result. In particular, a failed delivery does not prove that no text
reached the target. Check the field to avoid inserting duplicates.

## Clipboard

With **After recording** set to **Copy to clipboard**, TypeWhisper copies the
finished text instead of inserting it, and the overlay confirms the copy.
No result window opens. If the clipboard cannot be written, for example because
another app holds it, the *Your dictation* window opens instead. Processing and
workflow action failures still open their result window.

**Preserve clipboard content** (on by default) restores your previous clipboard
after TypeWhisper pastes. Turn it off to keep the transcribed text on the
clipboard after every insertion, so you can paste it again with Ctrl+V when no
text field had focus. The kept text appears in the Windows clipboard history.
The setting also applies to pasting the last dictation and to inserting from the
workflow palette. When a dictation paste is blocked or fails, TypeWhisper leaves
the text on the clipboard regardless of this setting, as long as the clipboard
is available.

## Copying and closing

**Copy text** copies the displayed result without closing it or running a plugin.
The confirmation explains that you can switch to the target field and paste.
**Close** closes the result window. The storage note tells you whether the text is
also in History; when History did not retain it, copy it before closing.

If action plugins are available, **More actions** reveals their picker and run
button. They are optional and execute only when selected and run. Do not retry an
unconfirmed external action without checking its destination first.

## Storage warnings

A History or History-audio save failure does not block delivery of successfully
processed text. TypeWhisper still attempts the selected paste or workflow action.
After successful delivery, a dismissible warning appears in the notice card at the overlay position,
without opening a result window or taking focus from the target application.

If delivery also fails, the result window keeps the text and reports the storage
warning alongside the reason for manual handling. Turning History off remains a
normal preference, not a storage error. The warning does not trigger an additional
History write.

## Reporting unexpected windows

Include the application version, heading and explanatory message, target app,
whether it happens only after startup, and the automatic-insertion setting.
Do not include private dictated text or credentials. Chromium/Electron can return
their render host instead of a text field on the first accessibility query.
Field capture retries briefly while the target stays in front; include a
fresh-start check when reporting insertion problems.
