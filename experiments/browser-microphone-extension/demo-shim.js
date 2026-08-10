"use strict";

globalThis.__TYPEWHISPER_POC_OPEN_SHADOW__ = true;

let mockRecording = false;
const listeners = [];
globalThis.chrome = {
  runtime: {
    lastError: null,
    onMessage: {
      addListener(listener) {
        listeners.push(listener);
      }
    },
    sendMessage(message, callback) {
      if (message.type === "typewhisper.start") {
        mockRecording = true;
        window.setTimeout(() => callback({ ok: true, state: "recording", sessionId: "demo" }), 25);
        return;
      }

      if (message.type === "typewhisper.stop" && mockRecording) {
        mockRecording = false;
        window.setTimeout(() => callback({
          ok: true,
          state: "completed",
          text: "TypeWhisper browser proof of concept"
        }), 150);
        return;
      }

      mockRecording = false;
      window.setTimeout(() => callback({ ok: true, state: "cancelled" }), 0);
    }
  }
};
