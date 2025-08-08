// ------------------------------------------------------------
// 
// Copyright (c) Jiří Polášek. All rights reserved.
// 
// ------------------------------------------------------------

using System.Text;

namespace JPSoftworks.MediaControlsExtension.Helpers;

internal static class StringBuilderExtensions
{
    public static void AppendWhenNotEmpty(this StringBuilder stringBuilder, string separator, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (stringBuilder.Length > 0)
            {
                stringBuilder.Append(separator);
            }
            stringBuilder.Append(value);
        }
    }
}

internal static class StringHelper
{
    public static string JoinNonEmpty(string separator, params IEnumerable<string?> values)
    {
        var nonEmptyValues = values.Where(v => !string.IsNullOrWhiteSpace(v));
        return string.Join(separator, nonEmptyValues);
    }
}