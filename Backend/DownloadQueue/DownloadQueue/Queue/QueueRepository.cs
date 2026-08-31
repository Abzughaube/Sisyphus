namespace Sisyphus.Backend.Queue; 

internal sealed class QueueRepository
{
    private readonly string _pendingFile;
    private readonly string _completedFile;
    private readonly string _retryFile;
    private readonly string _failedFile;

    public QueueRepository(string baseDirectory)
    {
        var queuePath = Path.Combine(baseDirectory, "queue");
        Directory.CreateDirectory(queuePath);

        _pendingFile = Path.Combine(queuePath, "pending.txt");
        _completedFile = Path.Combine(queuePath, "completed.txt");
        _retryFile = Path.Combine(queuePath, "retry.txt");
        _failedFile = Path.Combine(queuePath, "failed.txt");

        EnsureFileExists(_pendingFile);
        EnsureFileExists(_completedFile);
        EnsureFileExists(_retryFile);
    }

    public IEnumerable<string> LoadPendingAndRetryUrls()
    {
        return File.ReadAllLines(_pendingFile)
            .Concat(File.ReadAllLines(_retryFile))
            .Select(line => line.Trim())
            .Where(url => !string.IsNullOrWhiteSpace(url) && !url.StartsWith("#"));
    }

    public bool IsPending(string url)
    {
        return File.ReadAllLines(_pendingFile).Contains(url);
    }

    public bool IsCompleted(string url)
    {
        return File.ReadAllLines(_completedFile).Contains(url);
    }

    public void AddPending(string url)
    {
        File.AppendAllText(_pendingFile, url + Environment.NewLine);
    }

    public void MarkCompleted(string url)
    {
        File.AppendAllText(_completedFile, url + Environment.NewLine);
        RemoveFromPending(url);
    }

    public void MarkForRetry(string url)
    {
        File.AppendAllText(_retryFile, url + Environment.NewLine);
    }

    public void MarkFailed(string url)
    {
        File.AppendAllText(_failedFile, url + Environment.NewLine);
        RemoveFromPending(url);
    }

    public void ClearRetry()
    {
        File.WriteAllText(_retryFile, string.Empty);
    }

    private void RemoveFromPending(string url)
    {
        var lines = File.ReadAllLines(_pendingFile)
            .Where(line => line.Trim() != url)
            .ToList();

        File.WriteAllLines(_pendingFile, lines);
    }

    private static void EnsureFileExists(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, string.Empty);
        }
    }
}
