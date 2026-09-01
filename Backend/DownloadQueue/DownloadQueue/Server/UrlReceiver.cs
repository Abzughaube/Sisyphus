using System.Net;
using System.Text.Json;
using Sisyphus.Backend.ConsoleUi;
using Sisyphus.Backend.Downloads;
using Sisyphus.Backend.Queue;

namespace Sisyphus.Backend.Server;

internal sealed class UrlReceiver
{
    private readonly QueueRepository _queueRepository;
    private readonly DownloadWorker _downloadWorker;
    private readonly ConsoleLogger _logger;
    private readonly Action _urlAdded;
    private readonly HttpListener _listener;

    public UrlReceiver(
        QueueRepository queueRepository,
        DownloadWorker downloadWorker,
        ConsoleLogger logger,
        Action urlAdded)
    {
        _queueRepository = queueRepository;
        _downloadWorker = downloadWorker;
        _logger = logger;
        _urlAdded = urlAdded;

        _listener = new HttpListener();
        _listener.Prefixes.Add("http://localhost:5050/queue/");
    }

    public async Task RunAsync()
    {
        _listener.Start();

        while (true)
        {
            var context = await _listener.GetContextAsync();

            AddCorsHeaders(context.Response);

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

            await HandlePostAsync(context);
        }
    }

    private async Task HandlePostAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(
                context.Request.InputStream,
                context.Request.ContentEncoding);

            var body = await reader.ReadToEndAsync();

            var request = JsonSerializer.Deserialize<UrlRequest>(
                body,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (string.IsNullOrWhiteSpace(request?.Url))
            {
                context.Response.StatusCode = 400;
                return;
            }

            var url = request.Url.Trim();

            if (_queueRepository.IsPending(url))
            {
                _logger.Write(
                    ConsoleColor.Magenta,
                    $"URL bereits in Warteschlange: {url}");
            }
            else if (_queueRepository.IsCompleted(url))
            {
                _logger.Write(
                    ConsoleColor.Magenta,
                    $"URL bereits früher heruntergeladen: {url}");
            }
            else
            {
                _urlAdded();
                _queueRepository.AddPending(url);
                _downloadWorker.Enqueue(url);

                _logger.Write(
                    ConsoleColor.Magenta,
                    $"URL empfangen: {url}");
            }

            context.Response.StatusCode = 200;
        }
        catch (Exception ex)
        {
            _logger.WriteError(
                ConsoleColor.Red,
                $"Fehler beim Empfangen: {ex.Message}");

            context.Response.StatusCode = 500;
        }
        finally
        {
            context.Response.Close();
        }
    }

    private static void AddCorsHeaders(HttpListenerResponse response)
    {
        response.AddHeader("Access-Control-Allow-Origin", "*");
        response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
        response.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");
    }

    private sealed record UrlRequest(string Url);
}
