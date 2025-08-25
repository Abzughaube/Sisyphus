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
    WriteColored(ConsoleColor.Red, "Konfiguration ungültig oder fehlt. Bitte 'appsettings.json' überprüfen.", isError: true);
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
                WriteColored(ConsoleColor.DarkGray, $"[Hinweis] Toast konnte nicht angezeigt werden: {toastEx.Message}");
            }

            pendingCounter = 0;
        }
    }
});


WriteColored(ConsoleColor.Green, "Sisyphus-Service läuft auf http://localhost:5050/queue");
WriteColored(ConsoleColor.Green, $"Zielverzeichnis: {config.DownloadPath}");

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

        WriteColored(ConsoleColor.Cyan, $"Starte Download: {videoUrl}");

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
            WriteColored(ConsoleColor.Cyan, $"Download beendet: {videoUrl}");

            WriteColored(ConsoleColor.DarkYellow, $"Noch ausstehend: {urlQueue.Count}");

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
                WriteColored(ConsoleColor.Red, $"Zu viele aufeinanderfolgende Fehler ({consecutiveFailures}). Verarbeitung wird angehalten.", true);
                WriteColored(ConsoleColor.Red, "Bitte überprüfen Sie die Verbindung oder die Seite und starten Sie den Service neu.", true);
                
                break;
            }
            Console.Error.WriteLine($"Fehler beim Download: {ex.Message}");

            retryCounter[videoUrl] = currentRetries + 1;
            if (retryCounter[videoUrl] >= 3)
            {
                WriteColored(ConsoleColor.Red, $"Dauerhafter Fehler. URL in failed.txt verschoben: {videoUrl}");
                File.AppendAllText(failedFile, videoUrl + Environment.NewLine);

                // Aus pending.txt entfernen
                var lines = File.ReadAllLines(pendingFile).Where(l => l.Trim() != videoUrl).ToList();
                File.WriteAllLines(pendingFile, lines);
                retryCounter.Remove(videoUrl);
            }
            else
            {
                WriteColored(ConsoleColor.Yellow, $"Fehlgeschlagen. Versuche erneut ({retryCounter[videoUrl]}/3): {videoUrl}");
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
            if (!File.ReadAllLines(pendingFile).Contains(url) && !File.ReadAllLines(completedFile).Contains(url))
            {
                pendingCounter++;
                lastPendingTime = DateTime.UtcNow;

                File.AppendAllText(pendingFile, url + Environment.NewLine);

                urlQueue.Add(url);
                WriteColored(ConsoleColor.Magenta, $"URL empfangen: {url}");
            }
            else
            {
                WriteColored(ConsoleColor.Magenta, $"URL bereits in Warteschlange: {url}");
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
void WriteColored(ConsoleColor color, string message, bool isError = false)
{
    var originalColor = Console.ForegroundColor;
    Console.ForegroundColor = color;
    if (isError)
        Console.Error.WriteLine(message);
    else
        Console.WriteLine(message);
    Console.ForegroundColor = originalColor;
}

record UrlRequest(string Url);

record Config
{
    public string DownloadPath { get; init; } = string.Empty;
}