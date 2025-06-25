// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using Windows.ApplicationModel;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal sealed record DesktopAppInfo(string DisplayName, string Path, string AppId, string? IconPath) : IAppInfo;

internal sealed record ModernAppInfo(string DisplayName, string AppId, string? IconPath, AppInfo AppInfo) : IAppInfo
{
    public ModernAppInfo(AppInfo appInfo, string? iconPath) : this(appInfo.DisplayInfo?.DisplayName ?? "", appInfo.AppUserModelId!, iconPath, appInfo)
    {
    }
}

internal sealed record EmptyAppInfo : IAppInfo
{
    public static EmptyAppInfo Instance { get; } = new();

    public string DisplayName => string.Empty;
    public string AppId => string.Empty;
    public string? IconPath => null;
};

internal interface IAppInfo {
    string DisplayName { get; }
    string AppId { get; }
    string? IconPath { get; }
}