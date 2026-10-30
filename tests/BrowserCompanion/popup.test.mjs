import assert from "node:assert/strict";
import test from "node:test";

class Element {
    constructor(tag) { this.tag = tag; this.children = []; this.dataset = {}; this.attributes = {}; this.listeners = new Map(); }
    get firstElementChild() { return this.children[0] || null; }
    get nextElementSibling() { return this.parent?.children[this.parent.children.indexOf(this) + 1] || null; }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    addEventListener(type, listener) {
        if (!this.listeners.has(type)) this.listeners.set(type, []);
        this.listeners.get(type).push(listener);
    }
    dispatchEvent(event) {
        event.target ||= this;
        event.currentTarget = this;
        event.preventDefault ||= () => { event.defaultPrevented = true; };
        for (const listener of this.listeners.get(event.type) || []) listener(event);
        this[`on${event.type}`]?.(event);
        return !event.defaultPrevented;
    }
    focus() { document.activeElement = this; }
    contains(node) { return descendants(this).includes(node); }
    querySelector(selector) {
        return descendants(this).slice(1).find((node) => selector === "button" ? node.tag === "button" :
            selector === `[data-operation="${node.dataset.operation}"]`) || null;
    }
    remove() {
        if (this.parent) this.parent.children.splice(this.parent.children.indexOf(this), 1);
        this.parent = null;
    }
    insertBefore(node, before) {
        node.remove();
        const index = before ? this.children.indexOf(before) : this.children.length;
        assert.ok(index >= 0);
        this.children.splice(index, 0, node);
        node.parent = this;
    }
    append(...nodes) { for (const node of nodes) this.insertBefore(node, null); }
    replaceChildren(...nodes) {
        for (const child of [...this.children]) child.remove();
        this.append(...nodes);
    }
}

const descendants = (node) => [node, ...node.children.flatMap(descendants)];
const findClass = (node, name) => descendants(node).find((child) => child.className?.split(" ").includes(name));
const directClass = (node, name) => node.children.find((child) => child.className?.split(" ").includes(name));
const button = (node, operation) => descendants(node).find((child) => child.dataset.operation === operation);
const isBusy = (node) => node.attributes["aria-disabled"] === "true";
const settled = () => new Promise((resolve) => setImmediate(resolve));
const base = { tabId: 7, documentId: "doc-a", frameId: 0, title: "Video", page: "https://example.test/",
    capabilities: ["play", "pause", "activate"] };
const player = { ...base, id: "playing", kind: "html-media", state: "playing" };

async function popup(t, sessions = [player]) {
    const ids = ["sessions", "result", "status", "empty", "reconnect", "connection-dot", "connection-help",
        "connection-warning", "connection-title", "connection-detail", "connection-guidance", "extension-id", "section-heading"];
    const roots = new Map(ids.map((id) => { const node = new Element("div"); node.id = id; return [`#${id}`, node]; }));
    for (const id of ["result", "empty", "connection-warning", "connection-help"]) roots.get(`#${id}`).hidden = true;
    const events = new Element("document");
    globalThis.document = {
        createElement: (tag) => new Element(tag), createElementNS: (_, tag) => new Element(tag),
        querySelector: (selector) => roots.get(selector),
        querySelectorAll: (selector) => {
            assert.equal(selector, "button[data-command]");
            return [...roots.values()].flatMap(descendants).filter((node) => node.tag === "button" && node.dataset.command);
        },
        addEventListener: (type, listener) => events.addEventListener(type, listener),
        dispatchEvent: (event) => events.dispatchEvent(event)
    };
    const harness = { state: { connectionState: "connected", connectionStatus: "Connected to media service", epoch: "original", sessions },
        commands: [], response: null, roots, events };
    globalThis.chrome = { runtime: { id: "a".repeat(32), sendMessage: async (message) => {
        if (message.type === "status") return harness.state;
        harness.commands.push(message);
        if (harness.response) return harness.response(message);
        return { status: harness.state.sessions.some((session) => session.id === message.sessionId) && message.epoch === harness.state.epoch
            ? "completed" : "session-gone" };
    } } };
    const originalInterval = globalThis.setInterval;
    const originalTimeout = globalThis.setTimeout;
    const timers = [];
    let refresh;
    globalThis.setInterval = (callback) => { refresh = callback; };
    globalThis.setTimeout = (callback, delay) => {
        const timer = originalTimeout(callback, delay);
        timers.push(timer);
        return timer;
    };
    t.after(() => {
        globalThis.setInterval = originalInterval;
        globalThis.setTimeout = originalTimeout;
        timers.forEach(clearTimeout);
    });
    await import(`../../src/BrowserCompanion/extension/popup.js?test=${crypto.randomUUID()}`);
    await settled();
    harness.refresh = async () => { refresh(); await settled(); };
    harness.root = roots.get("#sessions");
    return harness;
}

