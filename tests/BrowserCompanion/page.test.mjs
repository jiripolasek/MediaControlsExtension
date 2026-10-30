import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";
import test from "node:test";

const source = await readFile(new URL("../../src/BrowserCompanion/extension/page.js", import.meta.url), "utf8");

function page(documentProperties = {}) {
    const events = new Map();
    const messages = [];
    const elements = [];
    const scheduled = [];
    class Media {
        static HAVE_FUTURE_DATA = 3;
        constructor() {
            this.isConnected = true; this.src = "fixture.wav"; this.paused = true; this.ended = false;
            this.currentTime = 0; this.duration = 12; this.volume = 1; this.playbackRate = 1; this.tagName = "AUDIO";
            this.muted = false; this.readyState = 4; this.played = { length: 0 }; this.listeners = new Map();
            this.seekable = { length: 1, start: () => 0, end: () => 12 };
        }
        async play() {
            this.paused = false;
            this.emit("play");
            if (this.readyState >= Media.HAVE_FUTURE_DATA) {
                this.played = { length: 1 };
                this.emit("playing");
            }
        }
        pause() { this.paused = true; this.emit("pause"); }
        addEventListener(name, listener) {
            if (!this.listeners.has(name)) this.listeners.set(name, []);
            this.listeners.get(name).push(listener);
        }
        emit(name) { for (const listener of this.listeners.get(name) || []) listener({ type: name }); }
        getAttribute() { return null; }
        querySelector() { return null; }
    }
    const window = { addEventListener: (name, listener) => events.set(name, listener), postMessage: (message) => messages.push(message) };
    const mediaSession = { metadata: null, playbackState: "none", setActionHandler() {} };
    const document = { title: "Fixture", contentType: "text/html", baseURI: "https://media.test/watch/",
        querySelectorAll: () => elements, addEventListener() {}, ...documentProperties };
    const context = vm.createContext({ window, navigator: { mediaSession }, HTMLMediaElement: Media, WeakRef, URL,
        setTimeout: (fn) => scheduled.push(fn), setInterval: () => {}, document });
    vm.runInContext(source, context);
    function flush() { while (scheduled.length) scheduled.shift()(); }
    function refresh(refreshId) {
        events.get("message")({ source: window, data: { channel: "jiripolasek-media-controls-v1", type: "refresh", refreshId } });
        if (!refreshId) flush();
    }
    async function command(playerId, operation, value) {
        const requestId = crypto.randomUUID();
        events.get("message")({ source: window, data: { channel: "jiripolasek-media-controls-v1", type: "command",
            requestId, playerId, operation, value, deadline: Date.now() + 1000 } });
        await new Promise((resolve) => setImmediate(resolve));
        flush();
        return messages.find((message) => message.type === "result" && message.requestId === requestId).status;
    }
    return { Media, elements, mediaSession, document, flush, refresh, command, messages,
        players: () => messages.findLast((message) => message.type === "snapshot").players };
}

test("actual page hook controls individual elements, validates seeking, and removes detached DOM players", async () => {
    const fixture = page();
    const first = new fixture.Media();
    const second = new fixture.Media();
    fixture.elements.push(first, second);
    await first.play(); first.pause();
    await second.play(); second.pause();
    fixture.flush();
    assert.equal(fixture.players().length, 2);
    assert.equal(await fixture.command("element-2", "play"), "completed");
    assert.equal(first.paused, true);
    assert.equal(second.paused, false);
    assert.equal(await fixture.command("element-2", "seek", 20), "unsupported");
    assert.equal(await fixture.command("element-2", "seek", 5), "completed");
    assert.equal(second.currentTime, 5);
    await fixture.command("element-2", "pause");
    second.isConnected = false;
    fixture.elements.pop();
    fixture.refresh();
    assert.equal(fixture.players().length, 1);
    assert.equal(await fixture.command("element-2", "play"), "session-gone");
});

test("a wake refresh captures current players immediately and echoes its correlation ID", async () => {
    const fixture = page();
    fixture.flush();
    assert.equal(fixture.players().length, 0);
    const audio = new fixture.Media();
    fixture.elements.push(audio);
    await audio.play();
    fixture.refresh("wake-refresh");
    assert.equal(fixture.messages.at(-1).refreshId, "wake-refresh");
    assert.equal(fixture.players().length, 1);
});

test("detached Audio observed through play remains controllable after pause", async () => {
    const fixture = page();
    fixture.flush();
    const audio = new fixture.Media();
    audio.isConnected = false;
    await audio.play();
    fixture.refresh();
    assert.equal(fixture.players().length, 1);
    assert.equal(await fixture.command("element-1", "pause"), "completed");
    assert.equal(fixture.players().length, 1);
    assert.equal(await fixture.command("element-1", "play"), "completed");
});

