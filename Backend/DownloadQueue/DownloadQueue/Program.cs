using Sisyphus.Backend.ConsoleUi;
using Sisyphus.Backend.Downloads;
using Sisyphus.Backend.Notifications;
using Sisyphus.Backend.Queue;
using Sisyphus.Backend.Updates;
using System.Net;
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

var downloadWorker = new DownloadWorker(
    queueRepository,
    ytDlpRunner,
    logger);

int pendingCounter = 0;
DateTime lastPendingTime = DateTime.UtcNow;

downloadWorker.LoadPersistedQueue();

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

        if (downloadWorker.PendingCount == 0 && showConclusionMessage)
        {
            toastNotifier.Show("Sisyphus: Downloads abgeschlossen");
            showConclusionMessage = false;
        }
    }
});

logger.Write(ConsoleColor.Green, "Sisyphus-Service läuft auf http://localhost:5050/queue");
logger.Write(ConsoleColor.Green, $"Zielverzeichnis: {config.DownloadPath}");

// Hintergrund-Worker zur Verarbeitung der Queue
downloadWorker.QueueDrained += () => showConclusionMessage = true;
downloadWorker.Start();

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

                downloadWorker.Enqueue(url);
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
