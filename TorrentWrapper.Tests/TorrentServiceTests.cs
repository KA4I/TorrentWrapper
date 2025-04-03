using Microsoft.VisualStudio.TestTools.UnitTesting;
using TorrentWrapper;
using MonoTorrent.Client; // For EngineSettingsBuilder, TorrentUpdateEventArgs etc.
using MonoTorrent.Dht;      // For NodeId, DhtEngine
using System.Buffers.Binary; // For BinaryPrimitives
using MonoTorrent;      // For InfoHash, MagnetLink etc.
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System;

namespace TorrentWrapper.Tests
{
    [TestClass]
    public class TorrentServiceTests
    {
        private TorrentService? _service1;
        private TorrentService? _service2; // For simulating multiple clients
        private string? _baseDirectory1;
        private string? _baseDirectory2;
        private EngineSettings? _settings1;
        private EngineSettings? _settings2;

        // Unique port numbers for each test run to avoid conflicts
        private static int _portCounter = 55000;
        private int _dhtPort1;
        private int _listenPort1;
        private int _dhtPort2;
        private int _listenPort2;


        [TestInitialize]
        public async Task TestInitialize()
        {
            // Create unique directories for each test instance
            _baseDirectory1 = Path.Combine(Path.GetTempPath(), "TorrentServiceTest1_" + Guid.NewGuid().ToString("N"));
            _baseDirectory2 = Path.Combine(Path.GetTempPath(), "TorrentServiceTest2_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_baseDirectory1);
            Directory.CreateDirectory(_baseDirectory2);

            // Assign unique ports
            _dhtPort1 = Interlocked.Increment(ref _portCounter);
            _listenPort1 = Interlocked.Increment(ref _portCounter);
            _dhtPort2 = Interlocked.Increment(ref _portCounter);
            _listenPort2 = Interlocked.Increment(ref _portCounter);

            // Configure settings for two separate engine instances
             _settings1 = new EngineSettingsBuilder {
                AllowPortForwarding = false, // Disable for local tests
                AutoSaveLoadFastResume = false, // Disable for predictable test state
                CacheDirectory = Path.Combine(_baseDirectory1, "cache"),
                DhtEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, _dhtPort1),
                ListenEndPoints = new Dictionary<string, System.Net.IPEndPoint> { { "", new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, _listenPort1) } }
            }.ToSettings();

