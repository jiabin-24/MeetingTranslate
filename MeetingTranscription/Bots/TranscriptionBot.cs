// <copyright file="TranscriptionBot.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

using EchoBot.Bot;
using EchoBot.Models;
using MeetingTranscription.Helpers;
using MeetingTranscription.Models.Configuration;
using MeetingTranscription.Services;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Teams;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MeetingTranscription.Bots
{
    public class TranscriptionBot : TeamsActivityHandler
    {
        /// <summary>
        /// Helper instance to make graph calls.
        /// </summary>
        private readonly GraphHelper graphHelper;

        /// <summary>
        /// Stores the Azure configuration values.
        /// </summary>
        private readonly IOptions<AzureSettings> azureSettings;

        /// <summary>
        /// Store details of meeting transcript.
        /// </summary>
        private readonly ConcurrentDictionary<string, string> transcriptsDictionary;

        /// <summary>
        /// Gets the thread-safe dictionary that stores meeting information for bots, keyed by bot identifier.
        /// </summary>
        /// <remarks>This dictionary allows concurrent access and updates from multiple threads. Each key
        /// represents a unique bot identifier, and the corresponding value contains meeting-related data for that
        /// bot.</remarks>
        private static ConcurrentDictionary<string, string> _botMeetingsDictionary { get; } = new ConcurrentDictionary<string, string>();

        /// <summary>
        /// Instance of card factory to create adaptive cards.
        /// </summary>
        private readonly ICardFactory cardFactory;

        /// <summary>
        /// The bot service
        /// </summary>
        private readonly IBotService _botService;

        /// <summary>
        /// Creates bot instance.
        /// </summary>
        /// <param name="azureSettings">Stores the Azure configuration values.</param>
        /// <param name="transcriptsDictionary">Store details of meeting transcript.</param>
        /// <param name="cardFactory">Instance of card factory to create adaptive cards.</param>
        /// <param name="botService">Join bot service</param>
        public TranscriptionBot(IOptions<AzureSettings> azureSettings, ConcurrentDictionary<string, string> transcriptsDictionary, ICardFactory cardFactory, IBotService botService)
        {
            this.transcriptsDictionary = transcriptsDictionary;
            this.azureSettings = azureSettings;
            graphHelper = new GraphHelper(azureSettings);
            this.cardFactory = cardFactory;
            this._botService = botService;
        }

        /// <summary>
        /// Activity handler for on message activity.
        /// </summary>
        /// <param name="turnContext">A strongly-typed context object for this turn.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        protected override async Task OnMessageActivityAsync(ITurnContext<IMessageActivity> turnContext, CancellationToken cancellationToken)
        {
            var replyText = $"Echo: {turnContext.Activity.Text}";
            await turnContext.SendActivityAsync(MessageFactory.Text(replyText, replyText), cancellationToken);
        }

        /// <summary>
        /// Handles the event triggered when a Microsoft Teams meeting starts, performing any necessary actions such as
        /// joining the call and invoking base event handling.
        /// </summary>
        /// <remarks>Override this method to customize bot behavior when a Teams meeting begins. This
        /// method is called automatically by the framework when a meeting start event is received.</remarks>
        /// <param name="meeting">The details of the Teams meeting that has started, including join URL and meeting title. Cannot be null.</param>
        /// <param name="turnContext">The context object for the current event turn, providing access to bot framework services and activity data.
        /// Cannot be null.</param>
        /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
        /// <returns>A task that represents the asynchronous operation.</returns>
        protected override async Task OnTeamsMeetingStartAsync(MeetingStartEventDetails meeting, ITurnContext<IEventActivity> turnContext, CancellationToken cancellationToken)
        {
            await base.OnTeamsMeetingStartAsync(meeting, turnContext, cancellationToken);
            try
            {
                // Join the meeting using the join URL from the meeting details
                var botMeeting = await _botService.JoinCallAsync(new JoinCallBody
                {
                    JoinUrl = meeting.JoinUrl.ToString()
                });

                _botMeetingsDictionary.TryAdd(meeting.Id, botMeeting!.Resource!.ChatInfo!.ThreadId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OnTeamsMeetingStartAsync: {ex.Message}");
            }
        }

        /// <summary>
        /// Activity handler for meeting end event.
        /// </summary>
        /// <param name="meeting">The details of the meeting.</param>
        /// <param name="turnContext">A strongly-typed context object for this turn.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A task that represents the work queued to execute.</returns>
        protected override async Task OnTeamsMeetingEndAsync(MeetingEndEventDetails meeting, ITurnContext<IEventActivity> turnContext, CancellationToken cancellationToken)
        {
            try
            {
                var meetingInfo = await TeamsInfo.GetMeetingInfoAsync(turnContext);
                Console.WriteLine($"Meeting Ended: {meetingInfo.Details.MsGraphResourceId}");

                // End the bot's call in the meeting
                if (_botMeetingsDictionary.TryRemove(meeting.Id, out var threadId))
                {
                    await _botService.EndCallByThreadIdAsync(threadId);
                }

                // NEW: Get meeting organizer information when meeting ends
                var organizerId = await graphHelper.GetMeetingOrganizerFromTeamsContextAsync(turnContext);
                if (!string.IsNullOrEmpty(organizerId))
                {
                    Console.WriteLine($"Meeting organizer identified: {organizerId}");
                }

                // NEW: Use Teams context to find organizer and get transcripts
                var result = await graphHelper.GetMeetingTranscriptionsAsync(meetingInfo.Details.MsGraphResourceId, organizerId);

                if (!string.IsNullOrEmpty(result))
                {
                    transcriptsDictionary.AddOrUpdate(meetingInfo.Details.MsGraphResourceId, result, (key, newValue) => result);

                    var attachment = this.cardFactory.CreateAdaptiveCardAttachement(new { MeetingId = meetingInfo.Details.MsGraphResourceId });
                    await turnContext.SendActivityAsync(MessageFactory.Attachment(attachment), cancellationToken);

                    Console.WriteLine($"Successfully retrieved and cached meeting transcript for {meetingInfo.Details.MsGraphResourceId}");
                }
                else
                {
                    var attachment = this.cardFactory.CreateNotFoundCardAttachement();
                    await turnContext.SendActivityAsync(MessageFactory.Attachment(attachment), cancellationToken);

                    Console.WriteLine($"No transcript found for meeting {meetingInfo.Details.MsGraphResourceId}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OnTeamsMeetingEndAsync: {ex.Message}");

                // Send error card to user
                var errorAttachment = this.cardFactory.CreateNotFoundCardAttachement();
                await turnContext.SendActivityAsync(MessageFactory.Attachment(errorAttachment), cancellationToken);
            }
        }

        /// <summary>
        /// Activity handler for Task module fetch event.
        /// </summary>
        /// <param name="turnContext">A strongly-typed context object for this turn.</param>
        /// <param name="taskModuleRequest">The task module invoke request value payload.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A Task Module Response for the request.</returns>
        protected override async Task<TaskModuleResponse> OnTeamsTaskModuleFetchAsync(ITurnContext<IInvokeActivity> turnContext, TaskModuleRequest taskModuleRequest, CancellationToken cancellationToken)
        {
            try
            {
                var meetingId = JObject.FromObject(taskModuleRequest.Data)["meetingId"];

                return new TaskModuleResponse
                {
                    Task = new TaskModuleContinueResponse
                    {
                        Type = "continue",
                        Value = new TaskModuleTaskInfo()
                        {
                            Url = $"{this.azureSettings.Value.AppBaseUrl}/home?meetingId={meetingId}",
                            Height = 600,
                            Width = 600,
                            Title = "Meeting Transcript",
                        },
                    }
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in OnTeamsTaskModuleFetchAsync: {ex.Message}");

                return new TaskModuleResponse
                {
                    Task = new TaskModuleContinueResponse
                    {
                        Type = "continue",
                        Value = new TaskModuleTaskInfo()
                        {
                            Url = this.azureSettings.Value.AppBaseUrl + "/home",
                            Height = 350,
                            Width = 350,
                            Title = "Meeting Transcript",
                        },
                    }
                };
            }
        }
    }
}