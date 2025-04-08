extern alias BCrypt; // Define the alias for BouncyCastle.Cryptography

// Add necessary using directives
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.BEncoding;
using MonoTorrent.Dht; // Added for NodeId
// --- System/Standard Namespaces ---
using System; // For Convert, InvalidOperationException, NotImplementedException, Buffer, Math, ArgumentException
using System.Collections.Generic;
using System.ComponentModel; // For EditorBrowsable
using System.IO; // For Path
using System.Linq; // For Select
using System.Security.Cryptography; // For SHA1
using System.Text; // For encoding
using System.Threading.Tasks;
using System.Net; // For IPEndPoint
// BouncyCastle types will be referenced via the BCrypt:: alias

namespace TorrentWrapper
{
    /// <summary>
    /// Provides information about a mutable torrent update, including the original
    /// identifier and the new InfoHash representing the updated state.
    /// </summary>
    public class MutableTorrentUpdateInfoEventArgs : EventArgs
    {
        /// <summary>
        /// The original identifier (typically the SHA1 hash of the public key)
        /// of the mutable torrent that was updated.
        /// </summary>
        public InfoHash OriginalInfoHash { get; }

        /// <summary>
        /// The new InfoHash representing the updated state of the torrent after the change.
        /// </summary>
        public InfoHash NewInfoHash { get; }

        // Consider adding SequenceNumber if available/useful
        // public long SequenceNumber { get; }

        public MutableTorrentUpdateInfoEventArgs(InfoHash originalInfoHash, InfoHash newInfoHash)
        {
            OriginalInfoHash = originalInfoHash;
            NewInfoHash = newInfoHash;
        }
    }


    /// <summary>
    /// Provides a simplified interface for interacting with MonoTorrent,
    /// focusing on mutable torrents (BEP46) for distributed data exchange.
    /// </summary>
    public class TorrentService : IAsyncDisposable // Ensure IAsyncDisposable is declared
    {
        /// <summary>
        /// Raised when an update is available for a mutable torrent being managed by this service.
        /// The InfoHash provided is the *new* InfoHash for the updated torrent data.
        /// The application should typically stop the old torrent and start a new one with this InfoHash.
        /// </summary>
        public event EventHandler<MutableTorrentUpdateInfoEventArgs>? MutableTorrentUpdateAvailable;

        private MonoTorrent.Client.ClientEngine? _engine; // Qualify
        private readonly Dictionary<MonoTorrent.InfoHash, MonoTorrent.Client.TorrentManager> _managedTorrents = new(); // Qualify
        private readonly Dictionary<MonoTorrent.InfoHash, long> _mutableSequenceNumbers = new(); // Tracks current seq number for mutable torrents we manage/modify
        private readonly Dictionary<MonoTorrent.InfoHash, byte[]> _publicKeys = new(); // Tracks public key for mutable torrents
        private readonly Dictionary<MonoTorrent.InfoHash, MonoTorrent.BEncoding.BEncodedDictionary> _lastKnownVDictionary = new(); // Tracks last known state
        private readonly Dictionary<MonoTorrent.InfoHash, byte[]> _lastKnownSignature = new(); // Tracks last known signature
        private string? _dhtNodesFilePath; // Path to store DHT nodes
        private EngineSettings? _currentEngineSettings; // Store settings used by the engine
 
        private static readonly string[] DefaultBootstrapNodes = new[]
        {
            "router.bittorrent.com:6881",
            "router.utorrent.com:6881",
            "dht.transmissionbt.com:6881"
        };

        public IReadOnlyList<string> BootstrapNodes { get; init; } = DefaultBootstrapNodes;

        /// <summary>
        /// Exposes the public DHT interface for advanced scenarios (like testing).
        /// </summary>
        public MonoTorrent.Client.IDht? DhtAccess => _engine?.Dht;

        public Dictionary<MonoTorrent.InfoHash, long> MutableSequenceNumbers => _mutableSequenceNumbers;

        /// <summary>
        /// Gets the actual endpoint the DHT listener is bound to after initialization.
        /// Returns null if the engine or settings are not initialized.
        /// </summary>
        public IPEndPoint? DhtListeningEndPoint => _currentEngineSettings?.DhtEndPoint;

        // --- Test Helpers ---
        [EditorBrowsable(EditorBrowsableState.Never)]
        public BEncodedDictionary? GetLastKnownVDictionaryForTest(InfoHash handle) =>
            _lastKnownVDictionary.TryGetValue(handle, out var dict) ? dict : null;

        [EditorBrowsable(EditorBrowsableState.Never)]
        public byte[]? GetLastKnownSignatureForTest(InfoHash handle) =>
            _lastKnownSignature.TryGetValue(handle, out var sig) ? sig : null;

