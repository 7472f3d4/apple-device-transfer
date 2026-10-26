using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AppleDeviceTransfer;

public sealed record DeviceInfo(string Id, string Name);

public sealed class MediaItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Folder { get; init; }
    public required string Kind { get; init; }
    public required string SizeText { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed record RemoteMediaItem(string Id, string RelativePath, long? Size, object SourceItem);

public sealed record ImportProgress(int Completed, int Total, string CurrentFile);

public sealed record ImportResult(string DestinationFolder, int Copied, IReadOnlyList<string> Errors);
