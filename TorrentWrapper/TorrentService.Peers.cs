using System;
using System.Collections.Generic;
using System.Linq;
using System.Net; // Added for IPEndPoint
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Dht; // Added for NodeId

namespace TorrentWrapper
{
    public partial class TorrentService // Marked as partial
    {
        /// <summary>
        /// Gets the list of currently connected peers for a specific torrent, tracked via events.
        /// </summary>
        /// <param name="infoHashes">The InfoHashes of the torrent.</param>
        /// <param name="token">Cancellation token (currently unused).</param>
        /// <returns>A list of PeerInfo objects for connected peers, or an empty list if the torrent is not found or has no connections.</returns>
        public Task<List<PeerInfo>> GetPeersAsync(InfoHashes infoHashes, CancellationToken token = default)
        {
            if (!_isInitialized) throw new InvalidOperationException("TorrentService must be initialized before getting peers.");
            if (infoHashes == null) throw new ArgumentNullException(nameof(infoHashes));

            if (_connectedPeers.TryGetValue(infoHashes, out var peerList))
            {
                // Return a snapshot of the currently connected peers' info from our tracked dictionary
                List<PeerInfo> peers = peerList.Values.ToList();
                Console.WriteLine($"[TorrentService] Returning {peers.Count} connected peers (tracked via events) for torrent: {infoHashes.V1OrV2.ToHex()}");
                return Task.FromResult(peers);
            }
            else
            {
                // If the torrent exists in _managedTorrents but not _connectedPeers, it means no peers connected yet.
                // If it doesn't exist in _managedTorrents, it's simply not managed.
                Console.WriteLine($"[TorrentService] Torrent not found in tracking dictionary or no peers connected for: {infoHashes.V1OrV2.ToHex()}");
                return Task.FromResult(new List<PeerInfo>()); // Return an empty list
            }
        }

        /// <summary>
        /// Gets the list of peers discovered via the NATS NAT Traversal service, if configured.
        /// Note: This is separate from the peers known directly by a specific TorrentManager.
        /// </summary>
        /// <returns>A dictionary mapping NodeId to IPEndPoint, or null if NATS is not configured.</returns>
        public IDictionary<NodeId, IPEndPoint>? GetNatsDiscoveredPeers()
        {
            // Check if NATS discovery is enabled in the settings first.
            if (!_engine.Settings.AllowNatsDiscovery)
            {
                Console.WriteLine("[TorrentService] NATS Discovery is not enabled in EngineSettings. Cannot get NATS discovered peers.");
                return null;
            }
            // ClientEngine now likely exposes this directly if NATS is enabled.
            return _engine.GetNatsDiscoveredPeers();
        }
    }
}