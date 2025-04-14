using NUnit.Framework;
using NUnit.Framework.Legacy; // Add this for ClassicAssert
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;
using NATS.Client.Core;
using MonoTorrent.Dht;
using MonoTorrent.BEncoding; // Added for BEncodedDictionary
using System.Security.Cryptography;
using System.Text; // Added for Encoding

namespace TorrentWrapper.Tests
{
    [TestFixture]
    public class TorrentServiceTests
    {
        private string _tempDirectory = null!;
        private string _downloadDirectory = null!;
        private string _cacheDirectory = null!;
        private string _metadataDirectory = null!;
        private string _fastResumeDirectory = null!;
        private TorrentService _service = null!;
        private EngineSettings _settings = null!;

        // Consider making NATS optional or using a mock/test server if available
        private static readonly string NatsServerUrl = "nats://demo.nats.io:4222"; // Use public demo server for tests
        // NATS options are now configured via EngineSettings
        private static readonly NatsOpts? TestNatsOptions = NatsOpts.Default with { Url = NatsServerUrl }; // Keep this for easy access in tests

        [SetUp]
        public void Setup()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            _downloadDirectory = Path.Combine(_tempDirectory, "Downloads");
            _cacheDirectory = Path.Combine(_tempDirectory, "Cache");
            _metadataDirectory = Path.Combine(_cacheDirectory, "metadata"); // Consistent with EngineSettings defaults
            _fastResumeDirectory = Path.Combine(_cacheDirectory, "fastresume"); // Consistent with EngineSettings defaults

            Directory.CreateDirectory(_tempDirectory);
            Directory.CreateDirectory(_downloadDirectory);
            Directory.CreateDirectory(_cacheDirectory);
            Directory.CreateDirectory(_metadataDirectory);
            Directory.CreateDirectory(_fastResumeDirectory);

