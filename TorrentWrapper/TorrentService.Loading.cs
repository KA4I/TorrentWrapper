using System;
using System; // Added for Uri
using System.Collections.Concurrent; // Added for ConcurrentDictionary
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

namespace TorrentWrapper
{
    public partial class TorrentService // Marked as partial
    {
        /// <summary>
        /// Loads a torrent from a .torrent file path and adds it to the engine.
        /// </summary>
        /// <param name="torrentPath">The path to the .torrent file.</param>
        /// <param name="saveDirectory">The directory where the downloaded files will be saved.</param>
        /// <param name="settings">Optional settings for the torrent.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The TorrentManager for the loaded torrent.</returns>
        public async Task<TorrentManager> LoadTorrentAsync(
            string torrentPath,
            string saveDirectory,
            TorrentSettings? settings = null,
            CancellationToken token = default)
        {
            if (!_isInitialized) throw new InvalidOperationException("TorrentService must be initialized before loading torrents.");
            if (string.IsNullOrEmpty(torrentPath)) throw new ArgumentNullException(nameof(torrentPath));
            if (!File.Exists(torrentPath)) throw new FileNotFoundException("Torrent file not found.", torrentPath);
            if (string.IsNullOrEmpty(saveDirectory)) throw new ArgumentNullException(nameof(saveDirectory));

            Console.WriteLine($"[TorrentService] Loading torrent from file: {torrentPath}");
            if (settings == null)
            {
                var builder = new TorrentSettingsBuilder();
                if (_engineSettings.AllowNatsDiscovery)
                {
                    builder.AllowDht = true;
                }
                settings = builder.ToSettings();
            }

            try
            {
                TorrentManager manager = await _engine.AddAsync(torrentPath, saveDirectory, settings);
                if (_managedTorrents.TryAdd(manager.InfoHashes, manager))
                {
                    // Add entry for peer tracking and subscribe to events
                    _connectedPeers.TryAdd(manager.InfoHashes, new ConcurrentDictionary<Uri, PeerInfo>());
                    manager.PeerConnected += TorrentManager_PeerConnected;
                    manager.PeerDisconnected += TorrentManager_PeerDisconnected;

                    Console.WriteLine($"[TorrentService] Torrent '{manager.Torrent?.Name ?? manager.InfoHashes.V1OrV2.ToHex()}' added from file.");
                    // Optionally start the torrent immediately after loading?
                    // await manager.StartAsync();
                    return manager;
                }
                else
                {
                    // Should not happen if ClientEngine prevents duplicates, but handle defensively
                    Console.WriteLine($"[TorrentService WARNING] Torrent '{manager.Torrent?.Name ?? manager.InfoHashes.V1OrV2.ToHex()}' was already managed? Returning existing manager.");
                    // Dispose the newly created manager as it won't be used - Handled by engine removal if needed
                    // manager.Dispose(); // Removed explicit Dispose call
                    return _managedTorrents[manager.InfoHashes];
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TorrentService ERROR] Failed to load torrent from file {torrentPath}: {ex.Message}");
                throw; // Re-throw the exception
            }
        }

