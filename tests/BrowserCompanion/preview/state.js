const mode = new URLSearchParams(location.search).get("state");
const track = {
    tabId: 7, frameId: 0, documentId: "music-document", id: "music-document:document", kind: "media-session",
    title: "Weightless", artist: "Marconi Union", album: "Ambient Transmissions Vol. 2",
    page: "https://music.youtube.com/", pageTitle: "YouTube Music", state: "playing", position: 142, duration: 506,
    siteName: "YouTube Music", tabUrl: "https://music.youtube.com/",
    artwork: new URL("/cover-one.svg", location.href).href, capabilities: ["play", "pause", "previoustrack", "nexttrack", "activate"]
};
const podcast = {
    ...track, tabId: 12, documentId: "podcast-document", id: "podcast-document:element-1", kind: "html-media",
    title: "Designing things that last", artist: "The Design Notes Podcast", album: "Episode 42",
    page: "https://podcasts.apple.com/", pageTitle: "Podcasts", state: "paused", position: 512, duration: 2438,
    siteName: "Apple Podcasts", tabUrl: "https://podcasts.apple.com/",
    artwork: new URL("/cover-two.svg", location.href).href, capabilities: ["play", "pause", "activate"]
};
const state = {
    connectionState: "connected", connectionStatus: "Connected to media service", epoch: "preview",
    sessions: [track, { ...track, id: "music-document:element-1", kind: "html-media", capabilities: ["play", "pause", "activate"] }, podcast]
};
if (mode === "empty") state.sessions = [];
if (mode === "disconnected" || mode === "forbidden") {
    state.connectionState = "disconnected";
    state.connectionStatus = "The companion service is not running.";
    if (mode === "forbidden") state.connectionStatus = "Access to the specified native messaging host is forbidden.";
}
if (mode === "sleeping") state.sessions = [{ ...podcast, state: "sleeping", capabilities: ["play", "activate"] }];
if (mode === "long") state.sessions = [{ ...track, title: "A very long title about the small details that make an everyday interaction feel effortless",
    artist: "An artist with an unusually long name", album: "An equally long album title", artwork: "", state: "unknown", duration: null,
    page: "https://a-very-long-subdomain.example.test/a-page", tabUrl: "", siteName: "", capabilities: ["activate"] }];
if (mode === "many") state.sessions = Array.from({ length: 12 }, (_, index) => ({ ...podcast, tabId: index + 20,
    id: `player-${index}`, documentId: `document-${index}`, title: `Episode ${index + 1}: Designing things that last` }));

globalThis.preview = { state, commands: [], outcome: "completed" };
globalThis.chrome = { runtime: { id: "a".repeat(32), sendMessage: async (message) => {
    if (message.type === "status") return structuredClone(state);
    preview.commands.push(message);
    if (message.type === "reconnect") {
        state.connectionState = "connected";
        state.connectionStatus = "Connected to media service";
        return { ok: true };
    }
    const player = state.sessions.find((item) => item.id === message.sessionId);
    if (!player || message.epoch !== state.epoch) return { status: "session-gone" };
    if (preview.outcome !== "completed") return { status: preview.outcome };
    for (const item of state.sessions.filter((item) => item.documentId === player.documentId)) {
        if (message.operation === "play") item.state = "playing";
        if (message.operation === "pause") item.state = "paused";
    }
    return { status: "completed" };
} } };
