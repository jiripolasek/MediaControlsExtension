import { readFile, stat, writeFile } from "node:fs/promises";
import path from "node:path";
import { parseArgs } from "node:util";
import config from "../astro.config.mjs";
import { site } from "../src/data/site.ts";

const { values } = parseArgs({
  options: {
    kit: { type: "string" },
    "desktop-store": { type: "string" },
    "site-url": { type: "string" },
    check: { type: "boolean", default: false },
  },
});

if (!values.kit) throw new Error("Pass --kit with the existing browser companion submission kit directory.");
const kit = path.resolve(values.kit);
const desktopStore = values["desktop-store"] ? path.resolve(values["desktop-store"]) : null;
if (desktopStore && !(await stat(desktopStore)).isDirectory()) {
  throw new Error("Pass an existing desktop Store paperwork directory.");
}
const base = `${config.base.replace(/\/*$/, "")}/`;
const root = new URL(values["site-url"] ?? new URL(base, config.site).href);
if (root.protocol !== "https:" || root.username || root.password || root.search || root.hash) {
  throw new Error("Use a public HTTPS site URL without credentials, a query, or a fragment.");
}
if (!root.pathname.endsWith("/")) root.pathname += "/";
const urls = {
  homepage: new URL("companions/", root).href,
  setup: new URL("companions/#setup", root).href,
  support: new URL("support/", root).href,
  privacy: new URL("privacy/", root).href,
};
const normalize = (value) => value.replace(/\r\n?/g, "\n").trimEnd() + "\n";
const plans = [];
async function plan(relative, content, directory = kit) {
  const target = path.join(directory, relative);
  let original = null;
  try {
    original = await readFile(target, "utf8");
  } catch (error) {
    if (error.code !== "ENOENT") throw error;
  }
  const newline = original?.includes("\r\n") || original === null ? "\r\n" : "\n";
  const updated = normalize(content).replaceAll("\n", newline);
  if (original !== updated) plans.push({ target, relative, updated });
}
function setLink(content, label, url) {
  const expression = new RegExp(`^${label}:.*$`, "m");
  return expression.test(content)
    ? content.replace(expression, `${label}: ${url}`)
    : `${content.trimEnd()}\n${label}: ${url}\n`;
}
const source = await readFile(new URL("../src/content/privacy.md", import.meta.url), "utf8");
const policyTitle = "Media Controls privacy policy";
const policy = `# ${policyTitle}\n\n` + source.replace(
  /\]\((\.\.\/[^)]+)\)/g,
  (_, relative) => `](${new URL(relative, urls.privacy).href})`,
);
let previousPolicy = null;
try {
  previousPolicy = await readFile(path.join(kit, "stores/shared/privacy-policy.md"), "utf8");
} catch (error) {
  if (error.code !== "ENOENT") throw error;
}
const policyChanged = previousPolicy === null || normalize(previousPolicy) !== normalize(policy);

for (const browser of ["chrome", "edge", "firefox"]) {
  const directory = path.join("stores", browser);
  const listing = JSON.parse(await readFile(path.join(kit, directory, "listing.json"), "utf8"));
  if (listing.name !== "Media Controls Companion" || listing.publicationStatus !== "prepared-locally") {
    throw new Error(`Expected an unpublished Media Controls Companion kit: ${browser}.`);
  }
  const expectedDescription = browser === "firefox" ? "description.md" : "description.txt";
  if (listing.descriptionFile !== expectedDescription) throw new Error(`Unexpected description file: ${browser}.`);
  if (policyChanged || listing.privacyPolicyUrl !== urls.privacy) listing.publicPrivacyUrlVerified = false;
  listing.homepageUrl = urls.homepage;
  listing.supportUrl = urls.support;
  listing.privacyPolicyUrl = urls.privacy;
  listing.privacyPolicyScope = "Shared Media Controls policy: Command Palette extension, browser companions, and website.";
  listing.setupUrl = urls.setup;
  await plan(path.join(directory, "listing.json"), JSON.stringify(listing, null, 2));

  const descriptionPath = path.join(directory, listing.descriptionFile);
  let description = normalize(await readFile(path.join(kit, descriptionPath), "utf8"));
  description = description.replace(/^Setup(?: and help)?:.*$/m, `Setup and help: ${urls.setup}`);
  description = setLink(description, "Privacy", urls.privacy);
  description = setLink(description, "Support", urls.support);
  await plan(descriptionPath, description);
}

