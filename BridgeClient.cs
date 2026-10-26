using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AppleDeviceTransfer;

internal sealed class BridgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<IReadOnlyList<BridgeDevice>> GetDevicesAsync() =>
        ReadItems<BridgeDevice>(await RunAsync(["devices"]));

    public async Task<IReadOnlyList<BridgeApp>> GetAppsAsync(string deviceId) =>
        ReadItems<BridgeApp>(await RunAsync(["apps", deviceId]));

    public async Task<IReadOnlyList<TransferEntry>> GetRemoteItemsAsync(string deviceId, string appId, string relativePath) =>
        ReadItems<TransferEntry>(await RunAsync(["list", deviceId, appId, relativePath]));

    public async Task DownloadAsync(
        string deviceId,
        string appId,
        string remotePath,
        string localPath,
        bool overwrite,
        IProgress<TransferProgress>? progress = null)
    {
        var arguments = new List<string> { "download", deviceId, appId, remotePath, localPath };
        if (overwrite)
            arguments.Add("--overwrite");
        using var result = await RunAsync(arguments, progress);
    }

    public async Task UploadAsync(
        string deviceId,
        string appId,
        string localPath,
        string remotePath,
        bool overwrite,
        IProgress<TransferProgress>? progress = null)
    {
        var arguments = new List<string> { "upload", deviceId, appId, localPath, remotePath };
        if (overwrite)
            arguments.Add("--overwrite");
        using var result = await RunAsync(arguments, progress);
    }

    private static IReadOnlyList<T> ReadItems<T>(JsonDocument result)
    {
        using (result)
        {
            if (!result.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return Array.Empty<T>();

            var rows = JsonSerializer.Deserialize<List<T>>(items.GetRawText(), JsonOptions);
            return rows is null ? Array.Empty<T>() : rows;
        }
    }

    private static async Task<JsonDocument> RunAsync(
        IReadOnlyList<string> arguments,
        IProgress<TransferProgress>? progress = null)
    {
        var bridgePath = FindBridgePath();
        var startInfo = new ProcessStartInfo
        {
            FileName = bridgePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("端末接続プログラムを起動できませんでした。");

        var errorTask = process.StandardError.ReadToEndAsync();
        JsonDocument? finalResult = null;
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            JsonDocument parsed;
            try
            {
                parsed = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            var root = parsed.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "progress")
            {
                progress?.Report(new TransferProgress(
                    root.TryGetProperty("direction", out var direction) ? direction.GetString() ?? string.Empty : string.Empty,
                    root.TryGetProperty("done", out var done) ? done.GetInt64() : 0,
                    root.TryGetProperty("total", out var total) ? total.GetInt64() : 0,
                    root.TryGetProperty("file", out var file) ? file.GetString() ?? string.Empty : string.Empty));
                parsed.Dispose();
                continue;
            }

            finalResult?.Dispose();
            finalResult = parsed;
        }

        await process.WaitForExitAsync();
        var stderr = await errorTask;
        if (finalResult is null)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? "端末接続プログラムから応答がありませんでした。"
                : stderr.Trim());

        if (!finalResult.RootElement.TryGetProperty("ok", out var success) || success.ValueKind != JsonValueKind.True)
        {
            var message = finalResult.RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : "端末との通信に失敗しました。";
            finalResult.Dispose();
            throw new InvalidOperationException(message ?? "端末との通信に失敗しました。");
        }

        return finalResult;
    }

    private static string FindBridgePath()
    {
        var outputBridge = Path.Combine(AppContext.BaseDirectory, "apple-device-bridge.exe");
        if (File.Exists(outputBridge))
            return outputBridge;

        var developmentBridge = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "bridge", "target", "release", "apple-device-bridge.exe"));
        if (File.Exists(developmentBridge))
            return developmentBridge;

        throw new FileNotFoundException("USB接続用のプログラムが見つかりません。アプリを再発行してください。", outputBridge);
    }
}
