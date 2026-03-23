using EchoBot.Constants;
using EchoBot.Util;
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
        private readonly CollectionEventHandler<IParticipantCollection, IParticipant> _participantsUpdatedHandler;
        private readonly ResourceEventHandler<IParticipant, Participant> _participantUpdatedHandler;

        /// <summary>
        /// Initializes a new instance of the <see cref="CallHandler" /> class.
        /// </summary>
        /// <param name="statefulCall">The stateful call.</param>
        /// <param name="settings">The settings.</param>
        public CallHandler(ICall statefulCall)
            : base(TimeSpan.FromMinutes(10), statefulCall?.GraphLogger!)
        {
            this._participantsUpdatedHandler = async (s, e) => await this.ParticipantsOnUpdated(s, e);
            this._participantUpdatedHandler = async (s, e) => await this.OnParticipantUpdated(s, e);

            this.Call = statefulCall;
            this.Call.OnUpdated += this.CallOnUpdated;
            this.Call.Participants.OnUpdated += this._participantsUpdatedHandler;

            this._cacheHelper = ServiceLocator.GetRequiredService<CacheHelper>();
            this._threadId = statefulCall.Resource.ChatInfo.ThreadId!;
            this.BotMediaStream = new BotMediaStream(this.Call.GetLocalMediaSession(), this.Call.Id, _threadId, this.GraphLogger);
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
            this.Call.Participants.OnUpdated -= this._participantsUpdatedHandler;

            foreach (var participant in this.Call.Participants)
            {
                participant.OnUpdated -= this._participantUpdatedHandler;
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
        private static string CreateParticipantUpdateJson(string participantId, string participantDisplayName = "")
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
                if (!participants.Any(p => p.Id == participant.Id))
                {
                    participants.Add(participant);
                    participant.OnUpdated += this._participantUpdatedHandler;
                }
                else
                {
                    var index = participants.FindIndex(p => p.Id == participant.Id);
                    if (index >= 0)
                    {
                        participants[index] = participant;
                    }
                }

                if (!string.IsNullOrEmpty(participantDisplayName))
                    this.BotMediaStream.LanguageService.AddPhrases([participantDisplayName]);
                await SubscribeToParticipantAudio(participant, forceSubscribe: false);
            }
            else
            {
                var existingParticipant = participants.FirstOrDefault(p => p.Id == participant.Id);
                if (existingParticipant != null)
                {
                    existingParticipant.OnUpdated -= this._participantUpdatedHandler;
                    participants.Remove(existingParticipant);
                }

                participant.OnUpdated -= this._participantUpdatedHandler;
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
                    json = await UpdateParticipant(this.BotMediaStream.Participants, participant, added, participantDetails.DisplayName);
                }
                else if (participant.Resource.Info.Identity.AdditionalData?.Count > 0)
                {
                    if (CheckParticipantIsUsable(participant))
                    {
                        json = await UpdateParticipant(this.BotMediaStream.Participants, participant, added);
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

            if (this.BotMediaStream.Participants.Count == 0)
            {
                await ServiceLocator.GetRequiredService<IBotService>().EndCallByThreadIdAsync(_threadId);
                await _cacheHelper.DeleteChildrenAsync(CacheConstants.MsAudioParticipantsKey(_threadId, null));
            }
        }

        private async Task OnParticipantUpdated(IParticipant sender, ResourceEventArgs<Participant> args)
        {
            var index = this.BotMediaStream.Participants.FindIndex(p => p.Id == sender.Id);
            if (index >= 0)
            {
                this.BotMediaStream.Participants[index] = sender;
            }

            await SubscribeToParticipantAudio(sender, forceSubscribe: false);
        }

        private async Task SubscribeToParticipantAudio(IParticipant participant, bool forceSubscribe = true)
        {
            var identity = TryGetParticipantIdentity(participant);
            var audioSourceId = participant.Resource.MediaStreams?.FirstOrDefault(m => m.MediaType == Modality.Audio)?.SourceId;
            var displayName = identity?.DisplayName;

            if (!string.IsNullOrEmpty(audioSourceId))
            {
                GraphLogger.Info($"Participant {participant.Id} audio source ready: {audioSourceId}");

                await _cacheHelper.SetAsync(CacheConstants.MsAudioParticipantsKey(_threadId, audioSourceId), TimeSpan.FromMinutes(30), displayName);
            }
            else if (forceSubscribe)
            {
                GraphLogger.Warn($"Participant {participant.Id} has no audio source yet.");
            }

            await Task.CompletedTask;
        }

        public static Identity TryGetParticipantIdentity(IParticipant participant)
        {
            var identitySet = participant?.Resource?.Info?.Identity;
            var identity = identitySet?.User;

            if (identity == null &&
                identitySet != null &&
                identitySet.AdditionalData.Any(kvp => kvp.Value is Identity))
            {
                identity = identitySet.AdditionalData.Values.First(v => v is Identity) as Identity;
            }

            return identity;
        }

        private async Task UnsubscribeFromParticipantAudio(IParticipant participant)
        {
            GraphLogger.Info($"Participant {participant.Id} audio unsubscribed.");
            await Task.CompletedTask;
        }

        /// <summary>
        /// Checks the participant is usable.
        /// </summary>
        /// <param name="p">The p.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise.</returns>
        private static bool CheckParticipantIsUsable(IParticipant p)
        {
            foreach (var i in p.Resource.Info.Identity.AdditionalData)
                if (i.Key != "applicationInstance" && i.Value is Identity)
                    return true;

            return false;
        }
    }
}

