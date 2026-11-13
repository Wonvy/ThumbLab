#nullable enable
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Security;

namespace ThumbLabWinUI;

internal sealed record BrowsePreferences(int LeftWidth, bool IconView, string Extension)
{
    private const string RegistryPath = @"Software\ThumbLab\BrowsePreferences";
    internal const int MinimumLeftWidth = 205;
    private const int DefaultLeftWidth = 315;
    private const string DefaultExtension = ".pdf";

    internal static BrowsePreferences Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            int width = key?.GetValue(nameof(LeftWidth)) is int storedWidth && storedWidth >= MinimumLeftWidth
                ? storedWidth : DefaultLeftWidth;
            bool iconView = key?.GetValue(nameof(IconView)) is 1;
            string extension = DefaultExtension;
            if (key?.GetValue(nameof(Extension)) is string storedExtension)
            {
                try { extension = ThumbnailSettings.Normalize(storedExtension); }
                catch (InvalidOperationException) { }
            }
            return new BrowsePreferences(width, iconView, extension);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Debug.WriteLine(ex);
            return new BrowsePreferences(DefaultLeftWidth, false, DefaultExtension);
        }
    }

    internal void Save()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue(nameof(LeftWidth), LeftWidth, RegistryValueKind.DWord);
            key.SetValue(nameof(IconView), IconView ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(nameof(Extension), Extension, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            Debug.WriteLine(ex);
        }
    }
}
