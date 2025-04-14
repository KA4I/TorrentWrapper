extern alias BCrypt; // Use the alias defined in csproj for BouncyCastle

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Dht;
using System.Security.Cryptography;
using BCrypt::Org.BouncyCastle.Crypto.Parameters;
using BCrypt::Org.BouncyCastle.Crypto.Signers;
using BCrypt::Org.BouncyCastle.Security; // For SignerUtilities
// Removed duplicated extern alias and using statements




namespace TorrentWrapper
{
    public partial class TorrentService // Marked as partial
    {
        /// <summary>
        /// Edits non-secure metadata of an existing torrent (e.g., announce URLs, comment).
        /// Note: This does NOT allow adding/removing files or changing content, which requires recreation.
        /// The changes are saved back to the .torrent file in the metadata cache.
        /// </summary>
        /// <param name="infoHashes">The InfoHashes of the torrent to edit.</param>
        /// <param name="editAction">An action that receives a TorrentEditor instance to perform modifications.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the torrent was found and edited successfully, false otherwise.</returns>
        public async Task<bool> EditTorrentMetadataAsync(
            InfoHashes infoHashes,
            Action<TorrentEditor> editAction,
            CancellationToken token = default)
        {
            if (!_isInitialized) throw new InvalidOperationException("TorrentService must be initialized before editing torrents.");
            if (infoHashes == null) throw new ArgumentNullException(nameof(infoHashes));
            if (editAction == null) throw new ArgumentNullException(nameof(editAction));

            // Replicate the logic of GetMetadataPath directly
            string metadataFileName = infoHashes.V1OrV2.ToHex() + ".torrent";
            string metadataPath = Path.Combine(_engineSettings.MetadataCacheDirectory, metadataFileName);

            if (!File.Exists(metadataPath))
            {
                Console.WriteLine($"[TorrentService] Cannot edit torrent: Metadata file not found at {metadataPath}");
                return false;
            }

            Console.WriteLine($"[TorrentService] Editing metadata for torrent: {infoHashes.V1OrV2.ToHex()}");

            try
            {
                // Load the existing metadata
                BEncodedDictionary torrentDict;
                byte[] existingData = await File.ReadAllBytesAsync(metadataPath, token);
                torrentDict = BEncodedValue.Decode<BEncodedDictionary>(existingData);

                // Create an editor
                var editor = new TorrentEditor(torrentDict);
                // IMPORTANT: Do not allow editing secure metadata by default,
                // as it changes the infohash and creates a different torrent.
                editor.CanEditSecureMetadata = false;

                // Apply user edits
                editAction(editor);

                // Save the modified metadata back
                BEncodedDictionary editedDict = editor.ToDictionary();
                byte[] newData = editedDict.Encode();
                await File.WriteAllBytesAsync(metadataPath, newData, token);

                Console.WriteLine($"[TorrentService] Successfully edited and saved metadata for torrent: {infoHashes.V1OrV2.ToHex()}");

                // Optional: If the torrent is loaded, potentially reload its metadata?
                // This is complex as TorrentManager doesn't easily support live metadata reloading.
                // A common approach is to remove and re-add the torrent if significant changes occurred.
                // For simple changes like announce URLs, TorrentManager might pick them up automatically or via re-announce.

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TorrentService ERROR] Failed to edit torrent metadata {infoHashes.V1OrV2.ToHex()}: {ex.Message}");
                return false;
            }
        }


