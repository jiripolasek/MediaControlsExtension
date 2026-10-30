const browserApi = globalThis.browser ?? chrome;
import { groupSessions } from "./grouping.mjs";
import { imageUrl } from "./routing.mjs";

const root = document.querySelector("#sessions");
const resultText = document.querySelector("#result");
const reconnect = document.querySelector("#reconnect");
const labels = { previoustrack: "Previous track", play: "Play", pause: "Pause", nexttrack: "Next track", activate: "Open tab" };
const icons = {
    play: "M8 5l11 7-11 7z",
    pause: "M8 5v14M16 5v14",
    previoustrack: "M6 5v14M19 5l-10 7 10 7z",
    nexttrack: "M18 5v14M5 5l10 7-10 7z",
    activate: "M14 4h6v6M20 4l-9 9M10 5H5v14h14v-5",
    media: "M9 18V5l11-2v13M9 9l11-2M9 18c0 2-5 3-5 0s5-3 5 0M20 16c0 2-5 3-5 0s5-3 5 0",
    more: "M5 12h.01M12 12h.01M19 12h.01",
    expand: "M6 9l6 6 6-6",
    dismiss: "M6 6l12 12M18 6L6 18"
};
const stateLabels = { playing: "Playing", paused: "Paused", ended: "Ended", sleeping: "Sleeping", unresponsive: "Waiting for tab", unknown: "Ready" };
const failures = {
    "session-gone": "This player is no longer available. Choose a current player.",
    unsupported: "This player no longer supports that control.",
    unavailable: "This player is not ready. Open its tab and try again.",
    unconfirmed: "The player did not confirm the action. Check its tab before trying again.",
    failed: "The action could not be completed. Check the player in its tab."
};
const views = new Map();
const pending = new Set();
let previousPrimaries = new Map();
let currentEpoch;
let refreshing = false;
let refreshFailed = false;
let latestAction = 0;
let nextViewId = 0;

function element(tag, className) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    return node;
}

function icon(name) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("aria-hidden", "true");
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", icons[name]);
    svg.append(path);
    return svg;
}

function feedback(message, target) {
    const container = target?.article || resultText;
    (target?.message || resultText).textContent = message;
    container.hidden = !message;
}

function createFeedback(controls) {
    const article = element("div", "player-result");
    article.hidden = true;
    article.setAttribute("role", "status");
    const message = element("p");
    const dismiss = element("button", "icon-button dismiss-result");
    dismiss.type = "button";
    dismiss.setAttribute("aria-label", "Dismiss message");
    dismiss.append(icon("dismiss"));
    dismiss.onclick = () => {
        article.hidden = true;
        controls.querySelector("button")?.focus();
    };
    article.append(message, dismiss);
    return { article, message, action: 0 };
}

function updatePendingControls() {
    for (const button of document.querySelectorAll("button[data-command]")) {
        const busy = pending.has(button.dataset.command);
        button.setAttribute("aria-disabled", String(busy));
        button.setAttribute("aria-busy", String(busy));
    }
}

