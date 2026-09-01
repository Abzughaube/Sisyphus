using Sisyphus.Backend.Server;
using Sisyphus.Backend.ConsoleUi;
using Sisyphus.Backend.Downloads;
using Sisyphus.Backend.Notifications;
using Sisyphus.Backend.Queue;
using Sisyphus.Backend.Updates;
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
if (config == null || string.IsNullOrWhiteSpace(config.DownloadPath))
{
    logger.WriteError(ConsoleColor.Red, "Konfiguration ungültig oder fehlt. Bitte 'appsettings.json' überprüfen.");
    return;
}

var ytDlpRunner = new YtDlpRunner(config.DownloadPath);

// yt-dlp Version prüfen
await versionChecker.CheckAsync();

var queueRepository = new QueueRepository(AppContext.BaseDirectory);
var notificationService = new QueueNotificationService(toastNotifier);

var downloadWorker = new DownloadWorker(
    queueRepository,
    ytDlpRunner,
    logger);

downloadWorker.LoadPersistedQueue();
notificationService.Start();

logger.Write(ConsoleColor.Green, "Sisyphus-Service läuft auf http://localhost:5050/queue");
logger.Write(ConsoleColor.Green, $"Zielverzeichnis: {config.DownloadPath}");

// Hintergrund-Worker zur Verarbeitung der Queue
downloadWorker.QueueDrained += notificationService.QueueDrained;
downloadWorker.Start();

// HTTP-Endpunkt für Browser-Addon
var urlReceiver = new UrlReceiver(
    queueRepository,
    downloadWorker,
    logger,
    notificationService.UrlAdded);

await urlReceiver.RunAsync();

record Config
{
    public string DownloadPath { get; init; } = string.Empty;
}
