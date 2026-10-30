(() => {
    const channel = "jiripolasek-media-controls-v1";
    const elements = new Map();
    const identities = new WeakMap();
    const handlers = new Map();
    let nextId = 0;
    let timer;
    let sequence = 0;
    const mediaSession = navigator.mediaSession;
    const nativePlay = HTMLMediaElement.prototype.play;
    const nativePause = HTMLMediaElement.prototype.pause;

    function track(element) {
        if (identities.has(element)) return;
        const id = `element-${++nextId}`;
        identities.set(element, id);
        const entry = { reference: new WeakRef(element), wasConnected: element.isConnected, wasAudible: false, source: undefined };
        elements.set(id, entry);
        for (const name of ["play", "playing", "pause", "ended", "loadedmetadata", "emptied", "volumechange", "ratechange", "seeked"])
            element.addEventListener(name, () => {
                if (name === "emptied") entry.wasAudible = false;
                isAvailable(element, entry);
                schedule();
            });
    }

    HTMLMediaElement.prototype.play = function (...args) {
        track(this);
        return Reflect.apply(nativePlay, this, args);
    };

    if (mediaSession) {
        const nativeSetHandler = mediaSession.setActionHandler;
        mediaSession.setActionHandler = function (action, handler) {
            const result = Reflect.apply(nativeSetHandler, this, [action, handler]);
            if (handler) handlers.set(action, handler);
            else handlers.delete(action);
            schedule();
            return result;
        };
    }

    function scan(root) {
        root.querySelectorAll("audio,video").forEach(track);
        for (const element of root.querySelectorAll("*")) {
            if (element.shadowRoot) scan(element.shadowRoot);
        }
    }

    function isAvailable(element, entry) {
        entry.wasConnected ||= element.isConnected;
        const source = element.currentSrc || element.srcObject || element.src || element.querySelector("source")?.src;
        const sourceChanged = entry.source !== source;
        if (sourceChanged) {
            entry.source = source;
            entry.wasAudible = false;
        }
        const hasPlayed = (sourceChanged && element.played.length > 0) || (!element.paused && !element.ended &&
            element.readyState >= HTMLMediaElement.HAVE_FUTURE_DATA);
        // Retain real players after pause or mute, without admitting silent previews.
        entry.wasAudible ||= !!source && hasPlayed && !element.muted && element.volume > 0;
        return !!source && entry.wasAudible && (element.isConnected || !entry.wasConnected || !element.paused);
    }

    function mediaFilename(element) {
        const source = element.currentSrc || element.src || element.querySelector("source")?.src;
        if (!source || element.srcObject) return "";
        try {
            const url = new URL(source, document.baseURI);
            if (!["https:", "http:", "file:"].includes(url.protocol)) return "";
            const filename = url.pathname.split("/").at(-1);
            try { return decodeURIComponent(filename).trim(); }
            catch { return filename.trim(); }
        } catch { return ""; }
    }

    function mediaTitle(element, id) {
        const label = element.getAttribute("aria-label")?.trim() || element.title?.trim();
        if (label) return label.slice(0, 512);
        const pageTitle = document.title.trim();
        if (!pageTitle || /^(audio|video)\//i.test(document.contentType || "")) {
            const filename = mediaFilename(element);
            if (filename) return filename.slice(0, 512);
        }
        return `${pageTitle} (${element.tagName.toLowerCase()} ${id.slice(8)})`.trim().slice(0, 512);
    }

    function snapshot(refreshId) {
        scan(document);
        const players = [];
        for (const [id, entry] of elements) {
            const element = entry.reference.deref();
            if (!element) { elements.delete(id); continue; }
            if (!isAvailable(element, entry)) continue;
            const capabilities = ["play", "pause", "volume", "mute", "rate"];
            if (element.seekable.length > 0) capabilities.push("seek");
            players.push({ id, title: mediaTitle(element, id),
                state: element.ended ? "ended" : element.paused ? "paused" : "playing",
                artwork: imageUrl(element.poster),
                position: element.currentTime, duration: Number.isFinite(element.duration) ? element.duration : null,
                volume: element.volume, muted: element.muted, rate: element.playbackRate, capabilities });
        }
        const metadata = mediaSession?.metadata;
        const artwork = [...(metadata?.artwork || [])].slice(0, 16)
            .map((item) => ({ src: imageUrl(item.src), size: Number.parseInt(item.sizes, 10) || 0 }))
            .filter((item) => item.src).sort((a, b) => b.size - a.size)[0]?.src || "";
        // Document art also covers an unambiguous HTML player without its own poster.
        if (players.length === 1 && !players[0].artwork) players[0].artwork = artwork;
        if (metadata || handlers.size) {
            players.push({ id: "document", title: (metadata?.title || document.title).slice(0, 512),
                artist: (metadata?.artist || "").slice(0, 512), album: (metadata?.album || "").slice(0, 512),
                state: mediaSession.playbackState,
                artwork,
                capabilities: [...handlers.keys()].filter((key) => ["play", "pause", "nexttrack", "previoustrack"].includes(key)) });
        }
        window.postMessage({ channel, type: "snapshot", sequence: ++sequence, players: players.slice(0, 128),
            refreshId: typeof refreshId === "string" ? refreshId.slice(0, 128) : undefined }, "*");
    }

    function imageUrl(value) {
        if (typeof value !== "string" || !value || value.length > 2048) return "";
        try {
            const url = new URL(value, document.baseURI);
            return ["https:", "http:"].includes(url.protocol) && !url.username && !url.password ? url.href : "";
        } catch { return ""; }
    }

    function schedule() {
        if (timer) return;
        timer = setTimeout(() => { timer = null; snapshot(); }, 100);
    }

    async function command(message) {
        if (!Number.isFinite(message.deadline) || Date.now() > message.deadline) return "unavailable";
        if (message.playerId === "document") {
            if (!mediaSession?.metadata && !handlers.size) return "session-gone";
            if (message.operation === "probe") return "completed";
            const handler = handlers.get(message.operation);
            if (!handler || !["play", "pause", "nexttrack", "previoustrack"].includes(message.operation)) return "unsupported";
            await handler({ action: message.operation });
            return "completed";
        }
        const entry = elements.get(message.playerId);
        const element = entry?.reference.deref();
        if (!element || !isAvailable(element, entry)) return "session-gone";
        switch (message.operation) {
            case "probe": break;
            case "play":
                await Reflect.apply(nativePlay, element, []);
                if (element.paused) return "unconfirmed";
                break;
            case "pause":
                Reflect.apply(nativePause, element, []);
                if (!element.paused) return "unconfirmed";
                break;
            case "seek": {
                if (!Number.isFinite(message.value)) return "failed";
                const ranges = element.seekable;
                let allowed = false;
                for (let i = 0; i < ranges.length; i++) {
                    if (message.value >= ranges.start(i) && message.value <= ranges.end(i)) allowed = true;
                }
                if (!allowed) return "unsupported";
                element.currentTime = message.value;
                break;
            }
            case "volume":
                if (!Number.isFinite(message.value) || message.value < 0 || message.value > 1) return "failed";
                element.volume = message.value;
                break;
            case "mute":
                if (typeof message.value !== "boolean") return "failed";
                element.muted = message.value;
                break;
            case "rate":
                if (!Number.isFinite(message.value) || message.value < 0.0625 || message.value > 16) return "failed";
                element.playbackRate = message.value;
                break;
            default: return "unsupported";
        }
        return "completed";
    }

    window.addEventListener("message", (event) => {
        if (event.source !== window || event.data?.channel !== channel) return;
        const message = event.data;
        if (message.type === "refresh") {
            if (typeof message.refreshId === "string") snapshot(message.refreshId);
            else schedule();
        }
        if (message.type !== "command" || typeof message.requestId !== "string") return;
        void command(message).catch(() => "failed").then((status) => {
            window.postMessage({ channel, type: "result", requestId: message.requestId, status }, "*");
            schedule();
        });
    });
    document.addEventListener("play", (event) => {
        if (event.target instanceof HTMLMediaElement) track(event.target);
        schedule();
    }, true);
    setInterval(snapshot, 2000);
    window.addEventListener("pageshow", schedule);
    schedule();
})();
