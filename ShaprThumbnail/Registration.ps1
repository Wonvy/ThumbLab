param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Install', 'Uninstall')]
    [string] $Action,
    [string] $DllPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$provider = '{7D076184-5648-487E-9EB6-591DE23C89C5}'
$handler = '{E357FCCD-A995-4576-B01F-234630154E96}'
$classes = 'Software\Classes'
$classKey = "$classes\CLSID\$provider"
$stateKey = 'Software\ThumbLab\ShaprThumbnail'
$extensions = @('.shapr')
$stringKind = [Microsoft.Win32.RegistryValueKind]::String
$dwordKind = [Microsoft.Win32.RegistryValueKind]::DWord
$readRaw = [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames

function Get-SlotPaths([string] $Extension) {
    "$classes\$Extension"
    "$classes\$Extension\ShellEx"
    "$classes\$Extension\ShellEx\$handler"
}

function Get-Handler([string] $Extension) {
    $paths = Get-SlotPaths $Extension
    $slot = $user.OpenSubKey($paths[2])
    if ($null -ne $slot) {
        try { $slot.GetValue('', $null, $readRaw) }
        finally { $slot.Dispose() }
    }
}

function Save-Handler([Microsoft.Win32.RegistryKey] $State, [string] $Extension) {
    $paths = Get-SlotPaths $Extension
    $existing = 0
    $backup = $State.CreateSubKey($Extension)
    try {
        for ($i = 0; $i -lt $paths.Count; $i++) {
            $key = $user.OpenSubKey($paths[$i])
            if ($null -eq $key) { break }
            try {
                $existing = $i + 1
                if ($i -eq 2 -and $key.GetValueNames() -contains '') {
                    $backup.SetValue('PreviousValue', $key.GetValue('', $null, $readRaw), $key.GetValueKind(''))
                } elseif ($i -eq 2) {
                    $backup.DeleteValue('PreviousValue', $false)
                }
            } finally { $key.Dispose() }
        }
        if ($existing -lt 3) { $backup.DeleteValue('PreviousValue', $false) }
        $backup.SetValue('ExistingKeys', $existing, $dwordKind)
    } finally { $backup.Dispose() }
}

function Restore-Handler([Microsoft.Win32.RegistryKey] $State, [string] $Extension) {
    $paths = Get-SlotPaths $Extension
    $backup = $State.OpenSubKey($Extension)
    if ($null -eq $backup) { return }
    try {
        $slot = $user.OpenSubKey($paths[2], $true)
        if ($null -ne $slot) {
            try {
                if ($provider -eq $slot.GetValue('', $null, $readRaw)) {
                    if ($backup.GetValueNames() -contains 'PreviousValue') {
                        $slot.SetValue('', $backup.GetValue('PreviousValue', $null, $readRaw),
                            $backup.GetValueKind('PreviousValue'))
                    } else {
                        $slot.DeleteValue('', $false)
                    }
                }
            } finally { $slot.Dispose() }
        }

        for ($i = 2; $i -ge $backup.GetValue('ExistingKeys', 3); $i--) {
            $key = $user.OpenSubKey($paths[$i])
            if ($null -eq $key) { continue }
            try { $empty = $key.SubKeyCount -eq 0 -and $key.ValueCount -eq 0 }
            finally { $key.Dispose() }
            if (!$empty) { break }
            $user.DeleteSubKey($paths[$i], $false)
        }
    } finally { $backup.Dispose() }
}

function Assert-X64Dll([string] $Path) {
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'The specified file is not a Windows DLL.' }
        $reader.BaseStream.Position = 0x3C
        $reader.BaseStream.Position = $reader.ReadUInt32()
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) {
            throw 'The specified file is not an x64 Windows DLL.'
        }
        $reader.BaseStream.Position += 16
        if (($reader.ReadUInt16() -band 0x2000) -eq 0) { throw 'The specified file is not a Windows DLL.' }
    } finally { $reader.Dispose() }
}

function Assert-NoLink([string] $Path) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -ne $item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "The installation path is a link: $Path"
    }
}

function Get-DllHash([string] $Path) {
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $stream = [IO.File]::OpenRead($Path)
        try { [Convert]::ToBase64String($sha256.ComputeHash($stream)) }
        finally { $stream.Dispose() }
    } finally { $sha256.Dispose() }
}

function Get-RegisteredServer {
    $server = $machine.OpenSubKey("$classKey\InprocServer32")
    if ($null -ne $server) {
        try { $server.GetValue('', $null, $readRaw) }
        finally { $server.Dispose() }
    }
}

function Remove-InstalledDll {
    if (![IO.File]::Exists($destination)) { return }
    try {
        [IO.File]::Delete($destination)
        return
    } catch [UnauthorizedAccessException], [IO.IOException] {
        # A COM surrogate can hold the DLL open after its registration is removed.
        # Retire it at a unique name so a later installation never shares a pending deletion path.
        $retired = Join-Path $installDirectory ('retired-' + [Guid]::NewGuid().ToString('N') + '.dll')
        try { [IO.File]::Move($destination, $retired) }
        catch {
            throw "The DLL is still in use. Sign out, sign back in, and run uninstall.cmd again. $($_.Exception.Message)"
        }
        try { [ShellRegistrationNative]::DeleteOnRestart($retired) }
        catch {
            [IO.File]::Move($retired, $destination)
            throw "The DLL could not be removed. Sign out, sign back in, and run uninstall.cmd again. $($_.Exception.Message)"
        }
        Write-Host 'The DLL is still in use. Windows will remove it at the next restart.'
    }
}

