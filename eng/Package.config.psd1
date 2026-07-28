@{
    RepositoryRoot = '..'

    AppProjectPath      = 'src\MediaControlsExtension\JPSoftworks.MediaControlsExtension.csproj'
    WapProjectPath      = 'src\MediaControlsExtension.Package\JPSoftworks.MediaControlsExtension.Package.wapproj'
    PackageManifestPath = 'src\MediaControlsExtension.Package\Package.appxmanifest'
    ArtifactsPath       = 'artifacts'

    PackageExecutablePath = 'JPSoftworks.MediaControlsExtension\JPSoftworks.MediaControlsExtension.exe'

    DefaultConfiguration = 'Release'
    DefaultPlatform      = 'x64'

    RuntimeIdentifiers = @{
        x64   = 'win-x64'
        ARM64 = 'win-arm64'
    }

    Host = @{
        ProcessName = 'Microsoft.CmdPal.UI'
        LaunchUri   = 'x-cmdpal://'
        ReloadUri   = 'x-cmdpal://reload'
    }
}
