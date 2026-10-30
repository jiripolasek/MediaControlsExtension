import assert from "node:assert/strict";
import test from "node:test";
import { groupSessions } from "../../src/BrowserCompanion/extension/grouping.mjs";

function player(id, properties = {}) {
    return { id, tabId: 7, documentId: "document-a", frameId: 0, kind: "html-media", state: "paused",
        title: "Identical title", capabilities: ["play", "pause", "activate"], ...properties };
}

test("a tab with two videos and page controls shows one primary while retaining every target", () => {
    const idle = player("element-1");
    const playing = player("element-2", { state: "playing" });
    const page = player("document", { kind: "media-session", state: "playing" });
    const groups = groupSessions([idle, playing, page]);
    assert.equal(groups.length, 1);
    assert.equal(groups[0].primary, page);
    assert.deepEqual(groups[0].players, [idle, playing, page]);
});

test("playing embedded media takes priority over an idle top-level Media Session", () => {
    const top = player("top-session", { kind: "media-session" });
    const embedded = player("frame-player", { documentId: "document-b", frameId: 2, state: "playing" });
    assert.equal(groupSessions([top, embedded])[0].primary, embedded);
    const frameSession = player("frame-session", {
        documentId: "document-b", frameId: 2, state: "unknown", kind: "media-session"
    });
    assert.equal(groupSessions([top, embedded, frameSession])[0].primary, frameSession);
});

test("metadata or track handlers alone do not hide an HTML player's playback controls", () => {
    const page = player("document", { kind: "media-session", state: "playing", capabilities: ["nexttrack", "activate"] });
    const html = player("element-1", { state: "playing" });
    const group = groupSessions([page, html])[0];
    assert.equal(group.primary, html);
    assert.deepEqual(group.primary.capabilities, ["play", "pause", "activate"]);
    assert.deepEqual(group.players[0].capabilities, ["nexttrack", "activate"]);
});

test("identical titles and URLs in different tabs remain separate groups", () => {
    const first = player("first", { page: "https://example.test/media" });
    const second = player("second", { tabId: 8, documentId: "document-b", page: first.page });
    assert.deepEqual(groupSessions([first, second]).map((group) => [group.tabId, group.primary.id]), [[7, "first"], [8, "second"]]);
});

test("equivalent candidates retain the primary across reordering, but active and removed targets update it", () => {
    const first = player("first");
    const second = player("second");
    const previous = new Map([[7, first.id]]);
    assert.equal(groupSessions([second, first], previous)[0].primary, first);
    const playing = { ...second, state: "playing" };
    assert.equal(groupSessions([first, playing], previous)[0].primary, playing);
    assert.equal(groupSessions([second], previous)[0].primary, second);
});

test("idle page playback controls are preferred, and activation-only or empty tabs remain valid", () => {
    const html = player("element-1");
    const page = player("document", { kind: "media-session" });
    assert.equal(groupSessions([html, page])[0].primary, page);
    const metadata = { ...page, capabilities: ["activate"] };
    assert.equal(groupSessions([html, metadata])[0].primary, html);
    assert.equal(groupSessions([metadata])[0].primary, metadata);
    assert.deepEqual(groupSessions([]), []);
});
