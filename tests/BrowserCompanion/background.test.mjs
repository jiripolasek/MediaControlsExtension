import assert from "node:assert/strict";
import test from "node:test";

function event() {
    const listeners = [];
    return { addListener: (listener) => listeners.push(listener), emit: (...args) => listeners.forEach((listener) => listener(...args)) };
}

function port(sender) {
    return { sender, name: "media-document", onMessage: event(), onDisconnect: event(), messages: [],
        postMessage(message) { this.messages.push(message); }, disconnect() { this.onDisconnect.emit(); } };
}

async function until(predicate) {
    for (let attempt = 0; attempt < 200; attempt++) {
        const value = predicate();
        if (value) return value;
        await new Promise((resolve) => setTimeout(resolve, 5));
    }
    throw new Error("Observation timeout");
}

async function worker(t, firefox = false) {
    const native = port();
    const nativeHosts = [];
    const calls = [];
    const mutations = [];
    const frames = new Map();
    const tabs = new Map([[1, { id: 1, windowId: 42, frozen: false, discarded: false }]]);
    const intervals = [];
    globalThis.chrome = {
        storage: { local: { get: async () => ({ profileId: crypto.randomUUID() }), set: async () => {} } },
        runtime: { id: "test-extension", connectNative: (host) => { nativeHosts.push(host); return native; }, onConnect: event(), onMessage: event(),
            onStartup: event(), onInstalled: event() },
        alarms: { create: async () => {}, onAlarm: event() },
        webNavigation: { onCommitted: event(), getFrame: async ({ tabId, frameId }) => frames.get(`${tabId}:${frameId}`) },
        tabs: { onRemoved: event(), onUpdated: event(),
            get: async (id) => tabs.get(id),
            update: async (id, change) => {
                mutations.push(["tab", id, change]);
                const tab = tabs.get(id);
                if (tab.frozen) { tab.frozen = false; chrome.tabs.onUpdated.emit(id, { frozen: false }); }
                return tab;
            },
            sendMessage: async (...args) => { calls.push(args); return { status: "completed" }; } },
        windows: { get: async () => ({ state: "minimized", focused: true }), update: async (...args) => mutations.push(["window", ...args]) }
    };
    if (firefox) {
        chrome.runtime.getBrowserInfo = async () => ({ name: "Firefox" });
        globalThis.browser = chrome;
        t.after(() => { delete globalThis.browser; });
    }
    const realInterval = globalThis.setInterval;
    globalThis.setInterval = (callback) => { intervals.push(callback); return intervals.length; };
    try { await import(`../../src/BrowserCompanion/extension/background.js?test=${crypto.randomUUID()}`); }
    finally { globalThis.setInterval = realInterval; }
    await new Promise((resolve) => setImmediate(resolve));
    const hello = native.messages.find((message) => message.type === "hello");
    assert.ok(hello.epoch);
    t.after(() => new Promise((resolve) => setTimeout(resolve, 150)));
    return { native, nativeHosts, calls, mutations, frames, tabs, intervals, hello,
        status: () => new Promise((resolve) => chrome.runtime.onMessage.emit({ type: "status" }, { id: chrome.runtime.id }, resolve)),
        attach(id = "document-a") {
            const document = port({ id: chrome.runtime.id, tab: tabs.get(1), frameId: 0, documentId: id, documentLifecycle: "active" });
            frames.set("1:0", { documentId: id, documentLifecycle: "active" });
            chrome.runtime.onConnect.emit(document);
            document.onMessage.emit({ type: "snapshot", players: [{ id: "element-1", title: "Track", state: "paused", capabilities: ["play", "pause"] }] });
            return document;
        },
        async command(operation, deadline = Date.now() + 1000, sessionId = "document-a:element-1") {
            const requestId = crypto.randomUUID();
            native.onMessage.emit({ type: "command", requestId, epoch: hello.epoch, sessionId, operation, deadline });
            for (let attempt = 0; attempt < 400; attempt++) {
                const result = native.messages.find((message) => message.requestId === requestId);
                if (result) return result;
                await new Promise((resolve) => setTimeout(resolve, 5));
            }
            throw new Error("Command result timeout");
        }
    };
}

