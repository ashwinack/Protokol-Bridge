using System;

namespace ProtokolBridge
{
    public enum TransportKind
    {
        Serial,
        Udp,
        Osc,
        TcpClient,
        TcpServer
    }

    public enum TransportState
    {
        Stopped,
        Connecting,
        Running,
        Faulted
    }

    /// <summary>
    /// A live connection. Implementations own their own background threads and must
    /// never touch the Unity API; everything surfaces through the queues that
    /// <see cref="BridgeManager"/> drains on the main thread.
    /// </summary>
    public interface ITransport : IDisposable
    {
        string Id { get; }
        string DisplayName { get; }
        TransportKind Kind { get; }
        TransportState State { get; }

        /// <summary>Last error, or empty when healthy. Shown in the UI next to the endpoint.</summary>
        string LastError { get; }

        /// <summary>Raised on a background thread. Handlers must be thread-safe.</summary>
        event Action<BridgeMessage> MessageReceived;

        void Start();
        void Stop();

        /// <summary>Queues a message for transmission. Returns false when the endpoint is not running.</summary>
        bool Send(BridgeMessage message);
    }
}
