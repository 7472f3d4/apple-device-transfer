using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace AppleDeviceTransfer;

public partial class MainWindow : Window
{
    private readonly BridgeClient _bridge = new();
    private readonly ShellDeviceService _photoService;
    private readonly ObservableCollection<LocalEntry> _localItems = [];
    private readonly ObservableCollection<TransferEntry> _remoteItems = [];
    private readonly ObservableCollection<MediaItem> _photoItems = [];
    private readonly DispatcherTimer _devicePollingTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private string _remotePath = string.Empty;
    private string? _deviceSignature;
    private bool _busy;
    private bool _initializing;
    private bool _devicePollInProgress;

    private BridgeDevice? SelectedDevice => DeviceComboBox.SelectedItem as BridgeDevice;
    private BridgeApp? SelectedApp => AppComboBox.SelectedItem as BridgeApp;

    public MainWindow()
    {
        InitializeComponent();
        _photoService = new ShellDeviceService();
        LocalGrid.ItemsSource = _localItems;
        RemoteGrid.ItemsSource = _remoteItems;
        MediaGrid.ItemsSource = _photoItems;
        _devicePollingTimer.Tick += DevicePollingTimer_Tick;
        LocalPathTextBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        PhotoDestinationTextBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "AppleDeviceImport");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _initializing = true;
        LoadLocalItems();
        _initializing = false;
        await RefreshDevicesAsync();
        _devicePollingTimer.Start();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_busy)
        {
            e.Cancel = true;
            MessageBox.Show(this, "転送が終わってからアプリを閉じてください。", "転送中",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _devicePollingTimer.Stop();
        _photoService.Dispose();
    }

    private async void DevicePollingTimer_Tick(object? sender, EventArgs e)
    {
        if (_busy || _devicePollInProgress)
            return;

        _devicePollInProgress = true;
        try
        {
            var devices = await _bridge.GetDevicesAsync();
            if (!string.Equals(GetDeviceSignature(devices), _deviceSignature, StringComparison.Ordinal))
                await RefreshDevicesAsync(devices);
        }
        catch
        {
            // Keep the last displayed state if a background scan temporarily fails.
        }
        finally
        {
            _devicePollInProgress = false;
        }
    }

    private async Task RefreshDevicesAsync(IReadOnlyList<BridgeDevice>? detectedDevices = null)
    {
        var previousDeviceId = SelectedDevice?.Id;
        SetBusy(true);
        StatusText.Text = "USB端末を検索しています…";
        DeviceComboBox.ItemsSource = null;
        AppComboBox.ItemsSource = null;
        _remoteItems.Clear();
        _remotePath = string.Empty;
        RemotePathText.Text = "/";
        RemoteEmptyText.Text = "端末を検索中…";
        UpdateRemoteTitle();

        try
        {
            var devices = detectedDevices ?? await _bridge.GetDevicesAsync();
            _deviceSignature = GetDeviceSignature(devices);
            DeviceComboBox.ItemsSource = devices;
            if (devices.Count > 0)
            {
                DeviceComboBox.SelectedItem = devices.FirstOrDefault(device => device.Id == previousDeviceId)
                    ?? devices[0];
                UpdateRemoteTitle();
                StatusText.Text = "端末を検出しました。共有アプリを検索しています…";
                await LoadAppsAsync(SelectedDevice ?? devices[0]);
            }
            else
            {
                StatusText.Text = "端末が見つかりません。";
                RemoteEmptyText.Text = "端末未検出";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = GetFriendlyError(exception.Message);
            RemoteEmptyText.Text = "USBサービスを確認";
        }
        finally
        {
            SetBusy(false);
            UpdateTransferButtons();
        }
    }

    private static string GetDeviceSignature(IEnumerable<BridgeDevice> devices) =>
        string.Join("|", devices.Select(device => device.Id).OrderBy(id => id, StringComparer.Ordinal));

    private async void DeviceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busy)
            return;

        UpdateRemoteTitle();
        if (SelectedDevice is not { } device)
            return;
        await LoadAppsAsync(device);
    }

    private void UpdateRemoteTitle()
    {
        var deviceName = SelectedDevice?.Name;
        RemoteTitleText.Text = string.IsNullOrWhiteSpace(deviceName)
            ? "端末の共有書類"
            : $"{deviceName}の共有書類";
    }

    private async Task LoadAppsAsync(BridgeDevice device)
    {
        SetBusy(true);
        StatusText.Text = "共有アプリを検索しています…";
        AppComboBox.ItemsSource = null;
        _remoteItems.Clear();
        _remotePath = string.Empty;
        RemotePathText.Text = "/";
        RemoteEmptyText.Text = "共有アプリを確認中…";

        try
        {
            var apps = await _bridge.GetAppsAsync(device.Id);
            AppComboBox.ItemsSource = apps;
            if (apps.Count > 0)
            {
                AppComboBox.SelectedIndex = 0;
                StatusText.Text = $"共有アプリを {apps.Count} 件検出しました。";
                await LoadRemoteItemsAsync(device, apps[0], string.Empty);
            }
            else
            {
                StatusText.Text = "共有アプリがありません。";
                RemoteEmptyText.Text = "共有アプリなし";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = GetFriendlyError(exception.Message);
            RemoteEmptyText.Text = "接続を確認";
        }
        finally
        {
            SetBusy(false);
            UpdateTransferButtons();
        }
    }

    private async void AppComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_busy || SelectedDevice is not { } device || SelectedApp is not { } app)
            return;

        await LoadRemoteItemsAsync(device, app, string.Empty);
    }

    private async void RefreshRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is { } device && SelectedApp is { } app)
            await LoadRemoteItemsAsync(device, app, _remotePath);
    }

    private async void RemoteUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } device || SelectedApp is not { } app || string.IsNullOrEmpty(_remotePath))
            return;

        var slash = _remotePath.LastIndexOf('/');
        _remotePath = slash < 0 ? string.Empty : _remotePath[..slash];
        await LoadRemoteItemsAsync(device, app, _remotePath);
    }

    private async Task LoadRemoteItemsAsync(BridgeDevice device, BridgeApp app, string relativePath)
    {
        SetBusy(true);
        StatusText.Text = "共有書類を読み込んでいます…";
        _remoteItems.Clear();
        RemoteEmptyText.Text = "読み込み中…";
        try
        {
            var items = await _bridge.GetRemoteItemsAsync(device.Id, app.Id, relativePath);
            _remotePath = relativePath;
            RemotePathText.Text = string.IsNullOrEmpty(relativePath) ? "/" : $"/{relativePath}";
            foreach (var item in items)
                _remoteItems.Add(item);
            if (items.Count == 0)
                RemoteEmptyText.Text = "ファイルなし";
            StatusText.Text = $"共有書類: {items.Count} 件";
        }
        catch (Exception exception)
        {
            StatusText.Text = GetFriendlyError(exception.Message);
            RemoteEmptyText.Text = "開けません";
        }
        finally
        {
            SetBusy(false);
            UpdateTransferButtons();
        }
    }

    private void BrowseLocalButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "PC側のフォルダーを選択",
            InitialDirectory = Directory.Exists(LocalPathTextBox.Text)
                ? LocalPathTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) == true)
        {
            LocalPathTextBox.Text = dialog.FolderName;
            LoadLocalItems();
        }
    }

    private void LocalPathTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_initializing)
            LoadLocalItems();
    }

    private void LocalUpButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var parent = Directory.GetParent(LocalPathTextBox.Text);
            if (parent is not null)
            {
                LocalPathTextBox.Text = parent.FullName;
                LoadLocalItems();
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = exception.Message;
        }
    }

    private void LoadLocalItems()
    {
        _localItems.Clear();
        var path = LocalPathTextBox.Text;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            UpdateTransferButtons();
            return;
        }

        try
        {
            var rows = Directory.EnumerateFileSystemEntries(path)
                .Select(fullPath =>
                {
                    var attributes = File.GetAttributes(fullPath);
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    var name = System.IO.Path.GetFileName(fullPath);
                    var size = isDirectory ? 0 : new FileInfo(fullPath).Length;
                    return new LocalEntry(name, fullPath, isDirectory, size);
                })
                .OrderByDescending(item => item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase);

            foreach (var row in rows)
                _localItems.Add(row);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"PC側のフォルダーを読み込めません: {exception.Message}";
        }

        UpdateTransferButtons();
    }

    private void LocalGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LocalGrid.SelectedItem is LocalEntry { IsDirectory: true } entry)
        {
            LocalPathTextBox.Text = entry.FullPath;
            LoadLocalItems();
        }
    }

    private async void RemoteGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RemoteGrid.SelectedItem is TransferEntry { IsDirectory: true } entry
            && SelectedDevice is { } device && SelectedApp is { } app)
        {
            await LoadRemoteItemsAsync(device, app, entry.Path);
        }
    }

    private void LocalGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTransferButtons();
    private void RemoteGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateTransferButtons();

    private async void UploadButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } device || SelectedApp is not { } app)
            return;
        var selections = LocalGrid.SelectedItems.Cast<LocalEntry>().ToArray();
        if (selections.Length == 0)
            return;

        await RunTransferAsync("接続端末へ転送しています…", async progress =>
        {
            foreach (var item in selections)
            {
                var remotePath = JoinRelative(_remotePath, item.Name);
                try
                {
                    await _bridge.UploadAsync(device.Id, app.Id, item.FullPath, remotePath, false, progress);
                }
                catch (InvalidOperationException exception) when (IsCollision(exception.Message))
                {
                    if (!ConfirmOverwrite(exception.Message))
                        continue;
                    await _bridge.UploadAsync(device.Id, app.Id, item.FullPath, remotePath, true, progress);
                }
                StatusText.Text = $"接続端末へコピーしました: {item.Name}";
            }
        });

        LoadLocalItems();
        if (SelectedDevice is { } currentDevice && SelectedApp is { } currentApp)
            await LoadRemoteItemsAsync(currentDevice, currentApp, _remotePath);
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } device || SelectedApp is not { } app)
            return;
        if (!Directory.Exists(LocalPathTextBox.Text))
        {
            MessageBox.Show(this, "PC側の保存先フォルダーを選択してください。", "保存先",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selections = RemoteGrid.SelectedItems.Cast<TransferEntry>().ToArray();
        if (selections.Length == 0)
            return;

        await RunTransferAsync("接続端末からPCへ転送しています…", async progress =>
        {
            foreach (var item in selections)
            {
                var localPath = System.IO.Path.Combine(LocalPathTextBox.Text, item.Name);
                try
                {
                    await _bridge.DownloadAsync(device.Id, app.Id, item.Path, localPath, false, progress);
                }
                catch (InvalidOperationException exception) when (IsCollision(exception.Message))
                {
                    if (!ConfirmOverwrite(exception.Message))
                        continue;
                    await _bridge.DownloadAsync(device.Id, app.Id, item.Path, localPath, true, progress);
                }
                StatusText.Text = $"PCへコピーしました: {item.Name}";
            }
        });

        LoadLocalItems();
        if (SelectedDevice is { } currentDevice && SelectedApp is { } currentApp)
            await LoadRemoteItemsAsync(currentDevice, currentApp, _remotePath);
    }

    private async Task RunTransferAsync(string initialStatus, Func<IProgress<TransferProgress>, Task> operation)
    {
        SetBusy(true);
        OperationProgress.Value = 0;
        OperationProgress.IsIndeterminate = true;
        OperationProgress.Visibility = Visibility.Visible;
        StatusText.Text = initialStatus;

        var progress = new Progress<TransferProgress>(value =>
        {
            OperationProgress.IsIndeterminate = value.Total <= 0;
            if (value.Total > 0)
                OperationProgress.Value = Math.Clamp(value.Done * 100.0 / value.Total, 0, 100);
            var direction = value.Direction == "upload" ? "端末へ" : "PCへ";
            StatusText.Text = string.IsNullOrWhiteSpace(value.FileName)
                ? $"{direction}転送中…"
                : $"{direction}転送中: {value.FileName}";
        });

        try
        {
            await operation(progress);
            OperationProgress.Value = 100;
            StatusText.Text = "転送完了";
        }
        catch (Exception exception)
        {
            StatusText.Text = GetFriendlyError(exception.Message);
            MessageBox.Show(this, StatusText.Text, "転送できませんでした",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            OperationProgress.IsIndeterminate = false;
            OperationProgress.Visibility = Visibility.Collapsed;
            SetBusy(false);
        }
    }

    private static bool IsCollision(string message) =>
        message.Contains("同名", StringComparison.OrdinalIgnoreCase)
        || message.Contains("overwrite", StringComparison.OrdinalIgnoreCase);

    private bool ConfirmOverwrite(string message) =>
        MessageBox.Show(this,
            $"同名の項目が既にあります。上書きしますか？\n\n{message}",
            "上書きの確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private void BrowsePhotoButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "写真・動画の保存先を選択",
            InitialDirectory = Directory.Exists(PhotoDestinationTextBox.Text)
                ? PhotoDestinationTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(this) == true)
            PhotoDestinationTextBox.Text = dialog.FolderName;
    }

    private async void ScanPhotosButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        PhotoCountText.Text = "検索中…";
        _photoItems.Clear();
        StatusText.Text = "写真を検索中…";

        try
        {
            var devices = await _photoService.DiscoverDevicesAsync();
            if (devices.Count == 0)
            {
                PhotoCountText.Text = "端末未検出";
                StatusText.Text = "端末が見つかりません。端末のロックを解除し、「このコンピュータを信頼」を選択してください。";
                return;
            }

            if (devices.Count > 1)
            {
                PhotoCountText.Text = $"{devices.Count}台を検出";
                StatusText.Text = "複数の端末が検出されました。写真を取り込む端末だけを接続してください。";
                return;
            }

            var items = await _photoService.ScanDeviceAsync(devices[0].Id);
            foreach (var item in items)
                _photoItems.Add(item);
            PhotoCountText.Text = $"{items.Count} 件";
            StatusText.Text = items.Count == 0
                ? "写真なし。ロック解除・信頼・iCloud写真の設定を確認。"
                : "写真・動画を選択して取り込み可能。";
        }
        catch (Exception exception)
        {
            PhotoCountText.Text = "検索に失敗しました";
            StatusText.Text = GetFriendlyError(exception.Message);
        }
        finally
        {
            SetBusy(false);
            ImportPhotosButton.IsEnabled = _photoItems.Count > 0;
        }
    }

    private async void ImportPhotosButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _photoItems.Where(item => item.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show(this, "取り込む写真・動画を選択してください。", "項目の選択",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(PhotoDestinationTextBox.Text))
        {
            MessageBox.Show(this, "保存先フォルダーを選択してください。", "保存先",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        OperationProgress.Value = 0;
        OperationProgress.IsIndeterminate = false;
        OperationProgress.Maximum = selected.Length;
        OperationProgress.Visibility = Visibility.Visible;
            StatusText.Text = "写真・動画を取り込み中…";
        var progress = new Progress<ImportProgress>(value =>
        {
            OperationProgress.Value = value.Completed;
            if (!string.IsNullOrEmpty(value.CurrentFile))
                StatusText.Text = $"取り込み中: {value.CurrentFile} ({value.Completed + 1}/{selected.Length})";
        });

        try
        {
            var result = await _photoService.ImportAsync(
                selected.Select(item => item.Id).ToArray(), PhotoDestinationTextBox.Text, progress);
            StatusText.Text = $"取り込み完了: {result.Copied} 件。保存先: {result.DestinationFolder}";
            if (result.Errors.Count > 0)
            {
                var details = string.Join(Environment.NewLine, result.Errors.Take(5));
                MessageBox.Show(this, $"{result.Copied} 件を取り込みました。一部を転送できませんでした。\n\n{details}",
                    "一部未完了", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show(this, $"{result.Copied} 件を取り込みました。\n\n{result.DestinationFolder}",
                    "取り込み完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = GetFriendlyError(exception.Message);
            MessageBox.Show(this, StatusText.Text, "取り込めませんでした",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            OperationProgress.Visibility = Visibility.Collapsed;
            OperationProgress.Maximum = 100;
            SetBusy(false);
            ImportPhotosButton.IsEnabled = _photoItems.Count > 0;
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        DeviceComboBox.IsEnabled = !busy;
        AppComboBox.IsEnabled = !busy;
        RefreshRemoteButton.IsEnabled = !busy;
        BrowseLocalButton.IsEnabled = !busy;
        LocalUpButton.IsEnabled = !busy;
        RemoteUpButton.IsEnabled = !busy;
        LocalPathTextBox.IsEnabled = !busy;
        LocalGrid.IsEnabled = !busy;
        RemoteGrid.IsEnabled = !busy;
        UploadButton.IsEnabled = !busy && SelectedDevice is not null && SelectedApp is not null && LocalGrid.SelectedItems.Count > 0;
        DownloadButton.IsEnabled = !busy && SelectedDevice is not null && SelectedApp is not null && RemoteGrid.SelectedItems.Count > 0;
        ScanPhotosButton.IsEnabled = !busy;
        ImportPhotosButton.IsEnabled = !busy && _photoItems.Any(item => item.IsSelected);
    }

    private void UpdateTransferButtons()
    {
        UploadButton.IsEnabled = !_busy && SelectedDevice is not null && SelectedApp is not null && LocalGrid.SelectedItems.Count > 0;
        DownloadButton.IsEnabled = !_busy && SelectedDevice is not null && SelectedApp is not null && RemoteGrid.SelectedItems.Count > 0;
        ImportPhotosButton.IsEnabled = !_busy && _photoItems.Any(item => item.IsSelected);
    }

    private static string JoinRelative(string parent, string name) =>
        string.IsNullOrEmpty(parent) ? name : $"{parent.TrimEnd('/', '\\')}/{name}";

    private static string GetFriendlyError(string message)
    {
        if (message.Contains("Apple Mobile Device Service", StringComparison.OrdinalIgnoreCase))
            return "Apple Mobile Device Service が停止中または無効です。Windows の「サービス」で起動してください。";
        if (message.Contains("信頼", StringComparison.OrdinalIgnoreCase)
            || message.Contains("pair", StringComparison.OrdinalIgnoreCase)
            || message.Contains("lockdown", StringComparison.OrdinalIgnoreCase))
            return "端末のロックを解除し、「このコンピュータを信頼」を選んでから再試行してください。";
        return message;
    }
}