for (const firefox of [false, true]) {
    test(`personal-domain native host is used in ${firefox ? "Firefox" : "Chromium"}`, async (t) => {
        const { nativeHosts } = await worker(t, firefox);
        assert.deepEqual(nativeHosts, ["com.jiripolasek.media_controls"]);
    });
}

test("actual service worker routes by browser document identity and retires navigated targets", async (t) => {
    const { native, calls, hello } = await worker(t);
    const first = port({ id: chrome.runtime.id, tab: { id: 1, favIconUrl: "https://site.test/favicon.png" }, frameId: 0, documentId: "document-a", documentLifecycle: "active" });
    const frame = port({ id: chrome.runtime.id, tab: { id: 1 }, frameId: 2, documentId: "document-b", documentLifecycle: "active" });
    chrome.runtime.onConnect.emit(first);
    chrome.runtime.onConnect.emit(frame);
    const observation = { type: "snapshot", players: [{ id: "element-1", tabId: 999, documentId: "forged", capabilities: ["pause"],
        favicon: "https://forged.test/favicon.png", artwork: "https://site.test/cover.png" }] };
    first.onMessage.emit(observation);
    frame.onMessage.emit(observation);
    await new Promise((resolve) => setTimeout(resolve, 150));
    const snapshot = native.messages.findLast((message) => message.type === "snapshot");
    assert.deepEqual(snapshot.sessions.map((session) => [session.id, session.tabId, session.frameId]),
        [["document-a:element-1", 1, 0], ["document-b:element-1", 1, 2]]);
    assert.equal(snapshot.sessions[0].favicon, "https://site.test/favicon.png");
    assert.equal(snapshot.sessions[0].artwork, "https://site.test/cover.png");
    chrome.tabs.onUpdated.emit(1, { favIconUrl: "https://site.test/new.png", title: "Updated title" });
    const updated = await new Promise((resolve) => chrome.runtime.onMessage.emit({ type: "status" }, { id: chrome.runtime.id }, resolve));
    assert.ok(updated.sessions.every((session) => session.favicon === "https://site.test/new.png" && session.pageTitle === "Updated title"));

    async function command(sessionId, epoch = hello.epoch) {
        const requestId = crypto.randomUUID();
        native.onMessage.emit({ type: "command", requestId, epoch, sessionId, operation: "pause", deadline: Date.now() + 1000 });
        await new Promise((resolve) => setImmediate(resolve));
        return native.messages.find((message) => message.requestId === requestId);
    }
    assert.equal((await command("document-b:element-1")).status, "completed");
    assert.equal(calls[0][0], 1);
    assert.deepEqual(calls[0][2], { documentId: "document-b" });
    assert.equal((await command("document-b:element-1", "old-epoch")).status, "session-gone");
    const stalePopupResult = await new Promise((resolve) => chrome.runtime.onMessage.emit({
        type: "control", sessionId: "document-b:element-1", epoch: "old-epoch", operation: "pause"
    }, { id: chrome.runtime.id }, resolve));
    assert.equal(stalePopupResult.status, "session-gone");
    chrome.webNavigation.onCommitted.emit({ tabId: 1, frameId: 0, documentId: "replacement" });
    assert.equal((await command("document-a:element-1")).status, "session-gone");
    assert.equal((await command("document-b:element-1")).status, "session-gone");
    assert.equal(calls.length, 1);
    await new Promise((resolve) => setTimeout(resolve, 150));
    assert.equal(native.messages.findLast((message) => message.type === "snapshot").sessions.length, 0);
});

