(function initializeTypeWhisperBrowserControl() {
  "use strict";

  const fieldTarget = globalThis.TypeWhisperFieldTarget;
  if (!fieldTarget || document.documentElement.dataset.typewhisperBrowserPoc === "active") {
    return;
  }
  document.documentElement.dataset.typewhisperBrowserPoc = "active";

  let focusedTarget = null;
  let capturedTarget = null;
  let state = "ready";

  const host = document.createElement("div");
  host.dataset.typewhisperControl = "";
  host.style.position = "fixed";
  host.style.zIndex = "2147483647";
  host.style.display = "none";
  host.style.pointerEvents = "auto";
  document.documentElement.appendChild(host);

  const shadowMode = globalThis.__TYPEWHISPER_POC_OPEN_SHADOW__ === true ? "open" : "closed";
  const shadow = host.attachShadow({ mode: shadowMode });
  const style = document.createElement("style");
  style.textContent = `
    :host { all: initial; }
    button {
      all: initial;
      box-sizing: border-box;
      width: 30px;
      height: 30px;
      border: 1px solid rgba(255, 255, 255, 0.22);
      border-radius: 999px;
      background: #24212f;
      color: #ffffff;
      box-shadow: 0 3px 12px rgba(0, 0, 0, 0.3);
      cursor: pointer;
      display: grid;
      place-items: center;
      font: 600 15px/1 system-ui, sans-serif;
    }
    button:hover { background: #342c48; }
    button:focus-visible { outline: 2px solid #9d82ff; outline-offset: 2px; }
    button[data-state="recording"] { background: #b42318; }
    button[data-state="processing"] { background: #5d4d8b; cursor: progress; }
    button[data-state="error"] { background: #7a271a; }
    @media (prefers-reduced-motion: no-preference) {
      button[data-state="recording"] { animation: typewhisper-pulse 1.4s ease-in-out infinite; }
      @keyframes typewhisper-pulse { 50% { transform: scale(1.08); } }
    }
  `;
  const button = document.createElement("button");
  button.type = "button";
  button.textContent = "●";
  shadow.append(style, button);

  function setState(nextState, error) {
    state = nextState;
    button.dataset.state = nextState;
    button.disabled = nextState === "processing";
    const labels = {
      ready: "Start TypeWhisper dictation",
      recording: "Stop TypeWhisper dictation",
      processing: "TypeWhisper is processing",
      error: error || "TypeWhisper is unavailable"
    };
    button.setAttribute("aria-label", labels[nextState] || labels.ready);
    button.title = labels[nextState] || labels.ready;
  }

  function positionControl() {
    if (!focusedTarget || !fieldTarget.isSupportedTarget(focusedTarget)) {
      host.style.display = "none";
      return;
    }

    const rect = focusedTarget.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0 || rect.bottom < 0 || rect.top > window.innerHeight) {
      host.style.display = "none";
      return;
    }

    host.style.display = "block";
    host.style.left = `${Math.max(4, Math.min(window.innerWidth - 34, rect.right - 34))}px`;
    host.style.top = `${Math.max(4, Math.min(window.innerHeight - 34, rect.top + 4))}px`;
  }

  function sendMessage(message) {
    return new Promise((resolve) => {
      chrome.runtime.sendMessage(message, (response) => {
        const error = chrome.runtime.lastError;
        resolve(error
          ? { ok: false, state: "error", error: error.message }
          : response || { ok: false, state: "error", error: "No response from TypeWhisper." });
      });
    });
  }

  async function toggleDictation() {
    if (state === "processing") {
      return;
    }

    if (state !== "recording") {
      const selection = window.getSelection?.();
      capturedTarget = fieldTarget.captureTarget(focusedTarget, selection);
      if (!capturedTarget) {
        setState("error", "The selected field is no longer available.");
        return;
      }

      setState("processing");
      const response = await sendMessage({ type: "typewhisper.start" });
      if (response.ok && response.state === "recording") {
        setState("recording");
      } else {
        capturedTarget = null;
        setState("error", response.error);
      }
      return;
    }

    setState("processing");
    const response = await sendMessage({ type: "typewhisper.stop" });
    if (response.ok && response.state === "completed" && response.text) {
      const insertion = fieldTarget.insertText(capturedTarget, response.text, document);
      capturedTarget = null;
      if (!insertion.ok) {
        setState("error", "The original field or selection is no longer available.");
        return;
      }
      setState("ready");
      positionControl();
      return;
    }

    capturedTarget = null;
    setState("error", response.error || "TypeWhisper did not return a transcription.");
  }

  button.addEventListener("pointerdown", (event) => event.preventDefault());
  button.addEventListener("click", () => void toggleDictation());

  document.addEventListener("focusin", (event) => {
    const candidate = event.target;
    if (fieldTarget.isSupportedTarget(candidate)) {
      focusedTarget = candidate;
      if (state !== "recording" && state !== "processing") {
        setState("ready");
      }
      positionControl();
    }
  }, true);

  document.addEventListener("focusout", () => {
    window.setTimeout(() => {
      if (state === "recording" || state === "processing") {
        return;
      }
      if (!fieldTarget.isSupportedTarget(document.activeElement)) {
        host.style.display = "none";
      }
    }, 0);
  }, true);

  window.addEventListener("scroll", positionControl, true);
  window.addEventListener("resize", positionControl);
  window.addEventListener("pagehide", () => {
    if (state === "recording") {
      chrome.runtime.sendMessage({ type: "typewhisper.cancel" });
    }
  });

  setState("ready");
})();