test("Media Session handlers are separate from element controls and null unregisters them", async () => {
    const fixture = page();
    const audio = new fixture.Media();
    fixture.elements.push(audio);
    await audio.play(); audio.pause();
    fixture.flush();
    let nextCount = 0;
    fixture.mediaSession.metadata = { title: "Document title" };
    fixture.mediaSession.setActionHandler("nexttrack", () => nextCount++);
    fixture.flush();
    assert.equal(fixture.players().length, 2);
    assert.equal(await fixture.command("document", "nexttrack"), "completed");
    assert.equal(nextCount, 1);
    assert.equal(await fixture.command("element-1", "nexttrack"), "unsupported");
    fixture.mediaSession.setActionHandler("nexttrack", null);
    fixture.flush();
    assert.equal(await fixture.command("document", "nexttrack"), "unsupported");
    fixture.mediaSession.metadata = null;
    fixture.refresh();
    assert.equal(fixture.players().length, 1);
});

test("artwork uses page metadata and individual video posters without mixing multiple players", async () => {
    const fixture = page();
    const first = new fixture.Media();
    fixture.elements.push(first);
    await first.play(); first.pause();
    fixture.mediaSession.metadata = { title: "Track", artwork: [
        { src: "/small.png", sizes: "96x96" }, { src: "/cover.png", sizes: "512x512" },
        { src: "file:///secret.png", sizes: "1024x1024" }
    ] };
    fixture.flush();
    assert.equal(fixture.players()[0].artwork, "https://media.test/cover.png");
    assert.equal(fixture.players()[1].artwork, "https://media.test/cover.png");
    first.poster = "poster.jpg";
    const second = new fixture.Media();
    fixture.elements.push(second);
    await second.play(); second.pause();
    fixture.refresh();
    assert.equal(fixture.players()[0].artwork, "https://media.test/watch/poster.jpg");
    assert.equal(fixture.players()[1].artwork, "");
    fixture.mediaSession.metadata = null;
    first.poster = "https://user:secret@media.test/private.png";
    fixture.refresh();
    assert.equal(fixture.players()[0].artwork, "");
});

test("unplayed media stays hidden", async () => {
    const fixture = page();
    const video = new fixture.Media();
    video.tagName = "VIDEO";
    video.currentTime = 5;
    fixture.elements.push(video);
    fixture.flush();
    assert.equal(fixture.players().length, 0);
    assert.equal(await fixture.command("element-1", "play"), "session-gone");
    assert.equal(video.paused, true);
});

for (const [name, options] of [["muted previews", { muted: true }], ["zero-volume previews", { volume: 0 }]]) {
    test(`${name} stay hidden until sound is enabled`, async () => {
        const fixture = page();
        const video = Object.assign(new fixture.Media(), { tagName: "VIDEO" }, options);
        fixture.elements.push(video);
        await video.play();
        fixture.flush();
        assert.equal(fixture.players().length, 0);
        video.pause();
        fixture.flush();
        assert.equal(fixture.players().length, 0);
        assert.equal(await fixture.command("element-1", "probe"), "session-gone");
        video.muted = false; video.volume = 1; video.emit("volumechange");
        fixture.flush();
        assert.equal(fixture.players().length, 0);
        await video.play();
        fixture.flush();
        assert.equal(fixture.players().length, 1);
        assert.equal(fixture.players()[0].state, "playing");
    });
}

test("audible playback survives pause and mute", async () => {
    const fixture = page();
    const audio = new fixture.Media();
    fixture.elements.push(audio);
    await audio.play();
    audio.muted = true; audio.volume = 0; audio.pause();
    fixture.flush();
    assert.equal(fixture.players().length, 1);
    assert.equal(fixture.players()[0].state, "paused");
    assert.equal(await fixture.command("element-1", "play"), "completed");
    audio.ended = true; audio.pause();
    fixture.flush();
    assert.equal(fixture.players()[0].state, "ended");
});

test("playback waits for data", async () => {
    const fixture = page();
    const audio = new fixture.Media();
    audio.readyState = 0;
    fixture.elements.push(audio);
    await audio.play();
    fixture.flush();
    assert.equal(fixture.players().length, 0);
    audio.readyState = 3; audio.emit("playing"); audio.pause();
    fixture.flush();
    assert.equal(fixture.players().length, 1);
    assert.equal(fixture.players()[0].state, "paused");
});

test("reused media must qualify each source", async () => {
    const fixture = page();
    const video = new fixture.Media();
    fixture.elements.push(video);
    await video.play();
    fixture.flush();
    assert.equal(fixture.players().length, 1);
    video.pause(); video.src = "preview.wav"; video.played = { length: 0 }; video.muted = true;
    assert.equal(await fixture.command("element-1", "play"), "session-gone");
    fixture.refresh();
    assert.equal(fixture.players().length, 0);
    await video.play();
    fixture.flush();
    assert.equal(fixture.players().length, 0);
    video.muted = false; video.emit("volumechange");
    fixture.flush();
    assert.equal(fixture.players().length, 1);
    video.pause(); video.played = { length: 0 }; video.emit("emptied");
    fixture.flush();
    assert.equal(fixture.players().length, 0);
});

