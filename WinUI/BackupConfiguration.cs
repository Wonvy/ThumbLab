#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ThumbLabWinUI;

internal sealed class BackupConfiguration : IDisposable
{
    public string Path { get; }

    private BackupConfiguration(string path) => Path = path;

    public static BackupConfiguration? Open(string path, out bool hasComponents)
    {
        using var zip = ZipFile.OpenRead(path);
        hasComponents = zip.Entries.Any(item => item.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
        var config = zip.Entries.SingleOrDefault(item => item.FullName.Equals("config.thumblab", StringComparison.OrdinalIgnoreCase));
        if (config == null) return null;
        if (config.Length > ProviderPackage.MaxBackupConfiguration)
            throw new InvalidDataException("备份配置文件过大。");
        using var input = config.Open();
        byte[] bytes = new byte[(int)config.Length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("备份配置大小与声明不一致。");
        string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".thumblab");
        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            output.Write(bytes);
        return new BackupConfiguration(temp);
    }

    public void Dispose()
    {
        try { File.Delete(Path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
