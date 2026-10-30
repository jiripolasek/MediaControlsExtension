import assert from "node:assert/strict";
import fs from "node:fs";
import net from "node:net";
import path from "node:path";
import { spawn, execFileSync } from "node:child_process";
import { createInterface } from "node:readline";

assert.equal(process.env.USERNAME, "WDAGUtilityAccount", "Run only inside Windows Sandbox");
const root = process.argv[2] || "C:\\FirefoxProof";
const results = path.join(root, "results");
const profile = `C:\\FirefoxProofWork\\profile-${Date.now()}`;
const addonId = "media-controls-companion@jiripolasek.com";
const alias = path.join(process.env.LOCALAPPDATA, "Microsoft\\WindowsApps\\MediaControlsBrowserBridge.exe");
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
fs.mkdirSync(results, { recursive: true });
const save = (name, value) => fs.writeFileSync(path.join(results, `${name}.json`), JSON.stringify(value, null, 2));
const log = (message) => console.log(`${new Date().toISOString()} ${message}`);
async function until(action, predicate, seconds = 45) {
    const end = Date.now() + seconds * 1000;
    let value;
    do {
        try { value = await action(); if (predicate(value)) return value; }
        catch (error) { value = { error: error.message }; }
        await sleep(1000);
    } while (Date.now() < end);
    throw new Error(`Timed out: ${JSON.stringify(value)}`);
}
function phase(name, label = "") {
    log(`Package phase: ${name} ${label}`);
    execFileSync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
        path.join(root, "Invoke-IntegratedPackageSandboxProof.ps1"), "-Phase", name,
        "-InputDirectory", root, "-ResultDirectory", results, "-Firefox", ...(label ? ["-Label", label] : [])],
    { timeout: 120000, windowsHide: true, encoding: "utf8" });
}
function catalog() {
    return JSON.parse(execFileSync(alias, ["--list"], { timeout: 10000, windowsHide: true, encoding: "utf8" }));
}
class Marionette {
    pending = new Map();
    buffer = Buffer.alloc(0);
    nextId = 1;
    async connect() {
        this.socket = await new Promise((resolve, reject) => {
            const socket = net.connect(2829, "127.0.0.1", () => resolve(socket));
            socket.once("error", reject);
        });
        this.socket.on("data", (data) => {
            this.buffer = Buffer.concat([this.buffer, data]);
            while (true) {
                const colon = this.buffer.indexOf(58);
                if (colon < 0) break;
                const length = Number(this.buffer.subarray(0, colon).toString());
                if (this.buffer.length < colon + 1 + length) break;
                const message = JSON.parse(this.buffer.subarray(colon + 1, colon + 1 + length));
                this.buffer = this.buffer.subarray(colon + 1 + length);
                if (!Array.isArray(message)) { this.greeting = message; continue; }
                const pending = this.pending.get(message[1]);
                if (pending) {
                    this.pending.delete(message[1]);
                    clearTimeout(pending.timer);
                    if (message[2]) pending.reject(new Error(JSON.stringify(message[2])));
                    else pending.resolve(message[3]);
                }
            }
        });
        this.socket.on("error", (error) => log(`Marionette: ${error.message}`));
    }
    command(name, args = {}) {
        const id = this.nextId++;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => { this.pending.delete(id); reject(new Error(`Timed out: ${name}`)); }, 30000);
            this.pending.set(id, { resolve, reject, timer });
            const payload = Buffer.from(JSON.stringify([0, id, name, args]));
            this.socket.write(Buffer.concat([Buffer.from(`${payload.length}:`), payload]));
        });
    }
    async script(script, args = []) {
        return (await this.command("WebDriver:ExecuteScript", { script, args, newSandbox: false })).value;
    }
    async asyncScript(script, args = []) {
        return (await this.command("WebDriver:ExecuteAsyncScript", { script, args, newSandbox: false })).value;
    }
}

