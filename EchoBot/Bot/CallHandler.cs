using EchoBot.Constants;
using EchoBot.Util;
using EchoBot.WebRTC;
using Microsoft.Graph.Communications.Calls;
using Microsoft.Graph.Communications.Calls.Media;
using Microsoft.Graph.Communications.Common.Telemetry;
using Microsoft.Graph.Communications.Resources;
using Microsoft.Graph.Models;
using System.Timers;

namespace EchoBot.Bot
{
    /// <summary>
    /// Call Handler Logic.
    /// </summary>
    public class CallHandler : HeartbeatHandler
    {
        /// <summary>
        /// Gets the call.
        /// </summary>
        /// <value>The call.</value>
        public ICall Call { get; }

        private readonly string _threadId;

        /// <summary>
        /// Gets the bot media stream.
        /// </summary>
        /// <value>The bot media stream.</value>
        public BotMediaStream BotMediaStream { get; private set; }

        private readonly CacheHelper _cacheHelper;

        private RtcSessionManager _rtcSessionManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="CallHandler" /> class.
        /// </summary>
        /// <param name="statefulCall">The stateful call.</param>
        /// <param name="settings">The settings.</param>
        public CallHandler(ICall statefulCall, AppSettings settings)
            : base(TimeSpan.FromMinutes(10), statefulCall?.GraphLogger!)
        {
            this.Call = statefulCall;
            this.Call.OnUpdated += this.CallOnUpdated;
            this.Call.Participants.OnUpdated += async (s, e) => await this.ParticipantsOnUpdated(s, e);

            this._cacheHelper = ServiceLocator.GetRequiredService<CacheHelper>();
            this._threadId = statefulCall.Resource.ChatInfo.ThreadId!;
            this._rtcSessionManager = ServiceLocator.GetRequiredService<RtcSessionManager>();
            this.BotMediaStream = new BotMediaStream(this.Call.GetLocalMediaSession(), this.Call.Id, _threadId, this.GraphLogger, settings);
        }

        /// <inheritdoc/>
        protected override Task HeartbeatAsync(ElapsedEventArgs args)
        {
            return this.Call.KeepAliveAsync();
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            this.Call.OnUpdated -= this.CallOnUpdated;
            this.Call.Participants.OnUpdated -= async (s, e) => await this.ParticipantsOnUpdated(s, e);
            _rtcSessionManager.Dispose(_threadId).Wait();

            foreach (var participant in this.Call.Participants)
            {
                participant.OnUpdated -= async (s, e) => await this.OnParticipantUpdated(s, e);
            }

            this.BotMediaStream?.ShutdownAsync().ForgetAndLogExceptionAsync(this.GraphLogger);
        }

        /// <summary>
        /// Event fired when the call has been updated.
        /// </summary>
        /// <param name="sender">The call.</param>
        /// <param name="e">The event args containing call changes.</param>
        private async void CallOnUpdated(ICall sender, ResourceEventArgs<Call> e)
        {
            GraphLogger.Info($"Call status updated to {e.NewResource.State} - {e.NewResource.ResultInfo?.Message}");

            if (e.OldResource.State != e.NewResource.State && e.NewResource.State == CallState.Established)
            {
                // Call is established...
            }

            if ((e.OldResource.State == CallState.Established) && (e.NewResource.State == CallState.Terminated))
            {
                if (BotMediaStream != null)
                {
                    await BotMediaStream.ShutdownAsync().ForgetAndLogExceptionAsync(GraphLogger);
                }
            }
        }

        /// <summary>
        /// Creates the participant update json.
        /// </summary>
        /// <param name="participantId">The participant identifier.</param>
        /// <param name="participantDisplayName">Display name of the participant.</param>
        /// <returns>System.String.</returns>
        private string CreateParticipantUpdateJson(string participantId, string participantDisplayName = "")
        {
            if (participantDisplayName.Length == 0)
                return "{" + String.Format($"\"Id\": \"{participantId}\"") + "}";
            else
                return "{" + String.Format($"\"Id\": \"{participantId}\", \"DisplayName\": \"{participantDisplayName}\"") + "}";
        }

        /// <summary>
        /// Updates the participant.
        /// </summary>
        /// <param name="participants">The participants.</param>
        /// <param name="participant">The participant.</param>
        /// <param name="added">if set to <c>true</c> [added].</param>
        /// <param name="participantDisplayName">Display name of the participant.</param>
        /// <returns>System.String.</returns>
        private async Task<string> UpdateParticipant(List<IParticipant> participants, IParticipant participant, bool added, string participantDisplayName = "")
        {
            if (added)
            {
                participants.Add(participant);
                participant.OnUpdated += async (s, e) => await this.OnParticipantUpdated(s, e);
                await SubscribeToParticipantAudio(participant, forceSubscribe: false);
            }
            else
            {
                participants.Remove(participant);
                participant.OnUpdated -= async (s, e) => await this.OnParticipantUpdated(s, e);
                await UnsubscribeFromParticipantAudio(participant);
            }
            return CreateParticipantUpdateJson(participant.Id, participantDisplayName);
        }