             _settings2 = new EngineSettingsBuilder {
                AllowPortForwarding = false,
                AutoSaveLoadFastResume = false,
                CacheDirectory = Path.Combine(_baseDirectory2, "cache"),
                DhtEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, _dhtPort2),
                ListenEndPoints = new Dictionary<string, System.Net.IPEndPoint> { { "", new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, _listenPort2) } }
            }.ToSettings();


            _service1 = new TorrentService();
            await _service1.InitializeAsync(_settings1);

            _service2 = new TorrentService();
            await _service2.InitializeAsync(_settings2);

            // Give DHT engines time to start automatically.
            // NOTE: This might not be reliable for ensuring they find each other.
            // Explicit addition might be needed within tests after managers are started.
            await Task.Delay(2000); // Reduced delay as explicit add is preferred
        }

        [TestCleanup]
        public async Task TestCleanup()
        {
            if (_service1 != null)
                await _service1.DisposeAsync();
             if (_service2 != null)
                await _service2.DisposeAsync();

            // Clean up temporary directories
            TryDeleteDirectory(_baseDirectory1);
            TryDeleteDirectory(_baseDirectory2);
        }

        // Helper to create compact node info (BEP 5)
        private ReadOnlyMemory<byte> CreateCompactNodeInfo(NodeId nodeId, System.Net.IPEndPoint endpoint)
        {
            // Only handle IPv4 for simplicity in this test helper
            if (endpoint.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new NotSupportedException("This helper only supports IPv4 endpoints for compact node info.");

            byte[] buffer = new byte[26]; // 20 bytes for NodeId + 4 bytes for IP + 2 bytes for Port
            nodeId.Span.CopyTo(buffer.AsSpan(0, 20));
            endpoint.Address.GetAddressBytes().CopyTo(buffer.AsSpan(20, 4));
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(24, 2), (ushort)endpoint.Port);
            return buffer;
        }

        private void TryDeleteDirectory(string? path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                try
                {
                    Directory.Delete(path, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not delete test directory '{path}': {ex.Message}");
                }
            }
        }

        private string CreateDummyFile(string baseDir, string filename, int size)
        {
            string filePath = Path.Combine(baseDir, filename);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!); // Ensure directory exists
            byte[] data = new byte[size];
            Random.Shared.NextBytes(data);
            File.WriteAllBytes(filePath, data);
            return filePath;
        }

        // --- Test Methods ---

        [TestMethod]
        public async Task CreateMutableTorrent_ShouldSucceed()
        {
            Assert.IsNotNull(_service1);
            Assert.IsNotNull(_baseDirectory1);

            string initialFile = CreateDummyFile(_baseDirectory1, "initial.txt", 10);

            var (magnetLink, privateKeySeed, initialIdentifier) = await _service1.CreateMutableTorrentAsync(initialFile);

            Assert.IsNotNull(magnetLink, "MagnetLink should not be null.");
            Assert.IsNotNull(privateKeySeed, "PrivateKeySeed should not be null.");
            Assert.AreEqual(32, privateKeySeed.Length, "PrivateKeySeed should be 32 bytes.");
            Assert.IsNotNull(initialIdentifier, "InitialIdentifier should not be null.");
            Assert.IsTrue(magnetLink.ToV1Uri().ToString().Contains("xs=urn:btpk:"), "Magnet link should contain public key (xs).");
            Assert.AreEqual(64, magnetLink.PublicKeyHex?.Length, "Public key hex should be 64 chars."); // ed25519 pubkey = 32 bytes = 64 hex
        }

        [TestMethod]
        public async Task LoadMutableTorrent_ShouldSucceed()
        {
            Assert.IsNotNull(_service1);
            Assert.IsNotNull(_service2);
            Assert.IsNotNull(_baseDirectory1);
            Assert.IsNotNull(_baseDirectory2);

            // Service 1 creates the torrent
            string initialFile = CreateDummyFile(_baseDirectory1, "shared.txt", 10);
            var (magnetLink, _, initialIdentifier) = await _service1.CreateMutableTorrentAsync(initialFile);

            // Service 2 loads the torrent
            string downloadPath2 = Path.Combine(_baseDirectory2, "downloads");
            Directory.CreateDirectory(downloadPath2);

            var loadedIdentifier = await _service2.LoadTorrentAsync(magnetLink, downloadPath2);

            Assert.AreEqual(initialIdentifier, loadedIdentifier, "Loaded identifier should match the initial identifier.");

            // Verify service 2 is managing it (internal check, requires access or helper)
            // This requires exposing _managedTorrents or adding a helper method like 'IsManaging(InfoHash)'
            // For now, we assume success if LoadTorrentAsync doesn't throw.
            // Assert.IsTrue(_service2.IsManaging(loadedIdentifier));
        }

        [TestMethod]
        public async Task AddFile_ShouldPropagateUpdate()
        {
            Assert.IsNotNull(_service1);
            Assert.IsNotNull(_service2);
            Assert.IsNotNull(_baseDirectory1);
            Assert.IsNotNull(_baseDirectory2);

            // Service 1 creates
            string initialFile = CreateDummyFile(_baseDirectory1, "initial.txt", 10);
            var (magnetLink, privateKeySeed, initialIdentifier) = await _service1.CreateMutableTorrentAsync(initialFile);

            // Service 2 loads
            string downloadPath2 = Path.Combine(_baseDirectory2, "downloads_add");
            var loadedIdentifier = await _service2.LoadTorrentAsync(magnetLink, downloadPath2);
            Assert.AreEqual(initialIdentifier, loadedIdentifier);


            // Explicitly add nodes to each other's DHT engine now that they are started
            if (_service1?.DhtAccess != null && _service2?.DhtAccess != null && _settings1?.DhtEndPoint != null && _settings2?.DhtEndPoint != null)
            {
                var dummyId1 = MonoTorrent.Dht.NodeId.Create();
                var dummyId2 = MonoTorrent.Dht.NodeId.Create();

                byte[] nodeInfo1 = new byte[26];
                dummyId1.Span.CopyTo(nodeInfo1.AsSpan(0, 20));
                _settings1.DhtEndPoint.Address.GetAddressBytes().CopyTo(nodeInfo1.AsSpan(20, 4));
                BinaryPrimitives.WriteUInt16BigEndian(nodeInfo1.AsSpan(24, 2), (ushort)_settings1.DhtEndPoint.Port);

                byte[] nodeInfo2 = new byte[26];
                dummyId2.Span.CopyTo(nodeInfo2.AsSpan(0, 20));
                _settings2.DhtEndPoint.Address.GetAddressBytes().CopyTo(nodeInfo2.AsSpan(20, 4));
                BinaryPrimitives.WriteUInt16BigEndian(nodeInfo2.AsSpan(24, 2), (ushort)_settings2.DhtEndPoint.Port);

                _service2.DhtAccess.Add(new ReadOnlyMemory<byte>[] { nodeInfo1 });
                _service1.DhtAccess.Add(new ReadOnlyMemory<byte>[] { nodeInfo2 });

                // Give a short delay for the pings/pongs to be processed and routing tables updated
                await Task.Delay(1000);
                Console.WriteLine("[Test] Explicitly added DHT nodes to each other.");
            }

            // Setup event listener on Service 2
            MutableTorrentUpdateInfoEventArgs? receivedArgs = null;
            var updateReceivedSignal = new TaskCompletionSource<bool>();
            _service2.MutableTorrentUpdateAvailable += (sender, args) => { // args is now MutableTorrentUpdateInfoEventArgs
                Console.WriteLine($"[Test Event Handler {_service2.GetHashCode()}] Received MutableTorrentUpdateAvailable. Original: {args.OriginalInfoHash.ToHex()}, New: {args.NewInfoHash.ToHex()}, Expected Original: {loadedIdentifier.ToHex()}");
                // Check if the update is for the torrent we loaded by comparing the original InfoHash
                 if (args.OriginalInfoHash == loadedIdentifier) {
                    Console.WriteLine($"[Test Event Handler {_service2.GetHashCode()}] Match found! Setting signal.");
                    receivedArgs = args; // Assign the correct type
                    updateReceivedSignal.TrySetResult(true);
                 } else {
                     Console.WriteLine($"[Test Event Handler {_service2.GetHashCode()}] Mismatch: OriginalInfoHash {args.OriginalInfoHash.ToHex()} != Expected {loadedIdentifier.ToHex()}");
                 }
            };

            // Service 1 adds a file
            string fileToAdd = CreateDummyFile(_baseDirectory1, "added_file.dat", 50);
            long newSeq1 = await _service1.AddFileToMutableTorrentAsync(initialIdentifier, fileToAdd, privateKeySeed);
            Assert.AreEqual(1, newSeq1, "Sequence number after first add should be 1.");

            // Trigger the mutable update check on service 2 AFTER service 1 has put the update.
            Console.WriteLine($"[Test {_service2.GetHashCode()}] Triggering mutable update check on Service 2 for {loadedIdentifier.ToHex()}");
            await _service2.TriggerMutableUpdateCheckAsync(loadedIdentifier); // Use the new method
            Console.WriteLine($"[Test {_service2.GetHashCode()}] Update check triggered. Waiting for update signal...");

             // Wait for Service 2 to receive the update via DHT/event
             Console.WriteLine($"[Test {_service2.GetHashCode()}] Waiting for update signal...");
             bool updateReceived = await Task.WhenAny(updateReceivedSignal.Task, Task.Delay(20000)) == updateReceivedSignal.Task; // Increased timeout to 20 sec

             Assert.IsTrue(updateReceived, "Service 2 did not receive the torrent update event within the timeout.");
            Assert.IsNotNull(receivedArgs, "Received event args should not be null.");
            // receivedArgs is now MutableTorrentUpdateInfoEventArgs
            Assert.IsNotNull(receivedArgs.NewInfoHash, "New InfoHash in event args should not be null.");
            Assert.AreNotEqual(initialIdentifier, receivedArgs.NewInfoHash, "New InfoHash should be different from the initial one."); // Compare against the correct property

            // Optional: Verify file list on Service 2 after update (would require loading the new torrent)
            // await _service2.RemoveAsync(loadedIdentifier); // Need a Remove method
            // var newMagnet = new MagnetLink(receivedArgs.NewInfoHash);
            // var newManager = await _service2.LoadTorrentAsync(newMagnet, downloadPath2);
            // var files = await _service2.GetTorrentFilesAsync(newManager.InfoHashes.V1OrV2);
            // Assert.IsTrue(files.Any(f => f.EndsWith("added_file.dat")));
        }

         [TestMethod]
        public async Task RemoveFile_ShouldPropagateUpdate()
        {
            Assert.IsNotNull(_service1);
            Assert.IsNotNull(_service2);
            Assert.IsNotNull(_baseDirectory1);
            Assert.IsNotNull(_baseDirectory2);

            // Service 1 creates with two files
            string initialFile1 = CreateDummyFile(_baseDirectory1, "file1.txt", 10);
            string initialFile2 = CreateDummyFile(_baseDirectory1, "file2.txt", 20);
            // Need TorrentCreator to add multiple files initially, or add one then modify
            // Let's modify after creation for simplicity here
             var (magnetLink, privateKeySeed, initialIdentifier) = await _service1.CreateMutableTorrentAsync(initialFile1);
             long seqAfterAdd = await _service1.AddFileToMutableTorrentAsync(initialIdentifier, initialFile2, privateKeySeed);
             Assert.AreEqual(1, seqAfterAdd, "Sequence number after adding second file should be 1.");
             // Need to get the *new* identifier after the add
             // This highlights a potential API improvement: modification methods could return the new identifier/state.
             // For now, we'll assume the identifier remains the public key hash, but the *state* is updated.

            // Service 2 loads
            string downloadPath2 = Path.Combine(_baseDirectory2, "downloads_remove");
            var loadedIdentifier = await _service2.LoadTorrentAsync(magnetLink, downloadPath2);
            Assert.AreEqual(initialIdentifier, loadedIdentifier); // Identifier should still match based on public key

            // Explicitly add nodes to each other's DHT engine now that they are started
            if (_service1?.DhtAccess != null && _service2?.DhtAccess != null && _settings1?.DhtEndPoint != null && _settings2?.DhtEndPoint != null)
            {
                var dummyId1 = MonoTorrent.Dht.NodeId.Create();
                var dummyId2 = MonoTorrent.Dht.NodeId.Create();

                byte[] nodeInfo1 = new byte[26];
                dummyId1.Span.CopyTo(nodeInfo1.AsSpan(0, 20));
                _settings1.DhtEndPoint.Address.GetAddressBytes().CopyTo(nodeInfo1.AsSpan(20, 4));
                BinaryPrimitives.WriteUInt16BigEndian(nodeInfo1.AsSpan(24, 2), (ushort)_settings1.DhtEndPoint.Port);

                byte[] nodeInfo2 = new byte[26];
                dummyId2.Span.CopyTo(nodeInfo2.AsSpan(0, 20));
                _settings2.DhtEndPoint.Address.GetAddressBytes().CopyTo(nodeInfo2.AsSpan(20, 4));
                BinaryPrimitives.WriteUInt16BigEndian(nodeInfo2.AsSpan(24, 2), (ushort)_settings2.DhtEndPoint.Port);

                _service2.DhtAccess.Add(new ReadOnlyMemory<byte>[] { nodeInfo1 });
                _service1.DhtAccess.Add(new ReadOnlyMemory<byte>[] { nodeInfo2 });

                // Give a short delay for the pings/pongs to be processed and routing tables updated
                await Task.Delay(1000);
                Console.WriteLine("[Test] Explicitly added DHT nodes to each other.");
            }

            // Setup event listener on Service 2
            MutableTorrentUpdateInfoEventArgs? receivedArgs = null;
            var updateReceivedSignal = new TaskCompletionSource<bool>();
            _service2.MutableTorrentUpdateAvailable += (sender, args) => { // args is now MutableTorrentUpdateInfoEventArgs
                 Console.WriteLine($"[Test Event Handler {_service2.GetHashCode()}] Received MutableTorrentUpdateAvailable. Original: {args.OriginalInfoHash.ToHex()}, New: {args.NewInfoHash.ToHex()}, Expected Original: {loadedIdentifier.ToHex()}");
                 // Check if the update is for the torrent we loaded by comparing the original InfoHash
                 if (args.OriginalInfoHash == loadedIdentifier) {
                     // Signal completion on the first relevant update received after triggering the check.
                     // The update check should fetch the latest state (Seq 2).
                     Console.WriteLine($"[Test Event Handler {_service2.GetHashCode()}] Matching update received! Setting signal.");
                     receivedArgs = args; // Assign the correct type
                     updateReceivedSignal.TrySetResult(true);
                 } else {
                     Console.WriteLine($"[Test Event Handler {_service2.GetHashCode()}] Mismatch: OriginalInfoHash {args.OriginalInfoHash.ToHex()} != Expected {loadedIdentifier.ToHex()}");
                 }
            };

             // Wait for the *first* update (from the AddFile) to likely propagate before removing
             await Task.Delay(5000); // Give DHT time

            // Service 1 removes the second file
            long seqAfterRemove = await _service1.RemoveFileFromMutableTorrentAsync(initialIdentifier, "file2.txt", privateKeySeed);
            Assert.AreEqual(2, seqAfterRemove, "Sequence number after removing file should be 2.");

            // Trigger the mutable update check on service 2 AFTER service 1 has put the update.
            Console.WriteLine($"[Test {_service2.GetHashCode()}] Triggering mutable update check on Service 2 for {loadedIdentifier.ToHex()} (after remove)");
            await _service2.TriggerMutableUpdateCheckAsync(loadedIdentifier); // Use the new method
            Console.WriteLine($"[Test {_service2.GetHashCode()}] Update check triggered. Waiting 500ms for event processing...");
            await Task.Delay(500); // Allow time for event processing after Get

            // Wait for Service 2 to receive the *second* update via DHT/event
            Console.WriteLine($"[Test {_service2.GetHashCode()}] Waiting for second update signal...");
            bool updateReceived = await Task.WhenAny(updateReceivedSignal.Task, Task.Delay(10000)) == updateReceivedSignal.Task; // 10 sec timeout

            Assert.IsTrue(updateReceived, "Service 2 did not receive the second torrent update event within the timeout.");
            Assert.IsNotNull(receivedArgs, "Received event args should not be null for removal.");
            // receivedArgs is now MutableTorrentUpdateInfoEventArgs
            Assert.IsNotNull(receivedArgs.NewInfoHash, "New InfoHash in event args should not be null for removal."); // Compare against the correct property
            // We can't easily assert the previous InfoHash here without storing it from the first event.
        }

        // Note: UpdateFile test would be very similar to Add/Remove as it uses them internally.
        // A more robust test would involve a single-step update if that were implemented.



        [TestMethod]
        public async Task CreateMutableTorrent_WithDirectory_ShouldSucceed()
        {
            Assert.IsNotNull(_service1);
            Assert.IsNotNull(_baseDirectory1);

            // Create a directory structure
            string dirPath = Path.Combine(_baseDirectory1!, "test_dir");
            Directory.CreateDirectory(dirPath);
            string file1 = CreateDummyFile(dirPath, "file1.txt", 10);
            string subDirPath = Path.Combine(dirPath, "subdir");
            Directory.CreateDirectory(subDirPath);
            string file2 = CreateDummyFile(subDirPath, "file2.dat", 20);

            var (magnetLink, privateKeySeed, initialIdentifier) = await _service1.CreateMutableTorrentAsync(dirPath);

            Assert.IsNotNull(magnetLink, "MagnetLink should not be null.");
            Assert.IsNotNull(privateKeySeed, "PrivateKeySeed should not be null.");
            Assert.AreEqual(32, privateKeySeed.Length, "PrivateKeySeed should be 32 bytes.");
            Assert.IsNotNull(initialIdentifier, "InitialIdentifier should not be null.");
            Assert.IsTrue(magnetLink.ToV1Uri().ToString().Contains("xs=urn:btpk:"), "Magnet link should contain public key (xs).");

            // Optional: Verify the created torrent contains the expected files (requires GetTorrentFilesAsync or similar)
            // This part depends on how TorrentService exposes file lists. Assuming GetTorrentFilesAsync works:
            // Need to wait for metadata if GetTorrentFilesAsync is used immediately after creation
            // await Task.Delay(1000); // Give some time for manager to potentially process metadata
            // try {
            //     var files = await _service1.GetTorrentFilesAsync(initialIdentifier);
            //     Assert.IsTrue(files.Any(f => f.EndsWith(Path.Combine("test_dir", "file1.txt"))), "File1 missing");
            //     Assert.IsTrue(files.Any(f => f.EndsWith(Path.Combine("test_dir", "subdir", "file2.dat"))), "File2 missing");
            // } catch (InvalidOperationException ex) when (ex.Message.Contains("metadata not yet available")) {
            //     Assert.Inconclusive("Metadata was not available in time to verify file list.");
            // }
        }

        [TestMethod]
        public async Task AddDirectory_ShouldPropagateUpdate()
        {
            Assert.IsNotNull(_service1);
            Assert.IsNotNull(_service2);
            Assert.IsNotNull(_baseDirectory1);
            Assert.IsNotNull(_baseDirectory2);

            // Service 1 creates initial torrent
            string initialFile = CreateDummyFile(_baseDirectory1!, "initial.txt", 10);
            var (magnetLink, privateKeySeed, initialIdentifier) = await _service1.CreateMutableTorrentAsync(initialFile);

            // Service 2 loads
            string downloadPath2 = Path.Combine(_baseDirectory2!, "downloads_add_dir");
            var loadedIdentifier = await _service2.LoadTorrentAsync(magnetLink, downloadPath2);
            Assert.AreEqual(initialIdentifier, loadedIdentifier);

            // Connect DHTs
            await ConnectDhtsAsync();

            // Setup event listener on Service 2
            var updateReceivedSignal = new TaskCompletionSource<MutableTorrentUpdateInfoEventArgs>();
            _service2.MutableTorrentUpdateAvailable += (sender, args) => {
                if (args.OriginalInfoHash == loadedIdentifier) {
                    updateReceivedSignal.TrySetResult(args);
                }
            };

            // Service 1 adds a directory
            string dirToAddPath = Path.Combine(_baseDirectory1!, "new_dir");
            Directory.CreateDirectory(dirToAddPath);
            CreateDummyFile(dirToAddPath, "new_file1.txt", 30);
            CreateDummyFile(Path.Combine(dirToAddPath, "new_subdir"), "new_file2.dat", 40);

            long newSeq1 = await _service1.AddFileToMutableTorrentAsync(initialIdentifier, dirToAddPath, privateKeySeed);
            Assert.AreEqual(1, newSeq1, "Sequence number after adding directory should be 1.");

            // Trigger update check on Service 2
            await _service2.TriggerMutableUpdateCheckAsync(loadedIdentifier);

            // Wait for update
            var receivedArgs = await WaitForUpdateAsync(updateReceivedSignal, 20000, "Service 2 did not receive the directory add update event.");
            Assert.IsNotNull(receivedArgs.NewInfoHash, "New InfoHash in event args should not be null.");
            Assert.AreNotEqual(initialIdentifier, receivedArgs.NewInfoHash, "New InfoHash should be different after adding directory.");

            // Optional: Verify file list on Service 2 after update
        }

        [TestMethod]
        public async Task RemoveDirectory_ShouldPropagateUpdate()
        {
            Assert.IsNotNull(_service1);
            Assert.IsNotNull(_service2);
            Assert.IsNotNull(_baseDirectory1);
            Assert.IsNotNull(_baseDirectory2);

            // Service 1 creates with a directory
            string dirPath = Path.Combine(_baseDirectory1!, "remove_dir_test");
            Directory.CreateDirectory(dirPath);
            CreateDummyFile(dirPath, "file_in_dir.txt", 15);
            CreateDummyFile(Path.Combine(dirPath, "subdir"), "another_file.dat", 25);
            var (magnetLink, privateKeySeed, initialIdentifier) = await _service1.CreateMutableTorrentAsync(dirPath);

            // Service 2 loads
            string downloadPath2 = Path.Combine(_baseDirectory2!, "downloads_remove_dir");
            var loadedIdentifier = await _service2.LoadTorrentAsync(magnetLink, downloadPath2);
            Assert.AreEqual(initialIdentifier, loadedIdentifier);

            // Connect DHTs
            await ConnectDhtsAsync();

            // Setup event listener on Service 2
            var updateReceivedSignal = new TaskCompletionSource<MutableTorrentUpdateInfoEventArgs>();
            _service2.MutableTorrentUpdateAvailable += (sender, args) => {
                if (args.OriginalInfoHash == loadedIdentifier) {
                    updateReceivedSignal.TrySetResult(args);
                }
            };

            // Service 1 removes the directory
            // Note: The path used for removal should be relative to the torrent root.
            // Since dirPath was the root, we use its name.
            string dirToRemoveRelative = Path.GetFileName(dirPath);
            long newSeq1 = await _service1.RemoveFileFromMutableTorrentAsync(initialIdentifier, dirToRemoveRelative, privateKeySeed);
            Assert.AreEqual(1, newSeq1, "Sequence number after removing directory should be 1.");

            // Trigger update check on Service 2
            await _service2.TriggerMutableUpdateCheckAsync(loadedIdentifier);

            // Wait for update
            var receivedArgs = await WaitForUpdateAsync(updateReceivedSignal, 20000, "Service 2 did not receive the directory remove update event.");
            Assert.IsNotNull(receivedArgs.NewInfoHash, "New InfoHash in event args should not be null after remove.");
            Assert.AreNotEqual(initialIdentifier, receivedArgs.NewInfoHash, "New InfoHash should be different after removing directory.");

            // Optional: Verify file list on Service 2 after update (should be empty or only contain other top-level files if added)
        }

        // Helper to connect DHTs
        private async Task ConnectDhtsAsync()
        {
             if (_service1?.DhtAccess != null && _service2?.DhtAccess != null && _settings1?.DhtEndPoint != null && _settings2?.DhtEndPoint != null)
            {
                var dummyId1 = MonoTorrent.Dht.NodeId.Create();
                var dummyId2 = MonoTorrent.Dht.NodeId.Create();

                byte[] nodeInfo1 = new byte[26];
                dummyId1.Span.CopyTo(nodeInfo1.AsSpan(0, 20));
                _settings1.DhtEndPoint.Address.GetAddressBytes().CopyTo(nodeInfo1.AsSpan(20, 4));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(nodeInfo1.AsSpan(24, 2), (ushort)_settings1.DhtEndPoint.Port);

                byte[] nodeInfo2 = new byte[26];
                dummyId2.Span.CopyTo(nodeInfo2.AsSpan(0, 20));
                _settings2.DhtEndPoint.Address.GetAddressBytes().CopyTo(nodeInfo2.AsSpan(20, 4));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(nodeInfo2.AsSpan(24, 2), (ushort)_settings2.DhtEndPoint.Port);

                _service2.DhtAccess.Add(new ReadOnlyMemory<byte>[] { nodeInfo1 });
                _service1.DhtAccess.Add(new ReadOnlyMemory<byte>[] { nodeInfo2 });

                await Task.Delay(1000); // Give time for nodes to potentially ping/pong
                Console.WriteLine("[Test Helper] Explicitly added DHT nodes to each other.");
            }
            else
            {
                Console.WriteLine("[Test Helper] Could not connect DHTs: Service or settings were null.");
            }
        }

        // Helper to wait for update event
        private async Task<MutableTorrentUpdateInfoEventArgs> WaitForUpdateAsync(TaskCompletionSource<MutableTorrentUpdateInfoEventArgs> signal, int timeoutMs, string timeoutMessage)
        {
            Console.WriteLine($"[Test Helper {_service2?.GetHashCode()}] Waiting for update signal...");
            var completedTask = await Task.WhenAny(signal.Task, Task.Delay(timeoutMs));
            if (completedTask != signal.Task)
            {
                Assert.Fail(timeoutMessage);
            }
            Console.WriteLine($"[Test Helper {_service2?.GetHashCode()}] Update signal received.");
            return await signal.Task; // Return the actual result
        }



        [TestMethod]
        public async Task AddDeeplyNestedDirectory_ShouldPropagateUpdate()
        {
            Assert.IsNotNull(_service1);
            Assert.IsNotNull(_service2);
            Assert.IsNotNull(_baseDirectory1);
            Assert.IsNotNull(_baseDirectory2);

            // Service 1 creates initial torrent
            string initialFile = CreateDummyFile(_baseDirectory1!, "root.txt", 5);
            var (magnetLink, privateKeySeed, initialIdentifier) = await _service1.CreateMutableTorrentAsync(initialFile);

            // Service 2 loads
            string downloadPath2 = Path.Combine(_baseDirectory2!, "downloads_add_deep_dir");
            var loadedIdentifier = await _service2.LoadTorrentAsync(magnetLink, downloadPath2);
            Assert.AreEqual(initialIdentifier, loadedIdentifier);

            // Connect DHTs
            await ConnectDhtsAsync();

            // Setup event listener on Service 2
            var updateReceivedSignal = new TaskCompletionSource<MutableTorrentUpdateInfoEventArgs>();
            _service2.MutableTorrentUpdateAvailable += (sender, args) => {
                if (args.OriginalInfoHash == loadedIdentifier) {
                    updateReceivedSignal.TrySetResult(args);
                }
            };

            // Service 1 adds a deeply nested directory
            string deepDirPath = Path.Combine(_baseDirectory1!, "level1", "level2", "level3");
            Directory.CreateDirectory(deepDirPath);
            CreateDummyFile(deepDirPath, "deep_file.txt", 60);
            CreateDummyFile(Path.Combine(_baseDirectory1!, "level1"), "level1_file.txt", 70);

            // Add the top-level directory containing the nested structure
            string dirToAddPath = Path.Combine(_baseDirectory1!, "level1");
            long newSeq1 = await _service1.AddFileToMutableTorrentAsync(initialIdentifier, dirToAddPath, privateKeySeed);
            Assert.AreEqual(1, newSeq1, "Sequence number after adding deep directory should be 1.");

            // Trigger update check on Service 2
            await _service2.TriggerMutableUpdateCheckAsync(loadedIdentifier);

            // Wait for update
            var receivedArgs = await WaitForUpdateAsync(updateReceivedSignal, 25000, "Service 2 did not receive the deep directory add update event."); // Increased timeout slightly
            Assert.IsNotNull(receivedArgs.NewInfoHash, "New InfoHash in event args should not be null.");
            Assert.AreNotEqual(initialIdentifier, receivedArgs.NewInfoHash, "New InfoHash should be different after adding deep directory.");

            // Optional: Verify file list on Service 2 after update
            // Requires GetTorrentFilesAsync and potentially waiting for metadata
        }

        [TestMethod]
        public async Task UpdateFileInNestedDirectory_ShouldPropagateUpdate()
        {
            Assert.IsNotNull(_service1);
            Assert.IsNotNull(_service2);
            Assert.IsNotNull(_baseDirectory1);
            Assert.IsNotNull(_baseDirectory2);

            // Service 1 creates with a nested structure
            string dirPath = Path.Combine(_baseDirectory1!, "update_test_dir");
            string subDirPath = Path.Combine(dirPath, "sub");
            Directory.CreateDirectory(subDirPath);
            string originalFilePath = CreateDummyFile(subDirPath, "file_to_update.txt", 100);
            var (magnetLink, privateKeySeed, initialIdentifier) = await _service1.CreateMutableTorrentAsync(dirPath);

            // Service 2 loads
            string downloadPath2 = Path.Combine(_baseDirectory2!, "downloads_update_nested");
            var loadedIdentifier = await _service2.LoadTorrentAsync(magnetLink, downloadPath2);
            Assert.AreEqual(initialIdentifier, loadedIdentifier);

            // Connect DHTs
            await ConnectDhtsAsync();

            // Setup event listener on Service 2 - Expect one update corresponding to the final state
            var updateReceivedSignal = new TaskCompletionSource<MutableTorrentUpdateInfoEventArgs>();
            _service2.MutableTorrentUpdateAvailable += (sender, args) => {
                if (args.OriginalInfoHash == loadedIdentifier) {
                    Console.WriteLine($"[Test Handler UpdateNested] Received final update event.");
                    updateReceivedSignal.TrySetResult(args); // Signal completion on the first (and only expected) update
                }
            };

            // Service 1 updates the file
            string updatedFilePath = CreateDummyFile(_baseDirectory1!, "updated_content.txt", 150); // Create new content elsewhere
            // Construct the relative path using forward slashes, as expected in torrent metadata
            // Construct the relative path using forward slashes, relative to the torrent's root
            string relativePathToUpdate = "sub/file_to_update.txt";

            await _service1.UpdateFileInMutableTorrentAsync(initialIdentifier, relativePathToUpdate, updatedFilePath, privateKeySeed);
            // UpdateFile calls Remove then Add, so sequence number should be 2
            // We need a way to get the *current* sequence number from service1, or assume it's 2.
            // Let's skip sequence number assertion here as UpdateFile doesn't return it directly.

            // Trigger update check on Service 2
            await _service2.TriggerMutableUpdateCheckAsync(loadedIdentifier);

            // Wait for the update event corresponding to the final state (after the Add part of Update)
            var receivedArgs = await WaitForUpdateAsync(updateReceivedSignal, 30000, "Service 2 did not receive the update event for nested file update."); // Increased timeout

            // We only expect one event reflecting the final state
            Assert.IsNotNull(receivedArgs.NewInfoHash, "New InfoHash in final event args should not be null.");
            Assert.AreNotEqual(initialIdentifier, receivedArgs.NewInfoHash, "New InfoHash should be different after updating nested file.");

            // Optional: Verify file content/hash on Service 2 after update (requires download completion)
        }

        // --- Test Methods End ---

    }
}