        [EditorBrowsable(EditorBrowsableState.Never)]
        public void StoreItemLocallyForTest(byte[] publicKey, byte[]? salt, BEncodedDictionary value, long sequenceNumber, byte[] signature)
        {
            if (_engine?.Dht == null)
            {
                System.Diagnostics.Debug.WriteLine("[StoreItemLocallyForTest] DhtEngine is null");
                return;
            }

            try
            {
                // We need to access the underlying DhtEngine to store items locally, but we only have IDht
                // Let's use reflection to access the internal DhtEngine instance and its LocalStorage property
                var dhtEngineWrapper = _engine.Dht;
                var wrapperType = dhtEngineWrapper.GetType();
                
                // Try to get the wrapped DhtEngine from the DhtEngineWrapper
                var dhtEngineField = wrapperType.GetField("engine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (dhtEngineField != null)
                {
                    var dhtEngine = dhtEngineField.GetValue(dhtEngineWrapper);
                    if (dhtEngine != null) 
                    {
                        var dhtEngineType = dhtEngine.GetType();
                        
                        // Try to access LocalStorage property in DhtEngine
                        var localStorageProperty = dhtEngineType.GetProperty("LocalStorage");
                        Dictionary<MonoTorrent.Dht.NodeId, MonoTorrent.Dht.StoredDhtItem> localStorage = null;
                        
                        if (localStorageProperty != null)
                        {
                            localStorage = localStorageProperty.GetValue(dhtEngine) as Dictionary<MonoTorrent.Dht.NodeId, MonoTorrent.Dht.StoredDhtItem>;
                        }
                        else
                        {
                            // Fall back to the public property which also returns the storage
                            var localStoragePropProperty = dhtEngineType.GetProperty("LocalStorageProperty");
                            if (localStoragePropProperty != null)
                            {
                                localStorage = localStoragePropProperty.GetValue(dhtEngine) as Dictionary<MonoTorrent.Dht.NodeId, MonoTorrent.Dht.StoredDhtItem>;
                            }
                        }
                        
                        if (localStorage != null)
                        {
                            // Convert parameters to appropriate types
                            var publicKeyBE = (BEncodedString)publicKey;
                            var saltBE = salt == null ? null : (BEncodedString)salt;
                            var signatureBE = (BEncodedString)signature;
                            
                            // Calculate target ID
                            var targetIdMethod = dhtEngineType.GetMethod("CalculateMutableTargetId", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                            if (targetIdMethod != null)
                            {
                                var targetId = targetIdMethod.Invoke(null, new object[] { publicKeyBE, saltBE });
                                
                                // Create StoredDhtItem using reflection
                                var storedDhtItemType = Type.GetType("MonoTorrent.Dht.StoredDhtItem, MonoTorrent") ?? 
                                                       dhtEngineType.Assembly.GetType("MonoTorrent.Dht.StoredDhtItem");
                                
                                if (storedDhtItemType != null)
                                {
                                    var constructor = storedDhtItemType.GetConstructor(new[] { 
                                        typeof(BEncodedValue), 
                                        typeof(BEncodedString), 
                                        typeof(BEncodedString), 
                                        typeof(long), 
                                        typeof(BEncodedString) 
                                    });
                                    
                                    if (constructor != null)
                                    {
                                        var storedItem = constructor.Invoke(new object[] { 
                                            value, 
                                            publicKeyBE, 
                                            saltBE, 
                                            sequenceNumber, 
                                            signatureBE 
                                        });
                                        
                                        // Add to LocalStorage dictionary
                                        var genericDictType = typeof(Dictionary<,>).MakeGenericType(targetId.GetType(), storedItem.GetType());
                                        var addMethod = genericDictType.GetMethod("set_Item") ?? localStorage.GetType().GetMethod("set_Item");
                                        
                                        if (addMethod != null)
                                        {
                                            addMethod.Invoke(localStorage, new[] { targetId, storedItem });
                                            System.Diagnostics.Debug.WriteLine($"[StoreItemLocallyForTest] Successfully stored item with Seq: {sequenceNumber}");
                                            return;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // If the above method doesn't work, try a simpler direct approach
                // Most likely the DHT interface has a StoreMutableLocally method
                var storeMutableMethod = wrapperType.GetMethod("StoreMutableLocally") ?? 
                                        dhtEngineWrapper.GetType().Assembly.GetType("MonoTorrent.Dht.DhtEngine")?.GetMethod("StoreMutableLocally");
                
                if (storeMutableMethod != null)
                {
                    var pk = (BEncodedString)publicKey;
                    var saltBE = salt == null ? null : (BEncodedString)salt;
                    var sig = (BEncodedString)signature;
                    storeMutableMethod.Invoke(dhtEngineWrapper, new object[] { pk, saltBE, value, sequenceNumber, sig });
                    System.Diagnostics.Debug.WriteLine($"[StoreItemLocallyForTest] Successfully stored item using StoreMutableLocally method with Seq: {sequenceNumber}");
                    return;
                }

                // Last resort - try using PutMutableAsync which should store locally as well
                System.Diagnostics.Debug.WriteLine("[StoreItemLocallyForTest] Using PutMutableAsync as fallback");
                var task = _engine.Dht.PutMutableAsync(
                    (BEncodedString)publicKey,
                    salt == null ? null : (BEncodedString)salt,
                    value,
                    sequenceNumber,
                    (BEncodedString)signature);
                
                task.ContinueWith(t => {
                    if (t.IsFaulted)
                        System.Diagnostics.Debug.WriteLine($"[StoreItemLocallyForTest] PutMutableAsync failed: {t.Exception?.InnerException?.Message}");
                    else
                        System.Diagnostics.Debug.WriteLine($"[StoreItemLocallyForTest] PutMutableAsync completed successfully");
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StoreItemLocallyForTest] Exception: {ex.Message}\n{ex.StackTrace}");
            }
        }
        // --- End Test Helpers ---

        /// <summary>
        /// Initializes the TorrentService and starts the MonoTorrent ClientEngine.
        /// </summary>
        /// <param name="settings">Optional engine settings.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task InitializeAsync(EngineSettings? settings = null)
        {
            // Use provided settings or default. Let DhtEndPoint remain null if not specified,
            // so MonoTorrent assigns a random port (by binding to port 0).
            int desiredPort = settings == null ? 6881 : 6882;
            int finalPort = desiredPort;

            bool IsPortAvailable(int port)
            {
                try
                {
                    var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    listener.Stop();
                    return true;
                }
                catch (System.Net.Sockets.SocketException)
                {
                    return false;
                }
            }

            if (!IsPortAvailable(desiredPort))
            {
                finalPort = 0; // Let OS pick a free port
            }

            EngineSettings engineSettings;
            var builder = settings == null ? new EngineSettingsBuilder() : new EngineSettingsBuilder(settings);
            builder.ListenEndPoints = new Dictionary<string, IPEndPoint>
            {
                { "ipv4", new IPEndPoint(IPAddress.Any, finalPort) }
            };
            engineSettings = builder.ToSettings();
            _currentEngineSettings = engineSettings; // Store the final settings
            _engine = new ClientEngine(engineSettings);
         
            _dhtNodesFilePath = Path.Combine(engineSettings.CacheDirectory, "dht_nodes.dat");

            ReadOnlyMemory<byte> initialNodes = ReadOnlyMemory<byte>.Empty;
            if (!string.IsNullOrEmpty(_dhtNodesFilePath) && File.Exists(_dhtNodesFilePath))
            {
                try
                {
                    initialNodes = await File.ReadAllBytesAsync(_dhtNodesFilePath);
                    initialNodes = await File.ReadAllBytesAsync(_dhtNodesFilePath);
                    System.Diagnostics.Debug.WriteLine($"[TorrentService] Loaded {initialNodes.Length} bytes of DHT nodes from {_dhtNodesFilePath}"); // Use Debug.WriteLine
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TorrentService] Warning: Failed to load DHT nodes from {_dhtNodesFilePath}: {ex.Message}"); // Use Debug.WriteLine
                }
            }
            else
            {
                 System.Diagnostics.Debug.WriteLine($"[TorrentService] DHT nodes file path is null or file not found at '{_dhtNodesFilePath}'. Starting DHT from scratch."); // Use Debug.WriteLine
            }

            if (_engine.Dht == null)
            {
                 System.Diagnostics.Debug.WriteLine("[TorrentService] DHT Engine is NULL after ClientEngine creation.");
            }
            else
            {
                 System.Diagnostics.Debug.WriteLine($"[TorrentService] DHT Engine exists. Type: {_engine.Dht.GetType().FullName}. State: {_engine.Dht.State}");
                 // If nodes were loaded, add them. The engine might bootstrap itself later if needed.
                 if (!initialNodes.IsEmpty) {
                     System.Diagnostics.Debug.WriteLine($"[TorrentService] Adding {initialNodes.Length} bytes of cached DHT nodes via IDht.Add.");
                     _engine.Dht.Add(new [] { initialNodes });
                 }
                 // Log the configured DHT endpoint (will likely be 0.0.0.0:0 initially if null in settings)
                 System.Diagnostics.Debug.WriteLine($"[TorrentService] DHT Listener Endpoint (configured in settings): {DhtListeningEndPoint?.ToString() ?? "Null (random port)"}");

                 // Explicitly start the DHT engine with default bootstrap nodes to enable public DHT connectivity
                 var bootstrapRouters = new[] { "router.bittorrent.com", "router.utorrent.com", "dht.transmissionbt.com" };
                 if (_engine.Dht is MonoTorrent.Dht.DhtEngine concreteDht)
                 {
                     try
                     {
                         System.Diagnostics.Debug.WriteLine("[TorrentService] Explicitly starting DHT engine with public bootstrap routers via concrete DhtEngine.");
                         await concreteDht.StartAsync(bootstrapRouters);
                         System.Diagnostics.Debug.WriteLine("[TorrentService] DHT engine StartAsync with bootstrap routers completed.");
                     }
                     catch (Exception ex)
                     {
                         System.Diagnostics.Debug.WriteLine($"[TorrentService] Warning: Failed to start DHT engine: {ex.Message}");
                     }
                 }
                 else if (_engine.Dht is MonoTorrent.Dht.IDhtEngine dhtEngine)
                 {
                     try
                     {
                         System.Diagnostics.Debug.WriteLine("[TorrentService] Explicitly starting DHT engine without bootstrap routers (fallback).");
                         await dhtEngine.StartAsync();
                         System.Diagnostics.Debug.WriteLine("[TorrentService] DHT engine StartAsync completed.");
                     }
                     catch (Exception ex)
                     {
                         System.Diagnostics.Debug.WriteLine($"[TorrentService] Warning: Failed to start DHT engine: {ex.Message}");
                     }
                 }
            }
        }

        /// <summary>
        /// Adds a Torrent object to the engine and starts it.
        /// </summary>
        /// <param name="torrent">The Torrent object.</param>
        /// <param name="savePath">Directory to save torrent data.</param>
        /// <returns>The TorrentManager.</returns>
        public async Task<TorrentManager> AddTorrentAsync(Torrent torrent, string savePath)
        {
            if (_engine == null) throw new InvalidOperationException("Service not initialized.");

            var manager = await _engine.AddAsync(torrent, savePath);
            _managedTorrents[torrent.InfoHashes.V1OrV2] = manager;

            await manager.StartAsync();

            return manager;
        }

        /// <summary>
        /// Creates a new mutable torrent (BEP46).
        /// </summary>
        /// <param name="initialContentPath">Path to the initial file or directory for the torrent.</param>
        /// <returns>A tuple containing the MagnetLink, the non-secret private key (byte[]), and the initial InfoHash.</returns>
        public async Task<(MonoTorrent.MagnetLink MagnetLink, byte[] PrivateKey, MonoTorrent.InfoHash InitialInfoHash)> CreateMutableTorrentAsync(string initialContentPath)
        {
            if (_engine == null) throw new InvalidOperationException("Service not initialized.");

            byte[] signingKeySeed = GeneratePrivateKeySeed();
            byte[] publicKey = GetPublicKeyFromSeed(signingKeySeed);

            var creator = new MonoTorrent.TorrentCreator();
            creator.Comment = "Mutable Torrent created by TorrentWrapper";
            var fileSource = new MonoTorrent.TorrentFileSource(initialContentPath);
            long totalSize = fileSource.Files.Sum(f => f.Length);
            int pieceLength = CalculatePieceLength(totalSize);
            creator.PieceLength = pieceLength;

            var fileList = new MonoTorrent.BEncoding.BEncodedList();
            foreach (var file in fileSource.Files)
            {
                fileList.Add(new MonoTorrent.BEncoding.BEncodedDictionary {
                    { "path", new MonoTorrent.BEncoding.BEncodedList(file.Destination.Parts.ToArray().Select(s => (MonoTorrent.BEncoding.BEncodedString)s)) },
                    { "length", new MonoTorrent.BEncoding.BEncodedNumber(file.Length) }
                });
            }

            long sequenceNumber = 0;
            var vDictionary = new MonoTorrent.BEncoding.BEncodedDictionary {
                { "name", (MonoTorrent.BEncoding.BEncodedString)fileSource.TorrentName },
                { "piece length", new MonoTorrent.BEncoding.BEncodedNumber(pieceLength) },
                { "files", fileList }
            };

            byte[] dataToSign = ConstructDataToSign(sequenceNumber, vDictionary);
            byte[] signature = SignData(dataToSign, signingKeySeed);

            MonoTorrent.InfoHash initialIdentifier = CalculateInfoHash(publicKey);
            System.Diagnostics.Debug.WriteLine($"[CreateMutableTorrentAsync] Calculated Initial Identifier (SHA1(PubKey)): {initialIdentifier.ToHex()}"); // Use Debug.WriteLine
            System.Diagnostics.Debug.WriteLine($"[CreateMutableTorrentAsync] Attempting DHT Put for PubKey: {Convert.ToHexString(publicKey).ToLowerInvariant()} | Seq: {sequenceNumber}"); // Use Debug.WriteLine
            try
            {
                await _engine.Dht.PutMutableAsync(
                    publicKey: (BEncodedString)publicKey,
                    salt: null,
                    value: vDictionary,
                    sequenceNumber: sequenceNumber,
                    signature: (BEncodedString)signature
                );
                System.Diagnostics.Debug.WriteLine($"[CreateMutableTorrentAsync] DHT Put successful for {initialIdentifier.ToHex()}"); // Use Debug.WriteLine
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CreateMutableTorrentAsync] DHT Put FAILED for {initialIdentifier.ToHex()}: {ex.Message}"); // Use Debug.WriteLine
                throw new TorrentException($"Failed to put initial mutable torrent data to DHT for '{initialIdentifier.ToHex()}'.", ex);
            }

            var magnetLink = new MagnetLink(Convert.ToHexString(publicKey).ToLowerInvariant(), name: fileSource.TorrentName);
            string savePath = Path.GetDirectoryName(initialContentPath) ?? throw new ArgumentException("Could not determine directory from initialContentPath.", nameof(initialContentPath));
            Directory.CreateDirectory(savePath);

            TorrentManager manager;
            System.Diagnostics.Debug.WriteLine($"[CreateMutableTorrentAsync] Attempting Engine.AddAsync for Magnet: {magnetLink}"); // Use Debug.WriteLine
            try
            {
                // Prevent manager from interacting with DHT initially for mutable torrents
                var settingsBuilder270 = new TorrentSettingsBuilder();
                settingsBuilder270.AllowDht = false;
                manager = await _engine.AddAsync(magnetLink, savePath, settingsBuilder270.ToSettings());
                System.Diagnostics.Debug.WriteLine($"[CreateMutableTorrentAsync] Engine.AddAsync successful for {initialIdentifier.ToHex()}. Manager State: {manager?.State}"); // Use Debug.WriteLine
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CreateMutableTorrentAsync] Engine.AddAsync FAILED for {initialIdentifier.ToHex()}: {ex.Message}"); // Use Debug.WriteLine
                 throw new TorrentException($"Failed to add created mutable torrent '{initialIdentifier.ToHex()}' to the engine.", ex);
             }

             if (manager == null)
             {
                 System.Diagnostics.Debug.WriteLine($"[CreateMutableTorrentAsync] Engine.AddAsync returned null for {initialIdentifier.ToHex()}."); // Use Debug.WriteLine
                 throw new TorrentException($"Failed to obtain TorrentManager for created mutable torrent '{initialIdentifier.ToHex()}'.");
             }

             _managedTorrents.Add(initialIdentifier, manager);
             _publicKeys[initialIdentifier] = publicKey;
            _mutableSequenceNumbers[initialIdentifier] = sequenceNumber;
            _lastKnownVDictionary[initialIdentifier] = vDictionary;
            _lastKnownSignature[initialIdentifier] = signature;
            string managerInfoHashHex = manager?.InfoHashes?.V1OrV2?.ToHex() ?? "N/A";
            System.Diagnostics.Debug.WriteLine($"[CreateMutableTorrentAsync] Subscribing TorrentManager ({managerInfoHashHex}) TorrentUpdateAvailable event for {initialIdentifier.ToHex()}."); // Use Debug.WriteLine
            manager.TorrentUpdateAvailable += OnTorrentUpdateAvailable;
            // Do not start the manager here. It will enter MetadataMode if needed
            // and start itself once metadata is retrieved, preventing the NullRef during DHT announce.
            System.Diagnostics.Debug.WriteLine($"[CreateMutableTorrentAsync] Manager added for {initialIdentifier.ToHex()}. It will start automatically after metadata retrieval if needed."); // Use Debug.WriteLine
 
            System.Diagnostics.Debug.WriteLine($"[CreateMutableTorrentAsync] Completed for {initialIdentifier.ToHex()}."); // Use Debug.WriteLine
            return (magnetLink, signingKeySeed, initialIdentifier);
        }

        /// <summary>
        /// Loads and starts managing a torrent from a magnet link.
        /// </summary>
        /// <param name="magnetLink">The magnet link (can be standard or BEP46 xs=).</param>
        /// <param name="savePath">The directory to save torrent data.</param>
        /// <returns>A handle or identifier for the managed torrent.</returns>
        public async Task<InfoHash> LoadTorrentAsync(MagnetLink magnetLink, string savePath)
        {
            System.Diagnostics.Debug.WriteLine($"[LoadTorrentAsync] Loading Magnet: {magnetLink}"); // Use Debug.WriteLine
            if (_engine == null) throw new InvalidOperationException("Service not initialized.");

            MonoTorrent.InfoHash identifier;
            byte[]? mutablePublicKey = null;

            if (magnetLink.InfoHashes != null && (magnetLink.InfoHashes.V1 != null || magnetLink.InfoHashes.V2 != null)) {
                identifier = magnetLink.InfoHashes.V1OrV2;
            } else if (!string.IsNullOrEmpty(magnetLink.PublicKeyHex)) {
                mutablePublicKey = Convert.FromHexString(magnetLink.PublicKeyHex);
                identifier = CalculateInfoHash(mutablePublicKey);
                System.Diagnostics.Debug.WriteLine($"[LoadTorrentAsync] Identified as Mutable Torrent. Identifier (SHA1(PubKey)): {identifier.ToHex()}"); // Use Debug.WriteLine
            } else {
                throw new ArgumentException("Magnet link does not contain a valid InfoHash or Public Key.", nameof(magnetLink));
            }

            if (_managedTorrents.ContainsKey(identifier))
            {
                System.Diagnostics.Debug.WriteLine($"[LoadTorrentAsync] Already managing {identifier.ToHex()}, returning."); // Use Debug.WriteLine
                return identifier;
            }

            System.Diagnostics.Debug.WriteLine($"[LoadTorrentAsync] Attempting Engine.AddAsync for {identifier.ToHex()}..."); // Use Debug.WriteLine
             // Pass specific settings only if it's a mutable torrent identified by public key
             TorrentSettings? torrentSettings = null;
             if (mutablePublicKey != null) {
                 var settingsBuilder333 = new TorrentSettingsBuilder();
                 settingsBuilder333.AllowDht = false;
                 torrentSettings = settingsBuilder333.ToSettings();
             }
             TorrentManager manager = await _engine.AddAsync(magnetLink, savePath, torrentSettings);
             System.Diagnostics.Debug.WriteLine($"[LoadTorrentAsync] Engine.AddAsync successful for {identifier.ToHex()}. Manager State: {manager?.State}"); // Use Debug.WriteLine
 
             if (manager == null)
             {
                 System.Diagnostics.Debug.WriteLine($"[LoadTorrentAsync] Engine.AddAsync returned null for {identifier.ToHex()}."); // Use Debug.WriteLine
                 throw new TorrentException($"Failed to obtain TorrentManager for loaded torrent '{identifier.ToHex()}'.");
             }

             _managedTorrents.Add(identifier, manager);
             if (mutablePublicKey != null)
             {
                 _publicKeys[identifier] = mutablePublicKey;
                 string loadedManagerInfoHashHex = manager?.InfoHashes?.V1OrV2?.ToHex() ?? "N/A";
                 System.Diagnostics.Debug.WriteLine($"[LoadTorrentAsync] Subscribing TorrentManager ({loadedManagerInfoHashHex}) events for mutable torrent {identifier.ToHex()}."); // Use Debug.WriteLine
                 manager.TorrentUpdateAvailable += OnTorrentUpdateAvailable;
                 manager.PeersFound += OnManagerPeersFound; // Subscribe to PeersFound
                 manager.PeerConnected += OnPeerConnected; // Subscribe to PeerConnected
                 manager.PeerDisconnected += OnPeerDisconnected; // Subscribe to PeerDisconnected
                 // Do not start the manager here. It will enter MetadataMode if needed
                 // and start itself once metadata is retrieved, preventing the NullRef during DHT announce.
                 System.Diagnostics.Debug.WriteLine($"[LoadTorrentAsync] Manager added for mutable {identifier.ToHex()}. It will start automatically after metadata retrieval if needed."); // Use Debug.WriteLine
             } else {
                 // Subscribe peer events for standard torrents too if desired
                 // manager.PeerConnected += OnPeerConnected;
                 // manager.PeerDisconnected += OnPeerDisconnected;
                 System.Diagnostics.Debug.WriteLine($"[LoadTorrentAsync] Completed loading for standard torrent {identifier.ToHex()}."); // Use Debug.WriteLine
             }
             return identifier;
        }

        /// <summary>
        /// Forces the underlying TorrentManager to perform a check for mutable torrent updates via DHT.
        /// </summary>
        /// <param name="torrentHandle">The identifier (SHA1 hash of public key) of the mutable torrent.</param>
        /// <returns>A task representing the asynchronous operation that returns the latest torrent state.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the service is not initialized or if the handle does not correspond to a mutable torrent.</exception>
        /// <exception cref="KeyNotFoundException">Thrown if the torrent handle is not found.</exception>
        public async Task<(long sequenceNumber, MonoTorrent.BEncoding.BEncodedDictionary vDictionary)> TriggerMutableUpdateCheckAsync(InfoHash torrentHandle)
        {
            if (_engine == null) throw new InvalidOperationException("Service not initialized.");
            if (!_managedTorrents.TryGetValue(torrentHandle, out var manager))
                throw new KeyNotFoundException($"Torrent handle not found or torrent is not managed: {torrentHandle.ToHex()}");
            if (!_publicKeys.ContainsKey(torrentHandle))
                throw new InvalidOperationException($"Torrent {torrentHandle.ToHex()} is not a mutable torrent.");

            System.Diagnostics.Debug.WriteLine($"[TriggerMutableUpdateCheckAsync] Triggering update check for {torrentHandle.ToHex()}"); // Use Debug.WriteLine
            System.Diagnostics.Debug.WriteLine($"[TriggerMutableUpdateCheckAsync] DHT State before check: {_engine.Dht?.State ?? DhtState.NotReady}"); // Use Debug.WriteLine
            try
            {
                await manager.ForceMutableUpdateCheckAsync();
                System.Diagnostics.Debug.WriteLine($"[TriggerMutableUpdateCheckAsync] ForceMutableUpdateCheckAsync call completed for {torrentHandle.ToHex()}"); // Use Debug.WriteLine
                
                // After forcing the update check, get the latest state from the DHT
                return await GetAndUpdateTorrentStateAsync(torrentHandle);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TriggerMutableUpdateCheckAsync] Update check FAILED for {torrentHandle.ToHex()}: {ex.Message}"); // Use Debug.WriteLine
                throw;
            }
        }

        /// <summary>
        /// Retrieves the public key associated with a mutable torrent handle.
        /// </summary>
        /// <param name="torrentHandle">The handle of the torrent.</param>
        /// <returns>The public key as a byte array.</returns>
        /// <exception cref="KeyNotFoundException">Thrown if the public key for the handle is not found.</exception>
        public byte[] GetPublicKeyForTorrent(MonoTorrent.InfoHash torrentHandle)
        {
            if (_publicKeys.TryGetValue(torrentHandle, out var publicKey))
            {
                return publicKey;
            }
            throw new KeyNotFoundException($"Could not find public key for the torrent handle '{torrentHandle.ToHex()}'. Ensure it was created or loaded by this service.");
        }

        /// <summary>
        /// Helper method to get the latest state from DHT and update local cache.
        /// </summary>
        /// <param name="torrentHandle">The handle of the torrent.</param>
        /// <returns>A tuple containing the sequence number and the vDictionary.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the service is not initialized or state cannot be retrieved.</exception>
        /// <exception cref="TorrentException">Thrown if the DHT Get operation fails.</exception>
        public async Task<(long sequenceNumber, MonoTorrent.BEncoding.BEncodedDictionary vDictionary)> GetAndUpdateTorrentStateAsync(MonoTorrent.InfoHash torrentHandle)
        {
            if (_engine == null) throw new InvalidOperationException("Service not initialized.");
            if (_engine.Dht == null) throw new InvalidOperationException("DHT Service is not available.");
 
            byte[] pubKeyBytes = GetPublicKeyForTorrent(torrentHandle);
            // Calculate the actual DHT target ID using the public key (and salt if applicable)
            // Salt is assumed null for this chat example based on CreateOrLoadMutableChatTorrentAsync
            var dhtTargetId = MonoTorrent.Dht.DhtEngine.CalculateMutableTargetId((BEncodedString)pubKeyBytes, null);
            System.Diagnostics.Debug.WriteLine($"[GetAndUpdateState] Handle/TargetID: {torrentHandle.ToHex()}, DHT Target NodeId: {dhtTargetId}"); // Use Debug.WriteLine
            System.Diagnostics.Debug.WriteLine($"[GetAndUpdateState] DHT State: {_engine.Dht.State}"); // Use Debug.WriteLine
 
            // --- START CHANGE ---
            long? seqToRequest = null;
            if (_mutableSequenceNumbers.TryGetValue(torrentHandle, out long knownSeq))
            {
                seqToRequest = knownSeq; // Request items newer than what we last processed/stored
                System.Diagnostics.Debug.WriteLine($"[GetAndUpdateState] Requesting sequence > {seqToRequest}");
            }
            else
            {
                 System.Diagnostics.Debug.WriteLine($"[GetAndUpdateState] No known sequence number for {torrentHandle.ToHex()}. Requesting latest.");
            }
            // --- END CHANGE ---

            (BEncodedValue? value, BEncodedString? publicKeyResult, BEncodedString? signature, long? sequenceNumber) result;
            try
            {
                 // --- MODIFIED LINE ---
                 System.Diagnostics.Debug.WriteLine($"[GetAndUpdateState] Calling DHT GetAsync for TargetID: {dhtTargetId} (Always requesting latest, seq=null)"); // Use Debug.WriteLine
                 result = await _engine.Dht.GetAsync(dhtTargetId, null); // Always request latest (seq = null)
                 // --- END MODIFIED LINE ---
                 System.Diagnostics.Debug.WriteLine($"[GetAndUpdateState] DHT GetAsync Result - Value Found: {result.value != null}, Seq Found: {result.sequenceNumber.HasValue}, Seq: {result.sequenceNumber?.ToString() ?? "N/A"}, Sig Found: {result.signature != null}"); // Use Debug.WriteLine
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[GetAndUpdateState] DHT GetAsync FAILED for TargetID {dhtTargetId}: {ex.Message}\nStackTrace: {ex.StackTrace}"); // Use Debug.WriteLine
                 throw new TorrentException($"Failed to get mutable torrent state from DHT for '{torrentHandle.ToHex()}'.", ex);
            }

            if (result.value is BEncodedDictionary vDict && result.sequenceNumber.HasValue)
            {
                _mutableSequenceNumbers[torrentHandle] = result.sequenceNumber.Value;
                _lastKnownVDictionary[torrentHandle] = vDict;
                if (result.signature != null)
                    _lastKnownSignature[torrentHandle] = result.signature.AsMemory().ToArray();

                return (result.sequenceNumber.Value, vDict);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[GetAndUpdateState] Could not retrieve valid mutable state for torrent '{torrentHandle.ToHex()}' from DHT. Result Value Type: {result.value?.GetType().Name ?? "null"}, Seq HasValue: {result.sequenceNumber.HasValue}"); // Use Debug.WriteLine
                throw new InvalidOperationException($"Could not retrieve valid mutable state for torrent '{torrentHandle.ToHex()}' from DHT.");
            }
        }

        /// <summary>
        /// Retrieves the BEncodedList representing the files from a given vDictionary.
        /// </summary>
        /// <param name="vDictionary">The vDictionary containing the file list.</param>
        /// <returns>The BEncodedList of files.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the 'files' key is not found or is not a list.</exception>
        private BEncodedList GetFileListFromVDictionary(BEncodedDictionary vDictionary)
        {
             if (vDictionary.TryGetValue("files", out var filesValue) && filesValue is BEncodedList fileList)
             {
                 return fileList;
             }
             throw new InvalidOperationException("Could not find 'files' list in the provided vDictionary.");
        }

        /// <summary>
        /// Adds a file or directory to an existing mutable torrent.
        /// </summary>
        /// <param name="torrentHandle">The handle of the torrent to modify (InfoHash of public key).</param>
        /// <param name="pathToAdd">Path to the file or directory to add.</param>
        /// <param name="signingKeySeed">The 32-byte non-secret private key seed for signing.</param>
        /// <returns>The new sequence number after the update.</returns>
        public async Task<long> AddFileToMutableTorrentAsync(MonoTorrent.InfoHash torrentHandle, string pathToAdd, byte[] signingKeySeed)
        {
            System.Diagnostics.Debug.WriteLine($"[AddFileToMutableTorrentAsync] START: Adding '{Path.GetFileName(pathToAdd)}' to torrent {torrentHandle.ToHex()}"); // Use Debug.WriteLine
            if (_engine == null) throw new InvalidOperationException("Service not initialized.");
            if (signingKeySeed == null || signingKeySeed.Length != 32) throw new ArgumentException("Invalid signing key seed provided.", nameof(signingKeySeed));
            if (!_managedTorrents.TryGetValue(torrentHandle, out var manager)) throw new KeyNotFoundException("Torrent handle not found or torrent is not managed.");
            if (!_publicKeys.TryGetValue(torrentHandle, out var publicKey)) throw new InvalidOperationException($"Torrent {torrentHandle.ToHex()} is not a known mutable torrent.");

            long currentSequenceNumber;
            BEncodedDictionary currentVDictionary;
            if (_mutableSequenceNumbers.TryGetValue(torrentHandle, out currentSequenceNumber) && _lastKnownVDictionary.TryGetValue(torrentHandle, out currentVDictionary))
            {
                System.Diagnostics.Debug.WriteLine($"[AddFileToMutableTorrentAsync] Using cached state Seq: {currentSequenceNumber}"); // Use Debug.WriteLine
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[AddFileToMutableTorrentAsync] Fetching current state from DHT for {torrentHandle.ToHex()}..."); // Use Debug.WriteLine
                (currentSequenceNumber, currentVDictionary) = await GetAndUpdateTorrentStateAsync(torrentHandle);
                System.Diagnostics.Debug.WriteLine($"[AddFileToMutableTorrentAsync] Fetched state Seq: {currentSequenceNumber}"); // Use Debug.WriteLine
            }

            BEncodedList currentFileList;
            try
            {
                currentFileList = GetFileListFromVDictionary(currentVDictionary);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not parse file list from vDictionary for torrent {torrentHandle.ToHex()}.", ex);
            }
            int pieceLength = (int)((MonoTorrent.BEncoding.BEncodedNumber)currentVDictionary["piece length"]).Number;
            string torrentName = ((MonoTorrent.BEncoding.BEncodedString)currentVDictionary["name"]).Text;

            long nextSequenceNumber = currentSequenceNumber + 1;
            var newFileSource = new MonoTorrent.TorrentFileSource(pathToAdd);
            var newFileListBEncoded = new MonoTorrent.BEncoding.BEncodedList(currentFileList);

            foreach (var newFileMapping in newFileSource.Files)
            {
                 bool alreadyExists = currentFileList.Cast<MonoTorrent.BEncoding.BEncodedDictionary>()
                     .Any(existingDict =>
                         existingDict.TryGetValue("path", out var pathValue) && pathValue is MonoTorrent.BEncoding.BEncodedList pathList &&
                         pathList.Cast<MonoTorrent.BEncoding.BEncodedString>().Select(s => s.Text).ToArray().SequenceEqual(newFileMapping.Destination.Parts.ToArray())
                     );

                 if (!alreadyExists)
                 {
                     newFileListBEncoded.Add(new MonoTorrent.BEncoding.BEncodedDictionary {
                         { "path", new MonoTorrent.BEncoding.BEncodedList(newFileMapping.Destination.Parts.ToArray().Select(s => (MonoTorrent.BEncoding.BEncodedString)s)) },
                         { "length", new MonoTorrent.BEncoding.BEncodedNumber(newFileMapping.Length) }
                     });
                 } else {
                      System.Diagnostics.Debug.WriteLine($"[AddFileToMutableTorrentAsync] Skipped adding duplicate path: {newFileMapping.Destination}"); // Use Debug.WriteLine
                 }
            }

            var newVDictionary = new MonoTorrent.BEncoding.BEncodedDictionary {
                { "name", (MonoTorrent.BEncoding.BEncodedString)torrentName },
                { "piece length", new MonoTorrent.BEncoding.BEncodedNumber(pieceLength) },
                { "files", newFileListBEncoded }
            };

            byte[] dataToSign = ConstructDataToSign(nextSequenceNumber, newVDictionary);
            byte[] newSignature = SignData(dataToSign, signingKeySeed);

            try
            {
                 System.Diagnostics.Debug.WriteLine($"[AddFileToMutableTorrentAsync] Attempting DHT Put for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}"); // Use Debug.WriteLine
                 await _engine.Dht.PutMutableAsync(
                    publicKey: (BEncodedString)publicKey,
                    salt: null,
                    value: newVDictionary,
                    sequenceNumber: nextSequenceNumber,
                    signature: (BEncodedString)newSignature
                );
                System.Diagnostics.Debug.WriteLine($"[AddFileToMutableTorrentAsync] DHT Put successful for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}"); // Use Debug.WriteLine
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[AddFileToMutableTorrentAsync] DHT Put FAILED for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}: {ex.Message}"); // Use Debug.WriteLine
                 throw new TorrentException($"Failed to put mutable torrent update (AddFile) to DHT for '{torrentHandle.ToHex()}'.", ex);
            }

            _mutableSequenceNumbers[torrentHandle] = nextSequenceNumber;
            _lastKnownVDictionary[torrentHandle] = newVDictionary;
            _lastKnownSignature[torrentHandle] = newSignature;
            System.Diagnostics.Debug.WriteLine($"[AddFileToMutableTorrentAsync] END: Completed for {torrentHandle.ToHex()}, returning Seq: {nextSequenceNumber}"); // Use Debug.WriteLine
            return nextSequenceNumber;
        } // Add closing brace for the method


        /// <summary>
        /// Updates an existing file within a mutable torrent. (Simplified: Remove then Add)
        /// </summary>
        /// <param name="torrentHandle">The handle of the torrent to modify.</param>
        /// <param name="filePathToUpdate">Path of the file to update (relative to torrent root).</param>
        /// <param name="newFilePath">Path to the new version of the file on local disk.</param>
        /// <param name="signingKeySeed">The 32-byte non-secret private key seed for signing.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task UpdateFileInMutableTorrentAsync(MonoTorrent.InfoHash torrentHandle, string filePathToUpdate, string newFilePath, byte[] signingKeySeed)
        {
            // Note: This simplified version increments sequence number twice.
            // A more optimized version would combine remove and add logic into one DHT Put.
            await RemoveFileFromMutableTorrentAsync(torrentHandle, filePathToUpdate, signingKeySeed);
            await AddFileToMutableTorrentAsync(torrentHandle, newFilePath, signingKeySeed);
        }

        /// <summary>
        /// Updates the BEncodedDictionary data for an existing mutable torrent.
        /// </summary>
        /// <param name="torrentHandle">The handle of the torrent to modify (InfoHash of public key).</param>
        /// <param name="newVDictionary">The new BEncodedDictionary representing the torrent state.</param>
        /// <param name="signingKeySeed">The 32-byte non-secret private key seed for signing.</param>
        /// <param name="salt">Optional salt used when creating/joining the torrent.</param>
        /// <returns>The new sequence number after the update.</returns>
        public async Task<long> UpdateMutableDataAsync(InfoHash torrentHandle, BEncodedDictionary newVDictionary, byte[] signingKeySeed, byte[]? salt = null)
        {
            System.Diagnostics.Debug.WriteLine($"[UpdateMutableDataAsync] START: Updating data for torrent {torrentHandle.ToHex()}"); // Use Debug.WriteLine
            if (_engine == null) throw new InvalidOperationException("Service not initialized.");
            if (signingKeySeed == null || signingKeySeed.Length != 32) throw new ArgumentException("Invalid signing key seed provided.", nameof(signingKeySeed));
            if (!_managedTorrents.TryGetValue(torrentHandle, out var manager)) throw new KeyNotFoundException("Torrent handle not found or torrent is not managed.");
            if (!_publicKeys.TryGetValue(torrentHandle, out var publicKey)) throw new InvalidOperationException($"Torrent {torrentHandle.ToHex()} is not a known mutable torrent.");

            // Fetch the latest state *first* to get the sequence number for CAS
            long latestSequenceNumber;
            BEncodedDictionary latestVDictionary;
            try
            {
                 System.Diagnostics.Debug.WriteLine($"[UpdateMutableDataAsync] Fetching latest state for CAS for {torrentHandle.ToHex()}..."); // Use Debug.WriteLine
                 (latestSequenceNumber, latestVDictionary) = await GetAndUpdateTorrentStateAsync(torrentHandle); // This updates local cache too
                 System.Diagnostics.Debug.WriteLine($"[UpdateMutableDataAsync] Fetched latest state for CAS. Seq: {latestSequenceNumber}"); // Use Debug.WriteLine
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[UpdateMutableDataAsync] Failed to fetch latest state for CAS for {torrentHandle.ToHex()}: {ex.Message}. Aborting Put."); // Use Debug.WriteLine
                 throw new TorrentException($"Failed to fetch latest state before Put for '{torrentHandle.ToHex()}'.", ex);
            }

            // Now modify the dictionary *based on the latest fetched state*
            // It's crucial that newVDictionary is based on the *actual* latest state,
            // otherwise the CAS might succeed but data loss could still occur if the
            // caller didn't provide a dictionary derived from the latest state.
            // For the chat example, the caller *does* fetch before calling this,
            // but this CAS adds an extra layer of safety.
            // We will proceed assuming the caller provided a correctly updated newVDictionary
            // based on the state they fetched just before calling this method.

            long nextSequenceNumber = latestSequenceNumber + 1;
            BEncodedString? saltBEncoded = salt == null ? null : (BEncodedString)salt;

            byte[] dataToSign = ConstructDataToSign(nextSequenceNumber, newVDictionary, saltBEncoded);
            byte[] newSignature = SignData(dataToSign, signingKeySeed);

            var pubKeyBencoded = (BEncodedString)publicKey; // Reuse for logging and Put
            var sigBencoded = (BEncodedString)newSignature; // Reuse for logging and Put
            // Note: PutMutableAsync uses the public key directly, not its hash, for internal routing/storage mapping.
            System.Diagnostics.Debug.WriteLine($"[UpdateMutableDataAsync] Preparing DHT Put for PubKey: {pubKeyBencoded.ToHex()} | TargetID (PubKeyHash): {torrentHandle.ToHex()} | Seq: {nextSequenceNumber} | Salt: {saltBEncoded?.ToHex() ?? "null"} | Sig: {sigBencoded.ToHex()} | VDict Keys: {string.Join(",", newVDictionary.Keys.Select(k => k.Text))}"); // Use Debug.WriteLine
 
            bool putSucceeded = false; // Track success
            try
            {
                 System.Diagnostics.Debug.WriteLine($"[UpdateMutableDataAsync] >>> Calling DHT PutMutableAsync (awaiting)..."); // Log BEFORE await

                 // Use the fetched latestSequenceNumber as the CAS value
                 await _engine.Dht.PutMutableAsync(
                    publicKey: pubKeyBencoded,
                    salt: saltBEncoded,
                    value: newVDictionary,
                    sequenceNumber: nextSequenceNumber,
                    signature: sigBencoded,
                    cas: latestSequenceNumber // Add CAS parameter
                );

                // Log success immediately after await returns without exception
                System.Diagnostics.Debug.WriteLine($"[UpdateMutableDataAsync] <<< DHT PutMutableAsync call completed SUCCESSFULLY for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}"); // Log AFTER await (success path)
                putSucceeded = true;
            }
            catch (Exception ex)
            {
                 // Log the specific failure reason immediately
                 System.Diagnostics.Debug.WriteLine($"[UpdateMutableDataAsync] <<< *** DHT PutMutableAsync call FAILED for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}: {ex.GetType().Name} - {ex.Message} ***"); // Log AFTER await (failure path)
                 // Re-throw but wrap in TorrentException for consistency if it's not already one
                 if (ex is TorrentException) throw;
                 throw new TorrentException($"Failed to put mutable torrent update (UpdateData) to DHT for '{torrentHandle.ToHex()}'. See inner exception.", ex);
            }

            // Only update local state if the PUT succeeded
            if (!putSucceeded)
            {
                System.Diagnostics.Debug.WriteLine($"[UpdateMutableDataAsync] Skipping local state update for {torrentHandle.ToHex()} due to failed PUT."); // Use Debug.WriteLine
                // Depending on desired behavior, you might want to re-throw here or return a specific failure indicator
                // For now, the exception thrown in the catch block handles failure propagation.
                // If the catch block didn't re-throw, you'd need to handle the failure here.
                // Example: return -1; // Or throw new InvalidOperationException("DHT Put failed");
            }

            // Update local state only on successful PUT
            if (putSucceeded)
            {
                _mutableSequenceNumbers[torrentHandle] = nextSequenceNumber;
                _lastKnownVDictionary[torrentHandle] = newVDictionary;
                _lastKnownSignature[torrentHandle] = newSignature;
            }

            System.Diagnostics.Debug.WriteLine($"[UpdateMutableDataAsync] END: Completed for {torrentHandle.ToHex()}, returning Seq: {nextSequenceNumber}"); // Use Debug.WriteLine
            return nextSequenceNumber; // Return the sequence number regardless of PUT success? Or only on success? Returning always for now.
        }

        /// <summary>
        /// Gets the list of files within a loaded torrent.
        /// </summary>
        /// <param name="torrentHandle">The handle of the torrent.</param>
        /// <returns>A list of file paths relative to the torrent root.</returns>
        public Task<IEnumerable<string>> GetTorrentFilesAsync(InfoHash torrentHandle)
        {
             if (!_managedTorrents.TryGetValue(torrentHandle, out var manager))
            {
                throw new KeyNotFoundException("Torrent handle not found or torrent is not managed.");
            }

            if (manager.Torrent == null)
            {
                throw new InvalidOperationException("Torrent metadata not yet available.");
            }
            return Task.FromResult(manager.Torrent.Files.Select(f => f.Path.ToString()));
        }

        /// <summary>
        /// Tries to get the TorrentManager associated with a given InfoHash handle.
        /// </summary>
        /// <param name="torrentHandle">The InfoHash handle.</param>
        /// <param name="manager">The TorrentManager if found, otherwise null.</param>
        /// <returns>True if the manager was found, false otherwise.</returns>
        public bool TryGetManager(InfoHash torrentHandle, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TorrentManager manager)
        {
            // Use the actual key used for storage (which is the pubkey hash for mutable)
            return _managedTorrents.TryGetValue(torrentHandle, out manager);
        }

        // Event handler for TorrentManager's update event
        private void OnTorrentUpdateAvailable(object? sender, TorrentUpdateEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[TorrentService {this.GetHashCode()}] Entered OnTorrentUpdateAvailable."); // Use Debug.WriteLine
            string senderType = sender?.GetType().Name ?? "null";
            string newInfoHashHex = e.NewInfoHash?.ToHex() ?? "null";
            System.Diagnostics.Debug.WriteLine($"[OnTorrentUpdateAvailable] Event received. Sender Type: {senderType}, NewInfoHash: {newInfoHashHex}"); // Use Debug.WriteLine
 
            if (sender is TorrentManager manager)
            {
                InfoHash? originalInfoHash = null;
                foreach (var kvp in _managedTorrents)
                {
                    if (kvp.Value == manager)
                    {
                        if (_publicKeys.ContainsKey(kvp.Key))
                        {
                            originalInfoHash = kvp.Key;
                            break;
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[OnTorrentUpdateAvailable] Found OriginalInfoHash: {originalInfoHash?.ToHex() ?? "null"} for Manager: {manager?.InfoHashes?.V1OrV2?.ToHex() ?? "N/A"}"); // Use Debug.WriteLine
                if (originalInfoHash != null)
                 {
                     if (e.NewInfoHash != null)
                     {
                         var customArgs = new MutableTorrentUpdateInfoEventArgs(originalInfoHash, e.NewInfoHash);
                         System.Diagnostics.Debug.WriteLine($"[OnTorrentUpdateAvailable] Invoking MutableTorrentUpdateAvailable for {originalInfoHash.ToHex()} -> {customArgs.NewInfoHash.ToHex()}"); // Use Debug.WriteLine
                         MutableTorrentUpdateAvailable?.Invoke(this, customArgs);
                     }
                     else
                     {
                         System.Diagnostics.Debug.WriteLine($"[OnTorrentUpdateAvailable] Warning: NewInfoHash was null for OriginalInfoHash {originalInfoHash.ToHex()}. Cannot invoke event."); // Use Debug.WriteLine
                     }
                 }
                 else
                {
                    System.Diagnostics.Debug.WriteLine($"[OnTorrentUpdateAvailable] Warning: Could not find OriginalInfoHash for sender manager {manager.InfoHashes.V1OrV2.ToHex()}."); // Use Debug.WriteLine
                }
            }
        }

       private async void OnManagerPeersFound(object? sender, PeersAddedEventArgs e)
       {
           // We are interested specifically when local peers are found for our mutable torrent
           if (e is LocalPeersAdded localPeers && _publicKeys.ContainsKey(localPeers.TorrentManager.InfoHashes.V1OrV2)) // Check if it's a mutable torrent we track
           {
               var handle = localPeers.TorrentManager.InfoHashes.V1OrV2;
               System.Diagnostics.Debug.WriteLine($"[OnManagerPeersFound] Local peer found for mutable torrent {handle.ToHex()}. Triggering state check.");
               // Fire-and-forget to avoid blocking the event handler
               _ = Task.Run(async () => {
                   try
                   {
                       // Fetch the latest state. This updates our internal cache (_mutableSequenceNumbers, _lastKnownVDictionary)
                       await GetAndUpdateTorrentStateAsync(handle);
                       // We don't necessarily need to raise MutableTorrentUpdateAvailable here,
                       // as the periodic timer or subsequent operations should pick up the cached state.
                       // Alternatively, could compare seq numbers and raise if newer. For now, just update cache.
                       System.Diagnostics.Debug.WriteLine($"[OnManagerPeersFound] State check completed for {handle.ToHex()} after local peer found.");
                   }
                   catch (Exception ex)
                   {
                       System.Diagnostics.Debug.WriteLine($"[OnManagerPeersFound] Error during state check after local peer found for {handle.ToHex()}: {ex.Message}");
                   }
               });
           }
       }

       // --- Peer Connection Event Handlers ---

       private void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
       {
           // Log the connection event
           System.Diagnostics.Debug.WriteLine($"[TorrentService PEER CONNECT] Connected to peer: {e.Peer.Uri} for torrent {e.TorrentManager.InfoHashes.V1OrV2.ToHex()}");
           // Optionally, add logic here if needed for all torrents
       }

       private void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
       {
           // Log the disconnection event
           System.Diagnostics.Debug.WriteLine($"[TorrentService PEER DISCONNECT] Disconnected from peer: {e.Peer.Uri} for torrent {e.TorrentManager.InfoHashes.V1OrV2.ToHex()}. Reason: {e.Reason}");
           // Optionally, add logic here if needed for all torrents
       }

        /// <summary>
        /// Joins a shared mutable torrent based on a predefined seed, creating it if it doesn't exist.
        /// </summary>
        /// <param name="sharedTorrentName">A descriptive name for the shared torrent (used if creating).</param>
        /// <param name="sharedSeed">The 32-byte private key seed unique to this shared torrent.</param>
        /// <param name="savePath">The directory to save torrent data.</param>
        /// <param name="salt">Optional salt for the mutable torrent (BEP46).</param>
        /// <returns>The InfoHash identifier (SHA1 of public key) for the shared torrent.</returns>
        public async Task<InfoHash> JoinOrCreateMutableTorrentAsync(string sharedTorrentName, byte[] sharedSeed, string savePath, byte[]? salt = null)
        {
            if (_engine == null) throw new InvalidOperationException("Service not initialized.");
            if (sharedSeed == null || sharedSeed.Length != 32) throw new ArgumentException("Shared seed must be 32 bytes.", nameof(sharedSeed));
            if (salt != null && salt.Length > 64) throw new ArgumentException("Salt cannot be longer than 64 bytes", nameof(salt));
            if (string.IsNullOrEmpty(savePath)) throw new ArgumentNullException(nameof(savePath));
            Directory.CreateDirectory(savePath);

            byte[] publicKey = GetPublicKeyFromSeed(sharedSeed);
            InfoHash targetId = CalculateInfoHash(publicKey);
            BEncodedString? saltBEncoded = salt == null ? null : (BEncodedString)salt;
            string publicKeyHex = Convert.ToHexString(publicKey).ToLowerInvariant();

            System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Torrent: '{sharedTorrentName}', TargetID: {targetId.ToHex()}"); // Use Debug.WriteLine

            if (_managedTorrents.ContainsKey(targetId))
            {
                System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Already managing {targetId.ToHex()}."); // Use Debug.WriteLine
                return targetId;
            }

            (BEncodedValue? value, BEncodedString? publicKey, BEncodedString? signature, long? sequenceNumber) dhtResult;
            BEncodedDictionary? vDictionary = null;
            long currentSequenceNumber = -1;
            byte[]? currentSignature = null;

            try
            {
                System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Attempting DHT Get for {targetId.ToHex()}..."); // Use Debug.WriteLine
                var dhtTarget = MonoTorrent.Dht.DhtEngine.CalculateMutableTargetId((BEncodedString)publicKey, saltBEncoded);
                dhtResult = await _engine.Dht.GetAsync(dhtTarget);

                if (dhtResult.value is BEncodedDictionary dict && dhtResult.sequenceNumber.HasValue)
                {
                    vDictionary = dict;
                    currentSequenceNumber = dhtResult.sequenceNumber.Value;
                    currentSignature = dhtResult.signature?.AsMemory().ToArray();
                    System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Found existing state on DHT. Seq: {currentSequenceNumber}"); // Use Debug.WriteLine
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] State not found on DHT."); // Use Debug.WriteLine
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] DHT Get failed for {targetId.ToHex()}: {ex.Message}. Assuming torrent needs creation."); // Use Debug.WriteLine
            }

            TorrentManager manager;
            MagnetLink magnetLink;

            if (vDictionary != null && currentSequenceNumber >= 0 && currentSignature != null)
            {
                string nameFromDht = sharedTorrentName;
                if (vDictionary.TryGetValue("name", out var nameVal) && nameVal is BEncodedString nameStr)
                {
                    nameFromDht = nameStr.Text;
                }
                magnetLink = new MagnetLink(publicKeyHex, salt == null ? null : Convert.ToHexString(salt).ToLowerInvariant(), name: nameFromDht);
                System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Loading existing torrent using Magnet: {magnetLink}"); // Use Debug.WriteLine
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[JoinOrCreateMutable] Creating new torrent state (Seq 0)."); // Use Debug.WriteLine
                currentSequenceNumber = 0;
                int pieceLength = CalculatePieceLength(0);

                vDictionary = new BEncodedDictionary {
                    { "name", (BEncodedString)sharedTorrentName },
                    { "piece length", new BEncodedNumber(pieceLength) },
                    { "files", new BEncodedList() }
                };

                byte[] dataToSign = ConstructDataToSign(currentSequenceNumber, vDictionary, saltBEncoded);
                currentSignature = SignData(dataToSign, sharedSeed);

                try
                {
                    System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Attempting initial DHT Put for {targetId.ToHex()} | Seq: {currentSequenceNumber}"); // Use Debug.WriteLine
                    await _engine.Dht.PutMutableAsync(
                        publicKey: (BEncodedString)publicKey,
                        salt: saltBEncoded,
                        value: vDictionary,
                        sequenceNumber: currentSequenceNumber,
                        signature: (BEncodedString)currentSignature
                    );
                    System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Initial DHT Put successful for {targetId.ToHex()}"); // Use Debug.WriteLine
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Initial DHT Put FAILED for {targetId.ToHex()}: {ex.Message}"); // Use Debug.WriteLine
                    throw new TorrentException($"Failed to put initial shared torrent data to DHT for '{targetId.ToHex()}'.", ex);
                }
 
                // Use the provided sharedTorrentName for the magnet link
                magnetLink = new MagnetLink(publicKeyHex, salt == null ? null : Convert.ToHexString(salt).ToLowerInvariant(), name: sharedTorrentName);
                System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Created new torrent using Magnet: {magnetLink}"); // Use Debug.WriteLine
            }

            try
            {
                System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Attempting Engine.AddAsync for {targetId.ToHex()}..."); // Use Debug.WriteLine
                // Prevent manager from interacting with DHT initially for mutable torrents
                var settingsBuilder913 = new TorrentSettingsBuilder();
                settingsBuilder913.AllowDht = false;
                manager = await _engine.AddAsync(magnetLink, savePath, settingsBuilder913.ToSettings());
                System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Engine.AddAsync successful for {targetId.ToHex()}. Manager State: {manager?.State}"); // Use Debug.WriteLine
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Engine.AddAsync FAILED for {targetId.ToHex()}: {ex.Message}"); // Use Debug.WriteLine
                throw new TorrentException($"Failed to add shared torrent '{targetId.ToHex()}' to the engine.", ex);
            }
 
            if (manager == null)
            {
                System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Engine.AddAsync returned null for {targetId.ToHex()}."); // Use Debug.WriteLine
                throw new TorrentException($"Failed to obtain TorrentManager for shared torrent '{targetId.ToHex()}'.");
            }

            _managedTorrents.Add(targetId, manager);
            _publicKeys[targetId] = publicKey;
            _mutableSequenceNumbers[targetId] = currentSequenceNumber;
            _lastKnownVDictionary[targetId] = vDictionary;
            if (currentSignature != null)
            {
                _lastKnownSignature[targetId] = currentSignature;
            } else {
                 System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Warning: Signature was null when storing state for {targetId.ToHex()}."); // Use Debug.WriteLine
                 _lastKnownSignature[targetId] = new byte[64]; // Placeholder
            }
 
            // *** Re-enable DHT for the manager now that we have the initial state ***
            // This allows it to participate in finding updates for the mutable torrent.
            var settingsBuilder949 = new TorrentSettingsBuilder(manager.Settings);
            settingsBuilder949.AllowDht = true;
            await manager.UpdateSettingsAsync(settingsBuilder949.ToSettings());
            System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Enabled DHT for manager {targetId.ToHex()}."); // Use Debug.WriteLine
 
            string managerInfoHashHex = manager?.InfoHashes?.V1OrV2?.ToHex() ?? "N/A";
            System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Subscribing TorrentManager ({managerInfoHashHex}) TorrentUpdateAvailable event for {targetId.ToHex()}.");
            manager.TorrentUpdateAvailable += OnTorrentUpdateAvailable;
            System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Subscribing TorrentManager ({managerInfoHashHex}) PeersFound event for {targetId.ToHex()}.");
            manager.PeersFound += OnManagerPeersFound; // Subscribe to PeersFound
            // The manager should now be able to participate in DHT correctly.
            // Let the engine manage starting implicitly. Remove explicit StartAsync/DhtAnnounceAsync.
            System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Manager added and configured for {targetId.ToHex()}. It will start automatically if needed.");
            // await manager.StartAsync(); // REMOVED - Let engine handle startup
            // _ = manager.DhtAnnounceAsync(); // REMOVED - Announce will happen automatically
            // System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Explicitly started manager and triggered initial DHT announce for {targetId.ToHex()}. Current State: {manager.State}"); // REMOVED
            System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Manager explicitly started for {targetId.ToHex()}. Current State: {manager.State}");

            System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Completed for {targetId.ToHex()}."); // Use Debug.WriteLine
            return targetId;
        }


        // --- Private Helper Methods ---

        private byte[] GeneratePrivateKeySeed()
        {
            var secureRandom = new BCrypt::Org.BouncyCastle.Security.SecureRandom();
            var keyPairGenerator = BCrypt::Org.BouncyCastle.Security.GeneratorUtilities.GetKeyPairGenerator("Ed25519");
            keyPairGenerator.Init(new BCrypt::Org.BouncyCastle.Crypto.Parameters.Ed25519KeyGenerationParameters(secureRandom));
            var keyPair = keyPairGenerator.GenerateKeyPair();
            var privateKeyParams = (BCrypt::Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters)keyPair.Private;
            return privateKeyParams.GetEncoded().AsSpan(0, 32).ToArray();
        }
        // Make public so DebugChatPage can call it
        public byte[] GetPublicKeyFromSeed(byte[] seed)
        {
            if (seed == null || seed.Length != 32)
                throw new ArgumentException("Seed must be 32 bytes.", nameof(seed));

            var privateKeyParams = new BCrypt::Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(seed, 0);
            var publicKeyParams = privateKeyParams.GeneratePublicKey();
            return publicKeyParams.GetEncoded();
        }

        private InfoHash CalculateInfoHash(byte[] publicKey)
        {
            using (var sha1 = SHA1.Create())
            {
                return new InfoHash(sha1.ComputeHash(publicKey));
            }
        }

        private byte[] SignData(byte[] dataToSign, byte[] signingKeySeed)
        {
            if (signingKeySeed == null || signingKeySeed.Length != 32)
                throw new ArgumentException("Signing key seed must be 32 bytes.", nameof(signingKeySeed));

            var signer = new BCrypt::Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
            var signingPrivateKeyParams = new BCrypt::Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(signingKeySeed, 0);
            signer.Init(true, signingPrivateKeyParams);
            signer.BlockUpdate(dataToSign, 0, dataToSign.Length);
            return signer.GenerateSignature();
        }

        private byte[] ConstructDataToSign(long sequenceNumber, BEncodedDictionary vDictionary, BEncodedString? salt = null)
        {
            var seqKey = new BEncodedString("seq");
            var seqValue = new BEncodedNumber(sequenceNumber);
            var vKey = new BEncodedString("v");
            var encodedVDictionary = vDictionary.Encode();

            int totalLength;
            BEncodedString? saltKey = null;

            if (salt != null)
            {
                saltKey = new BEncodedString("salt");
                totalLength = saltKey.LengthInBytes() + salt.LengthInBytes() + seqKey.LengthInBytes() + seqValue.LengthInBytes() + vKey.LengthInBytes() + encodedVDictionary.Length;
            }
            else
            {
                totalLength = seqKey.LengthInBytes() + seqValue.LengthInBytes() + vKey.LengthInBytes() + encodedVDictionary.Length;
            }

            var dataToSign = new byte[totalLength];
            int offset = 0;

            if (saltKey != null && salt != null)
            {
                offset += saltKey.Encode(dataToSign.AsSpan(offset));
                offset += salt.Encode(dataToSign.AsSpan(offset));
            }

            offset += seqKey.Encode(dataToSign.AsSpan(offset));
            offset += seqValue.Encode(dataToSign.AsSpan(offset));
            offset += vKey.Encode(dataToSign.AsSpan(offset));
            offset += vDictionary.Encode(dataToSign.AsSpan(offset));

            return dataToSign;
        }

        /// <summary>
        /// Stops the MonoTorrent ClientEngine and releases resources.
        /// </summary>
        public async ValueTask DisposeAsync() // Correctly implements IAsyncDisposable
        {
            if (_engine != null)
            {
                foreach(var kvp in _managedTorrents)
                {
                    // Unsubscribe from all events
                    kvp.Value.TorrentUpdateAvailable -= OnTorrentUpdateAvailable;
                    kvp.Value.PeersFound -= OnManagerPeersFound;
                    kvp.Value.PeerConnected -= OnPeerConnected;
                    kvp.Value.PeerDisconnected -= OnPeerDisconnected;
                }
                // Save DHT nodes before stopping
                if (_engine.Dht is IDhtEngine dhtEngine && !string.IsNullOrEmpty(_dhtNodesFilePath))
                {
                    try
                    {
                        ReadOnlyMemory<byte> nodesToSave = await dhtEngine.SaveNodesAsync();
                        if (!nodesToSave.IsEmpty)
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(_dhtNodesFilePath)!);
                            await File.WriteAllBytesAsync(_dhtNodesFilePath, nodesToSave.ToArray());
                            System.Diagnostics.Debug.WriteLine($"[TorrentService] Saved {nodesToSave.Length} bytes of DHT nodes to {_dhtNodesFilePath}"); // Use Debug.WriteLine
                        }
                    }
                    catch (Exception ex)
                    {
                         System.Diagnostics.Debug.WriteLine($"[TorrentService] Warning: Failed to save DHT nodes to {_dhtNodesFilePath}: {ex.Message}"); // Use Debug.WriteLine
                    }
                }

                await _engine.StopAllAsync();
                _engine.Dispose();
                _engine = null;
            }
        }

        private static int CalculatePieceLength(long totalSize)
        {
            const int minPieceLength = 16 * 1024; // 16 KiB
            const int maxPieceLength = 4 * 1024 * 1024; // 4 MiB
            const int targetPieces = 1500;

            if (totalSize == 0) return minPieceLength;

            long idealPieceLength = totalSize / targetPieces;

            int powerOf2Length = minPieceLength;
            while (powerOf2Length < idealPieceLength && powerOf2Length < maxPieceLength)
            {
                powerOf2Length *= 2;
            }
            return Math.Clamp(powerOf2Length, minPieceLength, maxPieceLength);
        }

        public async Task<InfoHash> CreateOrLoadMutableChatTorrentAsync(byte[] publicKey, byte[] privateKeySeed, string savePath)
        {
            if (_engine == null) throw new InvalidOperationException("Service not initialized.");

            var targetId = CalculateInfoHash(publicKey);

            if (_managedTorrents.ContainsKey(targetId))
                return targetId;

            // Use the hardcoded name for this specific chat torrent method
            var magnetLink = new MagnetLink(Convert.ToHexString(publicKey).ToLowerInvariant(), name: "MutableChatTorrent");

            // Create initial empty chat state
            // Use the provided name for the initial dictionary
            var vDict = new BEncodedDictionary
            {
                { "name", (BEncodedString)"MutableChatTorrent" }, // Use hardcoded name
                { "piece length", new BEncodedNumber(16384) },
                { "files", new BEncodedList() },
                { "messages", new BEncodedList() } // Add messages key for chat
            };

            byte[] signature;
            long sequenceNumber = 0;
            {
                var dataToSign = ConstructDataToSign(sequenceNumber, vDict);
                signature = SignData(dataToSign, privateKeySeed);
            }

            // Put initial state to DHT
            await _engine.Dht.PutMutableAsync(
                publicKey: (BEncodedString)publicKey,
                salt: null,
                value: vDict,
                sequenceNumber: sequenceNumber,
                signature: (BEncodedString)signature
            );

            // Add torrent to engine
            var settingsBuilder = new TorrentSettingsBuilder();
            settingsBuilder.AllowDht = true;
            var manager = await _engine.AddAsync(magnetLink, savePath, settingsBuilder.ToSettings());
            _managedTorrents[targetId] = manager;
            _publicKeys[targetId] = publicKey;
            _mutableSequenceNumbers[targetId] = sequenceNumber;
            _lastKnownVDictionary[targetId] = vDict;
            _lastKnownSignature[targetId] = signature;

            manager.TorrentUpdateAvailable += OnTorrentUpdateAvailable;
            await manager.StartAsync();

            return targetId;
        }

        /// <summary>
        /// Removes a file or directory from an existing mutable torrent.
        /// </summary>
        /// <param name="torrentHandle">The handle of the torrent to modify (InfoHash of public key).</param>
        /// <param name="pathToRemove">Path to the file or directory to remove (relative to torrent root).</param>
        /// <param name="signingKeySeed">The 32-byte non-secret private key seed for signing.</param>
        /// <returns>The new sequence number after the update.</returns>
        public async Task<long> RemoveFileFromMutableTorrentAsync(MonoTorrent.InfoHash torrentHandle, string pathToRemove, byte[] signingKeySeed)
        {
            System.Diagnostics.Debug.WriteLine($"[RemoveFileFromMutableTorrentAsync] START: Removing '{pathToRemove}' from torrent {torrentHandle.ToHex()}"); // Use Debug.WriteLine
            if (_engine == null) throw new InvalidOperationException("Service not initialized.");
            if (signingKeySeed == null || signingKeySeed.Length != 32) throw new ArgumentException("Invalid signing key seed provided.", nameof(signingKeySeed));
            if (!_managedTorrents.TryGetValue(torrentHandle, out var manager)) throw new KeyNotFoundException("Torrent handle not found or torrent is not managed.");
            if (!_publicKeys.TryGetValue(torrentHandle, out var publicKey)) throw new InvalidOperationException($"Torrent {torrentHandle.ToHex()} is not a known mutable torrent.");

            long currentSequenceNumber;
            BEncodedDictionary currentVDictionary;
            
            if (_mutableSequenceNumbers.TryGetValue(torrentHandle, out currentSequenceNumber) && _lastKnownVDictionary.TryGetValue(torrentHandle, out currentVDictionary))
            {
                System.Diagnostics.Debug.WriteLine($"[RemoveFileFromMutableTorrentAsync] Using cached state Seq: {currentSequenceNumber}"); // Use Debug.WriteLine
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[RemoveFileFromMutableTorrentAsync] Fetching current state from DHT for {torrentHandle.ToHex()}..."); // Use Debug.WriteLine
                (currentSequenceNumber, currentVDictionary) = await GetAndUpdateTorrentStateAsync(torrentHandle);
                System.Diagnostics.Debug.WriteLine($"[RemoveFileFromMutableTorrentAsync] Fetched state Seq: {currentSequenceNumber}"); // Use Debug.WriteLine
            }

            BEncodedList currentFileList;
            try
            {
                currentFileList = GetFileListFromVDictionary(currentVDictionary);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not parse file list from vDictionary for torrent {torrentHandle.ToHex()}.", ex);
            }

            int pieceLength = (int)((MonoTorrent.BEncoding.BEncodedNumber)currentVDictionary["piece length"]).Number;
            string torrentName = ((MonoTorrent.BEncoding.BEncodedString)currentVDictionary["name"]).Text;

            long nextSequenceNumber = currentSequenceNumber + 1;
            var pathToRemoveParts = pathToRemove.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            System.Diagnostics.Debug.WriteLine($"[RemoveFileFromMutableTorrentAsync] Trying to remove path: {pathToRemove} (Parsed as: [{string.Join(", ", pathToRemoveParts)}])");

            var newFileListBEncoded = new MonoTorrent.BEncoding.BEncodedList();
            bool removedSomething = false;

            foreach (var item in currentFileList.Cast<MonoTorrent.BEncoding.BEncodedDictionary>())
            {
                if (item.TryGetValue("path", out var pathValue) && pathValue is MonoTorrent.BEncoding.BEncodedList pathList)
                {
                    var pathParts = pathList.Cast<MonoTorrent.BEncoding.BEncodedString>().Select(s => s.Text).ToArray();
                    System.Diagnostics.Debug.WriteLine($"[RemoveFileFromMutableTorrentAsync] Checking against path: [{string.Join(", ", pathParts)}]");

                    bool isMatchOrSubPath = pathParts.Length >= pathToRemoveParts.Length &&
                                            pathParts.Take(pathToRemoveParts.Length).SequenceEqual(pathToRemoveParts);

                    if (!isMatchOrSubPath)
                    {
                        System.Diagnostics.Debug.WriteLine("[RemoveFileFromMutableTorrentAsync] -> No match, keeping."); // Use Debug.WriteLine
                        newFileListBEncoded.Add(item);
                    } else {
                        System.Diagnostics.Debug.WriteLine("[RemoveFileFromMutableTorrentAsync] -> Match found, removing."); // Use Debug.WriteLine
                        removedSomething = true;
                    }
                }
                else
                {
                    newFileListBEncoded.Add(item);
                }
            }

            if (!removedSomething)
            {
                System.Diagnostics.Debug.WriteLine($"[RemoveFileFromMutableTorrentAsync] Warning: Path '{pathToRemove}' not found in torrent {torrentHandle.ToHex()}. No changes made to file list."); // Use Debug.WriteLine
                // If nothing was removed, we don't need to update the DHT. Return the current sequence number.
                // However, the logic below will still proceed and bump the sequence number.
                // Decide if this is the desired behavior (bump seq even if no change) or return early.
                // For simplicity, let's proceed and bump the sequence number.
            }

            var newVDictionary = new MonoTorrent.BEncoding.BEncodedDictionary {
                { "name", (MonoTorrent.BEncoding.BEncodedString)torrentName },
                { "piece length", new MonoTorrent.BEncoding.BEncodedNumber(pieceLength) },
                { "files", newFileListBEncoded }
            };

            // Copy other keys from the current dictionary that we want to preserve (like "messages" for chat)
            foreach (var key in currentVDictionary.Keys)
            {
                if (key.Text != "name" && key.Text != "piece length" && key.Text != "files" && !newVDictionary.ContainsKey(key))
                {
                    newVDictionary[key] = currentVDictionary[key];
                }
            }

            byte[] dataToSign = ConstructDataToSign(nextSequenceNumber, newVDictionary);
            byte[] newSignature = SignData(dataToSign, signingKeySeed);

            try
            {
                System.Diagnostics.Debug.WriteLine($"[RemoveFileFromMutableTorrentAsync] Attempting DHT Put for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}"); // Use Debug.WriteLine
                await _engine.Dht.PutMutableAsync(
                    publicKey: (BEncodedString)publicKey,
                    salt: null,
                    value: newVDictionary,
                    sequenceNumber: nextSequenceNumber,
                    signature: (BEncodedString)newSignature
                );
                System.Diagnostics.Debug.WriteLine($"[RemoveFileFromMutableTorrentAsync] DHT Put successful for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}"); // Use Debug.WriteLine
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RemoveFileFromMutableTorrentAsync] DHT Put FAILED for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}: {ex.Message}"); // Use Debug.WriteLine
                throw new TorrentException($"Failed to put mutable torrent update (RemoveFile) to DHT for '{torrentHandle.ToHex()}'.", ex);
            }

            _mutableSequenceNumbers[torrentHandle] = nextSequenceNumber;
            _lastKnownVDictionary[torrentHandle] = newVDictionary;
            _lastKnownSignature[torrentHandle] = newSignature;
            System.Diagnostics.Debug.WriteLine($"[RemoveFileFromMutableTorrentAsync] END: Completed for {torrentHandle.ToHex()}, returning Seq: {nextSequenceNumber}"); // Use Debug.WriteLine
            return nextSequenceNumber;
        }
    } // End of TorrentService class
} // End of namespace
