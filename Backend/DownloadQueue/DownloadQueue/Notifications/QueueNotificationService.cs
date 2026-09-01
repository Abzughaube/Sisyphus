namespace Sisyphus.Backend.Notifications;

internal sealed class QueueNotificationService
{
    private readonly ToastNotifier _toastNotifier;

    private int _pendingCounter;
    private DateTime _lastPendingTime = DateTime.UtcNow;
    private bool _showConclusionMessage;

    public QueueNotificationService(ToastNotifier toastNotifier)
    {
        _toastNotifier = toastNotifier;
    }

    public void Start()
    {
        _ = Task.Run(ProcessNotificationsAsync);
    }

    public void UrlAdded()
    {
        _pendingCounter++;
        _lastPendingTime = DateTime.UtcNow;
    }

    public void QueueDrained()
    {
        _showConclusionMessage = true;
    }

    private async Task ProcessNotificationsAsync()
    {
        while (true)
        {
            await Task.Delay(1000);

            if (_pendingCounter > 0 &&
                (DateTime.UtcNow - _lastPendingTime).TotalSeconds > 3)
            {
                _toastNotifier.Show(
                    $"Sisyphus: {_pendingCounter} URL(s) empfangen");

                _pendingCounter = 0;
            }

            if (_showConclusionMessage)
            {
                _toastNotifier.Show("Sisyphus: Downloads abgeschlossen");
                _showConclusionMessage = false;
            }
        }
    }
}
