using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

// Konfiguration laden
var configRoot = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var config = configRoot.GetSection("Sisyphus").Get<Config>();

var logger = new ConsoleLogger();


var toastNotifier = new ToastNotifier(logger);
if (config == null || string.IsNullOrWhiteSpace(config.DownloadPath))
{
    logger.WriteError(ConsoleColor.Red, "Konfiguration ungültig oder fehlt. Bitte 'appsettings.json' überprüfen.");
    return;
}

// yt-dlp Version prüfen
await CheckYtDlpVersionAsync();

string queuePath = Path.Combine(AppContext.BaseDirectory, "queue");
Directory.CreateDirectory(queuePath);
string pendingFile = Path.Combine(queuePath, "pending.txt");
string completedFile = Path.Combine(queuePath, "completed.txt");
string retryFile = Path.Combine(queuePath, "retry.txt");
string failedFile = Path.Combine(queuePath, "failed.txt");
var showConclusionMessage = false;

var urlQueue = new BlockingCollection<string>();
var retryCounter = new Dictionary<string, int>();
int pendingCounter = 0;
DateTime lastPendingTime = DateTime.UtcNow;

// Ausstehende URLs beim Start einlesen
if (!File.Exists(pendingFile)) File.WriteAllText(pendingFile, string.Empty);
if (!File.Exists(retryFile)) File.WriteAllText(retryFile, string.Empty);

foreach (var line in File.ReadAllLines(pendingFile).Concat(File.ReadAllLines(retryFile)))
{
    var url = line.Trim();
    if (!string.IsNullOrWhiteSpace(url) && !url.StartsWith("#"))
    {
        urlQueue.Add(url);
    }
}

// retry.txt leeren (wird bei Fehlschlägen erneut befüllt)
File.WriteAllText(retryFile, string.Empty);

var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:5050/queue/");
listener.Start();

_ = Task.Run(async () =>
{
    while (true)
    {
        await Task.Delay(1000);

        if (pendingCounter > 0 && (DateTime.UtcNow - lastPendingTime).TotalSeconds > 3)
        {
            toastNotifier.Show($"Sisyphus: {pendingCounter} URL(s) empfangen");

            pendingCounter = 0;
        }

        if (urlQueue.Count == 0 && showConclusionMessage)
        {
            toastNotifier.Show("Sisyphus: Downloads abgeschlossen");
            showConclusionMessage = false;
        }
    }
});

logger.Write(ConsoleColor.Green, "Sisyphus-Service läuft auf http://localhost:5050/queue");
logger.Write(ConsoleColor.Green, $"Zielverzeichnis: {config.DownloadPath}");