test("Firefox preserves exact document routing without Chromium lifecycle fields", async (t) => {
    const fixture = await worker(t, true);
    assert.equal(fixture.hello.browser, "Firefox");
    const document = port({ id: browser.runtime.id, tab: fixture.tabs.get(1), frameId: 0, documentId: "firefox-document" });
    fixture.frames.set("1:0", { documentId: "firefox-document" });
    browser.runtime.onConnect.emit(document);
    document.onMessage.emit({ type: "snapshot", players: [{ id: "element-1", capabilities: ["pause"] }] });
    assert.equal((await fixture.command("pause", Date.now() + 1000, "firefox-document:element-1")).status, "completed");
    assert.deepEqual(fixture.calls[0][2], { documentId: "firefox-document" });
    assert.equal((await fixture.command("activate", Date.now() + 1000, "firefox-document:element-1")).status, "completed");
    const changes = fixture.mutations.length;
    fixture.frames.set("1:0", { documentId: "replacement" });
    assert.equal((await fixture.command("activate", Date.now() + 1000, "firefox-document:element-1")).status, "session-gone");
    assert.equal(fixture.mutations.length, changes);
    browser.webNavigation.onCommitted.emit({ tabId: 1, frameId: 0, documentId: "replacement" });
    assert.equal((await fixture.command("pause", Date.now() + 1000, "firefox-document:element-1")).status, "session-gone");
    assert.equal(fixture.calls.length, 1);
    for (const sender of [
        { id: browser.runtime.id, tab: fixture.tabs.get(1), frameId: 0 },
        { id: browser.runtime.id, tab: fixture.tabs.get(1), frameId: 0, documentId: "cached", documentLifecycle: "cached" },
        { id: browser.runtime.id, tab: { ...fixture.tabs.get(1), incognito: true }, frameId: 0, documentId: "private" }
    ]) {
        const rejected = port(sender);
        browser.runtime.onConnect.emit(rejected);
        rejected.onMessage.emit({ type: "snapshot", players: [{ id: "element-1", capabilities: ["pause"] }] });
    }
    assert.deepEqual((await fixture.status()).sessions, []);
});

test("embedded players use the top-level site name and tab URL", async (t) => {
    const fixture = await worker(t);
    const tab = { id: 1, url: "https://www.site.test/watch?private=value#position", favIconUrl: "https://site.test/favicon.png" };
    const top = port({ id: chrome.runtime.id, tab, url: tab.url, frameId: 0, documentId: "top", documentLifecycle: "active" });
    const frame = port({ id: chrome.runtime.id, tab, url: "https://player.test/embed", frameId: 2, documentId: "embedded", documentLifecycle: "active" });
    chrome.runtime.onConnect.emit(top);
    chrome.runtime.onConnect.emit(frame);
    top.onMessage.emit({ type: "snapshot", siteName: "  Site Music  ", players: [] });
    frame.onMessage.emit({ type: "snapshot", siteName: "Embedded service", players: [
        { id: "element-1", siteName: "Forged site", tabUrl: "https://forged.test/", capabilities: ["play"] }
    ] });
    let session = (await fixture.status()).sessions[0];
    assert.equal(session.siteName, "Site Music");
    assert.equal(session.tabUrl, "https://www.site.test/watch");
    assert.equal(session.favicon, tab.favIconUrl);
    assert.equal(session.page, "https://player.test/embed");
    assert.equal(session.id, "embedded:element-1");
    top.onMessage.emit({ type: "snapshot", siteName: "Renamed site", players: [] });
    chrome.tabs.onUpdated.emit(1, { url: "https://www.site.test/next?private=value" });
    session = (await fixture.status()).sessions[0];
    assert.equal(session.siteName, "Renamed site");
    assert.equal(session.tabUrl, "https://www.site.test/next");
    top.disconnect();
    session = (await fixture.status()).sessions[0];
    assert.equal(session.siteName, "");
    assert.equal(session.tabUrl, "https://www.site.test/next");
});

