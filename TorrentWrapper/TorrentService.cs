﻿extern alias BCrypt; // Define the alias for BouncyCastle.Cryptography

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
             if (_engine?.Dht is MonoTorrent.Dht.DhtEngine dhtEngine)
             {
                var pk = (BEncodedString)publicKey;
                var saltBE = salt == null ? null : (BEncodedString)salt;
                var sig = (BEncodedString)signature;
                dhtEngine.StoreMutableLocally(pk, saltBE, value, sequenceNumber, sig);
             } else {
                 Console.WriteLine("[StoreItemLocallyForTest] Warning: Could not cast DhtAccess to DhtEngine.");
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
            var engineSettings = settings ?? new EngineSettingsBuilder().ToSettings();
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

            // The ClientEngine should implicitly start the DHT when needed.
            // We don't need to manually call StartAsync on the DHT engine here.
            // The engine will load nodes from the cache file if it exists.
            if (_engine.Dht == null)
            {
                 System.Diagnostics.Debug.WriteLine("[TorrentService] DHT Engine is NULL after ClientEngine creation.");
            } else {
                 System.Diagnostics.Debug.WriteLine($"[TorrentService] DHT Engine exists. Type: {_engine.Dht.GetType().FullName}. State: {_engine.Dht.State}");
                 // If nodes were loaded, add them. The engine might bootstrap itself later if needed.
                 if (!initialNodes.IsEmpty) {
                     System.Diagnostics.Debug.WriteLine($"[TorrentService] Adding {initialNodes.Length} bytes of cached DHT nodes via IDht.Add.");
                     _engine.Dht.Add(new [] { initialNodes });
                 }
            }
            // Removed the explicit BootstrapAsync call.


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
                System.Diagnostics.Debug.WriteLine($"[LoadTorrentAsync] Subscribing TorrentManager ({loadedManagerInfoHashHex}) TorrentUpdateAvailable event for mutable torrent {identifier.ToHex()}."); // Use Debug.WriteLine
                manager.TorrentUpdateAvailable += OnTorrentUpdateAvailable;
                // Do not start the manager here. It will enter MetadataMode if needed
                // and start itself once metadata is retrieved, preventing the NullRef during DHT announce.
                System.Diagnostics.Debug.WriteLine($"[LoadTorrentAsync] Manager added for mutable {identifier.ToHex()}. It will start automatically after metadata retrieval if needed."); // Use Debug.WriteLine
                System.Diagnostics.Debug.WriteLine($"[LoadTorrentAsync] Completed loading for {identifier.ToHex()}."); // Use Debug.WriteLine
                return identifier;
            }
             System.Diagnostics.Debug.WriteLine($"[LoadTorrentAsync] Completed loading for standard torrent {identifier.ToHex()}."); // Use Debug.WriteLine
             return identifier;
        }

        /// <summary>
        /// Forces the underlying TorrentManager to perform a check for mutable torrent updates via DHT.
        /// </summary>
        /// <param name="torrentHandle">The identifier (SHA1 hash of public key) of the mutable torrent.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        /// <exception cref="InvalidOperationException">Thrown if the service is not initialized or if the handle does not correspond to a mutable torrent.</exception>
        /// <exception cref="KeyNotFoundException">Thrown if the torrent handle is not found.</exception>
        public async Task TriggerMutableUpdateCheckAsync(InfoHash torrentHandle)
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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TriggerMutableUpdateCheckAsync] Update check FAILED for {torrentHandle.ToHex()}: {ex.Message}"); // Use Debug.WriteLine
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
            // torrentHandle *is* the targetId (SHA1 of public key)
            var dhtTargetId = MonoTorrent.Dht.NodeId.FromInfoHash(torrentHandle); // Convert key to NodeId for DHT lookup
 
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
                 System.Diagnostics.Debug.WriteLine($"[GetAndUpdateState] Calling DHT GetAsync for TargetID: {dhtTargetId} with Seq: {seqToRequest?.ToString() ?? "null"}"); // Use Debug.WriteLine
                 result = await _engine.Dht.GetAsync(dhtTargetId, seqToRequest); // Pass the sequence number
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
        }

        /// <summary>
        /// Removes a file or directory from an existing mutable torrent.
        /// </summary>
        /// <param name="torrentHandle">The handle of the torrent to modify.</param>
        /// <param name="pathToRemove">Path of the file or directory to remove (relative to torrent root).</param>
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
            System.Diagnostics.Debug.WriteLine($"[RemoveFileFromMutableTorrentAsync] Trying to remove path: {pathToRemove} (Parsed as: [{string.Join(", ", pathToRemoveParts)}])"); // Use Debug.WriteLine

            var newFileListBEncoded = new MonoTorrent.BEncoding.BEncodedList();
            bool removedSomething = false;
            foreach (var item in currentFileList.Cast<MonoTorrent.BEncoding.BEncodedDictionary>())
            {
                if (item.TryGetValue("path", out var pathValue) && pathValue is MonoTorrent.BEncoding.BEncodedList pathList)
                {
                    var pathParts = pathList.Cast<MonoTorrent.BEncoding.BEncodedString>().Select(s => s.Text).ToArray();
                    System.Diagnostics.Debug.WriteLine($"[RemoveFileFromMutableTorrentAsync] Checking against path: [{string.Join(", ", pathParts)}]"); // Use Debug.WriteLine

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
 
            try
            {
                 System.Diagnostics.Debug.WriteLine($"[UpdateMutableDataAsync] Calling DHT PutMutableAsync..."); // Use Debug.WriteLine
                 // Use the fetched latestSequenceNumber as the CAS value
                 await _engine.Dht.PutMutableAsync(
                    publicKey: pubKeyBencoded,
                    salt: saltBEncoded,
                    value: newVDictionary,
                    sequenceNumber: nextSequenceNumber,
                    signature: sigBencoded,
                    cas: latestSequenceNumber // Add CAS parameter
                );
                System.Diagnostics.Debug.WriteLine($"[UpdateMutableDataAsync] DHT PutMutableAsync successful for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}"); // Use Debug.WriteLine
            }
            catch (Exception ex)
            {
                 System.Diagnostics.Debug.WriteLine($"[UpdateMutableDataAsync] DHT PutMutableAsync FAILED for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}: {ex.Message}\nStackTrace: {ex.StackTrace}"); // Use Debug.WriteLine
                 throw new TorrentException($"Failed to put mutable torrent update (UpdateData) to DHT for '{torrentHandle.ToHex()}'.", ex);
            }

            _mutableSequenceNumbers[torrentHandle] = nextSequenceNumber;
            _lastKnownVDictionary[torrentHandle] = newVDictionary;
            _lastKnownSignature[torrentHandle] = newSignature;
 
            System.Diagnostics.Debug.WriteLine($"[UpdateMutableDataAsync] END: Completed for {torrentHandle.ToHex()}, returning Seq: {nextSequenceNumber}"); // Use Debug.WriteLine
            return nextSequenceNumber;
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
            System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Subscribing TorrentManager ({managerInfoHashHex}) TorrentUpdateAvailable event for {targetId.ToHex()}."); // Use Debug.WriteLine
            manager.TorrentUpdateAvailable += OnTorrentUpdateAvailable;
            // The manager should now be able to start and participate in DHT correctly.
            System.Diagnostics.Debug.WriteLine($"[JoinOrCreateMutable] Manager added and configured for {targetId.ToHex()}. It will start automatically after metadata retrieval if needed."); // Use Debug.WriteLine
            await manager.StartAsync(); // Explicitly start the manager to trigger engine start
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

        private byte[] GetPublicKeyFromSeed(byte[] seed)
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
                    if (_publicKeys.ContainsKey(kvp.Key)) {
                         kvp.Value.TorrentUpdateAvailable -= OnTorrentUpdateAvailable;
                    }
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

    } // End of TorrentService class
} // End of namespace
