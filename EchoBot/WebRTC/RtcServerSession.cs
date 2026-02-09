using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace EchoBot.WebRTC
{
    public class RtcServerSession : IDisposable
    {
        private readonly RTCPeerConnection _pc;

        private readonly AudioExtrasSource _audioSource;

        public RtcServerSession()
        {
            _pc = new RTCPeerConnection(null);

            _audioSource = new AudioExtrasSource(new AudioEncoder(false), new AudioSourceOptions
            {
                AudioSource = AudioSourcesEnum.None,
                MusicInputSamplingRate = AudioSamplingRatesEnum.Rate16KHz
            });

            _pc.OnAudioFormatsNegotiated += (fmts) => _audioSource.SetAudioSourceFormat(fmts.First());
            _audioSource.OnAudioSourceEncodedSample += _pc.SendAudio;

            // 把音源挂成发送轨（SendOnly）
            var audioTrack = new MediaStreamTrack(_audioSource.GetAudioSourceFormats(), MediaStreamStatusEnum.SendOnly);
            _pc.addTrack(audioTrack);

            _pc.onconnectionstatechange += async (state) =>
            {
                if (state == RTCPeerConnectionState.failed)
                    _pc.Close("ice disconnection");
                else if (state == RTCPeerConnectionState.closed)
                    await _audioSource.CloseAudio();
                else if (state == RTCPeerConnectionState.connected)
                    await _audioSource.StartAudio();
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

        public async Task SendAudio(MemoryStream stream)
        {
            await _audioSource.SendAudioFromStream(stream, AudioSamplingRatesEnum.Rate16KHz);
        }

        public void Dispose()
        {
            try { _pc.Close("normal"); } catch { }
        }
    }
}
