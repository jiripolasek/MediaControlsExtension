# Command Palette WAP Packaging Guide

This guide defines the repository conventions and validation steps for:

- migrating an existing Command Palette extension from single-project MSIX
  packaging to a Windows Application Packaging Project (WAP); and
- adding WAP packaging to a new Command Palette extension.

The packaging project is the packaged startup and deployment project. The
application project remains an ordinary SDK-style executable project that can
be built independently.

## Canonical layout

Use this layout unless the repository already has a strong reason to differ:

```text
Directory.Build.props
Directory.Packages.props
eng/
  CmdPal.Extension.props
  Deploy-Package.ps1
  Initialize-WapProject.ps1
  Package.config.psd1
  Test-Package.ps1
  Test-WapProject.ps1
  Uninstall-Package.ps1
  templates/wap/
src/
  <Extension>/
    <Extension>.csproj
    Assets/                      # runtime assets only
    Properties/PublishProfiles/
  <Extension>.Package/
    <Extension>.Package.wapproj
    Assets/                      # manifest/package assets
    Strings/                     # package PRI resources
    Package.appxmanifest
    Package.StoreAssociation.xml   # optional and normally ignored
```

`eng/` is the single well-known home for repository automation. Keep scripts
that contributors or agents invoke directly at its root; nest only supporting
assets such as templates.

`eng/CmdPal.Extension.props` is the repository-specific contract shared by the
application, WAP, and automation. It uses `CmdPal*` property names so importing
it does not accidentally configure test or library projects.

`Package.appxmanifest` remains extension-specific. Do not generate capabilities,
package identity, Store identity, display resources, or the provider CLSID from
generic defaults during a migration.

Package assets and PRI strings belong beside `Package.appxmanifest`. Application
assets belong with the executable only when runtime code consumes them. Do not
link package resources across the application/WAP project boundary.

After that split, copy the application-owned runtime asset tree with one rule:

```xml
<None Update="Assets\**\*">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
</None>
```

Remove unused files before adopting the wildcard so migration does not turn
source-only leftovers into packaged payload.

## Shared property contract

Import the generated contract from `Directory.Build.props`:

```xml
<Import
  Project="$(MSBuildThisFileDirectory)eng\CmdPal.Extension.props"
  Condition="'$(CmdPalExtensionProject)' == '' and Exists('$(MSBuildThisFileDirectory)eng\CmdPal.Extension.props')" />
```

The contract defines:

- `CmdPalExtensionProject`
- `CmdPalExtensionProjectReference`
- `CmdPalExtensionAssemblyName`
- `CmdPalPackageProject`
- `CmdPalPackageManifest`
- `CmdPalPackageExecutable`
- `CmdPalPackageProjectGuid`
- `CmdPalTargetFramework`
- `CmdPalTargetPlatformVersion`
- `CmdPalTargetPlatformMinVersion`
- `CmdPalRuntimeIdentifiers`
- `CmdPalDefaultLanguage`
- `CmdPalArtifactsPath`
- `CmdPalSupportsNativeAot`

`CmdPalExtensionProject` is the repository-rooted path used by automation.
`CmdPalExtensionProjectReference` is the path from the WAP directory to the
same project. Keep the latter relative: Visual Studio Publish matches it to the
WAP `ProjectReference` item identity.

Put settings shared by all C# projects in `Directory.Build.props`. Guard them
from the WAP because it is not an SDK-style C# project:

```xml
<PropertyGroup Condition="'$(MSBuildProjectExtension)' == '.csproj'">
  <TargetFramework>$(CmdPalTargetFramework)</TargetFramework>
  <TargetPlatformMinVersion>$(CmdPalTargetPlatformMinVersion)</TargetPlatformMinVersion>
  <SupportedOSPlatformVersion>$(CmdPalTargetPlatformMinVersion)</SupportedOSPlatformVersion>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <LangVersion>preview</LangVersion>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
</PropertyGroup>
```

The application project maps only application-specific contract values:

```xml
<AssemblyName>$(CmdPalExtensionAssemblyName)</AssemblyName>
<RuntimeIdentifiers>$(CmdPalRuntimeIdentifiers)</RuntimeIdentifiers>
<PackageManifestPath>$(CmdPalPackageManifest)</PackageManifestPath>
```

