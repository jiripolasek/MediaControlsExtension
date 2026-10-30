import assert from "node:assert/strict";
import { spawn, execFileSync } from "node:child_process";
import { cp, mkdir, mkdtemp, rm, writeFile } from "node:fs/promises";
import net from "node:net";
import path from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { groupSessions } from "../../src/BrowserCompanion/extension/grouping.mjs";

const buildDirectory = process.env.BROWSER_COMPANION_TEST_HOST ||
    fileURLToPath(new URL("../../src/BrowserCompanion/host/bin/Debug/net10.0/", import.meta.url));
const artifactDirectory = fileURLToPath(new URL("../../artifacts/BrowserCompanion/tests/", import.meta.url));
const origin = `chrome-extension://${"a".repeat(32)}/`;

test("native browser metadata keeps custom Chromium installations separate", { timeout: 15000 }, async (t) => {
    const exe = path.join(buildDirectory, "JPSoftworks.MediaControlsExtension.BrowserCompanion.exe");
    const environment = { ...process.env, BROWSER_MEDIA_SPIKE_INSTANCE: crypto.randomUUID() };
    const service = spawn(exe, ["--service"], { env: environment, windowsHide: true, stdio: ["ignore", "ignore", "pipe"] });
    const sockets = [];
    t.after(async () => {
        for (const socket of sockets) socket.destroy();
        if (service.exitCode === null && service.signalCode === null) {
            await new Promise((resolve) => { service.once("close", resolve); service.kill(); });
        }
    });
    await new Promise((resolve, reject) => {
        let stderr = "";
        const timer = setTimeout(() => reject(new Error(`Service startup timed out: ${stderr}`)), 5000);
        service.stderr.on("data", (chunk) => {
            stderr += chunk;
            if (stderr.includes("Listening on")) { clearTimeout(timer); resolve(); }
        });
        service.once("error", (error) => { clearTimeout(timer); reject(error); });
    });
    const cli = (args) => execFileSync(exe, args, { env: environment, windowsHide: true, encoding: "utf8" });
    const pipe = cli(["--pipe-name"]).trim();
    const profileId = crypto.randomUUID();
    const paths = ["C:\\Browsers\\Brave\\brave.exe", "C:\\Browsers\\Custom\\browser.exe"];
    for (const executable of paths) {
        const socket = net.connect(`\\\\.\\pipe\\${pipe}`);
        sockets.push(socket);
        await new Promise((resolve, reject) => { socket.once("connect", resolve); socket.once("error", reject); });
        const wire = framing(socket);
        wire.send({ type: "browser", browserName: "Custom Chromium", browserExecutable: executable, applicationId: "" });
        wire.send({ type: "hello", version: 1, profileId, epoch: crypto.randomUUID(), browser: "Chromium" });
        assert.equal((await wire.read()).type, "ready");
    }
    const catalog = JSON.parse(cli(["--list"]));
    assert.equal(catalog.connections.length, 2);
    assert.deepEqual(catalog.connections.map((connection) => connection.browserExecutable).sort(), paths.sort());
    assert.ok(catalog.connections.every((connection) => connection.browser === "Custom Chromium"));
    assert.ok(catalog.connections.every((connection) => connection.applicationId === ""));
});