test("frozen tabs retain activation without page JavaScript and need fresh observations before playback", async (t) => {
    const fixture = await worker(t);
    const document = fixture.attach();
    fixture.tabs.get(1).frozen = true;
    chrome.tabs.onUpdated.emit(1, { frozen: true });
    document.onMessage.emit({ type: "snapshot", players: [{ id: "element-1", state: "playing", capabilities: ["play", "pause"] }] });
    const sleeping = (await fixture.status()).sessions[0];
    assert.equal(sleeping.state, "sleeping");
    assert.deepEqual(sleeping.capabilities, ["play", "activate"]);
    assert.equal((await fixture.command("pause")).status, "unsupported");
    chrome.tabs.sendMessage = (...args) => { fixture.calls.push(args); return new Promise(() => {}); };
    assert.equal((await fixture.command("activate")).status, "completed");
    assert.deepEqual(fixture.mutations, [["tab", 1, { active: true }],
        ["window", 42, { state: "normal" }], ["window", 42, { focused: true }]]);
    assert.ok(fixture.calls.every(([, message]) => message.type === "refresh"));
    assert.equal((await fixture.status()).sessions[0].state, "unresponsive");
    document.onMessage.emit({ type: "snapshot", players: [{ id: "element-1", state: "paused", capabilities: ["play", "pause"] }] });
    assert.equal((await fixture.status()).sessions[0].state, "paused");
    assert.ok((await fixture.status()).sessions[0].capabilities.includes("play"));
});

test("Play restores and focuses a frozen tab's current window before waiting for fresh state", async (t) => {
    const fixture = await worker(t);
    const document = fixture.attach();
    fixture.tabs.get(1).frozen = true;
    chrome.tabs.onUpdated.emit(1, { frozen: true });
    chrome.tabs.update = async (id, change) => {
        fixture.mutations.push(["tab", id, change]);
        fixture.tabs.get(id).windowId = 84;
        return fixture.tabs.get(id);
    };
    chrome.windows.get = async (id) => { assert.equal(id, 84); return { state: "minimized", focused: !fixture.tabs.get(1).frozen }; };
    chrome.windows.update = async (id, change) => {
        fixture.mutations.push(["window", id, change]);
        if (change.focused) {
            fixture.tabs.get(1).frozen = false;
            chrome.tabs.onUpdated.emit(1, { frozen: false });
        }
    };
    const players = [{ id: "element-1", state: "paused", capabilities: ["play", "pause"] },
        { id: "document", state: "paused", capabilities: ["nexttrack"] }];
    document.onMessage.emit({ type: "snapshot", players });
    assert.deepEqual((await fixture.status()).sessions[1].capabilities, ["activate"]);
    const deadline = Date.now() + 1000;
    const pending = fixture.command("play", deadline);
    const refresh = await until(() => fixture.calls.find(([, message]) => message.refreshId));
    assert.equal(fixture.tabs.get(1).frozen, false, "The page stays frozen until its window is focused");
    assert.deepEqual(refresh[2], { documentId: "document-a" });
    document.onMessage.emit({ type: "snapshot", players, refreshId: "unrelated-refresh" });
    await new Promise((resolve) => setImmediate(resolve));
    assert.ok(fixture.calls.every(([, message]) => message.type === "refresh"), "A refresh acknowledgement or unrelated snapshot is insufficient");
    document.onMessage.emit({ type: "snapshot", players, refreshId: refresh[1].refreshId });
    assert.equal((await pending).status, "completed");
    const commands = fixture.calls.filter(([, message]) => message.type === "command");
    assert.equal(commands.length, 1);
    assert.equal(commands[0][1].operation, "play");
    assert.equal(commands[0][1].playerId, "element-1");
    assert.equal(commands[0][1].deadline, deadline);
    assert.deepEqual(commands[0][2], { documentId: "document-a" });
    assert.deepEqual(fixture.mutations, [["tab", 1, { active: true }],
        ["window", 84, { state: "normal" }], ["window", 84, { focused: true }]]);
});

test("Play on an awake tab does not activate its window", async (t) => {
    const fixture = await worker(t);
    fixture.attach();
    assert.equal((await fixture.command("play")).status, "completed");
    assert.deepEqual(fixture.mutations, []);
    assert.equal(fixture.calls.length, 1);
});

