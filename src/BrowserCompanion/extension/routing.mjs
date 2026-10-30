export const operations = new Set([
    "play", "pause", "seek", "volume", "mute", "rate", "nexttrack", "previoustrack", "activate"
]);

export function imageUrl(value) {
    if (typeof value !== "string" || value.length > 2048) return "";
    try {
        const url = new URL(value);
        return ["https:", "http:"].includes(url.protocol) && !url.username && !url.password ? url.href : "";
    } catch { return ""; }
}

export function cleanPlayers(value) {
    if (!Array.isArray(value)) return [];
    const players = new Map();
    for (const item of value.slice(0, 128)) {
        if (!item || typeof item.id !== "string" || !/^(element-[0-9]+|document)$/.test(item.id)) continue;
        const text = (v) => typeof v === "string" ? v.slice(0, 512) : "";
        const number = (v) => Number.isFinite(v) ? v : null;
        players.set(item.id, {
            id: item.id,
            kind: item.id === "document" ? "media-session" : "html-media",
            title: text(item.title), artist: text(item.artist), album: text(item.album),
            artwork: imageUrl(item.artwork),
            state: ["playing", "paused", "ended"].includes(item.state) ? item.state : "unknown",
            position: number(item.position), duration: number(item.duration),
            volume: number(item.volume), muted: item.muted === true, rate: number(item.rate),
            capabilities: [...new Set((Array.isArray(item.capabilities) ? item.capabilities : [])
                .filter((op) => operations.has(op) && op !== "activate")), "activate"]
        });
    }
    return [...players.values()];
}

export function commandError(request, session, epoch) {
    if (!request || request.epoch !== epoch || !session) return "session-gone";
    if (!Number.isFinite(request.deadline) || Date.now() > request.deadline) return "unavailable";
    if (!session.capabilities.includes(request.operation)) return "unsupported";
    const ranges = { seek: [0, Number.MAX_SAFE_INTEGER], volume: [0, 1], rate: [0.0625, 16] };
    const range = ranges[request.operation];
    if (range && (!Number.isFinite(request.value) || request.value < range[0] || request.value > range[1])) return "failed";
    if (request.operation === "mute" && typeof request.value !== "boolean") return "failed";
    return null;
}

export async function activateSource(api, target, isCurrent, probe) {
    const check = () => { if (!isCurrent()) throw new Error("session-gone"); };
    check();
    await probe();
    check();
    await api.tabs.update(target.tabId, { active: true });
    check();
    // Resolve window ownership now; the tab may have moved since discovery.
    const tab = await api.tabs.get(target.tabId);
    check();
    const window = await api.windows.get(tab.windowId);
    check();
    await probe();
    check();
    if (window.state === "minimized") {
        await api.windows.update(tab.windowId, { state: "normal" });
        check();
    }
    await api.windows.update(tab.windowId, { focused: true });
    check();
    const focused = await api.windows.get(tab.windowId);
    check();
    if (!focused.focused) throw Object.assign(new Error("unavailable"), { detail: "The browser window did not gain foreground focus." });
}
