using System.Collections.Concurrent;
using Sisyphus.Backend.ConsoleUi;
using Sisyphus.Backend.Queue;

namespace Sisyphus.Backend.Downloads;

internal sealed class DownloadWorker
{
    private const int MaxRetries = 3;
    private const int MaxConsecutiveFailures = 3;

    private readonly QueueRepository _queueRepository;
    private readonly YtDlpRunner _ytDlpRunner;
    private readonly ConsoleLogger _logger;
    private readonly BlockingCollection<string> _urlQueue = new();
    private readonly Dictionary<string, int> _retryCounter = new();

    public DownloadWorker(
        QueueRepository queueRepository,
        YtDlpRunner ytDlpRunner,
        ConsoleLogger logger)
    {
        _queueRepository = queueRepository;
        _ytDlpRunner = ytDlpRunner;
        _logger = logger;
    }

    public int PendingCount => _urlQueue.Count;

    public event Action? QueueDrained;

    public void LoadPersistedQueue()
    {
        foreach (var url in _queueRepository.LoadPendingAndRetryUrls())
        {
            _urlQueue.Add(url);
        }

        _queueRepository.ClearRetry();
    }

    public void Enqueue(string url)
    {
        _urlQueue.Add(url);
    }

    public void Start()
    {
        _ = Task.Run(ProcessQueue);
    }

    private void ProcessQueue()
    {
        var consecutiveFailures = 0;

        while (true)
        {
            var videoUrl = _urlQueue.Take();
            _retryCounter.TryGetValue(videoUrl, out var currentRetries);

            _logger.Write(ConsoleColor.Cyan, $"Starte Download: {videoUrl}");

            try
            {
                var downloadResult = _ytDlpRunner.Run(videoUrl);

                if (downloadResult.Status == DownloadStatus.Failed)
                {
                    throw new Exception($"yt-dlp Fehlercode {downloadResult.ExitCode}");
                }

                Console.WriteLine();
                _logger.Write(ConsoleColor.Cyan, $"Download beendet: {videoUrl}");
                _logger.Write(ConsoleColor.DarkYellow, $"Noch ausstehend: {_urlQueue.Count}");

                consecutiveFailures = 0;
                _retryCounter.Remove(videoUrl);
                _queueRepository.MarkCompleted(videoUrl);

                if (_urlQueue.Count == 0)
                {
                    QueueDrained?.Invoke();
                }
            }
            catch (Exception ex)
            {
                consecutiveFailures++;

                if (consecutiveFailures >= MaxConsecutiveFailures)
                {
                    _logger.WriteError(
                        ConsoleColor.Red,
                        $"Zu viele aufeinanderfolgende Fehler ({consecutiveFailures}). Verarbeitung wird angehalten.");
                    _logger.WriteError(
                        ConsoleColor.Red,
                        "Bitte überprüfen Sie die Verbindung oder die Seite und starten Sie den Service neu.");
                    break;
                }

                _logger.WriteError(ConsoleColor.Red, $"Fehler beim Download: {ex.Message}");

                _retryCounter[videoUrl] = currentRetries + 1;

                if (_retryCounter[videoUrl] >= MaxRetries)
                {
                    _logger.Write(
                        ConsoleColor.Red,
                        $"Dauerhafter Fehler. URL in failed.txt verschoben: {videoUrl}");
                    _queueRepository.MarkFailed(videoUrl);
                    _retryCounter.Remove(videoUrl);
                }
                else
                {
                    _logger.Write(
                        ConsoleColor.Yellow,
                        $"Fehlgeschlagen. Versuche erneut ({_retryCounter[videoUrl]}/{MaxRetries}): {videoUrl}");
                    _queueRepository.MarkForRetry(videoUrl);
                    _urlQueue.Add(videoUrl);
                }
            }
        }
    }
}
