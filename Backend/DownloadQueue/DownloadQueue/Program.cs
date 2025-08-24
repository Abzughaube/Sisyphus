using System;
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

if (config == null || string.IsNullOrWhiteSpace(config.DownloadPath))
{
    Console.Error.WriteLine("Konfiguration ungültig oder fehlt. Bitte 'appsettings.json' überprüfen.");
    return;
}

string queuePath = Path.Combine(AppContext.BaseDirectory, "queue");
Directory.CreateDirectory(queuePath);
string pendingFile = Path.Combine(queuePath, "pending.txt");
string completedFile = Path.Combine(queuePath, "completed.txt");
string retryFile = Path.Combine(queuePath, "retry.txt");
string failedFile = Path.Combine(queuePath, "failed.txt");

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
    if (!string.IsNullOrWhiteSpace(url))
    {
        urlQueue.Add(url);
    }
}

// retry.txt leeren (wird bei Fehlschlägen erneut befüllt)
File.WriteAllText(retryFile, string.Empty);

foreach (var line in File.ReadAllLines(pendingFile))
{
    var url = line.Trim();
    if (!string.IsNullOrWhiteSpace(url))
    {
        urlQueue.Add(url);
    }
}

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
            try
            {
                new Process()
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell",
                        Arguments = $"-NoProfile -Command \"[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null; $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText01); $template.GetElementsByTagName('text').Item(0).AppendChild($template.CreateTextNode('Sisyphus: {pendingCounter} URL(s) empfangen')) > $null; $toast = [Windows.UI.Notifications.ToastNotification]::new($template); [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('SisyphusService').Show($toast)\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                }.Start();
            }
            catch (Exception toastEx)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"[Hinweis] Toast konnte nicht angezeigt werden: {toastEx.Message}");
                Console.ResetColor();
            }

            pendingCounter = 0;
        }
    }
});


Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("Sisyphus-Service läuft auf http://localhost:5050/queue");
Console.ResetColor();
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"Zielverzeichnis: {config.DownloadPath}");
Console.ResetColor();

// Hintergrund-Worker zur Verarbeitung der Queue
_ = Task.Run(() =>
{
    // Fehlerzähler für globale Fehler (z. B. Netzwerk)
    int consecutiveFailures = 0;
    const int maxConsecutiveFailures = 3;

    while (true)
    {
        var videoUrl = urlQueue.Take();
        retryCounter.TryGetValue(videoUrl, out int currentRetries);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Starte Download: {videoUrl}");
        Console.ResetColor();

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

            // Fortschrittszeile in-place ausgeben
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
                        Console.WriteLine();
                        Console.WriteLine(e.Data);
                    }
                }
            };

            proc!.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                {
                    Console.Error.WriteLine(e.Data);
                }
            };

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            proc.WaitForExit();

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Download beendet: {videoUrl}");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"Noch ausstehend: {urlQueue.Count}");
            Console.ResetColor();

            // Erfolgreicher Download, also globalen Fehlerzähler zurücksetzen
            consecutiveFailures = 0;

            // Retry-Zähler löschen
            retryCounter.Remove(videoUrl);

            // Erfolgreich abgeschlossen → in completed.txt eintragen
            File.AppendAllText(completedFile, videoUrl + Environment.NewLine);

            // Aus pending.txt entfernen
            var lines = File.ReadAllLines(pendingFile).Where(l => l.Trim() != videoUrl).ToList();
            File.WriteAllLines(pendingFile, lines);
        }
        catch (Exception ex)
        {
            consecutiveFailures++;
            if (consecutiveFailures >= maxConsecutiveFailures)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Zu viele aufeinanderfolgende Fehler ({consecutiveFailures}). Verarbeitung wird angehalten.");
                Console.WriteLine("Bitte überprüfen Sie die Verbindung oder die Seite und starten Sie den Service neu.");
                Console.ResetColor();
                break;
            }
            Console.Error.WriteLine($"Fehler beim Download: {ex.Message}");
        }

        retryCounter[videoUrl] = currentRetries + 1;
        if (retryCounter[videoUrl] >= 3)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Dauerhafter Fehler. URL in failed.txt verschoben: {videoUrl}");
            Console.ResetColor();
            File.AppendAllText(failedFile, videoUrl + Environment.NewLine);

            // Aus pending.txt entfernen
            var lines = File.ReadAllLines(pendingFile).Where(l => l.Trim() != videoUrl).ToList();
            File.WriteAllLines(pendingFile, lines);
            retryCounter.Remove(videoUrl);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Fehlgeschlagen. Versuche erneut ({retryCounter[videoUrl]}/3): {videoUrl}");
            Console.ResetColor();
            File.AppendAllText(retryFile, videoUrl + Environment.NewLine);
            urlQueue.Add(videoUrl);
        }
    }
});

// Anfragen annehmen
while (true)
{
    var context = await listener.GetContextAsync();

    // CORS Header setzen
    context.Response.AddHeader("Access-Control-Allow-Origin", "*");
    context.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
    context.Response.AddHeader("Access-Control-Allow-Methods", "POST, OPTIONS");

    // OPTIONS-Request (Preflight) direkt beantworten
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
            if (!File.ReadAllLines(pendingFile).Contains(url))
            {
                pendingCounter++;
                lastPendingTime = DateTime.UtcNow;

                File.AppendAllText(pendingFile, url + Environment.NewLine);

                urlQueue.Add(url);
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"URL empfangen: {url}");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"URL bereits in Warteschlange: {url}");
                Console.ResetColor();
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
        Console.Error.WriteLine($"Fehler beim Empfangen: {ex.Message}");
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
