import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import test from "node:test";

const source = await readFile(new URL("../../src/BrowserCompanion/extension/content.js", import.meta.url), "utf8");

test("the content bridge carries wake refresh correlation with fresh page observations", () => {
    const events = new Map();
    const pageMessages = [];
    const workerMessages = [];
    let onMessage;
    const window = { addEventListener: (name, callback) => events.set(name, callback),
        postMessage: (message) => pageMessages.push(message) };
    const chrome = { runtime: { id: "extension", onMessage: { addListener: (callback) => { onMessage = callback; } },
        connect: () => ({ onDisconnect: { addListener() {} }, postMessage: (message) => workerMessages.push(message) }) } };
    vm.runInNewContext(source, { window, chrome, document: { querySelector: () => null } });
    let acknowledgement;
    onMessage({ type: "refresh", refreshId: "wake" }, { id: "extension" }, (result) => { acknowledgement = result; });
    assert.equal(acknowledgement.ok, true);
    assert.equal(pageMessages.at(-1).refreshId, "wake");
    assert.equal(workerMessages.length, 0, "Receipt of a refresh does not prove the page hook is ready");
    const snapshot = { channel: "jiripolasek-media-controls-v1", type: "snapshot", sequence: 2, refreshId: "wake",
        tabId: 999, players: [{ id: "element-1", capabilities: ["play"] }] };
    events.get("message")({ source: window, data: snapshot });
    assert.equal(workerMessages.length, 1);
    assert.equal(workerMessages[0].refreshId, "wake");
    assert.equal(workerMessages[0].players[0].id, "element-1");
    assert.equal(workerMessages[0].tabId, undefined);
    events.get("message")({ source: window, data: { ...snapshot, sequence: 1 } });
    assert.equal(workerMessages.length, 1, "Old page snapshots must not satisfy a wake refresh");
});

test("the content bridge reads a bounded site name from page metadata", () => {
    const events = new Map();
    const messages = [];
    const metadata = new Map();
    const window = { addEventListener: (name, callback) => events.set(name, callback), postMessage() {} };
    const chrome = { runtime: { onMessage: { addListener() {} },
        connect: () => ({ onDisconnect: { addListener() {} }, postMessage: (message) => messages.push(message) }) } };
    vm.runInNewContext(source, { window, chrome, document: { querySelector: (selector) => metadata.get(selector) } });
    let sequence = 0;
    function snapshot() {
        events.get("message")({ source: window, data: { channel: "jiripolasek-media-controls-v1", type: "snapshot",
            sequence: ++sequence, players: [], siteName: "Forged player name" } });
        return messages.at(-1).siteName;
    }
    metadata.set('meta[property="og:site_name"]', { content: "  Example\n  Music  " });
    metadata.set('meta[name="application-name"]', { content: "Application" });
    assert.equal(snapshot(), "Example Music");
    metadata.set('meta[property="og:site_name"]', { content: "  " });
    assert.equal(snapshot(), "Application");
    metadata.set('meta[name="application-name"]', { content: "x".repeat(200) });
    assert.equal(snapshot(), "x".repeat(128));
    metadata.clear();
    assert.equal(snapshot(), "");
});