test("popup preserves requested extra controls and sends captured targets without fallback", async (t) => {
    const h = await popup(t, [
        { ...base, id: "idle", kind: "html-media", state: "paused" }, player,
        { ...base, id: "page", kind: "media-session", state: "playing" }
    ]);
    assert.equal(h.root.children.length, 1);
    const tab = h.root.children[0];
    const controls = findClass(tab, "controls");
    const panel = findClass(tab, "player-list");
    const more = findClass(tab, "more");
    const additional = findClass(tab, "additional-controls");
    const rows = findClass(tab, "players");
    assert.equal(rows.children.length, 2, "The primary player must not be repeated in the extra controls");
    assert.equal(panel.hidden, true);
    assert.equal(more.hidden, false);
    assert.equal(findClass(tab, "more-summary").attributes["aria-label"], "More controls for example.test");
    const originalPause = button(controls, "pause");
    assert.equal(button(controls, "play"), undefined);
    assert.equal(originalPause.attributes["aria-label"], "Pause");
    more.open = true;
    additional.onclick();
    assert.equal(panel.hidden, false);
    assert.equal(additional.attributes["aria-expanded"], "true");
    assert.equal(more.open, false);
    originalPause.focus();
    h.state.sessions = h.state.sessions.map((session) => ({ ...session, title: "New title", position: 5 }));
    await h.refresh();
    assert.equal(h.root.children[0], tab);
    assert.equal(panel.hidden, false);
    assert.equal(button(controls, "pause"), originalPause);
    assert.equal(document.activeElement, originalPause);
    await originalPause.onclick();
    assert.deepEqual(h.commands.at(-1), { type: "control", sessionId: "page", epoch: "original", operation: "pause" });
    await button(rows.children[0], "play").onclick();
    assert.equal(h.commands.at(-1).sessionId, "idle");

    h.state.sessions = h.state.sessions.filter((session) => session.id !== "page");
    await h.refresh();
    assert.equal(rows.children.length, 1);
    assert.equal(panel.hidden, true, "A replacement primary must not inherit the previous extra controls expansion");
    assert.equal(additional.attributes["aria-expanded"], "false");
    await originalPause.onclick();
    assert.equal(h.commands.at(-1).sessionId, "page");
    await button(controls, "pause").onclick();
    assert.equal(h.commands.at(-1).sessionId, "playing");

    const stalePause = button(controls, "pause");
    h.state.epoch = "replacement";
    await h.refresh();
    await stalePause.onclick();
    assert.equal(h.commands.at(-1).epoch, "original");
    await button(controls, "pause").onclick();
    assert.equal(h.commands.at(-1).epoch, "replacement");
    h.state.sessions = [];
    await h.refresh();
    assert.equal(h.root.children.length, 0);
    assert.equal(h.roots.get("#empty").hidden, false);
    assert.equal(h.roots.get("#section-heading").hidden, true);
});

