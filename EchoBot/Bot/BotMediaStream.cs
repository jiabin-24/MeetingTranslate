using EchoBot.Constants;
using EchoBot.Media;
using EchoBot.Util;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Skype.Bots.Media;
using Microsoft.Skype.Internal.Media.Services.Common;
using System.Runtime.InteropServices;

namespace EchoBot.Bot
{
    /// <summary>
    /// Class responsible for streaming audio and video.
    /// </summary>
    public class BotMediaStream : ObjectRootDisposable
    {
        /// <summary>
        /// The participants
        /// </summary>
        internal List<IParticipant> Participants = [];

        /// <summary>
        /// The audio socket
        /// </summary>
        private readonly IAudioSocket _audioSocket;

        private readonly CacheHelper _cacheHelper;

        /// <summary>
        /// The media stream
        /// </summary>
        private readonly ILogger _logger;
        private readonly string _threadId;
        private AudioVideoFramePlayer audioVideoFramePlayer;
        private readonly TaskCompletionSource<bool> audioSendStatusActive;
        private readonly TaskCompletionSource<bool> startVideoPlayerCompleted;
        private AudioVideoFramePlayerSettings audioVideoFramePlayerSettings;
        private List<AudioMediaBuffer> audioMediaBuffers = [];
        private readonly CallHandler _callHandler;
        private int shutdown;

