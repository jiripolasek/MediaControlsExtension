function rank(session, playingDocuments) {
    const playback = session.capabilities.includes("play") || session.capabilities.includes("pause");
    if (session.kind === "media-session" && playback) {
        return playingDocuments.has(session.documentId) ? 0 : 2;
    }
    if (session.kind === "html-media" && playback) {
        if (session.state === "playing") return 1;
        return session.state === "ended" ? 4 : 3;
    }
    return 5;
}

export function groupSessions(sessions, previousPrimaries = new Map()) {
    const tabs = new Map();
    for (const session of sessions) {
        if (!tabs.has(session.tabId)) tabs.set(session.tabId, []);
        tabs.get(session.tabId).push(session);
    }
    return [...tabs].map(([tabId, players]) => {
        const playingDocuments = new Set(players.filter((player) => player.state === "playing").map((player) => player.documentId));
        const bestRank = Math.min(...players.map((player) => rank(player, playingDocuments)));
        const candidates = players.filter((player) => rank(player, playingDocuments) === bestRank);
        // Keep equally suitable choices stable when snapshot order changes.
        const primary = candidates.find((player) => player.id === previousPrimaries.get(tabId)) ||
            candidates.find((player) => player.frameId === 0) || candidates[0];
        return { tabId, primary, players };
    });
}
