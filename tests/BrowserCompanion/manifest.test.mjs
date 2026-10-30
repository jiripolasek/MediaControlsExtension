import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const repoRoot = fileURLToPath(new URL("../../", import.meta.url));
const artifactDirectory = path.join(repoRoot, "artifacts", "BrowserCompanion", "tests");

for (const browser of ["Chromium", "Firefox"]) {
    test(`${browser} extension package uses personal-domain metadata`, async (t) => {
        await mkdir(artifactDirectory, { recursive: true });
        const temporary = await mkdtemp(path.join(artifactDirectory, "manifest-test-"));
        t.after(async () => {
            const relative = path.relative(artifactDirectory, temporary);
            assert.ok(relative && !relative.startsWith("..") && !path.isAbsolute(relative));
            await rm(temporary, { recursive: true, force: true });
        });
        const output = path.join(temporary, browser);
        execFileSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", path.join(repoRoot, "eng", "Build-BrowserExtension.ps1"), "-Browser", browser, "-OutputDirectory", output],
        { windowsHide: true, encoding: "utf8" });
        const manifest = JSON.parse(await readFile(path.join(output, "manifest.json"), "utf8"));
        assert.equal(manifest.homepage_url, "https://mediacontrols.jiripolasek.com/companions/");
        if (browser === "Firefox") {
            const id = manifest.browser_specific_settings.gecko.id;
            assert.equal(id, "media-controls-companion@jiripolasek.com");
            const setup = JSON.parse(await readFile(path.join(repoRoot, "src", "BrowserCompanion", "setup", "packaged-integration.json"), "utf8"));
            assert.deepEqual(setup.firefoxExtensionIds, [id]);
            assert.deepEqual(manifest.background.scripts, ["background.js"]);
        } else {
            assert.equal(manifest.browser_specific_settings, undefined);
            assert.equal(manifest.background.service_worker, "background.js");
        }
    });
}