test("popup keeps keyboard focus and prevents duplicate playback across a refresh", async (t) => {
    const h = await popup(t, [player, { ...player, id: "second", state: "paused" }]);
    const tab = h.root.children[0];
    const controls = findClass(tab, "controls");
    const pause = button(controls, "pause");
    const secondaryPlay = button(findClass(tab, "players"), "play");
    let finish;
    h.response = (message) => message.operation === "activate" ? { status: "completed" } : new Promise((resolve) => { finish = resolve; });
    pause.focus();
    const first = pause.onclick();
    assert.equal(isBusy(pause), true);
    assert.equal(isBusy(secondaryPlay), false, "A different player must remain independently controllable");
    assert.equal(isBusy(button(controls, "activate")), false);
    await pause.onclick();
    assert.equal(h.commands.length, 1);
    h.state.sessions[0] = { ...player, state: "paused" };
    await h.refresh();
    const play = button(controls, "play");
    assert.equal(document.activeElement, play);
    assert.equal(isBusy(play), true);
    await play.onclick();
    assert.equal(h.commands.length, 1);
    await button(controls, "activate").onclick();
    assert.equal(h.commands.at(-1).operation, "activate");
    finish({ status: "unconfirmed" });
    await first;
    assert.equal(isBusy(play), false);
    assert.equal(directClass(tab, "player-result").hidden, true, "Late playback failure must not replace a newer successful activation");
    assert.equal(h.roots.get("#result").hidden, true, "Successful commands must not display a global confirmation banner");
});

test("popup handles artwork failures, bounded progress and sleeping players", async (t) => {
    const h = await popup(t, [{ ...player, artwork: "file:///secret.png", duration: 100, position: 120, title: "<b>Track</b>" }]);
    const tab = h.root.children[0];
    const artwork = findClass(tab, "artwork");
    const timeline = findClass(tab, "timeline");
    assert.equal(artwork.children.length, 1, "Unsafe artwork must not create an image request");
    assert.equal(findClass(tab, "media-title").textContent, "<b>Track</b>");
    assert.equal(timeline.children[0].textContent, "1:40");
    assert.equal(timeline.children[1].value, 100);
    assert.equal(findClass(tab, "player-list").hidden, true);
    h.state.sessions[0].artwork = "https://example.test/cover.png";
    await h.refresh();
    const img = artwork.children[1];
    assert.equal(img.referrerPolicy, "no-referrer");
    assert.equal(img.hidden, true);
    img.onload();
    assert.equal(img.hidden, false);
    img.onerror();
    assert.equal(img.hidden, true);
    await h.refresh();
    assert.equal(artwork.children[1], img, "Failed artwork must not retry every second");
    h.state.sessions[0] = { ...player, state: "sleeping", capabilities: ["play", "activate"], duration: 100, position: 20 };
    await h.refresh();
    assert.equal(timeline.hidden, true, "Sleeping tabs must not show stale progress");
    assert.equal(artwork.children.length, 1);
    assert.equal(findClass(tab, "playback-state").textContent, "Sleeping");
    assert.ok(button(tab, "play"));
    assert.equal(button(tab, "pause"), undefined);
    h.state.sessions[0] = { ...player, duration: Infinity, position: 1 };
    await h.refresh();
    assert.equal(timeline.hidden, true);
});

test("popup keeps local controls available while disconnected and explains unsuccessful commands", async (t) => {
    const h = await popup(t);
    h.state.connectionState = "disconnected";
    h.state.connectionStatus = "Native host exited.";
    await h.refresh();
    assert.equal(h.roots.get("#status").textContent, "Desktop offline");
    assert.equal(h.roots.get("#connection-warning").hidden, false);
    assert.equal(h.roots.get("#connection-help").hidden, false);
    assert.equal(h.roots.get("#connection-detail").textContent, "Native host exited.");
    assert.equal(h.roots.get("#reconnect").hidden, false);
    const pause = button(h.root, "pause");
    const feedback = directClass(h.root.children[0], "player-result");
    assert.equal(isBusy(pause), false);
    await pause.onclick();
    assert.equal(h.commands.at(-1).operation, "pause");
    h.response = () => ({ status: "unconfirmed" });
    await pause.onclick();
    assert.match(feedback.children[0].textContent, /did not confirm/);
    assert.equal(feedback.hidden, false);
    assert.equal(h.roots.get("#result").hidden, true);
    h.response = () => { throw new Error("Transport internals"); };
    await pause.onclick();
    assert.match(feedback.children[0].textContent, /Reopen the popup/);
    assert.equal(isBusy(pause), false);
    h.response = () => { h.state.connectionState = "connected"; return { ok: true }; };
    await h.roots.get("#reconnect").onclick();
    assert.equal(h.commands.at(-1).type, "reconnect");
    assert.equal(h.roots.get("#connection-help").hidden, true);
    assert.equal(h.roots.get("#connection-warning").hidden, true);
    assert.equal(h.roots.get("#reconnect").hidden, true);
});

