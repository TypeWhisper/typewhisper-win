"use strict";

const nativeHostName = "com.typewhisper.browser_bridge";
const allowedMessageTypes = new Set([
  "typewhisper.start",
  "typewhisper.stop",
  "typewhisper.cancel"
]);
const allowedResponseStates = new Set([
  "ready",
  "recording",
  "processing",
  "completed",
  "cancelled",
  "error"
]);

const activeSessionStorageKey = "typewhisperActiveSession";

async function getActiveSession() {
  const stored = await chrome.storage.session.get(activeSessionStorageKey);
  return stored[activeSessionStorageKey] || null;
}

async function setActiveSession(session) {
  if (session) {
    await chrome.storage.session.set({ [activeSessionStorageKey]: session });
  } else {
    await chrome.storage.session.remove(activeSessionStorageKey);
  }
}

function senderIdentity(sender) {
  const tabId = sender?.tab?.id;
  if (!Number.isInteger(tabId)) {
    return null;
  }

  return { tabId, frameId: Number.isInteger(sender.frameId) ? sender.frameId : 0 };
}

function sameIdentity(left, right) {
  return left && right && left.tabId === right.tabId && left.frameId === right.frameId;
}

function sanitizeNativeResponse(response) {
  if (!response || response.ok !== true) {
    return {
      ok: false,
      state: "error",
      error: String(response?.error || "TypeWhisper browser bridge is unavailable.").slice(0, 500)
    };
  }

  const state = allowedResponseStates.has(response.state) ? response.state : "error";
  return {
    ok: state !== "error",
    state,
    sessionId: typeof response.session_id === "string" ? response.session_id.slice(0, 100) : null,
    text: typeof response.text === "string" ? response.text.slice(0, 200000) : null,
    error: typeof response.error === "string" ? response.error.slice(0, 500) : null
  };
}

function sendNative(command, sessionId) {
  return new Promise((resolve) => {
    chrome.runtime.sendNativeMessage(
      nativeHostName,
      { protocol_version: 1, command, session_id: sessionId || null },
      (response) => {
        const error = chrome.runtime.lastError;
        if (error) {
          resolve({ ok: false, state: "error", error: error.message });
          return;
        }

        resolve(sanitizeNativeResponse(response));
      });
  });
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  const identity = senderIdentity(sender);
  if (!identity || !allowedMessageTypes.has(message?.type)) {
    return false;
  }

  (async () => {
    if (message.type === "typewhisper.start") {
      const activeSession = await getActiveSession();
      if (activeSession) {
        sendResponse({ ok: false, state: "error", error: "TypeWhisper is already active in another field." });
        return;
      }

      const response = await sendNative("start", null);
      if (response.ok && response.state === "recording") {
        await setActiveSession({ ...identity, sessionId: response.sessionId });
      }
      sendResponse(response);
      return;
    }

    const activeSession = await getActiveSession();
    if (!sameIdentity(identity, activeSession)) {
      sendResponse({ ok: false, state: "error", error: "This field does not own the active TypeWhisper session." });
      return;
    }

    const command = message.type === "typewhisper.stop" ? "stop" : "cancel";
    const response = await sendNative(command, activeSession.sessionId);
    await setActiveSession(null);
    sendResponse(response);
  })().catch((error) => {
    setActiveSession(null).finally(() => {
      sendResponse({ ok: false, state: "error", error: String(error?.message || error) });
    });
  });

  return true;
});