test("previously played media is discovered after pause", () => {
    const fixture = page();
    const audio = new fixture.Media();
    audio.played = { length: 1 };
    fixture.elements.push(audio);
    fixture.flush();
    assert.equal(fixture.players().length, 1);
    assert.equal(fixture.players()[0].state, "paused");
});

for (const [name, contentType, tagName, documentTitle, url, expected] of [
    ["direct video without a document title", "video/mp4", "VIDEO", "",
        "https://biomes-one.vercel.app/assets/biomes-demo.mp4", "biomes-demo.mp4"],
    ["direct video with a browser-generated document title", "video/mp4", "VIDEO", "Video (1920x1080)",
        "https://media.test/videos/An%20example%20video.mp4?token=secret#t=10", "An example video.mp4"],
    ["direct audio", "audio/mpeg", "AUDIO", "Audio player",
        "https://media.test/music/Caf%C3%A9.mp3?download=1", "Caf\u00e9.mp3"],
    ["untitled HTML page", "text/html", "VIDEO", "",
        "../clips/clip.webm", "clip.webm"]
]) {
    test(`${name} reports the decoded media filename`, async () => {
        const fixture = page({ title: documentTitle, contentType });
        const media = Object.assign(new fixture.Media(), { tagName, currentSrc: url });
        fixture.elements.push(media);
        await media.play();
        fixture.flush();
        assert.equal(fixture.players()[0].title, expected);
    });
}

test("explicit media labels take priority over direct media filenames", async () => {
    const fixture = page({ title: "", contentType: "video/mp4" });
    const media = Object.assign(new fixture.Media(), { tagName: "VIDEO", src: "https://media.test/clip.mp4", title: "Director's cut" });
    let label = "Trailer";
    media.getAttribute = (name) => name === "aria-label" ? label : null;
    fixture.elements.push(media);
    await media.play();
    fixture.flush();
    assert.equal(fixture.players()[0].title, "Trailer");
    label = "  ";
    fixture.refresh();
    assert.equal(fixture.players()[0].title, "Director's cut");
    media.title = "";
    fixture.refresh();
    assert.equal(fixture.players()[0].title, "clip.mp4");
});

test("named HTML pages keep their distinct player titles", async () => {
    const fixture = page({ title: "Concert" });
    const first = Object.assign(new fixture.Media(), { tagName: "VIDEO", currentSrc: "https://media.test/asset.mp4" });
    const second = Object.assign(new fixture.Media(), { tagName: "VIDEO", currentSrc: "https://media.test/another.mp4" });
    fixture.elements.push(first, second);
    await first.play();
    await second.play();
    fixture.flush();
    assert.deepEqual(Array.from(fixture.players(), (player) => player.title), ["Concert (video 1)", "Concert (video 2)"]);
});

test("media filename fallback follows the selected source and supports source elements", async () => {
    const fixture = page({ title: "", contentType: "video/mp4" });
    const media = Object.assign(new fixture.Media(), { tagName: "VIDEO", src: "first.mp4", currentSrc: "chosen.mp4" });
    fixture.elements.push(media);
    await media.play();
    fixture.flush();
    assert.equal(fixture.players()[0].title, "chosen.mp4");
    media.currentSrc = "";
    fixture.refresh();
    assert.equal(fixture.players()[0].title, "first.mp4");
    media.src = "";
    media.querySelector = () => ({ src: "../clips/last.mp4?signature=secret" });
    fixture.refresh();
    assert.equal(fixture.players()[0].title, "last.mp4");
});

test("media filename fallback tolerates malformed escaping and keeps title bounds", async () => {
    const fixture = page({ title: "", contentType: "video/mp4" });
    const media = Object.assign(new fixture.Media(), { tagName: "VIDEO", currentSrc: "https://media.test/bad%ZZ.mp4?secret=1" });
    fixture.elements.push(media);
    await media.play();
    fixture.flush();
    assert.equal(fixture.players()[0].title, "bad%ZZ.mp4");
    media.currentSrc = `https://media.test/${"x".repeat(600)}.mp4`;
    fixture.refresh();
    assert.equal(fixture.players()[0].title, "x".repeat(512));
});

test("opaque media URLs keep generic fallback names", async () => {
    const fixture = page({ title: "", contentType: "video/mp4" });
    const media = Object.assign(new fixture.Media(), { tagName: "VIDEO" });
    fixture.elements.push(media);
    for (const url of ["blob:https://media.test/private-id", "data:video/mp4;base64,AAAA", "https://media.test/?token=secret"]) {
        media.currentSrc = url;
        await media.play();
        fixture.refresh();
        assert.equal(fixture.players()[0].title.trim(), "(video 1)");
    }
    media.currentSrc = "";
    media.src = "stale.mp4";
    media.srcObject = {};
    fixture.refresh();
    assert.equal(fixture.players()[0].title.trim(), "(video 1)", "A stream must not inherit an old URL's filename");
});