function updateControls(container, session, epoch, resultView) {
    const operations = Object.keys(labels).filter((operation) => session.capabilities.includes(operation) &&
        !(operation === "play" && session.state === "playing" && session.capabilities.includes("pause")) &&
        !(operation === "pause" && ["paused", "ended", "sleeping"].includes(session.state) && session.capabilities.includes("play")));
    const target = JSON.stringify([epoch, session.id, operations]);
    if (container.dataset.target === target) return;
    const binding = JSON.stringify([epoch, session.id]);
    if (container.dataset.binding !== binding) {
        resultView.action = ++latestAction;
        feedback("", resultView);
    }
    const focused = document.activeElement;
    const focusOperation = container.contains(focused) && container.dataset.binding === binding
        ? focused.dataset.operation : null;
    container.dataset.binding = binding;
    container.dataset.target = target;
    container.replaceChildren();
    for (const operation of operations) {
        const label = labels[operation];
        const button = element("button", "icon-button" +
            (["play", "pause"].includes(operation) ? " primary-control" : operation === "activate" ? " open-tab" : ""));
        button.type = "button";
        button.dataset.operation = operation;
        button.dataset.command = JSON.stringify([epoch, session.id, operation === "activate" ? "activation" : "playback"]);
        button.title = label;
        button.setAttribute("aria-label", label);
        if (operation === "activate") {
            const text = element("span");
            text.textContent = label;
            button.append(text);
        }
        button.append(icon(operation));
        button.onclick = async () => {
            const key = button.dataset.command;
            if (pending.has(key)) return;
            const action = ++latestAction;
            resultView.action = action;
            feedback("", resultView);
            pending.add(key);
            updatePendingControls();
            try {
                const result = await browserApi.runtime.sendMessage({ type: "control", sessionId: session.id, epoch, operation });
                const status = result?.status || "unavailable";
                if (action === resultView.action && container.dataset.binding === binding) {
                    feedback(status === "completed" ? "" : `${label}: ${failures[status] || failures.failed}`, resultView);
                }
            } catch {
                if (action === resultView.action && container.dataset.binding === binding) {
                    feedback("The extension could not reach this player. Reopen the popup and try again.", resultView);
                }
            } finally {
                pending.delete(key);
                updatePendingControls();
                void refresh();
            }
        };
        container.append(button);
    }
    if (focusOperation) {
        const operation = operations.includes(focusOperation) ? focusOperation :
            ["play", "pause"].includes(focusOperation) ? operations.find((value) => ["play", "pause"].includes(value)) : null;
        container.querySelector(`[data-operation="${operation}"]`)?.focus();
    }
}

function createPlayerRow() {
    const article = element("div", "player");
    const title = element("h3");
    const detail = element("p", "player-detail");
    const controls = element("div", "controls");
    const result = createFeedback(controls);
    article.append(title, detail, controls, result.article);
    return { article, title, detail, controls, result };
}

function createTabView() {
    const article = element("article", "tab");
    const sourceRow = element("div", "source-row");
    const source = element("span", "source");
    const separator = element("span", "source-separator");
    separator.textContent = "\u00b7";
    separator.setAttribute("aria-hidden", "true");
    const playbackState = element("span", "playback-state");
    sourceRow.append(source, separator, playbackState);
    const media = element("div", "media");
    const artwork = element("div", "artwork");
    artwork.setAttribute("aria-hidden", "true");
    artwork.append(icon("media"));
    const metadata = element("div", "metadata");
    const title = element("h3", "media-title");
    const artist = element("p", "artist");
    metadata.append(sourceRow, title, artist);
    const more = element("details", "more");
    const moreSummary = element("summary", "icon-button more-summary");
    moreSummary.title = "More controls";
    moreSummary.append(icon("more"));
    const menu = element("div", "more-menu");
    const additional = element("button", "additional-controls");
    additional.type = "button";
    const additionalLabel = element("span");
    additionalLabel.textContent = "Additional controls";
    additional.append(additionalLabel, icon("expand"));
    menu.append(additional);
    more.append(moreSummary, menu);
    media.append(artwork, metadata, more);
    const timeline = element("div", "timeline");
    const elapsed = element("span");
    const progress = element("progress");
    progress.setAttribute("aria-label", "Playback progress");
    const duration = element("span");
    timeline.append(elapsed, progress, duration);
    const controls = element("div", "controls");
    const result = createFeedback(controls);
    const note = element("p", "player-note");
    const details = element("div", "player-list");
    details.id = `additional-players-${++nextViewId}`;
    details.hidden = true;
    const heading = element("h3", "player-list-heading");
    heading.textContent = "Additional controls";
    const players = element("div", "players");
    details.append(heading, players);
    additional.setAttribute("aria-controls", details.id);
    additional.setAttribute("aria-expanded", "false");
    additional.onclick = () => {
        details.hidden = !details.hidden;
        additional.setAttribute("aria-expanded", String(!details.hidden));
        more.open = false;
        moreSummary.focus();
    };
    article.append(media, timeline, controls, note, details, result.article);
    return { article, source, playbackState, artwork, title, artist, timeline, elapsed, progress, duration,
        controls, note, details, more, moreSummary, additional, players, result, rows: new Map() };
}

