const browserApi = globalThis.browser ?? chrome;
import { activateSource, cleanPlayers, commandError, imageUrl } from "./routing.mjs";

const isFirefox = typeof browserApi.runtime.getBrowserInfo === "function";
const isActiveDocument = (value) => value.documentLifecycle === "active" || (isFirefox && value.documentLifecycle === undefined);

const host = "com.jiripolasek.media_controls";
const documents = new Map();
const busy = new Set();
let nativePort;
let connecting = false;
let epoch = "";
let revision = 0;
let connectionStatus = "Service not connected";
let connectionState = "disconnected";
let publishTimer;

function sessions() {
    const tabSources = new Map([...documents.values()].filter((doc) => doc.frameId === 0).map((doc) => [doc.tabId, doc]));
    return [...documents.values()].flatMap((doc) => doc.players.map((player) => ({
        ...player, id: `${doc.documentId}:${player.id}`, playerId: player.id,
        tabId: doc.tabId, frameId: doc.frameId, documentId: doc.documentId,
        page: doc.url, pageTitle: doc.title, favicon: doc.favicon,
        siteName: tabSources.get(doc.tabId)?.siteName || "", tabUrl: doc.tabUrl || tabSources.get(doc.tabId)?.url || "",
        ...(doc.frozen || doc.awaitingSnapshot ? {
            state: doc.frozen ? "sleeping" : "unresponsive",
            capabilities: doc.frozen && player.capabilities.includes("play") ? ["play", "activate"] : ["activate"]
        } : {})
    }))).slice(0, 256);
}

function publish() {
    clearTimeout(publishTimer);
    publishTimer = setTimeout(() => {
        if (!nativePort) return;
        try {
            nativePort.postMessage({ type: "snapshot", epoch, revision: ++revision, sessions: sessions() });
        } catch { nativePort.disconnect(); }
    }, 100);
}

async function connect() {
    if (nativePort || connecting) return;
    connecting = true;
    connectionState = "connecting";
    try {
        const stored = await browserApi.storage.local.get("profileId");
        const profileId = stored.profileId || crypto.randomUUID();
        if (!stored.profileId) await browserApi.storage.local.set({ profileId });
        const port = browserApi.runtime.connectNative(host);
        nativePort = port;
        epoch = crypto.randomUUID();
        revision = 0;
        connectionStatus = "Connecting";
        port.onMessage.addListener((message) => {
            if (nativePort !== port) return;
            if (message.type === "ready") {
                connectionState = "connected";
                connectionStatus = "Connected to media service";
            }
            if (message.type === "command") {
                void execute(message).then((result) => {
                    if (nativePort === port) port.postMessage({ type: "result", requestId: message.requestId, ...result });
                }).catch(() => {});
            }
        });
        port.onDisconnect.addListener(() => {
            const error = port.error?.message || browserApi.runtime.lastError?.message;
            if (nativePort !== port) return;
            nativePort = null;
            epoch = "";
            connectionStatus = error || "Service disconnected";
            connectionState = "disconnected";
            void browserApi.alarms.create("reconnect", { delayInMinutes: 0.5 });
        });
        port.postMessage({ type: "hello", version: 1, epoch, profileId, heartbeatIntervalMs: 5000,
            browser: isFirefox ? "Firefox" : navigator.userAgent.includes("Edg/") ? "Edge" :
                navigator.userAgentData?.brands?.some((item) => item.brand === "Google Chrome") ? "Chrome" : "Chromium" });
        for (const doc of documents.values()) {
            doc.awaitingSnapshot = true;
            void browserApi.tabs.sendMessage(doc.tabId, { type: "refresh" }, { documentId: doc.documentId }).catch(() => {});
        }
        publish();
    } catch (error) {
        connectionStatus = String(error.message);
        connectionState = "disconnected";
        void browserApi.alarms.create("reconnect", { delayInMinutes: 0.5 });
    } finally { connecting = false; }
}

