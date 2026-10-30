(() => {
    const channel = "jiripolasek-media-controls-v1";
    const pending = new Map();
    let port;
    let stopped = false;
    let retry;
    let lastSequence = 0;

    function siteName() {
        for (const selector of ['meta[property="og:site_name"]', 'meta[name="application-name"]']) {
            const name = document.querySelector(selector)?.content?.trim();
            if (name) return name.replace(/\s+/g, " ").slice(0, 128);
        }
        return "";
    }

    function connect() {
        if (stopped || port) return;
        try {
            const current = chrome.runtime.connect({ name: "media-document" });
            port = current;
            current.onDisconnect.addListener(() => {
                void chrome.runtime.lastError;
                if (port !== current) return;
                port = null;
                if (!stopped) retry = setTimeout(connect, 1000);
            });
            window.postMessage({ channel, type: "refresh" }, "*");
        } catch { stopped = true; }
    }

    window.addEventListener("message", (event) => {
        if (event.source !== window || event.data?.channel !== channel) return;
        const message = event.data;
        if (message.type === "snapshot" && Array.isArray(message.players) &&
            Number.isSafeInteger(message.sequence) && message.sequence > lastSequence) {
            lastSequence = message.sequence;
            // Page data is untrusted and cannot choose a tab, document, or native operation.
            if (JSON.stringify(message).length > 128000) return;
            try { port?.postMessage({ type: "snapshot", players: message.players.slice(0, 128), siteName: siteName(),
                refreshId: typeof message.refreshId === "string" ? message.refreshId.slice(0, 128) : undefined }); } catch {}
        }
        if (message.type === "result" && pending.has(message.requestId)) {
            const request = pending.get(message.requestId);
            pending.delete(message.requestId);
            clearTimeout(request.timer);
            request.respond({ status: ["completed", "failed", "unavailable", "unsupported", "session-gone", "unconfirmed"]
                .includes(message.status) ? message.status : "failed" });
        }
    });

    chrome.runtime.onMessage.addListener((message, sender, respond) => {
        if (sender.id !== chrome.runtime.id || stopped) return;
        if (message?.type === "refresh") {
            window.postMessage({ channel, type: "refresh", refreshId: message.refreshId }, "*");
            respond({ ok: true });
            return;
        }
        if (message?.type !== "command") return;
        if (!Number.isFinite(message.deadline) || Date.now() > message.deadline || pending.size >= 32) {
            respond({ status: "unavailable" });
            return;
        }
        const requestId = crypto.randomUUID();
        const timer = setTimeout(() => {
            pending.delete(requestId);
            respond({ status: "unconfirmed" });
        }, Math.min(3000, message.deadline - Date.now()));
        pending.set(requestId, { timer, respond });
        window.postMessage({ ...message, channel, requestId }, "*");
        return true;
    });

    window.addEventListener("pagehide", () => {
        stopped = true;
        clearTimeout(retry);
        port?.disconnect();
        port = null;
        for (const request of pending.values()) {
            clearTimeout(request.timer);
            request.respond({ status: "session-gone" });
        }
        pending.clear();
    });
    window.addEventListener("pageshow", () => { stopped = false; connect(); });
    connect();
})();