function updateArtwork(view, url) {
    const source = imageUrl(url);
    if (view.artwork.dataset.source === source) return;
    view.artwork.dataset.source = source;
    view.image?.remove();
    view.image = null;
    if (!source) return;
    const img = element("img");
    img.alt = "";
    img.referrerPolicy = "no-referrer";
    img.crossOrigin = "anonymous";
    img.hidden = true;
    img.onload = () => { img.hidden = false; };
    img.onerror = () => { img.hidden = true; };
    img.src = source;
    view.artwork.append(img);
    view.image = img;
}

function time(seconds) {
    const value = Math.max(0, Math.floor(seconds));
    const hours = Math.floor(value / 3600);
    const minutes = Math.floor(value / 60) % 60;
    return (hours ? `${hours}:${String(minutes).padStart(2, "0")}` : String(minutes)) + `:${String(value % 60).padStart(2, "0")}`;
}

function updateTimeline(view, session) {
    const visible = Number.isFinite(session.duration) && session.duration > 0 && Number.isFinite(session.position) &&
        !["sleeping", "unresponsive"].includes(session.state);
    view.timeline.hidden = !visible;
    if (!visible) return;
    const position = Math.max(0, Math.min(session.position, session.duration));
    view.elapsed.textContent = time(position);
    view.duration.textContent = time(session.duration);
    view.progress.max = session.duration;
    view.progress.value = position;
    view.progress.setAttribute("aria-valuetext", `${time(position)} of ${time(session.duration)}`);
}

function reconcile(parent, nodes) {
    let current = parent.firstElementChild;
    for (const node of nodes) {
        if (node === current) current = current.nextElementSibling;
        else parent.insertBefore(node, current);
    }
    while (current) {
        const next = current.nextElementSibling;
        current.remove();
        current = next;
    }
}

function render(state) {
    if (state.epoch !== currentEpoch) previousPrimaries.clear();
    currentEpoch = state.epoch;
    const groups = groupSessions(state.sessions, previousPrimaries);
    previousPrimaries = new Map(groups.map((group) => [group.tabId, group.primary.id]));
    document.querySelector("#empty").hidden = groups.length !== 0;
    document.querySelector("#section-heading").hidden = groups.length === 0;
    const articles = [];
    for (const group of groups) {
        let view = views.get(group.tabId);
        if (!view) { view = createTabView(); views.set(group.tabId, view); }
        const primary = group.primary;
        let site = primary.siteName || "";
        if (!site) {
            try { site = new URL(primary.tabUrl || primary.page).hostname.replace(/^www\./, ""); } catch {}
        }
        site ||= "Browser tab";
        view.source.textContent = site;
        view.source.title = site;
        view.title.textContent = primary.title || primary.pageTitle || "Untitled media";
        view.title.title = view.title.textContent;
        view.article.setAttribute("aria-label", `${view.title.textContent} - ${site}`);
        const secondary = [primary.artist, primary.album, primary.pageTitle].find((value) =>
            value && ![view.title.textContent, site].some((text) => text.toLowerCase() === value.toLowerCase()));
        view.artist.textContent = secondary || "";
        view.artist.title = view.artist.textContent;
        view.artist.hidden = !secondary;
        view.playbackState.textContent = stateLabels[primary.state] || stateLabels.unknown;
        view.playbackState.dataset.state = primary.state;
        updateArtwork(view, primary.artwork);
        updateTimeline(view, primary);
        updateControls(view.controls, primary, state.epoch, view.result);
        view.note.textContent = primary.state === "sleeping" ?
            (primary.capabilities.includes("play") ? "Open this tab or press Play to wake it." : "Open this tab to wake it.") :
            primary.state === "unresponsive" ? "Open this tab to check its player." : "";
        view.note.hidden = !view.note.textContent;
        const binding = JSON.stringify([state.epoch, primary.id]);
        const additionalPlayers = group.players.filter((player) => player.id !== primary.id);
        const hadAdditionalFocus = view.details.contains(document.activeElement);
        const hadMoreFocus = view.more.contains(document.activeElement);
        if (view.binding !== binding || !additionalPlayers.length) {
            view.details.hidden = true;
            view.additional.setAttribute("aria-expanded", "false");
            view.more.open = false;
            if (hadAdditionalFocus || hadMoreFocus) {
                if (additionalPlayers.length) view.moreSummary.focus();
                else view.controls.querySelector("button")?.focus();
            }
        }
        view.binding = binding;
        view.more.hidden = additionalPlayers.length === 0;
        view.moreSummary.setAttribute("aria-label", `More controls for ${site}`);
        const rows = [];
        for (const player of additionalPlayers) {
            let row = view.rows.get(player.id);
            if (!row) { row = createPlayerRow(); view.rows.set(player.id, row); }
            row.title.textContent = player.title || player.pageTitle || "Untitled media";
            row.detail.textContent = stateLabels[player.state] || stateLabels.unknown;
            updateControls(row.controls, player, state.epoch, row.result);
            rows.push(row.article);
        }
        const playerIds = new Set(additionalPlayers.map((player) => player.id));
        for (const id of view.rows.keys()) { if (!playerIds.has(id)) view.rows.delete(id); }
        const removedFocus = hadAdditionalFocus && !rows.some((row) => row.contains(document.activeElement));
        reconcile(view.players, rows);
        if (removedFocus) {
            if (additionalPlayers.length) view.moreSummary.focus();
            else view.controls.querySelector("button")?.focus();
        }
        articles.push(view.article);
    }
    const tabIds = new Set(groups.map((group) => group.tabId));
    for (const id of views.keys()) { if (!tabIds.has(id)) views.delete(id); }
    reconcile(root, articles);
    updatePendingControls();
}

