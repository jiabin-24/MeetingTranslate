using Concentus;
using Concentus.Enums;
using System.Runtime.InteropServices;

namespace EchoBot.WebRTC
{
    public class OpusBroadcaster
    {
        private readonly List<RtcServerSession> _peers = new List<RtcServerSession>();
        private readonly object _lock = new();
        private readonly IOpusEncoder _encoder;

        public OpusBroadcaster()
        {
            _encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            _encoder.Bitrate = 24000; // bits per second target
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

        public void PushPcm20Ms(ReadOnlySpan<byte> pcm20ms48k16le)
        {
            // Input is 16-bit little-endian PCM. Convert to short span and call the Concentus Encode overload which accepts ReadOnlySpan<short>.
            // Ensure we have an even number of bytes for 16-bit PCM
            if ((pcm20ms48k16le.Length & 1) != 0) return;
            ReadOnlySpan<short> pcmShorts = MemoryMarshal.Cast<byte, short>(pcm20ms48k16le);
            const int frameSize = 960; // 20ms @ 48kHz mono

            Span<byte> opusBuf = stackalloc byte[4000];
            int encoded = _encoder.Encode(pcmShorts, frameSize, opusBuf, opusBuf.Length);
            if (encoded <= 0) return;
            ReadOnlySpan<byte> frame = opusBuf[..encoded];

            RtcServerSession[] peers;
            lock (_lock)
            {
                peers = _peers.ToArray();
            }

            foreach (var p in peers)
            {
                try { p.SendOpus(frame); } catch { }
            }
        }
    }
}