// Hintergrund-Worker zur Verarbeitung der Queue
_ = Task.Run(() =>
{
    int consecutiveFailures = 0;
    const int maxConsecutiveFailures = 3;

    while (true)
    {
        var videoUrl = urlQueue.Take();
        retryCounter.TryGetValue(videoUrl, out int currentRetries);

        logger.Write(ConsoleColor.Cyan, $"Starte Download: {videoUrl}");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = $"--output \"{Path.Combine(config.DownloadPath, "%(title)s.%(ext)s")}\" \"{videoUrl}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);

            proc!.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    if (e.Data.Contains("[download]"))
                    {
                        Console.Write("\r" + e.Data.PadRight(Console.WindowWidth - 1));
                    }
                    else
                    {
                        Console.ResetColor();
                        Console.WriteLine();
                        Console.WriteLine(e.Data);
                    }
                }
            };

            var errorText = new StringBuilder();
            proc!.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    Console.Error.WriteLine(e.Data);
                    errorText.AppendLine(e.Data);
                }
            };

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            proc.WaitForExit();

            bool alreadyDownloaded = errorText.ToString().Contains("has already been downloaded", StringComparison.OrdinalIgnoreCase);

            if (proc.ExitCode != 0 && !alreadyDownloaded)
            {
                throw new Exception($"yt-dlp Fehlercode {proc.ExitCode}");
            }

            Console.WriteLine();
            logger.Write(ConsoleColor.Cyan, $"Download beendet: {videoUrl}");

            logger.Write(ConsoleColor.DarkYellow, $"Noch ausstehend: {urlQueue.Count}");

            consecutiveFailures = 0;
            retryCounter.Remove(videoUrl);

            File.AppendAllText(completedFile, videoUrl + Environment.NewLine);
            var lines = File.ReadAllLines(pendingFile).Where(l => l.Trim() != videoUrl).ToList();
            File.WriteAllLines(pendingFile, lines);

            if (urlQueue.Count == 0)
            {
                showConclusionMessage = true;
            }
        }
        catch (Exception ex)
        {
            consecutiveFailures++;
            if (consecutiveFailures >= maxConsecutiveFailures)
            {
                logger.WriteError(ConsoleColor.Red, $"Zu viele aufeinanderfolgende Fehler ({consecutiveFailures}). Verarbeitung wird angehalten.");
                logger.WriteError(ConsoleColor.Red, "Bitte überprüfen Sie die Verbindung oder die Seite und starten Sie den Service neu.");
                break;
            }
            logger.WriteError(ConsoleColor.Red, $"Fehler beim Download: {ex.Message}");

            retryCounter[videoUrl] = currentRetries + 1;
            if (retryCounter[videoUrl] >= 3)
            {
                logger.Write(ConsoleColor.Red, $"Dauerhafter Fehler. URL in failed.txt verschoben: {videoUrl}");
                File.AppendAllText(failedFile, videoUrl + Environment.NewLine);
                var lines = File.ReadAllLines(pendingFile).Where(l => l.Trim() != videoUrl).ToList();
                File.WriteAllLines(pendingFile, lines);
                retryCounter.Remove(videoUrl);
            }
            else
            {
                logger.Write(ConsoleColor.Yellow, $"Fehlgeschlagen. Versuche erneut ({retryCounter[videoUrl]}/3): {videoUrl}");
                File.AppendAllText(retryFile, videoUrl + Environment.NewLine);
                urlQueue.Add(videoUrl);
            }
        }
    }
});

// Anfragen annehmen
while (true)
{
    var context = await listener.GetContextAsync();

    context.Response.AddHeader("Access-Control-Allow-Origin", "*");
    context.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
    context.Response.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");

    if (context.Request.HttpMethod == "OPTIONS")
    {
        context.Response.StatusCode = 200;
        context.Response.Close();
        continue;
    }

    if (context.Request.HttpMethod != "POST")
    {
        context.Response.StatusCode = 405;
        context.Response.Close();
        continue;
    }

    try
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync();

        var json = JsonSerializer.Deserialize<UrlRequest>(
            body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (!string.IsNullOrWhiteSpace(json?.Url))
        {
            var url = json.Url.Trim();
            if (File.ReadAllLines(pendingFile).Contains(url))
            {
                logger.Write(ConsoleColor.Magenta, $"URL bereits in Warteschlange: {url}");
            }
            else if (File.ReadAllLines(completedFile).Contains(url))
            {
                logger.Write(ConsoleColor.Magenta, $"URL bereits früher heruntergeladen: {url}");
            }
            else
            {
                pendingCounter++;
                lastPendingTime = DateTime.UtcNow;

                File.AppendAllText(pendingFile, url + Environment.NewLine);

                urlQueue.Add(url);
                logger.Write(ConsoleColor.Magenta, $"URL empfangen: {url}");
            }

            context.Response.StatusCode = 200;
        }
        else
        {
            context.Response.StatusCode = 400;
        }
    }
    catch (Exception ex)
    {
        logger.WriteError(ConsoleColor.Red, $"Fehler beim Empfangen: {ex.Message}");
        context.Response.StatusCode = 500;
    }
    finally
    {
        context.Response.Close();
    }
}

async Task CheckYtDlpVersionAsync()
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
        var response = await client.GetStringAsync("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest");

        var json = JsonDocument.Parse(response);
        var latestVersion = json.RootElement.GetProperty("tag_name").GetString()?.Trim();

        if (!string.IsNullOrWhiteSpace(latestVersion) && latestVersion != installedVersion)
        {
            logger.Write(ConsoleColor.Yellow, $"Hinweis: Neue yt-dlp-Version verfügbar: {latestVersion} (Installiert: {installedVersion})");
        }
    }
    catch (Exception ex)
    {
        logger.Write(ConsoleColor.DarkGray, $"[Hinweis] Konnte yt-dlp-Version nicht prüfen: {ex.Message}");
    }
}

record UrlRequest(string Url);

record Config
{
    public string DownloadPath { get; init; } = string.Empty;
}
