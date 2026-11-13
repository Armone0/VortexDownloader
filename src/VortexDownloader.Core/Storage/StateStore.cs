using System.IO;
using System.Text.Json;
using VortexDownloader.Core.Models;

namespace VortexDownloader.Core.Storage;

public class StateStore
{
    private readonly string _storageFilePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public StateStore(string? customFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(customFilePath))
        {
            _storageFilePath = customFilePath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _storageFilePath = Path.Combine(appData, "VortexDownloader", "tasks.json");
        }

        string? dir = Path.GetDirectoryName(_storageFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string StorageFilePath => _storageFilePath;

    public async Task SaveTasksAsync(IReadOnlyList<DownloadTask> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string tempFile = _storageFilePath + ".tmp";
            string? dir = Path.GetDirectoryName(_storageFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Serialize tasks snapshot
            await using (var stream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, tasks, _jsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true); // else a power cut after the rename can leave a zero-length state file
            }

            // Atomic move / replace
            File.Move(tempFile, _storageFilePath, overwrite: true);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<List<DownloadTask>> LoadTasksAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_storageFilePath))
            {
                return [];
            }

            await using var stream = new FileStream(_storageFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            var tasks = await JsonSerializer.DeserializeAsync<List<DownloadTask>>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
            
            if (tasks == null)
            {
                return [];
            }

            tasks.RemoveAll(t => t == null); // a torn/edited file may hold null entries: keep the rest

            // Restore in-memory derived states
            foreach (var task in tasks)
            {
                task.RecalculateDownloadedBytes();
                if (task.Status == DownloadStatus.Downloading || task.Status == DownloadStatus.Assembling)
                {
                    // If power cut occurred during download or assembly, mark as Paused so user can resume
                    task.Status = DownloadStatus.Paused;
                }
            }

            return tasks;
        }
        catch (JsonException)
        {
            // Torn/corrupt JSON: start empty, but keep the bad file aside so the next save does not destroy it.
            try { File.Move(_storageFilePath, _storageFilePath + ".corrupt", overwrite: true); } catch { }
            return [];
        }
        catch
        {
            // If file is unreadable, return empty list gracefully
            return [];
        }
        finally
        {
            _fileLock.Release();
        }
    }
}
