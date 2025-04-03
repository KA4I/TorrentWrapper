﻿extern alias BCrypt; // Define the alias for BouncyCastle.Cryptography

// Add necessary using directives
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.BEncoding;
// --- System/Standard Namespaces ---
using System; // For Convert, InvalidOperationException, NotImplementedException, Buffer, Math, ArgumentException
using System.Collections.Generic;
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
    /// focusing on mutable torrents (BEP46) for game communication.
    /// </summary>
    public class TorrentService : IAsyncDisposable
    {
        /// <summary>
        /// Raised when an update is available for a mutable torrent being managed by this service.
        /// The InfoHash provided is the *new* InfoHash for the updated torrent data.
        /// The application should typically stop the old torrent and start a new one with this InfoHash.
        /// </summary>
        // Compiler Error CS0103 likely due to project reference issue. TorrentUpdateEventArgs is in MonoTorrent.Client.
        public event EventHandler<MutableTorrentUpdateInfoEventArgs>? MutableTorrentUpdateAvailable;

        private MonoTorrent.Client.ClientEngine? _engine; // Qualify
        private readonly Dictionary<MonoTorrent.InfoHash, MonoTorrent.Client.TorrentManager> _managedTorrents = new(); // Qualify
        private readonly Dictionary<MonoTorrent.InfoHash, long> _mutableSequenceNumbers = new(); // Tracks current seq number for mutable torrents we manage/modify
        private readonly Dictionary<MonoTorrent.InfoHash, byte[]> _publicKeys = new(); // Tracks public key for mutable torrents
        private readonly Dictionary<MonoTorrent.InfoHash, MonoTorrent.BEncoding.BEncodedDictionary> _lastKnownVDictionary = new(); // Tracks last known state


        /// <summary>
        /// Exposes the public DHT interface for advanced scenarios (like testing).
        /// </summary>
        public MonoTorrent.Client.IDht? DhtAccess => _engine?.Dht;

        /// <summary>
        /// Initializes the TorrentService and starts the MonoTorrent ClientEngine.
        /// </summary>
        /// <param name="settings">Optional engine settings.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public Task InitializeAsync(EngineSettings? settings = null)
        {
            _engine = new ClientEngine(settings ?? new EngineSettingsBuilder().ToSettings());
            // DHT engine is managed internally by ClientEngine and starts automatically
            // if enabled in settings (default is true).
            return Task.CompletedTask;
        }

        /// <summary>
        /// Creates a new mutable torrent (BEP46).
        /// </summary>
        /// <param name="initialContentPath">Path to the initial file or directory for the torrent.</param>
        /// <returns>A tuple containing the MagnetLink, the non-secret private key (byte[]), and the initial InfoHash.</returns>
        public async Task<(MonoTorrent.MagnetLink MagnetLink, byte[] PrivateKey, MonoTorrent.InfoHash InitialInfoHash)> CreateMutableTorrentAsync(string initialContentPath) // Qualify types
        {
            if (_engine == null) throw new InvalidOperationException("Service not initialized.");

            // 1. Generate Ed25519 keypair using BouncyCastle
            // Use alias for BouncyCastle types
            var secureRandom = new BCrypt::Org.BouncyCastle.Security.SecureRandom();
            var keyPairGenerator = BCrypt::Org.BouncyCastle.Security.GeneratorUtilities.GetKeyPairGenerator("Ed25519");
            keyPairGenerator.Init(new BCrypt::Org.BouncyCastle.Crypto.Parameters.Ed25519KeyGenerationParameters(secureRandom));
            var keyPair = keyPairGenerator.GenerateKeyPair();
            var privateKeyParams = (BCrypt::Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters)keyPair.Private;
            var publicKeyParams = (BCrypt::Org.BouncyCastle.Crypto.Parameters.Ed25519PublicKeyParameters)keyPair.Public;
            byte[] privateKey = privateKeyParams.GetEncoded(); // 32 bytes seed + 32 bytes public key = 64 bytes
            byte[] publicKey = publicKeyParams.GetEncoded();   // 32 bytes public key

            // Note: BEP44/46 typically uses the 32-byte seed as the 'private key' for signing,
            // but BouncyCastle's private key encoding includes the public key. We need the 32-byte seed.
            // Let's assume the first 32 bytes of the encoded private key are the seed.
            // **Verification needed**: Confirm BouncyCastle's Ed25519PrivateKeyParameters.GetEncoded() format
            // or find a way to get just the 32-byte seed if necessary. For now, we'll proceed assuming this.
            byte[] signingKeySeed = privateKey.AsSpan(0, 32).ToArray(); // Assuming first 32 bytes are the seed

            // 2. Create Torrent structure (TorrentCreator) - To get file info, piece length etc.
            // We need this info for the 'v' dictionary, even if the standard 'info' dict isn't used directly for BEP46 hash.
            var creator = new MonoTorrent.TorrentCreator(); // Qualify type
            creator.Comment = "Mutable Torrent created by TorrentWrapper";
            // Announce URLs are typically not needed for pure DHT operation via BEP46.
            // creator.Announce = "udp://tracker.example.com:80";

            // Use TorrentFileSource to get info about the initial content
            var fileSource = new MonoTorrent.TorrentFileSource(initialContentPath); // Qualify type

            // Calculate total size to suggest a piece length
            long totalSize = fileSource.Files.Sum(f => f.Length);
            int pieceLength = CalculatePieceLength(totalSize); // Use a helper method
            creator.PieceLength = pieceLength; // Set it on the creator for reference

            // TODO: Construct the file structure for the vDictionary based on fileSource.Files
            // This needs to match the format MonoTorrent expects when receiving a mutable item.
            // For simplicity, let's assume a single file for now. A directory needs recursion.
            var fileList = new MonoTorrent.BEncoding.BEncodedList(); // Qualify type
            // totalSize is already calculated above, remove re-declaration
            foreach (var file in fileSource.Files)
            {
                // Example for a simple file list (BEP 47 structure might be needed for directories)
                fileList.Add(new MonoTorrent.BEncoding.BEncodedDictionary { // Qualify type
                    { "path", new MonoTorrent.BEncoding.BEncodedList(file.Destination.Parts.ToArray().Select(s => (MonoTorrent.BEncoding.BEncodedString)s)) }, // Qualify types
                    { "length", new MonoTorrent.BEncoding.BEncodedNumber(file.Length) } // Qualify type
                    // Add attributes if needed: { "attr", (MonoTorrent.BEncoding.BEncodedString)"x" }
                });
                totalSize += file.Length;
            }

            // 3. Prepare initial signed data (v dictionary, seq 0, signature)
            long sequenceNumber = 0;

            // Construct the 'v' dictionary (value to be stored in DHT)
            // This needs to contain enough info for a client to reconstruct the torrent.
            // Structure needs confirmation based on BEP44/46 and MonoTorrent expectations.
            // Example structure (needs verification):
            // Construct the 'v' dictionary
            var vDictionary = new MonoTorrent.BEncoding.BEncodedDictionary { // Qualify type
                { "name", (MonoTorrent.BEncoding.BEncodedString)fileSource.TorrentName }, // Qualify type
                { "piece length", new MonoTorrent.BEncoding.BEncodedNumber(pieceLength) }, // Qualify type, add comma
                // Using "files" list as per BEP46/BEP47 structure.
                { "files", fileList } // Add the file list to the vDictionary
            };
            var encodedVDictionary = vDictionary.Encode();

            // Construct data to sign according to BEP44: "salt" + salt + "seq" + seq + "v" + value
            // Salt is omitted here. Sequence number MUST be BEncoded.
            var seqKey = new BEncodedString("seq");
            var seqValue = new BEncodedNumber(sequenceNumber); // Use BEncodedNumber for seq
            var vKey = new BEncodedString("v");

            // Calculate total length
            int totalLength = seqKey.LengthInBytes() + seqValue.LengthInBytes() + vKey.LengthInBytes() + encodedVDictionary.Length;
            var dataToSign = new byte[totalLength];

            // Encode into the buffer
            int offset = 0;
            offset += seqKey.Encode(dataToSign.AsSpan(offset));
            offset += seqValue.Encode(dataToSign.AsSpan(offset)); // Encode seq number
            offset += vKey.Encode(dataToSign.AsSpan(offset));
            offset += vDictionary.Encode(dataToSign.AsSpan(offset)); // Encode the dictionary itself

            // Sign using BouncyCastle Ed25519Signer
            var signer = new BCrypt::Org.BouncyCastle.Crypto.Signers.Ed25519Signer(); // Use alias
            // Use the 32-byte seed for signing with BouncyCastle's Ed25519Signer
            var signingPrivateKeyParams = new BCrypt::Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(signingKeySeed); // Use alias
            signer.Init(true, signingPrivateKeyParams);
            signer.BlockUpdate(dataToSign, 0, dataToSign.Length);
            byte[] signature = signer.GenerateSignature(); // 64 bytes signature

            // 4. Perform initial DHT put
            // Need to find the correct method in MonoTorrent's DhtEngine API.
            // It likely takes public key, optional salt, sequence number, value (BEncodedValue), and signature.
            // Example placeholder:
            // Use the public IDht interface which now exposes PutMutableAsync
            // Use _engine! now that we've checked for null
            // Use _engine directly after null check, remove '!'
            MonoTorrent.InfoHash initialIdentifier; // Declare earlier
            using (var sha1 = SHA1.Create()) {
                 initialIdentifier = new InfoHash(sha1.ComputeHash(publicKey));
            }
            Console.WriteLine($"[CreateMutableTorrentAsync] Calculated Initial Identifier (SHA1(PubKey)): {initialIdentifier.ToHex()}");
            Console.WriteLine($"[CreateMutableTorrentAsync] Attempting DHT Put for PubKey: {Convert.ToHexString(publicKey).ToLowerInvariant()} | Seq: {sequenceNumber}");
            try
            {
                await _engine.Dht.PutMutableAsync(
                    publicKey: (BEncodedString)publicKey,
                    salt: null, // Salt is optional, use null if not needed
                    value: vDictionary,
                    sequenceNumber: sequenceNumber,
                    signature: (BEncodedString)signature
                );
                Console.WriteLine($"[CreateMutableTorrentAsync] DHT Put successful for {initialIdentifier.ToHex()}");
            }
            catch (Exception ex)
            {
                // Wrap DHT exceptions for clarity
                // Compiler Error CS0103 likely due to project reference issue. TorrentException is in MonoTorrent.
                Console.WriteLine($"[CreateMutableTorrentAsync] DHT Put FAILED for {initialIdentifier.ToHex()}: {ex.Message}");
                throw new TorrentException($"Failed to put initial mutable torrent data to DHT for '{initialIdentifier.ToHex()}'.", ex);
            }

            // 5. Add the torrent to the local engine so it's managed
            var magnetLink = new MagnetLink(Convert.ToHexString(publicKey).ToLowerInvariant(), name: fileSource.TorrentName);
            string savePath = Path.GetDirectoryName(initialContentPath) ?? throw new ArgumentException("Could not determine directory from initialContentPath.", nameof(initialContentPath));
            // Ensure the save directory exists (MonoTorrent might handle this, but better safe)
            Directory.CreateDirectory(savePath);

            TorrentManager manager;
            Console.WriteLine($"[CreateMutableTorrentAsync] Attempting Engine.AddAsync for Magnet: {magnetLink}");
            try
            {
                manager = await _engine.AddAsync(magnetLink, savePath);
                Console.WriteLine($"[CreateMutableTorrentAsync] Engine.AddAsync successful for {initialIdentifier.ToHex()}. Manager State: {manager?.State}");
            }
            catch (Exception ex)
            {
                // If adding fails after DHT put, we have an inconsistent state.
                // Consider if cleanup (DHT delete?) is needed, though complex.
                Console.WriteLine($"[CreateMutableTorrentAsync] Engine.AddAsync FAILED for {initialIdentifier.ToHex()}: {ex.Message}");
                 throw new TorrentException($"Failed to add created mutable torrent '{initialIdentifier.ToHex()}' to the engine.", ex);
             }

             // Add null check for manager
             if (manager == null)
             {
                 // Handle the case where AddAsync returns null unexpectedly
                 Console.WriteLine($"[CreateMutableTorrentAsync] Engine.AddAsync returned null for {initialIdentifier.ToHex()}.");
                 throw new TorrentException($"Failed to obtain TorrentManager for created mutable torrent '{initialIdentifier.ToHex()}'.");
             }

             // 6. Store manager and subscribe to updates
             _managedTorrents.Add(initialIdentifier, manager);
             _publicKeys[initialIdentifier] = publicKey;
            _mutableSequenceNumbers[initialIdentifier] = sequenceNumber;
            _lastKnownVDictionary[initialIdentifier] = vDictionary;
            // Safely log manager infohashes, checking for null
            string managerInfoHashHex = manager?.InfoHashes?.V1OrV2?.ToHex() ?? "N/A";
            Console.WriteLine($"[CreateMutableTorrentAsync] Subscribing TorrentManager ({managerInfoHashHex}) TorrentUpdateAvailable event for {initialIdentifier.ToHex()}.");
            manager.TorrentUpdateAvailable += OnTorrentUpdateAvailable; // Subscribe for updates
            // Start the torrent manager so it can seed/participate in DHT
            await manager.StartAsync();
            Console.WriteLine($"[CreateMutableTorrentAsync] Started TorrentManager for {initialIdentifier.ToHex()}.");

            // 7. Return magnet link, private key (seed), infohash
            Console.WriteLine($"[CreateMutableTorrentAsync] Completed for {initialIdentifier.ToHex()}.");
            return (magnetLink, signingKeySeed, initialIdentifier);
        }

        /// <summary>
        /// Loads and starts managing a torrent from a magnet link.
        /// </summary>
        /// <param name="magnetLink">The magnet link (can be standard or BEP46 xs=).</param>
        /// <param name="savePath">The directory to save torrent data.</param>
        /// <returns>A handle or identifier for the managed torrent.</returns>
        public async Task<InfoHash> LoadTorrentAsync(MagnetLink magnetLink, string savePath) // Use using directive
        {
            Console.WriteLine($"[LoadTorrentAsync] Loading Magnet: {magnetLink}");
            if (_engine == null) throw new InvalidOperationException("Service not initialized.");

            // Determine the primary identifier (InfoHash or PublicKeyHash)
            MonoTorrent.InfoHash identifier; // Qualify type
            byte[]? mutablePublicKey = null; // Store the public key if it's a mutable torrent

            if (magnetLink.InfoHashes != null && (magnetLink.InfoHashes.V1 != null || magnetLink.InfoHashes.V2 != null)) { // Check InfoHashes null first
                identifier = magnetLink.InfoHashes.V1OrV2;
            } else if (!string.IsNullOrEmpty(magnetLink.PublicKeyHex)) {
                // For BEP46, use the SHA1 hash of the public key as the identifier
                mutablePublicKey = Convert.FromHexString(magnetLink.PublicKeyHex);
                using (var sha1 = SHA1.Create()) { // Use using directive
                    identifier = new InfoHash(sha1.ComputeHash(mutablePublicKey)); // Use using directive
                    Console.WriteLine($"[LoadTorrentAsync] Identified as Mutable Torrent. Identifier (SHA1(PubKey)): {identifier.ToHex()}");
                }
            } else {
                throw new ArgumentException("Magnet link does not contain a valid InfoHash or Public Key.", nameof(magnetLink));
            }

            // Check if already managed
            if (_managedTorrents.ContainsKey(identifier))
            {
                // Optionally, just return the existing handle or throw an error
                // For now, let's return the existing identifier
                Console.WriteLine($"[LoadTorrentAsync] Already managing {identifier.ToHex()}, returning.");
                return identifier;
                // Or: throw new InvalidOperationException($"Torrent with identifier {identifier} is already managed.");
            }

            // Add the torrent to the engine
            // Use _engine! now that we've checked for null
            // Use _engine directly after null check, remove '!'
            Console.WriteLine($"[LoadTorrentAsync] Attempting Engine.AddAsync for {identifier.ToHex()}...");
             TorrentManager manager = await _engine.AddAsync(magnetLink, savePath); // Use using directive
             Console.WriteLine($"[LoadTorrentAsync] Engine.AddAsync successful for {identifier.ToHex()}. Manager State: {manager?.State}");

             // Add null check for manager
             if (manager == null)
             {
                 Console.WriteLine($"[LoadTorrentAsync] Engine.AddAsync returned null for {identifier.ToHex()}.");
                 throw new TorrentException($"Failed to obtain TorrentManager for loaded torrent '{identifier.ToHex()}'.");
             }

             // Store the manager and potentially the public key/initial sequence state
             _managedTorrents.Add(identifier, manager);
             if (mutablePublicKey != null)
            {
                _publicKeys[identifier] = mutablePublicKey;
                // Subscribe to the update event for mutable torrents
                // Compiler Error CS0122 likely due to project reference issue. OnTorrentUpdateAvailable signature depends on TorrentUpdateEventArgs.
                // Safely log manager infohashes, checking for null
                string loadedManagerInfoHashHex = manager?.InfoHashes?.V1OrV2?.ToHex() ?? "N/A";
                Console.WriteLine($"[LoadTorrentAsync] Subscribing TorrentManager ({loadedManagerInfoHashHex}) TorrentUpdateAvailable event for mutable torrent {identifier.ToHex()}.");
                manager.TorrentUpdateAvailable += OnTorrentUpdateAvailable;
                // Start the torrent manager to enable active downloading/DHT interaction/update checks
                await manager.StartAsync();
                Console.WriteLine($"[LoadTorrentAsync] Started TorrentManager for {identifier.ToHex()}.");
                // We don't know the sequence number or vDictionary when just loading.
                // Modification methods will need to handle this by calling GetAndUpdateTorrentStateAsync first.
                // Return the identifier as the handle
                Console.WriteLine($"[LoadTorrentAsync] Completed loading for {identifier.ToHex()}.");
                return identifier;
            }
            // If it wasn't a mutable torrent, return the standard identifier
             Console.WriteLine($"[LoadTorrentAsync] Completed loading for standard torrent {identifier.ToHex()}.");
             return identifier; // Ensure non-mutable path also returns
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

            Console.WriteLine($"[TriggerMutableUpdateCheckAsync] Triggering update check for {torrentHandle.ToHex()}");
            try
            {
                // Call the new method on the TorrentManager instance
                await manager.ForceMutableUpdateCheckAsync();
                Console.WriteLine($"[TriggerMutableUpdateCheckAsync] Update check completed for {torrentHandle.ToHex()}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TriggerMutableUpdateCheckAsync] Update check FAILED for {torrentHandle.ToHex()}: {ex.Message}");
                // Decide if re-throwing is appropriate. For now, just log.
            }
        }

        // Retrieves the public key associated with a mutable torrent handle.
        private byte[] GetPublicKeyForTorrent(MonoTorrent.InfoHash torrentHandle)
        {
            if (_publicKeys.TryGetValue(torrentHandle, out var publicKey))
            {
                return publicKey;
            }
            throw new KeyNotFoundException($"Could not find public key for the torrent handle '{torrentHandle.ToHex()}'. Ensure it was created or loaded by this service.");
        }

        // Helper method to get the latest state from DHT and update local cache
        private async Task<(long sequenceNumber, MonoTorrent.BEncoding.BEncodedDictionary vDictionary)> GetAndUpdateTorrentStateAsync(MonoTorrent.InfoHash torrentHandle)
        {
            if (_engine == null) throw new InvalidOperationException("Service not initialized.");

            byte[] publicKey = GetPublicKeyForTorrent(torrentHandle); // Get the public key first
            // Target ID for mutable items is the SHA1 hash of the public key
            MonoTorrent.InfoHash targetId;
            using (var sha1 = SHA1.Create()) {
                 targetId = new MonoTorrent.InfoHash(sha1.ComputeHash(publicKey));
            }
            var dhtTargetId = MonoTorrent.Dht.NodeId.FromInfoHash(targetId); // Use static factory method

            // Perform the DHT Get operation
            // Qualify the GetAsync call just in case, though it shouldn't be needed.
            (BEncodedValue? value, BEncodedString? publicKey, BEncodedString? signature, long? sequenceNumber) result;
            try
            {
                 // Assuming IDht interface *does* have GetAsync in the modified source
                 // Compiler Error CS1061 likely due to project reference issue. GetAsync should exist in modified IDht.cs.
                 result = await _engine.Dht.GetAsync(dhtTargetId);
            }
            catch (Exception ex)
            {
                 // Compiler Error CS0103 likely due to project reference issue. TorrentException is in MonoTorrent.
                 throw new TorrentException($"Failed to get mutable torrent state from DHT for '{torrentHandle.ToHex()}'.", ex);
            }

            BEncodedDictionary? vDict = null; // Initialize vDict
            if (result.value is BEncodedDictionary tempVDict && result.sequenceNumber.HasValue)
            {
                // Update local cache with the fetched state
                _mutableSequenceNumbers[torrentHandle] = result.sequenceNumber.Value;
                vDict = tempVDict; // Assign the matched dictionary
                _lastKnownVDictionary[torrentHandle] = vDict;
                // Optional: Verify signature and public key match if needed for extra security
                // if (!VerifySignature(publicKey, result.salt, result.sequenceNumber.Value, vDict, result.signature)) { ... }
                return (result.sequenceNumber.Value, vDict); // Return the assigned vDict
            }
            else
            {
                // Handle cases where the item wasn't found or the format was incorrect
                // Maybe fall back to locally cached version if available? Or throw?
                // For now, throw if we can't get the latest state.
                throw new InvalidOperationException($"Could not retrieve valid mutable state for torrent '{torrentHandle.ToHex()}' from DHT.");
            }
        }

        // Retrieves the BEncodedList representing the files from a given vDictionary.
        private BEncodedList GetFileListFromVDictionary(BEncodedDictionary vDictionary) // Use using directive
        {
             if (vDictionary.TryGetValue("files", out var filesValue) && // Assuming the key is "files"
                 filesValue is BEncodedList fileList) // Use using directive
             {
                 return fileList;
             }
             throw new InvalidOperationException("Could not find 'files' list in the provided vDictionary.");
        }

        // This method seems redundant now that GetAndUpdateTorrentStateAsync fetches the latest vDictionary
        // and GetFileListFromVDictionary extracts the list from it. Removing it.
        // private BEncodedList GetCurrentFileListBEncoding(InfoHash torrentHandle)
        // { ... }
        // Removed extra brace from previous diff attempt

        /// <summary>
        /// Adds a file to an existing mutable torrent.
        /// </summary>
        /// <param name="torrentHandle">The handle of the torrent to modify (InfoHash of public key).</param>
        /// <param name="filePathToAdd">Path to the file to add.</param>
        /// <param name="signingKeySeed">The 32-byte non-secret private key seed for signing.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task<long> AddFileToMutableTorrentAsync(MonoTorrent.InfoHash torrentHandle, string filePathToAdd, byte[] signingKeySeed)
        {
            Console.WriteLine($"[AddFileToMutableTorrentAsync] START: Adding '{Path.GetFileName(filePathToAdd)}' to torrent {torrentHandle.ToHex()}");
            if (_engine == null) throw new InvalidOperationException("Service not initialized.");
            if (signingKeySeed == null || signingKeySeed.Length != 32) throw new ArgumentException("Invalid signing key seed provided.", nameof(signingKeySeed));
            if (!_managedTorrents.TryGetValue(torrentHandle, out var manager)) throw new KeyNotFoundException("Torrent handle not found or torrent is not managed.");
            if (!_publicKeys.TryGetValue(torrentHandle, out var publicKey)) throw new InvalidOperationException($"Torrent {torrentHandle.ToHex()} is not a known mutable torrent.");
            if (!_mutableSequenceNumbers.TryGetValue(torrentHandle, out var currentSequenceNumber)) throw new InvalidOperationException($"Current sequence number not found for mutable torrent {torrentHandle.ToHex()}.");
            if (!_lastKnownVDictionary.TryGetValue(torrentHandle, out var currentVDictionary)) throw new InvalidOperationException($"Last known vDictionary not found for mutable torrent {torrentHandle.ToHex()}.");

            // --- Use Locally Cached State ---
            // byte[] publicKey is retrieved above
            MonoTorrent.BEncoding.BEncodedList currentFileList;
            try
            {
                currentFileList = GetFileListFromVDictionary(currentVDictionary);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not parse file list from cached vDictionary for torrent {torrentHandle.ToHex()}.", ex);
            }

            // Extract piece length and name from the fetched vDictionary
            int pieceLength = (int)((MonoTorrent.BEncoding.BEncodedNumber)currentVDictionary["piece length"]).Number;
            string torrentName = ((MonoTorrent.BEncoding.BEncodedString)currentVDictionary["name"]).Text;

            // --- Prepare New State ---
            long nextSequenceNumber = currentSequenceNumber + 1;

            // Create source for the new file
            var newFileSource = new MonoTorrent.TorrentFileSource(filePathToAdd);
            if (newFileSource.Files.Count() != 1)
            {
                // For simplicity, only handle adding single files for now. Directories need more complex merging.
                throw new NotSupportedException("Adding directories or multiple files at once is not yet supported.");
            }
            var newFileMapping = newFileSource.Files.Single();

            // Construct the new file list BEncoding
            var newFileListBEncoded = new MonoTorrent.BEncoding.BEncodedList(currentFileList); // Copy existing
            newFileListBEncoded.Add(new MonoTorrent.BEncoding.BEncodedDictionary {
                { "path", new MonoTorrent.BEncoding.BEncodedList(newFileMapping.Destination.Parts.ToArray().Select(s => (MonoTorrent.BEncoding.BEncodedString)s)) },
                { "length", new MonoTorrent.BEncoding.BEncodedNumber(newFileMapping.Length) }
            });

            // --- Construct New vDictionary ---
            var newVDictionary = new MonoTorrent.BEncoding.BEncodedDictionary {
                { "name", (MonoTorrent.BEncoding.BEncodedString)torrentName },
                { "piece length", new MonoTorrent.BEncoding.BEncodedNumber(pieceLength) },
                { "files", newFileListBEncoded } // Assuming "files" list format
            };
            var encodedNewVDictionary = newVDictionary.Encode();

            // --- Sign New State ---
            // Construct data to sign according to BEP44: "salt" + salt + "seq" + seq + "v" + value
            // Salt is omitted here. Sequence number MUST be BEncoded.
            var seqKey = new BEncodedString("seq");
            var seqValue = new BEncodedNumber(nextSequenceNumber);
            var vKey = new BEncodedString("v");

            // Calculate total length
            int totalLength = seqKey.LengthInBytes() + seqValue.LengthInBytes() + vKey.LengthInBytes() + encodedNewVDictionary.Length;
            var dataToSign = new byte[totalLength];

            // Encode into the buffer
            int offset = 0;
            offset += seqKey.Encode(dataToSign.AsSpan(offset));
            offset += seqValue.Encode(dataToSign.AsSpan(offset));
            offset += vKey.Encode(dataToSign.AsSpan(offset));
            offset += newVDictionary.Encode(dataToSign.AsSpan(offset)); // Encode the dictionary itself

            var signer = new BCrypt::Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
            var signingPrivateKeyParams = new BCrypt::Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(signingKeySeed);
            signer.Init(true, signingPrivateKeyParams);
            signer.BlockUpdate(dataToSign, 0, dataToSign.Length);
            byte[] newSignature = signer.GenerateSignature();

            // --- Perform DHT Put ---
            try
            {
                 Console.WriteLine($"[AddFileToMutableTorrentAsync] Attempting DHT Put for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}");
                 await _engine.Dht.PutMutableAsync(
                    publicKey: (BEncodedString)publicKey,
                    salt: null, // Salt is optional
                    value: newVDictionary,
                    sequenceNumber: nextSequenceNumber,
                    signature: (BEncodedString)newSignature
                );
                Console.WriteLine($"[AddFileToMutableTorrentAsync] DHT Put successful for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}");
            }
            catch (Exception ex)
            {
                 // Compiler Error CS0103 likely due to project reference issue. TorrentException is in MonoTorrent.
                 Console.WriteLine($"[AddFileToMutableTorrentAsync] DHT Put FAILED for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}: {ex.Message}");
                 throw new TorrentException($"Failed to put mutable torrent update (AddFile) to DHT for '{torrentHandle.ToHex()}'.", ex);
            }

            // --- Update Local State ---
            _mutableSequenceNumbers[torrentHandle] = nextSequenceNumber; // Update local sequence number
            _lastKnownVDictionary[torrentHandle] = newVDictionary; // Update local vDictionary cache
            Console.WriteLine($"[AddFileToMutableTorrentAsync] END: Completed for {torrentHandle.ToHex()}, returning Seq: {nextSequenceNumber}");
            return nextSequenceNumber;
        }

        /// <summary>
        /// Removes a file from an existing mutable torrent.
        /// </summary>
        /// <param name="torrentHandle">The handle of the torrent to modify.</param>
        /// <param name="filePathToRemove">Path of the file to remove (relative to torrent root).</param>
        /// <param name="torrentPrivateKey">The non-secret private key for the mutable torrent.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task<long> RemoveFileFromMutableTorrentAsync(MonoTorrent.InfoHash torrentHandle, string filePathToRemove, byte[] signingKeySeed)
        {
            Console.WriteLine($"[RemoveFileFromMutableTorrentAsync] START: Removing '{filePathToRemove}' from torrent {torrentHandle.ToHex()}");

            if (_engine == null) throw new InvalidOperationException("Service not initialized.");
            if (signingKeySeed == null || signingKeySeed.Length != 32) throw new ArgumentException("Invalid signing key seed provided.", nameof(signingKeySeed));
            if (!_managedTorrents.TryGetValue(torrentHandle, out var manager)) throw new KeyNotFoundException("Torrent handle not found or torrent is not managed.");
            if (!_publicKeys.TryGetValue(torrentHandle, out var publicKey)) throw new InvalidOperationException($"Torrent {torrentHandle.ToHex()} is not a known mutable torrent.");
            if (!_mutableSequenceNumbers.TryGetValue(torrentHandle, out var currentSequenceNumber)) throw new InvalidOperationException($"Current sequence number not found for mutable torrent {torrentHandle.ToHex()}.");
            if (!_lastKnownVDictionary.TryGetValue(torrentHandle, out var currentVDictionary)) throw new InvalidOperationException($"Last known vDictionary not found for mutable torrent {torrentHandle.ToHex()}.");

            // --- Use Locally Cached State ---
            // byte[] publicKey is retrieved above
            MonoTorrent.BEncoding.BEncodedList currentFileList;
             try
            {
                currentFileList = GetFileListFromVDictionary(currentVDictionary);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not parse file list from cached vDictionary for torrent {torrentHandle.ToHex()}.", ex);
            }
            int pieceLength = (int)((MonoTorrent.BEncoding.BEncodedNumber)currentVDictionary["piece length"]).Number;
            string torrentName = ((MonoTorrent.BEncoding.BEncodedString)currentVDictionary["name"]).Text;

            // --- Prepare New State ---
            long nextSequenceNumber = currentSequenceNumber + 1;
            var pathToRemoveParts = filePathToRemove.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var newFileListBEncoded = new MonoTorrent.BEncoding.BEncodedList();
            foreach (var item in currentFileList.Cast<MonoTorrent.BEncoding.BEncodedDictionary>())
            {
                if (item.TryGetValue("path", out var pathValue) && pathValue is MonoTorrent.BEncoding.BEncodedList pathList)
                {
                    var pathParts = pathList.Cast<MonoTorrent.BEncoding.BEncodedString>().Select(s => s.Text).ToArray();
                    // Simple comparison - might need normalization for complex paths
                    if (!pathParts.SequenceEqual(pathToRemoveParts))
                    {
                        newFileListBEncoded.Add(item); // Keep files that don't match
                    }
                }
            }

            // --- Construct New vDictionary ---
             var newVDictionary = new MonoTorrent.BEncoding.BEncodedDictionary {
                { "name", (MonoTorrent.BEncoding.BEncodedString)torrentName },
                { "piece length", new MonoTorrent.BEncoding.BEncodedNumber(pieceLength) },
                { "files", newFileListBEncoded }
            };
            var encodedNewVDictionary = newVDictionary.Encode();

            // --- Sign New State ---
            // Construct data to sign according to BEP44: "salt" + salt + "seq" + seq + "v" + value
            // Salt is omitted here. Sequence number MUST be BEncoded.
            var seqKey = new BEncodedString("seq");
            var seqValue = new BEncodedNumber(nextSequenceNumber);
            var vKey = new BEncodedString("v");

            // Calculate total length
            int totalLength = seqKey.LengthInBytes() + seqValue.LengthInBytes() + vKey.LengthInBytes() + encodedNewVDictionary.Length;
            var dataToSign = new byte[totalLength];

            // Encode into the buffer
            int offset = 0;
            offset += seqKey.Encode(dataToSign.AsSpan(offset));
            offset += seqValue.Encode(dataToSign.AsSpan(offset));
            offset += vKey.Encode(dataToSign.AsSpan(offset));
            offset += newVDictionary.Encode(dataToSign.AsSpan(offset)); // Encode the dictionary itself

            var signer = new BCrypt::Org.BouncyCastle.Crypto.Signers.Ed25519Signer();
            var signingPrivateKeyParams = new BCrypt::Org.BouncyCastle.Crypto.Parameters.Ed25519PrivateKeyParameters(signingKeySeed);
            signer.Init(true, signingPrivateKeyParams);
            signer.BlockUpdate(dataToSign, 0, dataToSign.Length);
            byte[] newSignature = signer.GenerateSignature();

            // --- Perform DHT Put ---
            try
            {
                 Console.WriteLine($"[RemoveFileFromMutableTorrentAsync] Attempting DHT Put for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}");
                 await _engine.Dht.PutMutableAsync(
                    publicKey: (BEncodedString)publicKey,
                    salt: null,
                    value: newVDictionary,
                    sequenceNumber: nextSequenceNumber,
                    signature: (BEncodedString)newSignature
                );
                Console.WriteLine($"[RemoveFileFromMutableTorrentAsync] DHT Put successful for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}");
            }
            catch (Exception ex)
            {
                 // Compiler Error CS0103 likely due to project reference issue. TorrentException is in MonoTorrent.
                 Console.WriteLine($"[RemoveFileFromMutableTorrentAsync] DHT Put FAILED for {torrentHandle.ToHex()} | Seq: {nextSequenceNumber}: {ex.Message}");
                 throw new TorrentException($"Failed to put mutable torrent update (RemoveFile) to DHT for '{torrentHandle.ToHex()}'.", ex);
            }

            // --- Update Local State ---
            _mutableSequenceNumbers[torrentHandle] = nextSequenceNumber; // Update local sequence number
            _lastKnownVDictionary[torrentHandle] = newVDictionary; // Update local vDictionary cache
            Console.WriteLine($"[RemoveFileFromMutableTorrentAsync] END: Completed for {torrentHandle.ToHex()}, returning Seq: {nextSequenceNumber}");
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
            // Note: This simplified remove+add approach will now correctly fetch state between operations via GetAndUpdateTorrentStateAsync.
            // A single-step update would still be more efficient (one DHT put instead of two).
            await RemoveFileFromMutableTorrentAsync(torrentHandle, filePathToUpdate, signingKeySeed);
            await AddFileToMutableTorrentAsync(torrentHandle, newFilePath, signingKeySeed);
        }

        /// <summary>
        /// Gets the list of files within a loaded torrent.
        /// </summary>
        /// <param name="torrentHandle">The handle of the torrent.</param>
        /// <returns>A list of file paths relative to the torrent root.</returns>
        public Task<IEnumerable<string>> GetTorrentFilesAsync(InfoHash torrentHandle) // Restore Task return type
        {
             if (!_managedTorrents.TryGetValue(torrentHandle, out var manager))
            {
                throw new KeyNotFoundException("Torrent handle not found or torrent is not managed.");
            }

            // Ensure torrent metadata is available
            if (manager.Torrent == null)
            {
                // Metadata might still be downloading, especially if loaded from magnet link
                // We could wait for it: await manager.WaitForMetadataAsync();
                // Or return empty / throw depending on desired behavior.
                // Throwing for now, as accessing .Files would fail anyway.
                throw new InvalidOperationException("Torrent metadata not yet available.");
            }

            // Return the paths from the Torrent object's file list
            // Note: This reflects the state based on the *loaded* torrent metadata.
            // For mutable torrents, this might not reflect the *absolute latest* state from DHT
            // until an update is processed by the engine.
            return Task.FromResult(manager.Torrent.Files.Select(f => f.Path.ToString())); // Return Task
        }

        // Event handler for TorrentManager's update event
        // Compiler Error CS0103 likely due to project reference issue. TorrentUpdateEventArgs is in MonoTorrent.Client.
        private void OnTorrentUpdateAvailable(object? sender, TorrentUpdateEventArgs e)
        {
            Console.WriteLine($"[TorrentService {this.GetHashCode()}] Entered OnTorrentUpdateAvailable."); // Log entry
            // Enhanced Logging
            string senderType = sender?.GetType().Name ?? "null";
            string newInfoHashHex = e.NewInfoHash?.ToHex() ?? "null"; // Safely access NewInfoHash
            Console.WriteLine($"[OnTorrentUpdateAvailable] Event received. Sender Type: {senderType}, NewInfoHash: {newInfoHashHex}");

            // The sender is the TorrentManager which raised the event.
            if (sender is TorrentManager manager)
            {
                // Get the original identifier (public key hash) we stored for this manager.
                // We need to find the key in _managedTorrents whose value is 'manager'.
                InfoHash? originalInfoHash = null;
                foreach (var kvp in _managedTorrents)
                {
                    if (kvp.Value == manager)
                    {
                        // Check if this is actually a mutable torrent we track by public key hash
                        if (_publicKeys.ContainsKey(kvp.Key))
                        {
                            originalInfoHash = kvp.Key;
                            break;
                        }
                    }
                }

                Console.WriteLine($"[OnTorrentUpdateAvailable] Found OriginalInfoHash: {originalInfoHash?.ToHex() ?? "null"} for Manager: {manager?.InfoHashes?.V1OrV2?.ToHex() ?? "N/A"}");
                if (originalInfoHash != null)
                 {
                     // Ensure NewInfoHash is not null before creating args
                     if (e.NewInfoHash != null)
                     {
                         // Create the new event args with both original and new InfoHash
                         var customArgs = new MutableTorrentUpdateInfoEventArgs(originalInfoHash, e.NewInfoHash);
                         // Re-raise the event from this service using the custom args
                         Console.WriteLine($"[OnTorrentUpdateAvailable] Invoking MutableTorrentUpdateAvailable for {originalInfoHash.ToHex()} -> {customArgs.NewInfoHash.ToHex()}");
                         MutableTorrentUpdateAvailable?.Invoke(this, customArgs);
                     }
                     else
                     {
                         Console.WriteLine($"[OnTorrentUpdateAvailable] Warning: NewInfoHash was null for OriginalInfoHash {originalInfoHash.ToHex()}. Cannot invoke event.");
                     }
                 }
                 else
                {
                    // Log or handle the case where the original infohash couldn't be found for the manager
                    // This case should not happen if subscription logic is correct, but log just in case.
                    Console.WriteLine($"[OnTorrentUpdateAvailable] Warning: Could not find OriginalInfoHash for sender manager {manager.InfoHashes.V1OrV2.ToHex()}.");
                }
            }
        }

        // TODO: Add more event handlers as needed (e.g., StateChanged, PeersFound)

        /// <summary>
        /// Stops the MonoTorrent ClientEngine and releases resources.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            // Use _engine! now that we've checked for null
            if (_engine != null)
            {
                // Unsubscribe from events for all managed torrents
                foreach(var kvp in _managedTorrents)
                {
                    // Check if it was a mutable torrent we subscribed to
                    if (_publicKeys.ContainsKey(kvp.Key)) {
                         // Compiler Error CS0122 likely due to project reference issue. OnTorrentUpdateAvailable signature depends on TorrentUpdateEventArgs.
                         // No need to check _publicKeys here, just try to unsubscribe.
                         // If it wasn't subscribed, this does nothing.
                         kvp.Value.TorrentUpdateAvailable -= OnTorrentUpdateAvailable;
                    }
                }
                await _engine.StopAllAsync(); // Stop torrents before disposing engine
                _engine.Dispose();
                _engine = null;
            }
        }

        private static int CalculatePieceLength(long totalSize)
        {
            // Simple logic: Aim for roughly 1500 pieces.
            // Adjust thresholds and logic as needed.
            const int minPieceLength = 16 * 1024; // 16 KiB
            const int maxPieceLength = 4 * 1024 * 1024; // 4 MiB
            const int targetPieces = 1500;

            if (totalSize == 0) return minPieceLength;

            long idealPieceLength = totalSize / targetPieces;

            // Ensure it's a power of 2
            int powerOf2Length = minPieceLength;
            while (powerOf2Length < idealPieceLength && powerOf2Length < maxPieceLength)
            {
                powerOf2Length *= 2;
            }

            // Clamp to min/max
            return Math.Clamp(powerOf2Length, minPieceLength, maxPieceLength);
        }
    } // End of TorrentService class

} // End of namespace