async function execute(request) {
    const session = sessions().find((item) => item.id === request.sessionId);
    const error = commandError(request, session, epoch);
    if (error) return { status: error };
    const doc = documents.get(session.documentId);
    const busyKey = `${session.id}:${request.operation === "activate" ? "activation" : "playback"}`;
    if (busy.has(busyKey)) return { status: "unavailable" };
    busy.add(busyKey);
    const isCurrent = () => documents.get(session.documentId) === doc &&
        doc.players.some((item) => item.id === session.playerId) && request.epoch === epoch && Date.now() <= request.deadline;
    const send = async (operation) => {
        if (!isCurrent()) throw new Error("session-gone");
        if (doc.frozen || doc.awaitingSnapshot) throw new Error("unavailable");
        if (!doc.players.find((player) => player.id === session.playerId)?.capabilities.includes(operation))
            throw new Error("unsupported");
        const response = await beforeDeadline(browserApi.tabs.sendMessage(session.tabId, {
            type: "command", playerId: session.playerId, operation,
            value: request.value, deadline: request.deadline
        }, { documentId: session.documentId }), request.deadline);
        if (!isCurrent()) throw new Error("session-gone");
        if (!response || response.status !== "completed") throw new Error(response?.status || "unavailable");
    };
    const verifyDocument = async () => {
        const frame = await beforeDeadline(browserApi.webNavigation.getFrame({
            tabId: session.tabId, frameId: session.frameId
        }), request.deadline);
        if (!isCurrent() || !frame || frame.documentId !== session.documentId || !isActiveDocument(frame))
            throw new Error("session-gone");
    };
    try {
        if (request.operation === "activate") {
            await beforeDeadline(activateSource(browserApi, session, isCurrent, verifyDocument), request.deadline);
        } else {
            const tab = await beforeDeadline(browserApi.tabs.get(session.tabId), request.deadline);
            if (!isCurrent()) throw new Error("session-gone");
            if (tab.discarded) {
                removeDocuments((item) => item.tabId === session.tabId);
                throw new Error("session-gone");
            }
            if (tab.frozen) setFrozen(session.tabId, true);
            if (doc.frozen && request.operation === "play") {
                await beforeDeadline(activateSource(browserApi, session, isCurrent, verifyDocument), request.deadline, "unavailable");
                await freshSnapshot(doc, request.deadline, isCurrent);
                await verifyDocument();
            }
            await send(request.operation);
        }
        return { status: "completed" };
    } catch (error) {
        if (error.timedOut && documents.get(session.documentId) === doc) {
            doc.awaitingSnapshot = true;
            publish();
        }
        const statuses = ["session-gone", "unsupported", "unavailable", "unconfirmed", "failed"];
        return { status: statuses.includes(error.message) ? error.message : "failed", detail: String(error.detail || error.message).slice(0, 256) };
    } finally { busy.delete(busyKey); }
}

function beforeDeadline(operation, deadline, status = "unconfirmed") {
    let timer;
    return Promise.race([operation, new Promise((_, reject) => {
        timer = setTimeout(() => reject(Object.assign(new Error(status), { timedOut: true })), Math.max(0, deadline - Date.now()));
    })]).finally(() => clearTimeout(timer));
}

async function freshSnapshot(doc, deadline, isCurrent) {
    if (!isCurrent()) throw new Error("session-gone");
    const refreshId = crypto.randomUUID();
    let observe;
    const ready = new Promise((resolve, reject) => {
        observe = (message) => {
            if (!isCurrent()) { reject(new Error("session-gone")); return; }
            if (message?.refreshId !== refreshId) return;
            // A reply to this refresh proves the captured document is executing again.
            doc.frozen = false;
            doc.awaitingSnapshot = false;
            publish();
            resolve();
        };
        doc.observers.add(observe);
    });
    try {
        void browserApi.tabs.sendMessage(doc.tabId, { type: "refresh", refreshId }, { documentId: doc.documentId }).catch(() => {});
        await beforeDeadline(ready, deadline, "unavailable");
    } finally { doc.observers.delete(observe); }
}

