using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FilmAffinity.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FilmAffinity.Services;

/// <summary>
/// Persists per-item FilmAffinity refresh timestamps and vote counts to disk so
/// tiered refresh cadences survive Jellyfin restarts.
/// </summary>
public sealed class FilmAffinityRefreshStateStore : IDisposable
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _stateFilePath;
    private readonly ILogger<FilmAffinityRefreshStateStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="FilmAffinityRefreshStateStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths.</param>
    /// <param name="logger">The logger.</param>
    public FilmAffinityRefreshStateStore(
        IApplicationPaths applicationPaths,
        ILogger<FilmAffinityRefreshStateStore> logger)
        : this(
            Path.Combine(
                (applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths))).DataPath,
                "filmaffinity",
                "refresh-state.json"),
            logger)
    {
    }

    internal FilmAffinityRefreshStateStore(
        string stateFilePath,
        ILogger<FilmAffinityRefreshStateStore> logger)
    {
        _stateFilePath = stateFilePath;
        _logger = logger;
    }

    /// <summary>
    /// Loads all persisted item refresh entries from disk.
    /// Returns an empty dictionary if the file does not exist or cannot be parsed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A mutable dictionary mapping Jellyfin item IDs to their last refresh state.</returns>
    public async Task<Dictionary<Guid, RefreshStateEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return [];
            }

            var stream = new FileStream(
                _stateFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                var document = await JsonSerializer
                    .DeserializeAsync<RefreshStateDocument>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);

                if (document?.Items is null)
                {
                    return [];
                }

                var result = new Dictionary<Guid, RefreshStateEntry>(document.Items.Count);
                foreach (var (key, entry) in document.Items)
                {
                    if (Guid.TryParse(key, out var itemId) && entry is not null)
                    {
                        var normalizedUtc = entry.LastCheckedUtc.Kind == DateTimeKind.Utc
                            ? entry.LastCheckedUtc
                            : DateTime.SpecifyKind(entry.LastCheckedUtc, DateTimeKind.Utc);
                        result[itemId] = entry with { LastCheckedUtc = normalizedUtc };
                    }
                }

                return result;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(
                ex,
                "Failed to load FilmAffinity refresh state from {StateFilePath}; starting with empty state.",
                _stateFilePath);
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Atomically persists the provided refresh state entries to disk.
    /// </summary>
    /// <param name="entries">Dictionary mapping Jellyfin item IDs to their refresh state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous save operation.</returns>
    public async Task SaveAsync(
        IReadOnlyDictionary<Guid, RefreshStateEntry> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var serializedItems = new Dictionary<string, RefreshStateEntry>(entries.Count, StringComparer.Ordinal);
            foreach (var (itemId, entry) in entries)
            {
                serializedItems[itemId.ToString("D")] = entry;
            }

            var document = new RefreshStateDocument
            {
                Version = CurrentSchemaVersion,
                Items = serializedItems
            };

            var tempFilePath = _stateFilePath + ".tmp";
            var stream = new FileStream(
                tempFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer
                    .SerializeAsync(stream, document, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempFilePath, _stateFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "Failed to save FilmAffinity refresh state to {StateFilePath}.",
                _stateFilePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Removes entries from <paramref name="entries"/> whose keys are not in <paramref name="activeItemIds"/>.
    /// </summary>
    /// <param name="entries">The mutable state dictionary.</param>
    /// <param name="activeItemIds">The set of item IDs currently present in enabled libraries.</param>
    /// <returns>The number of pruned entries.</returns>
    public static int PruneDeletedItems(
        IDictionary<Guid, RefreshStateEntry> entries,
        IReadOnlySet<Guid> activeItemIds)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(activeItemIds);

        List<Guid>? toRemove = null;
        foreach (var itemId in entries.Keys)
        {
            if (!activeItemIds.Contains(itemId))
            {
                toRemove ??= [];
                toRemove.Add(itemId);
            }
        }

        if (toRemove is null)
        {
            return 0;
        }

        foreach (var itemId in toRemove)
        {
            entries.Remove(itemId);
        }

        return toRemove.Count;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
    }

    private sealed class RefreshStateDocument
    {
        public int Version { get; set; } = CurrentSchemaVersion;

        public Dictionary<string, RefreshStateEntry>? Items { get; set; }
    }
}
