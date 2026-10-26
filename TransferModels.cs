using System.Windows.Media;

namespace AppleDeviceTransfer;

public sealed record BridgeDevice(string Id, string Name);
public sealed record BridgeApp(string Id, string Name);

public sealed class TransferEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required bool IsDirectory { get; init; }
    public required long Size { get; init; }
    public ImageSource Icon => ShellIconProvider.GetIcon(IsDirectory, Name);
    public string SizeText => IsDirectory ? "—" : FormatSize(Size);

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.#} {units[index]}";
    }
}

public sealed record TransferProgress(string Direction, long Done, long Total, string FileName);

public sealed record LocalEntry(string Name, string FullPath, bool IsDirectory, long Size)
{
    public ImageSource Icon => ShellIconProvider.GetIcon(IsDirectory, Name);
    public string SizeText => IsDirectory ? "—" : FormatSize(Size);

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.#} {units[index]}";
    }
}
