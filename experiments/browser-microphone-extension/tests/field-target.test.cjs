"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { isSupportedTarget, captureTarget, insertText } = require("../field-target.js");

class FakeTextControl extends EventTarget {
  constructor(tagName, value) {
    super();
    this.tagName = tagName;
    this.type = "text";
    this.value = value;
    this.selectionStart = 0;
    this.selectionEnd = 0;
    this.isConnected = true;
    this.disabled = false;
    this.readOnly = false;
  }

  setRangeText(text, start, end) {
    this.value = this.value.slice(0, start) + text + this.value.slice(end);
    this.selectionStart = start + text.length;
    this.selectionEnd = this.selectionStart;
  }
}

const fakeDocument = {
  defaultView: { Event },
  createTextNode(text) {
    return { text, isConnected: true };
  }
};

test("normal input replaces the captured selection and raises input", () => {
  const input = new FakeTextControl("INPUT", "before selected after");
  input.selectionStart = 7;
  input.selectionEnd = 15;
  let inputEvents = 0;
  input.addEventListener("input", () => inputEvents++);

  const snapshot = captureTarget(input, null);
  const result = insertText(snapshot, "dictated", fakeDocument);

  assert.deepEqual(result, { ok: true });
  assert.equal(input.value, "before dictated after");
  assert.equal(input.selectionStart, 15);
  assert.equal(inputEvents, 1);
});

test("password and disconnected fields are rejected", () => {
  const password = new FakeTextControl("INPUT", "secret");
  password.type = "password";
  assert.equal(isSupportedTarget(password), false);

  const input = new FakeTextControl("TEXTAREA", "text");
  input.selectionStart = input.selectionEnd = 4;
  const snapshot = captureTarget(input, null);
  input.isConnected = false;
  assert.deepEqual(insertText(snapshot, " more", fakeDocument), {
    ok: false,
    reason: "target-unavailable"
  });
});

test("contenteditable replaces its captured range and restores the caret", () => {
  const inserted = [];
  const editable = new EventTarget();
  editable.tagName = "DIV";
  editable.isContentEditable = true;
  editable.isConnected = true;
  editable.contains = () => true;

  const range = {
    commonAncestorContainer: { isConnected: true },
    cloneRange() { return this; },
    deleteContents() { inserted.push("deleted"); },
    insertNode(node) { inserted.push(node.text); },
    setStartAfter() { inserted.push("caret"); },
    collapse() {}
  };
  const selection = {
    rangeCount: 1,
    getRangeAt() { return range; },
    removeAllRanges() { inserted.push("selection-cleared"); },
    addRange() { inserted.push("selection-restored"); }
  };
  const documentRef = {
    ...fakeDocument,
    defaultView: { Event, getSelection: () => selection }
  };
  let inputEvents = 0;
  editable.addEventListener("input", () => inputEvents++);

  const snapshot = captureTarget(editable, selection);
  const result = insertText(snapshot, "dictated", documentRef);

  assert.deepEqual(result, { ok: true });
  assert.deepEqual(inserted, [
    "deleted",
    "dictated",
    "caret",
    "selection-cleared",
    "selection-restored"
  ]);
  assert.equal(inputEvents, 1);
});