test("native bridges start one shared relay and release it after idle", { timeout: 100000 }, async (t) => {
    await mkdir(artifactDirectory, { recursive: true });
    const runtimeDirectory = await mkdtemp(path.join(artifactDirectory, "startup-test-"));
    const exe = path.join(runtimeDirectory, "JPSoftworks.MediaControlsExtension.BrowserCompanion.exe");
    const environment = { ...process.env, BROWSER_MEDIA_SPIKE_INSTANCE: crypto.randomUUID(), COMPANION_TEST_EXE: exe };
    const bridges = [];
    const processes = (action = "Select-Object -ExpandProperty Id") => execFileSync("powershell.exe",
        ["-NoProfile", "-NonInteractive", "-Command",
            `Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $env:COMPANION_TEST_EXE } | ${action}`],
        { env: environment, windowsHide: true, encoding: "utf8" }).trim();
    t.after(async () => {
        for (const child of bridges) child.kill();
        processes("Stop-Process -Force");
        const relative = path.relative(path.resolve(artifactDirectory), path.resolve(runtimeDirectory));
        assert.ok(relative && !relative.startsWith("..") && !path.isAbsolute(relative));
        await rm(runtimeDirectory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    });
    await cp(buildDirectory, runtimeDirectory, { recursive: true });
    await writeFile(path.join(runtimeDirectory, "native-host.json"), JSON.stringify({ allowed_origins: [origin] }));
    assert.throws(() => execFileSync(exe, [`chrome-extension://${"b".repeat(32)}/`],
        { env: environment, windowsHide: true, stdio: "pipe" }), /origin is not registered/);
    assert.equal(processes(), "", "An unregistered extension must not start a relay");
    const firefoxId = "media-controls-companion@jiripolasek.com";
    const firefoxManifest = path.join(runtimeDirectory, "firefox-native-host.json");
    await writeFile(firefoxManifest, JSON.stringify({ allowed_extensions: [firefoxId] }));
    assert.throws(() => execFileSync(exe, [firefoxManifest, "unregistered@example.org"],
        { env: environment, windowsHide: true, stdio: "pipe" }), /origin is not registered/);
    assert.throws(() => execFileSync(exe, [path.join(runtimeDirectory, "another-manifest.json"), firefoxId],
        { env: environment, windowsHide: true, stdio: "pipe" }));
    assert.equal(processes(), "", "An unregistered Firefox add-on must not start a relay");
    await Promise.all([0, 1, 2].map(async (index) => {
        const child = spawn(exe, index === 2 ? [firefoxManifest, firefoxId] : [origin], { env: environment, windowsHide: true });
        bridges.push(child);
        const wire = framing(child.stdout, child.stdin);
        wire.send({ type: "hello", version: 1, profileId: crypto.randomUUID(), epoch: crypto.randomUUID(), browser: "Fixture" });
        assert.equal((await wire.read()).type, "ready");
    }));
    const catalog = JSON.parse(execFileSync(exe, ["--list"], { env: environment, windowsHide: true, encoding: "utf8" }));
    assert.equal(catalog.connections.length, 3, "Chromium and Firefox bridges must share the same relay");
    await new Promise((resolve) => setTimeout(resolve, 32000));
    assert.equal(processes().split(/\r?\n/).filter(Boolean).length, 4, "Active bridges keep exactly one relay alive");
    await Promise.all(bridges.map((child) => new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error("Bridge retained its native messaging handles after disconnect")), 5000);
        child.once("close", () => { clearTimeout(timer); resolve(); });
        child.stdin.end();
    })));
    await new Promise((resolve) => setTimeout(resolve, 33000));
    assert.equal(processes(), "", "The idle relay must exit without requiring Task Manager");
});

function framing(readable, writable = readable) {
    let buffer = Buffer.alloc(0);
    const queued = [];
    const readers = [];
    readable.on("data", (chunk) => {
        buffer = Buffer.concat([buffer, chunk]);
        while (buffer.length >= 4 && buffer.length >= buffer.readUInt32LE(0) + 4) {
            const length = buffer.readUInt32LE(0);
            const message = JSON.parse(buffer.subarray(4, length + 4).toString("utf8"));
            buffer = buffer.subarray(length + 4);
            if (readers.length) readers.shift()(message);
            else queued.push(message);
        }
    });
    return {
        send(message, fragmented = false) {
            const bytes = Buffer.from(JSON.stringify(message));
            const header = Buffer.alloc(4); header.writeUInt32LE(bytes.length);
            const frame = Buffer.concat([header, bytes]);
            if (fragmented) for (const byte of frame) writable.write(Buffer.from([byte]));
            else writable.write(frame);
        },
        async read() {
            if (queued.length) return queued.shift();
            return new Promise((resolve, reject) => {
                const timer = setTimeout(() => reject(new Error("Frame timeout")), 5000);
                readers.push((message) => { clearTimeout(timer); resolve(message); });
            });
        }
    };
}

