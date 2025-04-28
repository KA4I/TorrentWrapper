using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Client.Listeners;
using MonoTorrent.Connections; // Added for ListenerStatus
using MonoTorrent.Connections.Dht;
using MonoTorrent.Dht;
using MonoTorrent.PortForwarding;
using NATS.Client.Core;
using MonoTorrent;
using System.Security.Cryptography; // Added for SHA1
using MonoTorrent; // Provides Factories
using MonoTorrent.Connections.Peer;

namespace TorrentWrapper
{
    public partial class TorrentService : IDisposable
    {
        private readonly ClientEngine _engine;
        // NatsNatTraversalService is now managed internally by ClientEngine based on settings
        private readonly EngineSettings _engineSettings;
        // NatsOpts are part of EngineSettings now, no need for a separate field
        private bool _isInitialized = false; // Ensure this field is defined
        private readonly ConcurrentDictionary<InfoHashes, TorrentManager> _managedTorrents = new ConcurrentDictionary<InfoHashes, TorrentManager>(); // Ensure this field is defined
        // Dictionary to track connected peers per torrent
        private readonly ConcurrentDictionary<InfoHashes, ConcurrentDictionary<Uri, PeerInfo>> _connectedPeers = new ConcurrentDictionary<InfoHashes, ConcurrentDictionary<Uri, PeerInfo>>();

        // Consider making NATS optional
        public TorrentService(EngineSettings? engineSettings = null)
        {
            // Use provided settings or create default ones.
            _engineSettings = engineSettings ?? new EngineSettingsBuilder
            {
                // Sensible defaults - allow port forwarding, use ephemeral ports
                AllowPortForwarding = true,
                DhtEndPoint = new IPEndPoint(IPAddress.Any, 0),
                ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(IPAddress.Any, 0) } }
                // NatsDiscovery defaults to false in EngineSettingsBuilder
            }.ToSettings();

            // Initialize the engine with the final settings.
            // ClientEngine constructor now handles NATS service creation internally if configured.
            // But we want to inject our NatsTurnConnection for 'natsturn' URIs.
            var factories = Factories.Default.WithPeerConnectionCreator(
                "natsturn",
                uri => new NatsTurnConnection(uri, _engineSettings.NatsOptions!));
            _engine = new ClientEngine(_engineSettings, factories);
        }

        public ClientEngine Engine => _engine;
        public ConnectionManager ConnectionManager => _engine.ConnectionManager; // Expose ConnectionManager
        // NatsService is internal to ClientEngine now
        public IReadOnlyDictionary<InfoHashes, TorrentManager> Torrents => _managedTorrents;


        /// <summary>
        /// Initializes the TorrentService, starting the ClientEngine and NATS service (if configured).
        /// This includes attempting port mapping and discovering the external IP.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        public async Task InitializeAsync(CancellationToken token = default)
        {
            if (_isInitialized)
            {
                return; // Already initialized
            }

            Console.WriteLine("[TorrentService] Initializing...");

            // 1. Start the ClientEngine if it's not already running.
            // This starts the DHT engine, peer listeners, etc.
            if (!_engine.IsRunning)
            {
                Console.WriteLine("[TorrentService] Starting ClientEngine components (DHT, Listeners)...");
                await _engine.StartAsync();
                Console.WriteLine("[TorrentService] ClientEngine components started.");
            }
            else
            {
                Console.WriteLine("[TorrentService] ClientEngine already running.");
            }

            // NATS initialization is now handled internally by ClientEngine.StartAsync
            // based on the EngineSettings provided during construction.

            _isInitialized = true;
            Console.WriteLine("[TorrentService] Initialization complete.");
        }

        /// <summary>
        /// Initializes the TorrentService, starting the ClientEngine with specific bootstrap nodes.
        /// </summary>
        /// <param name="bootstrapRouters">The DHT bootstrap routers to use. Pass an empty array to disable default bootstrapping.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task InitializeAsync(string[] bootstrapRouters, CancellationToken token = default)
        {
            if (_isInitialized)
            {
                return; // Already initialized
            }

            Console.WriteLine("[TorrentService] Initializing with custom bootstrap nodes...");

            // 1. Start the ClientEngine if it's not already running, passing bootstrap nodes.
            if (!_engine.IsRunning)
            {
                Console.WriteLine("[TorrentService] Starting ClientEngine components (DHT, Listeners) with custom bootstrap...");
                // Call the new StartAsync overload on ClientEngine
                await _engine.StartAsync(bootstrapRouters);
                Console.WriteLine("[TorrentService] ClientEngine components started.");
            }
            else
            {
                Console.WriteLine("[TorrentService] ClientEngine already running.");
            }

            _isInitialized = true;
            Console.WriteLine("[TorrentService] Initialization complete.");
        }

        private void TorrentManager_PeerConnected(object? sender, PeerConnectedEventArgs e)
        {
            // e.Peer is of type PeerId which has a Uri property
            if (sender is TorrentManager manager && e.Peer.Uri != null)
            {
                if (_connectedPeers.TryGetValue(manager.InfoHashes, out var peerList))
                {
                    var peerInfo = new PeerInfo(e.Peer.Uri); // Construct PeerInfo from Uri
                    peerList.TryAdd(e.Peer.Uri, peerInfo);
                    // Console.WriteLine($"[TorrentService] Peer connected: {e.Peer.Uri} for torrent {manager.InfoHashes.V1OrV2.ToHex()}");
                }
            }
        }

        private void TorrentManager_PeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
        {
            if (sender is TorrentManager manager && e.Peer.Uri != null)
            {
                if (_connectedPeers.TryGetValue(manager.InfoHashes, out var peerList))
                {
                    peerList.TryRemove(e.Peer.Uri, out _);
                    // Console.WriteLine($"[TorrentService] Peer disconnected: {e.Peer.Uri} for torrent {manager.InfoHashes.V1OrV2.ToHex()}");
                }
            }
        }

        // Torrent creation methods moved to TorrentService.Creation.cs
        // Placeholder for other methods (Load, List, Remove, Download, Progress, Edit, Peers)

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Console.WriteLine("[TorrentService] Disposing...");
                // Ensure the engine is stopped before disposing
                _engine?.Dispose();
                // NATS service is disposed by the ClientEngine
                Console.WriteLine("[TorrentService] Disposed.");
            }
        }
    }
}
