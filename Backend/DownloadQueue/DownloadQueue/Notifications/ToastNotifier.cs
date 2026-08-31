using Sisyphus.Backend.ConsoleUi;
using System.Diagnostics;

namespace Sisyphus.Backend.Notifications;

internal sealed class ToastNotifier
{
    private readonly ConsoleLogger _logger;

    public ToastNotifier(ConsoleLogger logger)
    {
        _logger = logger;
    }

    public void Show(string message)
    {
        try
        {
            new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -Command \"[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null; $template = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText01); $template.GetElementsByTagName('text').Item(0).AppendChild($template.CreateTextNode('{EscapeForPowerShell(message)}')) > $null; $toast = [Windows.UI.Notifications.ToastNotification]::new($template); [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('SisyphusService').Show($toast)\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            }.Start();
        }
        catch (Exception ex)
        {
            _logger.Write(
                ConsoleColor.DarkGray,
                $"[Hinweis] Toast konnte nicht angezeigt werden: {ex.Message}");
        }
    }

    private static string EscapeForPowerShell(string value)
    {
        return value.Replace("'", "''");
    }
}