The WAP maps the same contract to DesktopBridge properties and its entry-point
`ProjectReference`.

Omit `RootNamespace` and `AssemblyName` when the SDK defaults derived from the
project filename are correct. `IsAotCompatible` already makes `IsTrimmable`
true; declare `IsTrimmable` separately only when the project needs a different
policy. Keep `RuntimeIdentifiers` in every project that the WAP restores for a
RID-specific publish, including transitive project references. Keep
`AllowUnsafeBlocks` only where source or generated code requires it.

## Prerequisites

- Visual Studio with MSBuild and the DesktopBridge/MSIX Packaging Tools
  component.
- A Windows SDK containing `makeappx.exe`.
- A `Microsoft.Windows.SDK.BuildTools` version, preferably declared in
  `Directory.Packages.props` when central package management is enabled.
- x64 and ARM64 publish profiles under
  `Properties/PublishProfiles/win-<platform>.pubxml`.
- A unique provider class GUID already used by both the C# provider class and
  the package manifest.
- A package manifest whose identity, publisher, version, capabilities, display
  resources, and assets are understood before migration.

The checked-in WAP template expects self-contained application publish
profiles. A framework-dependent package is a separate deployment policy: add
the required Windows App SDK build reference to the WAP and validate the
framework package dependency in the generated manifest.

For deterministic WAP builds, `PublishSingleFile` must be `false`. Native AOT
and trimming are an explicit opt-in and must not be inferred merely because a
project has trim analyzers enabled.

## Migrate an existing single-project package

### 1. Record the existing contract

Before changing files, record:

- [ ] Application project path and `AssemblyName`.
- [ ] Target framework, target platform version, and minimum platform version.
- [ ] Supported platforms and RIDs.
- [ ] Package identity, publisher, and version.
- [ ] Provider CLSID from the C# `[Guid]` attribute.
- [ ] Both manifest copies of the CLSID.
- [ ] `com:ExeServer` executable and arguments.
- [ ] CmdPal supported interfaces.
- [ ] All capabilities and restricted capabilities.
- [ ] Every manifest-referenced asset.
- [ ] Store association and signing behavior.
- [ ] Whether Native AOT is currently proven to work.
- [ ] Runtime files under `Assets` or other content directories.
- [ ] Package-only `Assets` and `Strings/**/Resources.resw` files.

Do not combine the packaging migration with a target-framework upgrade,
dependency cleanup, capability change, localization rewrite, or AOT enablement.

### 2. Scaffold the WAP files

From the repository root:

```powershell
.\eng\Initialize-WapProject.ps1 `
  -AppProjectPath .\src\ExampleExtension\ExampleExtension.csproj
