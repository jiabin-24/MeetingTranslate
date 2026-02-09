using System.Collections.Concurrent;

namespace EchoBot.WebRTC
{
    public class RtcSessionManager
    {
        private readonly Dictionary<string, RtcServerSession> _sessions = [];
        private readonly object _lock = new();
        private readonly MeetingBroadcaster _broadcaster;

        private readonly ConcurrentDictionary<string, List<MeetingBroadcaster>> _meetingBroadcaster;


        public RtcSessionManager(MeetingBroadcaster broadcaster)
        {
            _broadcaster = broadcaster;
            _broadcaster.AttachSessionSource(() => _sessions.Values);
        }

        public Task<string> CreateOrUpdateAsync(string id, string sdpOffer, string meetingId, string targetLang)
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

        public async Task PushPcmToAll(MemoryStream stream)
        {
            // Broadcast a 20ms 48kHz/16bit/Mono PCM frame to all peers
            await _broadcaster.PushAudio(stream);
        }
    }
}