function connectionGuidance(detail) {
    const error = detail.toLowerCase();
    const setup = "Open Media Controls, then Media sources > Browser companion > Configure to enable browser integration.";
    if (error.includes("forbidden")) return `This extension ID is not allowed by the desktop companion. ${setup} For an unpacked extension, save the ID below under Advanced > Developer setup.`;
    if (error.includes("not found") || error.includes("not registered")) return `The desktop companion is not registered for this browser. ${setup}`;
    if (error.includes("exited") || error.includes("failed to start")) return "The desktop companion could not stay connected. Open Media Controls to check browser integration, then reconnect.";
    return setup;
}

function renderConnection(state) {
    const labels = { connected: "Desktop connected", connecting: "Connecting...", disconnected: "Desktop offline" };
    const connection = Object.hasOwn(labels, state.connectionState) ? state.connectionState : "disconnected";
    document.querySelector("#status").textContent = labels[connection] || labels.disconnected;
    document.querySelector("#connection-dot").dataset.state = connection;
    document.querySelector("#connection-warning").hidden = connection !== "disconnected";
    document.querySelector("#connection-help").hidden = connection !== "disconnected";
    document.querySelector("#connection-detail").textContent = state.connectionStatus || "";
    document.querySelector("#connection-guidance").textContent = connectionGuidance(state.connectionStatus || "");
    document.querySelector("#extension-id").textContent = browserApi.runtime.id || "Unavailable";
    reconnect.hidden = connection !== "disconnected";
    reconnect.disabled = connection === "connecting";
}

async function refresh() {
    if (refreshing) return;
    refreshing = true;
    try {
        const state = await browserApi.runtime.sendMessage({ type: "status" });
        renderConnection(state);
        render(state);
        if (refreshFailed) feedback("");
        refreshFailed = false;
    } catch {
        refreshFailed = true;
        renderConnection({ connectionState: "disconnected", connectionStatus: "Unable to refresh. Reopen the popup." });
        feedback("Unable to refresh. Reopen the popup.");
    } finally { refreshing = false; }
}

reconnect.onclick = async () => {
    reconnect.disabled = true;
    feedback("");
    try {
        await browserApi.runtime.sendMessage({ type: "reconnect" });
        await refresh();
    } catch {
        feedback("Could not reconnect. Open Media Controls to check browser integration.");
    } finally { reconnect.disabled = false; }
};
document.addEventListener("click", (event) => {
    for (const view of views.values()) {
        if (!view.more.contains(event.target)) view.more.open = false;
    }
});
document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape") return;
    for (const view of views.values()) {
        if (view.more.open) {
            view.more.open = false;
            view.moreSummary.focus();
        }
    }
});
void refresh();
setInterval(() => { void refresh(); }, 1000);
