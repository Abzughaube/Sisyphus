using System.Diagnostics;
using System.Text.Json;

internal sealed class YtDlpVersionChecker
{
    private readonly ConsoleLogger _logger;

    public YtDlpVersionChecker(ConsoleLogger logger)
    {
        _logger = logger;
    }

    public async Task CheckAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            string installedVersion = await proc!.StandardOutput.ReadToEndAsync();
            proc.WaitForExit();

            installedVersion = installedVersion.Trim();

            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SisyphusService/1.0");

            var response = await client.GetStringAsync(
                "https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest");

            using var json = JsonDocument.Parse(response);
            var latestVersion = json.RootElement
                .GetProperty("tag_name")
                .GetString()?
                .Trim();

            if (!string.IsNullOrWhiteSpace(latestVersion) &&
                latestVersion != installedVersion)
            {
                _logger.Write(
                    ConsoleColor.Yellow,
                    $"Hinweis: Neue yt-dlp-Version verfügbar: {latestVersion} " +
                    $"(Installiert: {installedVersion})");
            }
        }
        catch (Exception ex)
        {
            _logger.Write(
                ConsoleColor.DarkGray,
                $"[Hinweis] Konnte yt-dlp-Version nicht prüfen: {ex.Message}");
        }
    }
}