function setFrozen(tabId, frozen) {
    for (const doc of documents.values()) {
        if (doc.tabId !== tabId) continue;
        doc.frozen = frozen;
        doc.awaitingSnapshot = true;
        if (!frozen) void browserApi.tabs.sendMessage(tabId, { type: "refresh" }, { documentId: doc.documentId }).catch(() => {});
    }
    publish();
}

browserApi.runtime.onConnect.addListener((port) => {
    const sender = port.sender;
    if (port.name !== "media-document" || sender?.id !== browserApi.runtime.id || !sender.tab ||
        sender.tab.incognito || !sender.documentId || !isActiveDocument(sender)) {
        port.disconnect();
        return;
    }
    const doc = { port, tabId: sender.tab.id, frameId: sender.frameId, documentId: sender.documentId,
        url: (sender.url || "").split(/[?#]/)[0].slice(0, 512), title: (sender.tab.title || "").slice(0, 512),
        tabUrl: (sender.tab.url || "").split(/[?#]/)[0].slice(0, 512), siteName: "",
        favicon: imageUrl(sender.tab.favIconUrl), frozen: sender.tab.frozen === true, awaitingSnapshot: false, players: [], observers: new Set() };
    documents.set(doc.documentId, doc);
    port.onMessage.addListener((message) => {
        if (documents.get(doc.documentId) !== doc || message?.type !== "snapshot") return;
        doc.players = cleanPlayers(message.players);
        doc.siteName = typeof message.siteName === "string" ? message.siteName.trim().replace(/\s+/g, " ").slice(0, 128) : "";
        if (!doc.frozen) doc.awaitingSnapshot = false;
        for (const observe of doc.observers) observe(message);
        publish();
    });
    port.onDisconnect.addListener(() => {
        if (documents.get(doc.documentId) !== doc) return;
        documents.delete(doc.documentId);
        for (const observe of doc.observers) observe();
        publish();
    });
});

function removeDocuments(predicate) {
    for (const [id, doc] of documents) {
        if (!predicate(doc)) continue;
        documents.delete(id);
        for (const observe of doc.observers) observe();
        doc.port.disconnect();
    }
    publish();
}

browserApi.webNavigation.onCommitted.addListener((event) => {
    removeDocuments((doc) => doc.tabId === event.tabId && doc.documentId !== event.documentId &&
        (event.frameId === 0 || doc.frameId === event.frameId));
});
browserApi.tabs.onRemoved.addListener((tabId) => removeDocuments((doc) => doc.tabId === tabId));
browserApi.tabs.onUpdated.addListener((tabId, change) => {
    if (change.discarded) removeDocuments((doc) => doc.tabId === tabId);
    if (typeof change.frozen === "boolean") setFrozen(tabId, change.frozen);
    if ("favIconUrl" in change || "title" in change || "url" in change) {
        for (const doc of documents.values()) {
            if (doc.tabId !== tabId) continue;
            if ("favIconUrl" in change) doc.favicon = imageUrl(change.favIconUrl);
            if ("title" in change) doc.title = String(change.title || "").slice(0, 512);
            if ("url" in change) doc.tabUrl = String(change.url || "").split(/[?#]/)[0].slice(0, 512);
        }
        publish();
    }
});
browserApi.runtime.onMessage.addListener((message, sender, respond) => {
    if (sender.id !== browserApi.runtime.id || sender.tab) return;
    if (message?.type === "status") respond({ connectionState, connectionStatus, epoch, sessions: sessions() });
    if (message?.type === "reconnect") { void connect(); respond({ ok: true }); }
    if (message?.type === "control") {
        void execute({ ...message, deadline: Date.now() + 3000 }).then(respond);
        return true;
    }
});
browserApi.alarms.onAlarm.addListener((alarm) => { if (alarm.name === "reconnect") void connect(); });
browserApi.runtime.onStartup.addListener(() => { void connect(); });
browserApi.runtime.onInstalled.addListener(() => { void connect(); });
setInterval(() => {
    if (!nativePort) { void connect(); return; }
    try { nativePort.postMessage({ type: "heartbeat", epoch }); }
    catch { nativePort.disconnect(); }
}, 5000);
void connect();
