namespace Sisyphus.Backend.Downloads;

internal enum DownloadStatus
{
    Success,
    AlreadyDownloaded,
    Failed
}

internal sealed record DownloadResult(
    DownloadStatus Status,
    int ExitCode);