test("popup identifies the top-level site and gives actionable connection help", async (t) => {
    const h = await popup(t, [{ ...player, siteName: "Site Music", tabUrl: "https://www.site.test/watch", page: "https://embedded.test/player" }]);
    assert.equal(findClass(h.root, "source").textContent, "Site Music");
    h.state.sessions[0].siteName = "";
    await h.refresh();
    assert.equal(findClass(h.root, "source").textContent, "site.test");
    h.state.connectionState = "disconnected";
    for (const [error, guidance] of [
        ["Access to the specified native messaging host is forbidden.", /ID is not allowed/],
        ["Specified native messaging host not found.", /not registered for this browser/],
        ["Native host has exited.", /could not stay connected/],
        ["Disconnected", /Open Media Controls, then Media sources/]
    ]) {
        h.state.connectionStatus = error;
        await h.refresh();
        assert.match(h.roots.get("#connection-guidance").textContent, guidance);
        assert.equal(h.roots.get("#connection-detail").textContent, error);
        assert.equal(h.roots.get("#extension-id").textContent, "a".repeat(32));
    }
});

test("popup opens extra controls on request and dismisses overflow with Escape or an outside click", async (t) => {
    const h = await popup(t, [player, { ...player, id: "second", state: "paused" }]);
    const tab = h.root.children[0];
    const more = findClass(tab, "more");
    const summary = findClass(tab, "more-summary");
    const additional = findClass(tab, "additional-controls");
    const panel = findClass(tab, "player-list");
    assert.equal(panel.hidden, true);
    assert.equal(additional.attributes["aria-expanded"], "false");
    more.open = true;
    await h.refresh();
    assert.equal(more.open, true, "A snapshot refresh must not close an open overflow menu");
    h.events.dispatchEvent({ type: "click", target: summary });
    assert.equal(more.open, true, "Clicks inside the menu must not be treated as outside clicks");
    h.events.dispatchEvent({ type: "keydown", key: "Escape", target: summary });
    assert.equal(more.open, false);
    assert.equal(document.activeElement, summary);
    more.open = true;
    h.events.dispatchEvent({ type: "click", target: new Element("button") });
    assert.equal(more.open, false);
    more.open = true;
    additional.onclick();
    assert.equal(panel.hidden, false);
    assert.equal(more.open, false);
    assert.equal(document.activeElement, summary);
    additional.onclick();
    assert.equal(panel.hidden, true);
    assert.equal(additional.attributes["aria-expanded"], "false");
});

test("popup restores focus when extra controls disappear or the connection epoch changes", async (t) => {
    const extra = { ...player, id: "second", state: "paused" };
    const h = await popup(t, [player, extra]);
    const tab = h.root.children[0];
    const panel = findClass(tab, "player-list");
    const additional = findClass(tab, "additional-controls");
    additional.onclick();
    button(findClass(tab, "players"), "play").focus();
    h.state.sessions = [player];
    await h.refresh();
    assert.equal(findClass(tab, "more").hidden, true);
    assert.equal(panel.hidden, true);
    assert.equal(document.activeElement, button(findClass(tab, "controls"), "pause"));

    h.state.sessions = [player, extra];
    await h.refresh();
    additional.onclick();
    button(findClass(tab, "players"), "play").focus();
    h.state.epoch = "replacement";
    await h.refresh();
    assert.equal(panel.hidden, true);
    assert.equal(additional.attributes["aria-expanded"], "false");
    assert.equal(document.activeElement, findClass(tab, "more-summary"));
});