```

The script:

- reads the application project;
- copies the existing `Package.appxmanifest`;
- copies manifest-referenced assets and package PRI strings beside the WAP;
- updates the copied `com:ExeServer` path for the WAP payload layout;
- creates `eng/CmdPal.Extension.props`;
- creates the WAP from the checked-in template; and
- refuses to overwrite existing output.

Use `-WhatIf` to preview paths. Use `-SupportsNativeAot` only when the extension
already has a validated Native AOT path.

### 3. Review the generated package manifest

- [ ] Identity, publisher, and version are unchanged.
- [ ] Display names and `ms-resource` references are unchanged.
- [ ] All original capabilities are present.
- [ ] `com:Class/@Id`, `CreateInstance/@ClassId`, and the C# `[Guid]` match.
- [ ] `com:ExeServer/@Executable` is
  `<AssemblyName>\<AssemblyName>.exe`.
- [ ] `com:ExeServer/@Arguments` remains
  `-RegisterProcessAsComServer`.
- [ ] Supported CmdPal interfaces are unchanged.
- [ ] Visual assets still use the intended filenames and qualifiers.
- [ ] Manifest assets are under the WAP-local `Assets` directory.
- [ ] PRI resources remain configured when the manifest uses `ms-resource`.
- [ ] Package PRI resources are under the WAP-local `Strings` directory.

The generated `<Application Executable="$targetnametoken$.exe">` value is
resolved by the WAP. The explicit COM server executable must resolve to the
same packaged executable.

### 4. Import and consume the shared contract

- [ ] Import `eng/CmdPal.Extension.props` from `Directory.Build.props`.
- [ ] Put common C# framework, platform, nullable, language-version, and warning
  policy in a `.csproj`-conditioned `Directory.Build.props` property group.
- [ ] Map assembly values in the application and RID values in every
  RID-published project to the corresponding `CmdPal*` properties.
- [ ] Remove `RootNamespace` and `AssemblyName` declarations that only repeat
  SDK defaults.
- [ ] Do not repeat `IsTrimmable` when `IsAotCompatible` already enables it.
- [ ] Point any manifest-version synchronization target at
  `$(CmdPalPackageManifest)`.
- [ ] Keep application-only AOT, trimming, COM, and analyzer settings in the
  application project.
- [ ] Keep WAP-only packaging, bundle, signing, and artifact settings in the
  WAP.

`Directory.Build.props` is imported early. Store neutral values there or in the
imported contract; do not condition them on a marker that is declared later in
the application project.

### 5. Remove single-project MSIX ownership

Remove these from the application project after the WAP is wired:

- [ ] `AppxManifest`
- [ ] `EnableMsixTooling`
- [ ] `ProjectCapability Include="Msix"`
- [ ] `HasPackageAndPublishMenu`
- [ ] WAP-owned `Appx*` packaging and signing properties
- [ ] package-generation properties such as `GenerateAppInstallerFile`
- [ ] package-logo `Content` items that now belong to the WAP

After reviewing the copied resources, remove package-only assets and PRI strings
from the application directory. Do not remove runtime assets merely because
they live under `Assets`; files used by extension code must still copy to
publish output. A file should exist in both projects only when it has two
verified consumers.

After updating the application project:

- [ ] Remove the old application-directory `Package.appxmanifest`.
- [ ] Leave `app.manifest` in the application project.
- [ ] Keep publish profiles in the application project.

### 6. Add the WAP to the solution

For `.slnx`, the project needs the WAP project type and deploy mapping:

```xml
<Project
  Path="src/ExampleExtension.Package/ExampleExtension.Package.wapproj"
  Type="c7167f0d-bc9f-4e6e-afe1-012c56b48db5">
  <Platform Solution="*|ARM64" Project="ARM64" />
  <Platform Solution="*|x64" Project="x64" />
  <Deploy />
</Project>
```

For `.sln`, add the WAP through Visual Studio and verify that both x64 and ARM64
have `Build.0` and `Deploy.0` configuration mappings.

- [ ] WAP is the packaged startup/deployment project.
- [ ] Application and WAP use the same platform for each solution
  configuration.
- [ ] Test and library projects are not marked for deployment.

### 7. Update repository automation

- [ ] Point `Package.config.psd1` at the new application project, WAP, manifest,
  artifacts directory, and packaged executable.
- [ ] Keep `Deploy-Package.ps1`, `Uninstall-Package.ps1`, and
  `Test-Package.ps1` generic.
- [ ] Default to managed and untrimmed deployment.
- [ ] Reject `-Aot` when `CmdPalSupportsNativeAot` is false.
- [ ] Keep signing disabled for local unpacked registration.
- [ ] Inject signing only in the release workflow.

Eventually, automation should evaluate the WAP/shared contract instead of
duplicating those paths in a PowerShell data file.

### 8. Run static validation

```powershell
.\eng\Test-WapProject.ps1
```

For agent-readable output:

```powershell
.\eng\Test-WapProject.ps1 -OutputFormat Json
```

Resolve every failure before building. Warnings require an explicit review.

### 9. Build and inspect the package

- [ ] Build the application project independently.
- [ ] Confirm both publish profiles use the intended self-contained or
  framework-dependent deployment mode.
- [ ] Perform the redirected restore required by the WAP publish.
- [ ] Build `Release|x64` with Visual Studio MSBuild.
- [ ] Build `Release|ARM64`.
- [ ] Confirm the MSIX is produced under the configured artifacts directory.
- [ ] Unpack the MSIX.
- [ ] Parse the generated `AppxManifest.xml`.
- [ ] Confirm the package identity and capabilities.
- [ ] Confirm `Application/@Executable` equals
  `com:ExeServer/@Executable`.
- [ ] Confirm the configured executable exists in the payload.
- [ ] Confirm runtime assets and PRI resources exist.
- [ ] For managed output, confirm the expected managed runtime files exist.
- [ ] For Native AOT output, confirm `coreclr.dll`, `hostfxr.dll`, and the
  application `.deps.json` are absent.

### 10. Register and activate

- [ ] Register the unpacked development package.
- [ ] Confirm package status is `Ok`.
- [ ] Reload Command Palette.
- [ ] Confirm the extension is discovered.
- [ ] Activate the provider and execute a representative command.
- [ ] Restart Command Palette and repeat activation.
- [ ] Verify uninstall with and without preserved application data.

## Set up a new extension

Create the application project and provider class first. Then run:

```powershell
.\eng\Initialize-WapProject.ps1 `
  -AppProjectPath .\src\ExampleExtension\ExampleExtension.csproj `
  -CreateManifest `
  -PackageIdentityName Contoso.ExampleForCommandPalette `
  -Publisher 'CN=00000000-0000-0000-0000-000000000000' `
  -PublisherDisplayName Contoso `
  -DisplayName 'Example for Command Palette' `
  -ProviderClassId 00000000-0000-0000-0000-000000000001
```

