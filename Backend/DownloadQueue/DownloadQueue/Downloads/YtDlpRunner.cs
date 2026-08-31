using System.Diagnostics;
using System.Text;

namespace Sisyphus.Backend.Downloads;

internal sealed class YtDlpRunner
{
    private readonly string _downloadPath;

    public YtDlpRunner(string downloadPath)
    {
        _downloadPath = downloadPath;
    }

    public DownloadResult Run(string videoUrl)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "yt-dlp",
            Arguments =
                $"--output \"{Path.Combine(_downloadPath, "%(title)s.%(ext)s")}\" \"{videoUrl}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("yt-dlp konnte nicht gestartet werden.");

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;

            if (e.Data.Contains("[download]"))
            {
                Console.Write(
                    "\r" + e.Data.PadRight(Console.WindowWidth - 1));
            }
            else
            {
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine(e.Data);
            }
        };

        var errorText = new StringBuilder();

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;

            Console.Error.WriteLine(e.Data);
            errorText.AppendLine(e.Data);
        };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();

        var alreadyDownloaded = errorText
            .ToString()
            .Contains(
                "has already been downloaded",
                StringComparison.OrdinalIgnoreCase);

        if (alreadyDownloaded)
        {
            return new DownloadResult(
                DownloadStatus.AlreadyDownloaded,
                proc.ExitCode);
        }

        if (proc.ExitCode != 0)
        {
            return new DownloadResult(
                DownloadStatus.Failed,
                proc.ExitCode);
        }

        return new DownloadResult(
            DownloadStatus.Success,
            proc.ExitCode);
    }
}