        /// <summary>
        /// Publishes an update for a mutable torrent (BEP44/46) to the DHT.
        /// This requires the corresponding private key.
        /// </summary>
        /// <param name="publicKey">The Ed25519 public key (32 bytes) associated with the mutable torrent.</param>
        /// <param name="privateKey">The Ed25519 private key (64 bytes) corresponding to the public key.</param>
        /// <param name="sequenceNumber">The sequence number for this update (must be greater than the previous one).</param>
        /// <param name="updatedTorrentDict">The complete BEncodedDictionary of the updated torrent metadata.</param>
        /// <param name="salt">Optional salt associated with the mutable torrent (must match the original if used).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the update was successfully published to the DHT, false otherwise.</returns>
        public async Task<bool> PublishTorrentUpdateAsync(
            byte[] publicKey,
            byte[] privateKey,
            long sequenceNumber,
            BEncodedDictionary updatedTorrentDict,
            byte[]? salt = null,
            CancellationToken token = default)
        {
            if (!_isInitialized) throw new InvalidOperationException("TorrentService must be initialized before publishing updates.");
            if (publicKey == null || publicKey.Length != 32) throw new ArgumentException("Public key must be 32 bytes.", nameof(publicKey));
            if (privateKey == null || privateKey.Length != 64) throw new ArgumentException("Private key must be 64 bytes.", nameof(privateKey));
            if (sequenceNumber < 0) throw new ArgumentOutOfRangeException(nameof(sequenceNumber), "Sequence number cannot be negative.");
            if (updatedTorrentDict == null) throw new ArgumentNullException(nameof(updatedTorrentDict));

            // Ensure the DHT is configured (by checking if an endpoint is set) and the engine is running
            if (_engine.Settings.DhtEndPoint == null) // Check if DhtEndPoint is configured
            {
                Console.WriteLine("[TorrentService ERROR] Cannot publish update: DHT endpoint is not configured in engine settings.");
                return false;
            }
            if (!_engine.IsRunning)
            {
                Console.WriteLine("[TorrentService ERROR] Cannot publish update: ClientEngine is not running.");
                return false;
            }

            Console.WriteLine($"[TorrentService] Publishing mutable torrent update (Seq: {sequenceNumber}, Salt: {(salt != null ? Convert.ToHexString(salt) : "None")}) for PK: {Convert.ToHexString(publicKey)}");

            try
            {
                // 1. Extract the actual value 'v' from the updated dictionary
                // The updatedTorrentDict passed in should contain the full structure, including 'v'.
                // The engine's PutMutableAsync expects the 'v' value directly.
                if (!updatedTorrentDict.TryGetValue("v", out BEncodedValue? valueToStore))
                {
                    // Fallback: Maybe the user passed the *whole* dict intended as 'v'? Unlikely but check.
                    // A more robust API might take 'valueToStore' directly instead of the whole dict.
                    // For now, assume updatedTorrentDict *is* the value if 'v' isn't inside it.
                    // This is ambiguous based on the current method signature. Let's assume
                    // updatedTorrentDict IS the value 'v' for now, as the original dict
                    // was passed in the test.
                    valueToStore = updatedTorrentDict;
                    Console.WriteLine("[TorrentService WARNING] 'updatedTorrentDict' did not contain a 'v' key. Assuming the entire dictionary is the value to store.");
                    // Reconstruct the dictionary to sign based on this assumption
                    updatedTorrentDict = new BEncodedDictionary {
                        { "pk", new BEncodedString(publicKey) },
                        { "seq", new BEncodedNumber(sequenceNumber) },
                        { "v", valueToStore }
                    };
                    if (salt != null) updatedTorrentDict.Add("salt", new BEncodedString(salt));
                }

                // 2. Prepare the data structure for signing (seq:<s>, v:<v> or seq:<s>salt<l>:<salt>v:<v>)
                var dictToSign = new BEncodedDictionary {
                    { "seq", new BEncodedNumber(sequenceNumber) },
                    { "v", valueToStore }
                };
                if (salt != null) {
                    dictToSign.Add("salt", new BEncodedString(salt));
                }
                byte[] encodedDataToSign = dictToSign.Encode();

                // 3. Sign the data using BouncyCastle Ed25519
                if (privateKey.Length != 64) throw new ArgumentException("BouncyCastle signing requires a 64-byte private key.", nameof(privateKey));
                var privateKeyParams = new Ed25519PrivateKeyParameters(privateKey, 0);

                var signer = new Ed25519Signer();
                signer.Init(true, privateKeyParams);
                signer.BlockUpdate(encodedDataToSign, 0, encodedDataToSign.Length);
                byte[] signatureBytes = signer.GenerateSignature();

                if (signatureBytes.Length != 64) throw new InvalidOperationException($"Ed25519 signature generation failed or produced incorrect length ({signatureBytes.Length}).");

                // 4. Prepare parameters for the engine's PutMutableAsync
                var bPublicKey = new BEncodedString(publicKey);
                var bSalt = salt == null ? null : new BEncodedString(salt);
                var bSignature = new BEncodedString(signatureBytes);

                // 5. Call the public wrapper method on the ClientEngine
                // Pass null for cas for now
                await _engine.PutMutableAsync(bPublicKey, bSalt, valueToStore, sequenceNumber, bSignature, null);

                Console.WriteLine($"[TorrentService] Successfully published mutable torrent update for PK: {Convert.ToHexString(publicKey)}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TorrentService ERROR] Failed to publish mutable torrent update for PK {Convert.ToHexString(publicKey)}: {ex.Message}");
                if (ex.InnerException != null) {
                     Console.WriteLine($"[TorrentService ERROR] Inner Exception: {ex.InnerException.Message}");
                }
                return false;
            }
        }

        // Placeholder for GetPeersAsync (item 9) - Already exists in TorrentService.Peers.cs
    }
}