function Remove-Installation([Microsoft.Win32.RegistryKey] $State) {
    foreach ($extension in $extensions) { Restore-Handler $State $extension }

    $registered = Get-RegisteredServer
    if ($null -ne $registered -and $registered -ne $destination) {
        throw 'The COM registration now points to a different DLL.'
    }
    $machine.DeleteSubKeyTree($classKey, $false)

    Remove-InstalledDll
    if ([IO.Directory]::Exists($installDirectory) -and
        !(Get-ChildItem -LiteralPath $installDirectory -Force | Select-Object -First 1)) {
        [IO.Directory]::Delete($installDirectory)
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$machine = $null
$user = $null
$state = $null
$notify = $false
$exitCode = 0
try {
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this command from an administrator terminal under the Windows account that will use the thumbnails.'
    }
    if ($env:PROCESSOR_ARCHITECTURE -ne 'AMD64' -or ![Environment]::Is64BitProcess) {
        throw 'The thumbnail provider requires x64 Windows and a 64-bit administrator terminal.'
    }

    $installDirectory = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'ShaprThumbnail'
    $destination = Join-Path $installDirectory 'ShaprThumbnail.dll'
    Assert-NoLink $installDirectory
    Assert-NoLink $destination
    $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    $user = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
    $state = $machine.OpenSubKey($stateKey, $true)
    if ($null -ne $state -and $state.GetValue('OwnerSid') -ne $identity.User.Value) {
        throw 'Use the same Windows account that installed the thumbnail provider.'
    }

    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class ShellRegistrationNative
{
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool MoveFileExW(string currentPath, string newPath, uint flags);

    public static void AssociationsChanged()
    {
        SHChangeNotify(0x08000000, 0x2000, IntPtr.Zero, IntPtr.Zero);
    }
    public static void DeleteOnRestart(string path)
    {
        const uint DelayUntilReboot = 4;
        if (!MoveFileExW(path, null, DelayUntilReboot))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static void ReplaceDll(string source, string destination)
    {
        const uint ReplaceExisting = 1;
        if (!MoveFileExW(source, destination, ReplaceExisting))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
}
'@

    if ($Action -eq 'Install') {
        if ([string]::IsNullOrWhiteSpace($DllPath)) { throw 'Supply the path to ShaprThumbnail.dll.' }
        $source = (Get-Item -LiteralPath $DllPath).FullName
        Assert-X64Dll $source

        $newInstallation = $null -eq $state
        if ($newInstallation) {
            $existingClass = $machine.OpenSubKey($classKey)
            if ($null -ne $existingClass) {
                $existingClass.Dispose()
                throw 'The COM class is already registered by another installation.'
            }
            if ([IO.File]::Exists($destination)) { throw "The destination already exists: $destination" }
            $state = $machine.CreateSubKey($stateKey)
            $state.SetValue('OwnerSid', $identity.User.Value, $stringKind)
        } else {
            $registered = Get-RegisteredServer
            if ($null -ne $registered -and $registered -ne $destination) {
                throw 'The COM registration now points to a different DLL.'
            }
        }

        try {
            foreach ($extension in $extensions) {
                if ($newInstallation -or $provider -ne (Get-Handler $extension)) {
                    Save-Handler $state $extension
                }
            }

            [void] [IO.Directory]::CreateDirectory($installDirectory)
            if (![IO.File]::Exists($destination) -or
                (Get-DllHash $source) -cne (Get-DllHash $destination)) {
                $temporary = Join-Path $installDirectory ([Guid]::NewGuid().ToString('N') + '.tmp')
                try {
                    [IO.File]::Copy($source, $temporary)
                    [ShellRegistrationNative]::ReplaceDll($temporary, $destination)
                } finally {
                    if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
                }
            }

            $key = $machine.CreateSubKey($classKey)
            try { $key.SetValue('', 'Shapr3D project thumbnails', $stringKind) }
            finally { $key.Dispose() }
            $server = $machine.CreateSubKey("$classKey\InprocServer32")
            try {
                $server.SetValue('', $destination, $stringKind)
                $server.SetValue('ThreadingModel', 'Apartment', $stringKind)
            } finally { $server.Dispose() }

            $notify = $true
            foreach ($extension in $extensions) {
                $paths = Get-SlotPaths $extension
                $slot = $user.CreateSubKey($paths[2])
                try { $slot.SetValue('', $provider, $stringKind) }
                finally { $slot.Dispose() }
            }
            Write-Host 'Installed .shapr thumbnails for the current Windows account.'
        } catch {
            if ($newInstallation) {
                try {
                    Remove-Installation $state
                    $state.Dispose()
                    $state = $null
                    $machine.DeleteSubKeyTree($stateKey, $false)
                } catch {
                    Write-Warning "Installation cleanup failed. Run uninstall.cmd to retry. $($_.Exception.Message)"
                }
            }
            throw
        }
    } elseif ($null -eq $state) {
        Write-Host 'The thumbnail provider is not installed.'
    } else {
        $notify = $true
        Remove-Installation $state
        $state.Dispose()
        $state = $null
        $machine.DeleteSubKeyTree($stateKey, $false)
        Write-Host 'Uninstalled .shapr thumbnails.'
    }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    $exitCode = 1
} finally {
    if ($notify) { [ShellRegistrationNative]::AssociationsChanged() }
    if ($null -ne $state) { $state.Dispose() }
    if ($null -ne $user) { $user.Dispose() }
    if ($null -ne $machine) { $machine.Dispose() }
    $identity.Dispose()
}
exit $exitCode
