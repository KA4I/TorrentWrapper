using System;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent.Connections.Peer;
using NATS.Client.Core;
using System.Net;
using ReusableTasks;

namespace TorrentWrapper
{
    /// <summary>
    /// A TURN-style peer connection over NATS subjects as a fallback path.
    /// </summary>
    public class NatsTurnConnection : IPeerConnection
    {
        private readonly Uri _uri;
        private readonly NatsOpts _natsOptions;
        private NatsConnection? _nats;
        private Task? _readLoopTask;
        private readonly string _requestId;
        private bool _disposed;

        // IPeerConnection implementation
        public ReadOnlyMemory<byte> AddressBytes => Array.Empty<byte>();
        public bool CanReconnect => true;
        public bool Disposed => _disposed;
        public IPEndPoint? EndPoint => null;
        public bool IsIncoming => false;

        // Explicit interface implementations
        async ReusableTask MonoTorrent.Connections.Peer.IPeerConnection.ConnectAsync()
        {
            // Use public Task-based ConnectAsync
            await ConnectAsync();
        }

        ReusableTask<int> MonoTorrent.Connections.Peer.IPeerConnection.ReceiveAsync(Memory<byte> buffer)
            => throw new NotSupportedException("ReceiveAsync not supported for NATS TURN.");

        async ReusableTask<int> MonoTorrent.Connections.Peer.IPeerConnection.SendAsync(Memory<byte> buffer)
        {
            // Call public SendAsync and return count
            int sent = await SendAsync(new ArraySegment<byte>(buffer.ToArray()));
            return sent;
        }

        /// <summary>
        /// Constructs a new TURN-style connection for the given requestId subject.
        /// URI format: natsturn://{requestId}, using provided NATS options.
        /// </summary>
        public NatsTurnConnection(Uri uri, NatsOpts natsOptions)
        {
            _uri = uri ?? throw new ArgumentNullException(nameof(uri));
            _natsOptions = natsOptions;
            _requestId = uri.Host; // host portion holds the requestId
        }

        public Uri Uri => _uri;

        public event EventHandler<ArraySegment<byte>>? OnDataReceived;
        public event EventHandler<bool>? OnConnectionClosed;
        public event EventHandler<Exception>? OnConnectionError;

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            // Connect to NATS server (no cancellation)
            _nats = new NatsConnection(_natsOptions);
            await _nats.ConnectAsync();
            // Start background loop to pump incoming messages
            _readLoopTask = Task.Run(() => ReadLoopAsync(cancellationToken), cancellationToken);
        }

        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            if (_nats == null)
                return;
            await foreach (var msg in _nats.SubscribeAsync<byte[]>($"p2p.relay.{_requestId}", cancellationToken: cancellationToken))
            {
                OnDataReceived?.Invoke(this, new ArraySegment<byte>(msg.Data));
            }
        }

        public async Task<int> SendAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_nats == null)
                throw new InvalidOperationException("NATS connection not initialized. Call ConnectAsync first.");
            try
            {
                // Publish our bytes to the relay subject
                await _nats.PublishAsync($"p2p.relay.{_requestId}", buffer.ToArray());
                return buffer.Count;
            }
            catch (Exception ex)
            {
                OnConnectionError?.Invoke(this, ex);
                return 0;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                // Cancel read loop if running
                // Note: no linked cancellation here for simplicity
                _nats?.DisposeAsync().AsTask().Wait();
                OnConnectionClosed?.Invoke(this, true);
            }
        }
    }
}