for (const browser of ["shared", "chrome", "edge", "firefox"]) {
  const relative = path.join("stores", browser, "reviewer-notes.txt");
  let notes = normalize(await readFile(path.join(kit, relative), "utf8"));
  for (const [label, key] of [["Setup", "setup"], ["Privacy", "privacy"], ["Support", "support"]]) {
    notes = setLink(notes, label, urls[key]);
  }
  const scope = "PRIVACY POLICY SCOPE\n" +
    "The shared Media Controls privacy policy covers the Command Palette extension, browser companions, and website. Browser-store data-type and permission responses describe this browser companion only. VLC connections and saved VLC passwords belong to the optional desktop source; the browser extension does not request or use those credentials.\n" +
    "The desktop application writes local operational logs. Detailed logging and manually exported diagnostic ZIPs are also local and are not automatically uploaded. The shared policy describes storage, artwork requests, credentials, and removal for each component.\n";
  if (notes.includes("\nPRIVACY POLICY SCOPE\n")) {
    notes = notes.replace(/PRIVACY POLICY SCOPE\n[\s\S]*?(?=\nREVIEW CONTEXT\n)/, scope);
  } else {
    if (!notes.includes("\nREVIEW CONTEXT\n")) throw new Error(`Missing reviewer context: ${browser}.`);
    notes = notes.replace("\nREVIEW CONTEXT\n", `\n${scope}\nREVIEW CONTEXT\n`);
  }
  notes = notes.replace("No authentication, subscription, paid service,", "No companion account, subscription, paid service,");
  await plan(relative, notes);
}
const practicesPath = "stores/shared/privacy-practices.json";
const practices = JSON.parse(await readFile(path.join(kit, practicesPath), "utf8"));
practices.privacyPolicy = {
  title: policyTitle,
  url: urls.privacy,
  covers: ["Command Palette extension", "Browser companions", "Website"],
  responseScope: "These store permission and data-type responses describe the browser companion. Optional VLC transport and credentials are desktop features, not browser extension permissions or credential collection.",
};
await plan(practicesPath, JSON.stringify(practices, null, 2));
await plan("stores/shared/privacy-policy.md", policy);
await plan("stores/firefox/privacy-policy.md", policy.replace(/^#{1,6} (.+)$/gm, "**$1**"));
if (desktopStore) {
  await plan("privacy-policy.md", policy, desktopStore);
  await plan("PRIVACY-SUBMISSION.md", `# Media Controls desktop Store privacy handoff

Prepared locally from the shared Media Controls policy. This is a copy-ready handoff, not a dashboard upload or a submission confirmation.

## Listing links

| Field | Value |
| --- | --- |
| Product | Media Controls for Command Palette |
| Store listing | ${site.store} |
| Publisher | Jiri Polasek |
| Website | ${root.href} |
| Support | ${urls.support} |
| Privacy policy | ${urls.privacy} |

Use the same privacy URL as the browser listings. The policy explicitly covers the desktop extension, browser companions, and website. The policy copy is exported from src/content/privacy.md in the website checkout.

## Data-handling summary for review

- The desktop extension accesses player-provided media metadata, playback state, artwork, application identity, and Windows output volume/mute state for Command Palette and Dock controls.
- Windows media and iTunes integrations use local APIs. Local worker processes are part of the desktop package.
- Optional VLC connects to the user-configured local or remote server with HTTP Basic authentication. The password is kept by server URL in Windows Credential Locker, not settings.json. Windows can roam Locker credentials with the user's Microsoft account.
- Local settings persist across updates. Artwork is handled in memory, with temporary iTunes export files and a browser favicon disk cache subject to cleanup limitations.
- Operational/error logs are written locally during normal use. Additional detailed logging can be enabled. Saving diagnostic logs creates a local ZIP, with no automatic upload.
- Browser companions handle current tab/media details and share them with the local bridge. They do not access the desktop VLC password. Browser-store permissions and consent declarations remain specific to the browser package.
- The website uses no cookies, tracking, or analytics; an explicit theme choice is stored in browser local storage. Hosting request data and third-party service practices are disclosed.
- No project account, analytics, advertising, cloud media catalog, or sale of user data is involved. Voluntary support submissions are separate from automatic product data handling.

Do not answer that the desktop product accesses no user information merely because processing is local. Review each actual Partner Center question against the submitted build and this policy.

## Before using these fields

- [ ] Publish and open the privacy, website, and support URLs without signing in.
- [ ] Confirm that the desktop build's sources, settings, logging, credential storage, and cleanup behavior match the policy.
- [ ] Confirm how to remove separately retained VLC credentials, logs, exported ZIPs, and leftover temporary files; do not claim uninstall deletes all of them.
- [ ] Review optional unencrypted VLC HTTP credential transport against Microsoft Store security requirements before certifying compliance. The policy documents it; it does not turn HTTP into an encrypted connection.
- [ ] Enter the shared privacy policy URL in Partner Center and check the product's privacy link when preparing its next release.
- [ ] Recheck any required translations and the current dashboard's privacy answers before submission.

References:
- [Microsoft Store personal-information requirements](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies#105-personal-information)
- [Windows Credential Locker](https://learn.microsoft.com/en-us/windows/apps/develop/security/credential-locker)

No package, account, live Store listing, or publication state was changed by preparing this paperwork.
`, desktopStore);
}
await plan("WEBSITE-READINESS.md", `# Website handoff for store submission

These URLs target the prepared Astro website. They have not been verified as public.

| Listing field | URL |
| --- | --- |
| Homepage | ${urls.homepage} |
| Setup | ${urls.setup} |
| Support | ${urls.support} |
| Privacy policy | ${urls.privacy} |

Before submission:

- [ ] Publish the website at these URLs, or regenerate this handoff for its final host.
- [ ] Open each URL without signing in and verify its content and links.
- [ ] Set publicPrivacyUrlVerified to true in each listing only after that check.
- [ ] Supply a compatible desktop release in desktopReleaseForReviewUrl.
- [ ] Record Chrome/Edge store IDs and installation URLs; update the desktop allowlist.
- [ ] Complete Firefox signing and test the signed add-on with the final desktop package.
- [ ] Recheck store privacy disclosures against the submitted extension's behavior.

The shared Media Controls policy covers the Command Palette extension, browser companions, and website. Its copies come from src/content/privacy.md in the website checkout. Browser dashboard data-type and permission answers remain scoped to the browser companion.
Use --desktop-store with the existing desktop Store paperwork directory to export the same policy and its desktop privacy handoff.
Extension ZIPs, source archives, package checksums, and visual assets are unchanged.
`);
let readme = await readFile(path.join(kit, "README.md"), "utf8");
if (!readme.includes("WEBSITE-READINESS.md")) {
  readme = `${readme.trimEnd()}\n\n## Website handoff\n\nSee WEBSITE-READINESS.md for the prepared homepage, setup, support, and privacy URLs and the remaining public checks.\n`;
}
const scopeNote = "The shared privacy policy covers the Command Palette extension, browser companions, and website. Browser-store privacy responses remain scoped to the browser package.";
if (!readme.includes(scopeNote)) readme = `${readme.trimEnd()}\n\n${scopeNote}\n`;
await plan("README.md", readme);

if (values.check && plans.length) {
  throw new Error(`Store website material needs refreshing: ${plans.map((item) => item.relative).join(", ")}`);
}
for (const item of plans) await writeFile(item.target, item.updated, "utf8");
console.log(`${values.check ? "Checked" : "Updated"} ${plans.length} files. Extension packages are unchanged.`);
console.log(JSON.stringify(urls, null, 2));
console.log("Public availability and a compatible desktop release still need verification before submission.");
