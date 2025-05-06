using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace TorrentWrapper
{
    /// <summary>
    /// Represents the progress information of a torrent.
    /// </summary>
    public readonly struct TorrentProgressInfo
    {
        public string Name { get; }
        public InfoHashes InfoHashes { get; }
        public TorrentState State { get; }
        public double Progress { get; } // Percentage (0.0 to 100.0)
        public long DownloadSpeed { get; } // Bytes per second
        public long UploadSpeed { get; } // Bytes per second
        public long TotalDownloaded { get; } // Total bytes downloaded
        public long TotalUploaded { get; } // Total bytes uploaded
        public long Size { get; } // Total size of the torrent in bytes
        public int ConnectedPeers { get; }

        internal TorrentProgressInfo(TorrentManager manager)
        {
            Name = manager.Torrent?.Name ?? manager.InfoHashes.V1OrV2.ToHex();
            InfoHashes = manager.InfoHashes;
            State = manager.State;
            Progress = manager.Progress;
            DownloadSpeed = manager.Monitor.DownloadRate;
            UploadSpeed = manager.Monitor.UploadRate;
            TotalDownloaded = manager.Monitor.DataBytesDownloaded;
            TotalUploaded = manager.Monitor.DataBytesUploaded;
            Size = manager.Torrent?.Size ?? 0; // Size might be 0 if metadata isn't available yet
            ConnectedPeers = manager.OpenConnections; // Use OpenConnections for count of active connections
        }
    }

    public partial class TorrentService // Marked as partial
    {
        /// <summary>
        /// Returns a read-only list of all torrents currently managed by the service.
        /// </summary>
        /// <returns>A list of TorrentManager objects.</returns>
        public IReadOnlyList<TorrentManager> ListTorrents()
        {
            // Return a snapshot of the current values to avoid issues with modification during enumeration
            return _managedTorrents.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Removes a torrent from the service.
        /// </summary>
        /// <param name="infoHashes">The InfoHashes of the torrent to remove.</param>
        /// <param name="mode">Specifies whether to remove downloaded data and/or cached data.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the torrent was found and removed, false otherwise.</returns>
        public async Task<bool> RemoveTorrentAsync(
            InfoHashes infoHashes,
            RemoveMode mode = RemoveMode.CacheDataOnly,
            CancellationToken token = default)
        {
            if (!_isInitialized) throw new InvalidOperationException("TorrentService must be initialized before removing torrents.");
            if (infoHashes == null) throw new ArgumentNullException(nameof(infoHashes));

            Console.WriteLine($"[TorrentService] Attempting to remove torrent: {infoHashes.V1OrV2.ToHex()} with mode: {mode}");

            if (_managedTorrents.TryGetValue(infoHashes, out TorrentManager? manager))
            {
                try
                {
                    // Stop the torrent first if it's running
                    if (manager.State != TorrentState.Stopped)
                    {
                        Console.WriteLine($"[TorrentService] Stopping torrent '{manager.Torrent?.Name ?? infoHashes.V1OrV2.ToHex()}' before removal...");
                        await manager.StopAsync(); // Use default timeout for stopping during removal
                    }

                    // Remove from the engine (handles peer disconnection, listener removal, optional file deletion)
                    bool engineRemoved = await _engine.RemoveAsync(manager, mode);

                    if (engineRemoved)
                    {
                        // Remove from our managed dictionary and peer tracking dictionary
                        bool dictionaryRemoved = _managedTorrents.TryRemove(infoHashes, out _);
                        _connectedPeers.TryRemove(infoHashes, out _); // Remove peer tracking entry
// Unsubscribe from events to prevent memory leaks
manager.PeerConnected -= TorrentManager_PeerConnected;
manager.PeerDisconnected -= TorrentManager_PeerDisconnected;


                        if (dictionaryRemoved)
                        {
                            Console.WriteLine($"[TorrentService] Torrent '{manager.Torrent?.Name ?? infoHashes.V1OrV2.ToHex()}' removed successfully.");
                            return true;
                        }
                        else
                        {
                            // This indicates a potential concurrency issue if engine removal succeeded but dictionary removal failed.
                            Console.WriteLine($"[TorrentService WARNING] Torrent '{manager.Torrent?.Name ?? infoHashes.V1OrV2.ToHex()}' removed from engine but failed to remove from internal dictionary.");
                            return false; // Or handle more gracefully
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[TorrentService WARNING] Failed to remove torrent '{manager.Torrent?.Name ?? infoHashes.V1OrV2.ToHex()}' from the engine.");
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TorrentService ERROR] Error removing torrent {infoHashes.V1OrV2.ToHex()}: {ex.Message}");
                    // Depending on the error, the torrent might be in an inconsistent state.
                    // Consider if further cleanup in _managedTorrents is needed.
                    return false;
                }
            }
            else
            {
                Console.WriteLine($"[TorrentService] Torrent not found for removal: {infoHashes.V1OrV2.ToHex()}");
                return false;
            }
        }

        /// <summary>
        /// Removes a torrent from the service.
        /// </summary>
        /// <param name="torrent">The Torrent object to remove.</param>
        /// <param name="mode">Specifies whether to remove downloaded data and/or cached data.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the torrent was found and removed, false otherwise.</returns>
        public async Task<bool> RemoveTorrentAsync(
            Torrent torrent,
            RemoveMode mode = RemoveMode.CacheDataOnly,
            CancellationToken token = default)
        {
            if (torrent == null) throw new ArgumentNullException(nameof(torrent));
            return await RemoveTorrentAsync(torrent.InfoHashes, mode, token);
        }

        /// <summary>
        /// Removes a torrent from the service.
        /// </summary>
        /// <param name="manager">The TorrentManager object to remove.</param>
        /// <param name="mode">Specifies whether to remove downloaded data and/or cached data.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the torrent was found and removed, false otherwise.</returns>
        public async Task<bool> RemoveTorrentAsync(
            TorrentManager manager,
            RemoveMode mode = RemoveMode.CacheDataOnly,
            CancellationToken token = default)
        {
            if (manager == null) throw new ArgumentNullException(nameof(manager));
            // Ensure the manager actually belongs to our internal dictionary before proceeding
            if (!_managedTorrents.ContainsKey(manager.InfoHashes) || _managedTorrents[manager.InfoHashes] != manager)
            {
                 Console.WriteLine($"[TorrentService] Provided TorrentManager for '{manager.InfoHashes.V1OrV2.ToHex()}' is not managed by this service.");
                 return false;
            }
            return await RemoveTorrentAsync(manager.InfoHashes, mode, token);
        }

        /// <summary>
        /// Gets the current progress information for a specific torrent.
        /// </summary>
        /// <param name="infoHashes">The InfoHashes of the torrent to get progress for.</param>
        /// <param name="progressInfo">When this method returns, contains the progress info if the torrent was found; otherwise, null.</param>
        /// <returns>True if the torrent was found, false otherwise.</returns>
        public bool TryGetTorrentProgress(InfoHashes infoHashes, [MaybeNullWhen(false)] out TorrentProgressInfo progressInfo)
        {
             if (!_isInitialized) throw new InvalidOperationException("TorrentService must be initialized before getting progress.");
             if (infoHashes == null) throw new ArgumentNullException(nameof(infoHashes));

            if (_managedTorrents.TryGetValue(infoHashes, out TorrentManager? manager))
            {
                progressInfo = new TorrentProgressInfo(manager);
                return true;
            }
            else
            {
                progressInfo = default;
                return false;
            }
        }

        // Placeholder for EditTorrentAsync (item 8)
        // Placeholder for EditTorrentAsync (item 8)
        // Placeholder for GetPeersAsync (item 9)
} // End of partial class TorrentService
} // End of namespace TorrentWrapper