const driver = new Marionette();
let firefox, fixture, probe;
let probeReplies = [];
let probeWaiters = [];
async function snapshot() {
    const result = new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error("Probe timed out")), 15000);
        const consume = (line) => { clearTimeout(timer); resolve(JSON.parse(line)); };
        if (probeReplies.length) consume(probeReplies.shift()); else probeWaiters.push(consume);
    });
    probe.stdin.write("snapshot\n");
    const value = await result;
    if (value.error) throw new Error(value.error);
    return value;
}
async function browserErrors(name) {
    await sleep(6500);
    await driver.command("Marionette:SetContext", { value: "chrome" });
    save(name, await driver.script("return Services.console.getMessageArray().map(message => message.message).slice(-30);"));
    await driver.command("Marionette:SetContext", { value: "content" });
}
async function connected(name) {
    const state = await until(catalog, (c) => c.connections?.length === 1 && c.connections[0].hasSnapshot &&
        c.connections[0].heartbeatAgeMs >= 0 && c.connections[0].heartbeatAgeMs < 6000);
    save(`${name}-catalog`, state);
    phase("Capture", name);
    log(`${name}: connected`);
    return state;
}
try {
    if (!process.argv.includes("--resume")) {
        phase("Baseline");
        phase("Install");
        phase("Register");
    }
    assert(!fs.existsSync(profile), "Use a fresh proof profile");
    fs.mkdirSync(profile, { recursive: true });
    fs.writeFileSync(path.join(profile, "user.js"), [
        ["marionette.port", 2829], ["browser.shell.checkDefaultBrowser", false],
        ["browser.startup.homepage_override.mstone", "ignore"], ["browser.startup.page", 0],
        ["media.autoplay.default", 0], ["media.autoplay.blocking_policy", 0],
        ["media.hardwaremediakeys.enabled", true], ["datareporting.policy.dataSubmissionEnabled", false],
        ["browser.newtabpage.enabled", false], ["browser.tabs.warnOnClose", false]
    ].map(([key, value]) => `user_pref(${JSON.stringify(key)}, ${JSON.stringify(value)});`).join("\r\n"));
    fixture = spawn(process.execPath, [path.join(root, "fixture\\server.mjs")], { windowsHide: true, stdio: "ignore" });
    firefox = spawn("C:\\Program Files\\Mozilla Firefox\\firefox.exe",
        ["--no-remote", "--profile", profile, "--marionette", "--remote-allow-system-access", "about:blank"],
        { windowsHide: true, stdio: ["ignore", fs.openSync(path.join(results, "firefox.log"), "w"),
            fs.openSync(path.join(results, "firefox-errors.log"), "w")] });
    await until(async () => { await driver.connect(); return true; }, Boolean);
    save("firefox-session", await driver.command("WebDriver:NewSession", { capabilities: { alwaysMatch: { acceptInsecureCerts: false } } }));
    save("addon-install", await driver.command("Addon:Install", { path: path.join(root, "extension"), temporary: true }));
    await driver.command("Marionette:SetContext", { value: "chrome" });
    const uuid = await driver.script(`return JSON.parse(Services.prefs.getStringPref("extensions.webextensions.uuids"))[arguments[0]];`, [addonId]);
    assert(uuid, "Firefox assigned the addon a UUID");
    await sleep(2000);
    save("addon-diagnostics", await driver.script(`
        const {ExtensionParent} = ChromeUtils.importESModule("resource://gre/modules/ExtensionParent.sys.mjs");
        const extension = ExtensionParent.GlobalManager.getExtension(arguments[0]);
        return { uuid: extension.uuid, backgroundState: extension.backgroundState,
            permissions: [...extension.permissions], views: [...extension.views].map(view => ({type: view.viewType, url: view.uri?.spec})),
            messages: Services.console.getMessageArray().map(message => message.message).slice(-35) };
    `, [addonId]));
    await driver.command("Marionette:SetContext", { value: "content" });
    const before = await connected("connected");
    phase("Upgrade");
    const after = await connected("upgraded");
    assert.notEqual(after.connections[0].connectionId, before.connections[0].connectionId);
    phase("Remove");
    await browserErrors("removed-errors");
    phase("Remove", "again");
    phase("Register", "reenabled");
    await connected("reenabled");

    probe = spawn(path.join(root, "probe\\BrowserCompanion.Probe.exe"), [], { windowsHide: true, stdio: ["pipe", "pipe", "inherit"] });
    createInterface({ input: probe.stdout }).on("line", (line) => {
        if (probeWaiters.length) probeWaiters.shift()(line); else probeReplies.push(line);
    });
    await until(() => probeReplies.shift(), (line) => !!line && JSON.parse(line).ready === true);
    await driver.command("WebDriver:Navigate", { url: "http://127.0.0.1:8765/" });
    await driver.asyncScript("const done = arguments[arguments.length - 1]; document.querySelector('#first').play().then(() => done(true), error => done(String(error))); ");
    const media = await until(catalog, (c) => c.connections?.[0]?.sessions?.some((s) => s.state === "playing"));
    save("media-catalog", media);
    const initial = await until(snapshot, (s) => s.raw.length > 0 && s.claims.length > 0 &&
        s.sessions.some((item) => item.source.Provider?.Id === "browser.companion") &&
        s.sessions.every((item) => item.source.Provider?.Id !== "gsmtc"), 40);
    save("media-composite", initial);
    const appId = media.connections[0].applicationId;
    assert(initial.raw.some((s) => s.applicationId === appId), "Firefox native identity matches actual GSMTC");
    const first = media.connections[0].sessions.find((s) => s.state === "playing" && s.capabilities.includes("nexttrack"));
    assert(first, "Page Media Session handlers were captured");
    const command = (operation) => JSON.parse(execFileSync(alias,
        ["--command", media.connections[0].connectionId, first.id, operation], { timeout: 10000, windowsHide: true, encoding: "utf8" }));
    save("pause-command", command("pause"));
    assert(await driver.script("return document.querySelector('#first').paused;"));
    save("play-command", command("play"));
    assert.equal(await driver.script("return document.querySelector('#first').paused;"), false);
    save("next-command", command("nexttrack"));
    assert.equal(await driver.script("return navigator.mediaSession.metadata.title;"), "Fixture track 2");

    const disconnectedAt = Date.now();
    await driver.command("Addon:Uninstall", { id: addonId });
    const samples = [];
    let released;
    while (Date.now() - disconnectedAt < 75000) {
        const state = await snapshot();
        state.elapsedMs = Date.now() - disconnectedAt;
        samples.push(state);
        if (!state.claims.some((c) => c.ApplicationId === appId)) { released = state; break; }
        await sleep(1000);
    }
    save("fallback-samples", samples);
    assert(released, "GSMTC fallback released the Firefox claim");
    assert(released.elapsedMs >= 50000, "Fallback kept its delay");
    const fallback = await until(snapshot, (s) => s.sessions.some((item) =>
        item.source.NativeApplication?.ApplicationId === appId && item.source.Provider?.Id === "gsmtc"), 10);
    save("fallback-composite", fallback);
    save("fallback", released);
    const recoveringAt = Date.now();
    await driver.command("Addon:Install", { path: path.join(root, "extension"), temporary: true });
    const recovered = await connected("recovered");
    const recovery = await until(snapshot, (s) => s.claims.some((c) => c.ApplicationId === appId) &&
        s.sessions.some((item) => item.source.Provider?.Id === "browser.companion") &&
        s.sessions.every((item) => item.source.Provider?.Id !== "gsmtc"), 25);
    save("recovery", { elapsedMs: Date.now() - recoveringAt, catalog: recovered, snapshot: recovery });
    phase("Uninstall");
    await browserErrors("uninstalled-errors");
    phase("Cleanup");
    save("passed", { utc: new Date().toISOString(), version: "Firefox compatibility proof" });
    log("Proof passed");
} catch (error) {
    save("failure", { message: error.message, stack: error.stack });
    log(error.stack);
    process.exitCode = 1;
} finally {
    probe?.stdin.end("quit\n");
    try { if (driver.socket) await driver.command("Marionette:Quit", { flags: ["eForceQuit"] }); } catch {}
    driver.socket?.destroy();
    fixture?.kill();
    firefox?.kill();
}