            // Base settings used by most tests. NATS test will override some.
            _settings = new EngineSettingsBuilder
            {
                AllowPortForwarding = false, // Default to false, NATS test enables it
                AllowLocalPeerDiscovery = false,
                AllowMultipleTorrentInstances = true,
                AutoSaveLoadFastResume = false,
                AutoSaveLoadMagnetLinkMetadata = true,
                CacheDirectory = _cacheDirectory,
                // Use IPAddress.Any for DHT/Listen endpoints by default, similar to reference test setup
                // Tests not needing network can function, NATS test relies on this.
                DhtEndPoint = new IPEndPoint(IPAddress.Any, 0),
                ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(IPAddress.Any, 0) } },
                // NATS settings default to disabled here
                AllowNatsDiscovery = false,
                NatsOptions = null
            }.ToSettings();

            _service = new TorrentService(_settings); // Pass EngineSettings only
        }

        [TearDown]
        public async Task Teardown()
        {
            // Stop all torrents and dispose service
            if (_service != null)
            {
                 // Ensure all managers are stopped before disposing engine
                 var managers = _service.ListTorrents();
                 var stopTasks = managers.Select(m => m.StopAsync()).ToArray();
                 await Task.WhenAll(stopTasks);

                _service.Dispose();
            }

            // Clean up temporary directory
            try
            {
                if (Directory.Exists(_tempDirectory))
                {
                    Directory.Delete(_tempDirectory, true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to delete temp directory {_tempDirectory}: {ex.Message}");
            }
        }

        private string CreateDummyFile(string name, int sizeMB)
        {
            string path = Path.Combine(_downloadDirectory, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); // Ensure directory exists
            byte[] data = new byte[1024];
            var random = new Random();
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                for (int i = 0; i < sizeMB * 1024; i++)
                {
                    random.NextBytes(data);
                    fs.Write(data, 0, data.Length);
                }
            }
            return path;
        }

        // --- Test Cases ---

        [Test]
        public async Task InitializeService_Success()
        {
            await _service.InitializeAsync();
            // Basic assertion: Check if engine is running (or at least not throwing exceptions)
            // More specific checks could involve listener status if made public/testable
            ClassicAssert.IsNotNull(_service.Engine);
            // If NATS was configured, check if NatsService is not null
            if (TestNatsOptions != null)
            { // NatsService is internal to ClientEngine now, cannot assert directly here. Initialization success implies it worked if configured. }
                Console.WriteLine("InitializeService_Success: Passed");
            }
        }

        [Test]
        public async Task CreateTorrent_FromFile_Success()
        {
            await _service.InitializeAsync();
            string dummyFilePath = CreateDummyFile("dummy_create.dat", 1); // Create 1MB file
            var fileSource = new TorrentFileSource(dummyFilePath);

            Torrent torrent = await _service.CreateTorrentAsync(fileSource);

            ClassicAssert.IsNotNull(torrent);
            ClassicAssert.AreEqual(Path.GetFileName(dummyFilePath), torrent.Name);
            ClassicAssert.IsTrue(torrent.Files.Count > 0);
            ClassicAssert.AreEqual(new FileInfo(dummyFilePath).Length, torrent.Size);

            // Verify metadata file was saved
            string expectedMetadataPath = Path.Combine(_metadataDirectory, torrent.InfoHashes.V1OrV2.ToHex() + ".torrent");
            ClassicAssert.IsTrue(File.Exists(expectedMetadataPath), "Metadata file was not saved.");
            Console.WriteLine("CreateTorrent_FromFile_Success: Passed");
        }

         [Test]
        public async Task CreateTorrent_FromDirectory_Success()
        {
            await _service.InitializeAsync();
            string subDir = Path.Combine(_downloadDirectory, "test_dir");
            Directory.CreateDirectory(subDir);
            string dummyFilePath1 = CreateDummyFile(Path.Combine("test_dir", "dummy1.dat"), 1);
            string dummyFilePath2 = CreateDummyFile(Path.Combine("test_dir", "dummy2.dat"), 2);

            var fileSource = new TorrentFileSource(_downloadDirectory); // Source is the parent directory

            Torrent torrent = await _service.CreateTorrentAsync(fileSource);

            ClassicAssert.IsNotNull(torrent);
            ClassicAssert.AreEqual(Path.GetFileName(_downloadDirectory), torrent.Name); // Name is the root folder name
            ClassicAssert.AreEqual(2, torrent.Files.Count);
            ClassicAssert.AreEqual(new FileInfo(dummyFilePath1).Length + new FileInfo(dummyFilePath2).Length, torrent.Size);

            // Verify metadata file was saved
            string expectedMetadataPath = Path.Combine(_metadataDirectory, torrent.InfoHashes.V1OrV2.ToHex() + ".torrent");
            ClassicAssert.IsTrue(File.Exists(expectedMetadataPath), "Metadata file was not saved.");
            Console.WriteLine("CreateTorrent_FromDirectory_Success: Passed");
        }

        [Test]
        public async Task LoadTorrent_FromFile_Success()
        {
            await _service.InitializeAsync();
            string dummyFilePath = CreateDummyFile("dummy_load.dat", 1);
            var fileSource = new TorrentFileSource(dummyFilePath);
            Torrent createdTorrent = await _service.CreateTorrentAsync(fileSource);
            string metadataPath = Path.Combine(_metadataDirectory, createdTorrent.InfoHashes.V1OrV2.ToHex() + ".torrent");

            TorrentManager manager = await _service.LoadTorrentAsync(metadataPath, _downloadDirectory);

            ClassicAssert.IsNotNull(manager);
            ClassicAssert.AreEqual(createdTorrent.InfoHashes, manager.InfoHashes);
            ClassicAssert.AreEqual(1, _service.ListTorrents().Count);
            ClassicAssert.IsTrue(_service.Torrents.ContainsKey(manager.InfoHashes));
            Console.WriteLine("LoadTorrent_FromFile_Success: Passed");
        }

         [Test]
        public async Task LoadTorrent_FromMagnet_Success()
        {
            await _service.InitializeAsync();
            // Create a dummy torrent first to get a valid magnet link
            string dummyFilePath = CreateDummyFile("dummy_magnet.dat", 1);
            var fileSource = new TorrentFileSource(dummyFilePath);
            Torrent createdTorrent = await _service.CreateTorrentAsync(fileSource);
            var magnetLink = new MagnetLink(createdTorrent.InfoHashes, createdTorrent.Name);

            TorrentManager manager = await _service.LoadTorrentAsync(magnetLink, _downloadDirectory);

            ClassicAssert.IsNotNull(manager);
            ClassicAssert.AreEqual(createdTorrent.InfoHashes, manager.InfoHashes);
            ClassicAssert.AreEqual(1, _service.ListTorrents().Count);
            ClassicAssert.IsTrue(_service.Torrents.ContainsKey(manager.InfoHashes));
            // Metadata might not be available immediately after loading from magnet
            // ClassicAssert.AreEqual(createdTorrent.Name, manager.Torrent?.Name);
            Console.WriteLine("LoadTorrent_FromMagnet_Success: Passed");
        }

        [Test]
        public async Task EditMetadata_ChangeCommentAndTracker_Success()
        {
            await _service.InitializeAsync();
            string dummyFilePath = CreateDummyFile("dummy_edit.dat", 1);
            var fileSource = new TorrentFileSource(dummyFilePath);
            Torrent createdTorrent = await _service.CreateTorrentAsync(fileSource, announceUrls: new List<IList<string>> { new List<string> { "http://original.tracker/announce" } });
            string metadataPath = Path.Combine(_metadataDirectory, createdTorrent.InfoHashes.V1OrV2.ToHex() + ".torrent");

            TorrentManager manager = await _service.LoadTorrentAsync(metadataPath, _downloadDirectory);
            ClassicAssert.IsNotNull(manager.Torrent);
            ClassicAssert.AreEqual("http://original.tracker/announce", manager.Torrent.AnnounceUrls?.FirstOrDefault()?.FirstOrDefault()); // Added null check for AnnounceUrls
            ClassicAssert.IsTrue(string.IsNullOrEmpty(manager.Torrent.Comment));

            string newComment = "Edited Comment";
            string newTracker = "http://new.tracker/announce";

            bool edited = await _service.EditTorrentMetadataAsync(manager.InfoHashes, editor =>
            {
                editor.Comment = newComment;
                editor.Announce = newTracker; // Overwrite single tracker
                editor.Announces.Clear(); // Clear multi-tracker list if Announce is set
            });

            ClassicAssert.IsTrue(edited, "EditTorrentMetadataAsync should return true.");

            // Reload the torrent from the modified file to verify changes
            Torrent reloadedTorrent = Torrent.Load(metadataPath);
            ClassicAssert.AreEqual(newComment, reloadedTorrent.Comment);
            ClassicAssert.AreEqual(newTracker, reloadedTorrent.AnnounceUrls.FirstOrDefault()?.FirstOrDefault());
            Console.WriteLine("EditMetadata_ChangeCommentAndTracker_Success: Passed");
        }

        [Test]
        public async Task RemoveTorrent_CacheOnly_RemovesCacheFiles()
        {
            _settings = new EngineSettingsBuilder(_settings) { AutoSaveLoadFastResume = true }.ToSettings(); // Enable fast resume for this test
            _service = new TorrentService(_settings); // Recreate service with new settings (NatsOptions are part of settings if needed)
            await _service.InitializeAsync();

            string dummyFilePath = CreateDummyFile("dummy_remove_cache.dat", 1);
            var fileSource = new TorrentFileSource(dummyFilePath);
            Torrent createdTorrent = await _service.CreateTorrentAsync(fileSource);
            string metadataPath = Path.Combine(_metadataDirectory, createdTorrent.InfoHashes.V1OrV2.ToHex() + ".torrent");
            string fastResumePath = Path.Combine(_fastResumeDirectory, createdTorrent.InfoHashes.V1OrV2.ToHex() + ".fresume");

            TorrentManager manager = await _service.LoadTorrentAsync(metadataPath, _downloadDirectory);
            await manager.StartAsync();
            await Task.Delay(500); // Let it run briefly to potentially create fast resume
            await manager.StopAsync(); // Stop cleanly to trigger fast resume save

            ClassicAssert.IsTrue(File.Exists(metadataPath), "Metadata file should exist before removal.");
            ClassicAssert.IsTrue(File.Exists(fastResumePath), "FastResume file should exist before removal.");
            ClassicAssert.IsTrue(File.Exists(dummyFilePath), "Dummy file should exist before removal.");

            bool removed = await _service.RemoveTorrentAsync(manager, RemoveMode.CacheDataOnly);

            ClassicAssert.IsTrue(removed, "RemoveTorrentAsync should return true.");
            ClassicAssert.AreEqual(0, _service.ListTorrents().Count, "Torrent should be removed from service list.");
            ClassicAssert.IsFalse(File.Exists(metadataPath), "Metadata file should be deleted.");
            ClassicAssert.IsFalse(File.Exists(fastResumePath), "FastResume file should be deleted.");
            ClassicAssert.IsTrue(File.Exists(dummyFilePath), "Dummy file should NOT be deleted."); // Verify downloaded data remains
            Console.WriteLine("RemoveTorrent_CacheOnly_RemovesCacheFiles: Passed");
        }

        [Test]
        public async Task RemoveTorrent_DownloadedOnly_RemovesDataFiles()
        {
             _settings = new EngineSettingsBuilder(_settings) { AutoSaveLoadFastResume = true }.ToSettings();
             _service = new TorrentService(_settings);
            await _service.InitializeAsync();

            string dummyFilePath = CreateDummyFile("dummy_remove_data.dat", 1);
            var fileSource = new TorrentFileSource(dummyFilePath);
            Torrent createdTorrent = await _service.CreateTorrentAsync(fileSource);
            string metadataPath = Path.Combine(_metadataDirectory, createdTorrent.InfoHashes.V1OrV2.ToHex() + ".torrent");
            string fastResumePath = Path.Combine(_fastResumeDirectory, createdTorrent.InfoHashes.V1OrV2.ToHex() + ".fresume");

            TorrentManager manager = await _service.LoadTorrentAsync(metadataPath, _downloadDirectory);
             await manager.StartAsync();
             await Task.Delay(500);
             await manager.StopAsync();

            ClassicAssert.IsTrue(File.Exists(metadataPath), "Metadata file should exist before removal.");
            ClassicAssert.IsTrue(File.Exists(fastResumePath), "FastResume file should exist before removal.");
            ClassicAssert.IsTrue(File.Exists(dummyFilePath), "Dummy file should exist before removal.");

            bool removed = await _service.RemoveTorrentAsync(manager, RemoveMode.DownloadedDataOnly);

            ClassicAssert.IsTrue(removed, "RemoveTorrentAsync should return true.");
            ClassicAssert.AreEqual(0, _service.ListTorrents().Count, "Torrent should be removed from service list.");
            ClassicAssert.IsTrue(File.Exists(metadataPath), "Metadata file should NOT be deleted."); // Verify cache remains
            ClassicAssert.IsTrue(File.Exists(fastResumePath), "FastResume file should NOT be deleted."); // Verify cache remains
            ClassicAssert.IsFalse(File.Exists(dummyFilePath), "Dummy file should be deleted."); // Verify downloaded data removed
            Console.WriteLine("RemoveTorrent_DownloadedOnly_RemovesDataFiles: Passed");
        }

         [Test]
        public async Task RemoveTorrent_CacheAndDownloaded_RemovesAll()
        {
             _settings = new EngineSettingsBuilder(_settings) { AutoSaveLoadFastResume = true }.ToSettings();
             _service = new TorrentService(_settings);
            await _service.InitializeAsync();

            string dummyFilePath = CreateDummyFile("dummy_remove_all.dat", 1);
            var fileSource = new TorrentFileSource(dummyFilePath);
            Torrent createdTorrent = await _service.CreateTorrentAsync(fileSource);
            string metadataPath = Path.Combine(_metadataDirectory, createdTorrent.InfoHashes.V1OrV2.ToHex() + ".torrent");
            string fastResumePath = Path.Combine(_fastResumeDirectory, createdTorrent.InfoHashes.V1OrV2.ToHex() + ".fresume");

            TorrentManager manager = await _service.LoadTorrentAsync(metadataPath, _downloadDirectory);
             await manager.StartAsync();
             await Task.Delay(500);
             await manager.StopAsync();

            ClassicAssert.IsTrue(File.Exists(metadataPath), "Metadata file should exist before removal.");
            ClassicAssert.IsTrue(File.Exists(fastResumePath), "FastResume file should exist before removal.");
            ClassicAssert.IsTrue(File.Exists(dummyFilePath), "Dummy file should exist before removal.");

            bool removed = await _service.RemoveTorrentAsync(manager, RemoveMode.CacheDataAndDownloadedData);

            ClassicAssert.IsTrue(removed, "RemoveTorrentAsync should return true.");
            ClassicAssert.AreEqual(0, _service.ListTorrents().Count, "Torrent should be removed from service list.");
            ClassicAssert.IsFalse(File.Exists(metadataPath), "Metadata file should be deleted.");
            ClassicAssert.IsFalse(File.Exists(fastResumePath), "FastResume file should be deleted.");
            ClassicAssert.IsFalse(File.Exists(dummyFilePath), "Dummy file should be deleted.");
            Console.WriteLine("RemoveTorrent_CacheAndDownloaded_RemovesAll: Passed");
        }
        [Test]
        [Category("Integration")] // Requires external NATS server
        [Category("RequiresNats")]
        public async Task NatsDiscovery_TwoInstances_DiscoverEachOther()
        {
            if (TestNatsOptions == null)
            {
                ClassicAssert.Ignore("NATS tests are disabled. Set TestNatsOptions to run.");
                return; // Explicit return to satisfy compiler nullability analysis
            }

            // --- Instance A Setup ---
            string instanceADownloadDir = Path.Combine(_tempDirectory, "NatsA_Downloads");
            string instanceACacheDir = Path.Combine(_tempDirectory, "NatsA_Cache");
            Directory.CreateDirectory(instanceADownloadDir);
            Directory.CreateDirectory(instanceACacheDir);
            // Settings for instance A (already includes NATS config from builder)
            var settingsA = new EngineSettingsBuilder(_settings) {
                CacheDirectory = instanceACacheDir,
                AllowPortForwarding = true,
                ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(IPAddress.Any, 0) } },
                AllowNatsDiscovery = true,
                NatsOptions = TestNatsOptions
            }.ToSettings();
            var serviceA = new TorrentService(settingsA);

            // --- Instance B Setup ---
            string instanceBDownloadDir = Path.Combine(_tempDirectory, "NatsB_Downloads");
            string instanceBCacheDir = Path.Combine(_tempDirectory, "NatsB_Cache");
            Directory.CreateDirectory(instanceBDownloadDir);
            Directory.CreateDirectory(instanceBCacheDir);
            // Settings for instance B (already includes NATS config from builder)
            var settingsB = new EngineSettingsBuilder(_settings) {
                CacheDirectory = instanceBCacheDir,
                AllowPortForwarding = true,
                ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(IPAddress.Any, 0) } },
                AllowNatsDiscovery = true,
                NatsOptions = TestNatsOptions
            }.ToSettings();
            var serviceB = new TorrentService(settingsB);

            try
            {
                // Initialize both services concurrently
                var initTaskA = serviceA.InitializeAsync();
                var initTaskB = serviceB.InitializeAsync();
                await Task.WhenAll(initTaskA, initTaskB);
                Console.WriteLine("[NATS Test] Both services initialized (engines started, NATS init attempted).");

                // Wait for discovery via NATS publish/subscribe
                Console.WriteLine("[NATS Test] Waiting for NATS peer discovery (e.g., 10 seconds)...");
                var discoveryTimeout = TimeSpan.FromSeconds(15); // Allow ample time for publish/subscribe/processing
                var discoverySw = System.Diagnostics.Stopwatch.StartNew();
                bool discoveredA = false;
                bool discoveredB = false;
                // Calculate NodeIds from the public PeerId, similar to how seederNodeId was calculated.
                NodeId idA, idB;
                using (var sha1 = System.Security.Cryptography.SHA1.Create()) {
                    var idABytes = sha1.ComputeHash(serviceA.Engine.PeerId.AsMemory().Span.ToArray());
                    idA = NodeId.FromInfoHash(InfoHash.FromMemory(idABytes));
                }
                using (var sha1 = System.Security.Cryptography.SHA1.Create()) {
                    var idBBytes = sha1.ComputeHash(serviceB.Engine.PeerId.AsMemory().Span.ToArray());
                    idB = NodeId.FromInfoHash(InfoHash.FromMemory(idBBytes));
                }


                ClassicAssert.AreNotEqual(default(NodeId), idA, "Service A NodeId should not be default/zero.");
                ClassicAssert.AreNotEqual(default(NodeId), idB, "Service B NodeId should not be default/zero.");
                ClassicAssert.AreNotEqual(idA, idB, "Service A and B should have different NodeIds.");


                while (discoverySw.Elapsed < discoveryTimeout && (!discoveredA || !discoveredB))
                {
                    var peersForA = serviceA.GetNatsDiscoveredPeers(); // Use public method
                    var peersForB = serviceB.GetNatsDiscoveredPeers(); // Use public method

                    discoveredA = peersForA?.ContainsKey(idB) ?? false;
                    discoveredB = peersForB?.ContainsKey(idA) ?? false;

                    if (!discoveredA || !discoveredB)
                        await Task.Delay(500);
                }
                discoverySw.Stop();
                Console.WriteLine($"[NATS Test] Discovery loop finished after {discoverySw.ElapsedMilliseconds}ms. A discovered B: {discoveredA}, B discovered A: {discoveredB}");

                ClassicAssert.IsTrue(discoveredA, "Service A did not discover Service B via NATS.");
                ClassicAssert.IsTrue(discoveredB, "Service B did not discover Service A via NATS.");
                // Note: The NodeId calculation is now internal to ClientEngine/NatsService.
                // We need a way to get the NodeId for assertion if required, perhaps expose it?
                // For now, we assume the GetNatsDiscoveredPeers check is sufficient.
                // NodeId idA = ???;
                // NodeId idB = ???;

                // Optional: Verify discovered endpoints look reasonable (e.g., not null)
                 var peersA = serviceA.GetNatsDiscoveredPeers();
                 var peersB = serviceB.GetNatsDiscoveredPeers();
                 ClassicAssert.IsNotNull(peersA?[idB], "Endpoint for B discovered by A should not be null.");
                 ClassicAssert.IsNotNull(peersB?[idA], "Endpoint for A discovered by B should not be null.");

                 Console.WriteLine($"[NATS Test] Endpoint for B discovered by A: {peersA?[idB]}");
                 Console.WriteLine($"[NATS Test] Endpoint for A discovered by B: {peersB?[idA]}");

                 Console.WriteLine("NatsDiscovery_TwoInstances_DiscoverEachOther: Passed");

            }
                finally
                {
                    // Cleanup both service instances
                    serviceA?.Dispose();
                    serviceB?.Dispose();
                }
            }
        

        [Test]
        [Category("Integration")] // Requires external NATS server and involves file transfer
        [Category("RequiresNats")]
        public async Task NatsDiscovery_AndTransfer_Success()
        {
            if (TestNatsOptions == null)
            {
                ClassicAssert.Ignore("NATS tests are disabled. Set TestNatsOptions to run.");
                return; // Explicit return
            }

            // --- Instance A (Seeder) Setup ---
            string instanceADownloadDir = Path.Combine(_tempDirectory, "NatsA_Source"); // Source dir for seeder
            string instanceACacheDir = Path.Combine(_tempDirectory, "NatsA_Cache");
            Directory.CreateDirectory(instanceADownloadDir);
            Directory.CreateDirectory(instanceACacheDir);
            var settingsA = new EngineSettingsBuilder(_settings) {
                CacheDirectory = instanceACacheDir,
                AllowPortForwarding = true,
                ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(IPAddress.Any, 0) } },
                AllowNatsDiscovery = true,
                NatsOptions = TestNatsOptions
            }.ToSettings();
            var serviceA = new TorrentService(settingsA);

            // --- Instance B (Downloader) Setup ---
            string instanceBDownloadDir = Path.Combine(_tempDirectory, "NatsB_Downloads"); // Target dir for downloader
            string instanceBCacheDir = Path.Combine(_tempDirectory, "NatsB_Cache");
            Directory.CreateDirectory(instanceBDownloadDir);
            Directory.CreateDirectory(instanceBCacheDir);
            var settingsB = new EngineSettingsBuilder(_settings) {
                CacheDirectory = instanceBCacheDir,
                AllowPortForwarding = true,
                ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(IPAddress.Any, 0) } },
                AllowNatsDiscovery = true,
                NatsOptions = TestNatsOptions
            }.ToSettings();
            var serviceB = new TorrentService(settingsB);

            TorrentManager? managerA = null;
            TorrentManager? managerB = null;

            try
            {
                // Initialize both services concurrently
                var initTaskA = serviceA.InitializeAsync();
                var initTaskB = serviceB.InitializeAsync();
                await Task.WhenAll(initTaskA, initTaskB);
                Console.WriteLine("[NATS Transfer Test] Both services initialized.");

                // --- NATS Discovery Verification (Copied from previous test) ---
                Console.WriteLine("[NATS Transfer Test] Waiting for NATS peer discovery...");
                var discoveryTimeout = TimeSpan.FromSeconds(20); // Increased timeout slightly
                var discoverySw = System.Diagnostics.Stopwatch.StartNew();
                bool discoveredA = false;
                bool discoveredB = false;
                NodeId idA, idB;
                using (var sha1 = SHA1.Create()) {
                    var idABytes = sha1.ComputeHash(serviceA.Engine.PeerId.AsMemory().Span.ToArray());
                    idA = NodeId.FromInfoHash(InfoHash.FromMemory(idABytes));
                }
                using (var sha1 = SHA1.Create()) {
                    var idBBytes = sha1.ComputeHash(serviceB.Engine.PeerId.AsMemory().Span.ToArray());
                    idB = NodeId.FromInfoHash(InfoHash.FromMemory(idBBytes));
                }

                while (discoverySw.Elapsed < discoveryTimeout && (!discoveredA || !discoveredB))
                {
                    var peersForA = serviceA.GetNatsDiscoveredPeers();
                    var peersForB = serviceB.GetNatsDiscoveredPeers();
                    discoveredA = peersForA?.ContainsKey(idB) ?? false;
                    discoveredB = peersForB?.ContainsKey(idA) ?? false;
                    if (!discoveredA || !discoveredB)
                        await Task.Delay(500);
                }
                discoverySw.Stop();
                Console.WriteLine($"[NATS Transfer Test] Discovery loop finished after {discoverySw.ElapsedMilliseconds}ms. A discovered B: {discoveredA}, B discovered A: {discoveredB}");
                ClassicAssert.IsTrue(discoveredA, "Service A did not discover Service B via NATS.");
                ClassicAssert.IsTrue(discoveredB, "Service B did not discover Service A via NATS.");
                Console.WriteLine("[NATS Transfer Test] Peer discovery successful.");
                Console.WriteLine("[NATS Transfer Test] Peer discovery successful.");

                // --- Torrent Creation and Loading ---
                string dummyFileName = "nats_transfer_test.dat";
                string dummyFilePathA = Path.Combine(instanceADownloadDir, dummyFileName); // Create file in A's dir
                CreateDummyFile(dummyFilePathA, 1); // Create 1MB dummy file in A's source directory
                var fileSource = new TorrentFileSource(dummyFilePathA);
                Torrent createdTorrent = await serviceA.CreateTorrentAsync(fileSource);
                string metadataPath = Path.Combine(settingsA.MetadataCacheDirectory, createdTorrent.InfoHashes.V1OrV2.ToHex() + ".torrent");
                ClassicAssert.IsTrue(File.Exists(metadataPath), "Metadata file was not created by Service A.");
                Console.WriteLine($"[NATS Transfer Test] Torrent created by Service A: {createdTorrent.Name}");

                // Load torrent into Service B (downloader)
                managerB = await serviceB.LoadTorrentAsync(metadataPath, instanceBDownloadDir); // Download to B's dir
                ClassicAssert.IsNotNull(managerB, "Service B failed to load the torrent.");
                Console.WriteLine($"[NATS Transfer Test] Torrent loaded by Service B. Save path: {instanceBDownloadDir}");

                // Load torrent into Service A (seeder) - needed to manage the seeding state
                managerA = await serviceA.LoadTorrentAsync(metadataPath, instanceADownloadDir);
                ClassicAssert.IsNotNull(managerA, "Service A failed to load the torrent for seeding.");
                Console.WriteLine("[NATS Transfer Test] Torrent loaded by Service A for seeding.");

                // --- Explicitly add discovered peer (ensure it's considered) ---
                // --- Explicitly add discovered peer USING LOCAL ENDPOINT (Bypass NAT Loopback/Firewall) ---
                var localEndpointA = serviceA.Engine.PeerListeners.FirstOrDefault()?.LocalEndPoint; // Get A's actual listening endpoint from the first listener
                if (localEndpointA != null)
                {
                    // Construct URI using loopback IP and the listener's port
                    var localPeerUri = new Uri($"ipv4://127.0.0.1:{localEndpointA.Port}");
                    var localPeerInfoA = new PeerInfo(localPeerUri, serviceA.Engine.PeerId); // Use A's PeerId
                    await managerB.AddPeerAsync(localPeerInfoA);
                } else {
                     // Fallback to NATS discovered endpoint (might still fail)
                     var peersB = serviceB.GetNatsDiscoveredPeers();
                     if (peersB != null && peersB.TryGetValue(idA, out var natsEndpointA))
                     {
                         var natsPeerInfoA = new PeerInfo(new Uri($"ipv4://{natsEndpointA}"), serviceA.Engine.PeerId);
                         await managerB.AddPeerAsync(natsPeerInfoA);
                     } else {
                     }
                }
                // Give the ConnectionManager a moment to process the added peer
                await Task.Delay(1000); // Reduced delay slightly as local connection should be faster

                // --- Start Download/Seed ---
                var startTaskA = serviceA.DownloadTorrentAsync(managerA.InfoHashes); // Start seeding
                var startTaskB = serviceB.DownloadTorrentAsync(managerB.InfoHashes); // Start downloading
                await Task.WhenAll(startTaskA, startTaskB);
                Console.WriteLine("[NATS Transfer Test] Both torrent managers started.");

                // --- Wait for Download Completion ---
                var downloadTimeout = TimeSpan.FromSeconds(15); // Timeout for the download
                var downloadSw = System.Diagnostics.Stopwatch.StartNew();
                while (managerB.State != TorrentState.Seeding && downloadSw.Elapsed < downloadTimeout)
                {
                    if (managerB.State == TorrentState.Error)
                        ClassicAssert.Fail($"Downloader entered Error state: {managerB.Error?.Reason} - {managerB.Error?.Exception?.Message}");
                     // Add progress bar display
                     var dlRate = managerB.Monitor.DownloadRate; // Use DownloadRate
                     var ulRate = managerB.Monitor.UploadRate; // Use UploadRate
                     var connectedPeers = managerB.Peers.ConnectedPeers.Count; // More specific than OpenConnections
                     int progressBarWidth = 20;
                     int progressChars = (int)(progressBarWidth * (managerB.Progress / 100.0));
                     string progressBar = $"[{new string('#', progressChars)}{new string('-', progressBarWidth - progressChars)}]";
                     Console.WriteLine($"[NATS Transfer Test] B State: {managerB.State}, {progressBar} {managerB.Progress:F1}%, Peers: {connectedPeers}, DL: {dlRate / 1024.0:F1} kB/s, UL: {ulRate / 1024.0:F1} kB/s");

                    await Task.Delay(1000); // Check every second
                }
                downloadSw.Stop();

                // --- Assertions ---
                ClassicAssert.AreEqual(TorrentState.Seeding, managerB.State, "Downloader did not reach Seeding state within timeout.");
                ClassicAssert.AreEqual(100.0, managerB.Progress, 0.01, "Download did not complete to 100%.");

                string downloadedFilePathB = Path.Combine(instanceBDownloadDir, dummyFileName);
                ClassicAssert.IsTrue(File.Exists(downloadedFilePathB), "Downloaded file does not exist in Service B's directory.");
                ClassicAssert.AreEqual(new FileInfo(dummyFilePathA).Length, new FileInfo(downloadedFilePathB).Length, "Downloaded file size does not match original.");

                Console.WriteLine("NatsDiscovery_AndTransfer_Success: Passed");
            }
            finally
            {
                // Cleanup both service instances
                // Stop managers explicitly before disposing services
                if (managerA != null && managerA.State != TorrentState.Stopped) await managerA.StopAsync();
                if (managerB != null && managerB.State != TorrentState.Stopped) await managerB.StopAsync();
                serviceA?.Dispose();
                serviceB?.Dispose();
            }
        }

        // --- BEP44/46 Mutable Torrent Tests ---

        // Helper to generate placeholder Ed25519 keys (replace with actual generation if library available)
        private (byte[] publicKey, byte[] privateKey) GeneratePlaceholderKeys()
        {
            // IMPORTANT: These are NOT valid cryptographic keys. Use a library like Chaos.NaCl or BouncyCastle for real keys.
            var publicKey = new byte[32];
            var privateKey = new byte[64]; // Typically 32 bytes seed + 32 bytes public key
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(publicKey);
                rng.GetBytes(privateKey); // Fill with random data for placeholder
            }
            // Ensure private key's second half matches public key for typical Ed25519 representation
            // Array.Copy(publicKey, 0, privateKey, 32, 32); // This line might be needed depending on the crypto library used by MonoTorrent's PutMutableAsync for signing. Commented out for placeholder.
            return (publicKey, privateKey);
        }

        [Test]
        public async Task CreateMutableTorrent_Success()
        {
            await _service.InitializeAsync();
            string dummyFilePath = CreateDummyFile("dummy_mutable_create.dat", 1);
            var fileSource = new TorrentFileSource(dummyFilePath);
            var (publicKey, _) = GeneratePlaceholderKeys(); // Private key not needed for creation check
            long initialSequence = 5;
            byte[] salt = Encoding.UTF8.GetBytes("test-salt");

            Torrent torrent = await _service.CreateTorrentAsync(
                fileSource,
                publicKey: publicKey,
                sequenceNumber: initialSequence,
                salt: salt);

            ClassicAssert.IsNotNull(torrent);
            ClassicAssert.AreEqual(Path.GetFileName(dummyFilePath), torrent.Name);

            // Verify metadata file was saved and contains mutable keys
            string expectedMetadataPath = Path.Combine(_metadataDirectory, torrent.InfoHashes.V1OrV2.ToHex() + ".torrent");
            ClassicAssert.IsTrue(File.Exists(expectedMetadataPath), "Metadata file was not saved.");

            // Load the dictionary and check keys
            byte[] torrentBytes = await File.ReadAllBytesAsync(expectedMetadataPath);
            var torrentDict = BEncodedValue.Decode<BEncodedDictionary>(torrentBytes);

            ClassicAssert.IsTrue(torrentDict.ContainsKey("pk"), "Torrent dictionary missing 'pk' key.");
            ClassicAssert.AreEqual(publicKey, ((BEncodedString)torrentDict["pk"]).Span.ToArray(), "'pk' key mismatch.");

            ClassicAssert.IsTrue(torrentDict.ContainsKey("seq"), "Torrent dictionary missing 'seq' key.");
            ClassicAssert.AreEqual(initialSequence, ((BEncodedNumber)torrentDict["seq"]).Number, "'seq' key mismatch.");

            ClassicAssert.IsTrue(torrentDict.ContainsKey("salt"), "Torrent dictionary missing 'salt' key.");
            ClassicAssert.AreEqual(salt, ((BEncodedString)torrentDict["salt"]).Span.ToArray(), "'salt' key mismatch.");

            Console.WriteLine("CreateMutableTorrent_Success: Passed");
        }

        [Test]
        public async Task LoadMutableTorrent_FromMagnet_Success()
        {
            await _service.InitializeAsync();
            var (publicKey, _) = GeneratePlaceholderKeys();
            string pkHex = Convert.ToHexString(publicKey).ToLowerInvariant(); // BEP9 uses lowercase hex
            // Construct a dummy InfoHash (not cryptographically linked to pk, just for magnet structure)
            byte[] dummyInfoHashBytes = new byte[20];
            RandomNumberGenerator.Fill(dummyInfoHashBytes);
            var dummyInfoHash = InfoHash.FromMemory(dummyInfoHashBytes);

            // Construct magnet link with pk
            string magnetUri = $"magnet:?xt=urn:btih:{dummyInfoHash.ToHex()}&dn=MutableTest&xt=urn:btpk:{pkHex}";
            var magnetLink = MagnetLink.Parse(magnetUri);

            // Verify parsing worked (optional, depends on MagnetLink implementation)
            // ClassicAssert.IsTrue(magnetLink.AnnounceUrls.Any(url => url.Contains(pkHex)), "MagnetLink AnnounceUrls should contain pk");

            TorrentManager manager = await _service.LoadTorrentAsync(magnetLink, _downloadDirectory);

            ClassicAssert.IsNotNull(manager);
            // We can't easily assert that DHT lookups *will* happen without more complex setup,
            // but loading the manager successfully is the first step.
            ClassicAssert.AreEqual(1, _service.ListTorrents().Count);
            ClassicAssert.IsTrue(_service.Torrents.ContainsKey(manager.InfoHashes)); // Infohash will be the dummy one initially

            Console.WriteLine("LoadMutableTorrent_FromMagnet_Success: Passed");
        }

        [Test]
        // Removed RequiresReflection category
        public async Task PublishMutableTorrentUpdate_Success() // Renamed test
        {
            await _service.InitializeAsync();
            string dummyFilePath = CreateDummyFile("dummy_mutable_publish.dat", 1);
            var fileSource = new TorrentFileSource(dummyFilePath);
            var (publicKey, privateKey) = GeneratePlaceholderKeys();
            long initialSequence = 10;
            byte[] salt = Encoding.UTF8.GetBytes("publish-salt");

            // 1. Create the initial mutable torrent
            Torrent torrent = await _service.CreateTorrentAsync(
                fileSource,
                publicKey: publicKey,
                sequenceNumber: initialSequence,
                salt: salt);
            string metadataPath = Path.Combine(_metadataDirectory, torrent.InfoHashes.V1OrV2.ToHex() + ".torrent");
            byte[] initialTorrentBytes = await File.ReadAllBytesAsync(metadataPath);
            var initialTorrentDict = BEncodedValue.Decode<BEncodedDictionary>(initialTorrentBytes);

            // 2. Load the torrent (optional, but good practice)
            // TorrentManager manager = await _service.LoadTorrentAsync(metadataPath, _downloadDirectory);
            // ClassicAssert.IsNotNull(manager);

            // 3. Prepare the updated dictionary
            long updatedSequence = initialSequence + 1;
            // Correctly clone the dictionary
            var updatedTorrentDict = new BEncodedDictionary();
            foreach (var kvp in initialTorrentDict)
            {
                // Shallow copy is sufficient here as BEncodedValue types are typically immutable or handled correctly.
                // Crucially, the 'info' dictionary reference is copied, which is required by BEP44/46.
                updatedTorrentDict.Add(kvp.Key, kvp.Value);
            }
            updatedTorrentDict["seq"] = new BEncodedNumber(updatedSequence); // Update sequence number
            updatedTorrentDict["comment"] = new BEncodedString("Updated via publish test"); // Add/change something
            // IMPORTANT: The 'info' dictionary MUST remain unchanged. The shallow copy ensures this.

            // 4. Attempt to publish the update using the reflection-based method
            bool published = await _service.PublishTorrentUpdateAsync(
                publicKey,
                privateKey,
                updatedSequence,
                updatedTorrentDict,
                salt);

            // 5. Assert
            // This assertion primarily checks if the reflection call executed without throwing an exception.
            // It does NOT guarantee the data was accepted by the DHT network.
            ClassicAssert.IsTrue(published, "PublishTorrentUpdateAsync (via Reflection) should return true on success.");

            Console.WriteLine("PublishMutableTorrentUpdate_Reflection_Success: Passed (Reflection call successful)");
        }

        [Test]
        [Category("Integration")] // Requires DHT interaction
        [CancelAfter(60000)] // Use recommended attribute
        public async Task MutableTorrent_PublishRetrieveUpdateRetrieve_Success()
        {
            // --- Instance A (Publisher) Setup ---
            string instanceACacheDir = Path.Combine(_tempDirectory, "E2E_A_Cache");
            Directory.CreateDirectory(instanceACacheDir);
            var settingsA = new EngineSettingsBuilder(_settings) {
                CacheDirectory = instanceACacheDir,
                // Use different ports to avoid conflicts if running locally
                DhtEndPoint = new IPEndPoint(IPAddress.Loopback, 55551), // Use Loopback
                ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(IPAddress.Any, 55552) } }
            }.ToSettings();
            var serviceA = new TorrentService(settingsA);

            // --- Instance B (Retriever) Setup ---
            string instanceBCacheDir = Path.Combine(_tempDirectory, "E2E_B_Cache");
            Directory.CreateDirectory(instanceBCacheDir);
            var settingsB = new EngineSettingsBuilder(_settings) {
                CacheDirectory = instanceBCacheDir,
                DhtEndPoint = new IPEndPoint(IPAddress.Loopback, 55561), // Use Loopback
                ListenEndPoints = new Dictionary<string, IPEndPoint> { { "ipv4", new IPEndPoint(IPAddress.Any, 55562) } } // Use Loopback for listener too } }
            }.ToSettings();
            var serviceB = new TorrentService(settingsB);

            try
            {
                // Initialize both services, disabling default DHT bootstrapping
                var bootstrapRouters = Array.Empty<string>();
                await Task.WhenAll(serviceA.InitializeAsync(bootstrapRouters), serviceB.InitializeAsync(bootstrapRouters));
                Console.WriteLine("[E2E Test] Both services initialized (no default bootstrap).");

                // Removed manual node addition. Relying on automatic discovery/hair pinning.
                // The DHT engines should ideally discover each other through initial pings
                // or other mechanisms when configured with loopback addresses.

                // Give some time for the engines to potentially discover each other automatically.
                // The exact mechanism depends on MonoTorrent's DHT implementation details.
                // If they send initial pings or respond to each other's startup messages,
                // they might get added to the routing table.
                // to be received and processed, leading to routing table updates.
                // Increase delay to 20 seconds to ensure initial pings complete or timeout
                Console.WriteLine("[E2E Test] Waiting 20 seconds for routing tables to populate...");
                await Task.Delay(TimeSpan.FromSeconds(20));

                // Now verify the routing table counts directly
                Console.WriteLine("[E2E Test] Verifying routing table counts...");
                var dhtEngineA = serviceA.Engine.DhtEngine as MonoTorrent.Dht.DhtEngine;
                var dhtEngineB = serviceB.Engine.DhtEngine as MonoTorrent.Dht.DhtEngine;

                ClassicAssert.IsNotNull(dhtEngineA, "Service A's DHT Engine is null or not the expected type.");
                ClassicAssert.IsNotNull(dhtEngineB, "Service B's DHT Engine is null or not the expected type.");

                int countA = dhtEngineA?.RoutingTable.CountNodes() ?? -1;
                int countB = dhtEngineB?.RoutingTable.CountNodes() ?? -1;

                Console.WriteLine($"[E2E Test] Final Check - Node Count A: {countA}. Node Count B: {countB}.");
                // Each table should contain exactly one node: the other peer.
                // Each table should contain 2 nodes: self and the other peer.
                ClassicAssert.AreEqual(2, countA, "Node A's routing table should contain 2 nodes (self and Node B).");
                ClassicAssert.AreEqual(2, countB, "Node B's routing table should contain 2 nodes (self and Node A).");
                Console.WriteLine("[E2E Test] Routing tables populated successfully (checked counts).");

                // --- Publish Initial Version (A) ---
                var (publicKey, privateKey) = GeneratePlaceholderKeys();
                long sequence1 = 1;
                byte[]? salt = null; // No salt for simplicity
                var value1 = new BEncodedString("Initial Value");
                var dictToPublish1 = new BEncodedDictionary { { "v", value1 } };

                Console.WriteLine($"[E2E Test] Service A Publishing V1 (Seq {sequence1})...");
                bool published1 = await serviceA.PublishTorrentUpdateAsync(publicKey, privateKey, sequence1, dictToPublish1, salt);
                ClassicAssert.IsTrue(published1, "Service A failed to publish initial version.");
                Console.WriteLine("[E2E Test] Service A Published V1.");

                // Verify local storage on A after Put V1
                var bPublicKey = new BEncodedString(publicKey);
                var targetId = DhtEngine.CalculateMutableTargetId(bPublicKey, null); // Assuming salt is null
                Console.WriteLine($"[E2E Test] Verifying local storage on A for V1 (Target {targetId})...");
                // Allow some time for the Put operation's local storage update to complete
                await Task.Delay(500);
                ClassicAssert.IsTrue(serviceA.Engine.DhtLocalStorage.TryGetValue(targetId, out var storedItem1), "Item V1 not found in Service A local DHT storage after publish.");
                ClassicAssert.AreEqual(sequence1, storedItem1.SequenceNumber, "Local storage V1 sequence number mismatch.");
                ClassicAssert.AreEqual(value1.Text, ((BEncodedString)storedItem1.Value).Text, "Local storage V1 value mismatch.");
                Console.WriteLine($"[E2E Test] Verified local storage on A for V1.");

                // --- Retrieve Initial Version (B) ---
                // Target ID already calculated
                Console.WriteLine($"[E2E Test] Service B Retrieving V1 (Target {targetId})...");
                // Add a small delay to allow Put V1 to propagate/be processed by B before Get V1
                await Task.Delay(2000); // Increased delay
                var (retrievedValue1, _, _, retrievedSeq1) = await serviceB.Engine.GetMutableAsync(targetId);

                ClassicAssert.IsNotNull(retrievedValue1, "Service B failed to retrieve V1 value.");
                ClassicAssert.AreEqual(value1.Text, ((BEncodedString)retrievedValue1!).Text, "Retrieved V1 value mismatch.");
                ClassicAssert.IsNotNull(retrievedSeq1, "Service B failed to retrieve V1 sequence number.");
                ClassicAssert.AreEqual(sequence1, retrievedSeq1!.Value, "Retrieved V1 sequence number mismatch.");
                Console.WriteLine($"[E2E Test] Service B Retrieved V1 (Seq {retrievedSeq1}).");

                // --- Publish Updated Version (A) ---
                long sequence2 = sequence1 + 1;
                var value2 = new BEncodedString("Updated Value");
                var dictToPublish2 = new BEncodedDictionary { { "v", value2 } };

                Console.WriteLine($"[E2E Test] Service A Publishing V2 (Seq {sequence2})...");
                bool published2 = await serviceA.PublishTorrentUpdateAsync(publicKey, privateKey, sequence2, dictToPublish2, salt);
                ClassicAssert.IsTrue(published2, "Service A failed to publish updated version.");
                Console.WriteLine("[E2E Test] Service A Published V2.");

                // Verify local storage on A updated after Put V2
                Console.WriteLine($"[E2E Test] Verifying local storage on A for V2 (Target {targetId})...");
                 // Allow some time for the Put operation's local storage update to complete
                await Task.Delay(500);
                ClassicAssert.IsTrue(serviceA.Engine.DhtLocalStorage.TryGetValue(targetId, out var storedItem2), "Item V2 not found in Service A local DHT storage after publish.");
                ClassicAssert.AreEqual(sequence2, storedItem2.SequenceNumber, "Local storage V2 sequence number mismatch.");
                ClassicAssert.AreEqual(value2.Text, ((BEncodedString)storedItem2.Value).Text, "Local storage V2 value mismatch.");
                Console.WriteLine($"[E2E Test] Verified local storage on A for V2.");

                // --- Retrieve Updated Version (B) ---
                Console.WriteLine($"[E2E Test] Service B Retrieving V2 (Target {targetId})...");
                 // Add a small delay to allow Put V2 to propagate/be processed by B before Get V2
                await Task.Delay(2000); // Increased delay
                var (retrievedValue2, _, _, retrievedSeq2) = await serviceB.Engine.GetMutableAsync(targetId);

                ClassicAssert.IsNotNull(retrievedValue2, "Service B failed to retrieve V2 value.");
                ClassicAssert.AreEqual(value2.Text, ((BEncodedString)retrievedValue2!).Text, "Retrieved V2 value mismatch.");
                ClassicAssert.IsNotNull(retrievedSeq2, "Service B failed to retrieve V2 sequence number.");
                ClassicAssert.AreEqual(sequence2, retrievedSeq2!.Value, "Retrieved V2 sequence number mismatch.");
                Console.WriteLine($"[E2E Test] Service B Retrieved V2 (Seq {retrievedSeq2}).");

                Console.WriteLine("MutableTorrent_PublishRetrieveUpdateRetrieve_Success: Passed");
            }
            finally
            {
                // Cleanup both service instances
                serviceA?.Dispose();
                serviceB?.Dispose();
            }
        }

    } // End of TorrentServiceTests class
} // End of namespace TorrentWrapper.Tests
