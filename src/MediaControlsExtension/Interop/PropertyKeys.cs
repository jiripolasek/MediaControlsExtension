using System.Diagnostics.CodeAnalysis;

namespace JPSoftworks.MediaControlsExtension.Interop;

[SuppressMessage("ReSharper", "InconsistentNaming")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "<Pending>")]
internal static class PropertyKeys
{
    internal static PROPERTYKEY PKEY_ItemNameDisplay = PROPERTYKEY.FromString("{B725F130-47EF-101A-A5F1-02608C9EEBAC}", 10);
    internal static PROPERTYKEY PKEY_Link_TargetParsingPath = PROPERTYKEY.FromString("{B9B4B3FC-2B51-4A42-B5D8-324146AFCF25}", 2);
}