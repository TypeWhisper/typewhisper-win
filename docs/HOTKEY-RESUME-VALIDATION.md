# Hotkey recovery after sleep and session changes

WinUI watches `WM_POWERBROADCAST` and registers for `WM_WTSSESSION_CHANGE` on its primary window, including tray-only operation. These are the Windows [power resume](https://learn.microsoft.com/en-us/windows/win32/power/pbt-apmresumeautomatic) and [session change](https://learn.microsoft.com/en-us/windows/win32/termserv/wm-wtssession-change) notification paths.

On suspend, lock or disconnect, main/alternate dictation gestures are interrupted. An accepted start that has not finished is canceled; an already established recording is not discarded by this hotkey recovery component. Microphone/device recovery remains separate.

Resume, unlock and reconnect notifications schedule one recovery after a 500 ms settling interval. A newer transition invalidates the previous timer. Each low-level dictation hook, and the shared mouse hook for shortcuts on the middle or a side mouse button, is replaced on the installing UI thread, while ordinary `RegisterHotKey` reservations remain intact. Physical keys still held at recovery must be released before a new gesture can start. Saved bindings, recording modes and the user's paused-hotkeys preference are unchanged. Failed hook installation is reported through the existing tray error status and retried once after 1.5 seconds. Shutdown removes the session observer and invalidates outstanding timers.

## Physical validation

Run these checks on the selected build; automated gesture tests do not simulate
the Windows power subsystem or physical microphone reconnection.

1. With TypeWhisper only in the tray, dictate once, sleep, wake, unlock, release the wake/unlock keys, then dictate again without restarting the app.
2. Repeat with the main modifier-only chord and configured alternate Hold/Toggle shortcuts.
3. Lock/unlock without sleep, including a modifier held during unlock. No recording should start until the keys are released and a fresh chord is pressed.
4. Pause dictation hotkeys from the tray, sleep/wake, then confirm they remain paused. Resume them explicitly and verify the next gesture.
5. Repeat several cycles; check the workflow palette and workflow shortcuts still respond and no duplicate dictation starts occur.
6. Repeat check 1 with a dictation shortcut and an app action shortcut on a mouse side button, for example Mouse back button. After wake the button must start dictation or run the action, and must not also navigate back in the focused app.

Also disconnect and reconnect the microphone, then dictate without restarting.
Distinguish hotkey failures from microphone capture failures, where the hotkey
fires but audio does not start. Record the build, device and result for each check.
