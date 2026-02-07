using SIPSorcery.Net;

namespace EchoBot.WebRTC
{
    public class RtcServerSession : IDisposable
    {
        private RTCPeerConnection _pc;
        private uint _rtpTimestamp;

        public RtcServerSession()
        {
            _pc = new RTCPeerConnection(null);

            _pc.onconnectionstatechange += (state) =>
            {
                if (state is RTCPeerConnectionState.disconnected or RTCPeerConnectionState.failed or RTCPeerConnectionState.closed)
                {
                    try { _pc.Close("normal"); } catch { }
                }
            };
        }

        public Task<string> AcceptOfferAndGetAnswerAsync(string sdpOffer)
        {
            _pc.setRemoteDescription(new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = sdpOffer });
            var answer = _pc.createAnswer(null);
            _pc.setLocalDescription(answer);
            var local = _pc.localDescription;
            if (local == null) return Task.FromResult(string.Empty);
            try
            {
                return Task.FromResult(local.sdp.ToString() ?? local.ToString() ?? string.Empty);
            }
            catch
            {
                return Task.FromResult(string.Empty);
            }
        }

        public Task AddIceCandidateAsync(string cand)
        {
            _pc.addIceCandidate(new RTCIceCandidateInit { candidate = cand });
            return Task.CompletedTask;
        }

        public void SendOpus(ReadOnlySpan<byte> opusFrame)
        {
            // 48k mono @20ms -> 960 samples per packet
            _rtpTimestamp += 960;

            // SIPSorcery SendAudio typically takes (uint rtpTimestamp, byte[] payload)
            _pc.SendAudio(_rtpTimestamp, opusFrame.ToArray());
        }

        public void Dispose()
        {
            try { _pc.Close("normal"); } catch { }
        }
    }
}
