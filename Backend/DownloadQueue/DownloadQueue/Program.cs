using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Sisyphus.Backend.ConsoleUi;
using Sisyphus.Backend.Downloads;
using Sisyphus.Backend.Notifications;
using Sisyphus.Backend.Queue;
using Sisyphus.Backend.Updates;

// Konfiguration laden
var configRoot = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var config = configRoot.GetSection("Sisyphus").Get<Config>();

var logger = new ConsoleLogger();


var toastNotifier = new ToastNotifier(logger);
var versionChecker = new YtDlpVersionChecker(logger);
var ytDlpRunner = new YtDlpRunner(config.DownloadPath);
if (config == null || string.IsNullOrWhiteSpace(config.DownloadPath))
{
    logger.WriteError(ConsoleColor.Red, "Konfiguration ungültig oder fehlt. Bitte 'appsettings.json' überprüfen.");
    return;
}

// yt-dlp Version prüfen
await versionChecker.CheckAsync();

var queueRepository = new QueueRepository(AppContext.BaseDirectory);
var showConclusionMessage = false;

var urlQueue = new BlockingCollection<string>();
var retryCounter = new Dictionary<string, int>();
int pendingCounter = 0;
DateTime lastPendingTime = DateTime.UtcNow;

foreach (var url in queueRepository.LoadPendingAndRetryUrls())
{
    urlQueue.Add(url);
}

queueRepository.ClearRetry();

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
            var downloadResult = ytDlpRunner.Run(videoUrl);

            if (downloadResult.Status == DownloadStatus.Failed)
            {
                throw new Exception(
                    $"yt-dlp Fehlercode {downloadResult.ExitCode}");
            }

            Console.WriteLine();
            logger.Write(ConsoleColor.Cyan, $"Download beendet: {videoUrl}");

            logger.Write(ConsoleColor.DarkYellow, $"Noch ausstehend: {urlQueue.Count}");

            consecutiveFailures = 0;
            retryCounter.Remove(videoUrl);

            queueRepository.MarkCompleted(videoUrl);

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
                queueRepository.MarkFailed(videoUrl);
                retryCounter.Remove(videoUrl);
            }
            else
            {
                logger.Write(ConsoleColor.Yellow, $"Fehlgeschlagen. Versuche erneut ({retryCounter[videoUrl]}/3): {videoUrl}");
                queueRepository.MarkForRetry(videoUrl);
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
            if (queueRepository.IsPending(url))
            {
                logger.Write(ConsoleColor.Magenta, $"URL bereits in Warteschlange: {url}");
            }
            else if (queueRepository.IsCompleted(url))
            {
                logger.Write(ConsoleColor.Magenta, $"URL bereits früher heruntergeladen: {url}");
            }
            else
            {
                pendingCounter++;
                lastPendingTime = DateTime.UtcNow;

                queueRepository.AddPending(url);

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


record UrlRequest(string Url);

record Config
{
    public string DownloadPath { get; init; } = string.Empty;
}
