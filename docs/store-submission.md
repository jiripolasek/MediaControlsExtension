# Website and shared privacy paperwork for store submission

The extension packages, listing text, reviewer notes, and artwork are prepared in
`X:\source\_MediaControlsExtension.Private\browser companion`. This website supplies
the public product, setup, support, and privacy pages for those listings.
The shared policy also covers Media Controls for Command Palette and the website.
Desktop privacy paperwork is exported to the private repository's `store` folder.

## Listing URLs

The default Astro configuration produces these URLs:

| Field | URL |
| --- | --- |
| Homepage | https://jiripolasek.github.io/MediaControlsExtension/companions/ |
| Setup | https://jiripolasek.github.io/MediaControlsExtension/companions/#setup |
| Support | https://jiripolasek.github.io/MediaControlsExtension/support/ |
| Shared privacy policy | https://jiripolasek.github.io/MediaControlsExtension/privacy/ |

These are intended deployment destinations, not a claim that they are live.
Publish and verify them before submitting a listing. The pages require no account
and the privacy policy is linked from every page's footer.
The desktop Store listing uses the same privacy and support URLs, with the site
root as its homepage. Its existing product page is
https://apps.microsoft.com/detail/9N3BQ81G19K7.

## Keep the private kit aligned

The website and store policy copies share `src/content/privacy.md`. After changing
the policy or final host, refresh the prepared kit:

```powershell
npm run prepare:stores -- --kit 'X:\source\_MediaControlsExtension.Private\browser companion' --desktop-store 'X:\source\_MediaControlsExtension.Private\store'
```

The command reads the current `SITE_URL` and `BASE_PATH` configuration. You can
instead pass the complete final site root, including its base path:

```powershell
npm run prepare:stores -- --kit 'X:\source\_MediaControlsExtension.Private\browser companion' --desktop-store 'X:\source\_MediaControlsExtension.Private\store' --site-url 'https://jiripolasek.com/media-controls/'
```

It updates the three listing JSON files, descriptions, reviewer links, policy
copies, and `WEBSITE-READINESS.md`. It preserves existing line endings and does not
rebuild or modify any extension archive or visual asset. A changed policy or privacy
URL resets its public-verification flag. Publication status and store IDs remain
unchanged. Only kits marked `prepared-locally` are accepted.
The optional `--desktop-store` directory must already exist. It receives the same
`privacy-policy.md` and a `PRIVACY-SUBMISSION.md` handoff for Partner Center. Those
are local preparation documents; no live dashboard or app package is changed.

The policy names all three components and separates their data access, storage,
connections, diagnostics, and controls. Browser permission and data-type answers
still describe only the browser companion. The desktop's optional VLC password
and remote connection are not added to browser permission disclosures.

Add `--check` to verify that those files already match without changing them.
Run `npm run check`, `npm run build`, and the private kit's `Validate-Kit.py` after
editing submission content.

## What remains outside this website

The Edge companion is published on Microsoft Edge Add-ons, and its installation
link is configured in `src/data/site.ts`.

- Publish the website and verify all four URLs without signing in. Keep
  `publicPrivacyUrlVerified` false until then.
- Provide a desktop release containing the browser bridge and set
  `desktopReleaseForReviewUrl` in all three listings.
- Obtain the Chrome store installation link, add it to `src/data/site.ts`, and
  update the preview copy when it is published. Confirm that published store IDs
  are included in the shipped desktop application's allowlist.
- Complete Firefox review and signing, then test the signed add-on.
- Test each final store-installed companion with the final desktop package.
- Complete the publisher account, store dashboard disclosures, and submission.

No cookie banner is used. The website has no analytics or tracking; theme
preference is stored only in the browser's local storage.

## Policy source checks

The shared policy was checked against the application source on 29 October 2026.
This is a source review, not a claim of live release acceptance.

| Area | Source checked | Disclosure |
| --- | --- | --- |
| Windows media and volume | `Media.Gsmtc/GsmtcBackend.cs`, `Services/SystemVolumeService.cs` | Player metadata, application identity, and output volume/mute controls. |
| iTunes | `Media.ITunes/ITunesBackend.cs`, `docs/user/itunes-backend.md` | Local automation, current artwork export, and temporary-file cleanup. |
| VLC | `Media.Vlc/VlcBackend.cs`, `Helpers/VlcSettings.cs`, `docs/user/vlc-backend.md` | User-selected local/remote server, HTTP Basic authentication, endpoint-specific Credential Locker storage, and HTTP/HTTPS limits. |
| Settings | `Helpers/SettingsStore.cs` | Persisted preferences and recovery backups; no VLC password in settings JSON. |
| Diagnostics | `Program.cs`, `Services/DetailedLoggingMode.cs`, `MediaHost/WorkerFileLoggerProvider.cs`, `Services/DiagnosticLogArchiveService.cs` | Normal local logs, optional extra detail, rotation, and a manually saved ZIP with no automatic upload. |
| Browser images | `Media.Browser/BrowserImageLoader.cs`, browser `popup.js` | Image-host requests, anonymous popup loads, in-memory artwork, and favicon disk-cache cleanup. |

Application paths above are abbreviated within its `src` directory; namespace
directories have the `MediaControlsExtension.` prefix where applicable. Windows
Credential Locker can roam credentials with a Microsoft account as described in
[Microsoft's documentation](https://learn.microsoft.com/en-us/windows/apps/develop/security/credential-locker).
The desktop handoff flags the optional unencrypted VLC HTTP transport for Store
security review. Describing a behavior in a policy does not certify its compliance.

## Store guidance checked on 29 October 2026

- [Chrome listing fields](https://developer.chrome.com/docs/webstore/cws-dashboard-listing)
  describe homepage and support URLs.
- [Chrome user data guidance](https://developer.chrome.com/docs/webstore/program-policies/user-data-faq)
  requires a privacy policy for local data handling and a Limited Use disclosure
  on the homepage or one click away. The privacy page includes that disclosure.
- [Edge submission guidance](https://learn.microsoft.com/en-us/microsoft-edge/extensions/publish/publish-extension)
  requires an accessible, current policy describing data collection, use, and
  disclosure when personal information is accessed, transmitted, or collected.
- [Firefox add-on policies](https://extensionworkshop.com/documentation/publish/add-on-policies/)
  cover accurate functionality, data disclosures, and reviewer testing information.
- [Microsoft Store personal-information rules](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies#105-personal-information)
  cover the desktop product's policy, data access, storage, security, and controls.
