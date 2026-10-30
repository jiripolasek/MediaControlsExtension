import assert from "node:assert/strict";
import test from "node:test";
import { activateSource, cleanPlayers, commandError } from "../../src/BrowserCompanion/extension/routing.mjs";

test("page data cannot supply privileged routing or unlimited capabilities", () => {
    const result = cleanPlayers([{ id: "element-1", tabId: 99, title: "x".repeat(1000), state: "playing",
        capabilities: ["pause", "pause", "execute-code", "activate"], volume: Infinity }, { id: "arbitrary" }]);
    assert.equal(result.length, 1);
    assert.equal(result[0].tabId, undefined);
    assert.equal(result[0].title.length, 512);
    assert.equal(result[0].volume, null);
    assert.deepEqual(result[0].capabilities, ["pause", "activate"]);
    for (const artwork of ["file:///private.png", "data:image/png;base64,eA==", "https://user:secret@site.test/image.png", "x".repeat(2049)])
        assert.equal(cleanPlayers([{ id: "document", artwork }])[0].artwork, "");
});

test("commands reject old epochs, expired deadlines, unsupported operations and invalid arguments", () => {
    const session = { capabilities: ["play", "volume", "mute"] };
    const request = { epoch: "current", operation: "play", deadline: Date.now() + 5000 };
    assert.equal(commandError(request, session, "current"), null);
    assert.equal(commandError(request, session, "replacement"), "session-gone");
    assert.equal(commandError(request, null, "current"), "session-gone");
    assert.equal(commandError({ ...request, deadline: 0 }, session, "current"), "unavailable");
    assert.equal(commandError({ ...request, operation: "nexttrack" }, session, "current"), "unsupported");
    assert.equal(commandError({ ...request, operation: "volume", value: 2 }, session, "current"), "failed");
    assert.equal(commandError({ ...request, operation: "mute", value: "true" }, session, "current"), "failed");
});

test("activation uses the moved tab's current window and restores it before focusing", async () => {
    const calls = [];
    const api = {
        tabs: { update: async (...args) => calls.push(["tab", ...args]), get: async () => ({ windowId: 42 }) },
        windows: { get: async (id) => { assert.equal(id, 42); return { state: "minimized", focused: true }; },
            update: async (...args) => calls.push(["window", ...args]) }
    };
    await activateSource(api, { tabId: 7, windowId: 1 }, () => true, async () => calls.push(["probe"]));
    assert.deepEqual(calls, [["probe"], ["tab", 7, { active: true }], ["probe"],
        ["window", 42, { state: "normal" }], ["window", 42, { focused: true }]]);
});

test("navigation during probe prevents all activation mutations", async () => {
    let current = true;
    await assert.rejects(activateSource({}, { tabId: 7 }, () => current, async () => { current = false; }), /session-gone/);
});

test("navigation after tab selection prevents focusing a replacement source", async () => {
    let current = true;
    const api = { tabs: { update: async () => { current = false; } } };
    await assert.rejects(activateSource(api, { tabId: 7 }, () => current, async () => {}), /session-gone/);
});

test("maximized windows retain their state", async () => {
    const calls = [];
    const api = { tabs: { update: async () => {}, get: async () => ({ windowId: 8 }) },
        windows: { get: async () => ({ state: "maximized", focused: true }), update: async (...args) => calls.push(args) } };
    await activateSource(api, { tabId: 7 }, () => true, async () => {});
    assert.deepEqual(calls, [[8, { focused: true }]]);
});
