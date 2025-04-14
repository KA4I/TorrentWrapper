using System;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace TorrentWrapper
{
    public partial class TorrentService // Marked as partial
    {
        /// <summary>
        /// Starts downloading a torrent that has already been loaded into the service.
        /// </summary>
        /// <param name="infoHashes">The InfoHashes of the torrent to download.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the torrent was found and started, false otherwise.</returns>
        public async Task<bool> DownloadTorrentAsync(InfoHashes infoHashes, CancellationToken token = default)
        {
            if (!_isInitialized) throw new InvalidOperationException("TorrentService must be initialized before downloading torrents.");
            if (infoHashes == null) throw new ArgumentNullException(nameof(infoHashes));

            if (_managedTorrents.TryGetValue(infoHashes, out TorrentManager? manager))
            {
                if (manager.State == TorrentState.Downloading || manager.State == TorrentState.Seeding || manager.State == TorrentState.Starting)
                {
                    Console.WriteLine($"[TorrentService] Torrent '{manager.Torrent?.Name ?? infoHashes.V1OrV2.ToHex()}' is already downloading or seeding.");
                    return true; // Already in a downloading/seeding state
                }

                if (manager.State == TorrentState.Hashing || manager.State == TorrentState.HashingPaused)
                {
                     Console.WriteLine($"[TorrentService] Torrent '{manager.Torrent?.Name ?? infoHashes.V1OrV2.ToHex()}' is currently hashing. Download will start afterwards if not paused.");
                     // If paused during hashing, StartAsync will resume hashing then download.
                     // If hashing, StartAsync might be redundant but harmless.
                }

                Console.WriteLine($"[TorrentService] Starting download for torrent: {manager.Torrent?.Name ?? infoHashes.V1OrV2.ToHex()}");
                try
                {
                    await manager.StartAsync();
                    Console.WriteLine($"[TorrentService] Torrent '{manager.Torrent?.Name ?? infoHashes.V1OrV2.ToHex()}' started.");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TorrentService ERROR] Failed to start torrent {infoHashes.V1OrV2.ToHex()}: {ex.Message}");
                    // Optionally set manager state to Error? ClientEngine might handle this.
                    return false;
                }
            }
            else
            {
                Console.WriteLine($"[TorrentService] Torrent not found for download: {infoHashes.V1OrV2.ToHex()}");
                return false;
            }
        }

        /// <summary>
        /// Stops downloading a torrent.
        /// </summary>
        /// <param name="infoHashes">The InfoHashes of the torrent to stop.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the torrent was found and stopped, false otherwise.</returns>
        public async Task<bool> StopDownloadAsync(InfoHashes infoHashes, CancellationToken token = default)
        {
             if (!_isInitialized) throw new InvalidOperationException("TorrentService must be initialized before stopping torrents.");
            if (infoHashes == null) throw new ArgumentNullException(nameof(infoHashes));

            if (_managedTorrents.TryGetValue(infoHashes, out TorrentManager? manager))
            {
                 if (manager.State == TorrentState.Stopped || manager.State == TorrentState.Stopping)
                {
                    Console.WriteLine($"[TorrentService] Torrent '{manager.Torrent?.Name ?? infoHashes.V1OrV2.ToHex()}' is already stopped or stopping.");
                    return true;
                }

                Console.WriteLine($"[TorrentService] Stopping download for torrent: {manager.Torrent?.Name ?? infoHashes.V1OrV2.ToHex()}");
                 try
                {
                    await manager.StopAsync(); // Use default timeout
                    Console.WriteLine($"[TorrentService] Torrent '{manager.Torrent?.Name ?? infoHashes.V1OrV2.ToHex()}' stopped.");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TorrentService ERROR] Failed to stop torrent {infoHashes.V1OrV2.ToHex()}: {ex.Message}");
                    return false;
                }
            }
            else
            {
                Console.WriteLine($"[TorrentService] Torrent not found for stopping: {infoHashes.V1OrV2.ToHex()}");
                return false;
            }
        }

        // Placeholder for GetTorrentProgress (item 7)
        // Placeholder for EditTorrentAsync (item 8)
        // Placeholder for GetPeersAsync (item 9)
    }
}