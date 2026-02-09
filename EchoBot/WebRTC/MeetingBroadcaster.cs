using Concentus;
using Concentus.Enums;

namespace EchoBot.WebRTC
{
    public class MeetingBroadcaster
    {
        private readonly List<RtcServerSession> _peers = [];
        private readonly object _lock = new();

        public MeetingBroadcaster()
        {
            
        }

        public void AttachSessionSource(Func<IEnumerable<RtcServerSession>> source)
        {
            // Optional hook if you want to query sessions lazily; not used in this minimal sample
        }

        public void AddPeer(RtcServerSession s)
        {
            lock (_lock)
            {
                _peers.Add(s);
            }
        }

        public void RemovePeer(RtcServerSession s)
        {
            lock (_lock)
            {
                _peers.Remove(s);
            }
        }

        public async Task PushAudio(MemoryStream stream)
        {
            RtcServerSession[] peers;
            lock (_lock)
            {
                peers = [.. _peers];
            }

            foreach (var p in peers)
            {
                try { await p.SendAudio(stream); } catch { }
            }
        }
    }
}