test("popup keeps action errors beside their player and hides routine success feedback", async (t) => {
    const h = await popup(t, [player, { ...player, tabId: 8, id: "another-tab" }]);
    const [first, second] = h.root.children;
    const firstFeedback = directClass(first, "player-result");
    const secondFeedback = directClass(second, "player-result");
    const firstPause = button(findClass(first, "controls"), "pause");
    h.response = () => ({ status: "unconfirmed" });
    await firstPause.onclick();
    assert.equal(firstFeedback.hidden, false);
    assert.match(firstFeedback.children[0].textContent, /did not confirm/);
    assert.equal(secondFeedback.hidden, true);
    await button(findClass(second, "controls"), "pause").onclick();
    assert.equal(firstFeedback.hidden, false, "A different player's result must not clear this error");
    assert.equal(secondFeedback.hidden, false);
    findClass(firstFeedback, "dismiss-result").onclick();
    assert.equal(firstFeedback.hidden, true);
    assert.equal(document.activeElement, firstPause);
    assert.equal(secondFeedback.hidden, false);
    await firstPause.onclick();
    assert.equal(firstFeedback.hidden, false);
    h.response = () => ({ status: "completed" });
    await firstPause.onclick();
    assert.equal(firstFeedback.hidden, true);
    assert.equal(secondFeedback.hidden, false);
    assert.equal(h.roots.get("#result").hidden, true);
});

test("popup omits duplicate metadata and prefers the artist over album details", async (t) => {
    const h = await popup(t, [{ ...player, siteName: "Example Music", artist: "Performer", album: "Album", pageTitle: "Video" }]);
    const artist = findClass(h.root, "artist");
    assert.equal(artist.textContent, "Performer");
    assert.equal(artist.hidden, false);
    h.state.sessions[0].artist = "";
    await h.refresh();
    assert.equal(artist.textContent, "Album");
    h.state.sessions[0].album = "";
    await h.refresh();
    assert.equal(artist.hidden, true, "The media title must not be repeated as secondary metadata");
    h.state.sessions[0].pageTitle = "Example Music";
    await h.refresh();
    assert.equal(artist.hidden, true, "The source name must not be repeated as secondary metadata");
    h.state.sessions[0].pageTitle = "An episode page";
    await h.refresh();
    assert.equal(artist.hidden, false);
    assert.equal(artist.textContent, "An episode page");
    h.state.sessions[0].pageTitle = "";
    await h.refresh();
    assert.equal(artist.hidden, true, "Missing metadata must not create generic filler text");
});

test("popup keeps connection warnings out of the connected and connecting states", async (t) => {
    const h = await popup(t);
    assert.equal(h.roots.get("#status").textContent, "Desktop connected");
    assert.equal(h.roots.get("#connection-warning").hidden, true);
    assert.equal(h.roots.get("#reconnect").hidden, true);
    h.state.connectionState = "connecting";
    await h.refresh();
    assert.equal(h.roots.get("#status").textContent, "Connecting...");
    assert.equal(h.roots.get("#connection-warning").hidden, true);
    assert.equal(h.roots.get("#reconnect").hidden, true);
    h.state.connectionState = "disconnected";
    await h.refresh();
    assert.equal(h.roots.get("#connection-warning").hidden, false);
    assert.equal(h.roots.get("#reconnect").hidden, false);
    assert.equal(isBusy(button(h.root, "pause")), false);
});

test("popup does not attach an old primary action failure to its replacement", async (t) => {
    const h = await popup(t, [player, { ...player, id: "page", kind: "media-session" }]);
    const tab = h.root.children[0];
    const feedback = directClass(tab, "player-result");
    let finish;
    h.response = () => new Promise((resolve) => { finish = resolve; });
    const request = button(findClass(tab, "controls"), "pause").onclick();
    assert.equal(h.commands.at(-1).sessionId, "page");
    h.state.sessions = [player];
    await h.refresh();
    assert.equal(feedback.hidden, true);
    finish({ status: "unconfirmed" });
    await request;
    assert.equal(feedback.hidden, true, "A late result for the removed primary must not describe its replacement");
});

test("popup ignores an extra player action failure from a previous connection epoch", async (t) => {
    const h = await popup(t, [player, { ...player, id: "extra", state: "paused" }]);
    const tab = h.root.children[0];
    const additional = findClass(tab, "additional-controls");
    additional.onclick();
    const row = findClass(tab, "players").children[0];
    const feedback = directClass(row, "player-result");
    let finish;
    h.response = () => new Promise((resolve) => { finish = resolve; });
    const request = button(row, "play").onclick();
    assert.equal(h.commands.at(-1).epoch, "original");
    h.state.epoch = "replacement";
    await h.refresh();
    finish({ status: "unconfirmed" });
    await request;
    additional.onclick();
    assert.equal(feedback.hidden, true, "Reopening extra controls must not expose a failure from an obsolete connection");
});