        /// <summary>
        /// Updates the participants.
        /// </summary>
        /// <param name="eventArgs">The event arguments.</param>
        /// <param name="added">if set to <c>true</c> [added].</param>
        private async Task UpdateParticipants(ICollection<IParticipant> eventArgs, bool added = true)
        {
            foreach (var participant in eventArgs)
            {
                var json = string.Empty;

                // todo remove the cast with the new graph implementation,
                // for now we want the bot to only subscribe to "real" participants
                var participantDetails = participant.Resource.Info.Identity.User;

                if (participantDetails != null)
                {
                    json = await UpdateParticipant(this.BotMediaStream.participants, participant, added, participantDetails.DisplayName);
                }
                else if (participant.Resource.Info.Identity.AdditionalData?.Count > 0)
                {
                    if (CheckParticipantIsUsable(participant))
                    {
                        json = await UpdateParticipant(this.BotMediaStream.participants, participant, added);
                    }
                }

                GraphLogger.Info($"Update participants: {json}");
            }
        }

        /// <summary>
        /// Event fired when the participants collection has been updated.
        /// </summary>
        /// <param name="sender">Participants collection.</param>
        /// <param name="args">Event args containing added and removed participants.</param>
        public async Task ParticipantsOnUpdated(IParticipantCollection sender, CollectionEventArgs<IParticipant> args)
        {
            await UpdateParticipants(args.AddedResources);
            await UpdateParticipants(args.RemovedResources, false);

            if (this.BotMediaStream.participants.Count == 0)
            {
                if (!AppConstants.BotMeetingsDictionary.TryRemove(_threadId, out _))
                    return;
                await ServiceLocator.GetRequiredService<IBotService>().EndCallByThreadIdAsync(_threadId);
            }
        }

        private async Task OnParticipantUpdated(IParticipant sender, ResourceEventArgs<Participant> args)
        {
            var oldSourceId = args.OldResource.MediaStreams.FirstOrDefault(x => x.MediaType == Modality.Audio).SourceId;
            var newSourceId = args.NewResource.MediaStreams.FirstOrDefault(x => x.MediaType == Modality.Audio).SourceId;

            var newIdentityId = sender.Resource.Info.Identity.User.Id;
            var newIdentity = IdentityToParticipant(sender.Resource.Info.Identity.User, newSourceId);

            var oldIdentity = await _cacheHelper.GetAsync<Models.Participant>(ParticipantCacheKey(oldSourceId));
            if (oldIdentity != null && !newIdentityId.Equals(oldIdentity.Id))
            {
                await _cacheHelper.SetAsync(ParticipantCacheKey(newSourceId), TimeSpan.FromHours(2), newIdentity);
                return;
            }

            if (string.Equals(oldSourceId, newSourceId))
                return;

            await _cacheHelper.DeleteAsync(ParticipantCacheKey(oldSourceId));
            await _cacheHelper.SetAsync(ParticipantCacheKey(newSourceId), TimeSpan.FromHours(2), newIdentity);
        }

        /// <summary>
        /// Checks the participant is usable.
        /// </summary>
        /// <param name="p">The p.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise.</returns>
        private bool CheckParticipantIsUsable(IParticipant p)
        {
            foreach (var i in p.Resource.Info.Identity.AdditionalData)
                if (i.Key != "applicationInstance" && i.Value is Identity)
                    return true;

            return false;
        }

        private string ParticipantCacheKey(string sourceId)
        {
            return $"{_threadId}-{sourceId}";
        }

        private async Task SubscribeToParticipantAudio(IParticipant participant, bool forceSubscribe = true)
        {
            // filter the mediaStreams to see if the participant has a video send
            var audioStream = participant.Resource.MediaStreams!.FirstOrDefault(x => x.MediaType == Modality.Audio);
            if (audioStream != null)
            {
                await _cacheHelper.SetAsync(ParticipantCacheKey(audioStream.SourceId), TimeSpan.FromHours(2),
                    IdentityToParticipant(participant.Resource.Info.Identity.User, audioStream.SourceId)).ForgetAndLogExceptionAsync(GraphLogger);
            }
        }

        private async Task UnsubscribeFromParticipantAudio(IParticipant participant)
        {
            var audioStream = participant.Resource.MediaStreams!.FirstOrDefault(x => x.MediaType == Modality.Audio);
            if (audioStream != null)
            {
                await _cacheHelper.DeleteAsync(ParticipantCacheKey(audioStream.SourceId)).ForgetAndLogExceptionAsync(GraphLogger);
            }
        }

        private static Models.Participant IdentityToParticipant(Identity identity, string sourceId)
        {
            if (identity == null)
            {
                return new Models.Participant
                {
                    Id = sourceId,
                    DisplayName = string.Empty
                };
            }
            return new Models.Participant
            {
                Id = identity.Id!,
                DisplayName = identity?.DisplayName ?? string.Empty
            };
        }
    }
}

