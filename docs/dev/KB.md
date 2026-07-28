# Developer Knowledge Base

## WAP Native AOT publish fails with `NETSDK1004`

### Symptom

Building or deploying `JPSoftworks.MediaControlsExtension.Package.wapproj` with
package generation enabled can fail with an error similar to:

```text
error NETSDK1004: Assets file
'...\src\MediaControlsExtension\obj\wappublish\win-x64\project.assets.json'
not found. Run a NuGet package restore to generate this file.
```

### Cause

The DesktopBridge packaging project invokes `Publish` on the referenced
application project with its intermediate output redirected to
`obj\wappublish\<rid>\`. This keeps packaging state separate from an ordinary
build's `obj\` directory.

A normal solution restore, `dotnet restore`, or MSBuild `/restore` restores the
static project graph into the ordinary `obj\` directory. It does not reproduce
the dynamically redirected properties used later by the WAP publish step, so
the redirected `project.assets.json` can be missing. This is the same failure
pattern reported in [Windows App SDK issue #1015](https://github.com/microsoft/WindowsAppSDK/issues/1015).

### Fix

From the repository root, restore the application project once using the same
RID, Native AOT settings, and intermediate path as the WAP publish invocation.

For x64:

```powershell
dotnet restore .\src\MediaControlsExtension\JPSoftworks.MediaControlsExtension.csproj `
  -p:RuntimeIdentifier=win-x64 `
  -p:SelfContained=true `
  -p:PublishAot=true `
  -p:BaseIntermediateOutputPath=obj\wappublish\win-x64\
```

For ARM64:

```powershell
dotnet restore .\src\MediaControlsExtension\JPSoftworks.MediaControlsExtension.csproj `
  -p:RuntimeIdentifier=win-arm64 `
  -p:SelfContained=true `
  -p:PublishAot=true `
  -p:BaseIntermediateOutputPath=obj\wappublish\win-arm64\
```

Then build or deploy the WAP normally. With
`GenerateAppxPackageOnBuild=true`, its build invokes the application publish
and generates the MSIX/deployment recipe.

Repeat the redirected restore after deleting `obj`, when switching RID, or when
NuGet dependency changes make the redirected assets file stale.

## Automated build and deployment

For the reusable WAP layout, migration checklist, new-project setup, templates,
and convention checks, see [Command Palette WAP Packaging Guide](WapPackaging.md).

The repository script performs the redirected restore, builds the WAP directly
with Visual Studio MSBuild, unpacks its generated unsigned MSIX, and registers
the unpacked development package:

```powershell
.\eng\Deploy-Package.ps1
```

It defaults to a managed, untrimmed `Release|x64` deployment. Add `-Aot` to
enable Native AOT and trimming together:

```powershell
.\eng\Deploy-Package.ps1 -Aot
```

Select another supported configuration or platform with parameters:

```powershell
.\eng\Deploy-Package.ps1 -Configuration Debug -Platform ARM64 -Aot
```

The script uses the WAP property `GenerateAppxPackageOnBuild=true` directly;
`GeneratePackageLocally` is a single-project packaging convention and is not
used here. The script deliberately avoids `devenv.com /Deploy`, because that
path loads the full `.slnx` and can fail in Visual Studio's automatic
solution-level NuGet restore before the WAP build begins.

The solution-loader failure can appear before any project build output:

```text
NuGet package restore failed.
The operation failed as details for project
JPSoftworks.MediaControlsExtension.Media could not be loaded.
```

Because the development MSIX is unsigned, deployment registers an unpacked
package layout in Visual Studio's standard
`bin\<Platform>\<Configuration>\AppX` directory. Any existing registration for
the same package identity is removed with `-PreserveApplicationData` before
the new layout is registered. The directory is replaced through a staged swap,
so the previous layout and registration can be restored if registration fails.

### Reusing the scripts

All repository and application-specific values are stored in:

```text
eng\Package.config.psd1
```

The configuration defines the repository-relative app project, WAP project,
package manifest, artifacts directory, packaged executable, defaults, and
platform-to-RID mappings. It also defines the host process and its launch and
reload URIs for `Test-Package.ps1`. To reuse the scripts in another repository,
copy the scripts and edit only this data file. Pass an alternative
configuration with `-ConfigPath` when needed.

### Deploying and refreshing the host

Build and deploy the package, then ask Command Palette to reload extensions:

```powershell
.\eng\Test-Package.ps1 -AfterDeploy Reload
```

`Reload` is the default. To restart Command Palette after deployment instead:

```powershell
.\eng\Test-Package.ps1 -AfterDeploy Restart
```

The test script accepts the same `-Configuration`, `-Platform`, `-Aot`, and
`-VisualStudioPath` build options as `Deploy-Package.ps1`. Restart preserves
the active Command Palette channel by relaunching the executable of the
running host; if Command Palette was not running, it falls back to the launch
URI from `Package.config.psd1`.

### Uninstalling the development package

Uninstall the configured package for the current user:

```powershell
.\eng\Uninstall-Package.ps1
```

By default this also removes the package's application data. Preserve the data
for a later deployment with:

```powershell
.\eng\Uninstall-Package.ps1 -PreserveApplicationData
```

The uninstall script supports `-WhatIf` and removes only package registration;
it does not delete the generated `AppX` layout.