test("real service and native bridge isolate profiles, revisions, disconnects, deadlines and browser heartbeats", { timeout: 45000 }, async (t) => {
    const children = [];
    const sockets = [];
    const childEnvironment = { ...process.env, BROWSER_MEDIA_SPIKE_INSTANCE: crypto.randomUUID() };
    await mkdir(artifactDirectory, { recursive: true });
    const runtimeDirectory = await mkdtemp(path.join(artifactDirectory, "service-test-"));
    t.after(async () => {
        for (const socket of sockets) socket.destroy();
        await Promise.all(children.map(({ child }) => new Promise((resolve) => {
            if (child.exitCode !== null || child.signalCode !== null || !child.pid) return resolve();
            child.once("close", resolve);
            child.kill();
        })));
        const relative = path.relative(path.resolve(artifactDirectory), path.resolve(runtimeDirectory));
        assert.ok(relative && !relative.startsWith("..") && !path.isAbsolute(relative), "Cleanup must stay inside companion test artifacts");
        await rm(runtimeDirectory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
    });
    await cp(buildDirectory, runtimeDirectory, { recursive: true });
    const exe = path.join(runtimeDirectory, "JPSoftworks.MediaControlsExtension.BrowserCompanion.exe");
    const manifestPath = path.join(runtimeDirectory, "native-host.json");
    await writeFile(manifestPath, JSON.stringify({ allowed_origins: [origin] }));

    function launch(args, environment = childEnvironment) {
        const child = spawn(exe, args, { windowsHide: true, env: environment });
        const process = { child, stderr: "" };
        child.stderr.on("data", (data) => { process.stderr = (process.stderr + data.toString()).slice(-8192); });
        child.on("error", (error) => { process.stderr += error.message; });
        children.push(process);
        return process;
    }
    async function startService(environment = childEnvironment) {
        const process = launch(["--service"], environment);
        await new Promise((resolve, reject) => {
            const fail = (message) => {
                clearTimeout(timer);
                reject(new Error(`${message}\n${process.stderr.trim()}`));
            };
            const timer = setTimeout(() => fail("Service startup timeout"), 5000);
            process.child.stderr.on("data", () => {
                if (process.stderr.includes("Listening on")) { clearTimeout(timer); resolve(); }
            });
            process.child.once("exit", (code) => fail(`Service exited: ${code}`));
            process.child.once("error", (error) => fail(`Service startup failed: ${error.message}`));
        });
    }
    function cli(args, environment = childEnvironment) {
        return execFileSync(exe, args, { encoding: "utf8", windowsHide: true, env: environment }).trim();
    }
    await startService();
    await assert.rejects(startService(), /Service exited: 1\s+The browser companion service is already running\./);
    const otherEnvironment = { ...childEnvironment, BROWSER_MEDIA_SPIKE_INSTANCE: crypto.randomUUID() };
    await startService(otherEnvironment);
    const pipeName = cli(["--pipe-name"]);
    assert.notEqual(pipeName, cli(["--pipe-name"], otherEnvironment));
    const defaultEnvironment = { ...childEnvironment };
    delete defaultEnvironment.BROWSER_MEDIA_SPIKE_INSTANCE;
    assert.notEqual(pipeName, cli(["--pipe-name"], defaultEnvironment));
    async function client(request) {
        const socket = net.connect(`\\\\.\\pipe\\${pipeName}`);
        sockets.push(socket);
        const wire = framing(socket);
        await new Promise((resolve, reject) => { socket.once("connect", resolve); socket.once("error", reject); });
        wire.send(request);
        const result = await wire.read();
        socket.end();
        return result;
    }
    async function browser(profileId = crypto.randomUUID(), heartbeatIntervalMs = 0) {
        const { child: process } = launch([origin]);
        const wire = framing(process.stdout, process.stdin);
        const epoch = crypto.randomUUID();
        wire.send({ type: "hello", version: 1, profileId, epoch, browser: "Fixture", heartbeatIntervalMs }, true);
        const ready = await wire.read();
        assert.equal(ready.type, "ready");
        return { process, wire, epoch, profileId, connectionId: ready.connectionId };
    }
    const a = await browser();
    const b = await browser();
    const watcher = net.connect(`\\\\.\\pipe\\${pipeName}`);
    sockets.push(watcher);
    await new Promise((resolve, reject) => { watcher.once("connect", resolve); watcher.once("error", reject); });
    const watched = framing(watcher);
    watched.send({ type: "watch", version: 2 });
    let watchedRevision = -1;
    async function watchUntil(predicate) {
        for (let attempt = 0; attempt < 30; attempt++) {
            const catalog = await watched.read();
            assert.equal(catalog.type, "catalog");
            assert.equal(catalog.version, 2);
            assert.ok(catalog.revision >= watchedRevision);
            watchedRevision = catalog.revision;
            if (predicate(catalog)) return catalog;
        }
        throw new Error("Watch catalog did not converge");
    }
    await watchUntil((catalog) => catalog.connections.length === 2);
    function snapshot(browser, revision, title) {
        browser.wire.send({ type: "snapshot", epoch: browser.epoch, revision,
            sessions: [{ id: "same-document:element-1", title, tabId: 7, frameId: 0, documentId: "same-document",
                kind: "html-media", state: "playing", capabilities: ["pause", "activate"] }] });
    }
    snapshot(a, 2, "First profile");
    snapshot(b, 1, "Second profile");
    async function waitFor(predicate) {
        const deadline = Date.now() + 3000;
        while (Date.now() < deadline) {
            const list = await client({ type: "list" });
            if (predicate(list)) return list;
            await new Promise((resolve) => setTimeout(resolve, 25));
        }
        throw new Error("Catalog did not converge");
    }
    await waitFor((list) => list.connections.length === 2 && list.connections.every((item) => item.sessions.length === 1));
    const command = (browser, operation = "pause") => client({ type: "command", connectionId: browser.connectionId,
        sessionId: "same-document:element-1", operation });

    const pending = command(a);
    const dispatched = await a.wire.read();
    assert.equal(dispatched.epoch, a.epoch);
    assert.equal(dispatched.operation, "pause");
    assert.equal(dispatched.allowForeground, false);
    a.wire.send({ type: "result", requestId: dispatched.requestId, status: "completed" });
    assert.equal((await pending).status, "completed");
    assert.equal((await command(b, "nexttrack")).status, "unsupported");

    snapshot(a, 1, "Obsolete revision");
    const next = command(a, "activate");
    const activation = await a.wire.read();
    assert.equal(activation.allowForeground, false, "Legacy commands do not inherit foreground permission");
    a.wire.send({ type: "result", requestId: activation.requestId, status: "completed" });
    assert.equal((await next).status, "completed");
    const permissionRequest = client({ type: "command", connectionId: a.connectionId,
        sessionId: "same-document:element-1", operation: "activate", allowForeground: true });
    const permissionCommand = await a.wire.read();
    assert.equal(permissionCommand.allowForeground, false, "The native bridge must not treat the Node test launcher as a browser");
    a.wire.send({ type: "result", requestId: permissionCommand.requestId, status: "completed" });
    assert.equal((await permissionRequest).status, "completed");
    const list = await client({ type: "list" });
    assert.equal(list.connections.find((item) => item.connectionId === a.connectionId).sessions[0].title, "First profile");

    const initialWatch = await watchUntil((catalog) => catalog.connections.every((item) => item.tabs.length === 1));
    const originalTab = initialWatch.connections.find((item) => item.connectionId === a.connectionId).tabs[0];
    const fenced = (tab, extra = {}) => client({ type: "command", connectionId: a.connectionId,
        sessionId: tab.sessionId, tabId: tab.tabId, generation: tab.generation, operation: "pause", ...extra });
    const samples = [
        [
            { id: "top:document", documentId: "top", frameId: 0, kind: "media-session", state: "paused" },
            { id: "frame:video", documentId: "frame", frameId: 1, kind: "html-media", state: "playing" }
        ],
        [
            { id: "frame:video2", documentId: "frame", frameId: 1, kind: "html-media", state: "playing" },
            { id: "frame:video", documentId: "frame", frameId: 1, kind: "html-media", state: "playing" }
        ],
        [
            { id: "top:document", documentId: "top", frameId: 0, kind: "media-session", state: "paused" },
            { id: "top:video", documentId: "top", frameId: 0, kind: "html-media", state: "playing" },
            { id: "frame:video", documentId: "frame", frameId: 1, kind: "html-media", state: "paused" }
        ],
        [{ id: "frame:video", documentId: "frame", frameId: 1, kind: "html-media", state: "playing" }]
    ];
    let previousTab = originalTab;
    let previousPrimaries = new Map([[7, originalTab.sessionId]]);
    const retiredTabs = [];
    for (const [index, sample] of samples.entries()) {
        const sessions = sample.map((item) => ({ ...item, tabId: 7, capabilities: ["play", "pause", "activate"] }));
        a.wire.send({ type: "snapshot", epoch: a.epoch, revision: index + 3, sessions });
        const catalog = await watchUntil((value) => value.connections.find((item) => item.connectionId === a.connectionId)?.revision === index + 3);
        const tab = catalog.connections.find((item) => item.connectionId === a.connectionId).tabs[0];
        assert.equal(tab.sessionId, groupSessions(sessions, previousPrimaries)[0].primary.id);
        if (tab.sessionId === previousTab.sessionId) assert.equal(tab.generation, previousTab.generation);
        else {
            assert.ok(tab.generation > previousTab.generation);
            retiredTabs.push(previousTab);
        }
        for (const retired of retiredTabs) assert.equal((await fenced(retired)).status, "session-gone");
        previousTab = tab;
        previousPrimaries = new Map([[7, tab.sessionId]]);
    }
    assert.equal((await fenced(previousTab, { deadline: Date.now() - 1 })).status, "unavailable");
    const current = fenced(previousTab);
    const routed = await a.wire.read();
    assert.equal(routed.sessionId, previousTab.sessionId);
    a.wire.send({ type: "result", requestId: routed.requestId, status: "completed" });
    assert.equal((await current).status, "completed");
    snapshot(a, 10, "First profile");
    await watchUntil((catalog) => catalog.connections.find((item) => item.connectionId === a.connectionId)?.revision === 10);

    const abandoned = command(a);
    await a.wire.read();
    a.process.stdin.end();
    assert.equal((await abandoned).status, "session-gone");
    await waitFor((list) => list.connections.length === 1);
    await watchUntil((catalog) => catalog.connections.length === 1);
    const replacement = await browser(a.profileId);
    snapshot(replacement, 1, "Replacement");
    await waitFor((list) => list.connections.some((item) => item.connectionId === replacement.connectionId && item.sessions.length));
    assert.notEqual(replacement.connectionId, a.connectionId);
    assert.equal((await command(a)).status, "session-gone");
    assert.equal((await command(b)).status, "unconfirmed");

    const { child: malformed } = launch([origin]);
    const exited = new Promise((resolve) => malformed.once("exit", resolve));
    const oversized = Buffer.alloc(4); oversized.writeInt32LE(900001);
    malformed.stdin.end(oversized);
    assert.equal(await exited, 1);
    assert.equal((await client({ type: "list" })).connections.length, 2);
    assert.deepEqual(JSON.parse(cli(["--list"], otherEnvironment)).connections, []);

    const sleeping = await browser(crypto.randomUUID(), 5000);
    const healthy = await browser(crypto.randomUUID(), 5000);
    const silenceStarted = Date.now();
    snapshot(sleeping, 1, "Sleeping browser");
    snapshot(healthy, 1, "Responsive browser");
    await watchUntil((catalog) => catalog.connections.some((item) => item.connectionId === sleeping.connectionId && item.sessions.length));
    const beforeHeartbeat = (await client({ type: "list" })).connections.find((item) => item.connectionId === healthy.connectionId);
    assert.equal(beforeHeartbeat.hasSnapshot, true);
    assert.equal(beforeHeartbeat.heartbeatAgeMs, -1, "A snapshot is not a browser heartbeat");
    assert.equal(beforeHeartbeat.applicationId, "", "A Node-launched bridge must not claim a browser identity");
    healthy.wire.send({ type: "heartbeat", epoch: healthy.epoch });
    await watchUntil((catalog) => catalog.connections.some((item) => item.connectionId === healthy.connectionId && item.heartbeatAgeMs >= 0));
    const keepAlive = setInterval(() => {
        healthy.wire.send({ type: "heartbeat", epoch: healthy.epoch });
        if (Date.now() - silenceStarted < 15000) sleeping.wire.send({ type: "heartbeat", epoch: "old-epoch" });
    }, 1000);
    t.after(() => clearInterval(keepAlive));
    try {
        const expired = await watchUntil((catalog) => !catalog.connections.some((item) => item.connectionId === sleeping.connectionId));
        assert.ok(Date.now() - silenceStarted < 30000, "Wrong-epoch heartbeats must not extend the browser lease");
        assert.ok(expired.connections.some((item) => item.connectionId === healthy.connectionId && item.sessions.length));
        assert.equal((await command(sleeping)).status, "session-gone");
        const recovered = await browser(sleeping.profileId, 5000);
        snapshot(recovered, 1, "Recovered browser");
        await watchUntil((catalog) => catalog.connections.some((item) => item.connectionId === recovered.connectionId && item.sessions.length));
        assert.notEqual(recovered.connectionId, sleeping.connectionId);
        assert.equal((await command(sleeping)).status, "session-gone");
        const recoveredCommand = command(recovered);
        const message = await recovered.wire.read();
        assert.equal(message.epoch, recovered.epoch);
        recovered.wire.send({ type: "result", requestId: message.requestId, status: "completed" });
        assert.equal((await recoveredCommand).status, "completed");
        const replacedCommand = command(recovered);
        await recovered.wire.read();
        const replacementEpoch = await browser(recovered.profileId, 5000);
        snapshot(replacementEpoch, 1, "New worker epoch");
        const replacedCatalog = await watchUntil((catalog) => catalog.connections.some((item) => item.connectionId === replacementEpoch.connectionId && item.sessions.length));
        assert.equal(replacedCatalog.connections.filter((item) => item.profileId === recovered.profileId).length, 1);
        assert.equal((await replacedCommand).status, "session-gone");
        assert.equal((await command(recovered)).status, "session-gone");
    } finally {
        clearInterval(keepAlive);
    }
});