test("a refused browser foreground request does not report activation success or send Play", async (t) => {
    const fixture = await worker(t);
    fixture.attach();
    fixture.tabs.get(1).frozen = true;
    chrome.windows.get = async () => ({ state: "normal", focused: false });
    const result = await fixture.command("play");
    assert.equal(result.status, "unavailable");
    assert.match(result.detail, /foreground focus/);
    assert.ok(fixture.calls.every(([, message]) => message.type === "refresh" && !message.refreshId));
    assert.equal((await fixture.command("activate")).status, "unavailable");
    assert.equal(fixture.calls.filter(([, message]) => message.type === "command").length, 0);
});

test("navigation during wake-on-Play tab selection prevents window focus and playback", async (t) => {
    const fixture = await worker(t);
    fixture.attach();
    fixture.tabs.get(1).frozen = true;
    chrome.tabs.update = async (id, change) => {
        fixture.mutations.push(["tab", id, change]);
        fixture.frames.set("1:0", { documentId: "replacement", documentLifecycle: "active" });
    };
    assert.equal((await fixture.command("play")).status, "session-gone");
    assert.deepEqual(fixture.mutations, [["tab", 1, { active: true }]]);
    assert.equal(fixture.calls.length, 0);
});

test("a window restore finishing after the Play deadline cannot focus or send playback", async (t) => {
    const fixture = await worker(t);
    fixture.attach();
    fixture.tabs.get(1).frozen = true;
    let restored;
    chrome.windows.update = (id, change) => {
        fixture.mutations.push(["window", id, change]);
        return new Promise((resolve) => { restored = resolve; });
    };
    const pending = fixture.command("play", Date.now() + 200);
    try {
        await until(() => restored);
        assert.equal((await pending).status, "unavailable");
    } finally { restored?.(); }
    await new Promise((resolve) => setImmediate(resolve));
    assert.deepEqual(fixture.mutations, [["tab", 1, { active: true }], ["window", 42, { state: "normal" }]]);
    assert.ok(fixture.calls.every(([, message]) => message.type === "refresh"));
});

test("wake-on-Play rejects a changed document, removed player, or withdrawn capability", async (t) => {
    for (const change of ["navigation", "late-navigation-event", "discard", "disconnect", "player", "capability"]) {
        await t.test(change, async (t) => {
            const fixture = await worker(t);
            const document = fixture.attach();
            fixture.tabs.get(1).frozen = true;
            const pending = fixture.command("play");
            const refresh = await until(() => fixture.calls.find(([, message]) => message.refreshId));
            if (change === "navigation") chrome.webNavigation.onCommitted.emit({ tabId: 1, frameId: 0, documentId: "replacement" });
            if (change === "late-navigation-event") fixture.frames.set("1:0", { documentId: "replacement", documentLifecycle: "active" });
            if (change === "discard") chrome.tabs.onUpdated.emit(1, { discarded: true });
            if (change === "disconnect") document.disconnect();
            const players = change === "player" ? [] : [{ id: "element-1", state: "paused",
                capabilities: change === "capability" ? ["pause"] : ["play", "pause"] }];
            document.onMessage.emit({ type: "snapshot", players, refreshId: refresh[1].refreshId });
            assert.equal((await pending).status, change === "capability" ? "unsupported" : "session-gone");
            assert.ok(fixture.calls.every(([, message]) => message.type === "refresh"));
        });
    }
});