        public BaseSpeechService LanguageService { get; private set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="BotMediaStream" /> class.
        /// </summary>
        /// <param name="callHandler">The call handler.</param>
        /// <param name="threadId">The thread identity</param>
        /// <param name="graphLogger">The Graph logger.</param>
        /// <exception cref="InvalidOperationException">A mediaSession needs to have at least an audioSocket</exception>
        public BotMediaStream(
            CallHandler callHandler,
            string threadId,
            IGraphLogger graphLogger
        ) : base(graphLogger)
        {
            ArgumentVerifier.ThrowOnNullArgument(callHandler, nameof(callHandler));

            _logger = ServiceLocator.GetRequiredService<ILogger<BotMediaStream>>();
            _cacheHelper = ServiceLocator.GetRequiredService<CacheHelper>();
            _callHandler = callHandler;
            _threadId = threadId;

            this.audioSendStatusActive = new TaskCompletionSource<bool>();
            this.startVideoPlayerCompleted = new TaskCompletionSource<bool>();

            // Subscribe to the audio media.
            this._audioSocket = callHandler.Call.GetLocalMediaSession().AudioSocket ?? throw new InvalidOperationException("A mediaSession needs to have at least an audioSocket");
            var ignoreTask = this.StartAudioVideoFramePlayerAsync().ForgetAndLogExceptionAsync(this.GraphLogger, "Failed to start the player");

            this._audioSocket.AudioSendStatusChanged += OnAudioSendStatusChanged;
            this._audioSocket.AudioMediaReceived += this.OnAudioMediaReceived;

            var config = ServiceLocator.GetRequiredService<IConfiguration>();
            LanguageService = "ByteDance".Equals(config["SpeechServiceModel"]) ? new ByteDanceSpeechService(threadId, callHandler) : new AzureSpeechService(threadId, callHandler);
            LanguageService.SendMediaBuffer += this.OnSendMediaBuffer;
        }

        /// <summary>
        /// Gets the participants.
        /// </summary>
        /// <returns>List&lt;IParticipant&gt;.</returns>
        public List<IParticipant> GetParticipants()
        {
            return Participants;
        }

        /// <summary>
        /// Shut down.
        /// </summary>
        /// <returns><see cref="Task" />.</returns>
        public async Task ShutdownAsync()
        {
            if (Interlocked.CompareExchange(ref this.shutdown, 1, 1) == 1)
            {
                return;
            }

            await this.startVideoPlayerCompleted.Task.ConfigureAwait(false);

            // unsubscribe
            if (this._audioSocket != null)
            {
                this._audioSocket.AudioSendStatusChanged -= this.OnAudioSendStatusChanged;
            }

            // shutting down the players
            if (this.audioVideoFramePlayer != null)
            {
                await this.audioVideoFramePlayer.ShutdownAsync().ConfigureAwait(false);
            }

            // make sure all the audio and video buffers are disposed, it can happen that,
            // the buffers were not enqueued but the call was disposed if the caller hangs up quickly
            foreach (var audioMediaBuffer in this.audioMediaBuffers)
            {
                audioMediaBuffer.Dispose();
            }

            _logger.LogInformation("disposed {Count} audioMediaBUffers.", this.audioMediaBuffers.Count);

            this.audioMediaBuffers.Clear();

            await LanguageService.ShutDownAsync().ConfigureAwait(false);
            await _cacheHelper.Mux.GetDatabase().KeyDeleteAsync(CacheConstants.MeetingCaptionKey(_threadId));
            await _cacheHelper.DeleteChildrenAsync(CacheConstants.MsAudioParticipantsKey(_threadId, null));
        }

        /// <summary>
        /// Initialize AV frame player.
        /// </summary>
        /// <returns>Task denoting creation of the player with initial frames enqueued.</returns>
        private async Task StartAudioVideoFramePlayerAsync()
        {
            try
            {
                _logger.LogInformation("Send status active for audio and video Creating the audio video player");
                this.audioVideoFramePlayerSettings =
                    new AudioVideoFramePlayerSettings(new AudioSettings(20), new VideoSettings(), 1000);
                this.audioVideoFramePlayer = new AudioVideoFramePlayer(
                    (AudioSocket)_audioSocket,
                    null,
                    this.audioVideoFramePlayerSettings);

                _logger.LogInformation("created the audio video player");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create the audioVideoFramePlayer with exception");
            }
            finally
            {
                this.startVideoPlayerCompleted.TrySetResult(true);
            }
        }

        /// <summary>
        /// Callback for informational updates from the media plaform about audio status changes.
        /// Once the status becomes active, audio can be loopbacked.
        /// </summary>
        /// <param name="sender">The audio socket.</param>
        /// <param name="e">Event arguments.</param>
        private void OnAudioSendStatusChanged(object? sender, AudioSendStatusChangedEventArgs e)
        {
            _logger.LogTrace($"[AudioSendStatusChangedEventArgs(MediaSendStatus={e.MediaSendStatus})]");

            if (e.MediaSendStatus == MediaSendStatus.Active)
            {
                this.audioSendStatusActive.TrySetResult(true);
            }
        }

        /// <summary>
        /// Receive audio from subscribed participant.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="e">The audio media received arguments.</param>
        private async void OnAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
        {
            try
            {
                //await SingleAudioModeAudioMediaReceived(sender, e);
                await MulAudioModeAudioMediaReceived(sender, e);
            }
            catch (Exception ex)
            {
                this.GraphLogger.Error(ex);
                _logger.LogError(ex, "OnAudioMediaReceived error");
            }
            finally
            {
                e.Buffer.Dispose();
            }
        }

        private async Task SingleAudioModeAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
        {
            _logger.LogTrace($"Received Audio: [AudioMediaReceivedEventArgs(Data=<{e.Buffer.Data.ToString()}>, Length={e.Buffer.Length}, Timestamp={e.Buffer.Timestamp})]");

            if (!startVideoPlayerCompleted.Task.IsCompleted) { return; }

            if (e.Buffer == null)
                return;

            var speakerAudioId = e.Buffer.ActiveSpeakers.Length != 0 ? e.Buffer.ActiveSpeakers[0].ToString() : null;
            await LanguageService.AppendAudioBuffer(e.Buffer, ResolveSpeakerId(speakerAudioId));
        }

        private async Task MulAudioModeAudioMediaReceived(object? sender, AudioMediaReceivedEventArgs e)
        {
            if (e.Buffer.UnmixedAudioBuffers == null)
                return;

            foreach (var buffer in e.Buffer.UnmixedAudioBuffers)
            {
                var length = buffer.Length;
                var data = new byte[length];
                Marshal.Copy(buffer.Data, data, 0, (int)length);

                await LanguageService.AppendAudioBuffer(Util.Utilities.CreateAudioMediaBuffer(data, DateTime.Now.Ticks, _logger), ResolveSpeakerId(buffer.ActiveSpeakerId.ToString()));
            }
        }

        private string ResolveSpeakerId(string? audioSourceId)
        {
            if (string.IsNullOrWhiteSpace(audioSourceId))
                return "unknown";

            var participant = _callHandler.Call.Participants
                .SingleOrDefault(x => x.Resource.IsInLobby == false && x.Resource.MediaStreams.Any(y => y.SourceId == audioSourceId));

            return string.IsNullOrWhiteSpace(participant?.Id) ? audioSourceId : participant.Id;
        }

        private void OnSendMediaBuffer(object? sender, Media.MediaStreamEventArgs e)
        {
            this.audioMediaBuffers = e.AudioMediaBuffers;
            var result = Task.Run(async () => await this.audioVideoFramePlayer.EnqueueBuffersAsync(this.audioMediaBuffers, new List<VideoMediaBuffer>())).GetAwaiter();
        }
    }
}