        /// <summary>
        /// Loads a torrent from a magnet link and adds it to the engine.
        /// </summary>
        /// <param name="magnetLink">The magnet link.</param>
        /// <param name="saveDirectory">The directory where the downloaded files will be saved.</param>
        /// <param name="settings">Optional settings for the torrent.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The TorrentManager for the loaded torrent.</returns>
        public async Task<TorrentManager> LoadTorrentAsync(
            MagnetLink magnetLink,
            string saveDirectory,
            TorrentSettings? settings = null,
            CancellationToken token = default)
        {
            if (!_isInitialized) throw new InvalidOperationException("TorrentService must be initialized before loading torrents.");
            if (magnetLink == null) throw new ArgumentNullException(nameof(magnetLink));
            if (string.IsNullOrEmpty(saveDirectory)) throw new ArgumentNullException(nameof(saveDirectory));

            Console.WriteLine($"[TorrentService] Loading torrent from magnet link: {magnetLink.InfoHashes?.V1OrV2.ToHex() ?? magnetLink.Name ?? "Unknown"}");
            var settingsBuilder = settings == null ? new TorrentSettingsBuilder() : new TorrentSettingsBuilder(settings); // Use default ctor if settings is null

            // --- BEP46 Handling: Check for public key in MagnetLink ---
            byte[]? publicKey = null;
            string? pkHex = null;
            const string pkPrefix = "urn:btpk:";
            // Check AnnounceUrls for the xt parameter (common place for it)
            if (magnetLink.AnnounceUrls != null)
            {
                pkHex = magnetLink.AnnounceUrls
                    .FirstOrDefault(url => url.StartsWith(pkPrefix, StringComparison.OrdinalIgnoreCase))
                    ?.Substring(pkPrefix.Length);
            }

            // TODO: If not in AnnounceUrls, might need to parse magnetLink.OriginalString or another property
            // if the library doesn't expose 'xt' parameters directly.

            if (!string.IsNullOrEmpty(pkHex) && pkHex.Length == 64) // Ed25519 public key is 32 bytes = 64 hex chars
            {
                try
                {
                    publicKey = Convert.FromHexString(pkHex);
                    if (publicKey.Length == 32)
                    {
                        Console.WriteLine($"[TorrentService] Magnet link contains public key: {pkHex}. Mutable torrent detected.");
                        // No specific setting needed in TorrentSettingsBuilder assumed.
                        // Engine should handle DHT lookups based on 'pk' in metadata.
                    }
                    else
                    {
                        Console.WriteLine($"[TorrentService WARNING] Decoded public key from magnet link has incorrect length: {publicKey.Length} bytes. Ignoring.");
                        publicKey = null; // Reset if invalid length
                    }
                }
                catch (FormatException)
                {
                    Console.WriteLine($"[TorrentService WARNING] Invalid hex format for public key in magnet link: {pkHex}. Ignoring.");
                    publicKey = null; // Reset if invalid format
                }
            }
            // --- End BEP46 Handling ---

            settings = settingsBuilder.ToSettings(); // Finalize settings

            try
            {
                // Check if a manager for this infohash already exists
                if (magnetLink.InfoHashes != null && _managedTorrents.TryGetValue(magnetLink.InfoHashes, out var existingManager))
                {
                    Console.WriteLine($"[TorrentService] Torrent for magnet link '{magnetLink.InfoHashes.V1OrV2.ToHex()}' already exists. Returning existing manager.");
                    return existingManager;
                }

                TorrentManager manager = await _engine.AddAsync(magnetLink, saveDirectory, settings);
                if (_managedTorrents.TryAdd(manager.InfoHashes, manager))
                {
                    // Add entry for peer tracking and subscribe to events
                    _connectedPeers.TryAdd(manager.InfoHashes, new ConcurrentDictionary<Uri, PeerInfo>());
                    manager.PeerConnected += TorrentManager_PeerConnected;
                    manager.PeerDisconnected += TorrentManager_PeerDisconnected;

                    Console.WriteLine($"[TorrentService] Torrent '{manager.Torrent?.Name ?? manager.InfoHashes.V1OrV2.ToHex()}' added from magnet link.");
                    // Optionally start the torrent immediately after loading?
                    // await manager.StartAsync();
                    return manager;
                }
                else
                {
                    // Should not happen if ClientEngine prevents duplicates, but handle defensively
                     Console.WriteLine($"[TorrentService WARNING] Torrent '{manager.Torrent?.Name ?? manager.InfoHashes.V1OrV2.ToHex()}' was already managed after adding via magnet? Returning existing manager.");
                    // Dispose the newly created manager as it won't be used - Handled by engine removal if needed
                    // manager.Dispose(); // Removed explicit Dispose call
                    return _managedTorrents[manager.InfoHashes];
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TorrentService ERROR] Failed to load torrent from magnet link {magnetLink.InfoHashes?.V1OrV2.ToHex() ?? "Unknown"}: {ex.Message}");
                throw; // Re-throw the exception
            }
        }
    }
}