test("wake-on-Play expires without sending playback and ignores a late refresh", async (t) => {
    const fixture = await worker(t);
    const document = fixture.attach();
    fixture.tabs.get(1).frozen = true;
    const pending = fixture.command("play", Date.now() + 200);
    const refresh = await until(() => fixture.calls.find(([, message]) => message.refreshId));
    assert.equal((await pending).status, "unavailable");
    assert.deepEqual((await fixture.status()).sessions[0].capabilities, ["activate"]);
    document.onMessage.emit({ type: "snapshot", refreshId: refresh[1].refreshId,
        players: [{ id: "element-1", state: "paused", capabilities: ["play", "pause"] }] });
    await new Promise((resolve) => setImmediate(resolve));
    assert.ok(fixture.calls.every(([, message]) => message.type === "refresh"));
    assert.equal((await fixture.command("play")).status, "completed", "A new explicit command can use the released playback slot");
    assert.equal(fixture.calls.filter(([, message]) => message.type === "command").length, 1);
});

test("a stalled page command expires and releases its busy slot so browser activation still works", async (t) => {
    const fixture = await worker(t);
    const document = fixture.attach();
    chrome.tabs.sendMessage = (...args) => { fixture.calls.push(args); return new Promise(() => {}); };
    const pending = fixture.command("pause", Date.now() + 200);
    assert.equal((await fixture.command("activate")).status, "completed", "Activation must not queue behind page JavaScript");
    assert.equal((await pending).status, "unconfirmed");
    assert.deepEqual((await fixture.status()).sessions[0].capabilities, ["activate"]);
    assert.equal((await fixture.command("activate")).status, "completed");
    assert.equal(fixture.calls.length, 1, "A timed-out page command must not be replayed");
    document.onMessage.emit({ type: "snapshot", players: [{ id: "element-1", state: "paused", capabilities: ["play", "pause"] }] });
    chrome.tabs.sendMessage = async (...args) => { fixture.calls.push(args); return { status: "completed" }; };
    assert.equal((await fixture.command("pause")).status, "completed", "The playback slot must be released after timeout");
});

test("browser document identity fences activation even before navigation events arrive", async (t) => {
    const fixture = await worker(t);
    fixture.attach();
    fixture.frames.set("1:0", { documentId: "replacement", documentLifecycle: "active" });
    assert.equal((await fixture.command("activate")).status, "session-gone");
    assert.deepEqual(fixture.mutations, []);
    fixture.frames.set("1:0", { documentId: "document-a", documentLifecycle: "cached" });
    assert.equal((await fixture.command("activate")).status, "session-gone");
    assert.deepEqual(fixture.mutations, []);
    fixture.frames.set("1:0", { documentId: "document-a", documentLifecycle: "active" });
    chrome.tabs.update = async () => fixture.frames.set("1:0", { documentId: "replacement", documentLifecycle: "active" });
    assert.equal((await fixture.command("activate")).status, "session-gone");
    assert.deepEqual(fixture.mutations, [], "A replacement document must not cause window activation");
});

test("discarded documents retire and heartbeat messages carry the current browser epoch", async (t) => {
    const fixture = await worker(t);
    fixture.attach();
    assert.equal(fixture.hello.heartbeatIntervalMs, 5000);
    fixture.intervals[0]();
    assert.deepEqual(fixture.native.messages.at(-1), { type: "heartbeat", epoch: fixture.hello.epoch });
    chrome.tabs.onUpdated.emit(1, { discarded: true });
    assert.deepEqual((await fixture.status()).sessions, []);
    fixture.attach("replacement");
    assert.equal((await fixture.command("activate")).status, "session-gone");
    assert.deepEqual(fixture.mutations, []);
    assert.equal((await fixture.command("activate", Date.now() + 1000, "replacement:element-1")).status, "completed");
});

test("worker reports desktop connection state independently of available browser players", async (t) => {
    const h = await worker(t);
    h.attach();
    assert.equal((await h.status()).connectionState, "connecting");
    h.native.onMessage.emit({ type: "ready" });
    assert.equal((await h.status()).connectionState, "connected");
    h.native.disconnect();
    const disconnected = await h.status();
    assert.equal(disconnected.connectionState, "disconnected");
    assert.equal(disconnected.sessions.length, 1, "Local popup controls must remain available without the desktop service");
    assert.deepEqual(disconnected.sessions[0].capabilities, ["play", "pause", "activate"]);
});
