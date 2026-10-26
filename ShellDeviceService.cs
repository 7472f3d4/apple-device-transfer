using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace AppleDeviceTransfer;

/// <summary>
/// Uses the Windows Shell portable-device namespace. All Shell COM objects stay
/// on a dedicated STA thread; the application only receives ordinary .NET data.
/// </summary>
internal sealed class ShellDeviceService : IDisposable
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".heic", ".heif", ".dng", ".tif", ".tiff",
        ".mov", ".mp4", ".m4v", ".3gp", ".aae"
    };

    private readonly ManualResetEventSlim _dispatcherReady = new(false);
    private readonly Thread _thread;
    private readonly Dictionary<string, object> _deviceItemsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RemoteMediaItem> _remoteItemsById = new(StringComparer.Ordinal);
    private Dispatcher? _dispatcher;
    private object? _shell;
    private bool _disposed;

    public ShellDeviceService()
    {
        _thread = new Thread(DispatcherThreadMain)
        {
            IsBackground = true,
            Name = "Apple Device Shell STA"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_dispatcherReady.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Windows Shell の処理スレッドを開始できませんでした。");

        InvokeAsync(() =>
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            _shell = shellType is null
                ? throw new InvalidOperationException("Windows Shell を利用できません。")
                : Activator.CreateInstance(shellType)
                    ?? throw new InvalidOperationException("Windows Shell を開始できません。");
        }).GetAwaiter().GetResult();
    }

    public Task<IReadOnlyList<DeviceInfo>> DiscoverDevicesAsync() => InvokeAsync<IReadOnlyList<DeviceInfo>>(() =>
    {
        ThrowIfDisposed();
        ReleaseComObjects(_remoteItemsById.Values.Select(item => item.SourceItem));
        _remoteItemsById.Clear();
        ReleaseComObjects(_deviceItemsById.Values);
        _deviceItemsById.Clear();

        dynamic shell = _shell!;
        dynamic thisPc = shell.Namespace(17);
        if (thisPc is null)
            return Array.Empty<DeviceInfo>();

        dynamic items = thisPc.Items();
        int count = Convert.ToInt32(items.Count);
        var devices = new List<DeviceInfo>();

        for (var index = 0; index < count; index++)
        {
            dynamic item = items.Item(index);
            var name = ReadString(() => item.Name);
            if (!ReadBoolean(() => item.IsFolder) || !IsAppleDeviceName(name))
                continue;

            var id = Guid.NewGuid().ToString("N");
            _deviceItemsById[id] = item;
            devices.Add(new DeviceInfo(id, name));
        }

        return devices.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    });

    public Task<IReadOnlyList<MediaItem>> ScanDeviceAsync(string deviceId) => InvokeAsync<IReadOnlyList<MediaItem>>(() =>
    {
        ThrowIfDisposed();
        ReleaseComObjects(_remoteItemsById.Values.Select(item => item.SourceItem));
        _remoteItemsById.Clear();
        if (!_deviceItemsById.TryGetValue(deviceId, out var deviceItem))
            throw new InvalidOperationException("端末が再接続されたようです。端末を再検索してください。");

        dynamic device = deviceItem;
        object deviceFolder = device.GetFolder;
        var dcimFolder = FindFolder(deviceFolder, "DCIM", 0);
        if (dcimFolder is null)
            return Array.Empty<MediaItem>();

        var remoteItems = new List<RemoteMediaItem>();
        EnumerateMedia(dcimFolder, string.Empty, 0, remoteItems);

        foreach (var item in remoteItems)
            _remoteItemsById[item.Id] = item;

        return remoteItems
            .OrderBy(item => item.RelativePath, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new MediaItem
            {
                Id = item.Id,
                Name = Path.GetFileName(item.RelativePath),
                Folder = Path.GetDirectoryName(item.RelativePath) ?? "DCIM",
                Kind = GetKind(item.RelativePath),
                SizeText = FormatSize(item.Size)
            })
            .ToArray();
    });

    public Task<ImportResult> ImportAsync(
        IReadOnlyList<string> selectedIds,
        string destinationRoot,
        IProgress<ImportProgress> progress) => InvokeAsync(async () =>
    {
        ThrowIfDisposed();
        if (selectedIds.Count == 0)
            throw new InvalidOperationException("取り込む項目を選択してください。");
        if (string.IsNullOrWhiteSpace(destinationRoot))
            throw new InvalidOperationException("保存先を選択してください。");

        Directory.CreateDirectory(destinationRoot);
        var sessionFolder = CreateSessionFolder(destinationRoot);
        var errors = new List<string>();
        var copied = 0;
        var total = selectedIds.Count;

        for (var index = 0; index < selectedIds.Count; index++)
        {
            var id = selectedIds[index];
            if (!_remoteItemsById.TryGetValue(id, out var remoteItem))
            {
                errors.Add("一覧を更新した項目が含まれていたため、1 件をスキップしました。");
                continue;
            }

            var currentName = Path.GetFileName(remoteItem.RelativePath);
            progress.Report(new ImportProgress(index, total, currentName));

            try
            {
                var targetPath = GetSafeTargetPath(sessionFolder, remoteItem.RelativePath);
                var targetDirectory = Path.GetDirectoryName(targetPath)!;
                Directory.CreateDirectory(targetDirectory);

                dynamic targetShellFolder = ((dynamic)_shell!).Namespace(targetDirectory);
                if (targetShellFolder is null)
                    throw new IOException($"保存先フォルダーを開けません: {targetDirectory}");

                targetShellFolder.CopyHere(remoteItem.SourceItem, 16);
                await WaitForFileAsync(targetPath, remoteItem.Size, TimeSpan.FromMinutes(45));
                copied++;
            }
            catch (Exception exception)
            {
                errors.Add($"{remoteItem.RelativePath}: {exception.Message}");
            }
        }

        progress.Report(new ImportProgress(total, total, string.Empty));
        return new ImportResult(sessionFolder, copied, errors);
    });

    private void DispatcherThreadMain()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _dispatcherReady.Set();
        Dispatcher.Run();
    }

    private Task InvokeAsync(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatch(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    private Task<T> InvokeAsync<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatch(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    private Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatch(async () =>
        {
            try
            {
                completion.SetResult(await action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    private void Dispatch(Action action)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ShellDeviceService));

        var dispatcher = _dispatcher ?? throw new InvalidOperationException("Shell の処理スレッドが開始されていません。");
        dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    private object? FindFolder(object parentFolder, string targetName, int depth)
    {
        if (depth > 5)
            return null;

        dynamic folder = parentFolder;
        dynamic items = folder.Items();
        int count = Convert.ToInt32(items.Count);

        for (var index = 0; index < count; index++)
        {
            dynamic item = items.Item(index);
            if (!ReadBoolean(() => item.IsFolder))
                continue;

            var name = ReadString(() => item.Name);
            if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
                return item.GetFolder;

            if (depth < 5)
            {
                object childFolder = item.GetFolder;
                var found = FindFolder(childFolder, targetName, depth + 1);
                if (found is not null)
                    return found;
            }
        }

        return null;
    }

    private void EnumerateMedia(object folderObject, string relativeFolder, int depth, List<RemoteMediaItem> output)
    {
        if (depth > 12)
            return;

        dynamic folder = folderObject;
        dynamic items = folder.Items();
        int count = Convert.ToInt32(items.Count);

        for (var index = 0; index < count; index++)
        {
            dynamic item = items.Item(index);
            var name = ReadString(() => item.Name);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (ReadBoolean(() => item.IsFolder))
            {
                var childFolder = string.IsNullOrEmpty(relativeFolder)
                    ? name
                    : Path.Combine(relativeFolder, name);
                EnumerateMedia(item.GetFolder, childFolder, depth + 1, output);
                continue;
            }

            if (!MediaExtensions.Contains(Path.GetExtension(name)))
                continue;

            var relativePath = string.IsNullOrEmpty(relativeFolder)
                ? name
                : Path.Combine(relativeFolder, name);
            var id = Guid.NewGuid().ToString("N");
            output.Add(new RemoteMediaItem(id, relativePath, ReadSize(() => item.Size), item));
        }
    }

    private static async Task WaitForFileAsync(string path, long? expectedSize, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        long previousSize = -1;
        var stableChecks = 0;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            if (!File.Exists(path))
                continue;

            var currentSize = new FileInfo(path).Length;
            if (expectedSize is > 0 && currentSize >= expectedSize.Value)
                return;

            if (currentSize == previousSize)
                stableChecks++;
            else
                stableChecks = 0;

            if (stableChecks >= 20)
                return;

            previousSize = currentSize;
        }

        throw new TimeoutException("転送の完了を確認できませんでした。接続を確認してから再試行してください。");
    }

    private static string CreateSessionFolder(string destinationRoot)
    {
        var baseName = $"AppleImport_{DateTime.Now:yyyy-MM-dd_HHmmss}";
        var candidate = Path.Combine(destinationRoot, baseName);
        var suffix = 1;
        while (Directory.Exists(candidate))
        {
            candidate = Path.Combine(destinationRoot, $"{baseName}_{suffix:00}");
            suffix++;
        }

        Directory.CreateDirectory(candidate);
        return candidate;
    }

    private static string GetSafeTargetPath(string sessionFolder, string relativePath)
    {
        var root = Path.GetFullPath(sessionFolder) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(sessionFolder, relativePath));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("端末から不正なファイル名が返されました。");
        return target;
    }

    private static bool IsAppleDeviceName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        Regex.IsMatch(name, @"\b(iPhone|iPad)\b|Apple.*(iPhone|iPad)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string GetKind(string relativePath) => Path.GetExtension(relativePath).ToLowerInvariant() switch
    {
        ".mov" or ".mp4" or ".m4v" or ".3gp" => "動画",
        ".aae" => "編集情報",
        _ => "写真"
    };

    private static string FormatSize(long? size)
    {
        if (size is null)
            return "—";

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = size.Value;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private static string ReadString(Func<object?> read)
    {
        try { return Convert.ToString(read()) ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool ReadBoolean(Func<object?> read)
    {
        try { return Convert.ToBoolean(read()); }
        catch { return false; }
    }

    private static long? ReadSize(Func<object?> read)
    {
        try
        {
            var value = read();
            if (value is null)
                return null;
            return Convert.ToInt64(value);
        }
        catch
        {
            return null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ShellDeviceService));
    }

    private static void ReleaseComObjects(IEnumerable<object> objects)
    {
        foreach (var value in objects)
        {
            if (Marshal.IsComObject(value))
                Marshal.ReleaseComObject(value);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(() =>
            {
                ReleaseComObjects(_remoteItemsById.Values.Select(item => item.SourceItem));
                _remoteItemsById.Clear();
                ReleaseComObjects(_deviceItemsById.Values);
                _deviceItemsById.Clear();
                if (_shell is not null && Marshal.IsComObject(_shell))
                    Marshal.ReleaseComObject(_shell);
                _shell = null;
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
            }, DispatcherPriority.Send);
            _thread.Join(TimeSpan.FromSeconds(3));
        }

        _dispatcherReady.Dispose();
    }
}