Then complete these new-project requirements:

- [ ] Replace every sample identity and GUID with durable production values.
- [ ] Put the same provider GUID on the C# provider class.
- [ ] Add all manifest-required logo assets under the package project's
  `Assets` directory.
- [ ] Add only the capabilities the extension actually needs.
- [ ] Add `ms-resource` values and package-project `Strings/**/Resources.resw`
  resources together, never independently.
- [ ] Reserve/associate the Store identity only after local packaging works.
- [ ] Configure signing outside the committed reusable WAP defaults.
- [ ] Add the WAP to the solution with deployment enabled.
- [ ] Run the static, build, payload, registration, and activation checklists
  above.

## Agent migration rules

An automation agent performing a migration must:

- preserve identity, version, publisher, CLSID, capabilities, supported
  interfaces, and localization unless explicitly instructed otherwise;
- treat the source manifest and provider `[Guid]` as migration inputs rather
  than inventing replacements;
- avoid target-framework, dependency, and AOT changes in the same migration;
- run `Initialize-WapProject.ps1 -WhatIf` before scaffolding;
- run `Test-WapProject.ps1 -OutputFormat Json` before and after edits;
- inspect the generated package manifest and payload, not only the MSBuild exit
  code;
- avoid Store association or certificate material in reusable templates; and
- leave unrelated worktree changes untouched.

## Common failures

### `NETSDK1004` under `obj\wappublish`

Restore the application using the same RID, AOT/trimming values, self-contained
setting, and redirected `BaseIntermediateOutputPath` that the WAP publish will
use. A normal solution restore does not necessarily create this redirected
assets file.

### Extension is installed but not discovered

Check the app-extension name, supported interfaces, provider CLSID, COM class
CLSID, and provider C# `[Guid]`. Then confirm that the generated manifest uses
the same executable for the application and COM server.

### Assets are missing

Manifest assets are WAP-local package content. Runtime assets are application
publish content. Some files may need both roles, but duplication requires two
verified consumers; do not assume moving package-logo ownership also preserves
runtime copying.

### Local build requires a missing certificate

Remove committed certificate thumbprints from reusable configuration. Local
development registration should build unsigned. Release signing should inject
the certificate through the release environment.

### Managed migration unexpectedly publishes Native AOT

Remove unconditional `PublishAot=true` and `PublishTrimmed=true` from the WAP.
Make the deployment command opt in and verify that the extension declares
`CmdPalSupportsNativeAot=true`.

## Microsoft references

- [Set up a desktop application for MSIX packaging in Visual Studio](https://learn.microsoft.com/windows/msix/desktop/desktop-to-uwp-packaging-dot-net)
- [Windows App SDK deployment guide for framework-dependent packaged apps](https://learn.microsoft.com/windows/apps/windows-app-sdk/deploy-packaged-apps)
- [Package and deploy Windows apps overview](https://learn.microsoft.com/windows/apps/package-and-deploy/)
- [App package manifest schema reference](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/schema-root)
