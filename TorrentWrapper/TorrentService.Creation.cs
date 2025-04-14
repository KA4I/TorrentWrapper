using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Dht; // Added for potential key handling
 
namespace TorrentWrapper
{
    public partial class TorrentService // Marked as partial
    {
        /// <summary>
        /// Creates a new torrent file (.torrent) from the specified source.
        /// </summary>
        /// <param name="fileSource">The source of the files (e.g., TorrentFileSource).</param>
        /// <param name="torrentType">The type of torrent to create (V1, V2, Hybrid).</param>
        /// <param name="announceUrls">Optional list of tracker announce URLs.</param>
        /// <param name="comment">Optional comment for the torrent.</param>
        /// <param name="createdBy">Optional creator string.</param>
        /// <param name="isPrivate">Whether the torrent should be marked as private.</param>
        /// <param name="pieceLength">Optional piece length. If null, a recommended size is used.</param>
        /// <param name="publicKey">Optional Ed25519 public key (32 bytes) to create a mutable torrent (BEP46).</param>
        /// <param name="sequenceNumber">Initial sequence number for mutable torrents (default is 0).</param>
        /// <param name="salt">Optional salt for mutable torrents (BEP44).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created Torrent object.</returns>
        public async Task<Torrent> CreateTorrentAsync(
            ITorrentFileSource fileSource,
            TorrentType torrentType = TorrentType.V1V2Hybrid,
            IList<IList<string>>? announceUrls = null,
            string? comment = null,
            string? createdBy = null,
            bool isPrivate = false,
            int? pieceLength = null,
            byte[]? publicKey = null, // Added for BEP46
            // Removed privateKey - not needed for creation, only publishing
            long sequenceNumber = 0, // Added for BEP46
            byte[]? salt = null, // Added for BEP44
            CancellationToken token = default)
        {
            if (!_isInitialized) throw new InvalidOperationException("TorrentService must be initialized before creating torrents.");
            if (fileSource == null) throw new ArgumentNullException(nameof(fileSource));
            if (publicKey != null && publicKey.Length != 32)
                throw new ArgumentException("Public key must be 32 bytes for a mutable torrent.");
            // Removed private key checks


            bool isMutable = publicKey != null;
            Console.WriteLine($"[TorrentService] Creating {(isMutable ? "mutable" : "standard")} torrent '{fileSource.TorrentName}' of type {torrentType}...");

            var creator = new TorrentCreator(torrentType);

            // Set optional parameters
            if (announceUrls != null)
            {
                foreach (var tier in announceUrls)
                {
                    creator.Announces.Add(new List<string>(tier));
                }
                // If only a single tracker in a single tier is provided, set the primary 'announce' key too
                if (announceUrls.Count == 1 && announceUrls[0].Count == 1)
                {
                    creator.Announce = announceUrls[0][0];
                }
            }
            if (!string.IsNullOrEmpty(comment)) creator.Comment = comment;
            if (!string.IsNullOrEmpty(createdBy)) creator.CreatedBy = createdBy;
            creator.Private = isPrivate;
            if (pieceLength.HasValue) creator.PieceLength = pieceLength.Value;
            // creator.StoreMD5 = ...; // Add if needed
            // creator.StoreSHA1 = ...; // Add if needed

            // Removed incorrect creator.SetMutable call

            // Create the torrent dictionary (info-dict primarily)
            BEncodedDictionary torrentDict = await creator.CreateAsync(fileSource, token);

            // --- BEP44/46 Handling: Add keys to the top-level dictionary ---
            if (isMutable)
            {
                Console.WriteLine($"[TorrentService] Adding mutable torrent keys to dictionary (Seq: {sequenceNumber}, Salt: {(salt != null ? Convert.ToHexString(salt) : "None")})");
                // Add 'pk' (public key) - BEP46
                torrentDict.Add("pk", new BEncodedString(publicKey!));
                // Add 'seq' (sequence number) - BEP44/46
                torrentDict.Add("seq", new BEncodedNumber(sequenceNumber));
                // Add 'salt' (optional) - BEP44
                if (salt != null)
                {
                    torrentDict.Add("salt", new BEncodedString(salt));
                }
            }
            // --- End BEP44/46 Handling ---

            // Load the Torrent object from the dictionary
            Torrent torrent = Torrent.Load(torrentDict);

            // Save the .torrent file to the metadata cache directory specified in engine settings
            // Replicate internal GetMetadataPath logic:
            string metadataFileName = torrent.InfoHashes.V1OrV2.ToHex() + ".torrent";
            string metadataPath = Path.Combine(_engineSettings.MetadataCacheDirectory, metadataFileName);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
                await File.WriteAllBytesAsync(metadataPath, torrentDict.Encode(), token);
                Console.WriteLine($"[TorrentService] Saved torrent metadata to: {metadataPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TorrentService ERROR] Failed to save torrent metadata to {metadataPath}: {ex.Message}");
                // Decide how to handle failure - maybe just log and return the torrent object anyway?
            }

            Console.WriteLine($"[TorrentService] Torrent '{torrent.Name}' created successfully. InfoHash(es): {torrent.InfoHashes}");
            if (isMutable)
            {
                Console.WriteLine($"[TorrentService] Mutable Torrent Public Key: {Convert.ToHexString(publicKey!)}");
                // Private key is not handled here anymore
            }
            return torrent;
        }

        /// <summary>
        /// Creates a new torrent file (.torrent) from the specified directory path.
        /// </summary>
        /// <param name="directoryPath">The path to the directory containing the files.</param>
        /// <param name="torrentType">The type of torrent to create (V1, V2, Hybrid).</param>
        /// <param name="announceUrls">Optional list of tracker announce URLs.</param>
        /// <param name="comment">Optional comment for the torrent.</param>
        /// <param name="createdBy">Optional creator string.</param>
        /// <param name="isPrivate">Whether the torrent should be marked as private.</param>
        /// <param name="pieceLength">Optional piece length. If null, a recommended size is used.</param>
        /// <param name="publicKey">Optional Ed25519 public key (32 bytes) to create a mutable torrent (BEP46).</param>
        /// <param name="sequenceNumber">Initial sequence number for mutable torrents (default is 0).</param>
        /// <param name="salt">Optional salt for mutable torrents (BEP44).</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The created Torrent object.</returns>
        public async Task<Torrent> CreateTorrentAsync(
            string directoryPath,
            TorrentType torrentType = TorrentType.V1V2Hybrid,
            IList<IList<string>>? announceUrls = null,
            string? comment = null,
            string? createdBy = null,
            bool isPrivate = false,
            int? pieceLength = null,
            byte[]? publicKey = null, // Added
            // Removed privateKey
            long sequenceNumber = 0, // Added
            byte[]? salt = null, // Added
            CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(directoryPath)) throw new ArgumentNullException(nameof(directoryPath));
            if (!Directory.Exists(directoryPath)) throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");

            var fileSource = new TorrentFileSource(directoryPath);
            // Pass the updated parameters through
            return await CreateTorrentAsync(fileSource, torrentType, announceUrls, comment, createdBy, isPrivate, pieceLength, publicKey, sequenceNumber, salt, token);
        }
        // Removed extra closing brace
    }
}