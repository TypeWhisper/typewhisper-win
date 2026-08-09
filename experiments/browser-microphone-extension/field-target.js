(function initializeFieldTarget(root, factory) {
  const api = factory();
  if (typeof module === "object" && module.exports) {
    module.exports = api;
  } else {
    root.TypeWhisperFieldTarget = api;
  }
})(typeof globalThis === "undefined" ? this : globalThis, function createFieldTargetApi() {
  "use strict";

  const textInputTypes = new Set(["", "text", "search", "email", "url", "tel"]);

  function isSupportedTarget(element) {
    if (!element || element.disabled || element.readOnly) {
      return false;
    }

    const tagName = String(element.tagName || "").toUpperCase();
    if (tagName === "TEXTAREA") {
      return true;
    }

    if (tagName === "INPUT") {
      return textInputTypes.has(String(element.type || "").toLowerCase());
    }

    return element.isContentEditable === true;
  }

  function captureTarget(element, selection) {
    if (!isSupportedTarget(element)) {
      return null;
    }

    const tagName = String(element.tagName || "").toUpperCase();
    if (tagName === "INPUT" || tagName === "TEXTAREA") {
      const valueLength = String(element.value || "").length;
      const start = Number.isInteger(element.selectionStart) ? element.selectionStart : valueLength;
      const end = Number.isInteger(element.selectionEnd) ? element.selectionEnd : start;
      return {
        kind: "text-control",
        element,
        start: Math.max(0, Math.min(start, valueLength)),
        end: Math.max(0, Math.min(end, valueLength))
      };
    }

    if (!selection || selection.rangeCount === 0) {
      return null;
    }

    const range = selection.getRangeAt(0);
    if (!element.contains(range.commonAncestorContainer)) {
      return null;
    }

    return {
      kind: "contenteditable",
      element,
      range: range.cloneRange()
    };
  }

  function createInputEvent(documentRef, text) {
    const InputEventConstructor = documentRef?.defaultView?.InputEvent;
    if (typeof InputEventConstructor === "function") {
      return new InputEventConstructor("input", {
        bubbles: true,
        composed: true,
        data: text,
        inputType: "insertText"
      });
    }

    const EventConstructor = documentRef?.defaultView?.Event || globalThis.Event;
    return new EventConstructor("input", { bubbles: true, composed: true });
  }

  function insertText(snapshot, text, documentRef) {
    if (!snapshot || typeof text !== "string" || text.length === 0) {
      return { ok: false, reason: "empty" };
    }

    const element = snapshot.element;
    if (!element || element.isConnected === false || !isSupportedTarget(element)) {
      return { ok: false, reason: "target-unavailable" };
    }

    if (snapshot.kind === "text-control") {
      const currentValue = String(element.value || "");
      const start = Math.max(0, Math.min(snapshot.start, currentValue.length));
      const end = Math.max(start, Math.min(snapshot.end, currentValue.length));

      if (typeof element.setRangeText === "function") {
        element.setRangeText(text, start, end, "end");
      } else {
        element.value = currentValue.slice(0, start) + text + currentValue.slice(end);
        const caret = start + text.length;
        element.selectionStart = caret;
        element.selectionEnd = caret;
      }

      element.dispatchEvent(createInputEvent(documentRef, text));
      return { ok: true };
    }

    if (snapshot.kind !== "contenteditable" || !snapshot.range) {
      return { ok: false, reason: "unsupported" };
    }

    const range = snapshot.range.cloneRange();
    if (range.commonAncestorContainer?.isConnected === false) {
      return { ok: false, reason: "selection-unavailable" };
    }

    const textNode = documentRef.createTextNode(text);
    range.deleteContents();
    range.insertNode(textNode);
    range.setStartAfter(textNode);
    range.collapse(true);

    const selection = documentRef.defaultView?.getSelection?.();
    selection?.removeAllRanges();
    selection?.addRange(range);
    element.dispatchEvent(createInputEvent(documentRef, text));
    return { ok: true };
  }

  return { isSupportedTarget, captureTarget, insertText };
});
