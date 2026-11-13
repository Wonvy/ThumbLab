[CmdletBinding(DefaultParameterSetName = 'Set')]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Project,

    [Parameter(Mandatory = $true, Position = 1, ParameterSetName = 'Set')]
    [string] $Image,

    [Parameter(Mandatory = $true, ParameterSetName = 'Remove')]
    [switch] $Remove
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectFile = Get-Item -LiteralPath $Project
if ($projectFile -isnot [IO.FileInfo] -or $projectFile.Extension -ine '.shapr') {
    throw 'Choose a .shapr project file.'
}

$projectStream = [IO.File]::Open($projectFile.FullName, 'Open', 'Read', 'Read')
try {
    if ($projectStream.Length -eq 0 -or $projectStream.Length -gt 256MB) {
        throw 'The project file must be between 1 byte and 256 MiB.'
    }
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = [BitConverter]::ToString($sha256.ComputeHash($projectStream)).Replace('-', '').ToLowerInvariant()
    } finally { $sha256.Dispose() }
} finally { $projectStream.Dispose() }

$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
if ([string]::IsNullOrWhiteSpace($localAppData)) { throw 'Windows could not locate Local AppData.' }
$previewDirectory = Join-Path $localAppData 'ThumbLab\ShaprThumbnail\previews'
$previewFile = Join-Path $previewDirectory ($hash + '.image')

if (-not ('ThumbLab.ShaprPreviewFiles' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ThumbLab
{
    public static class ShaprPreviewFiles
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool MoveFileExW(string source, string destination, uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern void SHChangeNotify(uint eventId, uint flags, string item, IntPtr unused);

        public static void Replace(string source, string destination)
        {
            const uint ReplaceExisting = 1;
            if (!MoveFileExW(source, destination, ReplaceExisting))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public static void NotifyChanged(string path)
        {
            const uint UpdateItem = 0x2000;
            const uint PathAndFlushNoWait = 0x2005;
            SHChangeNotify(UpdateItem, PathAndFlushNoWait, path, IntPtr.Zero);
        }
    }
}
'@
}

if ($Remove) {
    if ([IO.File]::Exists($previewFile)) { [IO.File]::Delete($previewFile) }
} else {
    $imageFile = Get-Item -LiteralPath $Image
    if ($imageFile -isnot [IO.FileInfo]) { throw 'Choose a PNG or JPEG image file.' }
    $imageStream = [IO.File]::Open($imageFile.FullName, 'Open', 'Read', 'Read')
    try {
        if ($imageStream.Length -lt 8 -or $imageStream.Length -gt 16MB) {
            throw 'The preview image must be between 8 bytes and 16 MiB.'
        }
        $header = New-Object byte[] 8
        if ($imageStream.Read($header, 0, $header.Length) -ne $header.Length) {
            throw 'Could not read the preview image.'
        }
        $png = [BitConverter]::ToString($header) -eq '89-50-4E-47-0D-0A-1A-0A'
        $jpeg = $header[0] -eq 0xFF -and $header[1] -eq 0xD8 -and $header[2] -eq 0xFF
        if (!$png -and !$jpeg) { throw 'Choose a PNG or JPEG image file.' }
        $imageStream.Position = 0

        [void] [IO.Directory]::CreateDirectory($previewDirectory)
        $temporary = Join-Path $previewDirectory ([Guid]::NewGuid().ToString('N') + '.tmp')
        try {
            $destination = [IO.File]::Open($temporary, 'CreateNew', 'Write', 'None')
            try { $imageStream.CopyTo($destination) }
            finally { $destination.Dispose() }
            [ThumbLab.ShaprPreviewFiles]::Replace($temporary, $previewFile)
        } finally {
            if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        }
    } finally { $imageStream.Dispose() }
}

[ThumbLab.ShaprPreviewFiles]::NotifyChanged($projectFile.FullName)
if ($Remove) { Write-Host 'Removed the model preview.' }
else { Write-Host 'Saved the model preview.' }
