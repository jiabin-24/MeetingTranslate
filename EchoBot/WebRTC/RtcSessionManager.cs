namespace EchoBot.WebRTC
{
    public class RtcSessionManager
    {
        private readonly Dictionary<string, RtcServerSession> _sessions = new();
        private readonly object _lock = new();
        private readonly OpusBroadcaster _broadcaster;

        public RtcSessionManager(OpusBroadcaster broadcaster)
        {
            _broadcaster = broadcaster;
            _broadcaster.AttachSessionSource(() => _sessions.Values);
        }

        public Task<string> CreateOrUpdateAsync(string id, string sdpOffer)
        {
            lock (_lock)
            {
                if (!_sessions.TryGetValue(id, out var sess))
                {
                    sess = new RtcServerSession();
                    _sessions[id] = sess;
                }
                _broadcaster.AddPeer(sess);
                return sess.AcceptOfferAndGetAnswerAsync(sdpOffer);
            }
        }

        public Task AddIceCandidateAsync(string id, string candidate)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(id, out var sess)) return sess.AddIceCandidateAsync(candidate);
                return Task.CompletedTask;
            }
        }

        public Task CloseAsync(string id)
        {
            lock (_lock)
            {
                if (_sessions.Remove(id, out var sess))
                {
                    _broadcaster.RemovePeer(sess);
                    sess.Dispose();
                }
            }

            return Task.CompletedTask;
        }

        public void PushPcmToAll(ReadOnlySpan<byte> pcm20ms48k16le)
        {
            // Broadcast a 20ms 48kHz/16bit/Mono PCM frame to all peers
            _broadcaster.PushPcm20Ms(pcm20ms48k16le);
        }
    }
}
